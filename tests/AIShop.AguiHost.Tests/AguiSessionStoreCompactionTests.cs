#pragma warning disable MAAI001 // CompactionStrategy / CompactionMessageIndex 为 MAF [Experimental]（上下文压缩 API）
using AIShop.AguiHost;
using AIShop.Service;
using AIShop.Service.Agui;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S4 store 级收敛快照测试（agui-session-prod）：<see cref="SqliteAgentSessionStore.SaveSessionAsync"/> 落库前
/// 对 InMemoryChatHistoryProvider 历史做轮归一收敛。覆盖 spec R1（收敛有界 + 失败不阻断落库）/ R2（整轮为最小保留
/// 单位、FCC↔FRC 配对完整，硬约束）/ R3（轮数硬上限）/ R8 快速路径零回归，并验证非 InMemory provider 走跳过分支。
/// </summary>
/// <remarks>
/// 与 <see cref="AguiSessionStoreTests"/> 同集合（串行），避免同一 SQLite 文件并发写入冲突。
/// 落库读回经「新 agent + 新 store 实例」还原，断言以序列化快照为准（消息对象经 JSON 往返为新实例，故按内容而非引用断言）。
/// </remarks>
[Collection(nameof(AguiSessionStoreTests))]
public sealed class AguiSessionStoreCompactionTests : IDisposable
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
                // 文件仍被占用时忽略，交由系统清理
            }
        }
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_s4_{Guid.NewGuid():N}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    /// <summary>构造最小 ChatClientAgent（默认 InMemoryChatHistoryProvider）。</summary>
    private static ChatClientAgent CreateAgent(string name, ChatHistoryProvider? provider = null)
    {
        var chatClient = Substitute.For<Meai.IChatClient>();
        var options = new ChatClientAgentOptions { Name = name, ChatOptions = new Meai.ChatOptions { Instructions = "测试人设" } };
        if (provider is not null)
            options.ChatHistoryProvider = provider;
        return Assert.IsType<ChatClientAgent>(chatClient.AsAIAgent(options));
    }

    private static List<Meai.ChatMessage> MessagesOf(ChatClientAgent agent, AgentSession session)
        => Assert.IsType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider).GetMessages(session);

    /// <summary>拼接消息所有 TextContent 文本。</summary>
    private static string TextOf(Meai.ChatMessage message)
        => string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));

    /// <summary>向会话写入 count 个纯文本轮（每轮 user + assistant）。</summary>
    private static void SeedPlainRounds(ChatClientAgent agent, AgentSession session, int count)
    {
        var messages = MessagesOf(agent, session);
        for (int i = 1; i <= count; i++)
        {
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.User, $"用户{i}"));
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, $"回复{i}"));
        }
    }

    /// <summary>向会话写入 count 个含工具调用轮（user → assistant(FCC) → tool(FRC) → assistant）。</summary>
    private static void SeedToolRounds(ChatClientAgent agent, AgentSession session, int count)
    {
        var messages = MessagesOf(agent, session);
        for (int i = 1; i <= count; i++)
        {
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.User, $"用户{i}"));
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, [new Meai.FunctionCallContent($"call_{i}", "search_product", null)]));
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.Tool, [new Meai.FunctionResultContent($"call_{i}", $"结果{i}")]));
            messages.Add(new Meai.ChatMessage(Meai.ChatRole.Assistant, $"回复{i}"));
        }
    }

    /// <summary>用「新 agent + 新 store 实例」从同库读回会话消息（模拟重启还原 / 只读取未压缩快照）。</summary>
    private static async Task<List<Meai.ChatMessage>> ReadBackAsync(string connectionString, string agentName, string threadId)
    {
        var readAgent = CreateAgent(agentName);
        var readStore = new SqliteAgentSessionStore(connectionString);
        var restored = await readStore.GetSessionAsync(readAgent, threadId);
        return MessagesOf(readAgent, restored);
    }

    [Fact]
    public async Task Save_WithLongPlainSession_CompactsSnapshotStrictlySmallerWithinMaxRounds()
    {
        // spec R1 场景 1 + R3（验收 2）：15 轮会话、上限 5 → 落库快照严格小于原始且保留轮数 <= 上限。
        const string agentName = "CompactionAgent";
        const string threadId = "thread-long";
        const int maxRounds = 5;
        var connection = $"Data Source={NewDbPath()}";

        var agent = CreateAgent(agentName);
        var session = await agent.CreateSessionAsync();
        SeedPlainRounds(agent, session, 15); // 30 条消息

        var store = new SqliteAgentSessionStore(
            connection,
            new AguiSessionOptions { SessionMaxRounds = maxRounds },
            AguiCompaction.CreateStrategy()); // 阈值唯一来源（S1），勿在测试内重复声明
        await store.InitializeAsync();
        await store.SaveSessionAsync(agent, threadId, session);

        var restored = await ReadBackAsync(connection, agentName, threadId);

        int restoredRounds = restored.Count(m => m.Role == Meai.ChatRole.User);
        Assert.True(restored.Count < 30, $"快照消息数应严格小于压缩前（实际 {restored.Count}）");
        Assert.Equal(maxRounds, restoredRounds); // 恰好裁到上限
        Assert.True(restoredRounds <= maxRounds, "保留轮数不得超过硬上限");

        // 受保护的最后 2 轮（含末轮）内容完整保留
        var texts = restored.Select(TextOf).ToList();
        Assert.Contains("用户14", texts);
        Assert.Contains("回复14", texts);
        Assert.Contains("用户15", texts);
        Assert.Contains("回复15", texts);
        // 最旧轮被整轮丢弃
        Assert.DoesNotContain("用户1", texts);
        Assert.DoesNotContain("回复1", texts);
    }

    [Fact]
    public async Task Save_WithToolCallRounds_KeepsRoundsWholeAndPairsCallIdsInSnapshot()
    {
        // spec R2（验收 3，硬约束）：含工具轮收敛后无残缺轮，每个 FCC.CallId 在同快照内有配对 FRC。
        const string agentName = "ToolCompactionAgent";
        const string threadId = "thread-tools";
        const int maxRounds = 3;
        var connection = $"Data Source={NewDbPath()}";

        var agent = CreateAgent(agentName);
        var session = await agent.CreateSessionAsync();
        SeedToolRounds(agent, session, 5); // 5 轮 × 4 条 = 20 条

        var store = new SqliteAgentSessionStore(
            connection,
            new AguiSessionOptions { SessionMaxRounds = maxRounds },
            AguiCompaction.CreateStrategy());
        await store.InitializeAsync();
        await store.SaveSessionAsync(agent, threadId, session);

        var restored = await ReadBackAsync(connection, agentName, threadId);
        var texts = restored.Select(TextOf).ToList();

        // 逐轮「全有或全无」：不存在的轮其 4 条（含 FCC/FRC）一条都不在
        for (int i = 1; i <= 2; i++)
        {
            Assert.DoesNotContain($"用户{i}", texts);
            Assert.DoesNotContain($"回复{i}", texts);
        }

        for (int i = 3; i <= 5; i++)
        {
            Assert.Contains($"用户{i}", texts);   // 提问在
            Assert.Contains($"回复{i}", texts);   // 回复在（无「有回复无提问/有提问无回复」）
        }

        // FCC ↔ FRC 配对完整且仅保留最后 3 轮的工具调用
        var callIds = restored.SelectMany(m => m.Contents).OfType<Meai.FunctionCallContent>().Select(c => c.CallId).ToList();
        var resultIds = restored.SelectMany(m => m.Contents).OfType<Meai.FunctionResultContent>().Select(c => c.CallId).ToList();
        Assert.Equal(new[] { "call_3", "call_4", "call_5" }, callIds); // 保序，最旧轮整轮丢弃
        foreach (var callId in callIds)
            Assert.Contains(callId, resultIds); // 每个 FCC 都有同 CallId 的 FRC（同轮内，因整轮保留）

        // 用户轮数 == 回复轮数 == 3（无残缺轮）
        Assert.Equal(3, restored.Count(m => m.Role == Meai.ChatRole.User));
    }

    [Fact]
    public async Task Save_WithShortSession_FastPathLeavesMessagesUnchanged()
    {
        // spec R8 场景 1（零回归）：轮数 <= ProtectedRounds(2) 走快速路径，快照与现状一致、不写回内存历史。
        const string agentName = "ShortAgent";
        const string threadId = "thread-short";
        var connection = $"Data Source={NewDbPath()}";

        var agent = CreateAgent(agentName);
        var session = await agent.CreateSessionAsync();
        SeedPlainRounds(agent, session, 2); // 4 条

        var listBefore = MessagesOf(agent, session); // backing list 引用
        var store = new SqliteAgentSessionStore(connection, new AguiSessionOptions(), AguiCompaction.CreateStrategy());
        await store.InitializeAsync();
        await store.SaveSessionAsync(agent, threadId, session);

        // 快速路径 → 无收缩 → 不 SetMessages：内存历史 backing list 实例不变
        Assert.Same(listBefore, MessagesOf(agent, session));

        var restored = await ReadBackAsync(connection, agentName, threadId);
        Assert.Equal(4, restored.Count);
        Assert.Equal(new[] { "用户1", "回复1", "用户2", "回复2" }, restored.Select(TextOf).ToList());
    }

    [Fact]
    public async Task Save_WhenCompactionThrows_LogsWarningAndStillPersistsOriginalSnapshot()
    {
        // spec R1 场景 2：压缩异常被捕获（仅 Warning），会话仍以原样快照成功落库（不抛出、不丢会话）。
        const string agentName = "ThrowAgent";
        const string threadId = "thread-throw";
        var connection = $"Data Source={NewDbPath()}";

        var agent = CreateAgent(agentName);
        var session = await agent.CreateSessionAsync();
        SeedPlainRounds(agent, session, 5); // 10 条：轮数 > 2，确保绕过快速路径、真正进入压缩

        var throwing = new ThrowingStrategy();
        var store = new SqliteAgentSessionStore(
            connection,
            new AguiSessionOptions { SessionMaxRounds = 2 },
            throwing);
        await store.InitializeAsync();

        // 不抛出（压缩异常被吞并降级）
        await store.SaveSessionAsync(agent, threadId, session);
        Assert.True(throwing.Calls > 0, "应确实尝试了压缩（自定义策略被调用）");

        // 原样快照落库：10 条消息全部保留
        var restored = await ReadBackAsync(connection, agentName, threadId);
        Assert.Equal(10, restored.Count);
        Assert.Equal(5, restored.Count(m => m.Role == Meai.ChatRole.User));
    }

    [Fact]
    public async Task Save_WhenHistoryProviderNotInMemory_SkipsCompaction()
    {
        // spec R1 防御：ChatHistoryProvider 非 InMemoryChatHistoryProvider → 跳过压缩。
        // 用「计数 + 抛异常」策略：若压缩被误调用，计数 > 0 或抛异常使测试失败。
        const string agentName = "SkipAgent";
        const string threadId = "thread-skip";
        var connection = $"Data Source={NewDbPath()}";

        var agent = CreateAgent(agentName, provider: new StubChatHistoryProvider());
        Assert.IsNotType<InMemoryChatHistoryProvider>(agent.ChatHistoryProvider);
        var session = await agent.CreateSessionAsync();

        var strategy = new ThrowingStrategy();
        var store = new SqliteAgentSessionStore(
            connection,
            new AguiSessionOptions { SessionMaxRounds = 1 },
            strategy);
        await store.InitializeAsync();

        await store.SaveSessionAsync(agent, threadId, session); // 不抛出

        Assert.Equal(0, strategy.Calls); // 压缩分支被跳过，策略从未被触发
        Assert.Equal(1, await CountSessionRowsAsync(connection, $"{agentName}:{threadId}")); // 仍正常落库
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

    /// <summary>非 InMemory 的 ChatHistoryProvider（基类所有成员均 virtual，空子类即可），用于验证跳过压缩分支。</summary>
    private sealed class StubChatHistoryProvider : ChatHistoryProvider;

    /// <summary>
    /// 测试用压缩策略：触发器恒触发，<c>CompactCoreAsync</c> 计数后抛异常——
    /// 用于验证「压缩失败不阻断落库」（R1 场景 2）与「非 InMemory provider 跳过压缩」（计数为 0）。
    /// </summary>
    private sealed class ThrowingStrategy : CompactionStrategy
    {
        internal ThrowingStrategy()
            : base(CompactionTriggers.Always)
        {
        }

        internal int Calls { get; private set; }

        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("模拟压缩失败");
        }
    }
}
