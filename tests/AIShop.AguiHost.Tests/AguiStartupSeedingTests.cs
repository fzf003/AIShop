using AIShop.AguiHost;
using AIShop.Infrastructure.Data;
using AIShop.Service.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本测试集合会建多个临时 SQLite 文件库 + 加载 bge 模型（首个 EmbeddingGenerator 一次性、进程内共享会话），
/// 置于 DisableParallelization 串行集合，避免并行宿主竞争与 SQLite 文件写入冲突（learnings 先例同 ProgramSeedingTests）。
/// </summary>
[CollectionDefinition(nameof(AguiStartupSeedingTests), DisableParallelization = true)]
public sealed class AguiStartupSeedingTestsCollection;

/// <summary>
/// T3 启动引导测试：AddAguiBaseServices(独立 db/rag 连接串) + InitializeAsync
/// → MigrateAsync 建 schema（勿 EnsureCreated）+ 幂等播种 18 商品 & marla/steve/fzf003 + RAG 预热写独立向量库。
/// 对应 spec「启动走 MigrateAsync 并幂等播种（禁用 EnsureCreated）」「复用 EF 底座连独立 SQLite 业务库」
/// 「AguiHost 语义检索使用独立向量库（EnsureIndexedAsync 预热）」。
/// </summary>
[Collection(nameof(AguiStartupSeedingTests))]
public sealed class AguiStartupSeedingTests : IDisposable
{
    private readonly List<string> _createdDbPaths = [];

    public void Dispose()
    {
        // 释放 SQLite 连接池对文件的句柄后，删除本测试创建的临时 db 文件（锁未完全释放时忽略，交由系统清理）
        SqliteConnection.ClearAllPools();
        foreach (var path in _createdDbPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 文件仍被其他进程占用时忽略，交由系统清理
            }
        }
    }

    private string NewDbPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_t3_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    private static DbContextOptions<AppDbContext> OptionsFor(string dbPath) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;

    /// <summary>把底座 DI（独立业务库 + 独立向量库连接串）装入 ServiceCollection 并构建 ServiceProvider。
    /// 仅 AddAguiBaseServices（不启 WAF/不挂 AddAGUIServer），直接驱动 internal 装配与启动引导。</summary>
    private static ServiceProvider BuildProvider(string dbConnection, string ragConnection)
    {
        var services = new ServiceCollection();
        services.AddAguiBaseServices(new ConfigurationBuilder().Build(), dbConnection, ragConnection);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Initialize_OnFreshDatabase_Seeds18ProductsAnd3Users_AndCreatesIndependentRagDb()
    {
        var dbPath = NewDbPath("ef");
        var ragPath = NewDbPath("rag");
        using (var sp = BuildProvider($"Data Source={dbPath}", $"Data Source={ragPath}"))
        {
            // 启动引导：MigrateAsync 建 schema → 幂等播种 → RAG 预热（内部 try/catch，失败仅 Warning 不抛）
            await AguiServiceCollectionExtensions.InitializeAsync(sp);

            // EF 独立业务库：18 商品 + 3 测试用户均落库（MigrateAsync 建 schema，不是 EnsureCreated）
            using var ctx = new AppDbContext(OptionsFor(dbPath));
            Assert.Equal(18, await ctx.Products.CountAsync());
            Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "marla"));
            Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "steve"));
            Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "fzf003"));
        }

        // RAG 预热 EnsureIndexedAsync 写独立向量库：若 ragConnection 被忽略回退默认 aishop.rag.db，tempRag 不会生成
        Assert.True(File.Exists(ragPath), "AddAguiBaseServices 应把 ragConnection 传入 AddRagService，预热后独立向量库文件生成");
    }

    [Fact]
    public async Task Initialize_TwiceOnSameDatabase_IsIdempotent_NoDuplicateRows()
    {
        var dbPath = NewDbPath("ef");
        var ragPath = NewDbPath("rag");
        var efConnection = $"Data Source={dbPath}";
        var ragConnection = $"Data Source={ragPath}";

        // 第一次启动（全新库）
        using (var sp1 = BuildProvider(efConnection, ragConnection))
        {
            await AguiServiceCollectionExtensions.InitializeAsync(sp1);
        }
        SqliteConnection.ClearAllPools();

        // 第二次启动（既有库，模拟重复启动/多实例）：MigrateAsync 幂等跳过、AnyAsync 判空不重复播种
        using (var sp2 = BuildProvider(efConnection, ragConnection))
        {
            await AguiServiceCollectionExtensions.InitializeAsync(sp2);
        }
        SqliteConnection.ClearAllPools();

        using var ctx = new AppDbContext(OptionsFor(dbPath));
        Assert.Equal(18, await ctx.Products.CountAsync());
        Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "marla"));
        Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "steve"));
        Assert.Equal(1, await ctx.Users.CountAsync(u => u.Username == "fzf003"));
    }

    [Fact]
    public async Task CartToolProvider_SearchProduct_HitsWarmedIndependentRagIndex()
    {
        var dbPath = NewDbPath("ef");
        var ragPath = NewDbPath("rag");
        using var sp = BuildProvider($"Data Source={dbPath}", $"Data Source={ragPath}");

        // 先播种 + 预热索引（tempRag 建好向量索引）
        await AguiServiceCollectionExtensions.InitializeAsync(sp);

        // CartToolProvider 注入的 IProductSemanticSearch（非 null）指向独立 rag 库：
        // 语义命中「专业跑鞋」→ 走格式化命中分支「找到 N 个商品」，而非 semanticSearch=null 的「未找到包含」兜底文案
        var cartTools = sp.GetRequiredService<CartToolProvider>();
        var result = await cartTools.SearchProductAsync("跑步鞋");

        Assert.Contains("找到 ", result);
        Assert.Contains("#3 专业跑鞋", result);
        Assert.DoesNotContain("未找到包含", result);
    }
}
