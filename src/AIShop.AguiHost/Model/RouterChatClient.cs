using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.AguiHost.Model;

/// <summary>
/// 逐轮选模型的 delegating <see cref="IChatClient"/>（agui-model-switch C5 M2）。
/// 直接实现 <see cref="IChatClient"/>（而非 <see cref="DelegatingChatClient"/> 子类）：委托目标是每轮动态解析的、
/// 无固定 inner（塞 dummy inner 无意义）。持 <see cref="IActiveModelProvider"/> + <see cref="IModelChatClientFactory"/>，
/// 把 <see cref="GetResponseAsync"/> / <see cref="GetStreamingResponseAsync"/> / <see cref="GetService"/> 原样转发到
/// 「本轮激活模型」的底层 chatClient，请求参数透传、不包任何 Agent 层逻辑（spec ADDED Requirement 6）。
/// </summary>
/// <remarks>
/// 装配位置（spec Req6/Req7）：位于 agent 专属回复清洗链（<c>ReplySanitizingChatClient</c>，C3）之下——
/// ChatClientAgent → FICC（T14 工具护栏）→ ReplySanitizingChatClient → RouterChatClient → 每模型底层。
/// 工具/压缩/记忆 Provider/护栏/会话 store 在 RouterChatClient 之上或 agent 层，与选模型无关。
///
/// 决策规则（design §5.3）：<c>provider.ActiveModel</c> 命中工厂已知模型 → <see cref="IModelChatClientFactory.GetClient"/>
/// （该模型底层）；无 model / 未知 model → <see cref="IModelChatClientFactory.GetDefaultClient"/>（ActiveModel 底层），
/// 未知 model 时记录 Warning 但不阻断请求。非流式与流式按同一决策转发。
///
/// 本类型不感知 username/会话/清洗——只负责「把本轮对话交给哪个模型」。
/// </remarks>
internal sealed class RouterChatClient : IChatClient
{
    private readonly IActiveModelProvider _activeModelProvider;
    private readonly IModelChatClientFactory _modelChatClientFactory;

    public RouterChatClient(IActiveModelProvider activeModelProvider, IModelChatClientFactory modelChatClientFactory)
    {
        _activeModelProvider = activeModelProvider;
        _modelChatClientFactory = modelChatClientFactory;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => ResolveClient().GetResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => ResolveClient().GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => ResolveClient().GetService(serviceType, serviceKey);

    /// <summary>
    /// Dispose 为 no-op：不释放工厂缓存底层 chatClient——委托目标从 <see cref="IModelChatClientFactory"/> 懒建缓存
    /// 解析（单例共享），Dispose 会破坏跨轮复用（design §5.3 注释；底层客户端随宿主生命周期由工厂统一持有）。
    /// </summary>
    public void Dispose()
    {
        // 有意 no-op：RouterChatClient 不是底层的所有者（工厂单例缓存共享），不 Dispose 委托目标。
    }

    /// <summary>
    /// 本轮委托目标（唯一决策点）：requested = <see cref="IActiveModelProvider.ActiveModel"/>；命中工厂已知模型 →
    /// 该模型底层；否则（无 model / 未知 model）→ 工厂默认（ActiveModel）底层。
    /// </summary>
    private IChatClient ResolveClient()
    {
        var requested = _activeModelProvider.ActiveModel;
        var resolved = ResolveRequestedModel(requested, _modelChatClientFactory);

        if (resolved is not null)
            return _modelChatClientFactory.GetClient(resolved);

        // requested 非 null 但工厂未配置该模型 → 未知 model：记录 Warning 后回退 ActiveModel 缺省，不阻断请求（spec Req6）
        if (requested is not null)
        {
            Log.Warning(
                "请求模型 '{RequestedModel}' 未配置，回退到 ActiveModel 缺省 '{DefaultModelId}'",
                requested, _modelChatClientFactory.DefaultModelId);
        }

        return _modelChatClientFactory.GetDefaultClient();
    }

    /// <summary>
    /// 决策点纯函数（可单测，design §5.3）：<paramref name="requested"/> 命中工厂已知模型 → 返回该模型 id；
    /// 否则（null / 未知 model）→ 返回 null（＝走 ActiveModel 缺省，由 <see cref="ResolveClient"/> 读取侧解析）。
    /// 未知 model（requested 非 null 但工厂 <see cref="IModelChatClientFactory.ContainsModel"/> 为 false）同样返回
    /// null——由调用方据此记录 Warning 后回退默认，不阻断。
    /// </summary>
    internal static string? ResolveRequestedModel(string? requested, IModelChatClientFactory factory)
        => requested is not null && factory.ContainsModel(requested) ? requested : null;
}
