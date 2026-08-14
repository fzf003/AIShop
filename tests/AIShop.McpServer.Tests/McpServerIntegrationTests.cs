using System.Net.Http.Json;
using System.Text.Json;
using AIShop.Core.StaticData;
using AIShop.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIShop.McpServer.Tests;

/// <summary>
/// McpServer 端到端集成测试。
/// T8 起 ProductCatalog.All 走 ProductRepository 查库，隔离库需 EnsureCreated + 播种 18 商品，
/// 否则 match_products 抛 "no such table: Products"（与 T23-pre 同模式）。
/// </summary>
public sealed class McpServerIntegrationTests : IClassFixture<McpServerIntegrationTests.TestMcpServerFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpServerIntegrationTests(TestMcpServerFactory factory)
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
        Assert.Contains("Healthy", body);
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
        // The server serializes names with escapes like 专业跑鞋 (= 专业跑鞋)
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

    /// <summary>
    /// 测试用 WebApplicationFactory：隔离临时文件库 + EnsureCreated + 播种 18 商品。
    /// T8 改查库后 match_products 走 ProductCatalog.All → ProductRepository.GetAll() 查库，
    /// 无 Products 表的空库会抛 "no such table"。
    /// </summary>
    public sealed class TestMcpServerFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mcp_test_{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<AppDbContext>();

                var connStr = $"Data Source={_dbPath}";
                services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connStr));
                services.AddScoped<AppDbContext>(sp =>
                    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

                // 播种 18 商品（与 ChatEndpointsTests.ReplaceWithIsolatedDb 同模式：独立上下文构造，不 BuildServiceProvider）
                using var seedCtx = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options);
                seedCtx.Database.EnsureCreated();
                seedCtx.Products.AddRange(ProductSeedData.Products);
                seedCtx.SaveChanges();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // SQLite 文件可能被连接池短暂锁定，忽略清理异常
            }
        }
    }
}
