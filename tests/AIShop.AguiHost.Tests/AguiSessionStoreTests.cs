using AIShop.AguiHost;
using AIShop.Infrastructure.Services;
using AIShop.Service;
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

    [Fact]
    public async Task OldDatabases_AreNotTouchedBySessionStoreLifecycle()
    {
        // R8 场景 3「老库零接触」（S7 补充断言）：
        // 1) 会话库【缺省】连接串指向 agui.sessions.db、不含 aishop —— 与老 EF/RAG 库结构性隔离。
        Assert.Equal("Data Source=agui.sessions.db", AguiServiceCollectionExtensions.DefaultSessionDbConnection);
        Assert.DoesNotContain("aishop", AguiServiceCollectionExtensions.DefaultSessionDbConnection);

        // 2) 三个老库（Api 业务库 / Api RAG 库 / McpServer 业务库）在完整 store 生命周期前后逐字节不变。
        //    S8 修复：老库路径相对【仓库根】定位（自测试输出目录上溯找 AIShop.sln），不依赖测试进程 CWD——
        //    旧实现用相对 CWD 的路径解析，该目录无老库 → 前后快照两端都为 MISSING → 相等断言恒真（空转 /
        //    false assurance）。此处「任一老库缺失即显式失败」，若路径再次解析错，用例必须失败而非静默通过。
        var oldDbPaths = ResolveOldDatabasePaths();
        foreach (var oldDbPath in oldDbPaths)
            Assert.True(File.Exists(oldDbPath), $"期望存在的老库未找到，零接触断言无法生效（路径解析错误？）：{oldDbPath}");

        var before = SnapshotFiles(oldDbPaths);

        // 跑一遍会触发全部写/读/清理路径的 store 生命周期（仅作用于独立临时会话库）
        var sessionDbPath = NewDbPath("olddb");
        var connectionString = $"Data Source={sessionDbPath}";
        var agent = CreateBareAgent("OldDbAgent");
        var session = await agent.CreateSessionAsync();
        Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider).GetMessages(session)
            .Add(new Meai.ChatMessage(Meai.ChatRole.User, "触发 store 写路径（不得触碰老库）"));

        var store = new SqliteAgentSessionStore(connectionString);
        await store.InitializeAsync();
        await store.SaveSessionAsync(agent, "thread-olddb", session);
        await store.GetSessionAsync(agent, "thread-olddb");
        await store.CleanupExpiredAsync(AguiSessionOptions.DefaultSessionTtlDays);
        await store.DeleteSessionAsync(agent, "thread-olddb");

        Assert.Equal(before, SnapshotFiles(oldDbPaths));
    }

    /// <summary>
    /// 定位仓库根（自测试程序集输出目录 <see cref="AppContext.BaseDirectory"/> 逐级上溯，找含 AIShop.sln 的目录），
    /// 并据此解析三个老库（Api 业务库 / Api RAG 库 / McpServer 业务库）的绝对路径。
    /// 老库路径相对【仓库根】而非测试进程 CWD——用相对 CWD 的解析在 xUnit 下会指向输出目录（无老库），
    /// 使前后快照两端都为 MISSING、断言空转。定位不到仓库根时抛异常（用例显式失败，不退化通过）。
    /// </summary>
    private static string[] ResolveOldDatabasePaths()
    {
        var repoRoot = FindRepositoryRoot()
            ?? throw new InvalidOperationException(
                $"未找到仓库根：自测试输出目录 {AppContext.BaseDirectory} 逐级上溯均未见 AIShop.sln，无法定位老库做零接触断言");
        return
        [
            Path.Combine(repoRoot, "src", "AIShop.Api", "aishop.db"),
            Path.Combine(repoRoot, "src", "AIShop.Api", "aishop.rag.db"),
            Path.Combine(repoRoot, "src", "AIShop.McpServer", "aishop.db"),
        ];
    }

    /// <summary>自 <see cref="AppContext.BaseDirectory"/> 逐级上溯，返回首个含 AIShop.sln 的目录；找不到返回 null。</summary>
    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AIShop.sln")))
                return dir.FullName;
        }

        return null;
    }

    /// <summary>
    /// 快照一组文件的存在性/大小/最后写入时间（供「老库零接触」前后逐项比对）。
    /// 快照项带前缀区分：EXISTS 含 size 与 mtime，「文件不存在」以 MISSING 单独标记，
    /// 使「不存在」这一退化情形无法与「存在且未变」混淆。
    /// </summary>
    private static string[] SnapshotFiles(IEnumerable<string> paths)
        => [.. paths.Select(p => File.Exists(p)
            ? $"EXISTS:{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}"
            : $"MISSING:{p}")];

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
