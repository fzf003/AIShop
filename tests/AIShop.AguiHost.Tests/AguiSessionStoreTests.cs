using AIShop.AguiHost;
using AIShop.AguiHost.Agents;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本测试集合直测 SQLite 持久化 AgentSessionStore（T12）：store 落库往返 + 新 store 实例同库读回还原会话
/// （含 ChatClientAgent InMemoryChatHistoryProvider 写入 StateBag 的消息历史）+ keyed 装配解析。串行集合，
/// 避免 SQLite 文件并发写入冲突（learnings 先例同 AguiStartupSeedingTests）。
/// </summary>
[CollectionDefinition(nameof(AguiSessionStoreTests), DisableParallelization = true)]
public sealed class AguiSessionStoreTestsCollection;

/// <summary>
/// T12 store 级测试：SQLite AgentSessionStore 的持久化语义（Save/Get/Delete + 独立新实例还原 + keyed 装配），
/// 对应 spec「AG-UI 会话历史持久化（重启不丢上下文）」。断言以「store 落库往返 + 会话消息/状态还原」为准，
/// 不驱动 AG-UI 请求管线（那是 AguiSessionResumeTests 的职责）。
/// </summary>
[Collection(nameof(AguiSessionStoreTests))]
public sealed class AguiSessionStoreTests : IDisposable
{
    private readonly List<string> _createdDbPaths = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _createdDbPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // 文件仍被其他进程占用时忽略，交由系统清理
            }
        }
    }

    private string NewDbPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_t12_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    /// <summary>构造一个最小 ChatClientAgent（无工具/无压缩 provider，只验证 store 依赖的 StateBag 消息历史机制）。</summary>
    private static ChatClientAgent CreateBareAgent(string name)
    {
        var chatClient = Substitute.For<Meai.IChatClient>();
        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new Meai.ChatOptions { Instructions = "测试人设" }
        });
        return Assert.IsType<ChatClientAgent>(agent);
    }

    /// <summary>读取会话中 InMemoryChatHistoryProvider 的消息文本（state 存 Session.StateBag["InMemoryChatHistoryProvider"]）。</summary>
    private static string HistoryText(ChatClientAgent agent, AgentSession session)
    {
        var historyProvider = Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        return string.Join(" | ", historyProvider.GetMessages(session).Select(TextOf));
    }

    /// <summary>拼接消息所有 TextContent 文本（供断言含指定回复标记）。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    [Fact]
    public async Task Save_ThenGetWithNewStoreInstance_SameDatabase_RestoresMessagesAndIndependentSession()
    {
        // store 落库往返（tasks T12 验收 1）：真实 ChatClientAgent 会话（含 InMemoryChatHistoryProvider 写入 StateBag
        // 的消息历史）经 store1.SaveSessionAsync 落库 → 新建同库路径的 store2（模拟重启）GetSessionAsync
        // → 返回独立会话且消息历史还原（重启不丢上下文的 store 级保证）
        var sessionDbPath = NewDbPath("sessions");
        var sessionConnection = $"Data Source={sessionDbPath}";
        const string threadId = "thread-store-roundtrip";
        const string firstReply = "第一轮回复 STORE-MARKER 已为您找到专业跑鞋";

        // 准备一个带消息历史的会话：向 agent1 的 InMemoryChatHistoryProvider 写入一轮 user+assistant
        var agent1 = CreateBareAgent("StoreAgent");
        var session1 = await agent1.CreateSessionAsync();
        var history1 = Assert.IsType<InMemoryChatHistoryProvider>(agent1.ChatHistoryProvider);
        var messages1 = history1.GetMessages(session1);
        messages1.Add(new Meai.ChatMessage(Meai.ChatRole.User, "帮我推荐一双跑步鞋"));
        messages1.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, firstReply));

        // store1 落库
        var store1 = new SqliteAgentSessionStore(sessionConnection);
        await store1.InitializeAsync();
        await store1.SaveSessionAsync(agent1, threadId, session1);

        // 原始库文件确实写入会话行（不是 Noop：Noop 不会落任何文件）
        Assert.True(File.Exists(sessionDbPath));
        Assert.Equal(1, await CountSessionRowsAsync(sessionConnection, $"StoreAgent:{threadId}"));

        // store2（同库路径新实例 = 模拟宿主重启）还原
        var agent2 = CreateBareAgent("StoreAgent");
        var store2 = new SqliteAgentSessionStore(sessionConnection);
        var restored = await store2.GetSessionAsync(agent2, threadId);

        // 独立新实例（隔离契约：返回的 session 可被调用方安全 mutate，不影响已存快照）
        Assert.NotSame(session1, restored);

        // 消息历史已还原：agent2 的 InMemoryChatHistoryProvider 从 StateBag 读到同一轮消息
        Assert.Contains(firstReply, HistoryText(agent2, restored));
    }

    [Fact]
    public async Task GetSession_WhenNoRowStored_ReturnsNewEmptySession()
    {
        // 未命中语义（镜像 NoopAgentSessionStore / InMemory 一致）：无落库行 → CreateSessionAsync 返回全新空会话
        var sessionDbPath = NewDbPath("sessions");
        var sessionConnection = $"Data Source={sessionDbPath}";
        var agent = CreateBareAgent("StoreAgent");

        var store = new SqliteAgentSessionStore(sessionConnection);
        await store.InitializeAsync();

        var session = await store.GetSessionAsync(agent, "thread-unknown");

        Assert.NotNull(session);
        // 全新会话：InMemoryChatHistoryProvider 无任何历史消息
        Assert.Equal(string.Empty, HistoryText(agent, session));
    }

    [Fact]
    public async Task Save_Delete_ThenGet_RemovesRowAndReturnsNewSession()
    {
        // DeleteSessionAsync 语义：删行后 Get 回到未命中（全新空会话），且库中无残留行
        var sessionDbPath = NewDbPath("sessions");
        var sessionConnection = $"Data Source={sessionDbPath}";
        const string threadId = "thread-delete";
        var agent = CreateBareAgent("StoreAgent");
        var session = await agent.CreateSessionAsync();
        Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider).GetMessages(session)
            .Add(new Meai.ChatMessage(Meai.ChatRole.User, "要被删除的会话"));

        var store = new SqliteAgentSessionStore(sessionConnection);
        await store.InitializeAsync();
        await store.SaveSessionAsync(agent, threadId, session);
        Assert.Equal(1, await CountSessionRowsAsync(sessionConnection, $"StoreAgent:{threadId}"));

        await store.DeleteSessionAsync(agent, threadId);

        Assert.Equal(0, await CountSessionRowsAsync(sessionConnection, $"StoreAgent:{threadId}"));
        var afterDelete = await store.GetSessionAsync(agent, threadId);
        Assert.NotSame(session, afterDelete);
        Assert.Equal(string.Empty, HistoryText(agent, afterDelete));
    }

    [Fact]
    public async Task AddAguiBaseServices_PlusAddAguiSessionStore_KeyedStoreResolvesToSqliteStore_IndependentDb()
    {
        // 装配断言（tasks T12 验收 3）：keyed AgentSessionStore（key="AGUIShopping"）解析为 SqliteAgentSessionStore
        // 且指向独立会话库（非 Noop、非 aishop.db）——MapAGUIServer 按 agent.Name keyed 命中该 store 的前提
        var sessionDbPath = NewDbPath("sessions");
        var sessionConnection = $"Data Source={sessionDbPath}";
        var efDbPath = NewDbPath("ef");
        var ragDbPath = NewDbPath("rag");

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton(config);
        services.AddAguiBaseServices(config, $"Data Source={efDbPath}", $"Data Source={ragDbPath}");
        services.AddAguiSessionStore(sessionDbConnection: sessionConnection);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetKeyedService<AgentSessionStore>(AGUIShoppingAgent.AgentName);

        // 解析为持久 store（非 Noop 默认 ephemeral）
        var sqliteStore = Assert.IsType<SqliteAgentSessionStore>(store);
        // 指向传入的独立会话库连接串（老 aishop.db 零接触）
        Assert.Equal(sessionConnection, sqliteStore.ConnectionString);
        Assert.DoesNotContain("aishop.db", sqliteStore.ConnectionString);

        // store 初始化在独立库文件生成 agent_sessions 表（连接串生效，非回退默认）
        await sqliteStore.InitializeAsync();
        Assert.True(File.Exists(sessionDbPath));
    }

    /// <summary>直连会话库统计某 store_id 的会话行数。</summary>
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
