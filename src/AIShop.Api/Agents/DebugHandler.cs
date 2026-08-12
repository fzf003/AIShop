using System.Text;
using Serilog;

namespace AIShop.Api.Agents;

/// <summary>
/// 调试 Handler：拦截 MEAI 发给 LLM 的 HTTP 请求，打印完整请求/响应体。
/// 用于排查非 OpenAI 模型（千问、DeepSeek 等）在 MAF/MEAI 中工具调用不正常的原因。
/// 默认关闭（enabled=false）：透明转发，不打印、不读流；经 <c>AgentTelemetry:DebugHandler=true</c> 开启。
/// </summary>
public sealed class DebugHandler : DelegatingHandler
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DebugHandler>();
    private readonly bool _enabled;

    /// <summary>默认关闭：透明转发，不打印、不读流。</summary>
    public DebugHandler() : this(enabled: false)
    {
    }

    public DebugHandler(bool enabled) : this(new HttpClientHandler { UseProxy = false, Proxy = null }, enabled)
    {
    }

    public DebugHandler(HttpMessageHandler inner, bool enabled) : base(inner)
    {
        _enabled = enabled;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 未启用：透明转发，不打印请求/响应体、不读流
        if (!_enabled)
            return await base.SendAsync(request, cancellationToken);

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);

            // 打印完整请求体到控制台
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("");
            Console.WriteLine("══════════════  MEAI → LLM 请求体 ═══════════════");
            Console.ResetColor();
            Console.WriteLine(body);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("══════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine("");

            // 同时写 Serilog（会进日志文件）
            Log.Information("MEAI→LLM 请求体: {Body}", body);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content is not null)
        {
            var respBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("");
            Console.WriteLine("══════════════  LLM → MEAI 响应体 ═══════════════");
            Console.ResetColor();
            Console.WriteLine(respBody.Length > 2000 ? respBody[..2000] + "\n... (截断)" : respBody);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("══════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine("");
            Log.Information("LLM→MEAI 响应体: {Body}", respBody.Length > 3000 ? respBody[..3000] + "...(截断)" : respBody);
        }

        return response;
    }
}
