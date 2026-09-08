using AIShop.AguiHost.Model;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// C5 M2 单元测试：<see cref="ActiveModelProvider"/>（agui-model-switch）。
/// 对应 spec ADDED Requirement 4「IActiveModelProvider 提供执行流级『当前请求激活模型』上下文」：
/// AsyncLocal 值按 ExecutionContext 隔离——同一次 run 的 async 链内读到注入值、Task.Run 子执行流的写入不泄漏到
/// 调用方、SetActiveModel(null) 显式清空回 null（= 未指定，走 ActiveModel 缺省）。
/// </summary>
public sealed class ActiveModelProviderTests
{
    private readonly ActiveModelProvider _provider = new();

    [Fact]
    public void ActiveModel_WhenNeverSet_ReturnsNull()
    {
        // 未注入任何模型 → null（＝未指定，由 RouterChatClient 读取侧解析 ActiveModel 缺省；对应 spec Req4）
        Assert.Null(_provider.ActiveModel);
    }

    [Fact]
    public void SetActiveModel_ThenRead_SameFlowReturnsValue()
    {
        // 同一执行流内注入后立即读到同值（对应 spec Req4「值随同一次 run 的调用链流动」）
        _provider.SetActiveModel("deepseek");

        Assert.Equal("deepseek", _provider.ActiveModel);
    }

    [Fact]
    public void SetActiveModel_Null_ClearsToNull()
    {
        // SetActiveModel(null) 显式清空 → 回 null（新请求无 model metadata 时中间件调用；对应 spec Req4「null=未指定」）
        _provider.SetActiveModel("deepseek");

        _provider.SetActiveModel(null);

        Assert.Null(_provider.ActiveModel);
    }

    [Fact]
    public async Task SetActiveModel_InsideChildTask_DoesNotLeakToCallerFlow()
    {
        // 子执行流（Task.Run）内写入 deepseek：AsyncLocal 的改动不流回调用方执行流（不跨请求/任务泄漏）
        await Task.Run(() => _provider.SetActiveModel("deepseek"));

        Assert.Null(_provider.ActiveModel);
    }

    [Fact]
    public async Task ActiveModel_FlowsIntoChildAsyncChain()
    {
        // 注入值随 ExecutionContext 流入同一次 run 的子 async 链（真实使用：agent 的 async 工具/模型调用读到同一模型）
        _provider.SetActiveModel("qwen");

        var readInChild = await Task.Run(async () =>
        {
            await Task.Delay(1);
            return _provider.ActiveModel;
        });

        Assert.Equal("qwen", readInChild);
    }

    [Fact]
    public async Task ActiveModel_ConcurrentFlows_DoNotInterfere()
    {
        // 两个并行执行流各自注入不同模型：值按 ExecutionContext 隔离、互不干扰（每轮独立选择，对应 spec Req4「逐轮切换不泄漏」）
        var t1 = Task.Run(async () =>
        {
            _provider.SetActiveModel("deepseek");
            await Task.Delay(30);
            return _provider.ActiveModel;
        });
        var t2 = Task.Run(async () =>
        {
            _provider.SetActiveModel("qwen");
            await Task.Delay(30);
            return _provider.ActiveModel;
        });

        var results = await Task.WhenAll(t1, t2);

        Assert.Equal("deepseek", results[0]);
        Assert.Equal("qwen", results[1]);
    }
}
