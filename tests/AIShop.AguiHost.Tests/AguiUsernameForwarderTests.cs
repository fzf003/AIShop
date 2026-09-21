using System.Text;
using System.Text.Json;
using AIShop.AguiHost;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T5 ResolveUsername 纯函数单测：AGUI forwarded metadata 读 username；缺失/非法一律回退缺省用户。
/// 缺省用户为 seed 用户 "steve"（<see cref="AguiUsernameForwarder.DefaultUsername"/>），
/// 使未带 username 的 AG-UI 请求默认以可购物用户 steve 运行；解析层语义（缺失回退 DefaultUsername）不变。
/// <para>
/// 第 6 项（agui-client-support T11）在同一文件补充 <b>REST 身份通道</b>用例：查询参数解析
/// （<see cref="AguiClientIdentity.TryResolveQueryUsername"/>）与中间件的 REST 分支
/// （缺参 400 / 404 短路 / 成功注入 / 非必填放行 / 未挂标记放行）——驱动方式为最小请求管线
/// （<see cref="ApplicationBuilder"/> + <see cref="DefaultHttpContext"/> + <c>SetEndpoint(挂标记)</c> + 替身仓储/访问器），
/// 不启动真实宿主。对应 spec R15 / R16 / R17 / R20（中间件侧）。
/// </para>
/// </summary>
public sealed class AguiUsernameForwarderTests
{
    /// <summary>把任意对象序列化为 <see cref="JsonElement"/>（模拟 AG-UI forwarded metadata 容器）。</summary>
    private static JsonElement MetadataOf(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ResolveUsername_WhenForwardedMetadataContainsUsername_ReturnsUsername()
    {
        // metadata 含 username=marla → 解析出 "marla"
        var metadata = MetadataOf(new { username = "marla" });

        Assert.Equal("marla", AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameMissing_ReturnsNull_AndDefaultUsernameIsApplied()
    {
        // metadata 不含 username（forwardedProps 里只有其它字段）→ 解析层返回 null
        var metadata = MetadataOf(new { tenantId = "tenant-123" });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));

        // 缺省用户（seed 用户 steve，购物可用）：缺省常量 + 解析一次到位
        // helper 均回退 DefaultUsername。语义仍对齐 spec「metadata 缺失时按缺省用户处理」。
        Assert.Equal("steve", AguiUsernameForwarder.DefaultUsername);
        Assert.Equal("steve", AguiUsernameForwarder.ResolveUsernameOrDefault(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameValueIsNotString_ReturnsNull()
    {
        // username 以非字符串（数字）携带 → 视作无有效 username（避免类型错误注入）
        var metadata = MetadataOf(new { username = 42 });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameIsBlank_ReturnsNull()
    {
        // username 为空白串 → 视作缺失（由调用方应用 DefaultUsername 缺省）
        var metadata = MetadataOf(new { username = "   " });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenForwardedMetadataIsNotJsonObject_ReturnsNull()
    {
        // metadata 不是 JSON object（如字符串/数组）→ 无 username 可读
        var metadata = JsonSerializer.SerializeToElement("not-an-object");

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 第 6 项（T11）：REST 身份通道 —— wire 常量 + 查询参数解析（spec R15）
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolveQueryUsername_WhenQueryCarriesUsername_ReturnsUsername()
    {
        // spec R15 场景 1：REST 面身份来源是查询参数 ?username=（不是请求头 / 路径参数 / 请求体）
        Assert.Equal("marla", ResolveQueryUsername("?username=marla"));
    }

    [Fact]
    public void TryResolveQueryUsername_WhenUsernameMissing_ReturnsNull()
    {
        // spec R15「缺失」：无查询参数 / 只有其它参数 → null（由端点标记决定拒绝还是放行）
        Assert.Null(ResolveQueryUsername(null));
        Assert.Null(ResolveQueryUsername("?threadId=t1"));
    }

    [Fact]
    public void TryResolveQueryUsername_WhenUsernameEmptyOrBlank_ReturnsNull()
    {
        // spec R15「空白」：空串 / 纯空白一律视作未携带身份（不得据此注入任何人）
        Assert.Null(ResolveQueryUsername("?username="));
        Assert.Null(ResolveQueryUsername("?username=%20%20"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 第 6 项（T11）：中间件 REST 分支（spec R15 场景 2 / R16 场景 1 / R17 场景 1 / R20 场景 1）
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestBranch_RequiredUsernameMissing_Returns400WithoutEndpointCallOrInjection()
    {
        // spec R15 场景 2：缺参 + 必填标记（购物车 5 端点的缺省）→ 400 {"detail":"Username is required"}，
        // 不进端点、不注入身份、不回落缺省用户（不读库）。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, queryString: null, new AguiClientRestEndpoint(), users, accessor);

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("\"detail\":\"Username is required\"", result.Body);
        Assert.False(result.NextCalled);

        // 「不注入」+「不回落缺省用户」+「未读库」三条硬要求：漏发身份绝不能被静默当作他人身份处理
        accessor.DidNotReceive().SetCurrentUser(Arg.Any<string>());
        await users.DidNotReceive().GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestBranch_UnknownUsername_Returns404AndSkipsEndpoint()
    {
        // spec R16 场景 1（中间件层）：查无此人 → 404 {"detail":"User not found"} 且下游未被调用、未注入身份。
        var users = Substitute.For<IUserRepository>();
        users.GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, "?username=nobody", new AguiClientRestEndpoint(), users, accessor);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Contains("\"detail\":\"User not found\"", result.Body);
        Assert.False(result.NextCalled);
        accessor.DidNotReceive().SetCurrentUser(Arg.Any<string>());

        // 404 确实来自存在性校验（而非分支未命中）
        await users.Received(1).GetByUsernameAsync("nobody", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestBranch_ExistingUsername_InjectsIdentityAndCallsEndpoint()
    {
        // spec R15 场景 1：?username=marla（存在）→ 下游调用一次 + 注入 marla。
        var users = Substitute.For<IUserRepository>();
        users.GetByUsernameAsync("marla", Arg.Any<CancellationToken>())
            .Returns(new User { Username = "marla", DisplayName = "marla" });
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, "?username=marla", new AguiClientRestEndpoint(), users, accessor);

        Assert.True(result.NextCalled);
        accessor.Received(1).SetCurrentUser("marla");
    }

    [Fact]
    public async Task RestBranch_OptionalUsernameMissing_PassesThroughWithoutInjection()
    {
        // spec R17 场景 1（中间件侧）：RequireUsername=false（仅 GET /products）缺参 →
        // 放行且【不注入身份】——「缺参放行」不是回落，ICurrentUserAccessor 保持 null，且不读库。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, queryString: null,
            new AguiClientRestEndpoint { RequireUsername = false }, users, accessor);

        Assert.True(result.NextCalled);
        accessor.DidNotReceive().SetCurrentUser(Arg.Any<string>());
        await users.DidNotReceive().GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestBranch_OptionalUsernameWithUnknownValue_StillReturns404()
    {
        // spec R17 第 3 段「不要求身份 ≠ 忽略身份」：/products 带了非空 username 仍走存在性校验 → 404。
        var users = Substitute.For<IUserRepository>();
        users.GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, "?username=nobody",
            new AguiClientRestEndpoint { RequireUsername = false }, users, accessor);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Contains("\"detail\":\"User not found\"", result.Body);
        Assert.False(result.NextCalled);
    }

    [Fact]
    public async Task UnmarkedGetRequest_PassesThroughWithoutInjectionOrLookup()
    {
        // spec R20 场景 1（中间件侧）：未挂标记的既有 GET（/models、/health 等）零影响——放行、不注入、不读库。
        // 即便带了 ?username=，分支依据是【端点元数据】而非「查询参数是否存在」→ 同样不注入（防按路径/参数误判）。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, "?username=marla", marker: null, users, accessor);

        Assert.True(result.NextCalled);
        accessor.DidNotReceive().SetCurrentUser(Arg.Any<string>());
        await users.DidNotReceive().GetByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>在 <see cref="DefaultHttpContext"/> 上解析查询参数（不启动宿主）。</summary>
    private static string? ResolveQueryUsername(string? queryString)
    {
        var context = new DefaultHttpContext();
        if (queryString is not null)
            context.Request.QueryString = new QueryString(queryString);

        return AguiClientIdentity.TryResolveQueryUsername(context.Request);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // L3 C 防护：写方法 + 未挂身份标记 → 运行时 Warning（只观测、不改行为），
    // AG-UI 流端点（挂 AguiStreamEndpoint）与只读方法不得产生该 Warning。
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnmarkedWriteMethodEndpoint_LogsWarningAndKeepsFallbackBehavior()
    {
        // L3 缺陷的运行时观测（C 防护）：路由匹配到【写方法端点】但该端点既没挂 AguiClientRestEndpoint、
        // 也不是 AG-UI 流端点（AguiStreamEndpoint）→ 记一条 Warning，文案指向修法（挂 AguiClientRestEndpoint）。
        // 这正是「新增写端点却忘了挂标记 → 以缺省用户身份写数据、200 假成功」这一静默缺陷的告警出口。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        var recorder = new RecordingLoggerFactory();

        var result = await InvokePipelineAsync(
            HttpMethods.Post, queryString: null, marker: null, users, accessor,
            loggerFactory: recorder, endpointPresent: true);

        var warning = Assert.Single(recorder.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        // 文案必须能指向修法：点名 AguiClientRestEndpoint（挂标记）——否则告警等于没给出口。
        Assert.Contains("AguiClientRestEndpoint", warning.Message);

        // C 只加日志、【不改变行为】：未挂标记的 POST 仍走 AG-UI 分支 → 回落缺省用户并放行下游。
        Assert.Equal("steve", AguiUsernameForwarder.DefaultUsername);
        accessor.Received(1).SetCurrentUser("steve");
        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task AguiStreamMarkedEndpoint_DoesNotLogTheUnmarkedWriteWarning()
    {
        // 排除误报（C 防护的关键）：挂 AguiStreamEndpoint 的写端点（AG-UI 流端点 POST /、DevUI / OpenAI wire）
        // 【有意】走 AG-UI 分支拿缺省用户身份 → 不得产生「漏挂标记」告警。断言 0 条日志，防止以后把合法端点误报。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        var recorder = new RecordingLoggerFactory();

        var result = await InvokePipelineAsync(
            HttpMethods.Post, queryString: null, marker: null, users, accessor,
            loggerFactory: recorder, aguiStreamMarker: true);

        Assert.Empty(recorder.Entries);

        // 行为不变：仍回落缺省用户并放行（AG-UI 分支逐字不变）。
        accessor.Received(1).SetCurrentUser("steve");
        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task RestMarkedWriteEndpoint_DoesNotLogTheUnmarkedWriteWarning()
    {
        // 排除误报：挂 AguiClientRestEndpoint 的写端点（如 POST /cart/items）在 REST 分支即返回，
        // 不会走到告警判定 → 不产生「漏挂标记」告警（此处用 POST 驱动，避免只覆盖 GET 的盲区）。
        var users = Substitute.For<IUserRepository>();
        users.GetByUsernameAsync("marla", Arg.Any<CancellationToken>())
            .Returns(new User { Username = "marla", DisplayName = "marla" });
        var accessor = Substitute.For<ICurrentUserAccessor>();
        var recorder = new RecordingLoggerFactory();

        await InvokePipelineAsync(
            HttpMethods.Post, "?username=marla", new AguiClientRestEndpoint(), users, accessor,
            loggerFactory: recorder);

        Assert.Empty(recorder.Entries);
        accessor.Received(1).SetCurrentUser("marla");
    }

    [Fact]
    public async Task UnmarkedReadOnlyEndpoint_DoesNotLogTheUnmarkedWriteWarning()
    {
        // 不扩大告警面：只读方法（GET）即便未挂标记也只放行、不告警——否则 /models、/health 等
        // 每个只读端点都会刷屏，告警失去信号价值。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        var recorder = new RecordingLoggerFactory();

        var result = await InvokePipelineAsync(
            HttpMethods.Get, queryString: null, marker: null, users, accessor,
            loggerFactory: recorder, endpointPresent: true);

        Assert.Empty(recorder.Entries);
        Assert.True(result.NextCalled);
    }

    [Fact]
    public async Task UnmatchedWriteRequest_DoesNotLogTheUnmarkedWriteWarning()
    {
        // 不误伤 404：路由未匹配到任何端点（context.GetEndpoint() = null）时不算「端点漏挂」→ 不告警。
        // 否则任何 POST 到不存在路径的请求都会刷 Warnings。
        var users = Substitute.For<IUserRepository>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        var recorder = new RecordingLoggerFactory();

        await InvokePipelineAsync(
            HttpMethods.Post, queryString: null, marker: null, users, accessor,
            loggerFactory: recorder, endpointPresent: false);

        Assert.Empty(recorder.Entries);
    }

    /// <summary>最小请求管线的一次执行结果。</summary>
    private sealed record PipelineResult(int StatusCode, string Body, bool NextCalled);

    /// <summary>
    /// 在最小请求管线上驱动 <see cref="AguiUsernameForwarder.UseAguiUsernameForwarding"/>（不启动真实宿主）：
    /// 装配中间件 + 终结委托，向 <see cref="DefaultHttpContext"/> 发请求并跑完整管线；
    /// <paramref name="marker"/> 非 null 时把 <see cref="AguiClientRestEndpoint"/> 挂到端点元数据
    /// （模拟 <c>WithMetadata</c>，即 REST 分支的唯一判定依据）；<paramref name="aguiStreamMarker"/> 为 true 时
    /// 改挂 <see cref="AguiStreamEndpoint"/>（模拟 AG-UI 流端点）；<paramref name="endpointPresent"/> 为 true 时
    /// 挂一个<b>无任何标记</b>的端点（模拟「新加写端点漏挂标记」——路由匹配到了、但两个标记都没有）。
    /// <paramref name="loggerFactory"/> 非 null 时注册进管线，供告警断言（C 防护）。
    /// </summary>
    private static async Task<PipelineResult> InvokePipelineAsync(
        string method,
        string? queryString,
        AguiClientRestEndpoint? marker,
        IUserRepository users,
        ICurrentUserAccessor accessor,
        ILoggerFactory? loggerFactory = null,
        bool aguiStreamMarker = false,
        bool endpointPresent = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(users);
        services.AddSingleton(accessor);
        // 中间件经 WriteAsJsonAsync 写错误体：注册 Http JsonOptions（web 默认 = camelCase）
        // 使匿名类型 new { detail = ... } 序列化为 {"detail":"..."}，与真实宿主同形。
        services.ConfigureHttpJsonOptions(_ => { });
        if (loggerFactory is not null)
            services.AddSingleton(loggerFactory);
        await using var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseAguiUsernameForwarding();
        var nextCalled = false;
        app.Run(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = method;
        if (queryString is not null)
            context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();

        var metadata = new List<object>();
        if (marker is not null)
            metadata.Add(marker);
        if (aguiStreamMarker)
            metadata.Add(new AguiStreamEndpoint());

        if (marker is not null || aguiStreamMarker || endpointPresent)
        {
            context.SetEndpoint(new Endpoint(
                requestDelegate: null,
                metadata: new EndpointMetadataCollection(metadata),
                displayName: "test-endpoint"));
        }

        await pipeline(context);

        return new PipelineResult(
            context.Response.StatusCode,
            Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()),
            nextCalled);
    }

    /// <summary>
    /// 记录型 <see cref="ILoggerFactory"/>：捕获所有 <c>(Level, Message)</c>，供 C 防护的告警断言。
    /// 只实现管线需要用到的部分（<c>CreateLogger</c> + <c>Log</c>），不引第三方日志包。
    /// </summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        /// <summary>按记录顺序保存的日志条目。</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
            // 最小替身：不接 provider，记录只看 Entries
        }

        public void Dispose()
        {
            // 无可释放资源
        }

        private sealed class RecordingLogger : ILogger
        {
            private readonly List<(LogLevel Level, string Message)> _sink;

            public RecordingLogger(List<(LogLevel Level, string Message)> sink) => _sink = sink;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _sink.Add((logLevel, formatter(state, exception)));
        }
    }
}
