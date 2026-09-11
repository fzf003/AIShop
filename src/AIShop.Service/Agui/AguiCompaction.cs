#pragma warning disable MAAI001 // ContextWindowCompactionStrategy 为 MAF [Experimental]（上下文压缩 API）
using Microsoft.Agents.AI.Compaction;

namespace AIShop.Service.Agui;

/// <summary>
/// AguiHost 上下文压缩策略的<b>唯一阈值来源</b>（agui-session-prod S1）。
/// 常量与 <see cref="CreateStrategy"/> 由 Agent 装配（<c>AGUIShoppingAgent.Create</c>）与会话快照收敛
/// （<c>SqliteAgentSessionStore.SaveSessionAsync</c>，S4）共用同一份定义——DI 注册单例后两侧注入同一实例，
/// 从结构上消除「阈值在两处各写一遍」导致的配置漂移（glossary「配置漂移」）。
/// </summary>
/// <remarks>
/// 阈值对齐老 <c>ShoppingAssistantAgent.cs</c> L140（maxContextWindowTokens: 128000 / maxOutputTokens: 16384 /
/// toolEvictionThreshold: 0.5 / truncationThreshold: 0.8）。
/// <see cref="ContextWindowCompactionStrategy"/> 为 MAF <c>[Experimental]</c>（MAAI001），文件顶已禁用该诊断。
/// </remarks>
public static class AguiCompaction
{
    /// <summary>模型上下文窗口上限（token，对齐老 ShoppingAssistantAgent L140）。</summary>
    public const int MaxContextWindowTokens = 128000;

    /// <summary>预留给模型输出的 token 上限（对齐老 ShoppingAssistantAgent L140）。</summary>
    public const int MaxOutputTokens = 16384;

    /// <summary>工具结果驱逐阈值（占输入预算比例，对齐老 ShoppingAssistantAgent L140）。</summary>
    public const double ToolEvictionThreshold = 0.5;

    /// <summary>截断阈值（占输入预算比例，对齐老 ShoppingAssistantAgent L140）。</summary>
    public const double TruncationThreshold = 0.8;

    /// <summary>
    /// 按本类常量构造一个全新的 <see cref="ContextWindowCompactionStrategy"/>。
    /// DI 单例与 Agent/store 的缺省回退都调用本方法，保证阈值单一来源。
    /// </summary>
    public static ContextWindowCompactionStrategy CreateStrategy() =>
        new(
            maxContextWindowTokens: MaxContextWindowTokens,
            maxOutputTokens: MaxOutputTokens,
            toolEvictionThreshold: ToolEvictionThreshold,
            truncationThreshold: TruncationThreshold);
}
