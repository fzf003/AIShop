using System.Text.Json.Serialization.Metadata;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace AIShop.AguiHost.Recommendation;

/// <summary>
/// AG-UI 流选项装配（agui-reco-realtime S3 + C1，design §4.4 D 第 2 / 第 4 条）：把
/// <see cref="RecommendationPushContent"/> 映射为 <c>CUSTOM</c> 事件、把
/// <see cref="AguiStreamFailureContent"/> 映射为 <c>RUN_ERROR</c> 终止事件，并把这两个内容类型都登记进
/// <see cref="AIContent"/> 的 JSON 多态派生类型表。
///
/// <para><b>两件事缺一不可</b>（S1 探针实测，design §4.4 D 第 4 条）：</para>
/// <list type="number">
/// <item><see cref="AGUIStreamOptions.MapContent"/> 注册 —— 决定「自定义内容变成什么事件」；</item>
/// <item><see cref="AIContent"/> 的 JSON 多态派生类型注册 —— 决定「这条流能不能活到映射那一步」。
/// AG-UI 转换器遍历每个更新时<b>先无条件</b>做一次原始快照序列化（<c>ChatResponseUpdate</c>，早于内容映射），
/// 该序列化要求所有出现在 <c>Contents</c> 里的 <see cref="AIContent"/> 子类都已登记；漏登记会抛
/// <c>NotSupportedException</c>（<c>... is not supported by polymorphic type '...AIContent'</c>），
/// <b>整条 SSE 流在到达 mapper 之前就断开</b>（连 <c>RUN_FINISHED</c> 都不发）。</item>
/// </list>
/// <para>第 2 条的症状会<b>掩盖</b>第 1 条是否生效（看起来像「IOptions 路径不通」），故两者一并落在本扩展里，
/// 由调用方一次装配。</para>
///
/// <para><b>事件语义（design §5 契约）</b>：</para>
/// <list type="bullet">
/// <item><see cref="MapContent"/> 返回 <c>null</c> = 放弃该内容、交回转换器的既有语义（继续试下一个 mapper；
/// 全部放弃则该内容被静默丢弃）——<b>不伪造事件</b>，避免非推荐内容被误挂到推荐位置。</item>
/// <item>映射出的事件必在该轮 <c>RUN_FINISHED</c> <b>之前</b>（合成更新是流内元素，转换器在流末才发 RUN_FINISHED），
/// 一轮<b>至多一条</b>（装饰器只在流末追加一条更新）。</item>
/// <item>该事件<b>不产生</b>消息，也<b>不产生</b>工具调用栏条目（客户端 <c>CUSTOM</c> 分支只派发 <c>onCustomEvent</c>）。</item>
/// </list>
/// </summary>
internal static class AguiRecommendationStreamOptions
{
    /// <summary>
    /// 内容 → 事件的纯函数映射（可脱离宿主单测；注册与实际调用引用<b>同一个</b>方法，避免两套判定分叉）。
    ///
    /// <para><see cref="RecommendationPushContent"/> → 单元素 <see cref="CustomEvent"/> 序列
    /// （<c>name</c> = <see cref="RecommendationPushContent.EventName"/>、<c>value</c> = 原样 payload）；
    /// <see cref="AguiStreamFailureContent"/> → 单元素 <see cref="RunErrorEvent"/>（<c>code</c> =
    /// <see cref="AguiStreamFailureContent.ErrorCode"/>、<c>message</c> = 原样说明）；
    /// 其它任何内容（<see cref="TextContent"/> / <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/>
    /// 等）→ <c>null</c>。</para>
    /// </summary>
    internal static IEnumerable<BaseEvent>? MapContent(AIContent content) =>
        content switch
        {
            RecommendationPushContent push =>
            [
                new CustomEvent
                {
                    Name = RecommendationPushContent.EventName,
                    Value = push.Payload,
                }
            ],
            // C1：内层流失败时的确定收尾——映射成错误出口（RUN_ERROR），不得伪装成 RUN_FINISHED。
            AguiStreamFailureContent failure =>
            [
                new RunErrorEvent
                {
                    Message = failure.Message,
                    Code = AguiStreamFailureContent.ErrorCode,
                }
            ],
            _ => null,
        };

    /// <summary>
    /// 注册推荐事件的流选项与内容多态派生类型（见类型注释的「两件事缺一不可」）。
    /// 调用点：AguiHost <c>Program.cs</c> 在 <c>AddAGUIServer()</c> 之后（S5 工单接线）。
    /// </summary>
    internal static void AddAguiRecommendationStreamOptions(this IServiceCollection services)
    {
        // ① 内容 → CUSTOM 事件（design §4.4 D 第 2 条）：IOptions 路径，MapAGUIServer 的请求 lambda 读
        // RequestServices.GetService<IOptions<AGUIStreamOptions>>()?.Value（S1 探针实测该路径生效）。
        services.Configure<AGUIStreamOptions>(options => options.MapContent(MapContent));

        // ② 内容类型的 JSON 多态派生类型注册（design §4.4 D 第 4 条）：在宿主 HTTP JSON 选项的 resolver 链上挂
        // JsonTypeInfo 修饰器，把本类型追加进 AIContent 的多态表（复制既有配置，不动其它派生类型）。
        //
        // ⚠️ 作用面是**宿主级**（L18）：`Configure<HttpJsonOptions>` 对**全宿主的 HTTP JSON 序列化**生效，
        // 不只作用于 AG-UI SSE 的 CUSTOM 帧。当前**无副作用** —— 全仓没有第二个把 `AIContent` 当响应体
        // 序列化的 REST 端点（`/models`、`/products`、`/cart*` 都返回各自的 DTO）。**新增这类端点前**
        // 需重新评估：它会顺带获得这两个判别符，届时应考虑收窄（例如改挂在 AG-UI 端点自己的序列化选项上）。
        services.Configure<HttpJsonOptions>(options =>
        {
            var resolver = options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.SerializerOptions.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
            {
                if (typeInfo.Type != typeof(AIContent) || typeInfo.PolymorphismOptions is not { } polymorphism)
                    return;

                // 幂等：重复装配（或上游已登记）时不重复追加，避免出现重复判别符。
                // ⚠️ 必须【按类型逐个判断】，不能「只要看到 RecommendationPushContent 就整体 return」——
                // 那会让后登记的类型（如 AguiStreamFailureContent）被这次 early-return 直接跳过，
                // 结果是转换器在无条件快照序列化处抛 NotSupportedException，整条流在到达 mapper 之前就断
                //（比不注册更糟：失败路径一触发反而连 CUSTOM 帧都保不住）。
                var missing = new List<JsonDerivedType>();
                if (!polymorphism.DerivedTypes.Any(derived => derived.DerivedType == typeof(RecommendationPushContent)))
                    missing.Add(new JsonDerivedType(typeof(RecommendationPushContent), "recommendationPush"));
                if (!polymorphism.DerivedTypes.Any(derived => derived.DerivedType == typeof(AguiStreamFailureContent)))
                    missing.Add(new JsonDerivedType(typeof(AguiStreamFailureContent), "aguiStreamFailure"));
                if (missing.Count == 0)
                    return;

                var merged = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = polymorphism.TypeDiscriminatorPropertyName,
                    IgnoreUnrecognizedTypeDiscriminators = polymorphism.IgnoreUnrecognizedTypeDiscriminators,
                    UnknownDerivedTypeHandling = polymorphism.UnknownDerivedTypeHandling,
                };
                foreach (var derived in polymorphism.DerivedTypes)
                    merged.DerivedTypes.Add(derived);
                foreach (var derived in missing)
                    merged.DerivedTypes.Add(derived);
                typeInfo.PolymorphismOptions = merged;
            });
        });
    }
}
