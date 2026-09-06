using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本集合会多次启动 AguiHost Program（WAF 真实宿主，MigrateAsync/播种/预热独立 agui.db）并驱动 AG-UI 请求管线，
/// 置 DisableParallelization 串行集合，避免多个宿主并行迁移同一 SQLite 文件库（learnings 先例同 ProgramSeedingTests）。
/// </summary>
[CollectionDefinition(nameof(AguiRequestTests), DisableParallelization = true)]
public sealed class AguiRequestTestsCollection;

/// <summary>
/// T5 请求级测试：AGUIShoppingAgent 经 MapAGUIServer("/") 暴露为 AG-UI SSE 端点（路由冒烟，非 404）
/// + username 经 AGUI forwarded metadata 写入 ICurrentUserAccessor（含缺省用户）的端到端链路。
/// 离线驱动：Program 装配的默认 IChatClient 被 NSubstitute 替换（脚本化文本回复，不触发真实 LLM），
/// 对应 tasks T5T1/T5T3——preview MapAGUIServer 请求管线不便用真实 LLM 驱动，故用离线 chatClient 覆盖。
/// 协调收编（用户改动）：缺省用户由 "guest" 改为 seed 用户 "fzf003"（AguiUsernameForwarder.DefaultUsername），
/// 本类「无 metadata 请求」断言随之改 fzf003；marla 显式携带仍原样断言。
/// </summary>
[Collection(nameof(AguiRequestTests))]
public sealed class AguiRequestTests
{
    /// <summary>模拟回复里携带的 ASCII 标记，便于在 SSE 事件流中断言文本已到达。</summary>
    private const string SimulatedText = "购物助手已为您检索，T5-MARKER 专业跑鞋 349.00 元。";

    [Fact]
    public async Task PostRoot_RunAgentInput_ReturnsOk_EndpointIsMounted()
    {
        // 路由冒烟（tasks T5T1）：离线 chatClient 让 Program 免 Key 装配成功；POST "/" RunAgentInput →
        // 非 404（端点已挂载）+ 200 SSE 文本事件流（spec「AG-UI 官方宿主装配 AddAGUIServer + MapAGUIServer」）
        using var factory = CreateFactory(accessorOverride: null);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("smoke-thread", username: null), Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("T5-MARKER", sse);
    }

    [Fact]
    public async Task PostRoot_ForwardedMetadataUsername_IsWrittenToAccessor_DefaultUsernameWhenMissing()
    {
        // 请求级 username 注入（tasks T5T3，尽力而为）：替换 ICurrentUserAccessor 为 mock，
        // POST 携带 forwardedProps.username=marla → accessor 收到 SetCurrentUser("marla")；
        // 第二个无 metadata 的请求 → 回退 SetCurrentUser("fzf003")（协调收编：缺省用户 guest → seed 用户 fzf003，
        // 对齐 AguiUsernameForwarder.DefaultUsername；语义仍为「metadata 缺失时按缺省用户处理」）
        var mockAccessor = Substitute.For<ICurrentUserAccessor>();
        using var factory = CreateFactory(mockAccessor);
        using var client = factory.CreateClient();

        // 请求 1：forwarded metadata 含 username=marla
        using (var first = await client.PostAsync(
                   "/",
                   new StringContent(RunAgentBody("marla-thread", username: "marla"), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Contains("T5-MARKER", await first.Content.ReadAsStringAsync());
        }

        mockAccessor.Received(1).SetCurrentUser("marla");

        // 请求 2：无 forwarded metadata → 缺省 fzf003
        using (var second = await client.PostAsync(
                   "/",
                   new StringContent(RunAgentBody("default-thread", username: null), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("T5-MARKER", await second.Content.ReadAsStringAsync());
        }

        mockAccessor.Received(1).SetCurrentUser("fzf003");
    }

    [Fact]
    public async Task GetHealth_ReturnsOk_ObservabilityEndpointIsReady()
    {
        // T10 ServiceDefaults 可观测性验收：MapDefaultEndpoints 暴露 /health（Aspire Dashboard 健康探测）。
        // 走 WAF 真实宿主（离线 IChatClient 覆盖，同上方冒烟模式）→ GET /health 返回 200 即健康端点就绪。
        // 若 WAF 宿主下健康端点行为有出入，按实际断言并在此注释说明（当前实测为 200）。
        using var factory = CreateFactory(accessorOverride: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// 装配 WAF：把 AguiHost Program 的默认 IChatClient 替换为脚本化文本回复的 NSubstitute
    /// （Program 启动即 resolve IChatClient 构造 Agent，替换后可免 Key 离线启动）；
    /// 传入 <paramref name="accessorOverride"/> 时同时把 ICurrentUserAccessor 替换为 mock（记录 SetCurrentUser 调用）。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(ICurrentUserAccessor? accessorOverride)
    {
        var mockChat = CreateMockChatClient();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Meai.IChatClient>();
                services.AddSingleton<Meai.IChatClient>(mockChat);

                if (accessorOverride is not null)
                {
                    services.RemoveAll<ICurrentUserAccessor>();
                    services.AddSingleton<ICurrentUserAccessor>(accessorOverride);
                }
            });
        });
    }

    /// <summary>脚本化文本回复的 chatClient：GetResponseAsync / GetStreamingResponseAsync 均返回固定中文文本。</summary>
    private static Meai.IChatClient CreateMockChatClient()
    {
        var mockChat = Substitute.For<Meai.IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, SimulatedText)));
        mockChat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync());
        return mockChat;
    }

    /// <summary>单条文本增量更新流（ChatClientAgent 走 GetStreamingResponseAsync，yield 一次即结束流）。</summary>
    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextAsync()
    {
        yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, SimulatedText);
    }

    /// <summary>构造 AG-UI RunAgentInput 形状的请求体 JSON；<paramref name="username"/> 为 null 时不带 forwardedProps。</summary>
    private static string RunAgentBody(string threadId, string? username)
    {
        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "m1",
                    ["role"] = "user",
                    ["content"] = "你好，帮我推荐一双跑步鞋",
                }),
        };

        if (username is not null)
            root["forwardedProps"] = new JsonObject { ["username"] = username };

        return root.ToJsonString();
    }
}
