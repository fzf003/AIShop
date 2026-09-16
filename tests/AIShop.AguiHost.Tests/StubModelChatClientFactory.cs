using AIShop.AguiHost.Model;
using AIShop.Service.Agui;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C5 M4 测试共享 stub：<see cref="IModelChatClientFactory"/> 的离线实现——所有 modelId 都返回同一脚本化
/// chatClient（spec Req11：agent 聊天底层经 RouterChatClient → 工厂，离线 override 点从全局 <see cref="Meai.IChatClient"/>
/// 迁到本接口；WAF 用 <c>RemoveAll&lt;IModelChatClientFactory&gt; + AddSingleton(stub)</c> 替换后，Router 切任一模型
/// 与全局纯净 <c>IChatClient</c> seam（= 工厂 <see cref="GetDefaultClient"/>）都落到该脚本化 mock，免真实 LLM）。
/// 记录 <see cref="GetClient"/> 收到的 modelId 序列与 <see cref="GetDefaultClient"/> 调用次数，供请求级 model 切换
/// 断言（spec 验收标准 2：请求 <c>forwardedProps.model=deepseek</c> → Router 委托 <c>GetClient("deepseek")</c>，只可能
/// 经 RouterChatClient 到达本 stub = Router 已入 agent 链的装配证明）。
/// </summary>
internal sealed class StubModelChatClientFactory : IModelChatClientFactory
{
    private readonly Meai.IChatClient _client;

    public StubModelChatClientFactory(Meai.IChatClient client, string defaultModelId = "qwen",
        IReadOnlyList<ModelDescriptor>? availableModels = null)
    {
        _client = client;
        DefaultModelId = defaultModelId;
        AvailableModels = availableModels ?? [];
    }

    /// <inheritdoc />
    public string DefaultModelId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// agui-client-support T1：本 stub 供离线 WAF 驱动聊天链路，模型清单非其关注点 → 缺省空列表
    /// （<c>ModelDescriptor</c> 的清单断言走真实工厂 <c>AguiModelClientFactoryTests</c>，不在此处造假数据）。
    /// </remarks>
    public IReadOnlyList<ModelDescriptor> AvailableModels { get; }

    /// <summary><see cref="GetClient"/> 收到的 modelId 调用序列（按调用顺序记录）。</summary>
    public List<string> RequestedModelIds { get; } = [];

    /// <summary><see cref="GetDefaultClient"/> 被调用次数。</summary>
    public int GetDefaultClientCalls { get; private set; }

    /// <inheritdoc />
    public bool ContainsModel(string modelId) => true;

    /// <inheritdoc />
    public Meai.IChatClient GetClient(string modelId)
    {
        RequestedModelIds.Add(modelId);
        return _client;
    }

    /// <inheritdoc />
    public Meai.IChatClient GetDefaultClient()
    {
        GetDefaultClientCalls++;
        return _client;
    }
}
