using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIShop.Api.Features.Mcp;

/// <summary>
/// 通过 HTTP JSON-RPC 调用 MCP 服务的商品匹配客户端（同步请求-响应模式，非流式）。
/// 会话在首次调用时初始化，后续请求复用同一 session。
/// </summary>
public sealed class McpProductClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<McpProductClient> _logger;
    private string? _sessionId;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 序列化策略：CamelCase（JSON 标准命名风格）
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 忽略 null 值字段，减少传输体积
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public McpProductClient(HttpClient httpClient, ILogger<McpProductClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 调用 MCP 服务器的 match_products 工具，根据关键词匹配商品。
    /// </summary>
    /// <param name="keywords">要匹配的商品关键词数组</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>匹配结果数组，调用失败时返回空数组（静默降级）</returns>
    public async Task<ProductMatchResult[]> MatchProductsAsync(string[] keywords, CancellationToken ct = default)
    {
        // 空关键词直接返回空结果，避免无效的 MCP 调用
        if (keywords.Length == 0)
            return [];

        try
        {
            // 确保会话已初始化（首次调用时握手，后续复用）
            await EnsureSessionAsync(ct);
            // 调用 match_products 工具
            var resultJson = await CallToolAsync(_sessionId!, "match_products",
                new { keywords }, ct);
            return DeserializeResult(resultJson);
        }
        catch (Exception ex)
        {
            // 静默降级：MCP 调用失败时记录警告日志，返回空结果
            // 避免商品匹配异常影响主业务流程
            _logger.LogWarning(ex, "Failed to call MCP match_products with keywords {Keywords}", keywords);
            return [];
        }
    }

    /// <summary>
    /// 确保 MCP 会话已初始化，复用已有 session 避免重复握手。
    /// 线程安全：通过 SemaphoreSlim 确保并发调用只初始化一次。
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionId is not null) return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionId is not null) return; // Double-check
            _sessionId = await InitializeSessionAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// MCP 协议初始化握手：
    /// 1. 发送 initialize 请求（携带协议版本和能力声明）
    /// 2. 从响应头提取服务端分配的 sessionId
    /// 3. 发送 initialized 通知，告知服务端客户端已就绪
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>MCP 会话 ID，用于后续请求的身份标识</returns>
    private async Task<string> InitializeSessionAsync(CancellationToken ct)
    {
        // 构建 MCP initialize 请求体（JSON-RPC 格式）
        var request = new JsonRpcRequest
        {
            Id = 0,
            Method = "initialize",

            Params = new
            {
                protocolVersion = "2024-11-05", // MCP 协议版本
                capabilities = new { },          // 客户端能力声明（本客户端无特殊能力）
                clientInfo = new { name = "AIShop.Api", version = "1.0.0" }
            }
        };

        // 发送请求（初始化请求不带 sessionId）
        var response = await SendRequestAsync(request, null, ct);

        // 从响应头提取 MCP 服务端分配的 sessionId
        var sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : null;

        // sessionId 为空则说明 MCP 服务端未正确初始化
        if (string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("MCP server did not return a session ID");

        // 重要：必须消费 initialize 响应体后才能发送下一个请求（HTTP 协议约束）
        await response.Content.ReadAsStringAsync(ct);

        // 发送 initialized 通知，告知服务端客户端初始化完成
        // 这是一个 JSON-RPC Notification（无 Id），服务端无需回复
        await SendNotificationAsync(sessionId, "notifications/initialized", ct);

        return sessionId;
    }

    /// <summary>
    /// 调用 MCP 工具方法。
    /// 发送 tools/call 请求，解析 SSE 或纯 JSON 响应，返回结果文本。
    /// </summary>
    /// <param name="sessionId">MCP 会话 ID</param>
    /// <param name="toolName">要调用的工具名（如 match_products）</param>
    /// <param name="arguments">工具参数对象</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>工具返回的 JSON 文本字符串</returns>
    private async Task<string> CallToolAsync(string sessionId, string toolName, object arguments, CancellationToken ct)
    {
        // 构建 tools/call 请求体
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = "tools/call",
            Params = new
            {
                name = toolName,
                arguments
            }
        };

        var response = await SendRequestAsync(request, sessionId, ct);

        // 读取完整响应体
        var body = await response.Content.ReadAsStringAsync(ct);

        // MCP 服务端可能返回 SSE 格式流，需要从中提取 JSON 部分
        var json = ParseJsonFromSse(body);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 检查 JSON-RPC 错误响应
        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            throw new InvalidOperationException($"MCP tool error: {msg}");
        }

        // 从 result.content 数组中提取第一个 text 内容
        // MCP 工具返回格式：{ result: { content: [{ type: "text", text: "..." }] } }
        var content = root.GetProperty("result").GetProperty("content");
        if (content.GetArrayLength() == 0)
            return "[]";

        return content[0].GetProperty("text").GetString() ?? "[]";
    }

    /// <summary>
    /// 发送 JSON-RPC Notification（无 Id 字段，服务端无需回复）。
    /// 用于 initialized、cancel 等不需要返回结果的场景。
    /// </summary>
    private async Task SendNotificationAsync(string sessionId, string method, CancellationToken ct)
    {
        var request = new JsonRpcRequest
        {
            Method = method
        };

        await SendRequestAsync(request, sessionId, ct);
    }

    /// <summary>
    /// 底层 HTTP 请求发送方法。
    /// 统一封装：BaseAddress 校验、请求头设置、状态码检查。
    /// </summary>
    /// <param name="request">JSON-RPC 请求对象</param>
    /// <param name="sessionId">当前会话 ID（初始化时为 null）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>HTTP 响应消息</returns>
    private async Task<HttpResponseMessage> SendRequestAsync(JsonRpcRequest request, string? sessionId, CancellationToken ct)
    {
        // BaseAddress 必须在 DI 注册时配置（MCP 服务端地址）
        var baseUri = _httpClient.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress must be configured with the MCP server URL");

        var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/mcp"))
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        // 声明支持 SSE 流式响应（MCP 协议的推荐格式）
        msg.Headers.Add("Accept", "text/event-stream, application/json");

        // 初始化后的请求必须携带 sessionId，以便 MCP 服务端识别会话上下文
        if (sessionId != null)
            msg.Headers.Add("Mcp-Session-Id", sessionId);

        var response = await _httpClient.SendAsync(msg, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>
    /// 从 SSE（Server-Sent Events）格式响应中提取 JSON 部分。
    /// MCP 服务端可能返回 SSE 流（event: ... / data: ...），
    /// 也可能直接返回纯 JSON。此方法兼容两种格式。
    /// </summary>
    private static string ParseJsonFromSse(string body)
    {
        // 检测是否为 SSE 格式（以 "event:" 开头）
        if (body.StartsWith("event:", StringComparison.Ordinal))
        {
            // 从 data: 行提取 JSON 内容
            var dataPrefix = "data: ";
            var dataStart = body.IndexOf(dataPrefix, StringComparison.Ordinal);
            if (dataStart >= 0)
            {
                // 找到 data: 后的第一个 { 作为 JSON 起始位置
                var jsonStart = body.IndexOf('{', dataStart);
                if (jsonStart >= 0)
                    return body[jsonStart..];
            }
        }
        // 非 SSE 格式，直接返回原始文本
        return body;
    }

    /// <summary>
    /// 将 MCP 工具返回的 JSON 字符串反序列化为 ProductMatchResult 数组。
    /// </summary>
    private static ProductMatchResult[] DeserializeResult(string json)
    {
        return JsonSerializer.Deserialize<ProductMatchResult[]>(json, JsonOptions) ?? [];
    }

    /// <summary>
    /// JSON-RPC 2.0 请求的内部记录类型。
    /// - jsonrpc: 固定为 "2.0"（通过 JsonRpc 计算属性自动生成）
    /// - Id: 请求标识（notification 时为 null）
    /// - Method: 调用的方法名
    /// - Params: 方法参数（notification 时为 null）
    /// </summary>
    private sealed record JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
        public int? Id { get; init; }
        public string Method { get; init; } = "";
        public object? Params { get; init; }
    }
}
