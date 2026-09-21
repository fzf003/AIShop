using System.Reflection;
using System.Text.Json;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Services;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AIShop.Service.Tests;

/// <summary>
/// S2：<see cref="RecommendationToolProvider"/> 的「可推送负载」入口（<c>TryBuildPushPayloadAsync</c>）
/// 与推送门控纯函数 <c>ShouldPush</c>，以及**工具契约零回归**。
///
/// 三段：
/// ① 契约零回归 —— 工具入口返回值与改动前的**逐字节**快照一致（含 <c>hasRecommendation=false</c> 兜底分支），
///    且构造依赖为 5 个（第 5 个是 B1 新增的可选语义检索）、不含被禁服务；
/// ② 推送门控 —— 正例（命中关键词且推荐非空）形状与工具结果同构；两条**独立**否分支（无关键词命中 / 推荐列表为空）；
/// ③ 身份缺失 —— 推送返回 <c>null</c>，且**不是**工具路径那条「无法确定用户身份」的说明性负载。
///
/// <para>B1 追加第四段：<b>语义门控与语义反推内容</b>（修 L8「T恤有吗」漏推）——全部用替身
/// <see cref="IProductSemanticSearch"/> 驱动（不依赖 bge 模型文件，确定性可进 CI）：
/// 语义命中放行 / 闲聊不放行 / 关键词路径优先不漂移 / 检索异常降级为关键词门控 / 未注入时与改动前逐字节一致。</para>
///
/// <para>D 追加第五段：<b>否定语境门控</b>（修 L7「我最近在健身，不过今天不买」误推）——
/// 纯函数 <c>IsNegatedIntent</c> 的判定表 + 「不买只排除某一项、仍在找东西」的**误伤防护**用例。</para>
/// </summary>
public sealed class RecommendationPushPayloadTests
{
    private const string TestUser = "marla";

    /// <summary>命中白名单关键词的上一轮依据（回归基线用）。</summary>
    private const string KeywordMessage = "我想买跑步鞋";

    /// <summary>无白名单关键词的闲聊消息（spec R2 场景 1 的否分支）。</summary>
    private const string ChitChatMessage = "你好呀";

    /// <summary>
    /// 购物意图明确但**不在**白名单里的消息（盘点 L8）：23 组关键词里既无「T恤」也无展开命中，
    /// 且目录里唯一 T 恤（id=2）的 Tags 也不含「T恤」——关键词路径必然漏掉它。
    /// </summary>
    private const string TShirtMessage = "T恤有吗";

    /// <summary>无关键词命中时 RecommendationService 的精选兜底文案（口径单一来源）。</summary>
    private const string CuratedFallbackMessage = "为您精选商品";

    // ---------- 改动前基线快照 ----------
    // 以下 5 条字面量是**重构前**（本工单改动前）对当前 RecommendationToolProvider 的实测输出，
    // 逐字节冻结。用途：重构（抽私有 builder）后工具入口必须一字不差 —— 这是「工具契约零回归」的硬证据。
    // （JsonSerializerDefaults.Web 的默认编码器会转义非 ASCII，故此处为纯 ASCII 的 \uXXXX 形式。）

    private const string BaselineQueryHit =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u7535\u5B50\u4EA7\u54C1"",""\u5065\u5EB7""],""products"":[{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":8,""name"":""\u4E0D\u9508\u94A2\u4FDD\u6E29\u6C34\u74F6"",""category"":""\u914D\u4EF6"",""price"":24.99,""emoji"":""\uD83D\uDCA7"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""},{""id"":14,""name"":""\u690D\u7269\u86CB\u767D\u7C89"",""category"":""\u5065\u5EB7"",""price"":39.99,""emoji"":""\uD83D\uDCAA"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u5065\u8EAB\u300D""}]}";

    private const string BaselineCuratedFallback =
        @"{""message"":""\u4E3A\u60A8\u7CBE\u9009\u5546\u54C1"",""hasRecommendation"":false,""categories"":[],""products"":[]}";

    private const string BaselinePreferenceOnly =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u7535\u5B50\u4EA7\u54C1"",""\u5065\u5EB7""],""products"":[{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":8,""name"":""\u4E0D\u9508\u94A2\u4FDD\u6E29\u6C34\u74F6"",""category"":""\u914D\u4EF6"",""price"":24.99,""emoji"":""\uD83D\uDCA7"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":14,""name"":""\u690D\u7269\u86CB\u767D\u7C89"",""category"":""\u5065\u5EB7"",""price"":39.99,""emoji"":""\uD83D\uDCAA"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""}]}";

    private const string BaselineIdentityMissing =
        @"{""message"":""\u65E0\u6CD5\u786E\u5B9A\u7528\u6237\u8EAB\u4EFD"",""hasRecommendation"":false,""categories"":[],""products"":[]}";

    private const string BaselineQueryAndPreference =
        @"{""message"":""\u6839\u636E\u60A8\u7684\u5174\u8DA3\uFF0C\u4E3A\u60A8\u63A8\u8350\uFF1A"",""hasRecommendation"":true,""categories"":[""\u7535\u5B50\u4EA7\u54C1"",""\u978B\u7C7B"",""\u5065\u8EAB"",""\u914D\u4EF6"",""\u5065\u5EB7""],""products"":[{""id"":4,""name"":""\u65E0\u7EBF\u964D\u566A\u8033\u673A"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":249.99,""emoji"":""\uD83C\uDFA7"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":7,""name"":""\u590D\u53E4\u9ED1\u80F6\u5531\u7247\u673A"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\uD83C\uDFB5"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":10,""name"":""\u667A\u80FD\u8FD0\u52A8\u624B\u8868"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":199.99,""emoji"":""\u231A"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":15,""name"":""\u65E0\u7EBF\u5145\u7535\u677F"",""category"":""\u7535\u5B50\u4EA7\u54C1"",""price"":29.99,""emoji"":""\uD83D\uDD0B"",""reason"":""\u56E0\u4E3A\u4F60\u63D0\u5230\u300C\u8033\u673A\u300D""},{""id"":3,""name"":""\u4E13\u4E1A\u8DD1\u978B"",""category"":""\u978B\u7C7B"",""price"":129.99,""emoji"":""\uD83D\uDC5F"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""},{""id"":6,""name"":""\u9AD8\u7EA7\u745C\u4F3D\u57AB"",""category"":""\u5065\u8EAB"",""price"":59.99,""emoji"":""\uD83E\uDDD8"",""reason"":""\u6839\u636E\u4F60\u7684\u504F\u597D\u300C\u5065\u8EAB\u300D""}]}";

    // ---------- ① 工具契约零回归 ----------

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenQueryHitsCatalog()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselineQueryHit, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenCuratedFallbackApplies()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var json = await harness.Provider.RecommendProductsAsync("帮我随便看看");

        Assert.Equal(BaselineCuratedFallback, json);
        // 正向锚点：兜底分支确为 hasRecommendation=false（防「与快照相等」因两份都退化成别的东西而空转）
        Assert.False(JsonDocument.Parse(json).RootElement.GetProperty("hasRecommendation").GetBoolean());
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenPreferenceDrivesRecommendation()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselinePreferenceOnly, await harness.Provider.RecommendProductsAsync("帮我随便看看"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenQueryAndPreferenceMixed()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselineQueryAndPreference, await harness.Provider.RecommendProductsAsync("我想买耳机"));
    }

    [Fact]
    public async Task ShouldReturnByteIdenticalJson_WhenCurrentUserMissing()
    {
        using var harness = new Harness();   // Accessor 未绑定用户

        Assert.Equal(BaselineIdentityMissing, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));
    }

    [Fact]
    public void ShouldKeepFiveConstructorDependencies_WithoutProhibitedServices()
    {
        var parameters = typeof(RecommendationToolProvider)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .ToList();

        // B1：新增第 5 个**可选**依赖 IProductSemanticSearch（本地 bge ONNX 语义检索，非大模型）。
        // 断言「恰 5 个」防止后续再无声增依赖；逐项钉死类型防止被换成别的服务。
        Assert.Equal(5, parameters.Count);
        string[] expectedTypes =
            ["IServiceScopeFactory", "ICurrentUserAccessor", "IMemoryStore", "IMemoryCache", "IProductSemanticSearch"];
        Assert.Equal(expectedTypes, parameters.Select(parameter => parameter.ParameterType.Name));

        // 可选参语义（源码兼容）：既有直构造点 / 未注册 RAG 的宿主不必传 → 默认 null → 语义路径关闭
        Assert.Equal(typeof(IProductSemanticSearch), parameters[4].ParameterType);
        Assert.True(parameters[4].HasDefaultValue, "第 5 个依赖必须是可选参数（保持既有直构造点源码兼容）");
        Assert.Null(parameters[4].DefaultValue);

        // 零大模型调用的硬约束（IMemoryService 装配了 LlmReranker 精排）：bge 语义检索不在此列（进程内 ONNX，非 LLM）
        Assert.DoesNotContain(typeof(IChatClient), parameters.Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(typeof(IMemoryService), parameters.Select(parameter => parameter.ParameterType));
    }

    // ---------- ② 推送门控 ----------

    [Fact]
    public async Task ShouldBuildPushPayload_WhenKeywordHitsAndRecommendationNonEmpty()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var toolJson = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var push = await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋");

        var payload = Assert.IsType<JsonElement>(push);   // 非 null（JsonElement 是值类型，须显式判空后取值）
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        var message = payload.GetProperty("message");
        Assert.Equal(JsonValueKind.String, message.ValueKind);
        Assert.True(payload.GetProperty("hasRecommendation").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("categories").EnumerateArray().ToArray());

        var products = payload.GetProperty("products").EnumerateArray().ToList();
        Assert.InRange(products.Count, 1, 6);   // Top-6 截断口径不变

        string[] expectedFields = ["category", "emoji", "id", "name", "price", "reason"];
        foreach (var product in products)
        {
            var fieldNames = product.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedFields, fieldNames);
        }

        // 同 query / 同偏好 → 推送负载与工具结果**逐字节同构**（同一 builder 的唯一证据）
        Assert.Equal(toolJson, payload.GetRawText());
    }

    [Fact]
    public void ShouldPush_WhenKeywordHitsAndProductsNonEmpty()
    {
        // 关键词命中 → 推（B1 后判据为并集，此分支语义不变，回归）
        Assert.True(RecommendationToolProvider.ShouldPush(["跑步"], [], PayloadWithProducts()));
    }

    [Fact]
    public void ShouldPush_WhenOnlySemanticHitsAndProductsNonEmpty()
    {
        // 关键词未命中但语义命中 → 推（B1 修 L8 的那半边判据；「T恤有吗」走的就是这条）
        Assert.True(RecommendationToolProvider.ShouldPush([], [Hit(2, 0.72)], PayloadWithProducts()));
    }

    [Fact]
    public async Task ShouldNotPush_WhenChatRoundHasNoKeywordHit()
    {
        // 关键：本轮**有**偏好（故推荐列表非空），使「关键词未命中」成为唯一的阻断条件。
        // 否则精选兜底的 products 恒为空，本条与下一条就分不出是哪半句在起作用（断言空转）。
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        // 正向锚点：同一依据经工具路径确实算出了非空推荐 —— 证明确有东西可推，只是门控拦住了它
        var toolJson = await harness.Provider.RecommendProductsAsync("你好呀");
        Assert.NotEmpty(JsonDocument.Parse(toolJson).RootElement.GetProperty("products").EnumerateArray());

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("你好呀"));
        // 纯函数分支（与上条同源，独立可测）：关键词与语义皆无 → 不推（spec R2 场景 1）
        Assert.False(RecommendationToolProvider.ShouldPush([], [], PayloadWithProducts()));
    }

    [Fact]
    public async Task ShouldNotPush_WhenKeywordHitsButRecommendationIsEmpty()
    {
        // 空商品目录：关键词**命中**（白名单匹配与目录无关）但算不出任何商品 → 推荐列表为空
        using var harness = new Harness(products: []);
        harness.Accessor.SetCurrentUser(TestUser);

        // 正向锚点：工具路径确实走到了「命中关键词但无商品」的兜底负载（而非「关键词没命中」）
        var toolJson = await harness.Provider.RecommendProductsAsync("我想买跑步鞋");
        var toolRoot = JsonDocument.Parse(toolJson).RootElement;
        Assert.False(toolRoot.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal(CuratedFallbackMessage, toolRoot.GetProperty("message").GetString());
        Assert.Empty(toolRoot.GetProperty("products").EnumerateArray());

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋"));
        // 纯函数分支：依据非空但推荐为空 → 不推（防「兜底文案覆盖上一次面板」）
        Assert.False(RecommendationToolProvider.ShouldPush(["跑步"], [], PayloadWithProducts([])));
    }

    [Fact]
    public void ShouldNotPush_WhenSemanticHitsButRecommendationIsEmpty()
    {
        // 语义命中但推荐列表为空（反推词在目录里召不回商品）→ 仍不推（后半句判据独立生效）
        Assert.False(RecommendationToolProvider.ShouldPush([], [Hit(2, 0.72)], PayloadWithProducts([])));
    }

    // ---------- ③ 身份缺失 ----------

    [Fact]
    public async Task ShouldReturnNullAndNotIdentityMissingPayload_WhenCurrentUserMissing()
    {
        using var harness = new Harness();   // Accessor 未绑定用户

        // 正向锚点：工具路径在此场景下**确实**产出「无法确定用户身份」这条说明性负载……
        Assert.Equal(BaselineIdentityMissing, await harness.Provider.RecommendProductsAsync("我想买跑步鞋"));

        // ……而推送入口返回 null：身份问题不是推荐，不得推给面板覆盖上一次内容
        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync("我想买跑步鞋"));
    }

    // ---------- ④ B1：语义门控 + 内容来源第二层（修 L8「T恤有吗」漏推）----------

    /// <summary>
    /// L8 修复锚点（B1-dT）：购物意图明确、但**不在**关键词白名单里的「T恤有吗」，由语义命中放行门控，
    /// 并用命中商品的类别反推关键词，使负载真的含 id=2（目录内唯一 T 恤，其 Tags 里并没有「T恤」）。
    ///
    /// <para><b>正锚点（= 改动前判据的等价复现）</b>：同款 provider、同一句话，未注入语义检索（= B1 之前的
    /// 门控判据）时**不推**——证明下面的绿色不是环境自带的。</para>
    /// </summary>
    [Fact]
    public async Task ShouldPushTShirtRound_WhenSemanticSearchFindsIt()
    {
        // 正锚点：B1 之前（无语义检索）这句必然推不出来（23 组白名单里既无「T恤」也无展开命中）
        using var before = new Harness();
        before.Accessor.SetCurrentUser(TestUser);
        Assert.Null(await before.Provider.TryBuildPushPayloadAsync(TShirtMessage));

        var semantic = new StubSemanticSearch([Hit(2, 0.72, name: "有机棉T恤", category: "服装")]);
        using var after = new Harness(semanticSearch: semantic);
        after.Accessor.SetCurrentUser(TestUser);

        var push = await after.Provider.TryBuildPushPayloadAsync(TShirtMessage);
        var payload = Assert.IsType<JsonElement>(push);

        // 检索输入就是本轮依据（不是空串、也不是别的什么）
        Assert.Equal(new[] { TShirtMessage }, semantic.Queries);
        Assert.True(payload.GetProperty("hasRecommendation").GetBoolean());

        var products = payload.GetProperty("products").EnumerateArray().ToList();
        Assert.NotEmpty(products);
        Assert.Contains(products, product => product.GetProperty("id").GetInt32() == 2);   // ← L8 修复锚点

        // 内容来源第二层：反推词 = 命中商品的类别「服装」（目录内真实存在的串），reason 按它派生
        var tshirt = products.Single(product => product.GetProperty("id").GetInt32() == 2);
        Assert.Equal("有机棉T恤", tshirt.GetProperty("name").GetString());
        Assert.Equal("服装", tshirt.GetProperty("category").GetString());
        Assert.Equal("因为你提到「服装」", tshirt.GetProperty("reason").GetString());
    }

    /// <summary>
    /// 闲聊轮不放行（spec R2 场景 1，等价于既有 <see cref="ShouldNotPush_WhenChatRoundHasNoKeywordHit"/>，
    /// 但语义路径**已激活且返回空**）：放宽判据不得把闲聊也放进来。
    /// </summary>
    [Fact]
    public async Task ShouldNotPushChitChat_WhenSemanticSearchReturnsNothing()
    {
        var semantic = new StubSemanticSearch([]);
        using var harness = new Harness(semanticSearch: semantic);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync(ChitChatMessage));
        // 正锚点：语义检索确实被走到（否则「没推」可能只是没启用语义路径，断言空转）
        Assert.Equal(new[] { ChitChatMessage }, semantic.Queries);
    }

    /// <summary>
    /// B1-dT2（回归硬约束）：语义不可用或结果与关键词无关时，**关键词路径优先**——
    /// 「我想买跑步鞋」的推送负载与改动前逐字节一致（不回退、不漂移）。
    /// </summary>
    [Fact]
    public async Task ShouldKeepKeywordPathByteIdentical_WhenSemanticIsUnavailableOrIrrelevant()
    {
        // ③ 语义不可用（替身返回空）：逐字节等于改动前基线
        using var unavailable = new Harness(semanticSearch: new StubSemanticSearch([]));
        unavailable.Accessor.SetCurrentUser(TestUser);
        Assert.Equal(BaselineQueryHit, await unavailable.Provider.RecommendProductsAsync(KeywordMessage));
        Assert.Equal(
            BaselineQueryHit,
            Assert.IsType<JsonElement>(await unavailable.Provider.TryBuildPushPayloadAsync(KeywordMessage)).GetRawText());

        // 更强：语义**有**命中也不得抢走关键词路径的内容（否则「我想买跑步鞋」的结果会漂移成 T 恤类商品）
        using var irrelevant = new Harness(
            semanticSearch: new StubSemanticSearch([Hit(2, 0.72, name: "有机棉T恤", category: "服装")]));
        irrelevant.Accessor.SetCurrentUser(TestUser);
        Assert.Equal(BaselineQueryHit, await irrelevant.Provider.RecommendProductsAsync(KeywordMessage));
        Assert.Equal(
            BaselineQueryHit,
            Assert.IsType<JsonElement>(await irrelevant.Provider.TryBuildPushPayloadAsync(KeywordMessage)).GetRawText());
    }

    /// <summary>
    /// B1-bT1（spec R4 场景 1 的降级面）：语义检索抛异常 → 不抛、退化为纯关键词门控、**恰好一条** Warning。
    /// try/catch 只包检索调用本身，故这条 Warning 可归因到语义降级。
    /// </summary>
    [Fact]
    public async Task ShouldNotPushChitChatAndWarnExactlyOnce_WhenSemanticSearchThrows()
    {
        var semantic = new StubSemanticSearch(failure: new InvalidOperationException("B1 用例：语义检索替身故意抛出"));
        using var harness = new Harness(semanticSearch: semantic);
        harness.Accessor.SetCurrentUser(TestUser);

        var sink = new CollectingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        JsonElement? push;
        try
        {
            push = await harness.Provider.TryBuildPushPayloadAsync(ChitChatMessage);
        }
        finally
        {
            Log.Logger = original;
        }

        Assert.Null(push);   // 语义故障后仍是「闲聊不推」，未被异常改变
        var warning = Assert.Single(sink.Events, logEvent => logEvent.Level == LogEventLevel.Warning);
        Assert.Contains("推荐语义检索失败", warning.MessageTemplate.Text, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Equal(new[] { ChitChatMessage }, semantic.Queries);
    }

    /// <summary>
    /// B1-bT1 的另一半（降级不劣化）：语义检索抛异常时，**关键词路径照常推送**且内容逐字节不变。
    /// </summary>
    [Fact]
    public async Task ShouldStillPushKeywordRound_WhenSemanticSearchThrows()
    {
        var semantic = new StubSemanticSearch(failure: new InvalidOperationException("B1 用例：语义检索替身故意抛出"));
        using var harness = new Harness(semanticSearch: semantic);
        harness.Accessor.SetCurrentUser(TestUser);

        var push = await harness.Provider.TryBuildPushPayloadAsync(KeywordMessage);

        Assert.Equal(BaselineQueryHit, Assert.IsType<JsonElement>(push).GetRawText());
    }

    /// <summary>
    /// B1-bT2（⑤）：未注入语义检索（未启用 RAG 的宿主 / 既有直构造点）→ 行为与改动前逐字节一致，
    /// 「T恤有吗」仍推不出来（语义路径是唯一的差异来源）。
    /// </summary>
    [Fact]
    public async Task ShouldBehaveIdentically_WhenSemanticSearchNotInjected()
    {
        using var harness = new Harness();   // 不注册 IProductSemanticSearch → 可选参默认 null
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Equal(BaselineQueryHit, await harness.Provider.RecommendProductsAsync(KeywordMessage));
        Assert.Equal(
            BaselineQueryHit,
            Assert.IsType<JsonElement>(await harness.Provider.TryBuildPushPayloadAsync(KeywordMessage)).GetRawText());
        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync(TShirtMessage));
    }

    // ---------- ⑤ D：否定语境门控（修 L7 误推）----------
    //
    // 要修的 defect（真机实测 L7）：「我最近在健身，不过今天不买」→ 语义/关键词都因「健身」命中
    // 而照推健身类商品。语义检索分不出「我在陈述状态」与「我在找东西」，故在门控上叠加否定语境判据。
    // 同时必须**不误伤**「否定只是排除某一项、仍在找东西」的说法（下表的后两条）。

    /// <summary>「陈述状态 + 明确不买」：本轮不推（D 要修的那条）。</summary>
    private const string DeclineMessage = "我最近在健身，不过今天不买";

    /// <summary>否定只排除某一项、仍在要别的（**误伤防护**，必须照推）。</summary>
    private const string DeclineOneAskOthersMessage = "这个不买，有别的推荐吗";

    /// <summary>同上，换一个说法（**误伤防护**，必须照推）。</summary>
    private const string DeclineRunShoesAskOthersMessage = "不买跑鞋了，有没有别的鞋";

    /// <summary>
    /// 否定语境判据的**纯函数**表：拦得住哪些、哪些**有意**放行（宁可漏拦不要误杀）。
    /// </summary>
    [Theory]
    [InlineData(DeclineMessage, true)]                    // 陈述状态 + 今天不买 → 拦（L7 修复点）
    [InlineData("随便看看，没什么想买的", true)]              // 「只是看看」类 → 拦
    [InlineData("谢谢，不用买了", true)]                     // 「不用买」→ 拦
    [InlineData(DeclineOneAskOthersMessage, false)]       // 排除某一项 + 求新（「别的」）→ 放行（误伤防护）
    [InlineData(DeclineRunShoesAskOthersMessage, false)]  // 排除某一项 + 求新（「有没有」）→ 放行（误伤防护）
    [InlineData("我想买跑步鞋", false)]                     // 正常购物 → 放行（回归）
    [InlineData("你好呀", false)]                          // 闲聊 → 放行（由关键词/语义门控拦，不在本条职责内）
    [InlineData("我要不要买跑鞋", false)]                    // 裸「不要」不收 → 「要不要买」不得被误杀
    [InlineData("今天先不下单", false)]                      // 已知漏拦（线索表未收「不下单」）——有意接受，见 handoff-D
    [InlineData(null, false)]                             // 无消息 = 无否定语境
    [InlineData("", false)]
    public void IsNegatedIntent_ShouldOnlyMatchExplicitDeclineContexts(string? query, bool expected)
        => Assert.Equal(expected, RecommendationToolProvider.IsNegatedIntent(query));

    /// <summary>
    /// 「我最近在健身，不过今天不买」→ **不推**（面板保持上一次）。
    ///
    /// <para><b>正锚点</b>：同一 provider、只删掉否定分句的「我最近在健身」**会推**——证明该轮确有推荐依据
    /// （「健身」是白名单关键词，真机上语义同样高相关），「不推」的唯一原因是叠加的否定语境判据，
    /// 不是「本来就没东西可推」。</para>
    /// </summary>
    [Fact]
    public async Task ShouldNotPushDeclineRound_WhenMessageSaysNotBuyingToday()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        // 正锚点：去掉「不过今天不买」后照推（关键词「健身」命中）——本条即红端「误推」的等价复现
        var withoutDecline = await harness.Provider.TryBuildPushPayloadAsync("我最近在健身");
        Assert.NotNull(withoutDecline);

        // D：加上否定分句 → 拦
        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync(DeclineMessage));
        Assert.True(RecommendationToolProvider.IsNegatedIntent(DeclineMessage));
    }

    /// <summary>
    /// 误伤防护 ①：「这个不买，有别的推荐吗」→ **照推**（用户只是在排除某一项，仍在找东西）。
    ///
    /// <para>语义替身给出一条达阈值命中，使该轮**本来就能推**——若否定判据写成「含『不买』即拦」，
    /// 本条必红（正是「打地鼠」式实现会误杀的正常购物轮）。</para>
    /// </summary>
    [Fact]
    public async Task ShouldStillPush_WhenDeclinesOneItemButAsksForAlternatives()
    {
        var semantic = new StubSemanticSearch([Hit(2, 0.72, name: "有机棉T恤", category: "服装")]);
        using var harness = new Harness(semanticSearch: semantic);
        harness.Accessor.SetCurrentUser(TestUser);

        var push = await harness.Provider.TryBuildPushPayloadAsync(DeclineOneAskOthersMessage);
        var payload = Assert.IsType<JsonElement>(push);

        // 语义检索确实被走到（未被否定判据短路），且内容含命中商品 #2（反推「服装」兜底）
        Assert.Equal(new[] { DeclineOneAskOthersMessage }, semantic.Queries);
        var products = payload.GetProperty("products").EnumerateArray().ToList();
        Assert.Contains(products, product => product.GetProperty("id").GetInt32() == 2);
        Assert.False(RecommendationToolProvider.IsNegatedIntent(DeclineOneAskOthersMessage));
    }

    /// <summary>
    /// 误伤防护 ②：「不买跑鞋了，有没有别的鞋」→ **照推**（关键词路径命中「跑鞋」，不看语义）。
    /// </summary>
    [Fact]
    public async Task ShouldStillPush_WhenDeclinesRunShoesButAsksForAnotherPair()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var push = await harness.Provider.TryBuildPushPayloadAsync(DeclineRunShoesAskOthersMessage);
        var payload = Assert.IsType<JsonElement>(push);

        Assert.True(payload.GetProperty("hasRecommendation").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("products").EnumerateArray());
        Assert.False(RecommendationToolProvider.IsNegatedIntent(DeclineRunShoesAskOthersMessage));
    }

    /// <summary>正常购物意图不受影响（回归）：照推，且商品列表非空。</summary>
    [Fact]
    public async Task ShouldPushPurchaseIntentRound_WhenIntentIsExplicit()
    {
        using var harness = new Harness();
        harness.Accessor.SetCurrentUser(TestUser);

        var push = await harness.Provider.TryBuildPushPayloadAsync(KeywordMessage);
        var payload = Assert.IsType<JsonElement>(push);

        Assert.True(payload.GetProperty("hasRecommendation").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("products").EnumerateArray());
        // 且与工具入口同源同内容（同一 builder 的唯一证据）
        Assert.Equal(
            await harness.Provider.RecommendProductsAsync(KeywordMessage),
            payload.GetRawText());
        Assert.False(RecommendationToolProvider.IsNegatedIntent(KeywordMessage));
    }

    /// <summary>闲聊轮仍不推（回归，spec R2 场景 1）；本条由「关键词/语义皆无」拦，不由否定判据拦。</summary>
    [Fact]
    public async Task ShouldNotPushChatRound_WhenThereIsNoDeclineOrIntent()
    {
        using var harness = new Harness(memories: [Preference("用户喜欢跑步")]);
        harness.Accessor.SetCurrentUser(TestUser);

        Assert.Null(await harness.Provider.TryBuildPushPayloadAsync(ChitChatMessage));
        Assert.False(RecommendationToolProvider.IsNegatedIntent(ChitChatMessage));
    }

    // ---------- 夹具 ----------

    private static Memory Preference(string text)
        => new() { Id = Guid.NewGuid().ToString(), Text = text, UserId = TestUser };

    /// <summary>替身语义检索的一条命中（Score 为相似度，供门控阈值判定）。</summary>
    private static ProductSearchHit Hit(
        int productId,
        double score,
        string name = "语义命中商品",
        string category = "鞋类",
        decimal price = 99.99m)
        => new(productId, name, category, price, score);

    /// <summary>
    /// 替身语义检索（B1 用例专用）：返回脚本化命中或按需抛异常——**不依赖 bge 模型文件**，确定性可进 CI。
    /// 记录检索输入，供断言「检索用的就是本轮依据」。
    /// </summary>
    private sealed class StubSemanticSearch(
        IReadOnlyList<ProductSearchHit>? hits = null,
        Exception? failure = null) : IProductSemanticSearch
    {
        /// <summary>收到的检索输入（按调用序）。</summary>
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<ProductSearchHit>> SearchAsync(
            string query,
            string? domain = null,
            int top = 5,
            string? category = null,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return failure is null
                ? Task.FromResult(hits ?? [])
                : Task.FromException<IReadOnlyList<ProductSearchHit>>(failure);
        }

        public Task EnsureIndexedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>收集 Serilog 日志事件的内存 sink（配合临时替换全局 <c>Log.Logger</c> 捕获告警）。</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static RecommendationToolProvider.RecommendationPayload PayloadWithProducts(
        IReadOnlyList<RecommendationToolProvider.RecommendedProduct>? products = null)
        => new(
            "根据您的兴趣，为您推荐：",
            true,
            ["鞋类"],
            products ?? [new RecommendationToolProvider.RecommendedProduct(3, "专业跑鞋", "鞋类", 129.99m, "👟", "因为你提到「跑步」")]);

    /// <summary>
    /// 测试装配（沿用 RecommendationToolProviderTests 的口径）：替身 <see cref="IProductRepository"/> +
    /// 真实 <see cref="ProductCatalog"/> + 真实 <see cref="RecommendationService"/>，provider 经 DI 解析。
    /// </summary>
    /// <param name="memories">替身记忆存储返回的记忆（偏好关键词来源）。</param>
    /// <param name="products">替身仓储返回的商品目录（null = <see cref="ProductSeedData.Products"/>）。</param>
    /// <param name="semanticSearch">
    /// 替身语义检索；**null 表示不注册**（与 B1 之前 / 未启用 RAG 的宿主同形，可选参解析为 null）。
    /// </param>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;

        public Harness(
            IReadOnlyList<Memory>? memories = null,
            IReadOnlyList<Product>? products = null,
            IProductSemanticSearch? semanticSearch = null)
        {
            var repository = Substitute.For<IProductRepository>();
            repository.GetAll().Returns(products ?? ProductSeedData.Products);

            Store = Substitute.For<IMemoryStore>();
            Store.GetAllAsync(Arg.Any<MemoryFilter?>(), Arg.Any<CancellationToken>())
                .Returns(_ => AsAsyncEnumerable(memories ?? []));

            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddScoped<IProductCatalogService>(_ => new ProductCatalog(repository));
            services.AddScoped<RecommendationService>();
            services.AddSingleton<ICurrentUserAccessor>(Accessor);
            services.AddSingleton(Store);
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
            // 按**接口**静态类型注册（参数类型即 IProductSemanticSearch，故 TService 推断为接口）；
            // 若把它换成具体类型，DI 解析 IProductSemanticSearch 会落空、provider 静默拿到 null。
            if (semanticSearch is not null)
                services.AddSingleton(semanticSearch);
            services.AddSingleton<RecommendationToolProvider>();

            _services = services.BuildServiceProvider();
            Provider = _services.GetRequiredService<RecommendationToolProvider>();
        }

        public ICurrentUserAccessor Accessor { get; } = new CurrentUserAccessor();

        public IMemoryStore Store { get; }

        public RecommendationToolProvider Provider { get; }

        public void Dispose() => _services.Dispose();

        private static async IAsyncEnumerable<Memory> AsAsyncEnumerable(IEnumerable<Memory> memories)
        {
            foreach (var memory in memories)
            {
                await Task.Yield();
                yield return memory;
            }
        }
    }
}
