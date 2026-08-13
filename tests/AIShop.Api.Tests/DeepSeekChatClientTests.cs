using AIShop.Api.Agents;
using Microsoft.Extensions.AI;

namespace AIShop.Api.Tests;

/// <summary>
/// R10/R10.1 — DeepSeekChatClient 实现 IChatClient.Metadata 并经 GetService 暴露：
/// 修 DeepSeek gen_ai 遥测属性（gen_ai.provider.name / gen_ai.request.model）为空——
/// 旧主构造函数未实现 Metadata，接口默认 Metadata 为空对象；R10 实现 Metadata，
/// R10.1 经 GetService(typeof(ChatClientMetadata)) 暴露（MEAI 埋点从 GetService 读 provider.name）。
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

    [Fact]
    public void GetService_ReturnsMetadata_ForChatClientMetadata()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        Assert.NotNull(metadata);
        Assert.Equal("DeepSeek", metadata!.ProviderName);
        Assert.Equal("deepseek-v4-flash", metadata.DefaultModelId);
    }

    [Fact]
    public void GetService_ReturnsNull_ForOtherServiceTypes()
    {
        using var client = new DeepSeekChatClient(new HttpClient(), "deepseek-v4-flash");

        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }
}
