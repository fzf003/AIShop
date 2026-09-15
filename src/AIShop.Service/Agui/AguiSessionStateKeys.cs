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

    /// <summary>
    /// 上下文压缩器（MAF <c>CompactionProvider</c>）的状态键 —— 与 <c>AGUIShoppingAgent.Create</c> 里传给
    /// <c>CompactionProvider(stateKey: …)</c> 的值<b>必须一致</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 该键承载压缩器的<b>消息组索引</b>：组只增不减（压缩策略只把旧组标记 <c>IsExcluded</c>，不删除），
    /// 且<b>不受任何裁剪</b>——S4 的收敛只处理 <c>InMemoryChatHistoryProvider</c> 的消息列表，看不见这个索引。
    /// 故 <see cref="SqliteAgentSessionStore.SaveSessionAsync"/> 在序列化前<b>剔除该键</b>，使落库快照真正有界。
    /// </para>
    /// <para>
    /// 剔除是安全的：压缩器下次运行发现状态为空 → 用当轮消息<b>从零重建索引</b>
    /// （<c>CompactionProvider.InvokingCoreAsync</c> 的 <c>state.MessageGroups.Count == 0</c> 分支；
    /// 新会话第一轮本来就走这条路，是常态路径）。失去的只有「哪些组曾被排除」的跨轮记忆——而压缩在
    /// 本项目的消息规模下从未真正触发（prompt 远低于阈值），该记忆恒为空集。
    /// </para>
    /// <para>
    /// 抽成共享常量的理由同 <see cref="ConversationId"/>：该 key 由 agent 装配侧传入、由 store 侧剔除，
    /// 两处各写一遍字面量必然漂移。
    /// </para>
    /// </remarks>
    public const string Compaction = "AGUIShopping-Compaction";
}
