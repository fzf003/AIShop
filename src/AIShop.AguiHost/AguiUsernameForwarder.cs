using System.Text;
using System.Text.Json;
using AIShop.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost;

/// <summary>
/// AG-UI username 注入装配（agui-host T5）。
/// 把 AG-UI <c>RunAgentInput</c> 请求的 forwarded metadata（preview wire 键为顶层 <c>forwardedProps</c>）
/// 中携带的 <c>username</c> 解析出来，写入 <see cref="ICurrentUserAccessor"/>（缺省 <see cref="DefaultUsername"/> = guest），
/// 使购物工具经 <c>ICurrentUserAccessor.CurrentUser</c> 读到同一用户（spec「username 经 ICurrentUserAccessor 由
/// AGUI metadata 注入，缺省 guest」）。
/// </summary>
/// <remarks>
/// 挂点说明（以 MAF 1.20 preview <c>MapAGUIServer</c> 请求管线为准）：preview 的 <c>MapAGUIServer("/", agent)</c>
/// 内部注册的是一个 <c>MapPost + [FromBody] RunAgentInput</c> 请求处理器，框架未提供「从 forwarded metadata 提取用户」
/// 的中间件/isolation 挂点（<c>AgentIsolationKeyProvider</c> 面向 ThreadId 会话隔离，不读 body username）。
/// 因此本类提供两个层次：① <see cref="ResolveUsername"/> —— 纯函数（可单测），从 forwarded metadata 读 username；
/// ② <see cref="UseAguiUsernameForwarding"/> —— 装配中间件，置于 <c>MapAGUIServer</c> 之前，缓冲读同一请求体、
/// 解析 metadata 并在 agent 运行前把 username（或 guest）写入 <see cref="ICurrentUserAccessor"/>。
/// </remarks>
internal static class AguiUsernameForwarder
{
    /// <summary>AG-UI RunAgentInput 顶层 forwarded metadata 键（preview AGUI .NET 0.0.5 wire 名，与镜像集成测试一致）。</summary>
    internal const string ForwardedPropsProperty = "forwardedProps";

    /// <summary>forwarded metadata 中的 username 键。</summary>
    internal const string UsernameMetadataKey = "username";

    /// <summary>metadata 缺失 username 时的缺省用户（spec「metadata 缺失时按 guest 处理」）。</summary>
    internal const string DefaultUsername = "fzf003";

    /// <summary>
    /// 从 forwarded metadata（JSON object，如 <c>RunAgentInput.forwardedProps</c>）读取 <c>username</c>。
    /// 缺失该键 / 值非字符串 / 空白 / metadata 非 object → 返回 null（表示「应由调用方应用 guest 缺省」）。
    /// </summary>
    internal static string? ResolveUsername(JsonElement forwardedMetadata)
    {
        if (forwardedMetadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!forwardedMetadata.TryGetProperty(UsernameMetadataKey, out var usernameProperty))
            return null;

        if (usernameProperty.ValueKind != JsonValueKind.String)
            return null;

        var username = usernameProperty.GetString();
        return string.IsNullOrWhiteSpace(username) ? null : username;
    }

    /// <summary>解析 + guest 缺省一次到位（装配点用）：metadata 缺失/非法时回退 <see cref="DefaultUsername"/>。</summary>
    internal static string ResolveUsernameOrDefault(JsonElement forwardedMetadata)
        => ResolveUsername(forwardedMetadata) ?? DefaultUsername;

    /// <summary>
    /// 装配挂点中间件：把 username 写入 <see cref="ICurrentUserAccessor"/> 的请求管线入口。
    /// 对每个 AG-UI <c>POST RunAgentInput</c>：缓冲读取请求体 → 从 forwarded metadata（<c>forwardedProps.username</c>）
    /// 解析 username → 执行流注入 <see cref="ICurrentUserAccessor"/>（缺失/解析失败一律 <see cref="DefaultUsername"/>）→
    /// 回退 Body 位置后放行，由 <c>MapAGUIServer</c> 端点继续处理（同一请求体重新反序列化）。
    /// </summary>
    internal static IApplicationBuilder UseAguiUsernameForwarding(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // 仅 AG-UI 承载 body 的 POST（RunAgentInput）需要提取 username；其余请求透传
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await next(context);
                return;
            }

            // 从请求体 forwarded metadata 解析 username；读体/解析失败视为「无 metadata」→ guest（不阻断请求）
            string? username = null;
            try
            {
                // EnableBuffering：使请求体可 seek，读完回退位置供 MapAGUIServer 的 [FromBody] 重新读取
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
                            && root.TryGetProperty(ForwardedPropsProperty, out var forwarded)
                            && forwarded.ValueKind == JsonValueKind.Object)
                        {
                            username = ResolveUsername(forwarded);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 非法 JSON / 读体失败：视为无 forwarded metadata → guest；交由端点按自身契约返回 4xx
                username = null;
            }

            // 执行流级注入：metadata 缺失 → guest（购物工具经 CurrentUser 读取同一用户，无需感知用户来源）
            var accessor = context.RequestServices.GetRequiredService<ICurrentUserAccessor>();
            accessor.SetCurrentUser(username ?? DefaultUsername);

            await next(context);
        });
    }
}
