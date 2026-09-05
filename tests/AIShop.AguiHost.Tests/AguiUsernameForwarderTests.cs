using System.Text.Json;
using AIShop.AguiHost;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T5 ResolveUsername 纯函数单测：AGUI forwarded metadata 读 username；缺失/非法一律回退 guest。
/// 对应 spec「username 经 ICurrentUserAccessor 由 AGUI metadata 注入，缺省 guest」的解析层。
/// </summary>
public sealed class AguiUsernameForwarderTests
{
    /// <summary>把任意对象序列化为 <see cref="JsonElement"/>（模拟 AG-UI forwarded metadata 容器）。</summary>
    private static JsonElement MetadataOf(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ResolveUsername_WhenForwardedMetadataContainsUsername_ReturnsUsername()
    {
        // metadata 含 username=marla → 解析出 "marla"
        var metadata = MetadataOf(new { username = "marla" });

        Assert.Equal("marla", AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameMissing_ReturnsNull_AndGuestDefaultIsApplied()
    {
        // metadata 不含 username（forwardedProps 里只有其它字段）→ 解析层返回 null
        var metadata = MetadataOf(new { tenantId = "tenant-123" });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));

        // 缺省 guest（spec「metadata 缺失时按 guest 处理」）：缺省常量 + 解析一次到位 helper 均回退 guest
        Assert.Equal("guest", AguiUsernameForwarder.DefaultUsername);
        Assert.Equal("guest", AguiUsernameForwarder.ResolveUsernameOrDefault(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameValueIsNotString_ReturnsNull()
    {
        // username 以非字符串（数字）携带 → 视作无有效 username（避免类型错误注入）
        var metadata = MetadataOf(new { username = 42 });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenUsernameIsBlank_ReturnsNull()
    {
        // username 为空白串 → 视作缺失 → guest
        var metadata = MetadataOf(new { username = "   " });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }

    [Fact]
    public void ResolveUsername_WhenForwardedMetadataIsNotJsonObject_ReturnsNull()
    {
        // metadata 不是 JSON object（如字符串/数组）→ 无 username 可读
        var metadata = JsonSerializer.SerializeToElement("not-an-object");

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));
    }
}
