using System.Net.Http.Json;
using AIShop.Api.Features.Chat;
using AIShop.Core.Entities;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIShop.Api.Tests;

/// <summary>
/// T7 测试集合定义：本类启动多个 WebApplicationFactory / SQLite 文件库，
/// 置于 DisableParallelization 串行集合，避免与 ServiceDefaultsDebugTests 等并行宿主竞争
/// （learnings 先例：共享工作树并行 WebApplicationFactory 会触发 flaky 失败）。
/// </summary>
[CollectionDefinition(nameof(ProgramSeedingTests), DisableParallelization = true)]
public sealed class ProgramSeedingTestsCollection;

/// <summary>
/// T7 测试集合：Program.cs 启动建表（MigrateAsync）+ 播种 18 商品。
/// 对应 spec「Product 表幂等播种」与「UserPreferences 表持久化」：
///  - 单元级：EF 模型生成的 schema 契约（表名/约束 PascalCase）与播种幂等
///  - 端到端全新库：MigrateAsync 建全部 schema → 播种恰 18 行，重复启动不产生重复行
///  - 端到端已迁移库：迁移历史已存在 → 启动跳过建表 → 播种成功
///  - user_preferences 空表首写可落库（完整累加链路归 T14/T20）
/// 注意：EF 实际生成的表名为 PascalCase（Products / UserPreferences，约束 PK_Products / PK_UserPreferences），
/// 迁移 InitialCreate 即按此 schema 建表（Migration 与 EnsureCreated 同源于 EF 模型）。
/// </summary>
[Collection(nameof(ProgramSeedingTests))]
public sealed class ProgramSeedingTests : IDisposable
{
    private readonly List<string> _createdDbPaths = [];
    private readonly List<WebApplicationFactory<Program>> _liveFactories = [];

    public void Dispose()
    {
        foreach (var factory in _liveFactories)
            factory.Dispose();
        _liveFactories.Clear();

        // 释放 SQLite 连接池对文件的句柄后，删除本测试创建的临时 db 文件
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
        var path = Path.Combine(Path.GetTempPath(), $"t7_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    private static DbContextOptions<AppDbContext> OptionsFor(string connStr) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options;

    /// <summary>把 AppDbContext 注册替换为指向指定 SQLite 文件库（不预播种，让 Program.cs 的启动播种执行）。</summary>
    private static void ReplaceDbWith(IServiceCollection services, string connStr)
    {
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connStr));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
    }

    // ---------- 单元级：schema 契约 + 播种幂等（核对 EF 生成的 DDL） ----------

    [Fact]
    public async Task FreshDatabase_EnsureCreated_EfSchemaMatchesHandwrittenDdl_AndSeedIsIdempotent()
    {
        var dbPath = NewDbPath("unit_new");
        var connStr = $"Data Source={dbPath}";
        var options = OptionsFor(connStr);

        // 1. 全新库 EnsureCreated 建全部 schema
        using (var ctx = new AppDbContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // 2. 核对：EF 生成的表名为 PascalCase Products/UserPreferences（与 tasks.md 早期小写示例相反），
        //    约束名 PK_Products / PK_UserPreferences，非空列全 NOT NULL，Price/Tags 存 TEXT。
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('Products','UserPreferences') ORDER BY name;";
                var names = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    names.Add(reader.GetString(0));
                Assert.Equal(["Products", "UserPreferences"], names);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='Products';";
                var sql = Assert.IsType<string>(await cmd.ExecuteScalarAsync());
                Assert.Contains("\"PK_Products\" PRIMARY KEY", sql);
                Assert.Contains("\"Price\" TEXT NOT NULL", sql);
                Assert.Contains("\"Tags\" TEXT NOT NULL", sql);
                Assert.Contains("\"Name\" TEXT NOT NULL", sql);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='UserPreferences';";
                var sql = Assert.IsType<string>(await cmd.ExecuteScalarAsync());
                Assert.Contains("\"PK_UserPreferences\" PRIMARY KEY", sql);
                Assert.Contains("\"KeywordsJson\" TEXT NOT NULL", sql);
                Assert.Contains("\"UpdatedAt\" TEXT NOT NULL", sql);
            }
        }

        // 3. 播种：空表才 AddRange（与 Program.cs 同一逻辑）
        using (var ctx = new AppDbContext(options))
        {
            Assert.False(await ctx.Products.AnyAsync());
            if (!await ctx.Products.AnyAsync())
            {
                ctx.Products.AddRange(ProductSeedData.Products);
                await ctx.SaveChangesAsync();
            }
        }
        using (var ctx = new AppDbContext(options))
        {
            Assert.Equal(18, await ctx.Products.CountAsync());
            Assert.Equal(18, ProductSeedData.Products.Count);
        }

        // 4. 幂等：再次执行播种逻辑，不产生重复行
        using (var ctx = new AppDbContext(options))
        {
            if (!await ctx.Products.AnyAsync())
            {
                ctx.Products.AddRange(ProductSeedData.Products);
                await ctx.SaveChangesAsync();
            }
        }
        using (var ctx = new AppDbContext(options))
        {
            Assert.Equal(18, await ctx.Products.CountAsync());
        }
    }

    // ---------- 单元级：既有库缺表路径（兜底 DDL 补建） ----------

    [Fact]
    public async Task ExistingDatabase_MissingNewTables_DdlCreatesTables_AndSeeds18()
    {
        var dbPath = NewDbPath("unit_existing");
        var connStr = $"Data Source={dbPath}";
        var options = OptionsFor(connStr);

        // 1. 先建含既有表（users/sessions/carts 等）的旧库，再删掉 Products/UserPreferences，模拟历史库
        using (var ctx = new AppDbContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            await ctx.Database.ExecuteSqlRawAsync(
                "DROP TABLE \"Products\"; DROP TABLE \"UserPreferences\";");
        }

        // 2. 模拟 Program.cs 兜底建表 DDL（与 Program.cs 中完全一致的 CREATE TABLE IF NOT EXISTS，
        //    表名/约束名用 EF 生成的 PascalCase，否则播种/查询会因表名不匹配失败）
        using (var ctx = new AppDbContext(options))
        {
            await ctx.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "Products" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "Category" TEXT NOT NULL,
                    "Tags" TEXT NOT NULL,
                    "Price" TEXT NOT NULL,
                    "Emoji" TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS "UserPreferences" (
                    "UserId" TEXT NOT NULL CONSTRAINT "PK_UserPreferences" PRIMARY KEY,
                    "KeywordsJson" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);

            // 3. 播种（空表检查 → AddRange + SaveChanges，与 Program.cs 同一逻辑）
            if (!await ctx.Products.AnyAsync())
            {
                ctx.Products.AddRange(ProductSeedData.Products);
                await ctx.SaveChangesAsync();
            }
        }

        // 4. 断言：两表被补建、18 商品播种成功、UserPreferences 空表首写可落库（验证③）
        using (var ctx = new AppDbContext(options))
        {
            Assert.Equal(18, await ctx.Products.CountAsync());

            var userId = Guid.NewGuid();
            ctx.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = "{\"咖啡\":1}",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();

            var readBack = await ctx.UserPreferences.FindAsync(userId);
            Assert.NotNull(readBack);
            Assert.Equal("{\"咖啡\":1}", readBack!.KeywordsJson);
        }
    }

    // ---------- 端到端：全新库启动（验证①）+ 重复启动幂等 ----------

    [Fact]
    public async Task Startup_NewDatabase_GetProducts_Returns18_AndRerunNoDuplicates()
    {
        var dbPath = NewDbPath("e2e_new");
        var connStr = $"Data Source={dbPath}";

        // 第一次启动（全新库）：EnsureCreated 建全部 schema → 兜底 DDL 无副作用 → 播种 18
        using (var factory = CreateFactory(connStr))
        {
            _liveFactories.Add(factory);
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/products");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProductsResponse>();
            Assert.NotNull(result);
            Assert.Equal(18, result!.products.Length);
        }
        _liveFactories.Clear();
        SqliteConnection.ClearAllPools();

        // 第二次启动（既有库，已含 Products）：AnyAsync 命中 → 不重复播种 → 仍 18 行
        using (var factory = CreateFactory(connStr))
        {
            _liveFactories.Add(factory);
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/products");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProductsResponse>();
            Assert.NotNull(result);
            Assert.Equal(18, result!.products.Length);
        }
        _liveFactories.Clear();
        SqliteConnection.ClearAllPools();
    }

    // ---------- 端到端：已迁移库启动（迁移跳过）+ user_preferences 可写（验证③） ----------

    [Fact]
    public async Task Startup_MigratedDatabase_SkipsMigration_GetProducts_Returns18_AndUserPreferencesWritable()
    {
        var dbPath = NewDbPath("e2e_existing");
        var connStr = $"Data Source={dbPath}";

        // 预建完整 schema：用 Migrate 写迁移历史（模拟已迁移库），与宿主 Program.cs 的 MigrateAsync 同一路径。
        // （原「EnsureCreated 旧库 + 启动兜底 DDL 补缺表」场景在迁移世界不适用：迁移保证 schema 完整，
        //   历史库升级走迁移机制而非启动时手动补表。）
        var options = OptionsFor(connStr);
        using (var ctx = new AppDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        // 启动：MigrateAsync 检测迁移历史已存在 → 跳过建表 → 空 Products 表播种 18
        using (var factory = CreateFactory(connStr))
        {
            _liveFactories.Add(factory);
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/products");
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ProductsResponse>();
            Assert.NotNull(result);
            Assert.Equal(18, result!.products.Length);
        }
        _liveFactories.Clear();
        SqliteConnection.ClearAllPools();

        // user_preferences 空表首写可落库：表已由迁移建好，写入一行并读回成功
        using (var ctx = new AppDbContext(options))
        {
            var userId = Guid.NewGuid();
            ctx.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = "{\"健身\":2}",
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();

            var readBack = await ctx.UserPreferences.FindAsync(userId);
            Assert.NotNull(readBack);
            Assert.Equal("{\"健身\":2}", readBack!.KeywordsJson);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connStr) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => ReplaceDbWith(services, connStr)));

    private sealed record ProductsResponse(ProductDto[] products);
}
