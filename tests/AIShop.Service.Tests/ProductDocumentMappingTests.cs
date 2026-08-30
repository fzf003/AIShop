using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Core.Models;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Rag;

namespace AIShop.Service.Tests;

/// <summary>
/// ProductDocument → ProductDocumentRecord 映射单元测试（Task 4，design §4.3）。
/// 覆盖：Tags→TagsJson 序列化、Price decimal→double 转换、Id/Domain/ProductId/Text 一致性、Emoji 不落库。
/// </summary>
public sealed class ProductDocumentMappingTests
{
    [Fact]
    public void ToRecord_MapsTagsToJsonArray_Losslessly()
    {
        var doc = ToDocument(ProductSeedData.Products.Single(p => p.Id == 5));

        var record = ProductDocumentMapping.ToRecord(doc);

        // TagsJson 是 JSON 数组字符串（规避 SqliteMapper 对 string[] 属性映射的不确定性，design §4.2）；
        // 反序列化回源数组逐元素一致，证明 JSON 序列化无损（Tags 是 embedding 拼接与检索的来源之一，须可复现）
        Assert.StartsWith("[", record.TagsJson);
        var deserialized = JsonSerializer.Deserialize<string[]>(record.TagsJson);
        Assert.NotNull(deserialized);
        Assert.Equal(doc.Tags, deserialized);
    }

    [Fact]
    public void ToRecord_ConvertsDecimalPriceToDouble()
    {
        var doc = ToDocument(ProductSeedData.Products.Single(p => p.Id == 5));

        var record = ProductDocumentMapping.ToRecord(doc);

        // decimal → double：SqliteVec 数值列对 decimal 支持不可靠（design §4.2），存 double；数值本身不变
        Assert.Equal(349.99, record.Price);
        Assert.Equal(typeof(double), record.Price.GetType());
    }

    [Fact]
    public void ToRecord_KeepsIdentityAndTextFields()
    {
        var doc = ToDocument(ProductSeedData.Products.Single(p => p.Id == 12));

        var record = ProductDocumentMapping.ToRecord(doc);

        // Id/Domain/ProductId/Name/Category/Text 一一对应（Text 即 embedding 输入，design §4.1 拼接规则产物）
        Assert.Equal(doc.Id, record.Id);
        Assert.Equal(doc.Domain, record.Domain);
        Assert.Equal(doc.ProductId, record.ProductId);
        Assert.Equal(doc.Name, record.Name);
        Assert.Equal(doc.Category, record.Category);
        Assert.Equal(doc.Text, record.Text);
    }

    [Fact]
    public void ToRecord_DoesNotPersistEmoji()
    {
        // design §4.3：Emoji 仅前端展示，不落向量库（检索回链商品时经 IProductRepository 取）；
        // DTO 无 Emoji 属性即结构保证「存储模型不带展示字段」
        Assert.Null(typeof(ProductDocumentRecord).GetProperty("Emoji"));
    }

    /// <summary>
    /// 按 §4.1 字段契约把种子商品映射为 ProductDocument（Id = "product-{id}"、Domain = "product"）。
    /// 完整映射实现归 Task 6（RagIndexer.BuildDocument），此处只构造测试数据。
    /// </summary>
    private static ProductDocument ToDocument(Product product) =>
        new(
            Id: $"product-{product.Id}",
            Domain: "product",
            ProductId: product.Id,
            Name: product.Name,
            Category: product.Category,
            Tags: product.Tags,
            Price: product.Price,
            Emoji: product.Emoji);
}
