#pragma warning disable MAAI001 // ContextWindowCompactionStrategy / CompactionProvider 为 MAF [Experimental]
using AIShop.AgentTelemetry;
using AIShop.AguiHost;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Services;
using AIShop.Service;
using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S1 共享压缩策略单一来源测试：<c>AguiCompaction</c> 是 AguiHost 压缩阈值的唯一来源，
/// DI 注册单例后 Agent 装配注入同一实例（design §4.5）。对应 spec「压缩 MUST 复用装配同一
/// <see cref="ContextWindowCompactionStrategy"/> 实例」与「装配不回退」。
/// </summary>
public sealed class AguiCompactionTests
{
    /// <summary>期望阈值（对齐老 ShoppingAssistantAgent L140；spec R1 / design §4.5）。</summary>
    private const int ExpectedMaxContextWindowTokens = 128000;
    private const int ExpectedMaxOutputTokens = 16384;
    private const double ExpectedToolEvictionThreshold = 0.5;
    private const double ExpectedTruncationThreshold = 0.8;

    private static CartToolProvider CreateCartTools()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        return new CartToolProvider(scopeFactory, accessor);
    }

    [Fact]
    public void CreateStrategy_ReturnsContextWindowCompactionStrategy_WithExpectedThresholds()
    {
        // 单一来源工厂：返回具体 ContextWindowCompactionStrategy，且四项阈值精确对齐老 Agent
        // （MaxOutputTokens / ToolEvictionThreshold / TruncationThreshold 等公开 get 属性可直接断言）。
        var strategy = AguiCompaction.CreateStrategy();

        Assert.IsType<ContextWindowCompactionStrategy>(strategy);
        Assert.Equal(ExpectedMaxContextWindowTokens, strategy.MaxContextWindowTokens);
        Assert.Equal(ExpectedMaxOutputTokens, strategy.MaxOutputTokens);
        Assert.Equal(ExpectedToolEvictionThreshold, strategy.ToolEvictionThreshold);
        Assert.Equal(ExpectedTruncationThreshold, strategy.TruncationThreshold);

        // 常量与工厂同源：类常量即构造实参，二者不得分叉
        Assert.Equal(ExpectedMaxContextWindowTokens, AguiCompaction.MaxContextWindowTokens);
        Assert.Equal(ExpectedMaxOutputTokens, AguiCompaction.MaxOutputTokens);
        Assert.Equal(ExpectedToolEvictionThreshold, AguiCompaction.ToolEvictionThreshold);
        Assert.Equal(ExpectedTruncationThreshold, AguiCompaction.TruncationThreshold);
    }

    [Fact]
    public void AddAguiBaseServices_RegistersCompactionStrategySingleton_WithSameThresholds()
    {
        // DI 单例接线：AddAguiBaseServices 后可解析 ContextWindowCompactionStrategy，且与
        // AguiCompaction.CreateStrategy() 同型同参；两次解析返回同一实例（确为 Singleton，供
        // Program keyed factory 与 S4 store 侧压缩共享，阈值不可能分叉）。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:qwen:Endpoint"] = "https://example.com/v1",
                ["Models:qwen:Model"] = "qwen3-test",
                ["Models:qwen:Key"] = "test-key",
                ["ActiveModel"] = "qwen",
                ["AgentTelemetry:Level"] = "Metadata",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddAguiBaseServices(config, "Data Source=agui.db", "Data Source=agui.rag.db");
        using var sp = services.BuildServiceProvider();

        var fromDi = sp.GetRequiredService<ContextWindowCompactionStrategy>();
        var secondResolution = sp.GetRequiredService<ContextWindowCompactionStrategy>();

        // 单例语义：同一实例（不是每次解析新建）
        Assert.Same(fromDi, secondResolution);

        // 同型同参：与独立调用工厂的产物阈值一致（DI 未改写阈值）
        Assert.IsType<ContextWindowCompactionStrategy>(fromDi);
        var fresh = AguiCompaction.CreateStrategy();
        Assert.Equal(fresh.MaxContextWindowTokens, fromDi.MaxContextWindowTokens);
        Assert.Equal(fresh.MaxOutputTokens, fromDi.MaxOutputTokens);
        Assert.Equal(fresh.ToolEvictionThreshold, fromDi.ToolEvictionThreshold);
        Assert.Equal(fresh.TruncationThreshold, fromDi.TruncationThreshold);
    }

    [Fact]
    public void Create_WithInjectedCompactionStrategy_AttachesExactlyOneCompactionProvider_WithExplicitStateKey()
    {
        // 注入共享策略后装配不回退：恰挂 1 个 CompactionProvider，stateKey 仍为 "AGUIShopping-Compaction"。
        var injected = AguiCompaction.CreateStrategy();

        var agent = AGUIShoppingAgent.Create(
            Substitute.For<IChatClient>(),
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            compactionStrategy: injected);

        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(chatClientAgent.AIContextProviders);
        var compactionProviders = chatClientAgent.AIContextProviders.OfType<CompactionProvider>().ToList();
        Assert.Single(compactionProviders);
        Assert.Equal(new[] { "AGUIShopping-Compaction" }, compactionProviders[0].StateKeys);
    }

    [Fact]
    public void Create_WithoutCompactionStrategy_StillAttachesCompactionProvider()
    {
        // 源码兼容：不传 compactionStrategy 的直构调用点仍可用，内部回退 AguiCompaction.CreateStrategy()，
        // 装配面与注入路径等价（恰挂 1 个 CompactionProvider + 显式 stateKey）。
        var agent = AGUIShoppingAgent.Create(
            Substitute.For<IChatClient>(),
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None });

        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(chatClientAgent.AIContextProviders);
        var compactionProviders = chatClientAgent.AIContextProviders.OfType<CompactionProvider>().ToList();
        Assert.Single(compactionProviders);
        Assert.Equal(new[] { "AGUIShopping-Compaction" }, compactionProviders[0].StateKeys);
    }
}
