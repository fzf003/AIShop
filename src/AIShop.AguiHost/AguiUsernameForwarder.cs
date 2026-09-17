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
/// 中携带的 <c>username</c> 解析出来，写入 <see cref="ICurrentUserAccessor"/>（缺省 <see cref="DefaultUsername"/> = steve），
/// 使购物工具经 <c>ICurrentUserAccessor.CurrentUser</c> 读到同一用户（spec「username 经 ICurrentUserAccessor 由
/// AGUI metadata 注入，缺省用户」）。
/// </summary>
/// <remarks>
/// 挂点说明（以 MAF 1.20 preview <c>MapAGUIServer</c> 请求管线为准）：preview 的 <c>MapAGUIServer("/", agent)</c>
/// 内部注册的是一个 <c>MapPost + [FromBody] RunAgentInput</c> 请求处理器，框架未提供「从 forwarded metadata 提取用户」
/// 的中间件/isolation 挂点（<c>AgentIsolationKeyProvider</c> 面向 ThreadId 会话隔离，不读 body username）。
/// 因此本类提供两个层次：① <see cref="ResolveUsername"/> —— 纯函数（可单测），从 forwarded metadata 读 username；
/// ② <see cref="UseAguiUsernameForwarding"/> —— 装配中间件，置于 <c>MapAGUIServer</c> 之前，缓冲读同一请求体、
/// 解析 metadata 并在 agent 运行前把 username（或缺省用户）写入 <see cref="ICurrentUserAccessor"/>。
/// <para>
/// <b>同一中间件承担两条身份通道（agui-client-support 第 6 项，design §13.3）</b>：③ 第 6 项新增的商品 / 购物车
/// REST 端点带 <see cref="AguiClientRestEndpoint"/> 元数据标记，身份走查询参数 <c>?username=</c>，分支依据是该标记
/// （不是 HTTP 方法、也不是路径字符串，见 <see cref="HandleRestIdentityAsync"/>）。两条通道共用同一份「解析 →
/// 存在性校验 → 注入 → 短路」实现，杜绝「AG-UI 校验、REST 不校验」的分叉；但 <b>REST 面 MUST NOT 回落缺省用户</b>
/// （缺参在购物车端点被 400 拒绝、在 <c>/products</c> 被放行，两者都不是回落）。
/// </para>
/// <para>
/// <b>本校验不是认证 / 授权（agui-client-support R5）</b>：三个种子账户（marla / steve / fzf003）无密码、无凭证、
/// 无 token，用户身份完全<b>由请求体自称</b>——<c>forwardedProps.username</c> 写谁就是谁。该用户名直接决定数据归属
/// （会话 store key = <c>{agentName}:{username}</c>、<c>conversation_id</c>、购物车 / 聊天历史 / 记忆的读写主体），
/// 因此任何人只要在请求体里写别人的用户名，就能读写别人的会话与购物车——存在性校验<b>不减少</b>这一能力
/// （三个名字都是公开的）。本校验解决的唯一问题是「用户名拼错导致静默空会话 / 工具层『用户不存在』错误文本」，
/// 属数据质量与 UX 问题。<b>严禁</b>在注释、文档或对外文案中把它表述为「登录校验」「登录」「鉴权」或「认证」。
/// </para>
/// </remarks>
internal static class AguiUsernameForwarder
{
    /// <summary>AG-UI RunAgentInput 顶层 forwarded metadata 键（preview AGUI .NET 0.0.5 wire 名，与镜像集成测试一致）。</summary>
    internal const string ForwardedPropsProperty = "forwardedProps";

    /// <summary>forwarded metadata 中的 username 键。</summary>
    internal const string UsernameMetadataKey = "username";

    /// <summary>metadata 缺失 username 时的缺省用户（spec「metadata 缺失时按 steve 处理」）。</summary>
    internal const string DefaultUsername = "steve";

    /// <summary>
    /// 从 forwarded metadata（JSON object，如 <c>RunAgentInput.forwardedProps</c>）读取 <c>username</c>。
    /// 缺失该键 / 值非字符串 / 空白 / metadata 非 object → 返回 null（表示「应由调用方应用缺省用户」）。
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

    /// <summary>解析 + 缺省用户一次到位（装配点用）：metadata 缺失/非法时回退 <see cref="DefaultUsername"/>。</summary>
    internal static string ResolveUsernameOrDefault(JsonElement forwardedMetadata)
        => ResolveUsername(forwardedMetadata) ?? DefaultUsername;

    /// <summary>
    /// 装配挂点中间件：把 username 写入 <see cref="ICurrentUserAccessor"/> 的请求管线入口。
    /// 对每个 AG-UI <c>POST RunAgentInput</c>：缓冲读取请求体 → 从 forwarded metadata（<c>forwardedProps.username</c>）
    /// 解析 username → <b>显式携带且非空白</b>时先做用户表存在性校验（不存在则 404 短路，不进入 Agent）→
    /// 执行流注入 <see cref="ICurrentUserAccessor"/>（缺失/解析失败一律 <see cref="DefaultUsername"/>）→
    /// 回退 Body 位置后放行，由 <c>MapAGUIServer</c> 端点继续处理（同一请求体重新反序列化）。
    /// </summary>
    internal static IApplicationBuilder UseAguiUsernameForwarding(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // 【第 6 项】REST 通道优先：带 AguiClientRestEndpoint 元数据的端点，身份走查询参数 ?username=。
            // 必须排在下面的「非 POST 放行」之前——GET /cart 无请求体，若走 AG-UI 分支会被直接放行、身份不注入。
            // 分支依据 = 端点自身携带的元数据（不是 HTTP 方法、也不是路径字符串）：元数据随路由走，
            // 失配时的失败方向安全（REST 请求落入 AG-UI 分支 → 身份未注入 → 端点返回 400 明确拒绝，
            // 而非静默按缺省用户处理，spec R20）。REST 面 MUST NOT 回落 DefaultUsername（design §13.4）。
            if (context.GetEndpoint()?.Metadata.GetMetadata<AguiClientRestEndpoint>() is { } restEndpoint)
            {
                await HandleRestIdentityAsync(context, restEndpoint, next);
                return;
            }

            // 仅 AG-UI 承载 body 的 POST（RunAgentInput）需要提取 username；其余请求透传
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await next(context);
                return;
            }

            // 从请求体 forwarded metadata 解析 username；读体/解析失败视为「无 metadata」→ 缺省用户（不阻断请求）
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
                // 非法 JSON / 读体失败：视为无 forwarded metadata → 缺省用户；交由端点按自身契约返回 4xx
                username = null;
            }

            // agui-client-support T3（spec R3）：仅对【显式携带且非空白】的 username 做用户表存在性校验。
            // 不在表内 → 写 404 + {"detail":"User not found"}（形状对齐老 POST /api/login）并短路：
            // 不进入 Agent、不注入身份、不回落缺省用户 → 零 LLM 调用 / 零会话快照 / 零聊天历史 / 零购物车 / 零记忆副作用。
            // 这是 fail-fast：校验发生在中间件层（MapAGUIServer 之前），若下沉到 Agent 工具层，失败将发生在一轮 LLM 之后
            // （秒级延迟 + token 成本），且错误只能以自然语言字符串返回，前端无法据此回到登录页。
            if (username is not null && !await IsExistingUserAsync(context, username))
                return;

            // 缺省回落路径零改动（spec R4）：缺失 / 非字符串 / 空白 → DefaultUsername，且【不读库】（零开销、零回归）。
            // 只校验显式携带的 username，是为了避免把一处登录页 UX 校验扩散成对非 AG-UI POST（如 Development 下的
            // DevUI / OpenAI wire 端点）的全局约束——那类请求的体里没有 forwardedProps，永远走本分支。
            var accessor = context.RequestServices.GetRequiredService<ICurrentUserAccessor>();
            accessor.SetCurrentUser(username ?? DefaultUsername);

            await next(context);
        });
    }

    /// <summary>
    /// REST 通道身份处理（agui-client-support 第 6 项，spec R15 / R16 / R17）：
    /// 从查询参数 <c>?username=</c> 解析身份 → 存在性校验 → 注入 <see cref="ICurrentUserAccessor"/> → 放行；
    /// 任一失败步骤短路返回。<b>不读请求体</b>（GET 无体；POST 体是业务载荷，不是身份载体）。
    /// </summary>
    /// <remarks>
    /// 缺参（缺失 / 空白）时的行为由端点标记 <see cref="AguiClientRestEndpoint.RequireUsername"/> 决定：
    /// <list type="bullet">
    /// <item><c>true</c>（购物车 5 端点，缺省）→ <c>400</c> + <c>{"detail":"Username is required"}</c> 短路；
    /// 不调用端点、不注入身份、<b>不回落 <see cref="DefaultUsername"/></b>——避免「前端漏发身份」被静默当作
    /// 缺省用户的身份处理。</item>
    /// <item><c>false</c>（仅 <c>GET /products</c>，公开可读）→ 不注入身份、直接放行（端点返回 200）。
    /// <b>这不是回落</b>：<see cref="ICurrentUserAccessor"/> 保持 null（与 <c>/models</c> 的公开可读取舍同源）。</item>
    /// </list>
    /// 两种取值下「带了非空白值」的行为一致：与 AG-UI 面<b>同一份</b>存在性校验（<see cref="IsExistingUserAsync"/>）
    /// ——查无此人 → 404 短路（错误体逐字节一致），命中 → 注入后放行。
    /// </remarks>
    private static async Task HandleRestIdentityAsync(
        HttpContext context,
        AguiClientRestEndpoint endpoint,
        RequestDelegate next)
    {
        var username = AguiClientIdentity.TryResolveQueryUsername(context.Request);

        if (username is null)
        {
            if (!endpoint.RequireUsername)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { detail = AguiClientIdentity.UsernameRequiredDetail },
                context.RequestAborted);
            return;
        }

        if (!await IsExistingUserAsync(context, username))
            return;

        context.RequestServices.GetRequiredService<ICurrentUserAccessor>().SetCurrentUser(username);
        await next(context);
    }

    /// <summary>
    /// 显式 username 的用户表存在性校验（agui-client-support T3，spec R3）。
    /// 返回 <c>true</c> = 用户存在、放行；返回 <c>false</c> = 已写出 404 短路响应，调用方<b>必须立即 return</b>。
    /// </summary>
    /// <remarks>
    /// 实现要点：
    /// <list type="bullet">
    /// <item><see cref="IUserRepository"/> 是 Scoped（EF <c>DbContext</c>），必须经 <c>CreateScope()</c> 解析，
    /// <b>不得</b>缓存到静态字段 / 单例。</item>
    /// <item>取消令牌取 <c>RequestAborted</c>。</item>
    /// <item>读库异常<b>不</b> try/catch 吞（与老 <c>/api/login</c> 一致）：自然上抛为 5xx。把「读不出来」乐观当作
    /// 「校验通过」会掩盖数据层故障。</item>
    /// <item>响应是<b>普通 HTTP 404 + JSON</b>，不是 AG-UI 端点的 SSE 事件流（本中间件位于 MapAGUIServer 之前），
    /// 客户端需能处理非流式 / 非 200 响应。</item>
    /// </list>
    /// 提醒：本方法只证明「该用户名在用户表中存在」，<b>不是</b>认证 / 授权，也<b>不是</b>访问控制（身份由请求体自称）。
    /// </remarks>
    private static async Task<bool> IsExistingUserAsync(HttpContext context, string username)
    {
        using var scope = context.RequestServices.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await userRepository.GetByUsernameAsync(username, context.RequestAborted);
        if (user is not null)
            return true;

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(
            new { detail = "User not found" },
            context.RequestAborted);
        return false;
    }
}
