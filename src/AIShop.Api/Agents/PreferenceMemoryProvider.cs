#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AIShop.Api.Agents;

/// <summary>
/// 会话偏好记忆 Provider — 在同一会话中自动注入已记住的用户偏好。
/// 偏好以纯文本形式存储在 <c>AgentSession.StateBag["Preferences"]</c> 中，
/// 由端点层在拿到 Agent 回复后写入。
/// </summary>
public sealed class PreferenceMemoryProvider : AIContextProvider
{
    private const string StateKey = "Preferences";

    public override IReadOnlyList<string> StateKeys => [nameof(PreferenceMemoryProvider)];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        var prefs = context.Session?.StateBag.GetValue<string>(StateKey);

        if (string.IsNullOrWhiteSpace(prefs))
            return new ValueTask<AIContext>(new AIContext());

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = $"""
                【已知用户偏好】
                {prefs}

                请在推荐商品时优先考虑以上偏好。
                """
        });
    }
}
