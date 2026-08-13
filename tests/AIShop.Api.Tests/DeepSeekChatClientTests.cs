using AIShop.Api.Agents;

namespace AIShop.Api.Tests;

/// <summary>
/// R10 — DeepSeekChatClient 实现 IChatClient.Metadata：
/// 修 DeepSeek gen_ai 遥测属性（ProviderName/DefaultModelId）为空——旧主构造函数未实现 Metadata，
/// 接口默认 Metadata 为空对象，OTel gen_ai 属性（gen_ai.provider/gen_ai.request.model）缺失。
/// 注：10.8.3 的属性名为 DefaultModelId（非 ModelId），断言按实际契约。
/// </summary>
public sealed class DeepSeekChatClientTests
{
    [Fact]
    public void Metadata_ReturnsProviderAndDefaultModelId()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        Assert.Equal("DeepSeek", client.Metadata.ProviderName);
        Assert.Equal("deepseek-v4-flash", client.Metadata.DefaultModelId);
    }
}
