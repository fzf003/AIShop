using System.Text.Json;
using AIShop.AguiHost;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T5 ResolveUsername 纯函数单测：AGUI forwarded metadata 读 username；缺失/非法一律回退缺省用户。
/// 缺省用户为 seed 用户 "steve"（<see cref="AguiUsernameForwarder.DefaultUsername"/>），
/// 使未带 username 的 AG-UI 请求默认以可购物用户 steve 运行；解析层语义（缺失回退 DefaultUsername）不变。
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
    public void ResolveUsername_WhenUsernameMissing_ReturnsNull_AndDefaultUsernameIsApplied()
    {
        // metadata 不含 username（forwardedProps 里只有其它字段）→ 解析层返回 null
        var metadata = MetadataOf(new { tenantId = "tenant-123" });

        Assert.Null(AguiUsernameForwarder.ResolveUsername(metadata));

        // 缺省用户（seed 用户 steve，购物可用）：缺省常量 + 解析一次到位
        // helper 均回退 DefaultUsername。语义仍对齐 spec「metadata 缺失时按缺省用户处理」。
        Assert.Equal("steve", AguiUsernameForwarder.DefaultUsername);
        Assert.Equal("steve", AguiUsernameForwarder.ResolveUsernameOrDefault(metadata));
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
        // username 为空白串 → 视作缺失（由调用方应用 DefaultUsername 缺省）
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
