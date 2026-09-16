namespace AIShop.Service.Agui;

/// <summary>
/// 模型清单项（agui-client-support T1）：模型客户端工厂对外暴露的模型描述，
/// 供 AguiHost <c>GET /models</c> 端点直接返回（spec R1/R2：清单唯一来源 = 工厂，端点层不二次解析配置）。
/// </summary>
/// <param name="Id">
/// <c>Models</c> 节键（如 <c>qwen</c> / <c>deepseek</c> / <c>gpt-4.1</c>），
/// 即前端在 <c>forwardedProps.model</c> 中回传、用于逐轮选模型的值。
/// </param>
/// <param name="Name">显示名（节内 <c>Name</c>；缺失时回退节键，规则与工厂解析一致）。</param>
/// <param name="Model">真实 wire 模型名（节内 <c>Model</c>），供前端显示副标题。</param>
/// <param name="IsDefault">
/// 是否为配置的默认模型（<c>Id</c> 与 <see cref="IModelChatClientFactory.DefaultModelId"/> 大小写不敏感相等）。
/// 注意：<c>ActiveModel</c> 配成未知键时**所有项均为 <c>false</c>**（工厂守卫语义——宁可不高亮也不虚假高亮），前端需自行兜底。
/// </param>
/// <remarks>
/// 本记录**不含** Endpoint 与 Key：清单是公开可读的（前端在选定身份之前就要用），敏感配置不进响应。
/// </remarks>
public sealed record ModelDescriptor(string Id, string Name, string Model, bool IsDefault);
