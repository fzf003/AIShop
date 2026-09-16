namespace AIShop.AguiHost;

/// <summary>
/// AguiHost 的可选 CORS 装配（design §6、spec R6「CORS 默认不注册」/ R7「白名单可配置且精确匹配」）。
/// <para>
/// <b>默认不启用（零开销）</b>：<c>appsettings.json</c> <b>不写</b> <c>Cors</c> 节 → <see cref="AddAguiCors"/> 返回
/// <c>false</c>，既不调用 <c>AddCors</c> 也不调用 <c>UseCors</c>，请求管线与未引入本类时逐字节一致。开发期前端用
/// Vite dev proxy、生产用同域反向代理（浏览器视角同源），本就不需要 CORS——本类是「前端必须独立域名」时的兜底。
/// </para>
/// <para>
/// <b>启用时用白名单精确匹配</b>：<c>WithOrigins</c> 按「协议 + 域名 + 端口」完整字符串比对（不是前缀、不是通配）。
/// <b>禁用</b> <c>AllowAnyOrigin()</c> 与 <c>AllowCredentials()</c>：AG-UI 的身份走请求体 <c>forwardedProps</c>
/// 而非 Cookie，不需要凭据；不需要凭据就不该放开任意源（二者亦不得同用——ASP.NET Core 构建策略时会抛异常）。
/// 若未来某变更引入 Cookie/凭据，<b>必须</b>继续用 <c>WithOrigins</c> 精确白名单，不得改成 <c>AllowAnyOrigin()</c>。
/// </para>
/// </summary>
internal static class AguiCors
{
    /// <summary>命名策略名：中间件式 <c>UseCors(PolicyName)</c>（全站生效），不用端点级 <c>[EnableCors]</c>。</summary>
    internal const string PolicyName = "AguiClient";

    /// <summary>白名单配置键。键缺失 / 空数组 / 全为空白项 = 不启用 CORS（默认态）。</summary>
    internal const string ConfigKey = "Cors:AllowedOrigins";

    /// <summary>
    /// 读白名单并注册命名策略 <see cref="PolicyName"/>，返回「是否已启用」供 Program 决定是否调用 <c>UseCors</c>。
    /// <para>
    /// 无有效项（键缺失 / 空数组 / 全为空白项）→ <b>不调用</b> <c>AddCors</c> 并返回 <c>false</c>；
    /// 有有效项时先做启动期形态校验（fail-fast，见 <see cref="ValidateOrigins"/>）再注册。
    /// </para>
    /// </summary>
    internal static bool AddAguiCors(this IServiceCollection services, IConfiguration config)
    {
        var origins = ReadAllowedOrigins(config);
        if (origins.Length == 0)
            return false;

        ValidateOrigins(origins);

        // 命名策略：仅白名单 origin 可跨源 + 任意方法与任意请求头（AG-UI 的 POST application/json 与 GET /models
        // 都会触发预检，需放行 content-type 等头）；刻意不加 AllowCredentials / AllowAnyOrigin（见类注释）。
        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader()));

        return true;
    }

    /// <summary>读 <see cref="ConfigKey"/>，逐项 Trim 后过滤空白项（配置里的空串 / 纯空白项不作数）。</summary>
    private static string[] ReadAllowedOrigins(IConfiguration config)
        => config.GetSection(ConfigKey).Get<string[]>()?
            .Select(origin => origin.Trim())
            .Where(origin => origin.Length > 0)
            .ToArray()
           ?? [];

    /// <summary>
    /// 启动期形态校验（fail-fast）：每项须可 <c>Uri.TryCreate(..., UriKind.Absolute)</c> 且 scheme ∈ {http, https}。
    /// <para>
    /// 必要性：<c>WithOrigins</c> 是<b>精确字符串匹配</b>——写 <c>*</c>、写缺 scheme 的 <c>localhost:5173</c>、
    /// 或末尾多一个 <c>/</c>（浏览器发出的 <c>Origin</c> 头不带尾部斜杠）都不会在运行时报错，只会「永不匹配」；
    /// 静默失效比启动即抛更难排查，故把可判定的形态错误提前到启动期。
    /// </para>
    /// </summary>
    private static void ValidateOrigins(IReadOnlyList<string> origins)
    {
        foreach (var origin in origins)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"配置 {ConfigKey} 中的「{origin}」不是合法 origin：必须是含协议与端口、无末尾斜杠的绝对地址"
                + "（如 http://localhost:5173）。WithOrigins 为精确字符串匹配，写 * 或省略协议只会「永不匹配」而静默失效。");
        }
    }
}
