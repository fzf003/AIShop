using System.Reflection;
using AIShop.AgentTelemetry;
using AIShop.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Api.Tests;

/// <summary>
/// ModelRouterTests 拆类迁移（service-layer-extraction T15）：WebApplicationFactory&lt;Program&gt; 集成段。
/// 依赖 Api 的 Program 装配（端点映射、DI 注册、ServiceDefaults），留在 Api.Tests 形成
/// Api.Tests → Api → Service 合法依赖链。
/// </summary>
public sealed class ModelRouterWebTests
{
    // ============ WebApplicationFactory 集成测试 ============

    /// <summary>
    /// 场景 A：仅包含 "OpenAI" 节，无 "Models" 节。
    /// 使用 WebApplicationFactory 启动完整的 DI 容器，验证旧配置向后兼容。
    /// </summary>
    [Fact]
    public void LegacyOpenAIConfig_WithWebApplicationFactory_ReturnsOneModelAndDefaultAgent()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    // 清空所有配置源，仅保留旧版 "OpenAI" 节
                    config.Sources.Clear();
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OpenAI:Endpoint"] = "https://test.endpoint/v1",
                        ["OpenAI:Key"] = "test-key",
                        ["OpenAI:Model"] = "gpt-4",
                    });
                });
            });

        // Act
        var router = factory.Services.GetRequiredService<ModelRouter>();
        var models = router.GetAvailableModels().ToList();
        var agent = router.GetDefaultAgent();

        // Assert
        Assert.Single(models);
        Assert.Equal("legacy", models[0].Id);
        Assert.False(string.IsNullOrEmpty(models[0].Name));
        Assert.NotNull(agent);
        Assert.IsAssignableFrom<IShoppingAssistantAgent>(agent);
    }

    /// <summary>
    /// 场景 B：既无 "Models" 节也无 "OpenAI" 节。
    /// 验证 ModelRouter 构造时抛出 InvalidOperationException。
    /// </summary>
    [Fact]
    public void NoConfig_WithWebApplicationFactory_ThrowsInvalidOperationException()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    // 清空所有配置源：既无 "OpenAI" 也无 "Models"
                    config.Sources.Clear();
                });
            });

        // Act & Assert：ModelRouter 因缺少配置在构造时抛出 InvalidOperationException
        Assert.Throws<InvalidOperationException>(() =>
            factory.Services.GetRequiredService<ModelRouter>());
    }

    // =========================================================
    // T13 — ShoppingAssistantAgent 接入 Instrument + ModelRouter 注入链路
    // 对应 spec「ModelRouter 注入遥测选项」+「配置节驱动采集级别」。
    // 用 WebApplicationFactory<Program> 走真实 DI 注册（T5 已绑定 "AgentTelemetry" 配置节），
    // 验证：默认配置（appsettings AgentTelemetry:Level=Metadata）下 GetDefaultAgent()
    // 返回的 agent 反射 _agent 含 OpenTelemetryAgent；WithWebHostBuilder 覆盖为
    // MetadataAndContent 后 EnableSensitiveData==true（仅改配置生效，无需重编译）。
    // =========================================================

    [Fact]
    public void DefaultAgent_WithDefaultConfig_InternalAgentIsOpenTelemetryWrapped()
    {
        // 默认 appsettings.json：AgentTelemetry:Level=Metadata（生产安全默认）
        using var factory = new WebApplicationFactory<Program>();

        var router = factory.Services.GetRequiredService<ModelRouter>();
        var agent = router.GetDefaultAgent();

        // 反射断言私有 _agent 字段已被 AgentTelemetry.Instrument 包装
        Assert.Contains("OpenTelemetryAgent", GetInternalAgentTypeName(agent));
    }

    [Fact]
    public void DefaultAgent_WithMetadataAndContentConfig_HasSensitiveDataEnabled()
    {
        // 覆盖 AgentTelemetry:Level=MetadataAndContent：无需重编译，仅改配置即生效
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AgentTelemetry:Level"] = "MetadataAndContent",
                    });
                });
            });

        var router = factory.Services.GetRequiredService<ModelRouter>();
        var agent = router.GetDefaultAgent();

        // Instrument 包装后返回的正是 OpenTelemetryAgent：EnableSensitiveData 应为 true
        var internalAgent = GetInternalAgent(agent);
        Assert.IsType<OpenTelemetryAgent>(internalAgent);
        Assert.True(((OpenTelemetryAgent)internalAgent).EnableSensitiveData);
    }

    /// <summary>反射读取 ShoppingAssistantAgent 私有 _agent 字段。</summary>
    private static object GetInternalAgent(IShoppingAssistantAgent agent)
    {
        var field = typeof(ShoppingAssistantAgent).GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = field?.GetValue(agent);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>反射读取私有 _agent 字段的类型名。</summary>
    private static string GetInternalAgentTypeName(IShoppingAssistantAgent agent)
        => GetInternalAgent(agent).GetType().Name;
}
