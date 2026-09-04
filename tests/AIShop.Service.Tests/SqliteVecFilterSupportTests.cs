using AIShop.Infrastructure.Rag;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;

namespace AIShop.Service.Tests;

/// <summary>
/// 验证 SqliteVec（CommunityToolkit.VectorData.SqliteVec）的 LINQ Filter 是否翻译成 SQL WHERE，
/// 决定 search_product 能否走"Category 维度精确过滤 + 向量 KNN"路径（而非纯向量）。
/// </summary>
public sealed class SqliteVecFilterSupportTests
{
    [Fact]
    public async Task SearchAsync_WithCategoryFilter_ReturnsOnlyMatchingCategory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ragvec_{Guid.NewGuid():N}.db");
        try
        {
            var conn = $"Data Source={dbPath}";
            var services = new ServiceCollection();
            services.AddSqliteVectorStore(_ => conn);
            services.AddSqliteCollection<string, ProductDocumentRecord>("product", _ => conn);
            using var sp = services.BuildServiceProvider();
            var collection = sp.GetRequiredService<VectorStoreCollection<string, ProductDocumentRecord>>();
            await collection.EnsureCollectionExistsAsync(default);

            // 两条不同 Category 的商品记录（512 维向量，方向区分开）
            var recKitchen = Make("a", "厨房用品", 0.1f);
            var recDigital = Make("b", "数码", 0.9f);
            await collection.UpsertAsync([recKitchen, recDigital], default);

            // 带 Category 过滤检索：用 recKitchen 的向量查，应只回 厨房用品
            var options = new VectorSearchOptions<ProductDocumentRecord>
            {
                Filter = r => r.Category == "厨房用品",
            };
            var hits = new List<VectorSearchResult<ProductDocumentRecord>>();
            await foreach (var hit in collection.SearchAsync(recKitchen.Embedding, 5, options, default))
                hits.Add(hit);

            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal("厨房用品", h.Record.Category));
            Assert.Contains(hits, h => h.Record.Id == "a");
            Assert.DoesNotContain(hits, h => h.Record.Id == "b");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); }
            catch (IOException) { /* 连接未完全释放时忽略，系统清理 */ }
        }
    }

    private static ProductDocumentRecord Make(string id, string category, float seed) => new()
    {
        Id = id,
        Domain = "product",
        Category = category,
        Name = id,
        Text = $"{id} {category}",
        Embedding = Enumerable.Range(0, 512).Select(i => seed + i * 0.000001f).ToArray(),
    };
}
