namespace AIShop.Service.Agui;

/// <summary>
/// AguiHost AG-UI 会话 <see cref="Microsoft.Agents.AI.AgentSession.StateBag"/> 的共享状态键。
/// </summary>
/// <remarks>
/// 为什么独立出一个中立的键常量类型，而不是由 store 直接引用 <see cref="SqlChatHistoryProvider"/> 的常量：
/// 写入方 <see cref="SqliteAgentSessionStore"/> 是<b>与具体 ChatHistoryProvider 无关</b>的通用会话持久化设施，
/// 读取方 <see cref="SqlChatHistoryProvider"/> 是其中一种（未来可能换 / 并存多种）provider。若 store 反向引用
/// provider 的常量，就把「会话持久化」语义绑死在「SQL 聊天历史」这一实现上。把键抽到双方共同依赖的中立类型，
/// 依赖方向保持 store（通用）←→ 键 ←→ provider（特化），store 不感知具体 provider。
/// </remarks>
public static class AguiSessionStateKeys
{
    /// <summary>
    /// 会话标识键：值为 AG-UI <c>ThreadId</c>（design-sql-chat-history-provider §2.2「会话标识（ThreadId）」）。
    /// 由 <see cref="SqliteAgentSessionStore"/> 取会话时写入，<see cref="SqlChatHistoryProvider"/> 初始化会话状态时读取，
    /// 作为 <c>chat_messages.conversation_id</c>——使 §1.3 / §10.3 的审计 / 召回可按 <c>conversation_id = ThreadId</c> 查询。
    /// </summary>
    public const string ConversationId = "AguiConversationId";
}
