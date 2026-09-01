using AIShop.Core.Interfaces;
using Mem0Sharp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Providers;

/// <summary>
/// 基于 Mem0 的用户偏好记忆 Provider（重构 PreferenceMemoryProvider 的新实现，旧代码不动）。
///
/// 设计：
/// - <see cref="IMemoryService"/> 为 DI 注册的**单例**（有状态，含 LlmMemoryExtractor/门控/精排，见 AddMemoryService），此处只注入
/// - 提供（ProvideAIContextAsync）：语义检索当前用户的记忆，注入 Instructions（偏好应用）
/// - 存储（StoreAIContextAsync）：对话后 Mem0 自动提取事实并入库（Infer=true，更新偏好）
/// </summary>
public sealed class MemoryContextProvider(
    IMemoryService memory,
    ICurrentUserAccessor currentUser) : AIContextProvider
{
    private const int TopK = 3;

    private readonly IMemoryService _memory = memory;
    private readonly ICurrentUserAccessor _currentUser = currentUser;

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // 用当前对话文本作检索查询，语义召回该用户的记忆（偏好）注入
        var query = string.Join(" ", context.AIContext.Messages?
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text) ?? []);
        var userId = _currentUser.CurrentUser;

        if (string.IsNullOrWhiteSpace(query) || userId is null)
            return context.AIContext;

        var memories = await _memory.SearchAsync(query, new MemorySearchOptions
        {
            Filter = new MemoryFilter { UserId = userId },
            TopK = TopK,
        }, cancellationToken);

        return memories.Any()
            ? new AIContext { Instructions = "## 用户长期记忆\n" + string.Join("\n", memories.Select(r => r.Memory.Text)) }
            : context.AIContext;
    }

    /// <inheritdoc />
    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.CurrentUser;
        if (userId is null)
            return;

        // 本轮对话（用户 + 助手）交给 Mem0 提取事实并入库（Infer=true 触发 LlmMemoryExtractor 提取偏好）
        var messages = (context.RequestMessages ?? [])
            .Concat(context.ResponseMessages ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new Message(m.Role == ChatRole.User ? "user" : "assistant", m.Text));

        await _memory.AddAsync(messages, new MemoryAddOptions { UserId = userId, Infer = true }, cancellationToken);
    }
}
