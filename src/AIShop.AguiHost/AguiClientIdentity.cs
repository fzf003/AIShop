using Microsoft.AspNetCore.Http;

namespace AIShop.AguiHost;

/// <summary>
/// 客户端支撑 REST 端点的身份通道（agui-client-support 第 6 项，design §13.3 / §13.8）：
/// wire 常量与查询参数解析。新端点的身份走查询参数 <c>?username=</c>，由既有
/// <see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/> 中间件统一解析——「解析 → 存在性校验 →
/// 注入 <see cref="AIShop.Core.Interfaces.ICurrentUserAccessor"/> → 失败短路」全仓只有一份实现，
/// 端点上只声明「是否必须带身份」（<see cref="AguiClientRestEndpoint"/>），不另写解析。
/// </summary>
/// <remarks>
/// <b>不是认证 / 授权（spec R5）</b>：与 AG-UI 面的 <c>forwardedProps.username</c> 一样，<c>?username=</c> 是客户端
/// 「自称」——三个种子账户（marla / steve / fzf003）无密码、无凭证，写谁的用户名就是谁。存在性校验解决的唯一问题
/// 是「用户名拼错导致静默空会话 / 孤儿购物车」的数据质量问题，<b>不减少</b>上述能力。<b>严禁</b>在注释、文档或
/// 对外文案中把它表述为「登录校验」「登录」「鉴权」或「认证」。
/// </remarks>
internal static class AguiClientIdentity
{
    /// <summary>REST 面身份查询参数键（design §13.3：用查询参数让路由保持资源语义，中间件不必知道路由模板）。</summary>
    internal const string UsernameQueryKey = "username";

    /// <summary>用户不存在的错误文案——与 AG-UI 面 <c>forwardedProps.username</c> 的校验为同一字节契约（spec R16）。</summary>
    internal const string UserNotFoundDetail = "User not found";

    /// <summary>购物车端点在缺 / 空白 <c>?username=</c> 时的错误文案（spec R15）。</summary>
    internal const string UsernameRequiredDetail = "Username is required";

    /// <summary>
    /// 从查询参数读用户名：缺失 / 空白 → <c>null</c>。调用方据 null 决定「拒绝（400）还是放行」，见
    /// <see cref="AguiClientRestEndpoint.RequireUsername"/>。
    /// </summary>
    /// <remarks>
    /// <b>REST 面 MUST NOT 回落缺省用户</b>：null 只表示「客户端未携带身份」，绝不表示「用 <c>DefaultUsername</c>」。
    /// 多值（<c>?username=a&amp;username=b</c>）取第一项，与查询参数「单值」语义一致。
    /// </remarks>
    internal static string? TryResolveQueryUsername(HttpRequest request)
    {
        if (!request.Query.TryGetValue(UsernameQueryKey, out var values) || values.Count == 0)
            return null;

        var username = values[0];
        return string.IsNullOrWhiteSpace(username) ? null : username;
    }
}

/// <summary>
/// 端点元数据标记：带此标记的端点身份走 REST 通道（查询参数 <c>?username=</c>），由
/// <see cref="AguiUsernameForwarder"/> 中间件消费。
/// </summary>
/// <remarks>
/// <para>
/// <b>分支依据是端点自身携带的元数据</b>，不是 HTTP 方法、也不是路径字符串：按方法分会把 <c>POST /cart/items</c>
/// 误判为 AG-UI 请求（漏带参数时静默写进缺省用户的购物车）；按路径前缀会随路由分组 / 前缀调整静默失配。
/// 元数据随路由走，失配时的失败方向是安全的（REST 请求落入 AG-UI 分支 → 身份未注入 → 端点返回 400 明确拒绝，
/// 而非静默按缺省用户处理，spec R20）。
/// </para>
/// <para>
/// <see cref="RequireUsername"/> <b>缺省 <c>true</c></b>：购物车 5 端点用缺省——缺参（缺失 / 空白）→ 400 短路，
/// 不注入、<b>不回落缺省用户</b>；公开可读端点（<c>GET /products</c>）显式置 <c>false</c>——缺参 → 不注入身份、
/// 直接放行，端点返回 200。两种取值下「带了非空白值」的行为一致：一律存在性校验（查无此人 → 404）。
/// </para>
/// <para>
/// 该字段只声明「缺参是否拒绝」，<b>不新增任何身份解析路径</b>：6 个端点的解析 / 校验 / 注入 / 短路仍只有中间件一处。
/// </para>
/// </remarks>
internal sealed class AguiClientRestEndpoint
{
    /// <summary>缺参（缺失 / 空白）时是否必须带身份：<c>true</c> → 400 短路；<c>false</c> → 放行且不注入身份。</summary>
    public bool RequireUsername { get; init; } = true;
}

#pragma warning disable S2094 // 端点元数据标记：无成员是设计意图（只作 GetMetadata<T>() 的存在性判别），非漏写
/// <summary>
/// 端点元数据标记：<b>有意走 AG-UI 身份分支</b>的端点——AG-UI 流式端点（<c>POST /</c>）以及同源的
/// Development 端点（DevUI 面板 / OpenAI Responses / Conversations wire）。
/// </summary>
/// <remarks>
/// <para>
/// 这些端点<b>有意不挂</b> <see cref="AguiClientRestEndpoint"/>：其身份来自请求体 <c>forwardedProps</c>
/// （缺失时回落 <see cref="AguiUsernameForwarder.DefaultUsername"/>），而非 REST 面的 <c>?username=</c>。
/// </para>
/// <para>
/// 本标记的<b>唯一用途</b>是让 <see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/> 的
/// 「漏挂标记」告警（L3 C 防护）能区分「有意走 AG-UI 分支的合法端点」与「新加写端点却忘记挂
/// <see cref="AguiClientRestEndpoint"/>」——否则对每一个 AG-UI / DevUI 的 POST 都会误报。
/// </para>
/// <para>
/// 本标记<b>不参与身份解析</b>：REST 分支的判定依据仍然只有 <see cref="AguiClientRestEndpoint"/> 一个
/// （见 <see cref="AguiUsernameForwarder"/>），加本标记不改变任何请求的数据流向。
/// </para>
/// </remarks>
internal sealed class AguiStreamEndpoint;
#pragma warning restore S2094
