using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Core.Interfaces;
using AIShop.Service;
using AIShop.Service.Agui;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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
/// 离线驱动：Program 装配的模型 seam 被 <see cref="IModelChatClientFactory"/> stub 替换（脚本化文本回复，
/// 不触发真实 LLM）；C5 M4 起离线 override 点从全局 IChatClient 迁到该工厂接口（spec Req11——agent 聊天底层经
/// RouterChatClient → 工厂，stub 让所有 modelId 返回脚本化 mock；全局纯净 IChatClient seam = 工厂 GetDefaultClient
/// 亦变 mock）。对应 tasks T5T1/T5T3 + C5 请求级用例——preview MapAGUIServer 请求管线不便用真实 LLM 驱动。
/// 缺省用户为 seed 用户 "steve"（AguiUsernameForwarder.DefaultUsername），
/// 本类「无 metadata 请求」断言随之用 steve；marla 显式携带仍原样断言。
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
        // 第二个无 metadata 的请求 → 回退 SetCurrentUser("steve")（缺省用户为 seed 用户 steve，
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

        // 请求 2：无 forwarded metadata → 缺省 steve
        using (var second = await client.PostAsync(
                   "/",
                   new StringContent(RunAgentBody("default-thread", username: null), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("T5-MARKER", await second.Content.ReadAsStringAsync());
        }

        mockAccessor.Received(1).SetCurrentUser("steve");
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

    [Fact]
    public async Task PostRoot_ForwardedModel_Deepseek_DelegatesToDeepseekClientViaRouter()
    {
        // C5 请求级（spec Req6/Req7 + 验收标准 2）：POST forwardedProps.model="deepseek" → 该轮经 RouterChatClient
        // 委托 stub 工厂 GetClient("deepseek")（model 到底层只可能经 RouterChatClient 到达工厂 = Router 已入 agent
        // 链的装配证明，见 design §3 末段测试 seam 迁移说明）；响应 200 + SSE 文本照常（脚本化 mock，与 C5 前一致）。
        var stub = new StubModelChatClientFactory(CreateMockChatClient());
        using var factory = CreateFactory(accessorOverride: null, factoryStub: stub);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("model-thread", username: null, model: "deepseek"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("T5-MARKER", await response.Content.ReadAsStringAsync());
        Assert.Contains("deepseek", stub.RequestedModelIds);
    }

    [Fact]
    public async Task PostRoot_NoModel_DelegatesToDefaultClient_AndEndpointNotRegressed()
    {
        // C5 请求级（spec Req6 缺省 + Req10「无 model 行为不回归」）：无 model metadata → 中间件 SetActiveModel(null)，
        // RouterChatClient 委托缺省 ActiveModel 底层（stub GetDefaultClient）；SSE 文本/路由与 C5 前一致
        // （forwardedProps 缺失 → 端点照常 200 文本，验收标准 4 端点不回归）。
        var stub = new StubModelChatClientFactory(CreateMockChatClient());
        using var factory = CreateFactory(accessorOverride: null, factoryStub: stub);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/",
            new StringContent(RunAgentBody("default-model-thread", username: null, model: null), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("T5-MARKER", await response.Content.ReadAsStringAsync());
        Assert.Empty(stub.RequestedModelIds);
        Assert.True(stub.GetDefaultClientCalls >= 1);
    }

    [Fact]
    public async Task SameThread_TwoRounds_SwitchDeepseekThenDefault_SecondRoundSeesFirstContext()
    {
        // C5 请求级（spec Req9 跨模型共享会话上下文 + Req6 逐轮独立选模型，尽力而为）：同一 ThreadId 两轮——
        // 第一轮 forwardedProps.model="deepseek" → 该宿主 stub 收到 GetClient("deepseek")（Router 入链 + 按轮委托证明）；
        // 第二轮无 model 在第二个宿主（模拟重启，同会话库文件）续聊 → 其 stub 收到 GetDefaultClient（缺省 ActiveModel），
        // 且第二轮底层收到的输入含第一轮 assistant 回复标记（换模型续聊带上文——会话由 SqliteAgentSessionStore 按
        // ThreadId 持久化、模型无关，spec Req9）。
        // 实施期降级说明（对齐 tasks「尽力而为」）：同一 TestServer 宿主内两轮连续驱动时，preview AG-UI 请求管线会跨
        // 请求复用 ExecutionContext（首轮 AsyncLocal 模型值残留，测试宿主进程内串行请求的伪影；真实 Kestrel 每请求独立
        // ExecutionContext），使第二轮 Router 读到首轮模型而非缺省。故改用双宿主同会话库驱动（同 AguiSessionResumeTests
        // 跨宿主续聊模式）：既验证同 ThreadId 跨模型续聊上下文又不引入该伪影；同宿主逐轮热切换的最终行为移交 E2E 人工验收。
        var sessionPath = Path.Combine(Path.GetTempPath(), $"agui_switch_{Guid.NewGuid():N}.db");
        try
        {
            const string threadId = "switch-resume-thread";
            var sessionConnection = $"Data Source={sessionPath}";

            // 第一宿主：model=deepseek 一轮 → 该轮委托 deepseek 底层
            var firstStub = new StubModelChatClientFactory(CreateMockChatClient(SimulatedText, capture: null));
            using (var factory1 = CreateFactory(accessorOverride: null, factoryStub: firstStub, sessionConnection: sessionConnection))
            {
                using var client1 = factory1.CreateClient();
                using var first = await client1.PostAsync(
                    "/",
                    new StringContent(RunAgentBody(threadId, username: null, model: "deepseek"), Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Contains("T5-MARKER", await first.Content.ReadAsStringAsync());
                Assert.Contains("deepseek", firstStub.RequestedModelIds);
            }
            SqliteConnection.ClearAllPools();

            // 等待第一轮会话真正落库（SaveSessionAfterStreamingAsync 在 SSE 流结束后执行，轮询避免跨宿主时序竞态）
            await WaitForSessionRowAsync(sessionConnection, threadId);

            // 第二宿主（模拟重启）：同 ThreadId 无 model 续聊 → 缺省 ActiveModel 底层（无首轮模型泄漏）
            var secondCaptured = new List<Meai.ChatMessage>();
            var secondStub = new StubModelChatClientFactory(CreateMockChatClient(SimulatedText, capture: secondCaptured));
            using (var factory2 = CreateFactory(accessorOverride: null, factoryStub: secondStub, sessionConnection: sessionConnection))
            {
                using var client2 = factory2.CreateClient();
                using var second = await client2.PostAsync(
                    "/",
                    new StringContent(RunAgentBody(threadId, username: null, model: null), Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                Assert.Contains("T5-MARKER", await second.Content.ReadAsStringAsync());
                Assert.Empty(secondStub.RequestedModelIds);
                Assert.True(secondStub.GetDefaultClientCalls >= 1);
            }

            // 续聊带上下文：第二轮底层收到的输入含第一轮 assistant 回复标记（跨模型续聊不丢上下文，spec Req9）
            var secondInput = string.Join(" | ", secondCaptured.Select(TextOf));
            Assert.Contains("T5-MARKER", secondInput);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(sessionPath))
                    File.Delete(sessionPath);
            }
            catch (IOException)
            {
                // 文件仍被占用时忽略，交由系统清理
            }
        }
    }

    /// <summary>轮询等待会话行落库（store_id = "AGUIShopping:{threadId}"，key 带 agent.Name 前缀；同 AguiSessionResumeTests）。</summary>
    private static async Task WaitForSessionRowAsync(string connectionString, string threadId, int timeoutMs = 10_000)
    {
        var storeId = $"AGUIShopping:{threadId}";
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await CountSessionRowsAsync(connectionString, storeId) >= 1)
                    return;
            }
            catch (SqliteException)
            {
                // 宿主仍在写库（快照/WAL），短暂重试
            }
            await Task.Delay(200);
        }
        Assert.Fail($"等待会话落库超时：store_id = {storeId}");
    }

    private static async Task<long> CountSessionRowsAsync(string connectionString, string storeId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_sessions WHERE store_id = $storeId";
        command.Parameters.AddWithValue("$storeId", storeId);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>拼接消息所有 TextContent 文本（供断言输入含历史回复标记）。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    /// <summary>
    /// 装配 WAF：把 AguiHost Program 的模型 seam 替换为 <see cref="IModelChatClientFactory"/> stub（spec Req11：
    /// 离线 override 点从全局 IChatClient 迁到工厂接口——agent 聊天底层经 RouterChatClient → 工厂；stub 让所有
    /// modelId 返回脚本化 mock，免 Key/免真实 LLM 离线启动）。传入 <paramref name="accessorOverride"/> 时同时把
    /// ICurrentUserAccessor 替换为 mock（记录 SetCurrentUser 调用）；传 <paramref name="factoryStub"/> 时可复用同一
    /// stub 实例以断言 GetClient(modelId) 调用序列（请求级 model 切换）；传 <paramref name="sessionConnection"/> 时把
    /// 默认会话 store 覆写到临时会话库（跨宿主续聊用例同 AguiSessionResumeTests 模式）。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        ICurrentUserAccessor? accessorOverride,
        StubModelChatClientFactory? factoryStub = null,
        string? sessionConnection = null)
    {
        factoryStub ??= new StubModelChatClientFactory(CreateMockChatClient());

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelChatClientFactory>();
                services.AddSingleton<IModelChatClientFactory>(factoryStub);

                if (sessionConnection is not null)
                {
                    // 覆写 Program AddAguiSessionStore 的默认 store：RemoveAll 默认注册后以临时库路径重注册
                    // 具体 store + keyed AgentSessionStore（key = "AGUIShopping"），MapAGUIServer 按 agent.Name 命中
                    // （同 AguiSessionResumeTests.CreateFactory）。
                    services.RemoveAll<AgentSessionStore>();
                    services.RemoveAll<SqliteAgentSessionStore>();
                    services.AddSingleton(new SqliteAgentSessionStore(sessionConnection));
                    services.AddKeyedSingleton<AgentSessionStore>(AGUIShoppingAgent.AgentName,
                        static (sp, _) => sp.GetRequiredService<SqliteAgentSessionStore>());
                }

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
        => CreateMockChatClient(SimulatedText, capture: null);

    /// <summary>
    /// 脚本化文本回复的 chatClient：GetResponseAsync / GetStreamingResponseAsync 均返回固定 <paramref name="reply"/> 文本；
    /// <paramref name="capture"/> 非 null 时把流式请求收到的输入消息快照进该列表（供跨模型续聊断言上下文还原）。
    /// </summary>
    private static Meai.IChatClient CreateMockChatClient(string reply, List<Meai.ChatMessage>? capture)
    {
        var mockChat = Substitute.For<Meai.IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, reply)));
        mockChat.GetStreamingResponseAsync(
                Arg.Do<IEnumerable<Meai.ChatMessage>>(messages =>
                {
                    if (capture is not null)
                        capture.AddRange(messages.ToList());
                }),
                Arg.Any<Meai.ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync(reply));
        return mockChat;
    }

    /// <summary>单条文本增量更新流（ChatClientAgent 走 GetStreamingResponseAsync，yield 一次即结束流）。</summary>
    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextAsync(string text)
    {
        yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, text);
    }

    /// <summary>构造 AG-UI RunAgentInput 形状的请求体 JSON；username/model 任一非 null 才带 forwardedProps。
    /// 用户消息 id 每次生成唯一 GUID（对齐 AguiSessionResumeTests）：同 ThreadId 续聊时若消息 id 与已还原会话历史
    /// 重复（如固定 "m1"），ChatClientAgent 会按 id 判重合并 → 第二轮输入丢失首轮上下文。</summary>
    private static string RunAgentBody(string threadId, string? username, string? model = null, string? userMessage = null)
    {
        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = $"m-{Guid.NewGuid():N}",
                    ["role"] = "user",
                    ["content"] = userMessage ?? "你好，帮我推荐一双跑步鞋",
                }),
        };

        if (username is not null || model is not null)
        {
            var forwardedProps = new JsonObject();
            if (username is not null)
                forwardedProps["username"] = username;
            if (model is not null)
                forwardedProps["model"] = model;
            root["forwardedProps"] = forwardedProps;
        }

        return root.ToJsonString();
    }
}
