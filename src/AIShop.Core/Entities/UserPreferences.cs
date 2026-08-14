namespace AIShop.Core.Entities;

/// <summary>
/// 用户偏好持久化实体：主键 UserId，KeywordsJson 存 {关键词: 权重} 的 JSON 对象。
/// 由偏好写入队列异步落库（design 4.2），每次 /chat 从数据库回填 Agent 上下文。
/// 本文件为 T4 接口（IPreferenceRepository）的编译依赖，按 design 3.2 定义。
/// </summary>
public sealed class UserPreferences
{
    public Guid UserId { get; init; }

    public string KeywordsJson { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
