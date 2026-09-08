using System.Text;
using System.Text.Json;
using AIShop.AguiHost.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C5 M3 单元测试：<see cref="AguiModelForwarder"/>（agui-model-switch）。
/// 对应 spec ADDED Requirement 5「AGUI forwardedProps.model 注入中间件」：
/// ① <see cref="AguiModelForwarder.ResolveModel"/> 纯函数——model 为字符串 → 该模型 id；缺失 / 非字符串 /
/// 空白 / metadata 非 object → null（＝未指定，走 ActiveModel 缺省）；
/// ② <see cref="AguiModelForwarder.UseAguiModelForwarding"/> 中间件——POST 携带 <c>forwardedProps.model=deepseek</c>
/// → <see cref="IActiveModelProvider.SetActiveModel"/>("deepseek")；无 model metadata / 非法 JSON → SetActiveModel(null)
/// （不注入缺省模型值，「缺省 = ActiveModel」由 RouterChatClient 读取侧解析）。
/// </summary>
public sealed class AguiModelForwarderTests
{
    /// <summary>把任意对象序列化为 <see cref="JsonElement"/>（模拟 AG-UI forwarded metadata 容器）。</summary>
    private static JsonElement MetadataOf(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ResolveModel_WhenForwardedMetadataContainsModel_ReturnsModel()
    {
        // forwardedProps 含 model=deepseek → 解析出 "deepseek"（对应 spec Req5 正常注入路径）
        var metadata = MetadataOf(new { model = "deepseek" });

        Assert.Equal("deepseek", AguiModelForwarder.ResolveModel(metadata));
    }

    [Fact]
    public void ResolveModel_WhenModelKeyMissing_ReturnsNull_AndWireKeyIsModel()
    {
        // metadata 不含 model（forwardedProps 里只有其它字段，如 username）→ 返回 null（= 未指定，走 ActiveModel 缺省）
        var metadata = MetadataOf(new { username = "marla" });

        Assert.Null(AguiModelForwarder.ResolveModel(metadata));

        // wire 键名锁定 "model"（spec Req5「forwardedProps.model」，测试防键名漂移）
        Assert.Equal("model", AguiModelForwarder.ModelMetadataKey);
    }

    [Fact]
    public void ResolveModel_WhenModelValueIsNotString_ReturnsNull()
    {
        // model 以非字符串（数字）携带 → 视作无有效模型（避免类型错误注入）
        var metadata = MetadataOf(new { model = 42 });

        Assert.Null(AguiModelForwarder.ResolveModel(metadata));
    }

    [Fact]
    public void ResolveModel_WhenModelIsBlank_ReturnsNull()
    {
        // model 为空白串 → 视作缺失（= 未指定，走 ActiveModel 缺省）
        var metadata = MetadataOf(new { model = "   " });

        Assert.Null(AguiModelForwarder.ResolveModel(metadata));
    }

    [Fact]
    public void ResolveModel_WhenForwardedMetadataIsNotJsonObject_ReturnsNull()
    {
        // metadata 不是 JSON object（如字符串/数组）→ 无 model 可读
        var metadata = JsonSerializer.SerializeToElement("not-an-object");

        Assert.Null(AguiModelForwarder.ResolveModel(metadata));
    }

    [Fact]
    public async Task UseAguiModelForwarding_PostWithModelDeepseek_SetsActiveModel()
    {
        // 中间件端到端：POST RunAgentInput 带 forwardedProps.model=deepseek → provider 收到 SetActiveModel("deepseek")
        // （对应 spec Req5「中间件解析 forwardedProps.model → SetActiveModel」；该值在当轮 run 的执行流中被 Router 读到）
        var provider = new RecordingActiveModelProvider();
        await InvokeForwardingAsync(provider,
            """{"threadId":"t1","messages":[],"forwardedProps":{"model":"deepseek"}}""");

        Assert.Equal("deepseek", provider.ActiveModel);
        Assert.Equal(["deepseek"], provider.SetCalls);
    }

    [Fact]
    public async Task UseAguiModelForwarding_PostWithoutModelMetadata_ClearsActiveModel_NotInjectsDefault()
    {
        // 无 forwardedProps / 无 model metadata → SetActiveModel(null) 显式清空（不注入缺省模型值；
        // 「缺省 = ActiveModel」由 RouterChatClient 读取侧解析——中间件不擅自注入缺省值，spec Req5）
        var provider = new RecordingActiveModelProvider();
        await InvokeForwardingAsync(provider, """{"threadId":"t1","messages":[]}""");

        Assert.Null(provider.ActiveModel);
        Assert.Equal([null], provider.SetCalls);
    }

    [Fact]
    public async Task UseAguiModelForwarding_PostMalformedJson_ClearsActiveModel_NotInjectsDefault()
    {
        // 非法 JSON（非 object 形状/语法错误）→ 视为无 model，SetActiveModel(null)（读体失败不阻断请求，
        // 由端点按自身契约返回 4xx；model 语义回 ActiveModel 缺省）
        var provider = new RecordingActiveModelProvider();
        await InvokeForwardingAsync(provider, "not-valid-json");

        Assert.Null(provider.ActiveModel);
        Assert.Equal([null], provider.SetCalls);
    }

    [Fact]
    public async Task UseAguiModelForwarding_PostWithModelMetadata_RewindsBodyForDownstream()
    {
        // 中间件读完回退 Body.Position=0：下游 MapAGUIServer 的 [FromBody] 能重新反序列化同一请求体
        // （与 username 中间件同款缓冲读语义；验证 AG-UI 端点不因 model 注入而读不到 body）
        var provider = new RecordingActiveModelProvider();
        await InvokeForwardingAsync(provider,
            """{"threadId":"t1","messages":[],"forwardedProps":{"username":"marla","model":"qwen"}}""");

        Assert.Equal("qwen", provider.ActiveModel);
    }

    /// <summary>可写/记录 SetActiveModel 调用序列的 <see cref="IActiveModelProvider"/> stub（记录每轮注入值）。</summary>
    private sealed class RecordingActiveModelProvider : IActiveModelProvider
    {
        /// <summary>最近一次注入的模型 id（null = 未指定）。</summary>
        public string? ActiveModel { get; private set; }

        /// <summary>按调用顺序记录 SetActiveModel 的入参（断言每轮注入值）。</summary>
        public List<string?> SetCalls { get; } = [];

        public void SetActiveModel(string? modelId)
        {
            ActiveModel = modelId;
            SetCalls.Add(modelId);
        }
    }

    /// <summary>
    /// 在最小请求管线上驱动 <see cref="AguiModelForwarder.UseAguiModelForwarding"/>（不启动真实宿主/Program，
    /// 只验证中间件行为）：装配中间件 + 终结点，向 <see cref="DefaultHttpContext"/> 发 POST 并跑完整管线；
    /// 结束后断言请求体已回退 Position=0（下游 [FromBody] 可重读）。
    /// </summary>
    private static async Task InvokeForwardingAsync(RecordingActiveModelProvider provider, string body)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActiveModelProvider>(provider);
        var sp = services.BuildServiceProvider();

        var app = new ApplicationBuilder(sp);
        app.UseAguiModelForwarding();
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var context = new DefaultHttpContext
        {
            RequestServices = sp,
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        await pipeline(context);

        // 中间件缓冲读后回退 Position=0：下游端点能重新反序列化同一请求体（与 username 中间件同款语义）
        Assert.Equal(0, context.Request.Body.Position);
    }
}
