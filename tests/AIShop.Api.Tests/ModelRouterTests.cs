using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIShop.AgentTelemetry;
using AIShop.Api.Agents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Agents.AI;
using NSubstitute;

namespace AIShop.Api.Tests;

public sealed class ModelRouterTests
{
    [Fact]
    public void GetAvailableModels_WithLegacyOpenAIConfig_ReturnsSingleModel()
    {
        // Arrange: only old-style "OpenAI" section
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:Endpoint"] = "https://test.endpoint/v1",
                ["OpenAI:Key"] = "test-key",
                ["OpenAI:Model"] = "gpt-4",
            })
            .Build();

        var sp = Substitute.For<IServiceProvider>();
        var router = new ModelRouter(config, sp);

        // Act
        var models = router.GetAvailableModels().ToList();

        // Assert
        Assert.Single(models);
        Assert.Equal("legacy", models[0].Id);
    }

    [Fact]
    public void GetAvailableModels_DoesNotExposeSensitiveFields()
    {
        // Arrange: 3 models, "qwen" as ActiveModel
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:qwen:Endpoint"] = "https://qwen.test/v1",
                ["Models:qwen:Key"] = "qwen-key",
                ["Models:qwen:Model"] = "qwen3.7-plus",
                ["Models:qwen:Name"] = "Qwen 3.7",
                ["Models:gpt-4.1:Endpoint"] = "https://gpt.test/v1",
                ["Models:gpt-4.1:Key"] = "gpt-key",
                ["Models:gpt-4.1:Model"] = "gpt-4.1",
                ["Models:gpt-4.1:Name"] = "GPT 4.1",
                ["Models:deepseek:Endpoint"] = "https://deepseek.test/v1",
                ["Models:deepseek:Key"] = "deepseek-key",
                ["Models:deepseek:Model"] = "deepseek-chat",
                ["Models:deepseek:Name"] = "DeepSeek Chat",
                ["ActiveModel"] = "qwen",
            })
            .Build();

        var sp = Substitute.For<IServiceProvider>();
        var router = new ModelRouter(config, sp);

        // Act
        var models = router.GetAvailableModels().ToList();

        // Assert: length 3
        Assert.Equal(3, models.Count);

        // Assert: each model serializes to exactly { id, name, isDefault } — no Key/Endpoint leak
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        var allowedKeys = new[] { "id", "name", "isDefault" };
        foreach (var model in models)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(model, jsonOptions);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
            Assert.NotNull(dict);
            Assert.Equal(3, dict.Count);
            Assert.All(allowedKeys, key => Assert.Contains(key, dict.Keys));
        }

        // Assert: isDefault: true matches ActiveModel
        var defaultModel = models.Single(m => m.IsDefault);
        Assert.Equal("qwen", defaultModel.Id);
    }

    [Fact]
    public void Constructor_WithNoConfig_ThrowsInvalidOperationException()
    {
        // Arrange: neither "Models" nor "OpenAI" section exists
        var config = new ConfigurationBuilder().Build();
        var sp = Substitute.For<IServiceProvider>();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new ModelRouter(config, sp));
    }

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
