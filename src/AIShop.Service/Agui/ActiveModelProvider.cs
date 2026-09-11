namespace AIShop.Service.Agui;

/// <summary>
/// <see cref="IActiveModelProvider"/> 的 AsyncLocal 单例实现（agui-model-switch C5 M2）。
/// 单例注册 + AsyncLocal 字段：值按 <see cref="System.Threading.ExecutionContext"/> 隔离——注入中间件在请求入口
/// <see cref="SetActiveModel"/> 后，同一执行流的 async/await 链（agent 运行、工具调用）读到同一模型 id；当次 run
/// 结束随 ExecutionContext 消失，不残留到下一请求（与既有 username 注入 <c>CurrentUserAccessor</c> 同机制）。
/// </summary>
public sealed class ActiveModelProvider : IActiveModelProvider
{
    private readonly AsyncLocal<string?> _activeModel = new();

    /// <inheritdoc />
    public void SetActiveModel(string? modelId) => _activeModel.Value = modelId;

    /// <inheritdoc />
    public string? ActiveModel => _activeModel.Value;
}
