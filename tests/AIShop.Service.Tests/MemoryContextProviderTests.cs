#pragma warning disable MAAI001 // Microsoft.Agents.AI Experimental（AIContextProvider 上下文类型）
using AIShop.Core.Interfaces;
using AIShop.Service.Providers;
using Mem0Sharp;
using Microsoft.Agents.AI;
using NSubstitute;
using Meai = Microsoft.Extensions.AI;

namespace AIShop.Service.Tests;

/// <summary>
/// MemoryContextProvider 单元测试（重构 PreferenceMemoryProvider 后的 Mem0 记忆 Provider）。
///
/// 验证行为：
/// - 提供（ProvideAIContextAsync）：语义检索当前用户记忆 → 注入 Instructions「## 用户长期记忆」；
///   无记忆 / 空查询 / 无当前用户时原样返回，不检索。
/// - 存储（StoreAIContextAsync）：对话后把 request+response 消息交给 Mem0 自动提取入库（Infer=true）。
///
/// 通过 mock IMemoryService 隔离 Mem0 内部（LLM 提取/门控/精排/向量），只测 Provider 的组装与分派。
/// </summary>
public sealed class MemoryContextProviderTests
{
    /// <summary>mock 一个 AIAgent（InvokingContext 构造要求非空，内容无关）。</summary>
    private static readonly AIAgent s_agent = Substitute.For<AIAgent>();

    private static MemoryContextProvider CreateProvider(IMemoryService memory, string? userId)
    {
        var currentUser = Substitute.For<ICurrentUserAccessor>();
        currentUser.CurrentUser.Returns(userId);
        return new MemoryContextProvider(memory, currentUser);
    }

    /// <summary>Mock IMemoryService：SearchAsync 返回给定记忆（默认空），AddAsync 返回空结果。</summary>
    private static IMemoryService CreateMemory(IReadOnlyList<SearchResult>? results = null)
    {
        var memory = Substitute.For<IMemoryService>();
        memory.SearchAsync(
                Arg.Any<string>(), Arg.Any<MemorySearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>(results ?? Array.Empty<SearchResult>()));
        // Store 的 AddAsync 需返回非 null Task<AddResult>，否则 await 空引用崩溃
        memory.AddAsync(
                Arg.Any<IEnumerable<Message>>(), Arg.Any<MemoryAddOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AddResult([])));
        return memory;
    }

    // ── Provide：记忆注入 ──

    [Fact]
    public async Task Provide_WithMemory_InjectUserLongTermMemoryInstructions()
    {
        var memory = CreateMemory([
            new SearchResult(new Memory { Id = "1", Text = "用户喜欢咖啡", UserId = "u1" }, 0.92),
        ]);
        var provider = CreateProvider(memory, "u1");
        var context = new AIContextProvider.InvokingContext(s_agent, null,
            new AIContext { Messages = [new Meai.ChatMessage(Meai.ChatRole.User, "推荐点咖啡")] });

        var result = await provider.InvokingAsync(context);

        Assert.NotNull(result.Instructions);
        Assert.Contains("## 用户长期记忆", result.Instructions);
        Assert.Contains("用户喜欢咖啡", result.Instructions);

        // 检索按当前用户过滤 + TopK=3（MemoryContextProvider.TopK）
        await memory.Received(1).SearchAsync(
            Arg.Any<string>(),
            Arg.Is<MemorySearchOptions>(o => o.Filter != null && o.Filter.UserId == "u1" && o.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provide_WithoutMemory_ReturnsOriginalContext()
    {
        var memory = CreateMemory(); // 空检索结果
        var provider = CreateProvider(memory, "u1");
        var context = new AIContextProvider.InvokingContext(s_agent, null,
            new AIContext { Messages = [new Meai.ChatMessage(Meai.ChatRole.User, "你好")] });

        var result = await provider.InvokingAsync(context);

        Assert.Null(result.Instructions); // 无记忆 → 不注入
    }

    [Fact]
    public async Task Provide_EmptyQuery_DoesNotSearch()
    {
        var memory = CreateMemory();
        var provider = CreateProvider(memory, "u1");
        // 消息全为空文本 → query 为空 → 直接返回，不检索
        var context = new AIContextProvider.InvokingContext(s_agent, null,
            new AIContext { Messages = [new Meai.ChatMessage(Meai.ChatRole.User, "")] });

        var result = await provider.InvokingAsync(context);

        Assert.Null(result.Instructions);
        await memory.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<MemorySearchOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provide_WithoutCurrentUser_DoesNotSearch()
    {
        var memory = CreateMemory();
        var provider = CreateProvider(memory, null); // 无当前用户
        var context = new AIContextProvider.InvokingContext(s_agent, null,
            new AIContext { Messages = [new Meai.ChatMessage(Meai.ChatRole.User, "你好")] });

        var result = await provider.InvokingAsync(context);

        Assert.Null(result.Instructions);
        await memory.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<MemorySearchOptions>(), Arg.Any<CancellationToken>());
    }

    // ── Store：对话后存记忆 ──

    [Fact]
    public async Task Store_WithUserId_CallsAddAsyncWithUserMessagesOnly()
    {
        var memory = CreateMemory();
        var provider = CreateProvider(memory, "u1");
        var context = new AIContextProvider.InvokedContext(
            s_agent, null,
            [new Meai.ChatMessage(Meai.ChatRole.User, "你好")],
            [new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟回复")]);

        await provider.InvokedAsync(context);

        // 只存用户消息（不存 assistant 回复），Infer=true 触发提取，Behavior 当前为 Normal（记用户事实）
        await memory.Received(1).AddAsync(
            Arg.Is<IEnumerable<Message>>(msgs =>
                msgs.Select(m => m.Role).SequenceEqual(new[] { "user" })
                && msgs.Select(m => m.Content).SequenceEqual(new[] { "你好" })),
            Arg.Is<MemoryAddOptions>(o => o.UserId == "u1" && o.Infer
                && o.Behavior == MemoryBehavior.Normal),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Store_WithoutCurrentUser_DoesNotCallAddAsync()
    {
        var memory = CreateMemory();
        var provider = CreateProvider(memory, null);
        var context = new AIContextProvider.InvokedContext(
            s_agent, null,
            [new Meai.ChatMessage(Meai.ChatRole.User, "你好")],
            [new Meai.ChatMessage(Meai.ChatRole.Assistant, "模拟回复")]);

        await provider.InvokedAsync(context);

        await memory.DidNotReceive().AddAsync(
            Arg.Any<IEnumerable<Message>>(), Arg.Any<MemoryAddOptions>(), Arg.Any<CancellationToken>());
    }
}
