using AIShop.Core.Models;

namespace AIShop.Core.Interfaces;

/// <summary>
/// 压缩策略 — 纯领域规则，无 IO。输入未压缩消息集合，输出要标记为已压缩的行 ID。
/// 接口形态对齐 MAF <c>CompactionStrategy</c>：策略可插拔，换实现不影响 Provider。
/// </summary>
public interface IChatCompactionPolicy
{
    /// <summary>
    /// 决定哪些消息行应标记为已压缩（IsCompacted=true，非物理删除）。
    /// 输入应为未压缩集合（IsCompacted==false）；实现内部亦可防御性过滤。
    /// </summary>
    IReadOnlyList<long> SelectCompaction(IReadOnlyList<StoredMessage> messages);
}
