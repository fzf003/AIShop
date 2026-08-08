using AIShop.AgentTelemetry;
// 命名空间 AIShop.AgentTelemetry 与其中的静态类 AgentTelemetry 同名，
// C# 遮蔽规则下「AgentTelemetry」解析为命名空间而非类，故加别名引用静态类。
using AgentTelemetryHelper = AIShop.AgentTelemetry.AgentTelemetry;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Reflection;
using AIShop.Api.Agents;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIShop.Api.Tests;

public sealed class AgentTelemetryTests
{
    // =========================================================
    // T9 — AgentTelemetry.Instrument 按级别包装 Agent
    // 对应 spec「Instrument 按级别包装 Agent」+「EnableSensitiveData 与采集级别联动」。
    // 用 NSubstitute mock IChatClient 构造最小 HarnessAgent，不发起真实 LLM 调用。
    // =========================================================

    [Fact]
    public void ShouldThrowArgumentNullException_WhenAgentIsNull()
    {
        // 入参校验：null agent 必须 fail fast（ArgumentNullException.ThrowIfNull）
        Assert.Throws<ArgumentNullException>(() =>
            AgentTelemetryHelper.Instrument(null!, "Test.Source", AgentTelemetryLevel.None));
    }

    [Fact]
    public void ShouldReturnSameInstanceWithoutDecoration_WhenLevelIsNone()
    {
        var agent = CreateMinimalHarnessAgent();

        var result = AgentTelemetryHelper.Instrument(agent, "Test.Source", AgentTelemetryLevel.None);

        // None 级别裸返回，不包任何装饰器：同一引用、类型名不含 OpenTelemetryAgent
        Assert.Same(agent, result);
        Assert.DoesNotContain("OpenTelemetryAgent", result.GetType().Name);
    }

    [Theory]
    [InlineData(AgentTelemetryLevel.Metadata)]
    [InlineData(AgentTelemetryLevel.MetadataAndContent)]
    public async Task ShouldWrapWithOpenTelemetryAgent_AndRemainCallable(AgentTelemetryLevel level)
    {
        var agent = CreateMinimalHarnessAgent();

        var result = AgentTelemetryHelper.Instrument(agent, "Test.Source", level);

        // 非 None 级别经 AsBuilder().UseOpenTelemetry(...).Build() 装饰，返回 OpenTelemetryAgent
        Assert.Contains("OpenTelemetryAgent", result.GetType().Name);

        // 装饰后仍可正常调用：mock IChatClient 返回固定文本，不触真实 LLM
        var session = await result.CreateSessionAsync();
        var response = await result.RunAsync("你好", session);
        Assert.Equal("ok", response.Text);
    }

    /// <summary>
    /// 用 NSubstitute mock IChatClient 构造最小 HarnessAgent（不触发真实 LLM 调用）。
    /// </summary>
    private static AIAgent CreateMinimalHarnessAgent()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        return new HarnessAgent(chatClient);
    }

    // =========================================================
    // T10 — EnableSensitiveData 与采集级别联动 + Options 默认值
    // 对应 spec「EnableSensitiveData 与采集级别联动」+「默认采集级别为 Metadata」。
    // 决策点：EnableSensitiveData 是 OpenTelemetryAgent 的公开属性（反射确认 getter/setter 均 public），
    // 且 UseOpenTelemetry().Build() 直接返回 OpenTelemetryAgent 实例，故断言精确类型后读公开属性，
    // 比反射遍历包装器私有字段对象图更稳健（不依赖 MAF 内部字段名），
    // 仅在属性非公开 / 类型被二次包装时才需要退化到反射遍历。
    // =========================================================

    [Fact]
    public void ShouldSetEnableSensitiveDataFalse_WhenLevelIsMetadata()
    {
        var agent = CreateMinimalHarnessAgent();

        var wrapped = AgentTelemetryHelper.Instrument(agent, "Test.Source", AgentTelemetryLevel.Metadata);

        // Metadata 级别生产安全：只采集元数据，不采集消息内容 / 工具参数 / 工具结果
        var otelAgent = Assert.IsType<OpenTelemetryAgent>(wrapped);
        Assert.False(otelAgent.EnableSensitiveData);
    }

    [Fact]
    public void ShouldSetEnableSensitiveDataTrue_WhenLevelIsMetadataAndContent()
    {
        var agent = CreateMinimalHarnessAgent();

        var wrapped = AgentTelemetryHelper.Instrument(agent, "Test.Source", AgentTelemetryLevel.MetadataAndContent);

        // MetadataAndContent 级别调试用：额外采集消息内容 / 工具参数 / 工具结果
        var otelAgent = Assert.IsType<OpenTelemetryAgent>(wrapped);
        Assert.True(otelAgent.EnableSensitiveData);
    }

    [Fact]
    public void ShouldDefaultToMetadataLevelAndNullSourceName_WhenNoConfiguration()
    {
        // new AgentTelemetryOptions() 默认 Level==Metadata（生产安全）、SourceName==null（用框架默认）
        var options = new AgentTelemetryOptions();

        Assert.Equal(AgentTelemetryLevel.Metadata, options.Level);
        Assert.Null(options.SourceName);
    }

    // =========================================================
    // T11 — AgentTelemetryOptions 配置绑定
    // 对应 spec「配置节驱动采集级别」/「默认采集级别为 Metadata」，
    // 与 Program.cs 同一绑定路径：Configure<AgentTelemetryOptions>(section) + IOptions.Value。
    // =========================================================

    [Fact]
    public void ShouldBindMetadataAndContent_WhenLevelValueIsLowercase()
    {
        // 枚举名大小写不敏感：小写 "metadataandcontent" 绑定后应为 MetadataAndContent
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AgentTelemetry:Level"] = "metadataandcontent",
        });

        Assert.Equal(AgentTelemetryLevel.MetadataAndContent, options.Level);
        // 仅配置了 Level，SourceName 未配置时应为 null（使用框架默认）
        Assert.Null(options.SourceName);
    }

    [Fact]
    public void ShouldBindMetadata_WhenLevelValueIsPascalCase()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AgentTelemetry:Level"] = "Metadata",
        });

        Assert.Equal(AgentTelemetryLevel.Metadata, options.Level);
        Assert.Null(options.SourceName);
    }

    [Fact]
    public void ShouldThrow_WhenLevelValueIsInvalidEnum()
    {
        // 非法枚举字符串走配置绑定异常（fail fast），不额外写校验逻辑
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentTelemetry:Level"] = "bogus",
            })
            .Build();
        var section = config.GetSection("AgentTelemetry");

        var services = new ServiceCollection();
        services.Configure<AgentTelemetryOptions>(section);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<AgentTelemetryOptions>>();

        Assert.Throws<InvalidOperationException>(() => _ = options.Value);
    }

    /// <summary>
    /// 以与 Program.cs 相同的绑定路径（配置节 + Options 模式）绑定 AgentTelemetryOptions。
    /// </summary>
    private static AgentTelemetryOptions BindOptions(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var section = config.GetSection("AgentTelemetry");

        var services = new ServiceCollection();
        services.Configure<AgentTelemetryOptions>(section);
        using var sp = services.BuildServiceProvider();

        return sp.GetRequiredService<IOptions<AgentTelemetryOptions>>().Value;
    }

    // =========================================================
    // T13 — ShoppingAssistantAgent 接入 Instrument + RunChatAsync 行为不变
    // 对应 spec「ShoppingAssistantAgent 接入 Instrument」。
    // 直接构造 ShoppingAssistantAgent（NSubstitute mock 全部依赖），
    // 反射断言私有 _agent 字段已被 Instrument 包装为 OpenTelemetryAgent，
    // 且 IShoppingAssistantAgent.RunChatAsync 行为与包装前一致。
    // =========================================================

    [Fact]
    public async Task ShouldWrapInternalAgentWithOpenTelemetry_WhenConstructedWithMetadata()
    {
        using var fixture = new ShoppingAssistantAgentFixture();

        var agent = fixture.CreateAgent(AgentTelemetryLevel.Metadata);

        // 构造即被 AgentTelemetry.Instrument 包装：私有 _agent 字段类型名含 OpenTelemetryAgent
        var internalAgent = agent.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(agent);
        Assert.NotNull(internalAgent);
        Assert.Contains("OpenTelemetryAgent", internalAgent.GetType().Name);

        // 行为不变：mock IChatClient 返回固定 JSON 回复，RunChatAsync 原样返回
        var sessionId = Guid.NewGuid();
        CartToolProvider.SetCurrentUser("t13-user");
        var (result, _) = await agent.RunChatAsync(sessionId, "帮我推荐跑鞋", "t13-user");

        Assert.NotNull(result);
        Assert.Equal("模拟推荐", result.Reply);
        Assert.Equal("跑步", Assert.Single(result.Keywords));
    }

    [Fact]
    public async Task ShouldReturnSameReply_WhenConstructedWithMetadataAndContent()
    {
        using var fixture = new ShoppingAssistantAgentFixture();

        // MetadataAndContent 级别：Instrument 同样包装 _agent 为 OpenTelemetryAgent
        var agent = fixture.CreateAgent(AgentTelemetryLevel.MetadataAndContent);

        var internalAgent = agent.GetType().GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(agent);
        Assert.NotNull(internalAgent);
        Assert.Contains("OpenTelemetryAgent", internalAgent.GetType().Name);

        var sessionId = Guid.NewGuid();
        CartToolProvider.SetCurrentUser("t13-user");
        var (result, _) = await agent.RunChatAsync(sessionId, "查看购物车", "t13-user");

        Assert.NotNull(result);
        Assert.Equal("模拟推荐", result.Reply);
    }

    /// <summary>
    /// 直接构造 ShoppingAssistantAgent 的最小夹具：
    /// mock IChatClient（返回固定 JSON 回复）、SQLite 内存库（EnsureCreated）、
    /// mock IDbContextFactory / IProductCatalogService / CartToolProvider。
    /// </summary>
    private sealed class ShoppingAssistantAgentFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly IChatClient _chatClient;
        private readonly IProductCatalogService _catalog;
        private readonly CartToolProvider _cartTools;
        private readonly IServiceScopeFactory _scopeFactory;

        public ShoppingAssistantAgentFixture()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;

            using (var seed = new AppDbContext(options))
            {
                seed.Database.EnsureCreated();
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

            _chatClient = Substitute.For<IChatClient>();
            _chatClient.GetResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"Reply\":\"模拟推荐\",\"Keywords\":[\"跑步\"],\"Preferences\":[]}")));
            _chatClient.GetStreamingResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

            _catalog = Substitute.For<IProductCatalogService>();
            _catalog.KeywordMap.Returns(new Dictionary<string, string[]>());

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
            _scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            _cartTools = new CartToolProvider(_scopeFactory);
        }

        public ShoppingAssistantAgent CreateAgent(AgentTelemetryLevel level)
            => new(_chatClient, _dbFactory, _catalog, _cartTools, isOpenAI: false,
                new AgentTelemetryOptions { Level = level });

        public void Dispose()
        {
            _connection.Close();
            _connection.Dispose();
        }
    }
}
