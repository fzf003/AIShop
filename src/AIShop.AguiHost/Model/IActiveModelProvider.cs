namespace AIShop.AguiHost.Model;

/// <summary>
/// 当前请求激活模型上下文（agui-model-switch C5 M2）。
/// AsyncLocal 单例形态（仿 <c>ICurrentUserAccessor</c>/<c>CurrentUserAccessor</c>，AguiHost 内自建、不进 Core）。
/// 每个 AG-UI 轮次（RunAgentInput）由 model 注入中间件把该轮模型 id 写入；<see cref="RouterChatClient"/> 在
/// 同一次 run 的 async 链任意深处读到该值并委托到「本轮激活模型」的底层 chatClient。
/// </summary>
/// <remarks>
/// 语义（spec ADDED Requirement 4）：值按 ExecutionContext 隔离——随同一次 run 的 async/await 调用链流动、
/// 不跨请求泄漏；<see cref="SetActiveModel"/>(null) 表示「未指定」＝走 ActiveModel 缺省（由
/// <see cref="RouterChatClient"/> 读取侧解析，注入中间件不注入缺省模型值）。
/// </remarks>
internal interface IActiveModelProvider
{
    /// <summary>每轮请求（AG-UI RunAgentInput）进入时注入该轮模型 id；null = 未指定（走 ActiveModel 缺省）。</summary>
    void SetActiveModel(string? modelId);

    /// <summary>当前执行流绑定的模型 id（未指定返回 null）。</summary>
    string? ActiveModel { get; }
}
