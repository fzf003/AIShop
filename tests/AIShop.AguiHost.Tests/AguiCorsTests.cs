using System.Net;
using AIShop.AguiHost;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// agui-client-support T4：可选 CORS 装配（design §6）。
/// 覆盖 spec ADDED R6（<b>CORS 默认不注册</b>：无配置 → 容器无 CORS 服务、响应无 <c>Access-Control-Allow-*</c>）
/// 与 R7（<b>白名单可配置且精确匹配</b>：命中放行 / 未命中不放行 / 预检通过 / 配置形态错误启动即抛）。
/// <para>
/// L9 收紧：形态校验由「绝对地址 + scheme」收紧为「与协议 + 域名 + 端口的规范形式逐字（忽略大小写）相等」，
/// 使「末尾斜杠 / 路径 / 查询串」这类<b>运行期永不匹配</b>的配置在启动期即抛（与校验自身文案一致）。
/// </para>
/// <para>
/// 驱动方式：WAF 真实宿主（<see cref="WebApplicationFactory{TEntryPoint}"/>，内容根 = <c>src/AIShop.AguiHost</c>，
/// 读真实 <c>appsettings.json</c>，其中<b>没有</b> <c>Cors</c> 节）+ 脚本化模型工厂<b>不替换</b>——
/// 用例只打 <c>GET /models</c>（公开只读、不触模型）与 <c>OPTIONS /</c>（预检被 CORS 中间件短路，不进 Agent），
/// 故离线可启动（模型客户端懒建）。启用白名单的用例经环境变量 <c>Cors__AllowedOrigins__0</c> 注入配置。
/// </para>
/// <para>
/// 为什么用环境变量而不是 <c>ConfigureAppConfiguration</c>：Program 顶层（<c>AddAguiCors(builder.Configuration)</c>）
/// 在 <c>builder.Build()</c> 之前就读取了配置，<c>WebApplicationFactory.WithWebHostBuilder</c> 的
/// <c>ConfigureAppConfiguration</c> 覆盖并入配置更晚，对本用例无效；环境变量在
/// <c>WebApplication.CreateBuilder</c> 阶段即入配置，早于顶层读取（learnings 已记录的 T16 seam 手法）。
/// 变量在 <c>CreateClient()</c>（触发宿主构建的时点）之后<b>立即恢复</b>，防进程级污染后续用例。
/// </para>
/// <para>
/// 挂 <c>[Collection(nameof(AguiRequestTests))]</c>（约束 D）：本类启动真实宿主并操作进程级环境变量，
/// 必须与其它宿主级测试串行（DisableParallelization 集合），避免并行迁移同一 SQLite 文件库 / 互相污染 seam。
/// </para>
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiCorsTests
{
    /// <summary>白名单里唯一被允许的 origin（协议 + 域名 + 端口三元组，末尾无斜杠）。</summary>
    private const string AllowedOrigin = "http://localhost:5173";

    /// <summary>第一个配置项的完整配置键（环境变量形态 = <c>Cors__AllowedOrigins__0</c>）。</summary>
    private const string Origin0ConfigKey = "Cors:AllowedOrigins:0";

    private const string AllowOriginHeader = "Access-Control-Allow-Origin";

    [Fact]
    public async Task GetModels_WithoutCorsConfiguration_HasNoCorsHeadersAndNoCorsServices()
    {
        // spec R6 场景 1：appsettings 不含 Cors:AllowedOrigins → 带 Origin 头的 GET /models 响应【无】
        // Access-Control-Allow-Origin，且服务容器中无 CORS 服务注册（策略未注册）。
        using var factory = StartFactory(allowedOrigin: null);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.Contains(AllowOriginHeader),
            "未配置 Cors:AllowedOrigins 时响应不得携带 Access-Control-Allow-Origin");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));

        // 容器侧证明「未注册」：无 ICorsService / 无命名策略（不是「注册了但没生效」）
        Assert.Null(factory.Services.GetService<ICorsService>());
        Assert.Null(factory.Services.GetService<ICorsPolicyProvider>());
    }

    [Fact]
    public async Task GetModels_OriginInAllowList_IsAllowed()
    {
        // spec R7 场景 1：Cors:AllowedOrigins = ["http://localhost:5173"] → 从该 origin 发 GET /models
        // 响应含 Access-Control-Allow-Origin: http://localhost:5173（精确回显该 origin，不是 * ）。
        using var factory = StartFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(AllowOriginHeader), "白名单命中的 origin 必须被放行");
        Assert.Equal(AllowedOrigin, string.Join(",", response.Headers.GetValues(AllowOriginHeader)));

        // 精确白名单（非任意源）：证明未退化成 AllowAnyOrigin
        Assert.NotEqual("*", response.Headers.GetValues(AllowOriginHeader).Single());
    }

    /// <summary>
    /// L12：CORS 的**作用面必须与 spec 声明一致** —— spec 只声明覆盖 <c>GET /models</c>、AG-UI 端点
    /// （<c>/</c>）、<c>/products</c>、<c>/cart*</c>；声明外的路径（此处的 <c>/health</c>，以及
    /// <c>/devui</c>、<c>/v1/*</c>）不得获得跨源放行头。
    ///
    /// 修复前用全站中间件 <c>app.UseCors(PolicyName)</c>，白名单 origin 连 DevUI / OpenAI wire 都被放行，
    /// 属契约外暴露面。
    ///
    /// 反证：把 <c>Program.cs</c> 的 <c>UseWhen</c> 换回全站 <c>UseCors</c>，本用例必须变红。
    /// </summary>
    [Fact]
    public async Task PathsOutsideDeclaredScope_DoNotGetCorsHeaders()
    {
        using var factory = StartFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.False(
            response.Headers.Contains(AllowOriginHeader),
            "spec 未声明覆盖 /health，该路径不得携带 Access-Control-Allow-Origin（L12）");
    }

    [Fact]
    public async Task GetModels_OriginNotInAllowList_IsNotAllowed()
    {
        // spec R7 场景 2：请求携带 Origin: http://evil.example → 响应不含 Access-Control-Allow-Origin
        // （浏览器据此拒绝读取响应；服务端本身不因跨源与否改变业务结果，故仍 200）。
        using var factory = StartFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await client.SendAsync(request);

        Assert.False(
            response.Headers.Contains(AllowOriginHeader),
            "非白名单 origin 不得拿到 Access-Control-Allow-Origin");
    }

    [Fact]
    public async Task OptionsPreflight_OnAguiEndpoint_IsAnsweredWithAllowedMethodAndHeader()
    {
        // spec R7 场景 3：对 AG-UI 端点（"/"）发 OPTIONS 预检（Access-Control-Request-Method: POST +
        // Access-Control-Request-Headers: content-type）→ 被 CORS 中间件应答（非 404 / 非 405），
        // 且放行 POST 与 content-type。注意 "/" 本身只映射 POST，若预检未被中间件短路会落到路由层报 405。
        using var factory = StartFactory(AllowedOrigin);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/");
        request.Headers.Add("Origin", AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        // 预检由 CORS 中间件直接应答（204 No Content），不进入端点管线
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(AllowedOrigin, string.Join(",", response.Headers.GetValues(AllowOriginHeader)));
        Assert.Contains("POST", ResponseHeader(response, "Access-Control-Allow-Methods"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", ResponseHeader(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddAguiCors_ValidOrigin_RegistersExactMatchPolicyWithoutAnyOriginOrCredentials()
    {
        // spec R7 正文的正向对照（也让上方「不注册」断言不成为恒真）：有效白名单 → 返回 true + 策略可解析，
        // 且策略是【精确 origin 列表 + 任意方法 + 任意头】，【没有】放开任意源、【没有】启用凭据。
        var services = new ServiceCollection();

        Assert.True(services.AddAguiCors(BuildConfig((Origin0ConfigKey, AllowedOrigin))));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(AguiCors.PolicyName);

        Assert.NotNull(policy);
        Assert.Equal(new[] { AllowedOrigin }, policy.Origins);
        Assert.True(policy.AllowAnyMethod);
        Assert.True(policy.AllowAnyHeader);
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Theory]
    [InlineData("localhost:5173")]       // 缺 scheme（Uri 会解析成 scheme=localhost）
    [InlineData("*")]                    // 通配：WithOrigins 下永不匹配
    [InlineData("http://localhost:5173/")]      // L9：末尾斜杠——浏览器 Origin 头不带尾斜杠，运行期永不匹配
    [InlineData("http://localhost:5173/path")]  // L9：带路径
    [InlineData("http://localhost:5173?a=1")]   // L9：带查询串
    public void AddAguiCors_MalformedOrigin_FailsFastWithProblemValue(string malformedOrigin)
    {
        // spec R7 场景 4：形态不合规的 origin 必须在【启动期】以明确异常快速失败（不得静默「永不匹配」），
        // 异常消息含问题值。fail-fast 发生在注册之前 → 容器里不得留下半注册的 CORS 服务。
        // L9：校验收紧到与自身文案一致——「末尾斜杠 / 路径 / 查询串」都属非法（WithOrigins 精确匹配永远对不上）。
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddAguiCors(BuildConfig((Origin0ConfigKey, malformedOrigin))));

        Assert.Contains(malformedOrigin, exception.Message, StringComparison.Ordinal);
        Assert.False(
            services.Any(descriptor => descriptor.ServiceType == typeof(ICorsService)),
            "形态校验未通过时不得留下任何 CORS 服务注册");
    }

    [Theory]
    [InlineData(AllowedOrigin)]                    // 回归：常规 origin（协议 + 域名 + 端口，无尾斜杠）
    [InlineData("https://example.com:8443")]       // 回归：https + 非默认端口
    [InlineData("http://LOCALHOST:5173")]          // WithOrigins 匹配实测为序数忽略大小写 → 不得误报
    [InlineData("http://example.com")]             // 无显式端口（默认端口即规范形式）
    public void AddAguiCors_CanonicalOrigin_IsAccepted(string validOrigin)
    {
        // L9 回归面：收紧后既有合法配置必须照样通过，且策略按【精确 origin 列表】注册（非任意源）。
        // 判据 = 与「协议 + 域名 + 端口」的规范形式逐字（忽略大小写）相等。
        var services = new ServiceCollection();

        Assert.True(
            services.AddAguiCors(BuildConfig((Origin0ConfigKey, validOrigin))),
            $"合法 origin「{validOrigin}」不得被形态校验拒绝");

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(AguiCors.PolicyName);

        Assert.NotNull(policy);
        // WithOrigins 会把 scheme/host 规范化为小写（实测），故按忽略大小写比对：策略里落的正是该项本身。
        Assert.Equal(validOrigin, policy.Origins.Single(), ignoreCase: true);
        Assert.False(policy.AllowAnyOrigin);
    }

    [Fact]
    public void AddAguiCors_NoUsableOrigins_ReturnsFalseAndRegistersNothing()
    {
        // spec R6：空数组 / 全空白项都是「未配置」的等价形态 → 返回 false，且不注册任何 CORS 服务。
        var emptyServices = new ServiceCollection();
        Assert.False(emptyServices.AddAguiCors(BuildConfig()));

        var blankServices = new ServiceCollection();
        Assert.False(blankServices.AddAguiCors(BuildConfig((Origin0ConfigKey, "   "))));

        foreach (var services in new[] { emptyServices, blankServices })
        {
            Assert.False(
                services.Any(descriptor => descriptor.ServiceType == typeof(ICorsService)),
                "无有效白名单时不得注册 CORS 服务");
        }
    }

    /// <summary>
    /// 装配 WAF 真实宿主；<paramref name="allowedOrigin"/> 非 null 时经环境变量
    /// <c>Cors__AllowedOrigins__0</c> 注入白名单（null = 清除该变量，即「未配置」形态）。
    /// </summary>
    /// <remarks>
    /// 环境变量必须在宿主构建（<c>CreateClient()</c>，即 Program 顶层
    /// <c>AddAguiCors(builder.Configuration)</c> 执行的时点）<b>之前</b>设置，构建完成后<b>立即恢复</b>——
    /// 常驻会污染同进程后续用例的顶层配置读取。
    /// </remarks>
    private static WebApplicationFactory<Program> StartFactory(string? allowedOrigin)
    {
        const string envKey = "Cors__AllowedOrigins__0";
        var previous = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, allowedOrigin);

        try
        {
            var factory = new WebApplicationFactory<Program>();

            // CreateClient() 触发宿主构建（Program 顶层读取配置的时点），构建完成后即可恢复环境变量
            _ = factory.CreateClient();
            return factory;
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previous);
        }
    }

    /// <summary>以内存配置构造 <see cref="IConfiguration"/>（键值对为空 = 无 Cors 节的等价形态）。</summary>
    private static IConfiguration BuildConfig(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();

    /// <summary>读取响应头的拼接值（缺失时返回空串，便于直接做 Contains 断言）。</summary>
    private static string ResponseHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
}
