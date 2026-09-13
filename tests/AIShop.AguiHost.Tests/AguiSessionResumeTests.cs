using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIShop.AguiHost;
using AIShop.AguiHost.Model;
using AIShop.Service;
using AIShop.Service.Agui;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本集合多次启动 AguiHost Program（WAF 真实宿主）共享同一临时会话库，验证「重启后同 ThreadId 续聊带上下文」，
/// 置 DisableParallelization 串行集合：避免多个宿主并行迁移/写入同一 SQLite 文件库（learnings 先例同 AguiRequestTests）。
/// </summary>
[CollectionDefinition(nameof(AguiSessionResumeTests), DisableParallelization = true)]
public sealed class AguiSessionResumeTestsCollection;

/// <summary>
/// 重启续聊验收（WAF 级）：服务端不再补历史（<c>SqlChatHistoryProvider.ProvideChatHistoryAsync</c> 已停用，只存不取），
/// 上下文由客户端重发全量历史承载（真实 AG-UI 客户端每轮重发整段前文）。本用例：宿主重启（新 WAF 工厂 = 新 host +
/// 新 store 实例，同会话库文件）后同一 ThreadId 续聊——第二轮请求带上第一轮全量消息，第二次 chatClient 收到的输入
/// 应含第一轮 assistant 回复（「第二轮看到第一轮上下文」回归护栏；历史来源改为客户端而非服务端补历史）。
/// 离线驱动：每个工厂的 IChatClient 替换为脚本化文本回复的 NSubstitute（第一次固定首轮回复文本，第二次捕获输入消息
/// 并返回次轮回复文本），不触发真实 LLM。
/// </summary>
[Collection(nameof(AguiSessionResumeTests))]
public sealed class AguiSessionResumeTests : IDisposable
{
    private const string FirstReply = "第一轮回复 RESUME-MARKER 专业跑鞋 ¥129.99";
    private const string SecondReply = "第二轮回复 已按上文继续为您服务";

    private readonly string _sessionDbPath = Path.Combine(Path.GetTempPath(), $"agui_sessions_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_sessionDbPath))
                File.Delete(_sessionDbPath);
        }
        catch (IOException)
        {
            // 文件仍被占用时忽略，交由系统清理
        }
    }

    [Fact]
    public async Task RestartHost_SameThreadId_SecondRunSeesFirstRoundContext()
    {
        const string threadId = "resume-thread-1";
        const string firstUserMessage = "你好，帮我推荐一双跑步鞋";
        const string secondUserMessage = "那再帮我看看其他推荐";
        var sessionConnection = $"Data Source={_sessionDbPath}";

        // 第一个宿主：POST 一轮对话（「推荐跑步鞋」）→ 服务端只负责落库本轮（SqlChatHistoryProvider 只存不取）
        using (var factory1 = CreateFactory(sessionConnection, CreateMockChatClient(FirstReply, capture: null)))
        {
            using var client1 = factory1.CreateClient();
            using var first = await client1.PostAsync(
                "/",
                new StringContent(RunAgentBody(threadId, firstUserMessage), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Contains("RESUME-MARKER", await first.Content.ReadAsStringAsync());
        }
        SqliteConnection.ClearAllPools();

        // 等待第一轮会话真正落库（SaveSessionAfterStreamingAsync 在 SSE 流结束后执行，轮询避免时序竞态）
        await WaitForSessionRowAsync(sessionConnection, threadId);

        // 第二个宿主（模拟重启：新工厂 = 新 host + 新 store 实例，同会话库文件）：同 ThreadId 续聊。
        // 服务端不再从库补历史（ProvideChatHistoryAsync 已停用），上下文改由【客户端重发全量历史】承载
        // ——模仿真实 AG-UI 客户端每轮重发整段前文（第一轮 user + assistant 回复 + 本轮新 user）。
        var secondCaptured = new List<Meai.ChatMessage>();
        using (var factory2 = CreateFactory(sessionConnection, CreateMockChatClient(SecondReply, capture: secondCaptured)))
        {
            using var client2 = factory2.CreateClient();
            using var second = await client2.PostAsync(
                "/",
                new StringContent(
                    RunAgentBody(
                        threadId,
                        [
                            ("user", firstUserMessage),
                            ("assistant", FirstReply),
                            ("user", secondUserMessage),
                        ]),
                    Encoding.UTF8,
                    "application/json"));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("第二轮回复", await second.Content.ReadAsStringAsync());
        }

        // 关键断言（回归护栏）：第二次 chatClient 收到的输入消息含第一轮 assistant 回复（RESUME-MARKER）——
        // 即「第二轮看到第一轮上下文」。历史来源由服务端补历史改为客户端重发，断言本身不变。
        var allSecondInputText = string.Join(" | ", secondCaptured.Select(TextOf));
        Assert.Contains("RESUME-MARKER", allSecondInputText);
    }

    /// <summary>
    /// 构造 WAF：替换模型 seam 为 <see cref="IModelChatClientFactory"/> stub（C5 M4 起离线 override 点从全局 IChatClient
    /// 迁到工厂接口——agent 聊天底层经 RouterChatClient → 工厂；stub 让 mockChat 成为所有 modelId 的底层），并把会话
    /// store 覆写到共享临时库（同 <see cref="_sessionDbPath"/>）。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(string sessionConnection, Meai.IChatClient mockChat)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelChatClientFactory>();
                services.AddSingleton<IModelChatClientFactory>(new StubModelChatClientFactory(mockChat));

                // 覆写 Program AddAguiSessionStore 的默认 store：RemoveAll 默认注册后以临时库路径重注册
                // 具体 store + keyed AgentSessionStore（key = "AGUIShopping"），MapAGUIServer 按 agent.Name 命中。
                services.RemoveAll<AgentSessionStore>();
                services.RemoveAll<SqliteAgentSessionStore>();
                services.AddSingleton(new SqliteAgentSessionStore(sessionConnection));
                services.AddKeyedSingleton<AgentSessionStore>(AGUIShoppingAgent.AgentName,
                    static (sp, _) => sp.GetRequiredService<SqliteAgentSessionStore>());
            }));
    }

    /// <summary>脚本化文本回复的 chatClient：GetResponseAsync / GetStreamingResponseAsync 均返回固定文本；
    /// <paramref name="capture"/> 非 null 时把流式请求收到的输入消息快照进该列表（供断言上下文还原）。</summary>
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

    /// <summary>拼接消息所有 TextContent 文本（供断言含首轮回复标记）。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    /// <summary>构造 AG-UI RunAgentInput 形状的单轮请求体 JSON（username 由 username 中间件缺省 steve，会话仅按 ThreadId 续接）。</summary>
    private static string RunAgentBody(string threadId, string userMessage)
        => RunAgentBody(threadId, [("user", userMessage)]);

    /// <summary>构造带多轮消息（模拟客户端重发全量历史）的 AG-UI RunAgentInput 请求体 JSON；每条消息 id 取唯一 GUID。</summary>
    private static string RunAgentBody(string threadId, IReadOnlyList<(string Role, string Content)> messages)
    {
        var array = new JsonArray();
        foreach (var (role, content) in messages)
        {
            array.Add(new JsonObject
            {
                ["id"] = $"m-{Guid.NewGuid():N}",
                ["role"] = role,
                ["content"] = content,
            });
        }

        var root = new JsonObject
        {
            ["threadId"] = threadId,
            ["messages"] = array,
        };
        return root.ToJsonString();
    }

    /// <summary>轮询等待会话行落库（store_id = "AGUIShopping:{threadId}"，key 带 agent.Name 前缀）。</summary>
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
}
