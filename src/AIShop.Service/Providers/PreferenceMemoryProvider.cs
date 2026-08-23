#pragma warning disable MAAI001
using System.Text.Json;
using System.Text.Json.Serialization;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AIShop.Service.Providers;

/// <summary>
/// 会话偏好记忆 Provider — 以 State 为主的用户偏好读写中枢。
/// 偏好按用户隔离（State 承载 <see cref="State.UserId"/>），同一会话内以 State 为权威来源。
///
/// - Provide：以 State 为主；State 无偏好时按用户从偏好表加载进 State；同时合并端点写入的本轮新偏好
/// - Store：把端点写入 <c>StateBag["NewPreferences"]</c> 的本轮新偏好入队（权重累加写库），随后清除
///
/// 依赖可选注入（<see cref="IPreferenceRepository"/> / <see cref="IPreferenceQueue"/>），
/// 未注入时优雅降级为纯上下文注入（仅读 StateBag 中的偏好文本）。
/// </summary>
public sealed class PreferenceMemoryProvider : MessageAIContextProvider
{
    /// <summary>StateBag 中当前用户 ID 的键（RunChatAsync 写入）。</summary>
    public const string UserIdStateKey = "UserId";

    /// <summary>StateBag 中本轮新偏好（LLM 提取，端点写入）的键。</summary>
    public const string NewPreferencesStateKey = "NewPreferences";

    /// <summary>StateBag 中偏好文本的键（RunChatAsync 从端点 preferences 参数写入，兼容既有注入路径）。</summary>
    private const string PreferencesStateKey = "Preferences";

    private const string DefaultStateKey = nameof(PreferenceMemoryProvider);

    private readonly ProviderSessionState<State> _sessionState;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly IPreferenceQueue? _prefQueue;
    private IReadOnlyList<string>? _stateKeys;

    /// <summary>
    /// 初始化 <see cref="PreferenceMemoryProvider"/>。
    /// </summary>
    /// <param name="dbFactory">可选的 EF Core DbContext 工厂，用于 Provide 时从偏好表加载用户偏好（可跨 scope 使用）。</param>
    /// <param name="preferenceQueue">可选的偏好写入队列，用于 Store 时将新偏好入队（权重累加写库）。</param>
    /// <param name="stateInitializer">可选的状态初始化器；未提供时默认从 StateBag["UserId"] 构造 State。</param>
    public PreferenceMemoryProvider(
        IDbContextFactory<AppDbContext>? dbFactory = null,
        IPreferenceQueue? preferenceQueue = null,
        Func<AgentSession?, State>? stateInitializer = null)
    {
        _dbFactory = dbFactory;
        _prefQueue = preferenceQueue;
        _sessionState = new ProviderSessionState<State>(
            stateInitializer ?? DefaultStateInitializer,
            DefaultStateKey,
            null);
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => _stateKeys ??= [_sessionState.StateKey];

    /// <summary>
    /// 默认状态初始化器：从 StateBag["UserId"] 构造 State（按用户隔离）。
    /// </summary>
    private static State DefaultStateInitializer(AgentSession? session)
    {
        if (session?.StateBag.TryGetValue<string>(UserIdStateKey, out var uid, null) is true
            && Guid.TryParse(uid, out var userId))
        {
            return new State(userId);
        }

        return new State(null);
    }

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        // 以 State 为主：State 无偏好且能按用户查库时，从偏好表加载进 State（首次/会话重建）
        if (string.IsNullOrWhiteSpace(state.KeywordsJson) && state.UserId is { } uid && _dbFactory is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var prefs = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == uid, cancellationToken);
            state.KeywordsJson = prefs?.KeywordsJson ?? "{}";
            _sessionState.SaveState(context.Session, state);
        }

        // 合并本轮新偏好（StateBag["NewPreferences"]）与兼容的既有偏好注入（StateBag["Preferences"]）作为展示文本
        var newPrefsText = context.Session?.StateBag.GetValue<string>(NewPreferencesStateKey);
        var legacyPrefsText = context.Session?.StateBag.GetValue<string>(PreferencesStateKey);
        var prefsText = BuildDisplayText(state.KeywordsJson, newPrefsText, legacyPrefsText);

        if (string.IsNullOrWhiteSpace(prefsText))
            return new AIContext();

        return new AIContext
        {
            Instructions = $"""
                【已知用户偏好】
                {prefsText}

                请在推荐商品时优先考虑以上偏好。
                """
        };
    }

    /// <inheritdoc />
    protected override ValueTask StoreAIContextAsync(
        AIContextProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        // 本轮新偏好：端点写入 StateBag["NewPreferences"] → 入队累加 → 清除（避免下次重复入队）
        var newPrefs = context.Session?.StateBag.GetValue<string>(NewPreferencesStateKey);
        if (!string.IsNullOrWhiteSpace(newPrefs) && state.UserId is { } uid && _prefQueue is not null)
        {
            var prefsList = newPrefs.Split(['、', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (prefsList.Count > 0)
            {
                _prefQueue.TryEnqueue(new UserPreferenceUpdate(uid, prefsList));
            }
        }

        if (context.Session is not null)
        {
            context.Session.StateBag.TryRemoveValue(NewPreferencesStateKey);
        }

        return default;
    }

    /// <summary>
    /// 合并库偏好（KeywordsJson 的 {词: 权重}）、既有偏好文本（StateBag["Preferences"]）
    /// 与本轮新偏好（StateBag["NewPreferences"]）为展示文本。
    /// </summary>
    private static string BuildDisplayText(string? keywordsJson, string? newPrefsText, string? legacyPrefsText)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(keywordsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(keywordsJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.GetInt32() > 0)
                        parts.Add(prop.Name);
                }
            }
            catch (JsonException)
            {
                // 存量数据被手工改坏时降级为原样展示
                parts.Add(keywordsJson);
            }
        }

        if (!string.IsNullOrWhiteSpace(legacyPrefsText))
            parts.Add(legacyPrefsText);

        if (!string.IsNullOrWhiteSpace(newPrefsText))
            parts.Add(newPrefsText);

        return string.Join("、", parts);
    }

    /// <summary>
    /// 会话偏好状态：承载当前用户 ID 与会话内偏好（State 为主）。
    /// </summary>
    public sealed class State
    {
        [JsonConstructor]
        public State(Guid? userId)
        {
            this.UserId = userId;
        }

        /// <summary>当前会话所属用户 ID（按用户隔离偏好）。</summary>
        [JsonPropertyName("userId")]
        public Guid? UserId { get; }

        /// <summary>会话内偏好（{词: 权重} JSON），以 State 为主，首次从偏好表加载。</summary>
        [JsonPropertyName("keywordsJson")]
        public string? KeywordsJson { get; set; }
    }
}
