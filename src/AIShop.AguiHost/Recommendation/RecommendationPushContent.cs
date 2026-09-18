using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Recommendation;

/// <summary>
/// 「推荐负载」标记内容（agui-reco-realtime S3，design §4.4 C）：一个<b>只携带数据、无任何行为</b>的自定义
/// <see cref="AIContent"/>，由轮末装饰器（<c>RecommendationPushAgent</c>，S4）合成进 AG-UI 流，
/// 再由 <see cref="AguiRecommendationStreamOptions.MapContent"/> 映射成
/// <c>{ "type": "CUSTOM", "name": "recommendation", "value": &lt;推荐 JSON 对象&gt; }</c> 事件。
///
/// <para><b>为什么用「自定义 AIContent」承载</b>：AG-UI 转换器对未命中内置映射的内容会走
/// <c>MapContent</c> 回调（S1 探针实测），这是我们唯一能挂「自定义事件」的宿主侧缝。</para>
///
/// <para><b>生命周期边界（三条均为结构性保证，不是约定）</b>：</para>
/// <list type="bullet">
/// <item><b>不序列化</b>——它只在宿主进程内作为标记传递；进入转换器时已变成 <c>CustomEvent</c>（<see cref="Payload"/>
/// 是 <see cref="JsonElement"/>，无需二次序列化）。<b>注意</b>：AG-UI 转换器对每个更新做的原始快照序列化会解析
/// <c>AIContent</c> 的 JSON 多态派生类型，故本类型<b>必须</b>同时注册进该多态表——见
/// <see cref="AguiRecommendationStreamOptions.AddAguiRecommendationStreamOptions"/>（S1 实测硬约束，缺则整条流断开）。</item>
/// <item><b>不落库</b>——它挂在装饰器合成的更新上，装饰器位于 <c>OpenTelemetryAgent</c> 之外（design §4.1），
/// 会话快照与聊天历史（<c>SqlChatHistoryProvider</c>）保存的都是内层消息，本内容不在其中。</item>
/// <item><b>不回喂模型</b>——工具循环（FICC）在 <c>ChatClientAgent</c> 之下，看不到装饰器合成的更新，
/// 模型下一轮不会收到任何推荐 JSON。</item>
/// </list>
/// </summary>
/// <param name="payload">推荐负载（由 <c>RecommendationToolProvider.TryBuildPushPayloadAsync</c> 产出的 camelCase JSON 对象，
/// 与 <c>recommend_products</c> 工具结果同构）。</param>
internal sealed class RecommendationPushContent(JsonElement payload) : AIContent
{
    /// <summary>AG-UI <c>CUSTOM</c> 事件的 <c>name</c>（wire 契约，前端按此识别推荐事件）。</summary>
    internal const string EventName = "recommendation";

    /// <summary>事件载荷（原样进 <c>CustomEvent.Value</c>，不做任何改写）。</summary>
    internal JsonElement Payload { get; } = payload;
}
