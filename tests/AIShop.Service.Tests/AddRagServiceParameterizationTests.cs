using AIShop.Infrastructure;
using AIShop.Infrastructure.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace AIShop.Service.Tests;

/// <summary>
/// AddRagService 可选 connectionString 参数化（agui-host T2）：
/// 传入独立连接串 → 向量库落在指定路径（AguiHost 独立 agui.rag.db 前置）；默认常量保留；
/// 老 AddRagService() 无参调用仍指向默认 aishop.rag.db（回归护航）。
/// </summary>
public sealed class AddRagServiceParameterizationTests
{
    [Fact]
    public async Task AddRagService_WithCustomConnectionString_CreatesStoreAtGivenPath()
    {
        // 传入独立连接串 → SqliteVec 商品集合在 temp 路径建库；若参数被忽略回退默认 aishop.rag.db，temp 路径不会出现文件
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragparam_{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddRagService($"Data Source={dbPath}");
            using var sp = services.BuildServiceProvider();

            var collection = sp.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>();
            await collection.EnsureCollectionExistsAsync(default);

            Assert.True(File.Exists(dbPath), $"传入连接串应使向量库在 {dbPath} 生成");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch (IOException) { /* 连接未完全释放时忽略，系统清理 */ }
        }
    }

    [Fact]
    public void VectorConnectionString_RemainsDefaultAishopRagDb()
    {
        // 默认常量保留且不变：缺省连接串回退点 = aishop.rag.db（spec「参数缺省时沿用默认 aishop.rag.db」）
        Assert.Equal("Data Source=aishop.rag.db", RagDependencyInjection.VectorConnectionString);
    }

    [Fact]
    public async Task AddRagService_WithoutConnectionString_UsesDefaultStore()
    {
        // 回归护航：老 AddRagService() 无参调用行为零变化——向量库仍指向默认连接串（cwd 下 aishop.rag.db），
        // 与老宿主（AIShop.Api Program）使用方式一致，语义检索装配链路不抛异常
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "aishop.rag.db");
        try
        {
            var services = new ServiceCollection();
            services.AddRagService();
            using var sp = services.BuildServiceProvider();

            var collection = sp.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>();
            await collection.EnsureCollectionExistsAsync(default);

            Assert.True(File.Exists(dbPath), "无参 AddRagService() 应沿用默认连接串在 cwd 生成 aishop.rag.db");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch (IOException) { /* 连接未完全释放时忽略，系统清理 */ }
        }
    }
}
