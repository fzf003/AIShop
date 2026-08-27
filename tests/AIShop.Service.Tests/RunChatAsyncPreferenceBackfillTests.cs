#pragma warning disable MAAI001
using AIShop.AgentTelemetry;
using AIShop.Service;
using AIShop.Service.Tools;
using AIShop.Core.Entities;
using AIShop.Core.StaticData;
using AIShop.Core.Services;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// T16 — RunChatAsync 增加 preferences 参数 + StateBag 回填。
/// 验证链路：preferences 参数 → session.StateBag["Preferences"] →
/// PreferenceMemoryProvider.ProvideAIContextAsync 注入 Instructions →
/// HarnessAgent 将 Instructions 合入 ChatOptions.Instructions（LLM system prompt）。
/// 实测确认（probe）：偏好文本以「【已知用户偏好】…」块出现在捕获的 ChatOptions.Instructions 中。
/// 对应 spec「会话重建从数据库回填偏好」的 Agent 层回填环节（DB → 端点的完整路径由 T18 覆盖）。
/// </summary>
public sealed class RunChatAsyncPreferenceBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RunChatAsyncPreferenceBackfillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Database.EnsureCreated();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task RunChatAsync_WithPreferences_BackfillsStateBag_AndInjectsPreferenceTextIntoLlmInput()
    {
        // 预置 UserPreferences：用户已存在持久化偏好（权重 Top-3 为 咖啡/健身/音乐）
        var userId = Guid.NewGuid();
        using (var seed = new AppDbContext(_options))
        {
            seed.UserPreferences.Add(new UserPreferences
            {
                UserId = userId,
                KeywordsJson = """{"咖啡":3,"健身":2,"音乐":1}""",
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // 模拟 T18 端点从 DB 权重 Top-5 生成的顿号连接偏好文本
        var preferencesText = "咖啡、健身、音乐";

        // 捕获 mock IChatClient 收到的完整 LLM 输入（消息 + ChatOptions.Instructions）
        Meai.ChatOptions? capturedOptions = null;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOptions = ci.Arg<Meai.ChatOptions?>();
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                    """{"Reply":"模拟回复","Keywords":["跑步"],"Preferences":[]}"""));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory, new CurrentUserAccessor());

        var agent = new ShoppingAssistantAgent(
            mockClient, new ChatHistoryStore(dbFactory), new RoundBasedCompactionPolicy(), ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        var sessionId = Guid.NewGuid();
        var (result, _) = await agent.RunChatAsync(sessionId, "推荐商品", "t16-user", preferencesText);

        // Agent 正常返回（新签名 + 可选参数不破坏既有调用形态）
        Assert.NotNull(result);
        Assert.Equal("模拟回复", result.Reply);

        // PreferenceMemoryProvider 回填生效：LLM system prompt（ChatOptions.Instructions）包含偏好文本
        Assert.NotNull(capturedOptions);
        var instructions = capturedOptions.Instructions;
        Assert.NotNull(instructions);
        Assert.Contains("咖啡、健身、音乐", instructions);
        Assert.Contains("已知用户偏好", instructions);
    }

    [Fact]
    public async Task RunChatAsync_WithNullPreferences_DoesNotInjectPreferenceText()
    {
        // 无偏好（null）时 StateBag 不回填，LLM system prompt 不含偏好文本
        Meai.ChatOptions? capturedOptions = null;
        var mockClient = Substitute.For<Meai.IChatClient>();
        mockClient.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedOptions = ci.Arg<Meai.ChatOptions?>();
                return new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant,
                    """{"Reply":"无偏好回复","Keywords":[],"Preferences":[]}"""));
            });
        mockClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(), Arg.Any<Meai.ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<Meai.ChatResponseUpdate>());

        var dbFactory = Substitute.For<IDbContextFactory<AppDbContext>>();
        dbFactory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new AppDbContext(_options));
        dbFactory.CreateDbContext().Returns(_ => new AppDbContext(_options));

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cartTools = new CartToolProvider(scopeFactory, new CurrentUserAccessor());

        var agent = new ShoppingAssistantAgent(
            mockClient, new ChatHistoryStore(dbFactory), new RoundBasedCompactionPolicy(), ProductKeywordMap.Entries, cartTools,
            isOpenAI: false, new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        await agent.RunChatAsync(Guid.NewGuid(), "推荐商品", "t16-user", preferences: null);

        Assert.NotNull(capturedOptions);
        var instructions = capturedOptions.Instructions;
        Assert.NotNull(instructions);
        Assert.DoesNotContain("已知用户偏好", instructions);
        Assert.DoesNotContain("咖啡、健身、音乐", instructions);
    }
}
