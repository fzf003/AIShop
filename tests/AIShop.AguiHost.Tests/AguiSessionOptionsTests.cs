using AIShop.AguiHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIShop.AguiHost.Tests;

/// <summary>
/// S3 会话配置测试：<see cref="AguiSessionOptions"/> 的三项配置（TTL / 清理周期 / 快照轮数上限）
/// 经 <see cref="AguiServiceCollectionExtensions.AddAguiSessionStore"/> 绑定生效、缺省回退类默认 30/12/12、
/// 归一语义（<c>SessionTtlDays &lt;= 0</c> 禁用 TTL；<c>SessionCleanupIntervalHours &lt;= 0</c> 回退 12h），
/// 以及 <c>new SqliteAgentSessionStore(conn)</c> 直构的源码兼容。对应 spec R7「三项配置可配 + 缺省回退」。
/// </summary>
public sealed class AguiSessionOptionsTests
{
    /// <summary>测试用独立会话库连接串（不触盘：仅构造 store，不 InitializeAsync）。</summary>
    private const string TestSessionConnection = "Data Source=:memory:";

    /// <summary>构造三项会话配置键的配置对象（键名对齐 spec R7 / AguiSessionOptions 绑定键）。</summary>
    private static IConfiguration BuildConfig(int? ttlDays, int? cleanupHours, int? maxRounds)
    {
        var data = new Dictionary<string, string?>();
        if (ttlDays is not null)
            data["Agui:SessionTtlDays"] = ttlDays.Value.ToString();
        if (cleanupHours is not null)
            data["Agui:SessionCleanupIntervalHours"] = cleanupHours.Value.ToString();
        if (maxRounds is not null)
            data["Agui:SessionMaxRounds"] = maxRounds.Value.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>经 AddAguiSessionStore 装配并解析 store 的选项（走真实 DI 绑定路径，而非直接 new options）。</summary>
    private static AguiSessionOptions ResolveBoundOptions(IConfiguration? config)
    {
        var services = new ServiceCollection();
        services.AddAguiSessionStore(config, sessionDbConnection: TestSessionConnection);
        using var sp = services.BuildServiceProvider();
        var store = Assert.IsType<SqliteAgentSessionStore>(sp.GetRequiredService<SqliteAgentSessionStore>());
        return store.Options;
    }

    [Fact]
    public void Configuration_WithAllThreeKeys_BindsValuesToStoreOptions()
    {
        // 配置提供三项键 → 绑定值生效（spec R7 场景 1 / 验收 5）：非默认值逐项断言，
        // 证明是「配置绑定」而非「碰巧等于默认值」。
        var options = ResolveBoundOptions(BuildConfig(ttlDays: 7, cleanupHours: 3, maxRounds: 4));

        Assert.Equal(7, options.SessionTtlDays);
        Assert.Equal(3, options.SessionCleanupIntervalHours);
        Assert.Equal(4, options.SessionMaxRounds);
        // 绑定生效后的派生语义随之改变：TTL 启用 + 周期取配置值（非回退值）
        Assert.True(options.IsTtlEnabled);
        Assert.Equal(TimeSpan.FromHours(3), options.EffectiveCleanupInterval);
    }

    [Fact]
    public void NoConfiguration_StoreOptionsFallBackToClassDefaults()
    {
        // 无配置（config 为 null）→ 30/12/12 缺省回退（spec R7 缺省回退）。
        var options = ResolveBoundOptions(config: null);

        Assert.Equal(AguiSessionOptions.DefaultSessionTtlDays, options.SessionTtlDays);
        Assert.Equal(AguiSessionOptions.DefaultSessionCleanupIntervalHours, options.SessionCleanupIntervalHours);
        Assert.Equal(AguiSessionOptions.DefaultSessionMaxRounds, options.SessionMaxRounds);
        Assert.Equal(30, options.SessionTtlDays);
        Assert.Equal(12, options.SessionCleanupIntervalHours);
        Assert.Equal(12, options.SessionMaxRounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void SessionTtlDays_ZeroOrNegative_DisablesTtl(int ttlDays)
    {
        // SessionTtlDays <= 0 → 禁用 TTL（spec R7「<=0 禁用」）。
        var options = new AguiSessionOptions { SessionTtlDays = ttlDays };

        Assert.False(options.IsTtlEnabled);
    }

    [Fact]
    public void SessionTtlDays_Positive_EnablesTtl()
    {
        // 对照：正值启用 TTL（证伪「IsTtlEnabled 恒 false」）。
        var options = new AguiSessionOptions { SessionTtlDays = 1 };

        Assert.True(options.IsTtlEnabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-12)]
    public void SessionCleanupIntervalHours_NonPositive_FallsBackToTwelveHours(int hours)
    {
        // SessionCleanupIntervalHours <= 0 → 生效周期回退默认 12h（spec R7 缺省回退，避免 0 值忙循环）。
        var options = new AguiSessionOptions { SessionCleanupIntervalHours = hours };

        Assert.Equal(TimeSpan.FromHours(12), options.EffectiveCleanupInterval);
    }

    [Fact]
    public void SessionCleanupIntervalHours_Positive_UsesConfiguredInterval()
    {
        // 对照：正值周期取配置值（证伪「EffectiveCleanupInterval 恒 12h」）。
        var options = new AguiSessionOptions { SessionCleanupIntervalHours = 6 };

        Assert.Equal(TimeSpan.FromHours(6), options.EffectiveCleanupInterval);
    }

    [Fact]
    public void DirectConstructor_WithoutOptions_IsSourceCompatibleAndUsesDefaults()
    {
        // 源码兼容：既有 new SqliteAgentSessionStore(conn) 调用点仍可用，内部回退类默认 30/12/12（S3 约定）。
        var store = new SqliteAgentSessionStore(TestSessionConnection);

        Assert.Equal(TestSessionConnection, store.ConnectionString);
        Assert.Equal(AguiSessionOptions.DefaultSessionTtlDays, store.Options.SessionTtlDays);
        Assert.Equal(AguiSessionOptions.DefaultSessionCleanupIntervalHours, store.Options.SessionCleanupIntervalHours);
        Assert.Equal(AguiSessionOptions.DefaultSessionMaxRounds, store.Options.SessionMaxRounds);
    }
}
