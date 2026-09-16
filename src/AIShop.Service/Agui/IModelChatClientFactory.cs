using Microsoft.Extensions.AI;

namespace AIShop.Service.Agui;

/// <summary>
/// 「模型 → 底层客户端」唯一来源（agui-model-switch C5）。AguiHost 自建，不引用老 Service <c>ModelRouter</c>。
/// 读 appsettings.json 的 <c>Models</c> 节（多模型 qwen/deepseek/gpt-4.1，各含 Endpoint/Key/Model/Name）与
/// <c>ActiveModel</c>（缺省取 Models 首键）；每模型底层 chatClient 懒建缓存（模型 id 大小写不敏感）、
/// 每客户端统一外包 OTel 遥测。
/// </summary>
/// <remarks>
/// 本接口是三处共用点（design §5.2）：
/// <list type="bullet">
/// <item><c>RouterChatClient</c>（C5 M2）：按本轮 <see cref="IActiveModelProvider.ActiveModel"/> 解析委托目标——命中工厂已知
/// 模型 → <see cref="GetClient"/>，无 model / 未知 model → <see cref="GetDefaultClient"/>。</item>
/// <item>全局纯净 <c>IChatClient</c> seam（M4 起）＝ <see cref="GetDefaultClient"/>（ActiveModel 底层 + OTel，无清洗），
/// 供 <c>IMemoryService</c> 等内部链路复用。</item>
/// <item>测试 override seam：AguiHost 离线 WAF 测试替换本接口为 stub 工厂，让所有 modelId 返回脚本化 mock
/// （agent 聊天底层经 RouterChatClient → 工厂，override 点从全局 <c>IChatClient</c> 迁到本接口）。</item>
/// </list>
/// 实现为 internal + InternalsVisibleTo 暴露给 AIShop.AguiHost.Tests（不对外、不进 Core/Service）。
/// </remarks>
public interface IModelChatClientFactory
{
    /// <summary>缺省模型 id（＝ 配置节 <c>ActiveModel</c>；ActiveModel 缺失时为 Models 节首个键）。</summary>
    string DefaultModelId { get; }

    /// <summary>
    /// 全部已配置模型清单（agui-client-support T1）：**清单的唯一来源**——端点层（<c>GET /models</c>）
    /// MUST NOT 二次解析 <c>Models</c> 配置节（spec R2），只转发本属性。
    /// </summary>
    /// <remarks>
    /// 顺序 = 构造时 <c>Models</c> 子节的 <c>IConfiguration.GetChildren()</c> 顺序（**子键序数升序**，非 appsettings 书写顺序），
    /// 确定且可复现；<see cref="ModelDescriptor.Name"/> 缺省回退节键、<see cref="ModelDescriptor.IsDefault"/>
    /// 的大小写口径均与工厂其余解析规则同源（口径只在工厂实现一处）。
    /// </remarks>
    IReadOnlyList<ModelDescriptor> AvailableModels { get; }

    /// <summary>模型 id 是否已配置（<see cref="StringComparer.OrdinalIgnoreCase"/>）。</summary>
    bool ContainsModel(string modelId);

    /// <summary>
    /// 取指定模型底层 chatClient；首次访问懒建并缓存，后续复用同一缓存实例（不重复构建）。
    /// </summary>
    /// <exception cref="KeyNotFoundException">模型 id 未配置时抛出。</exception>
    IChatClient GetClient(string modelId);

    /// <summary>取缺省模型（<see cref="DefaultModelId"/>）底层 chatClient，等价 <see cref="GetClient"/>。</summary>
    IChatClient GetDefaultClient();
}
