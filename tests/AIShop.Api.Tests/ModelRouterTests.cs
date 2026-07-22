using System;
using System.Collections.Generic;
using System.Linq;
using AIShop.Api.Agents;
using Microsoft.Extensions.Configuration;
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
    public void GetAvailableModels_WithMultipleModels_ReturnsAll()
    {
        // Arrange: new-style "Models" section with 3 entries + ActiveModel
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

        // Assert
        Assert.Equal(3, models.Count);
        var defaultModel = models.Single(m => m.IsDefault);
        Assert.Equal("qwen", defaultModel.Id);

        // ModelInfo is a record with only Id, Name, IsDefault — no Key/Endpoint exposed
        foreach (var model in models)
        {
            Assert.NotNull(model.Id);
            Assert.NotNull(model.Name);
        }
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
}
