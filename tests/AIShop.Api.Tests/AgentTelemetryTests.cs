using AIShop.AgentTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIShop.Api.Tests;

public sealed class AgentTelemetryTests
{
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
