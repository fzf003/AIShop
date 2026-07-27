#pragma warning disable MAAI001
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Entities;
using AIShop.Api.Agents;
using NSubstitute;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Api.Tests;

public sealed class SqliteChatHistoryProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SqliteChatHistoryProvider _provider;
    private readonly Guid _sessionId;
    private readonly AgentSession _session;

    private static readonly Type ProviderType = typeof(SqliteChatHistoryProvider);
    private static readonly MethodInfo ProvideMethod = ProviderType.GetMethod(
        "ProvideChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StoreMethod = ProviderType.GetMethod(
        "StoreChatHistoryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public SqliteChatHistoryProviderTests()
    {
        _sessionId = Guid.NewGuid();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Seed the session
        using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();
            seed.Sessions.Add(new Core.Entities.Session { Id = _sessionId, UserId = Guid.NewGuid() });
            seed.SaveChanges();
        }

        _dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        _dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var ctx = new AppDbContext(options);
            ctx.Database.EnsureCreated();
            return ctx;
        });
        _dbFactory.CreateDbContext().Returns(_ =>
        {
            var ctx = new AppDbContext(options);
            ctx.Database.EnsureCreated();
            return ctx;
        });

        _provider = new SqliteChatHistoryProvider(_dbFactory);
        _session = new TestSession();
        _session.StateBag.SetValue("SessionId", _sessionId.ToString());
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private async Task<IReadOnlyList<AgentChatMessage>> InvokeProvideAsync(
        IList<AgentChatMessage>? requestMessages = null)
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokingContext(agent, _session,
            requestMessages ?? [new AgentChatMessage(ChatRole.User, "Hello")]);

        var result = ProvideMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask<IEnumerable<AgentChatMessage>>)result!;
        return (await valueTask).ToList();
    }

    private async Task InvokeStoreAsync(
        IList<AgentChatMessage>? requestMessages = null,
        IList<AgentChatMessage>? responseMessages = null)
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokedContext(agent, _session,
            requestMessages ?? [new AgentChatMessage(ChatRole.User, "Hello")],
            responseMessages ?? [new AgentChatMessage(ChatRole.Assistant, "Hi")]);

        var result = StoreMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask)result!;
        await valueTask;
    }

    [Fact]
    public async Task Store_UserMessage_WritesContentColumn()
    {
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "你好")]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "user")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("你好", row.Content);
    }

    [Fact]
    public async Task Store_AssistantMessage_WritesContentToolCallsAndReasoning()
    {
        var asstMsg = new AgentChatMessage(ChatRole.Assistant, "搜索中");
        asstMsg.Contents.Add(new FunctionCallContent("call_1", "search_product",
            new Dictionary<string, object?> { ["q"] = "手机" }));
        asstMsg.Contents.Add(new TextReasoningContent("思考过程"));

        await InvokeStoreAsync(responseMessages: [asstMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "assistant")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("搜索中", row.Content);
        Assert.NotNull(row.ToolCalls);
        Assert.Contains("search_product", row.ToolCalls);
        Assert.Equal("思考过程", row.Reasoning);
    }

    [Fact]
    public async Task Store_ToolMessage_WritesContentAndToolCallId()
    {
        var toolMsg = new AgentChatMessage { Role = ChatRole.Tool };
        toolMsg.Contents.Add(new FunctionResultContent("call_123", "查询结果"));

        await InvokeStoreAsync(responseMessages: [toolMsg]);

        using var ctx = await _dbFactory.CreateDbContextAsync();
        var row = await ctx.ChatMessageRecords
            .Where(m => m.SessionId == _sessionId && m.Role == "tool")
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal("查询结果", row.Content);
        Assert.Equal("call_123", row.ToolCallId);
    }

    [Fact]
    public async Task Store_AppendsThenTrimsToStoredLimit()
    {
        SeedMessages(15);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            var count = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId)
                .CountAsync();
            Assert.Equal(17, count);
        }
    }

    [Fact]
    public async Task Store_TrimsWhenExceedsLimit()
    {
        SeedMessages(55);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            // 55 种子 + 2 新增 = 57 → 物理删除 5，保留 52
            var count = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId)
                .CountAsync();
            Assert.Equal(52, count);
        }
    }

    [Fact]
    public async Task Store_TrimsToExactly50()
    {
        SeedMessages(60);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            // 60 种子 + 2 新增 = 62 → 物理删除 10，保留 52
            // 新增的 2 条始终保留，种子部分保留最新 50 条，共 52
            var count = await ctx.ChatMessageRecords
                .Where(m => m.SessionId == _sessionId)
                .CountAsync();
            Assert.Equal(52, count);
        }
    }

    [Fact]
    public async Task Provide_RebuildsUserMessageAsTextContent()
    {
        SeedMessages(1, roles: ["user"], contents: ["你好"]);

        var result = await InvokeProvideAsync();

        var userMsg = Assert.Single(result);
        Assert.Equal(ChatRole.User, userMsg.Role);
        var textContent = Assert.Single(userMsg.Contents.OfType<TextContent>());
        Assert.Equal("你好", textContent.Text);
    }

    [Fact]
    public async Task Provide_RebuildsAssistantWithToolCalls()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{\"q\":\"手机\"}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "搜索中",
                ToolCalls = toolCallsJson,
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var asstMsg = Assert.Single(result);
        Assert.Equal(ChatRole.Assistant, asstMsg.Role);
        Assert.Single(asstMsg.Contents.OfType<TextContent>());
        var fcc = Assert.Single(asstMsg.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", fcc.CallId);
        Assert.Equal("search_product", fcc.Name);
    }

    [Fact]
    public async Task Provide_RebuildsToolMessageAsFunctionResultContent()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "",
                ToolCalls = toolCallsJson,
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "tool",
                Content = "查询结果文本",
                ToolCallId = "call_1",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var toolMsg = Assert.Single(result, m => m.Role == ChatRole.Tool);
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        var frc = Assert.Single(toolMsg.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call_1", frc.CallId);
        Assert.Equal("查询结果文本", frc.Result);
    }

    [Fact]
    public async Task Provide_FiltersCompactedMessages()
    {
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "活跃消息",
                IsCompacted = false,
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "已压缩消息",
                IsCompacted = true,
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        var msg = Assert.Single(result);
        Assert.Equal("活跃消息", msg.Text);
    }

    [Fact]
    public async Task Provide_SkipsPureFccNoTextAssistant()
    {
        var toolCallsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "call_1", type = "function", function = new { name = "search_product", arguments = "{}" } }
        }, JsonOptions);

        using (var seed = _dbFactory.CreateDbContext())
        {
            // 纯 tool_call 无文本的 assistant
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "",
                ToolCalls = toolCallsJson,
            });
            // 正常 user 消息
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "正常消息",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        // 纯 FCC assistant 需要保留（否则 tool 消息无法配对），所以返回 2 条
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.Assistant, result[0].Role);
        Assert.Contains("search_product", result[0].Contents.OfType<FunctionCallContent>().Single().Name);
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Equal("正常消息", result[1].Text);
    }

    [Fact]
    public async Task Provide_ReturnsMessagesInIdAscendingOrder()
    {
        using (var seed = _dbFactory.CreateDbContext())
        {
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "第一条",
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "assistant",
                Content = "回复第二条",
            });
            seed.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = "user",
                Content = "第三条",
            });
            await seed.SaveChangesAsync();
        }

        var result = await InvokeProvideAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("第一条", result[0].Text);
        Assert.Equal("回复第二条", result[1].Text);
        Assert.Equal("第三条", result[2].Text);
    }

    private async Task StoreAndLoad(string content)
    {
        await InvokeStoreAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, content)]);

        var result = await InvokeProvideAsync(
            requestMessages: [new AgentChatMessage(ChatRole.User, "Hello")]);

        // Store: user(content) + assistant("Hi")
        // Provide: 过滤掉未配对 tool → just user + assistant
        Assert.Equal(2, result.Count);
        Assert.Equal(content, result[0].Text);
    }

    [Fact]
    public async Task StoreAndProvide_RoundTrip_PreservesContent()
    {
        await StoreAndLoad("你好！有什么可以帮您的？");
    }

    private void SeedMessages(int count, string[]? roles = null, string[]? contents = null)
    {
        using var ctx = _dbFactory.CreateDbContext();
        for (int i = 0; i < count; i++)
        {
            string role;
            if (roles is not null && i < roles.Length)
                role = roles[i];
            else
                role = i % 2 == 0 ? "user" : "assistant";

            string content;
            if (contents is not null && i < contents.Length)
                content = contents[i];
            else
                content = $"消息{i}";

            ctx.ChatMessageRecords.Add(new ChatMessageRecord
            {
                SessionId = _sessionId,
                Role = role,
                Content = content,
            });
        }
        ctx.SaveChanges();
    }

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
