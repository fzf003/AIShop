using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Model;

/// <summary>
/// AG-UI model 注入装配（agui-model-switch C5 M3）。
/// 把 AG-UI <c>RunAgentInput</c> 请求的 forwarded metadata（preview wire 键为顶层 <c>forwardedProps</c>，
/// 与 username 同源同形状，见 <see cref="AguiUsernameForwarder.ForwardedPropsProperty"/>）中携带的 <c>model</c>
/// 解析出来，写入 <see cref="IActiveModelProvider"/>，使 <see cref="RouterChatClient"/> 在同一次 run 的 async 链
/// 深处把该轮对话委托到「本轮激活模型」的底层 chatClient（spec ADDED Requirement 5）。
/// </summary>
/// <remarks>
/// 与 username 中间件（<see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/>）同款独立中间件（不并入），
/// 两个 concern 独立、测试缝独立；代价是请求体二次 JSON 解析（AG-UI body 小，可接受，design §6.1）。
/// 关键差异：username 缺失时回退缺省用户（<c>steve</c>）；model 缺失/非法时【不注入缺省模型值】，而是
/// <see cref="IActiveModelProvider.SetActiveModel"/>(null) 显式清空——「缺省 = ActiveModel」由
/// <see cref="RouterChatClient"/> 在读取侧解析（单一数据源：ActiveModel 配置的归属是工厂/Router，不是中间件）。
/// </remarks>
internal static class AguiModelForwarder
{
    /// <summary>forwarded metadata 中的 model 键（wire 键名，锁 spec Req5「forwardedProps.model」）。</summary>
    internal const string ModelMetadataKey = "model";

    /// <summary>
    /// 从 forwarded metadata（JSON object，如 <c>RunAgentInput.forwardedProps</c>）读取 <c>model</c>。
    /// metadata 非 object / 缺 <c>model</c> 键 / 值非字符串 / 空白 → 返回 null（＝未指定，走 ActiveModel 缺省；
    /// 对应 spec Req5「缺失/非字符串/空白 → null」）。
    /// </summary>
    internal static string? ResolveModel(JsonElement forwardedMetadata)
    {
        if (forwardedMetadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!forwardedMetadata.TryGetProperty(ModelMetadataKey, out var modelProperty))
            return null;

        if (modelProperty.ValueKind != JsonValueKind.String)
            return null;

        var model = modelProperty.GetString();
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    /// <summary>
    /// 装配挂点中间件：把每轮激活模型写入 <see cref="IActiveModelProvider"/> 的请求管线入口。
    /// 对每个 AG-UI <c>POST RunAgentInput</c>：缓冲读取请求体 → 从 forwarded metadata（<c>forwardedProps.model</c>）
    /// 解析模型 → <see cref="IActiveModelProvider.SetActiveModel"/>（解析失败一律 <c>null</c> 显式清空，不注入缺省值）
    /// → 回退 Body 位置后放行，由 <c>MapAGUIServer</c> 端点继续处理（同一请求体重新反序列化）。
    /// </summary>
    internal static IApplicationBuilder UseAguiModelForwarding(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // 仅 AG-UI 承载 body 的 POST（RunAgentInput）需要提取 model；其余请求透传
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await next(context);
                return;
            }

            // 从请求体 forwarded metadata 解析 model；读体/解析失败视为「无 model」（→ null，走 ActiveModel 缺省，
            // 由 RouterChatClient 读取侧解析），不阻断请求
            string? model = null;
            try
            {
                // EnableBuffering：使请求体可 seek，读完回退位置供 MapAGUIServer 的 [FromBody] 重新读取。
                // 与 username 中间件各自缓冲读同一请求体、互不干扰（AG-UI body 小，二次 JSON 解析可接受）。
                context.Request.EnableBuffering();
                using (var reader = new StreamReader(
                           context.Request.Body,
                           Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: false,
                           bufferSize: 1024,
                           leaveOpen: true))
                {
                    var body = await reader.ReadToEndAsync(context.RequestAborted);
                    context.Request.Body.Position = 0;

                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty(AguiUsernameForwarder.ForwardedPropsProperty, out var forwarded)
                            && forwarded.ValueKind == JsonValueKind.Object)
                        {
                            model = ResolveModel(forwarded);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 非法 JSON / 读体失败：视为无 forwarded metadata → model = null（走 ActiveModel 缺省）
                model = null;
            }

            // 执行流级注入：model 缺失/非法 → 显式 SetActiveModel(null)（清空 = 「未指定」，缺省 ActiveModel 由
            // Router 读取侧解析；中间件不注入缺省模型值）。AsyncLocal 值随本次 run 的执行流流动、不跨请求泄漏。
            context.RequestServices.GetRequiredService<IActiveModelProvider>().SetActiveModel(model);

            await next(context);
        });
    }
}
