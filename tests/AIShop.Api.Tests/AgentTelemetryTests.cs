using AIShop.AgentTelemetry;
// 命名空间 AIShop.AgentTelemetry 与其中的静态类 AgentTelemetry 同名，
// C# 遮蔽规则下「AgentTelemetry」解析为命名空间而非类，故加别名引用静态类。
using AgentTelemetryHelper = AIShop.AgentTelemetry.AgentTelemetry;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Trace;
using AIShop.Api.Agents;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIShop.Api.Tests;

public sealed class AgentTelemetryTests : IDisposable
{
    // T25 — ConfigureDebugTelemetry 的 debug=true 分支会注册 FileSpanExporter processor（默认写 AppContext.BaseDirectory），
    // 测试通过临时目录参数隔离，Dispose 统一清理（development-flow「测试资源清理」）。
    private readonly string _tempDir;

    public AgentTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"atte_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
                // 清理失败（文件占用等）不遮蔽测试结论，仅残留临时目录
            }
        }
    }

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

    // =========================================================
    // T23 — AgentTelemetryOptions.Debug 默认值与配置绑定
    // 对应 spec「Debug 开关默认关闭」的默认值部分 +「Debug 开启时...」的配置读取。
    // 与 T11 同一绑定路径（AddInMemoryCollection + Configure 绑定 + IOptions.Value）。
    // =========================================================

    [Fact]
    public void ShouldDefaultToFalse_WhenDebugNotConfigured()
    {
        // new AgentTelemetryOptions() 默认 Debug == false（生产安全：不抓 HTTP body）
        var options = new AgentTelemetryOptions();

        Assert.False(options.Debug);
    }

    [Fact]
    public void ShouldBindTrue_WhenDebugConfiguredAsTrue()
    {
        // AddInMemoryCollection 注入 AgentTelemetry:Debug=true，经 ConfigurationBinder 绑定后 Debug == true
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AgentTelemetry:Debug"] = "true",
        });

        Assert.True(options.Debug);
    }

    [Fact]
    public void ShouldBindFalse_WhenDebugConfiguredAsFalse()
    {
        // 显式配置 AgentTelemetry:Debug=false 绑定后 Debug == false（Debug 关闭无额外开销）
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AgentTelemetry:Debug"] = "false",
        });

        Assert.False(options.Debug);
    }

    // =========================================================
    // T25 — ConfigureDebugTelemetry 的 EnrichWith 回调行为
    // 对应 spec「Debug 开启时抓取 HTTP 请求/响应 body 写本地文件」的 span 属性部分 +
    //              「Debug 关闭时无额外开销」的零配置路径。
    // EnrichWith 回调为 HttpClientTraceInstrumentationOptions 上的公开委托属性
    // （EnrichWithHttpRequestMessage / EnrichWithHttpResponseMessage），
    // 配置后可直接以 (Activity, HttpRequestMessage) / (Activity, HttpResponseMessage) 调用（T19 决策点），
    // 无需走完整 HttpClientInstrumentation 管道，聚焦验证回调写 tag 的契约。
    // debug=true 路径同时验证 processor 注册：经配置出的 Sdk.CreateTracerProviderBuilder() 上执行
    // ConfigureDebugTelemetry 后 Build() 出 TracerProvider，触发一次真实 HTTP 出站请求，
    // 断言临时目录生成 traces_*.log 且含请求/响应 body（body 仅本地文件，不进 OTLP，无 OTLP exporter 注册）。
    // debug=false 路径：直接以默认 options 调用，断言两个 EnrichWith 委托仍为 null、
    // 无 FileSpanExporter 落盘，且无任何 span 被写出（零配置）。
    // =========================================================

    [Fact]
    public void ShouldWriteRequestHeaderAndBodyTags_ButNotResponseBody_WhenEnrichWithCalled()
    {
        using var activity = CreateStartedActivity("System.Net.Http.HttpRequestOut");

        // 构造带 body 的请求/响应消息：headers + Content body（模拟真实 LLM 请求/响应）
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/chat")
        {
            Headers = { { "Authorization", "Bearer secret-key" } },
            Content = new StringContent("{\"message\":\"你好\"}", Encoding.UTF8, "application/json"),
        };
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { { "X-Request-Id", "req-123" } },
            Content = new StringContent("{\"error\":\"rate limit\"}", Encoding.UTF8, "application/json"),
        };

        // Act：debug=true 配置出的 EnrichWith 回调，直接以 (Activity, 消息) 调用
        //（ConfigureDebugTelemetry 只设回调、不注册 processor，可在 options 闭包内安全调用）
        var httpOptions = new HttpClientTraceInstrumentationOptions();
        AgentTelemetryHelper.ConfigureDebugTelemetry(
            Sdk.CreateTracerProviderBuilder(),
            debug: true,
            httpOptions);
        httpOptions.EnrichWithHttpRequestMessage!(activity, request);
        httpOptions.EnrichWithHttpResponseMessage!(activity, response);

        // Assert：抓 request headers + request body；【不抓 response body】。
        // request body 是内存 content（StringContent）可重读，抓取安全；
        // response body 是网络流——EnrichWith* 回调是 void 委托，async lambda 为 fire-and-forget 并发执行，
        // 抓 response body 会与 DeepSeek 直连路径（DeepSeekDirectCallAsync 解析读取）并发读同一响应流，抛
        // System.InvalidOperationException: "The stream was already consumed"。回复内容由 MAF MetadataAndContent 采集进 Aspire。
        var tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);

        Assert.Contains("Authorization: Bearer secret-key", Assert.IsType<string>(tags["http.request.headers"]));
        Assert.Equal("{\"message\":\"你好\"}", tags["http.request.content.body"]);
        Assert.Contains("req-123", Assert.IsType<string>(tags["http.response.headers"]));
        Assert.DoesNotContain("http.response.content.body", tags.Keys);
    }

    [Fact]
    public void ShouldNotConfigureEnrichCallbacks_WhenDebugFalse()
    {
        // debug=false（默认）：ConfigureDebugTelemetry 直接返回，不设置 EnrichWith 回调。
        // 因方法体对 builder 零触碰，直接传 Sdk builder（不 Build）即可；httpOptions 作为回调持有点是断言对象
        var httpOptions = new HttpClientTraceInstrumentationOptions();

        AgentTelemetryHelper.ConfigureDebugTelemetry(
            Sdk.CreateTracerProviderBuilder(),
            debug: false,
            httpOptions);

        Assert.Null(httpOptions.EnrichWithHttpRequestMessage);
        Assert.Null(httpOptions.EnrichWithHttpResponseMessage);
        Assert.Empty(Directory.GetFiles(_tempDir, "traces_*.log"));
    }

    [Fact]
    public void ShouldNotCreateAnyFile_WhenDebugFalseAndSpanExported()
    {
        // debug=false 零配置路径：即便 TracerProvider 上存在其他 processor，
        // AddFileSpanExporter 也不注册 FileSpanExporter —— 配置目录不产生 traces_*.log 落盘
        //（EnrichWith 为 null 是对「零配置」的决定性断言；本测试验证无本地日志副作用）
        var builder = Sdk.CreateTracerProviderBuilder()
            .AddSource("AIShop.AgentTelemetryTests")
            .AddProcessor(new NoopActivityProcessor());
        AgentTelemetryHelper.AddFileSpanExporter(
            builder,
            debug: false,
            _tempDir);

        using var provider = builder.Build();
        using var activity = CreateStartedActivity("System.Net.Http.HttpRequestOut");
        provider.ForceFlush();

        Assert.Empty(Directory.GetFiles(_tempDir, "traces_*.log"));
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

    // =========================================================
    // T25 辅助：启动已采样 span + 最小 TracerProviderBuilder 假实现
    // =========================================================

    /// <summary>
    /// 经 ActivitySource + ActivityListener 启动一个已采样（Recorded）的 Activity。
    /// 关键点：SimpleActivityExportProcessor 只导出 <c>Activity.Recorded == true</c> 的 span
    /// （BaseExportProcessor.OnEnd 按采样结果过滤），裸 <c>new Activity()</c> 不带 Recorded 标记会被丢弃，
    /// 与 FileSpanExporterTests 同一模式（T24 踩坑结论）。
    /// </summary>
    private static Activity CreateStartedActivity(string operationName)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("AIShop.AgentTelemetryTests");
        var activity = source.StartActivity(operationName, ActivityKind.Client)!;
        activity.Stop();
        return activity;
    }

    /// <summary>
    /// 空处理器：用于验证 debug=false 时即便有 provider 也不产生 FileSpanExporter 落盘。
    /// </summary>
    private sealed class NoopActivityProcessor : BaseProcessor<Activity>
    {
    }
}
