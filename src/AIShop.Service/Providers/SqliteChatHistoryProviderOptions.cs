using System.Text.Json;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AIShop.Service.Providers;

/// <summary>
/// Options for configuring <see cref="SqliteChatHistoryProvider"/>.
/// 对齐官方 Provider 的 options 模式：压缩参数、StateKey、过滤器均可配置。
/// </summary>
public sealed class SqliteChatHistoryProviderOptions
{
    /// <summary>压缩保留的最大完整轮数（K=12，偏保守，容量约等于旧 50 条硬切）。</summary>
    public int MaxCompletedRounds { get; set; } = 12;

    /// <summary>未完成轮上限（5 个）：未完成轮堆积超限时强制压最旧未完成轮并记 Warning（T7 安全阀 1，spec #11）。</summary>
    public int MaxIncompleteRounds { get; set; } = 5;

    /// <summary>条数硬上限（4K 条）：保留区未压缩条数超限时强制压最旧整轮并记 Warning（T7 安全阀 2，spec #10）。</summary>
    public int MaxMessagesHardLimit { get; set; } = 4096;

    /// <summary>可选的 StateBag 存储键，默认使用类名。</summary>
    public string? StateKey { get; set; }

    /// <summary>可选的 JSON 序列化选项，用于序列化 Provider 状态。</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>可选的过滤器：检索历史时的输出过滤器。</summary>
    public Func<IEnumerable<AgentChatMessage>, IEnumerable<AgentChatMessage>>? ProvideOutputMessageFilter { get; set; }

    /// <summary>可选的过滤器：存储前的请求消息过滤器。</summary>
    public Func<IEnumerable<AgentChatMessage>, IEnumerable<AgentChatMessage>>? StoreInputRequestMessageFilter { get; set; }

    /// <summary>可选的过滤器：存储前的响应消息过滤器。</summary>
    public Func<IEnumerable<AgentChatMessage>, IEnumerable<AgentChatMessage>>? StoreInputResponseMessageFilter { get; set; }
}
