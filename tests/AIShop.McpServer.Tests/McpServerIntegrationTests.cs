using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AIShop.McpServer.Tests;

public sealed class McpServerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpServerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static async Task<(HttpClient client, string sessionId)> InitializeSessionAsync(HttpClient client)
    {
        var initMsg = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 0,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "test-client", version = "1.0.0" }
                }
            })
        };
        initMsg.Headers.Add("Accept", "text/event-stream, application/json");

        var initResp = await client.SendAsync(initMsg);
        initResp.EnsureSuccessStatusCode();

        var sessionId = initResp.Headers.GetValues("Mcp-Session-Id").FirstOrDefault();
        Assert.NotNull(sessionId);

        await initResp.Content.ReadAsStringAsync();

        // Send initialized notification
        var notifyMsg = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" })
        };
        notifyMsg.Headers.Add("Mcp-Session-Id", sessionId);
        notifyMsg.Headers.Add("Accept", "text/event-stream, application/json");

        var notifyResp = await client.SendAsync(notifyMsg);
        notifyResp.EnsureSuccessStatusCode();

        return (client, sessionId);
    }

    private static async Task<JsonElement> SendMcpRequest(HttpClient client, string sessionId, object request)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(request)
        };
        msg.Headers.Add("Mcp-Session-Id", sessionId);
        msg.Headers.Add("Accept", "text/event-stream, application/json");

        var resp = await client.SendAsync(msg);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();

        // Parse SSE event stream: extract JSON from "event: message\ndata: {...}"
        if (body.StartsWith("event:", StringComparison.Ordinal))
        {
            var dataPrefix = "data: ";
            var dataStart = body.IndexOf(dataPrefix, StringComparison.Ordinal);
            if (dataStart >= 0)
            {
                var jsonStart = body.IndexOf('{', dataStart);
                if (jsonStart >= 0)
                {
                    body = body[jsonStart..];
                }
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    [Fact]
    public async Task HealthCheck_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }

    [Fact]
    public async Task ToolsList_ReturnsMatchProducts()
    {
        var client = _factory.CreateClient();
        var (_, sessionId) = await InitializeSessionAsync(client);

        var json = await SendMcpRequest(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list"
        });

        var tools = json.GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Contains("match_products", names);
    }

    [Fact]
    public async Task ToolsCall_MatchProducts_ReturnsResults()
    {
        var client = _factory.CreateClient();
        var (_, sessionId) = await InitializeSessionAsync(client);

        var json = await SendMcpRequest(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "match_products",
                arguments = new { keywords = new[] { "运动" } }
            }
        });

        var content = json.GetProperty("result").GetProperty("content")[0];
        var text = content.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.NotEmpty(text);
        // The JSON text contains Unicode-escaped Chinese characters in the form \uXXXX
        // The server serializes names with escapes like \u4E13\u4E1A\u8DD1\u978B (= 专业跑鞋)
        Assert.Contains("\\u4E13\\u4E1A\\u8DD1\\u978B", text);
    }

    [Fact]
    public async Task ToolsCall_EmptyKeywords_ReturnsEmptyArray()
    {
        var client = _factory.CreateClient();
        var (_, sessionId) = await InitializeSessionAsync(client);

        var json = await SendMcpRequest(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "match_products",
                arguments = new { keywords = Array.Empty<string>() }
            }
        });

        var text = json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("[]", text);
    }

    [Fact]
    public async Task ToolsCall_InvalidTool_ReturnsError()
    {
        var client = _factory.CreateClient();
        var (_, sessionId) = await InitializeSessionAsync(client);

        var json = await SendMcpRequest(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "nonexistent_tool",
                arguments = new { }
            }
        });

        Assert.True(json.TryGetProperty("error", out _));
    }
}
