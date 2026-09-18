using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AIShop.AguiHost.Recommendation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-reco-realtime S3 —— 推荐标记内容与 <c>MapContent</c> → <c>CustomEvent</c> 映射的单元验收。
///
/// <para>本套用例只对我们<b>自己的判定逻辑</b>负责：内容形状、映射返回值与注册链路存在性。
/// 「注册的 mapper 是否真被 AG-UI 转换器调用」由 S1 探针（<see cref="AguiCustomEventProbeTests"/>）证明、
/// 由 S6 端到端复核，此处不作主证据。</para>
///
/// <para><b>不启动宿主</b>：注册链路用例用裸 <see cref="ServiceCollection"/>（<c>AddAGUIServer()</c> +
/// 被测扩展），故不需要 WAF、不需要串行集合。</para>
/// </summary>
public sealed class RecommendationStreamOptionsTests
{
    /// <summary>推荐负载样本（与 <c>recommend_products</c> 工具结果同构的 camelCase 对象）。</summary>
    private const string FirstReason = "因为你提到「跑步」";

    /// <summary>第二条推荐的理由（含偏好分支文案，用于逐条断言）。</summary>
    private const string SecondReason = "根据你的偏好「袜子」";

    /// <summary>
    /// 正例（S3 清单第 1 条，对应 R1-1）：合法推荐 JSON → mapper 返回<b>恰 1 条</b> <see cref="CustomEvent"/>，
    /// <c>name</c> 为 <c>recommendation</c>，<c>value</c> 与 payload 逐字段相等（含 <c>products</c> 长度与各条 <c>reason</c>）。
    /// </summary>
    [Fact]
    public void MapContent_WhenRecommendationPushContent_ReturnsSingleRecommendationEvent()
    {
        var payload = SamplePayload();

        var events = AguiRecommendationStreamOptions.MapContent(new RecommendationPushContent(payload));

        Assert.NotNull(events);
        var single = Assert.Single(events);
        var customEvent = Assert.IsType<CustomEvent>(single);
        Assert.Equal("recommendation", customEvent.Name);
        Assert.Equal(RecommendationPushContent.EventName, customEvent.Name);

        // value 与 payload 整体逐字节相等（同一 JsonElement，无改写）
        Assert.True(customEvent.Value.HasValue, "CUSTOM 事件必须携带 value（前端按 value 解析推荐负载）");
        var value = customEvent.Value.GetValueOrDefault();
        Assert.Equal(payload.GetRawText(), value.GetRawText());

        // 逐字段相等：标量字段、products 数组长度、各条 reason
        Assert.Equal("根据您的对话，为您推荐：", value.GetProperty("message").GetString());
        Assert.True(value.GetProperty("hasRecommendation").GetBoolean());
        Assert.Equal(1, value.GetProperty("categories").GetArrayLength());
        Assert.Equal("运动", value.GetProperty("categories")[0].GetString());

        var products = value.GetProperty("products");
        Assert.Equal(2, products.GetArrayLength());
        Assert.Equal(3, products[0].GetProperty("id").GetInt32());
        Assert.Equal("专业跑鞋", products[0].GetProperty("name").GetString());
        Assert.Equal(FirstReason, products[0].GetProperty("reason").GetString());
        Assert.Equal(4, products[1].GetProperty("id").GetInt32());
        Assert.Equal(SecondReason, products[1].GetProperty("reason").GetString());
    }

    /// <summary>
    /// 非标记内容（S3 清单第 2 条）：<see cref="TextContent"/> / <see cref="FunctionCallContent"/> /
    /// <see cref="FunctionResultContent"/> 一律返回 <c>null</c> —— 不伪造事件，避免推荐负载被塞进别人的位置。
    ///
    /// <para>同用例内的<b>正向锚点</b>（防断言空转）：同一判定的正例确实产出事件，故此处三个 <c>null</c>
    /// 是「判定分叉」而非「mapper 恒 null」。</para>
    /// </summary>
    [Fact]
    public void MapContent_WhenOtherContent_ReturnsNull()
    {
        // 正向锚点：同一次调用序列里，标记内容确实被映射成事件
        var positive = AguiRecommendationStreamOptions.MapContent(new RecommendationPushContent(SamplePayload()));
        Assert.NotNull(positive);
        Assert.Single(positive);

        Assert.Null(AguiRecommendationStreamOptions.MapContent(new TextContent("你好")));
        Assert.Null(AguiRecommendationStreamOptions.MapContent(
            new FunctionCallContent("call-1", "recommend_products", new Dictionary<string, object?> { ["query"] = "跑步鞋" })));
        Assert.Null(AguiRecommendationStreamOptions.MapContent(new FunctionResultContent("call-1", "{}")));
    }

    /// <summary>
    /// 注册可解析（S3 清单第 3 条）：裸 <see cref="ServiceCollection"/> 调 <c>AddAGUIServer()</c> + 被测扩展后，
    /// <see cref="AGUIStreamOptions"/> 可解析（注册链路存在性）。
    /// </summary>
    [Fact]
    public void AddAguiRecommendationStreamOptions_RegistersResolvableStreamOptions()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<AGUIStreamOptions>>().Value;

        Assert.NotNull(options);
    }

    /// <summary>
    /// 多态派生类型注册（design §4.4 D 第 4 条，S3 清单外必补的一步）：注册后
    /// <see cref="AIContent"/> 的 JSON 多态派生类型表<b>包含</b> <see cref="RecommendationPushContent"/>。
    ///
    /// <para>观测方式 = 读宿主 HTTP JSON 选项的 <c>AIContent</c> 类型信息（正是 AG-UI 转换器做原始快照序列化时
    /// 走的那条解析路径）。正向锚点：该多态表确实存在且含内置派生类型 <see cref="TextContent"/>，
    /// 故「包含」断言不是落在空表上的恒真断言。</para>
    /// </summary>
    [Fact]
    public void AddAguiRecommendationStreamOptions_RegistersPushContentAsJsonDerivedType()
    {
        using var provider = BuildProvider();

        var derivedTypes = AIContentDerivedTypes(provider);

        Assert.Contains(typeof(TextContent), derivedTypes);
        Assert.Contains(typeof(RecommendationPushContent), derivedTypes);
    }

    /// <summary>
    /// 负锚点（配合上一条用例）：<b>不</b>调用被测扩展时，同一个多态表里没有
    /// <see cref="RecommendationPushContent"/> —— 证明上一条的「包含」确实由该注册带来，而非别处（或恒真）。
    /// </summary>
    [Fact]
    public void WithoutRegistration_AIContentPolymorphismHasNoPushContent()
    {
        var services = new ServiceCollection();
        // AddOptions 先注册选项基础设施（AguiHost 生产环境由 WebApplicationBuilder 提供；
        // AddAGUIServer 自身只追加 IConfigureOptions<JsonOptions>，不注册 IOptions<>）
        services.AddOptions();
        services.AddAGUIServer();
        using var provider = services.BuildServiceProvider();

        var derivedTypes = AIContentDerivedTypes(provider);

        // 正锚点：多态表本身可用（含内置派生类型），否则 DoesNotContain 是空转
        Assert.Contains(typeof(TextContent), derivedTypes);
        Assert.DoesNotContain(typeof(RecommendationPushContent), derivedTypes);
    }

    /// <summary>裸服务集合装配：AG-UI 服务（生产 Program 同款）+ 被测扩展（两者顺序与生产一致）。</summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAGUIServer();
        services.AddAguiRecommendationStreamOptions();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 读宿主 HTTP JSON 选项里 <see cref="AIContent"/> 的多态派生类型清单
    /// （转换器快照序列化所走的同一条解析路径）。
    /// </summary>
    private static IReadOnlyList<Type> AIContentDerivedTypes(IServiceProvider provider)
    {
        var jsonOptions = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value;
        var typeInfo = jsonOptions.SerializerOptions.GetTypeInfo(typeof(AIContent));
        var polymorphism = typeInfo.PolymorphismOptions;

        Assert.NotNull(polymorphism);
        return [.. polymorphism.DerivedTypes.Select(derived => derived.DerivedType)];
    }

    /// <summary>构造与 <c>RecommendationToolProvider</c> 产出同构的推荐负载样本（camelCase 对象）。</summary>
    private static JsonElement SamplePayload() => JsonSerializer.SerializeToElement(new
    {
        message = "根据您的对话，为您推荐：",
        hasRecommendation = true,
        categories = new[] { "运动" },
        products = new[]
        {
            new
            {
                id = 3,
                name = "专业跑鞋",
                category = "运动",
                price = 129.99m,
                emoji = "👟",
                reason = FirstReason,
            },
            new
            {
                id = 4,
                name = "速干跑步袜",
                category = "运动",
                price = 19.9m,
                emoji = "🧦",
                reason = SecondReason,
            },
        },
    });
}
