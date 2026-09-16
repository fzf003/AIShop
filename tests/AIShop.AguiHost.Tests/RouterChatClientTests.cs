using AIShop.AguiHost.Model;
using AIShop.Service.Agui;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C5 M2 单元测试：<see cref="RouterChatClient"/>（agui-model-switch）逐轮选模型 delegating IChatClient。
/// 对应 spec ADDED Requirement 6「RouterChatClient 按本轮激活模型委托底层 chatClient」：
/// provider.ActiveModel 命中工厂已知模型 → GetClient(modelId)；无 model / 未知 model → GetDefaultClient()（ActiveModel，
/// 未知记录 Warning 不阻断）；非流式与流式按同一决策转发、请求参数原样透传、GetService 管线自省转发。
/// 内部接口用自写 stub（不走 NSubstitute，避免 internal 代理的 DynamicProxyGenAssembly2 可见性负担）。
/// </summary>
public sealed class RouterChatClientTests
{
    /// <summary>可写 ActiveModel 的 <see cref="IActiveModelProvider"/> stub（逐轮切换模型值用）。</summary>
    private sealed class StubActiveModelProvider : IActiveModelProvider
    {
        public string? ActiveModel { get; set; }
        public void SetActiveModel(string? modelId) => ActiveModel = modelId;
    }

    /// <summary>
    /// 记录调用点的 <see cref="IModelChatClientFactory"/> stub：GetClient(modelId) 记录入参并返回对应底层、
    /// GetDefaultClient() 计数并返回缺省底层。
    /// </summary>
    private sealed class RecordingModelChatClientFactory : IModelChatClientFactory
    {
        private readonly Dictionary<string, Meai.IChatClient> _clients;

        public RecordingModelChatClientFactory(string defaultModelId, Meai.IChatClient defaultClient,
            params (string ModelId, Meai.IChatClient Client)[] clients)
        {
            DefaultModelId = defaultModelId;
            DefaultClient = defaultClient;
            _clients = clients.ToDictionary(
                c => c.ModelId, c => c.Client, StringComparer.OrdinalIgnoreCase);
        }

        public string DefaultModelId { get; }

        /// <inheritdoc />
        /// <remarks>
        /// agui-client-support T1：本 stub 只服务 Router 选模型断言，模型清单非其关注点 → 恒空列表
        /// （接口新增成员的手写实现同步，<c>Array.Empty</c> 语义即「本替身不提供清单」）。
        /// </remarks>
        public IReadOnlyList<ModelDescriptor> AvailableModels => [];

        public Meai.IChatClient DefaultClient { get; }

        /// <summary>GetClient 收到的 modelId 调用序列（按调用顺序记录）。</summary>
        public List<string> RequestedModelIds { get; } = [];

        /// <summary>GetDefaultClient 被调用次数。</summary>
        public int DefaultClientCalls { get; private set; }

        public bool ContainsModel(string modelId) => _clients.ContainsKey(modelId);

        public Meai.IChatClient GetClient(string modelId)
        {
            RequestedModelIds.Add(modelId);
            return _clients[modelId];
        }

        public Meai.IChatClient GetDefaultClient()
        {
            DefaultClientCalls++;
            return DefaultClient;
        }
    }

    /// <summary>脚本化非流式回复 mock：GetResponseAsync 返回含指定文本的 assistant 消息。</summary>
    private static Meai.IChatClient MockResponseClient(string replyText)
    {
        var client = Substitute.For<Meai.IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, replyText)));
        return client;
    }

    /// <summary>脚本化流式回复 mock：GetStreamingResponseAsync 依次 yield 各文本增量。</summary>
    private static Meai.IChatClient MockStreamingClient(params string[] chunks)
    {
        var client = Substitute.For<Meai.IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamAsync(chunks));
        return client;
    }

    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamAsync(string[] chunks)
    {
        foreach (var chunk in chunks)
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, chunk);
    }

    [Fact]
    public async Task GetResponseAsync_ProviderNull_DelegatesToDefaultClient_AndForwardsRequest()
    {
        // 无激活 model（null）→ 委托工厂缺省底层（ActiveModel）；请求参数原样透传（对应 spec Req6「无 model → 回退默认」）
        var defaultClient = MockResponseClient("default-reply");
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient);
        var provider = new StubActiveModelProvider { ActiveModel = null };
        var router = new RouterChatClient(provider, factory);

        var messages = new[] { new Meai.ChatMessage(Meai.ChatRole.User, "你好") };
        var options = new Meai.ChatOptions { ModelId = "qwen" };
        var response = await router.GetResponseAsync(messages, options);

        Assert.Same(defaultClient, factory.DefaultClient);
        Assert.Equal(1, factory.DefaultClientCalls);
        Assert.Empty(factory.RequestedModelIds);
        await defaultClient.Received(1)
            .GetResponseAsync(Arg.Is<IEnumerable<Meai.ChatMessage>>(m => ReferenceEquals(m, messages)),
                Arg.Is<Meai.ChatOptions?>(o => ReferenceEquals(o, options)),
                Arg.Any<CancellationToken>());
        Assert.Equal("default-reply", Assert.Single(response.Messages).Text);
    }

    [Fact]
    public async Task GetResponseAsync_ActiveModelKnown_DelegatesToGetClient_WithThatModel()
    {
        // provider.ActiveModel = "deepseek" 且工厂 ContainsModel → 委托 GetClient("deepseek")（对应 spec Req6「命中工厂已知模型」）
        var defaultClient = MockResponseClient("default-reply");
        var deepseekClient = MockResponseClient("deepseek-reply");
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient, ("deepseek", deepseekClient));
        var provider = new StubActiveModelProvider { ActiveModel = "deepseek" };
        var router = new RouterChatClient(provider, factory);

        var response = await router.GetResponseAsync([new Meai.ChatMessage(Meai.ChatRole.User, "hi")]);

        Assert.Equal(["deepseek"], factory.RequestedModelIds);
        Assert.Equal(0, factory.DefaultClientCalls);
        Assert.Equal("deepseek-reply", Assert.Single(response.Messages).Text);
    }

    [Fact]
    public async Task GetResponseAsync_ActiveModelUnknown_FallsBackToDefault_WithoutThrowing()
    {
        // provider.ActiveModel = "unknown"（工厂不含该模型）→ 回退 GetDefaultClient() 且不抛（对应 spec Req6「未知 model 回退默认并记录 Warning 不阻断」）
        var defaultClient = MockResponseClient("default-reply");
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient);
        var provider = new StubActiveModelProvider { ActiveModel = "unknown" };
        var router = new RouterChatClient(provider, factory);

        var response = await router.GetResponseAsync([new Meai.ChatMessage(Meai.ChatRole.User, "hi")]);

        Assert.Equal(1, factory.DefaultClientCalls);
        Assert.Empty(factory.RequestedModelIds);
        Assert.Equal("default-reply", Assert.Single(response.Messages).Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ActiveModelKnown_ForwardsToSameResolvedClient()
    {
        // 流式按同一决策转发：provider=deepseek → deepseek 底层收到流请求并产出文本（对应 spec Req6「流式按同一决策转发」）
        var deepseekClient = MockStreamingClient("deepseek-part1", "deepseek-part2");
        var defaultClient = MockStreamingClient("default-part");
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient, ("deepseek", deepseekClient));
        var provider = new StubActiveModelProvider { ActiveModel = "deepseek" };
        var router = new RouterChatClient(provider, factory);

        var messages = new[] { new Meai.ChatMessage(Meai.ChatRole.User, "hi") };
        var collected = new List<string>();
        await foreach (var update in router.GetStreamingResponseAsync(messages))
        {
            if (!string.IsNullOrEmpty(update.Text))
                collected.Add(update.Text);
        }

        Assert.Equal(["deepseek"], factory.RequestedModelIds);
        Assert.Equal(0, factory.DefaultClientCalls);
        Assert.Equal("deepseek-part1deepseek-part2", string.Concat(collected));
    }

    [Fact]
    public async Task MultipleCalls_EachRoundDelegatesByActiveModelValue_NoCrossRoundLeak()
    {
        // 同一 Router 实例多次独立调用、每轮 provider 值不同（deepseek → null → qwen）：各自委托对应底层，
        // 不跨调用缓存上一次的委托目标（对应 spec Req4/Req6「每轮独立、缺省回 ActiveModel」）
        var deepseekClient = MockResponseClient("deepseek-reply");
        var qwenClient = MockResponseClient("qwen-reply");
        var defaultClient = MockResponseClient("default-reply");
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient,
            ("deepseek", deepseekClient), ("qwen", qwenClient));
        var provider = new StubActiveModelProvider();
        var router = new RouterChatClient(provider, factory);

        provider.ActiveModel = "deepseek";
        var r1 = await router.GetResponseAsync([new Meai.ChatMessage(Meai.ChatRole.User, "m1")]);
        provider.ActiveModel = null;
        var r2 = await router.GetResponseAsync([new Meai.ChatMessage(Meai.ChatRole.User, "m2")]);
        provider.ActiveModel = "qwen";
        var r3 = await router.GetResponseAsync([new Meai.ChatMessage(Meai.ChatRole.User, "m3")]);

        Assert.Equal(["deepseek", "qwen"], factory.RequestedModelIds);
        Assert.Equal(1, factory.DefaultClientCalls);
        Assert.Equal("deepseek-reply", Assert.Single(r1.Messages).Text);
        Assert.Equal("default-reply", Assert.Single(r2.Messages).Text);
        Assert.Equal("qwen-reply", Assert.Single(r3.Messages).Text);
    }

    [Fact]
    public void GetService_ForwardsToResolvedClient()
    {
        // GetService 管线自省转发到当轮目标底层（对应 spec Req6 关联的管线自省面：ChatClientMetadata 命中当轮底层）
        var deepseekClient = Substitute.For<Meai.IChatClient>();
        var metadata = new Meai.ChatClientMetadata(providerName: "DeepSeek");
        deepseekClient.GetService(typeof(Meai.ChatClientMetadata), Arg.Any<object?>())
            .Returns(metadata);

        var defaultClient = Substitute.For<Meai.IChatClient>();
        var factory = new RecordingModelChatClientFactory("qwen", defaultClient, ("deepseek", deepseekClient));
        var provider = new StubActiveModelProvider { ActiveModel = "deepseek" };
        var router = new RouterChatClient(provider, factory);

        var result = router.GetService(typeof(Meai.ChatClientMetadata));

        Assert.Same(metadata, result);
        Assert.Equal(["deepseek"], factory.RequestedModelIds);
        deepseekClient.Received(1).GetService(typeof(Meai.ChatClientMetadata), null);
    }

    [Fact]
    public void ResolveRequestedModel_PureDecision()
    {
        // 决策点纯函数：已知模型（大小写不敏感）→ 返回 modelId；null / 未知 → null（走缺省，由 ResolveClient 读取侧解析）
        var factory = new RecordingModelChatClientFactory("qwen", Substitute.For<Meai.IChatClient>(),
            ("qwen", Substitute.For<Meai.IChatClient>()), ("deepseek", Substitute.For<Meai.IChatClient>()));

        Assert.Equal("deepseek", RouterChatClient.ResolveRequestedModel("deepseek", factory));
        Assert.Equal("QWEN", RouterChatClient.ResolveRequestedModel("QWEN", factory));
        Assert.Null(RouterChatClient.ResolveRequestedModel(null, factory));
        Assert.Null(RouterChatClient.ResolveRequestedModel("unknown", factory));
    }
}
