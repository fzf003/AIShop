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
}
