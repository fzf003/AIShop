using AIShop.AgentTelemetry;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.Api.Tests;

public sealed class AgentTelemetryWebTests
{
    // =========================================================
    // T14 — AgentTelemetryOptions DI 注册与默认级别（集成）
    // 对应 spec「配置节注册到 DI」+「默认采集级别为 MetadataAndContent」+「配置节驱动采集级别」。
    // 用 WebApplicationFactory<Program> 走 Program.cs 真实 DI 注册（Configure 绑定配置节 + AddSingleton），
    // 直接从容器解析 AgentTelemetryOptions 单例，验证：
    //   1) 默认配置（appsettings AgentTelemetry:Level=MetadataAndContent）下可解析且 Level==MetadataAndContent；
    //   2) 同一容器解析两次返回同一实例（单例语义）；
    //   3) WithWebHostBuilder 覆盖 Level=Metadata 后 Level==Metadata（配置覆盖生效，无需重编译）。
    // 注：单例语义验证在"覆盖"测试中进行，因为默认配置测试复用 WebApplicationFactory
    // 的 Provider 缓存，断言 ReferenceEquals 会跨测试共享实例，产生脆弱耦合。
    // =========================================================

    [Fact]
    public void Options_WithDefaultConfig_IsResolvableAndLevelIsMetadataAndContent()
    {
        // 默认 appsettings.json：AgentTelemetry:Level=MetadataAndContent（配合 Debug=true 排查，LLM 内容进 Aspire）
        using var factory = new WebApplicationFactory<Program>();

        // 直接从容器解析：DI 注册（Configure 绑定配置节 + AddSingleton）必须可解析
        var options = factory.Services.GetRequiredService<AgentTelemetryOptions>();

        // 默认 appsettings 生效：Level==MetadataAndContent、SourceName==null（用框架默认）
        Assert.Equal(AgentTelemetryLevel.MetadataAndContent, options.Level);
        Assert.Null(options.SourceName);
    }

    [Fact]
    public void Options_WithMetadataOverride_IsResolvableAndLevelIsMetadata()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    // 覆盖 AgentTelemetry:Level=Metadata：默认已是 MetadataAndContent，
                    // 覆盖回 Metadata 验证「配置节驱动采集级别」生效，无需重编译。
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AgentTelemetry:Level"] = "Metadata",
                    });
                });
            });

        var options = factory.Services.GetRequiredService<AgentTelemetryOptions>();

        // 覆盖后 Level==Metadata（配置节驱动采集级别）
        Assert.Equal(AgentTelemetryLevel.Metadata, options.Level);

        // 单例语义：注册为 AddSingleton(sp => IOptions.Value)，同一容器解析两次返回同一实例
        var same = factory.Services.GetRequiredService<AgentTelemetryOptions>();
        Assert.Same(options, same);
    }
}
