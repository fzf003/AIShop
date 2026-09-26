using Meai = Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// T16 mock-LLM E2E 测试共享 helper：脚本化「工具调用 → 最终文本」的 <see cref="Meai.IChatClient"/>。
/// 按顺序逐段驱动：对一次 Agent run（FICC 工具循环，见 T14 实证）——
/// 模型决策入口（FICC 每轮调内层，SSE 路径实测走 <see cref="GetStreamingResponseAsync"/>，工具迭代每轮一次内层调用）
/// 首个未决调用返回脚本该段的 assistant <see cref="Meai.FunctionCallContent"/>（工具名 + 强类型 Arguments 字典），
/// FICC 以真实工具执行后把 <see cref="Meai.FunctionResultContent"/> 追加回输入，下一次内层调用返回该段的最终文本。
/// 两入口（<see cref="GetResponseAsync"/> / <see cref="GetStreamingResponseAsync"/>）共享同一状态机，
/// 使 ChatClientAgent 的非流式（RunAsync）与 SSE 流式（RunStreaming）驱动形态一致收敛（tasks 实施期确认项 ⑩）。
/// </summary>
internal sealed class MockToolChatClient : Meai.IChatClient
{
    /// <summary>一段脚本：先请求工具（名 + 强类型参数），工具结果回填后产出 <see cref="FinalText"/>。</summary>
    internal sealed record ToolScript(
        string ToolName,
        IReadOnlyDictionary<string, object?> Arguments,
        string FinalText)
    {
        /// <summary>
        /// 为 <c>true</c> 时，本段产出的 <see cref="Meai.FunctionCallContent"/> 的
        /// <see cref="Meai.FunctionCallContent.Arguments"/> 置为 <c>null</c>——即真机 Mimo 的失败形态
        /// （wire 上 tool_call 的 <c>arguments</c> 为字符串 <c>"null"</c>，解析到 MEAI 后即表现为
        /// <c>Arguments == null</c>；见 <c>ReplySanitizingChatClient</c> 的 C3 规范化）。
        /// <para>缺省 <c>false</c> = 沿用原有行为（以 <see cref="Arguments"/> 强类型字典构造），
        /// 既有用例因此逐字节不变；置 <c>true</c> 时 <see cref="Arguments"/> 的取值被忽略
        /// （传空字典占位即可），仅作为「工具名 + FinalText」的载体。</para>
        /// </summary>
        internal bool NullArguments { get; init; }
    }

    private readonly IReadOnlyList<ToolScript> _scripts;
    private int _currentSegment;
    private bool _awaitingToolResult;
    private int _callSeq;

    /// <summary>初始化脚本化工具 mock。</summary>
    /// <param name="scripts">按顺序的段脚本；每段 = 一次工具调用 + 随后的最终文本。</param>
    internal MockToolChatClient(IReadOnlyList<ToolScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count == 0)
            throw new ArgumentException("至少需一段工具脚本", nameof(scripts));
        _scripts = scripts;
    }

    /// <summary>单段便捷构造。</summary>
    internal MockToolChatClient(ToolScript script)
        : this([script])
    {
    }

    /// <summary>
    /// 「工具结果已回填」调用（mock 刚被 FICC 以含工具结果的输入再次调用、准备产出最终文本）收到的输入消息快照。
    /// 供断言真实工具执行结果真回填：快照中应含 FICC 追加的 <see cref="Meai.FunctionResultContent"/>，
    /// 其 <c>Result</c> 为真实工具（CartToolProvider）的返回文本（如 search_product 命中/ add_to_cart 成功文案）。
    /// </summary>
    internal List<IReadOnlyList<Meai.ChatMessage>> ToolResultInputs { get; } = [];

    /// <summary>「请求工具」调用（mock 产出 FunctionCallContent）收到的输入消息快照。</summary>
    internal List<IReadOnlyList<Meai.ChatMessage>> ToolCallInputs { get; } = [];

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public Task<Meai.ChatResponse> GetResponseAsync(
        IEnumerable<Meai.ChatMessage> messages,
        Meai.ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var message = Decide(messages);
        return Task.FromResult(new Meai.ChatResponse([message]));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Meai.ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Meai.ChatMessage> messages,
        Meai.ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = Decide(messages);

        // 把决策消息转成流式更新：文本段产出一条文本增量；工具段产出一条携带 FunctionCallContent 的增量。
        // （ChatClientAgent SSE 出口经 streaming —— FICC 从更新里收集 FCC 执行真实工具，见 T14/实测。）
        if (message.Contents.OfType<Meai.FunctionCallContent>().Any())
        {
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, message.Contents.ToList());
        }
        else
        {
            var text = string.Concat(message.Contents.OfType<Meai.TextContent>().Select(c => c.Text));
            yield return new Meai.ChatResponseUpdate(Meai.ChatRole.Assistant, text);
        }

        await Task.CompletedTask;
    }

    /// <summary>共享状态机：等待工具结果 → 产出当前段最终文本并推进；否则 → 产出当前段工具调用。</summary>
    private Meai.ChatMessage Decide(IEnumerable<Meai.ChatMessage> messages)
    {
        var input = messages.ToList();

        if (_awaitingToolResult)
        {
            // FICC 已执行完当前段请求的工具并把 FunctionResultContent 追加回输入——此调用产出该段最终文本
            _awaitingToolResult = false;
            ToolResultInputs.Add(input);

            var finalText = _scripts[_currentSegment].FinalText;
            _currentSegment++;
            return new Meai.ChatMessage(Meai.ChatRole.Assistant, finalText);
        }

        // 首次请求（或上一段文本已产出、新一轮用户请求到达）→ 请求当前段脚本工具
        var script = _scripts[_currentSegment];
        _awaitingToolResult = true;
        ToolCallInputs.Add(input);

        var callId = $"e2e-call-{++_callSeq}";
        // FICC FunctionCallContent 参数经 Arguments 字典以强类型值提供（tasks 实施期确认项 ⑪）；
        // MEAI 构造收 IDictionary<string, object?>，脚本存 IReadOnlyDictionary，此处拷贝适配。
        // NullArguments=true 时传 null（Mimo 形态），不拷贝字典——否则无法表达该失败形态。
        Dictionary<string, object?>? arguments = script.NullArguments
            ? null
            : new Dictionary<string, object?>(script.Arguments);
        var functionCall = new Meai.FunctionCallContent(callId, script.ToolName, arguments);
        return new Meai.ChatMessage
        {
            Role = Meai.ChatRole.Assistant,
            Contents = [functionCall]
        };
    }
}
