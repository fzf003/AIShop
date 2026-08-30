using AIShop.Core.Entities;
using AIShop.Core.Models;
using AIShop.Core.StaticData;

namespace AIShop.Service.Tests;

/// <summary>
/// ProductDocument 领域模型单元测试（Task 2）。
/// 重点验证 design §4.1 的 Text 拼接规则与确定性（AI-2）：Text 是 embedding 输入，
/// 同一商品多次构建必须产出完全一致的文本，否则同一条目索引向量会漂移，破坏检索确定性。
/// </summary>
public sealed class ProductDocumentTests
{
    [Fact]
    public void Text_ConcatenatesFieldsPerRule_WithChineseCommaSeparatedTags()
    {
        // 用种子商品 Id=5（意式浓缩咖啡机）验证 §4.1 拼接模板；价格 349.99 硬编码断言（若运行环境小数分隔符非「.」则此测试红）
        var doc = ToDocument(ProductSeedData.Products.Single(p => p.Id == 5));

        Assert.Equal("意式浓缩咖啡机。类别：厨房用品。标签：咖啡、浓缩、厨房、早晨。价格：¥349.99", doc.Text);
    }

    [Fact]
    public void Text_IsDeterministic_TwoConstructionsProduceIdenticalText()
    {
        // AI-2 确定性前置：相同输入两次构建，Text 完全一致（embedding 输入可复现）
        var product = ProductSeedData.Products.Single(p => p.Id == 12);
        var first = ToDocument(product);
        var second = ToDocument(product);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal("香薰蜡烛套装。类别：家居。标签：放松、家居、香薰、礼物。价格：¥34.99", second.Text);
    }

    [Fact]
    public void Text_AllSeedProducts_FollowConcatRule()
    {
        // 全量种子回归：18 条商品每条 Text 都严格符合 §4.1 模板（含中文顿号分隔、¥ 价格前缀）
        foreach (var product in ProductSeedData.Products)
        {
            var doc = ToDocument(product);
            var expected = $"{product.Name}。类别：{product.Category}。标签：{string.Join("、", product.Tags)}。价格：¥{product.Price}";
            Assert.Equal(expected, doc.Text);
        }
    }

    /// <summary>
    /// 按 §4.1 字段契约把种子商品映射为 ProductDocument（Id/Domain 规则同 design：
    /// Id = "product-{id}"、Domain = "product"）。完整映射实现归 Task 6（RagIndexer.BuildDocument），
    /// 此处只构造测试数据，不引入依赖。
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
