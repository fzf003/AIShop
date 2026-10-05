using AIShop.Core.Services;

namespace AIShop.Api.Tests;

/// <summary>
/// ReplySanitizer（Core 商品编号清洗；agui-host 新链的 ReplySanitizingChatClient 直接调用）的
/// Core 级边界单测。api-freeze-2026-10-05 删除的 ChatReplySanitizationTests 曾通过 /api/chat
/// 端点间接覆盖这些边界；本文件把同一批边界用例直接落在 ReplySanitizer.Clean / CleanIncremental 上，
/// 使删除端点测试后边界行为仍被覆盖（覆盖缺口 G1）。
/// 语义：有效商品编号范围 1..18（含端点）；清洗只作用于文本，不误删名称/价格。
/// </summary>
public sealed class ReplySanitizerTests
{
    // ---------- 超范围 id 边界（HashIdPattern + IsProductId 1..18）----------

    // 原 deleted 用例：PostChat_ReplyWithOutOfRangeHashIds_KeepsNonProductIds_StripsInRangeIds
    [Theory]
    [InlineData("#1")]   // 下界（含）
    [InlineData("#5")]
    [InlineData("#18")]  // 上界（含）
    public void Clean_StripsHashId_WhenIdInRange(string marker)
    {
        var result = ReplySanitizer.Clean($"{marker} 某商品");

        Assert.DoesNotContain(marker, result);
        Assert.Contains("某商品", result);
    }

    // 非商品 ID 的 #数字必须保留（防误删订单号 / 越界编号）
    [Theory]
    [InlineData("#0")]       // 低于下界
    [InlineData("#19")]      // 刚越上界
    [InlineData("#20")]
    [InlineData("#123456")]  // 订单号形态
    public void Clean_KeepsHashId_WhenIdOutOfRange(string marker)
    {
        var result = ReplySanitizer.Clean($"订单号 {marker} 处理中");

        Assert.Contains(marker, result);
        Assert.Contains("处理中", result);
    }

    // 原 deleted 用例的完整文本：同段内夹杂保留项与删除项，验证区分正确
    [Fact]
    public void Clean_MixedHashIds_StripsInRangeKeepsNonProductIds()
    {
        const string raw =
            "订单号 #123456 处理中，#20 号商品，#5 意式浓缩咖啡机 ¥349.99，商品ID: 4 无线降噪耳机，#18 户外帐篷，#19 待上架商品";

        var result = ReplySanitizer.Clean(raw);

        // 非商品 ID 的 #数字保留
        Assert.Contains("#123456", result);
        Assert.Contains("#20", result);
        Assert.Contains("#19", result);
        Assert.Contains("订单号", result);
        Assert.Contains("处理中", result);

        // 范围内的 #5 / #18 删除
        Assert.DoesNotContain("#5", result);
        Assert.DoesNotContain("#18", result);
        Assert.Contains("意式浓缩咖啡机", result);
        Assert.Contains("349.99", result);
        Assert.Contains("户外帐篷", result);

        // 「商品ID: 4」删除，商品名保留
        Assert.DoesNotContain("商品ID", result);
        Assert.Contains("无线降噪耳机", result);
    }

    // ---------- 格式变体 ----------

    // 原 deleted 用例：PostChat_ReplyWithFixedProductIdFormat_StripsFixedFormat
    [Fact]
    public void Clean_MixedFixedIdFormatVariants_AreStripped_NamesPricesKept()
    {
        const string raw =
            "无线降噪耳机 商品Id:4 售价 ¥249.99，意式浓缩咖啡机 商品id:5 ¥349.99，高级瑜伽垫 商品Id：6";

        var result = ReplySanitizer.Clean(raw);

        // FixedIdPattern：英文冒号 / 小写 id（IgnoreCase）/ 中文冒号 三种变体精确删除
        Assert.DoesNotContain("商品Id:4", result);
        Assert.DoesNotContain("商品id:5", result);
        Assert.DoesNotContain("商品Id：6", result);
        // 名称与价格保留
        Assert.Contains("无线降噪耳机", result);
        Assert.Contains("249.99", result);
        Assert.Contains("意式浓缩咖啡机", result);
        Assert.Contains("高级瑜伽垫", result);
    }

    // 原 deleted 用例：PostChat_ReplyWithProductIdForIsVariants_StripsFallbackVariants
    [Fact]
    public void Clean_ProductIdForIsVariants_AreStripped_NamesPricesKept()
    {
        const string raw =
            "为您推荐无线降噪耳机，价格为249.99元，商品ID为4。另一款意式浓缩咖啡机商品ID是5，需要为您加入购物车吗？";

        var result = ReplySanitizer.Clean(raw);

        // ProductIdLabelPattern 兜底：「商品ID为4」「商品ID是5」（为/是 连接词变体）删除
        Assert.DoesNotContain("商品ID", result);
        Assert.DoesNotContain("为4", result);
        Assert.DoesNotContain("是5", result);
        // 名称与价格保留
        Assert.Contains("无线降噪耳机", result);
        Assert.Contains("意式浓缩咖啡机", result);
        Assert.Contains("249.99", result);
    }

    // ---------- 核心行为回归锚点 ----------

    // 同时混入「在册编号」与「应保留的名称/价格」：若清洗根本没跑到（或什么都没发生）本用例必红
    [Fact]
    public void Clean_InRangeIdsAndPreservedContent_Anchor()
    {
        const string raw = "推荐 #3 专业跑鞋（¥129.99）与商品Id:4 意式浓缩咖啡机 ¥349.99";

        var result = ReplySanitizer.Clean(raw);

        // 在册编号被清（区分「已清洗」与「未清洗」）
        Assert.DoesNotContain("#3", result);
        Assert.DoesNotContain("商品Id:4", result);
        Assert.DoesNotContain("#", result);
        // 名称/价格保留（区分「正确清洗」与「全删」）
        Assert.Contains("专业跑鞋", result);
        Assert.Contains("129.99", result);
        Assert.Contains("意式浓缩咖啡机", result);
        Assert.Contains("349.99", result);
    }

    // ---------- CleanIncremental（流式增量；边界 = 跨 chunk 拆分）----------

    // 无编号纯文本：整段可安全发出
    [Fact]
    public void CleanIncremental_PlainText_EmitsAll()
    {
        var (safeToEmit, remaining) = ReplySanitizer.CleanIncremental("你好，欢迎光临", "");

        Assert.Equal("你好，欢迎光临", safeToEmit);
        Assert.Equal("", remaining);
    }

    // 跨 chunk 拆开的 #编号：先缓冲可疑前缀，补全后编号前的文本才发出、编号留在缓冲区不泄漏
    [Fact]
    public void CleanIncremental_SplitHashAcrossChunks_HoldsPatternInBuffer()
    {
        // 第一块以「#」结尾（可能是编号前缀）→ 整段缓冲
        var (safe1, rest1) = ReplySanitizer.CleanIncremental("推荐 #", "");
        Assert.Equal("", safe1);
        Assert.Equal("推荐 #", rest1);

        // 第二块补全为「#4」→ 发出「#4」之前的文本，「#4…」留在缓冲区
        var (safe2, rest2) = ReplySanitizer.CleanIncremental("4 无线降噪耳机", rest1);
        Assert.Equal("推荐 ", safe2);
        Assert.Equal("#4 无线降噪耳机", rest2);
        Assert.DoesNotContain("#4", safe2);
    }
}
