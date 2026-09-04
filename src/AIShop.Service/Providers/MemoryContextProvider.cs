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

#pragma warning disable S2971 // LINQ expressions should be simplified
        var query = context.AIContext.Messages?.Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text)).LastOrDefault()?.Text ?? "";
#pragma warning restore S2971 // LINQ expressions should be simplified

        var userId = _currentUser.CurrentUser;

        // 不产生新消息的分支必须返回空 AIContext（Messages=null）：
        // MAF AIContextProvider 基类会把返回的 Messages 与原 input 做 concat merge，
        // 若返回带 messages 的 context.AIContext，user 消息会被合并两次 → chat_messages 双写。
        if (string.IsNullOrWhiteSpace(query) || userId is null)
            return new AIContext();

        var memories = await _memory.SearchAsync(query, new MemorySearchOptions
        {
            Filter = new MemoryFilter { UserId = userId },
            TopK = TopK,
            Threshold = 0.3,        // 过滤低相关结果
            Hybrid = true,           // 确保混合搜索开启
        }, cancellationToken);

        // 无记忆命中：同样返回空 AIContext（不注入记忆、不带 messages，避免 merge 重复）
        return memories.Any()
            ? new AIContext { Instructions = "## 用户长期记忆\n" + string.Join("\n", memories.Select(r => r.Memory.Text)) }
            : new AIContext();
    }

    /// <inheritdoc />
    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.CurrentUser;
        if (userId is null)
            return ValueTask.CompletedTask;

        // 只取用户消息存记忆（偏好从用户意图提取）；物化避免惰性枚举在后台执行时依赖已释放上下文
        var messages = (context.RequestMessages ?? [])
            .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new Message("user", m.Text))
            .ToList();

        if (messages.Count > 0)
        {
            // 记忆提取（Infer=true 触发 LlmMemoryExtractor 调 LLM，慢）放后台异步执行，不阻塞对话响应。
            // MemoryService 为 DI 单例（不依赖请求 scope），后台任务安全；异常在此捕获避免 unobserved exception。
            _ = PersistMemoryAsync(messages, userId, context.Agent.Id);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>后台持久化记忆：Mem0 提取（LLM）+ 门控 + 向量入库；失败仅记日志不影响对话。</summary>
    private async Task PersistMemoryAsync(IReadOnlyList<Message> messages, string userId, string? agentId)
    {
        try
        {
            // AddAsync(IEnumerable<Message>) 已支持多消息一次提取；AddManyAsync 只收 string 文本（不适用）
            await _memory.AddAsync(messages, new MemoryAddOptions
            {
                UserId = userId,
                Infer = true,
                AgentId = agentId,
                Behavior = MemoryBehavior.Normal,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "记忆提取后台失败（不影响对话响应）");
        }
    }
}
