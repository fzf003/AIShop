using System.Net;
using System.Text.Json;
using AIShop.Core.Interfaces;
using AIShop.Core.StaticData;
using AIShop.Service.Agui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T12：根级 <c>GET /products</c> 端点契约 + 「端点层不另建商品数据源」的静态源码断言。
/// 覆盖 spec ADDED R17（商品列表端点：返回全量商品 / 不另建商品数据源 / 公开可读但携带身份即校验）。
/// <para>
/// 驱动方式：WAF <b>真实宿主</b>（<see cref="WebApplicationFactory{TEntryPoint}"/>）+ 临时业务库（Program 的 T16
/// 环境变量 seam <c>Agui__DbConnection</c>）+ 真实播种——临时库由 <c>InitializeAsync</c> 的 <c>MigrateAsync</c>
/// 与幂等播种建好，18 个商品与 3 个种子用户<b>真实存在</b>，故<b>不替换</b>任何仓储替身（「查无此人」用非种子名
/// <c>nobody</c>）。这与 spec R17 场景 1「内容与 <c>IProductRepository.GetAll()</c> 一致」相配：断言对象就是
/// 「真实仓储 + 真实播种」的产物。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主（MigrateAsync / 播种 / RAG 预热）
/// 并操作进程级环境变量，必须与其它宿主级测试串行，避免并行迁移同一 SQLite 文件库 / 互相污染 seam。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiProductsEndpointTests : IDisposable
{
    /// <summary>用户不存在的错误体片段（spec R16 契约，与 AG-UI 面逐字节一致）。</summary>
    private const string UserNotFoundBody = "\"detail\":\"User not found\"";

    /// <summary><c>/products</c> 的实现文件（spec R17 场景 2 的静态断言对象）。</summary>
    private const string EndpointsSourcePath = "src/AIShop.AguiHost/AguiProductEndpoints.cs";

    /// <summary><c>/products</c> 的注册点（装配断言：确实在 Program 里被映射）。</summary>
    private const string ProgramSourcePath = "src/AIShop.AguiHost/Program.cs";

    private readonly List<string> _cleanupDirs = [];
    private readonly List<(string Key, string? Previous)> _envRestore = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // 恢复环境变量（host 构建完成后即恢复；残留会污染同进程后续测试的 Program 顶层 seam 读取）
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();

        foreach (var dir in _cleanupDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略，交由系统清理
            }
        }
        _cleanupDirs.Clear();
    }

    [Fact]
    public async Task GetProducts_WithoutIdentity_ReturnsFullCatalogMatchingRepository()
    {
        // spec R17 场景 1：不带任何身份 → 200 + 全量商品数组，每项含 id/name/category/tags/price/emoji，
        // 且内容与 IProductRepository.GetAll() 逐项一致（顺序、值均相同——端点未重排、未另起映射）。
        // 数量以【播种结果】为准（ProductSeedData.Products），不把字面量 18 当契约。
        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.EnumerateArray().ToList();

        // 5 分钟缓存（products_all）下的同一份数据源：端点响应与仓储读回必须完全一致
        IReadOnlyList<AIShop.Core.Entities.Product> expected;
        using (var scope = factory.Services.CreateScope())
            expected = scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAll();

        // 商品数与目录同源：AguiHost 用的是自己那份种子（共享 18 条 + 本宿主追加 8 条），
        // 不是 Core 的 ProductSeedData（那份维持 18 条、由 Api 使用）。
        Assert.Equal(AguiProductSeedData.Products.Count, expected.Count);
        Assert.Equal(expected.Count, items.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            var product = expected[i];
            var item = items[i];

            // 属性名同时锁定 camelCase 契约：PascalCase 下 GetProperty 会抛 KeyNotFoundException
            Assert.Equal(product.Id, item.GetProperty("id").GetInt32());
            Assert.Equal(product.Name, item.GetProperty("name").GetString());
            Assert.Equal(product.Category, item.GetProperty("category").GetString());
            Assert.Equal(product.Price, item.GetProperty("price").GetDecimal());
            Assert.Equal(product.Emoji, item.GetProperty("emoji").GetString());

            var tags = item.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString() ?? "").ToArray();
            Assert.Equal(product.Tags, tags);
        }
    }

    [Fact]
    public async Task GetProducts_WithUnknownUsername_Returns404WithAguiErrorContract()
    {
        // spec R17 第 3 段「携带即校验」：公开可读 ≠ 忽略身份——带了非空 ?username=nobody 而用户表查无此人，
        // 仍走 REST 分支的存在性校验 → 404 + {"detail":"User not found"}（与 AG-UI 面同一份实现、同一字节契约）。
        // 本条同时锁定 T11 遗留假设：WebApplication 自动把 UseRouting 插到管线最前，
        // 中间件能经 context.GetEndpoint() 读到端点元数据（否则会落入 AG-UI 分支、GET 直接放行 → 200）。
        using var factory = StartFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/products?username=nobody");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(UserNotFoundBody, body);
    }

    [Fact]
    public void ProductsEndpointSource_UsesRepositoryAndNoSecondDataSource()
    {
        // spec R17 场景 2（静态源码断言）：//products 的实现只经 IProductRepository.GetAll() 供数，
        // 不得出现 EF 数据库上下文（AppDbContext/DbContext）或写入路径（SaveChanges）。
        // 期望存在的文件缺失即显式失败（ReadRepoFile 内 FileNotFoundException），避免断言空转。
        var endpointsSource = ReadRepoFile(EndpointsSourcePath);
        var programSource = ReadRepoFile(ProgramSourcePath);

        // 正向：确实经仓储供数（否则「单一数据源」无从谈起）
        Assert.Contains("IProductRepository", endpointsSource);
        Assert.Contains("GetAll", endpointsSource);

        // 反向：端点层不得直连数据库上下文 / 不得有写入路径（一出现即说明另建了数据源）
        Assert.DoesNotContain("AppDbContext", endpointsSource);
        Assert.DoesNotContain("DbContext", endpointsSource);
        Assert.DoesNotContain("SaveChanges", endpointsSource);

        // 装配：注册点确实调用了扩展方法（否则 /products 根本没被映射）
        Assert.Contains("MapProductEndpoints", programSource);
    }

    /// <summary>
    /// 装配 WAF 真实宿主：临时业务 / 向量 / 会话 / 聊天历史四套库（Program T16 环境变量 seam
    /// <c>Agui__*Connection</c>），<b>不替换任何服务</b>——商品与用户都来自真实播种，这正是本类断言的前提。
    /// </summary>
    /// <remarks>
    /// 环境变量在 <c>CreateClient()</c>（触发 host 构建、Program 顶层读取 seam 的时点）之后立即恢复，
    /// 防进程级污染同进程后续测试。宿主离线可启动：模型工厂构造只解析配置、底层客户端懒建。
    /// </remarks>
    private WebApplicationFactory<Program> StartFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agui_t12_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanupDirs.Add(dir);

        SetEnvironment("Agui__DbConnection", $"Data Source={Path.Combine(dir, "business.db")}");
        SetEnvironment("Agui__RagConnection", $"Data Source={Path.Combine(dir, "rag.db")}");
        SetEnvironment("Agui__SessionConnection", $"Data Source={Path.Combine(dir, "sessions.db")}");
        SetEnvironment("Agui__ChatConnection", $"Data Source={Path.Combine(dir, "chat.db")}");

        try
        {
            var factory = new WebApplicationFactory<Program>();
            // CreateClient() 触发 host 构建（Program 顶层读取 seam 配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
            return factory;
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    private void SetEnvironment(string key, string value)
    {
        _envRestore.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, previous) in _envRestore)
            Environment.SetEnvironmentVariable(key, previous);
        _envRestore.Clear();
    }

    /// <summary>
    /// 仓库根定位（约束 E）：从 <see cref="AppContext.BaseDirectory"/> 上溯找含 <c>AIShop.sln</c> 的目录。
    /// <b>不用</b> <c>Path.GetFullPath(相对路径)</c>——它相对测试进程 CWD（bin/Debug/net10.0）解析，会让断言空转。
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIShop.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 上溯找到含 AIShop.sln 的仓库根");
    }

    /// <summary>读取仓库内文件全文；<b>期望存在的文件缺失即显式失败</b>（不得静默跳过，否则断言空转）。</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"期望存在的仓库文件缺失：{fullPath}", fullPath);

        return File.ReadAllText(fullPath);
    }
}
