using System.Text;
using AIShop.AgentTelemetry;
using AIShop.AguiHost.Model;
using AIShop.Service.Agui;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C5 M1 单元测试：<see cref="AguiModelClientFactory"/>（agui-model-switch）读 Models 节 + ActiveModel 缺省、
/// 每模型懒建缓存（不重复构建 / 大小写不敏感 / 未知抛错）、每客户端统一外包 OTel。
/// 对应 spec ADDED Requirement 1/2/3（DefaultModelId=ActiveModel / 无节抛错 / 缓存复用 / KeyNotFound / OTel）。
/// 配置项 Key 一律给非空 "test-key"（<c>ApiKeyCredential("")</c> 抛 ArgumentException，T3 learnings 先例）；
/// 构建底层客户端不联网（OpenAIClient 构造为离线对象），缓存实例仅供断言类型/元数据。
/// </summary>
public sealed class AguiModelClientFactoryTests
{
    private const string ModelsBlock = """
        "qwen": { "Endpoint": "https://example.com/v1", "Key": "test-key", "Model": "qwen3-test", "Name": "Qwen" },
        "deepseek": { "Endpoint": "https://api.deepseek.com/v1", "Key": "test-key", "Model": "deepseek-test", "Name": "DeepSeek" },
        "gpt-4.1": { "Endpoint": "https://mimo.example.com/v1", "Key": "test-key", "Model": "mimo-v2", "Name": "Mimo" }
        """;

    /// <summary>含 ActiveModel=qwen 的最小配置（ActiveModel=qwen → DefaultModelId=qwen）。</summary>
    private static string ActiveModelJson => $$"""
        { "Models": { {{ModelsBlock}} }, "ActiveModel": "qwen" }
        """;

    /// <summary>缺 ActiveModel 的最小配置（DefaultModelId 回退 Models 节首个 <see cref="Microsoft.Extensions.Configuration.ConfigurationSection.GetChildren"/> 子键）。</summary>
    private static string NoActiveModelJson => $$"""
        { "Models": { {{ModelsBlock}} } }
        """;

    private static IConfiguration BuildConfig(string json)
        => new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

    private static AguiModelClientFactory CreateFactory(string json)
        => new(BuildConfig(json), new AgentTelemetryOptions { SourceName = "agui-model-switch-tests" });

    [Fact]
    public void DefaultModelId_WhenActiveModelConfigured_ReturnsActiveModel()
    {
        // ActiveModel=qwen → DefaultModelId 取 ActiveModel（对应 spec Req1）
        var factory = CreateFactory(ActiveModelJson);

        Assert.Equal("qwen", factory.DefaultModelId);
        Assert.True(factory.ContainsModel("qwen"));
    }

    [Fact]
    public void DefaultModelId_WhenActiveModelMissing_FallsBackToFirstModelsKey()
    {
        // ActiveModel 缺失 → DefaultModelId 取 Models 节首个 GetChildren 子键。Configuration 对子键做序数去重聚合，
        // GetChildren 按序数升序返回（[deepseek, gpt-4.1, qwen]）→ 首个 = "deepseek"（与老 ModelRouter 读同一
        // GetChildren 语义一致；对应 spec Req1「DefaultModelId 取 Models 首键」）
        var factory = CreateFactory(NoActiveModelJson);

        Assert.Equal("deepseek", factory.DefaultModelId);
        Assert.True(factory.ContainsModel(factory.DefaultModelId));
    }

    [Fact]
    public void Ctor_WhenNoModelsSection_ThrowsInvalidOperationException()
    {
        // 空配置（无 Models 节）→ 构造抛 InvalidOperationException（对应 spec Req1「无 Models 节抛错」）
        var empty = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => new AguiModelClientFactory(empty, new AgentTelemetryOptions()));
    }

    [Fact]
    public void Ctor_WhenModelsSectionEmpty_ThrowsInvalidOperationException()
    {
        // Models 节存在但为空对象 → 无可用模型，同样抛 InvalidOperationException（对应 spec Req1「空节抛错」）
        var emptyModels = BuildConfig("""{ "Models": {} }""");

        Assert.Throws<InvalidOperationException>(() => new AguiModelClientFactory(emptyModels, new AgentTelemetryOptions()));
    }

    [Fact]
    public void GetClient_SameModelTwice_ReturnsSameCachedInstance()
    {
        // 同一 modelId 两次 GetClient → 返回同一缓存实例（懒建缓存不重复构建，对应 spec Req2）
        var factory = CreateFactory(ActiveModelJson);

        var first = factory.GetClient("deepseek");
        var second = factory.GetClient("deepseek");

        Assert.Same(first, second);
    }

    [Fact]
    public void GetClient_ModelIdCaseInsensitive_ReturnsSameCachedInstance()
    {
        // 模型 id 大小写不敏感："QWEN" 命中 "qwen" 缓存实例（OrdinalIgnoreCase，对应 spec Req2）
        var factory = CreateFactory(ActiveModelJson);

        var lower = factory.GetClient("qwen");
        var upper = factory.GetClient("QWEN");

        Assert.Same(lower, upper);
    }

    [Fact]
    public void GetClient_DifferentModels_ReturnDistinctInstances()
    {
        // 不同模型各自懒建独立底层客户端（qwen/deepseek 不共享缓存项，对应 spec Req2「每模型各自懒建一次」）
        var factory = CreateFactory(ActiveModelJson);

        var qwen = factory.GetClient("qwen");
        var deepseek = factory.GetClient("deepseek");

        Assert.NotSame(qwen, deepseek);
    }

    [Fact]
    public void GetClient_WhenModelUnknown_ThrowsKeyNotFoundException()
    {
        // 未知模型 id → KeyNotFoundException（对应 spec Req2「未知模型 id 抛 KeyNotFoundException」）
        var factory = CreateFactory(ActiveModelJson);

        Assert.Throws<KeyNotFoundException>(() => factory.GetClient("unknown"));
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("deepseek")]
    public void GetClient_EveryModel_IsWrappedWithOpenTelemetryAndExposesMetadata(string modelId)
    {
        // 每个模型（含非默认 deepseek）底层最外层都是 OpenTelemetryChatClient（OTel 外包移入工厂每客户端），
        // 且沿链可解析 ChatClientMetadata（对应 spec Req3「Router 切到任一模型 gen_ai 链路都可见」）
        var factory = CreateFactory(ActiveModelJson);

        var client = factory.GetClient(modelId);

        Assert.Contains("OpenTelemetryChatClient", client.GetType().Name);
        Assert.NotNull(client.GetService(typeof(ChatClientMetadata)));
    }

    [Fact]
    public void GetDefaultClient_ReturnsActiveModelCachedClient()
    {
        // GetDefaultClient() = GetClient(DefaultModelId)：与 Router 缺省走同一工厂缓存实例（design §5.2）
        var factory = CreateFactory(ActiveModelJson);

        Assert.Same(factory.GetDefaultClient(), factory.GetClient("qwen"));
    }
}
