using Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Recommendation;

/// <summary>
/// 「本轮流失败」终止标记内容（agui-reco-realtime C1）：一个<b>只携带数据、无任何行为</b>的自定义
/// <see cref="AIContent"/>，由 <see cref="RecommendationPushAgent"/> 在**内层模型流抛异常时**补发，
/// 再由 <see cref="AguiRecommendationStreamOptions.MapContent"/> 映射成 AG-UI 的
/// <c>{ "type": "RUN_ERROR", "message": ..., "code": ... }</c> 终止事件。
///
/// <para><b>为什么需要它</b>：宿主在 net10.0 走 <c>TypedResults.ServerSentEvents</c>，其错误兜底
/// （<c>RunErrorEvent</c>）只存在于 <c>#if !NET10_0_OR_GREATER</c> 分支（安装包 net10.0 DLL 内
/// <c>RunErrorEvent</c> / <c>StreamingError</c> 字符串命中数为 0，net8.0/net9.0 各为 1）。因此内层模型/网关
/// 故障一旦让异常直接冲出 agent 迭代器，整条 SSE 流会**静默断在半途**：既无终止帧，也无任何错误信号——
/// 客户端 <c>@ag-ui/client</c> 的 <c>isRunning</c> 因此可能永不复位。本类型就是宿主侧自补的那条终止信号。</para>
///
/// <para><b>与「吞异常」的区别</b>：装饰器在补发本内容的同时记一条 Serilog Error（含原始堆栈），
/// 并让迭代器<b>正常结束</b>——<b>不</b>重抛异常，以免宿主中止连接而丢掉尚未送达的帧
/// （2026-09-20 实测修正，理由见 <see cref="RecommendationPushAgent"/> 的方法注释）。
/// 故障不会因此被隐藏：终止帧携带 <c>code</c>/<c>message</c>，日志保留完整异常。</para>
///
/// <para><b>生命周期边界</b>（同 <see cref="RecommendationPushContent"/>）：不落库（装饰器在
/// <c>OpenTelemetryAgent</c> 之外，会话快照/聊天历史都只含内层消息）、不回喂模型（FICC 在
/// <c>ChatClientAgent</c> 之下，看不到装饰器合成的更新）。<b>注意</b>：AG-UI 转换器对每个更新做的原始快照
/// 序列化会解析 <see cref="AIContent"/> 的多态派生类型，故本类型<b>必须</b>同时注册进该多态表
/// （见 <see cref="AguiRecommendationStreamOptions.AddAguiRecommendationStreamOptions"/>，缺则整条流断开）。</para>
/// </summary>
/// <param name="message">面向客户端的错误说明（不携带内部细节；完整异常由装饰器记 Serilog Error）。</param>
internal sealed class AguiStreamFailureContent(string message) : AIContent
{
    /// <summary>AG-UI <c>RUN_ERROR</c> 事件的 <c>code</c>（稳定标识，供客户端按类别识别）。</summary>
    internal const string ErrorCode = "agent_stream_failure";

    /// <summary>缺省错误说明（装饰器不另传时使用）。</summary>
    internal const string DefaultMessage = "本轮回复因服务端异常中断，请重试。";

    /// <summary>错误说明（原样进 <c>RunErrorEvent.Message</c>）。</summary>
    internal string Message { get; } = message;
}
