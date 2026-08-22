namespace AIShop.Service;

/// <summary>
/// 流式聊天输出的单个 chunk。
/// </summary>
public sealed record ChatStreamChunk
{
    /// <summary>文本增量片段（已清洗商品 ID），流式过程中逐块推送。</summary>
    public required string TextDelta { get; init; }

    /// <summary>是否为最后一个 chunk。true 时 FullResult 必有值。</summary>
    public required bool IsComplete { get; init; }

    /// <summary>完整结果（仅 IsComplete=true 时有值），含 Reply/Keywords/Preferences。</summary>
    public AgentChatResult? FullResult { get; init; }
}
