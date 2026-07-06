#pragma warning disable MAAI001
using System.Reflection;
using AIShop.Api.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace AIShop.Api.Tests;

public sealed class PreferenceMemoryProviderTests
{
    private readonly PreferenceMemoryProvider _provider;

    private static readonly Type ProviderType = typeof(PreferenceMemoryProvider);
    private static readonly MethodInfo ProvideMethod = ProviderType.GetMethod(
        "ProvideAIContextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public PreferenceMemoryProviderTests()
    {
        _provider = new PreferenceMemoryProvider();
    }

    [Fact]
    public void StateKeys_ReturnsProviderName()
    {
        var keys = _provider.StateKeys;
        Assert.Single(keys);
        Assert.Equal(nameof(PreferenceMemoryProvider), keys[0]);
    }

    [Fact]
    public async Task ProvideContext_NoPreferences_ReturnsEmptyContext()
    {
        var session = CreateSession(); // no Preferences set
        var context = CreateInvokingContext(session);

        var result = await InvokeProvideAsync(context);

        Assert.NotNull(result);
        Assert.Null(result.Instructions);
        Assert.Null(result.Messages);
        Assert.Null(result.Tools);
    }

    [Fact]
    public async Task ProvideContext_WithPreferences_ReturnsInstructions()
    {
        var session = CreateSession();
        session.StateBag.SetValue("Preferences", "跑步、越野跑");

        var context = CreateInvokingContext(session);

        var result = await InvokeProvideAsync(context);

        Assert.NotNull(result);
        Assert.NotNull(result.Instructions);
        Assert.Contains("跑步、越野跑", result.Instructions);
        Assert.Contains("已知用户偏好", result.Instructions);
        Assert.Contains("优先考虑以上偏好", result.Instructions);
    }

    [Fact]
    public async Task ProvideContext_EmptyPreferences_ReturnsEmptyContext()
    {
        var session = CreateSession();
        session.StateBag.SetValue("Preferences", "");

        var context = CreateInvokingContext(session);

        var result = await InvokeProvideAsync(context);

        Assert.NotNull(result);
        Assert.Null(result.Instructions);
    }

    [Fact]
    public async Task ProvideContext_NullSession_ReturnsEmptyContext()
    {
        // Create context with null session
        var agent = Substitute.For<AIAgent>();
        var context = new AIContextProvider.InvokingContext(agent, null, new AIContext());

        var result = await InvokeProvideAsync(context);

        Assert.NotNull(result);
        Assert.Null(result.Instructions);
    }

    private static TestSession CreateSession()
    {
        return new TestSession();
    }

    private static AIContextProvider.InvokingContext CreateInvokingContext(AgentSession session)
    {
        var agent = Substitute.For<AIAgent>();
        return new AIContextProvider.InvokingContext(agent, session, new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User, "你好")]
        });
    }

    private async Task<AIContext> InvokeProvideAsync(AIContextProvider.InvokingContext context)
    {
        var result = ProvideMethod.Invoke(_provider, [context, CancellationToken.None]);
        var valueTask = (ValueTask<AIContext>)result!;
        return await valueTask;
    }

    private sealed class TestSession : AgentSession
    {
        public TestSession() : base(new AgentSessionStateBag()) { }
    }
}
