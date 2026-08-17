using AIShop.Infrastructure.Entities;

namespace AIShop.Api.Tests;

/// <summary>
/// ChatMessageRecord 实体 T1T 契约测试：新增轮次字段（RunId / IsFinal）的默认值
/// （对应 design §3 数据模型变更与 spec「run_id 轮次分组 / is_final 轮次终点标记」）。
/// </summary>
public sealed class ChatMessageRecordEntityTests
{
    [Fact]
    public void NewChatMessageRecord_RunId_DefaultsToNull()
    {
        var record = new ChatMessageRecord();

        // run_id 可空兼容存量：新建实体默认无轮次归属
        Assert.Null(record.RunId);
    }

    [Fact]
    public void NewChatMessageRecord_IsFinal_DefaultsToFalse()
    {
        var record = new ChatMessageRecord();

        // is_final 轮次终点标记：新建实体默认为未完成轮（false）
        Assert.False(record.IsFinal);
    }
}
