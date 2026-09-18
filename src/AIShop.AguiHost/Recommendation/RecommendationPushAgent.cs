using System.Runtime.CompilerServices;
using System.Text.Json;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Serilog;

namespace AIShop.AguiHost.Recommendation;

/// <summary>
/// 推荐实时推送装饰器（agui-reco-realtime S4，design §4.1 / §4.4 B）：把 <see cref="AIAgent"/> 包一层，
/// 在**内层流式响应结束后**按门控合成一条携带 <see cref="RecommendationPushContent"/> 的更新，
/// 由 <see cref="AguiRecommendationStreamOptions.MapContent"/> 变成
/// <c>{ "type": "CUSTOM", "name": "recommendation", "value": &lt;推荐 JSON 对象&gt; }</c> 事件。
///
/// <para><b>只 override <see cref="RunCoreStreamingAsync"/></b>：<c>Name</c> / <c>GetService</c> /
/// 会话读写（<c>CreateSessionAsync</c> / <c>SerializeSessionAsync</c> / <c>DeserializeSessionAsync</c>）
/// 全部由 <see cref="DelegatingAIAgent"/> 基类原样转发（<b>不得</b> override）——否则
/// <c>MapAGUIServer</c> 的 keyed 会话 store 解析、<c>GetService(typeof(ChatOptions))</c> 工具集断言、
/// 会话持久化链路都会断。</para>
///
/// <para><b>为什么必须挂在 <c>OpenTelemetryAgent</c> 之外（最外层）</b>：<c>OpenTelemetryAgent</c>
/// 不透传更新对象，而是把内层 agent 当 <c>IChatClient</c> 走一遍 MEAI <c>OpenTelemetryChatClient</c>
/// （design §4.1 末，S1 实测踩过）。合成更新若在内层，就要穿越「MEAI 遥测客户端的序列化/还原 +
/// <c>RawRepresentation as AgentResponseUpdate</c> 还原」两处，自定义 <see cref="AIContent"/> 有被拒绝
/// 或还原时丢弃的风险。挂在最外层后，合成内容<b>从不进入</b> MEAI/OTel 序列化路径（零风险），
/// OTel 侧行为逐字节不变。故装配点只能是宿主 keyed factory（S5），<b>不得</b>塞进
/// <c>AGUIShoppingAgent.Create</c>。</para>
///
/// <para><b>为什么不进消息 / 不进历史 / 不回喂模型</b>：合成更新只存在于本装饰器返回的流里——
/// 它在工具循环（FICC，位于 <c>ChatClientAgent</c> <b>之下</b>）之外，模型看不到；它在
/// <c>ChatClientAgent</c> 的消息装配之外，因此不会成为 assistant 消息、不进
/// <c>SqlChatHistoryProvider</c> / 会话快照（spec R4 场景 3）。</para>
///
/// <para><b>为什么非流式 <see cref="RunCoreAsync"/> 不注入</b>：<c>CUSTOM</c> 事件只在 AG-UI 流式
/// 通道上有意义（<c>MapAGUIServer</c> 只走流式），非流式路径没有承载它的缝；本期不注入，
/// 基类的转发行为保持不变（design §9 第 5 条）。</para>
/// </summary>
internal sealed class RecommendationPushAgent : DelegatingAIAgent
{
    /// <summary>
    /// 承载推荐依据的工具名（与 <see cref="RecommendationToolProvider.CreateTools"/> 注册的工具名一致；
    /// 由测试断言两者相同，防两处字面量漂移）。
    /// </summary>
    internal const string ToolName = "recommend_products";

    /// <summary>工具参数名（模型填自然语言 query，design §4.3）。</summary>
    private const string QueryArgumentName = "query";

    private readonly RecommendationToolProvider _recommendationTools;

    /// <summary>
    /// 构造装饰器。会话 / 名称 / 服务解析一律经 <c>base(inner)</c> 转发，本类不持有额外状态。
    /// </summary>
    /// <param name="inner">内层 agent（= 宿主 keyed factory 的 <c>AGUIShoppingAgent.Create</c> 产物）。</param>
    /// <param name="recommendationTools">推荐负载提供者（S2 的推送入口，同一 builder 保证与工具结果同口径）。</param>
    internal RecommendationPushAgent(AIAgent inner, RecommendationToolProvider recommendationTools)
        : base(inner)
    {
        _recommendationTools = recommendationTools;
    }

    /// <summary>
    /// 流式路径：逐条转发内层更新（不改对象、不加延迟），流末按门控追加**至多一条**推荐更新。
    /// </summary>
    /// <remarks>
    /// 依据解析（design §4.3）：本轮模型调过 <c>recommend_products</c> → 取其 <c>query</c> 实参
    /// （多次调用取最后一次有效值）；否则取本轮输入里最后一条 <see cref="ChatRole.User"/> 消息的文本。
    /// 门控（是否推送）不在本类：<see cref="RecommendationToolProvider.TryBuildPushPayloadAsync"/>
    /// 只在「依据命中白名单关键词 且 推荐列表非空」时返回非 <c>null</c>，故「非 null 才追加」即门控本身。
    /// </remarks>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 本轮输入物化一次：既要原样转交内层，又要在流末解析「最后一条 User 消息」，
        // 避免把惰性序列枚举两遍（`messages` 可能是单次可枚举的实现）。
        var input = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        string? toolQuery = null;

        await foreach (var update in InnerAgent.RunStreamingAsync(input, session, options, cancellationToken))
        {
            // 扫描工具调用：只为「以本轮模型实际用的 query 为依据」——不修改更新本身（引用保持不变，逐条原样吐给调用方）。
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call
                    && string.Equals(call.Name, ToolName, StringComparison.Ordinal)
                    && ReadQuery(call) is { } query)
                {
                    // 取最后一次**有效**值：缺失 / 非字符串 / 空白不覆盖已有的有效值（回退本轮用户消息）
                    toolQuery = query;
                }
            }

            yield return update;
        }

        JsonElement? payload = null;
        try
        {
            // 依据：模型调用工具时的 query 优先，否则本轮最后一条 User 消息文本（两者皆无 → null，provider 自会不推）
            payload = await _recommendationTools.TryBuildPushPayloadAsync(
                toolQuery ?? LastUserText(input),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 尽力而为的旁路（spec R4）：此刻本轮回复已完整吐出，推荐算不出来就放弃推送（面板保持上一次），
            // 绝不能让异常冲出迭代器把已经建立的流打断——那会连 RUN_FINISHED 都发不出去。
            Log.Warning(ex, "推荐推送计算失败，本轮跳过推送（面板保持上一次）");
        }

        // 非 null 才追加 = 门控落地：闲聊轮 / 身份缺失 / 关键词命中但无可推商品都不会产生事件，面板因此天然保持上一次。
        if (payload is { } pushPayload)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new RecommendationPushContent(pushPayload)]);
        }
    }

    /// <summary>
    /// 读 <c>recommend_products</c> 的 <c>query</c> 实参：缺失 / 非字符串 / 空白一律视为**无效**
    /// （返回 <c>null</c>，调用方保持已有值或回退本轮用户消息）。
    /// </summary>
    private static string? ReadQuery(FunctionCallContent call)
        => call.Arguments is { } arguments
            && arguments.TryGetValue(QueryArgumentName, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>本轮输入里最后一条 <see cref="ChatRole.User"/> 消息的文本（没有用户消息时为 <c>null</c>）。</summary>
    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
        => messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text;
}
