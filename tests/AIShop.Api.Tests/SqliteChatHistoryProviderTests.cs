#pragma warning disable MAAI001
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using AIShop.Infrastructure.Data;
using AIShop.Api.Agents;
using NSubstitute;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;
using CoreChatMessage = AIShop.Core.Entities.ChatMessage;

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

    private async Task<IReadOnlyList<AgentChatMessage>> InvokeProvideAsync()
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokingContext(agent, _session,
            new[] { new AgentChatMessage(ChatRole.User, "Hello") });

        var result = ProvideMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask<IEnumerable<AgentChatMessage>>)result!;
        return (await valueTask).ToList();
    }

    private async Task InvokeStoreAsync()
    {
        var agent = Substitute.For<AIAgent>();
        var context = new ChatHistoryProvider.InvokedContext(agent, _session,
            new[] { new AgentChatMessage(ChatRole.User, "Hello") },
            new[] { new AgentChatMessage(ChatRole.Assistant, "Hi") });

        var result = StoreMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask)result!;
        await valueTask;
    }

    [Fact]
    public async Task Provide_NoHistory_ReturnsEmpty()
    {
        var result = await InvokeProvideAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task Provide_WithHistory_ReturnsMessages()
    {
        SeedMessages(3);

        var result = await InvokeProvideAsync();

        // MaxMessages = 20, all 3 messages fit within limit
        Assert.Equal(3, result.Count);
        Assert.Contains(result, m => m.Text == "消息0");
        Assert.Contains(result, m => m.Text == "消息1");
        Assert.Contains(result, m => m.Text == "消息2");
    }

    [Fact]
    public async Task Provide_LimitToMaxReadMessages()
    {
        SeedMessages(25);

        var result = await InvokeProvideAsync();

        // MaxReadMessages = 30, 25 条全部在窗口内
        Assert.Equal(25, result.Count);
    }

    [Fact]
    public async Task Store_AppendsThenTrimsToStoredLimit()
    {
        SeedMessages(15);

        await InvokeStoreAsync();

        using (var ctx = await _dbFactory.CreateDbContextAsync())
        {
            // 追加 2 条 → 共 17，MaxStoredMessages=50 不裁剪
            // 新增的 2 条在 SaveChangesAsync 时持久化
            var count = await ctx.ChatMessages
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
            // 追加 2 条 → 共 57，55 条中删 7（Skip 50），保留 50 + 2 新增
            var count = await ctx.ChatMessages
                .Where(m => m.SessionId == _sessionId)
                .CountAsync();
            Assert.Equal(52, count);
        }
    }

    private void SeedMessages(int count)
    {
        using var ctx = _dbFactory.CreateDbContext();
        for (int i = 0; i < count; i++)
        {
            ctx.ChatMessages.Add(new CoreChatMessage
            {
                SessionId = _sessionId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"消息{i}",
                SourceType = "agent",
                Timestamp = DateTime.UtcNow.AddMinutes(i)
            });
        }
        ctx.SaveChanges();
    }

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
