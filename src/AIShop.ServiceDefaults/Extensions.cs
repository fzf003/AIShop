using AIShop.AgentTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AIShop.ServiceDefaults;

public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                // DebugTelemetry 扩展：按 AgentTelemetry:Debug 配置启用 HTTP 请求/响应 body 本地日志抓取。
                // 不依赖 AgentTelemetryOptions POCO（避免与 Api 侧 DI 注册耦合）；null/false 时 ConfigureDebugTelemetry
                // 直接返回，保持标准 AddHttpClientInstrumentation() 行为，不配 EnrichWith、零额外开销。
                // 注意：ConfigureDebugTelemetry 只设 EnrichWith 回调（可在 AddHttpClientInstrumentation 的 options 闭包内安全调用）；
                // FileSpanExporter processor 注册放 DI 服务注册层（下方 ConfigureOpenTelemetryTracerProvider），
                // 因为 provider 构造期间（options 闭包内）再调 AddProcessor 会抛 NotSupportedException。
                var httpDebug = builder.Configuration.GetValue<bool?>("AgentTelemetry:Debug") ?? false;

                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation(http =>
                        tracing.ConfigureDebugTelemetry(httpDebug, http))
                    .AddSource(builder.Environment.ApplicationName)
                    .AddSource("Experimental.Microsoft.Agents.AI");
            });

        // DebugTelemetry 扩展：在 DI 服务注册层注册 FileSpanExporter processor（写本地 traces_*.log）。
        // 不能在 AddHttpClientInstrumentation 的 options 闭包内注册（provider 构造期间禁止二次配置）。
        // body 含敏感信息（Authorization / API key），仅写本地文件，不进 OTLP / Aspire Dashboard。
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddFileSpanExporter(builder.Configuration.GetValue<bool?>("AgentTelemetry:Debug") ?? false));

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static void AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
        }

    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = _ => true
        });

        return app;
    }
}
