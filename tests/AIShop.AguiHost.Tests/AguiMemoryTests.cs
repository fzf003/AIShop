#pragma warning disable MAAI001 // AIContextProvider/CompactionProvider 为 MAF [Experimental]，测试内引用类型亦触发诊断
using System.Collections.Concurrent;
using AIShop.AgentTelemetry;
using AIShop.AguiHost;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.MemoryService;
using AIShop.Service;
using AIShop.Service.Agui;
using AIShop.Service.Providers;
using AIShop.Service.Tools;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// 本集合会加载 bge 模型 + 建多个临时 SQLite 文件库，置 DisableParallelization 串行集合：
/// 避免并行写入同一 SQLite 文件库（learnings 先例同 AguiStartupSeedingTests）。
/// </summary>
[CollectionDefinition(nameof(AguiMemoryTests), DisableParallelization = true)]
public sealed class AguiMemoryTestsCollection;

/// <summary>
/// T13 Mem0 跨会话记忆（独立库 + MemoryContextProvider 挂载 + 写入时机）验收测试：
/// ① 独立记忆库参数生效（AddMemoryService 可选路径 / AddAguiBaseServices memoryDatabasePath → 独立库建 memories 表）；
/// ② AGUIShopping 装配含记忆 provider（与 CompactionProvider 并列；依赖缺省时不挂）；
/// ③ 写入时机 + 读注入：直接驱动真实 <see cref="ChatClientAgent"/>（AGUIShopping 运行时类型）run——
///    run 开始 Provide 召回记忆注入 Instructions（mock IChatClient 捕获 ChatOptions），run 结束 Store
///    触发 IMemoryService.AddAsync（后台）—— 证明读写触发点在装配内接通。
/// 说明（取舍）：跨会话「提取→召回」完整闭环依赖真实 Mem0 LLM 提取 + 向量库，属老 MemoryService 已覆盖行为
/// （MemoryContextProviderTests / 老偏好链路）；此处以 mock IMemoryService + 真实装配面证明读写触发点。
/// AGUI MapAGUIServer 请求管线的用户名流向已由 AguiRequestTests 覆盖，真实记忆 E2E 移交人工验收
/// （tasks T13「尽力而为」降级）。
/// </summary>
[Collection(nameof(AguiMemoryTests))]
public sealed class AguiMemoryTests : IDisposable
{
    private const string ReplyText = "记忆链路已接通 AGUI-MEMORY-MARKER 专业跑鞋 ¥129.99";
    private const string ReplyMarker = "AGUI-MEMORY-MARKER";

    private readonly List<string> _createdDbPaths = [];

    public void Dispose()
    {
        // 释放 SQLite 连接池对文件的句柄后，删除本测试创建的临时 db 文件（锁未完全释放时忽略，交由系统清理）
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

    private string NewDbPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agui_mem_{Guid.NewGuid():N}_{suffix}.db");
        _createdDbPaths.Add(path);
        return path;
    }

    private static IConfiguration BuildMinimalConfig()
    {
        // 最小 Models 节：ModelRouter 构造要求存在且至少一个模型（ActiveModel=qwen → 非 DeepSeek 路径，
        // GetDefaultChatClient 构建离线 chatClient 不联网）；AgentTelemetry:Level 供遥测选项绑定
        var data = new Dictionary<string, string?>
        {
            ["Models:qwen:Endpoint"] = "https://example.com/v1",
            ["Models:qwen:Model"] = "qwen3-test",
            ["Models:qwen:Name"] = "Qwen",
            ["Models:qwen:Key"] = "test-key",
            ["ActiveModel"] = "qwen",
            ["AgentTelemetry:Level"] = "Metadata",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static ServiceProvider BuildProvider(string dbConnection, string ragConnection, string memoryPath)
    {
        var services = new ServiceCollection();
        services.AddAguiBaseServices(BuildMinimalConfig(), dbConnection, ragConnection, memoryPath);
        return services.BuildServiceProvider();
    }

    // ── ① 独立记忆库 ──

    [Fact]
    public void DefaultMemoryDatabasePath_IsIndependentAguiMemoryDb_NotAishopDb()
    {
        // AguiHost 缺省记忆库 = 独立文件 agui.memory.db（数据隔离，spec 验收 5；老 aishop.db 零接触）
        Assert.Equal("agui.memory.db", AguiServiceCollectionExtensions.DefaultMemoryDatabasePath);
        Assert.NotEqual("aishop.db", AguiServiceCollectionExtensions.DefaultMemoryDatabasePath);
    }

    [Fact]
    public async Task AddAguiBaseServices_WithCustomMemoryPath_InitializeCreatesIndependentMemoryDb()
    {
        var efPath = NewDbPath("ef");
        var ragPath = NewDbPath("rag");
        var memPath = NewDbPath("mem");

        // 底座 DI：AddAguiBaseServices 把 memoryDatabasePath 传入 AddMemoryService（独立记忆库）；
        // InitializeAsync 预热 SqliteMemoryStore → 在 memPath 建 memories / memory_history 表。
        // SqliteMemoryStore 为 IAsyncDisposable 单例且此处实例化 → 容器须 await using 释放
        await using (var sp = BuildProvider($"Data Source={efPath}", $"Data Source={ragPath}", memPath))
        {
            var store = sp.GetRequiredService<SqliteMemoryStore>();
            Assert.NotNull(store);
            Assert.NotNull(sp.GetRequiredService<IMemoryStore>());

            await AguiServiceCollectionExtensions.InitializeAsync(sp);
        }

        Assert.True(File.Exists(memPath), "memoryDatabasePath 应使独立记忆库在指定路径生成（老 aishop.db 零接触）");
        Assert.Equal(2, await CountMemoriesTablesAsync(memPath));
    }

    // ── ② Provider 挂载（AGUIShopping 装配面） ──

    [Fact]
    public void Create_WithMemoryServiceAndCurrentUser_MountsMemoryContextProviderAlongsideCompaction()
    {
        var agent = CreateAgent(memory: Substitute.For<IMemoryService>(), currentUser: Substitute.For<ICurrentUserAccessor>());

        // Level.None → 裸 ChatClientAgent；AIContextProviders 应恰含 1 个 CompactionProvider + 1 个 MemoryContextProvider
        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(chatClientAgent.AIContextProviders);

        Assert.Single(chatClientAgent.AIContextProviders.OfType<CompactionProvider>());
        Assert.Single(chatClientAgent.AIContextProviders.OfType<MemoryContextProvider>());
        Assert.Equal(2, chatClientAgent.AIContextProviders.Count);
    }

    [Fact]
    public void Create_WithoutMemoryDependencies_KeepsOnlyCompactionProvider()
    {
        var agent = CreateAgent(memory: null, currentUser: null);

        // 回归（T4/T8 装配断言不回退）：不传记忆依赖 → 不挂 MemoryContextProvider，仍仅 1 个 CompactionProvider
        var chatClientAgent = Assert.IsType<ChatClientAgent>(agent);
        Assert.NotNull(chatClientAgent.AIContextProviders);
        Assert.Single(chatClientAgent.AIContextProviders.OfType<CompactionProvider>());
        Assert.Empty(chatClientAgent.AIContextProviders.OfType<MemoryContextProvider>());
    }

    // ── ③ 写入时机 + 读注入（真实 ChatClientAgent 引擎离线驱动） ──

    [Fact]
    public async Task ChatClientAgent_Run_ProvideInjectsMemoryAndStoreTriggersAddAsync()
    {
        // mock 记忆服务：SearchAsync 返回一条固定记忆（读注入用）；AddAsync 记录调用（写触发用）
        var memory = Substitute.For<IMemoryService>();
        memory.SearchAsync(
                Arg.Any<string>(), Arg.Any<MemorySearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(
                [new SearchResult(new Memory { Id = "mem1", Text = "用户偏好黑咖啡", UserId = "marla" }, 0.95)]));
        var addOptions = new ConcurrentQueue<MemoryAddOptions>();
        memory.AddAsync(
                Arg.Do<IEnumerable<Message>>(_ => { }),
                Arg.Do<MemoryAddOptions>(o => addOptions.Enqueue(o)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AddResult([])));

        // mock chatClient：捕获流式请求收到的 ChatOptions（断言记忆 Instructions 注入）；返回固定回复文本
        Meai.ChatOptions? capturedChatOptions = null;
        var mockChat = Substitute.For<Meai.IChatClient>();
        mockChat.GetResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Do<Meai.ChatOptions?>(o => capturedChatOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new Meai.ChatResponse(new Meai.ChatMessage(Meai.ChatRole.Assistant, ReplyText)));
        mockChat.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<Meai.ChatMessage>>(),
                Arg.Do<Meai.ChatOptions?>(o => capturedChatOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(StreamingTextAsync());

        // 固定返回 marla 的 accessor stub：MemoryContextProvider 按用户读写记忆
        var accessor = Substitute.For<ICurrentUserAccessor>();
        accessor.CurrentUser.Returns("marla");

        var agent = Assert.IsType<ChatClientAgent>(AGUIShoppingAgent.Create(
            mockChat,
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            memoryService: memory,
            currentUser: accessor));

        // 消费完整流：ChatClientAgent run 开始 Provide（记忆注入 Instructions）、流结束 Store（AddAsync 后台触发）
        var responseText = string.Empty;
        await foreach (var update in agent.RunStreamingAsync(
                             new Meai.ChatMessage(Meai.ChatRole.User, "你好，帮我推荐一双跑步鞋，我平时爱喝黑咖啡")))
        {
            responseText += update.Text;
        }

        Assert.Contains(ReplyMarker, responseText);

        // 读注入：run 开始 Provide 语义召回用户记忆 → 记忆文本并入 ChatOptions.Instructions（进入 LLM system prompt）
        Assert.NotNull(capturedChatOptions?.Instructions);
        Assert.Contains("用户偏好黑咖啡", capturedChatOptions.Instructions);
        await memory.Received(1).SearchAsync(
            Arg.Any<string>(),
            Arg.Is<MemorySearchOptions>(o => o.Filter != null && o.Filter.UserId == "marla"),
            Arg.Any<CancellationToken>());

        // 写触发：流结束 Store → MemoryContextProvider 后台把本轮用户消息经 IMemoryService.AddAsync 落独立记忆库。
        // AddAsync 是 fire-and-forget 后台任务，轮询避免时序竞态（MemoryContextProvider 注释：后台提取不阻塞对话响应）。
        await WaitUntilAsync(() => !addOptions.IsEmpty, "记忆写入未触发（ChatClientAgent run 结束应调用 MemoryContextProvider.Store）");
        Assert.Contains(addOptions, o => o.UserId == "marla" && o.Infer);
    }

    // ── 装配辅助 ──

    private static AIAgent CreateAgent(IMemoryService? memory, ICurrentUserAccessor? currentUser)
    {
        var chatClient = Substitute.For<Meai.IChatClient>();
        // Level.None：Instrument 裸返回原 ChatClientAgent（不包 OpenTelemetryAgent 装饰器），便于断言装配面
        return AGUIShoppingAgent.Create(
            chatClient,
            CreateCartTools(),
            new AgentTelemetryOptions { Level = AgentTelemetryLevel.None },
            memoryService: memory,
            currentUser: currentUser);
    }

    private static CartToolProvider CreateCartTools()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var accessor = Substitute.For<ICurrentUserAccessor>();
        return new CartToolProvider(scopeFactory, accessor);
    }

    private static async IAsyncEnumerable<Meai.ChatResponseUpdate> StreamingTextAsync()
    {
        yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, ReplyText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failMessage, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }
        Assert.Fail(failMessage);
    }

    /// <summary>统计记忆库中 memories / memory_history 两表是否都存在（各计 1，存在应为 2）。</summary>
    private static async Task<int> CountMemoriesTablesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('memories', 'memory_history')";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
