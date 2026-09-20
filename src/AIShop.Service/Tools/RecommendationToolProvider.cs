using System.ComponentModel;
using System.Text.Json;
using AIShop.Core.Interfaces;
using AIShop.Core.Models;
using AIShop.Core.Services;
using AIShop.Core.StaticData;
using Mem0Sharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AIShop.Service.Tools;

/// <summary>
/// 推荐工具宿主（形态同 <see cref="CartToolProvider"/>）：向 Agent 暴露 <c>recommend_products</c>。
///
/// 硬约束：**执行路径上不存在大模型调用**（延迟毫秒级）——
/// 关键词匹配是纯字符串（<see cref="ProductKeywordMap"/>），推荐口径复用已有的
/// <see cref="RecommendationService"/>（Scoped，经 scope 解析），理由派生是纯函数
/// （<see cref="RecommendationReasons"/>）。因此本类**不得**依赖 <c>IChatClient</c> 或
/// <c>IMemoryService</c>（后者装配了 LlmReranker 精排）；偏好只走 <c>IMemoryStore</c> 直读
/// （纯 SQLite 读，无 embedding / 无网络），结果进 <c>IMemoryCache</c> 压掉「每次调用全表枚举该用户记忆」。
/// 被禁依赖由测试以「反射构造参数 + 一调用即抛异常的替身装配」双重锁定。
///
/// <para>可选依赖 <see cref="IProductSemanticSearch"/>（B1）：**本地 bge ONNX 句向量 + 向量库 KNN**，
/// 不走任何大模型（模型在进程内推理），故上述「零大模型调用」硬约束对两条入口依然成立；
/// 未注册 / 检索失败时整体退化回关键词判据，见 <see cref="SearchRelatedAsync"/>。</para>
///
/// 两条对外入口共用同一个私有 builder（{身份校验 → 对话关键词 → 语义命中 → 偏好关键词 → Build → 投影}），
/// 因此**口径不可能分叉**：
/// <list type="bullet">
/// <item><see cref="RecommendProductsAsync"/> —— 工具入口，关键词路径的返回值/schema 逐字节不变；</item>
/// <item><see cref="TryBuildPushPayloadAsync"/> —— 推送入口（供 AG-UI 轮末装饰器调用），带门控。</item>
/// </list>
/// 约束：推送入口**不得**改变工具契约（返回值、参数 schema、reason 文案），**不得**新增被禁依赖
/// （<c>IChatClient</c> / <c>IMemoryService</c> 仍被禁止，零大模型调用的硬约束对两条入口同时成立）。
/// </summary>
/// <param name="semanticSearch">
/// 商品语义检索（可选注入，照抄 <see cref="CartToolProvider"/> 的可选参模式）。bge 是**本地 ONNX embedding**、
/// **非大模型**，故不破坏「零大模型调用」的硬约束；未注册（宿主未启用 RAG / 直构造）或检索抛异常时为 <c>null</c>
/// 语义路径整体关闭，门控退回纯关键词判据，行为与 B1 之前逐字节一致。
/// </param>
public sealed class RecommendationToolProvider(
    IServiceScopeFactory scopeFactory,
    ICurrentUserAccessor currentUserAccessor,
    IMemoryStore memoryStore,
    IMemoryCache memoryCache,
    IProductSemanticSearch? semanticSearch = null)
{
    /// <summary>返回推荐的商品条数上限（与 /api/recommendations 面板展示口径一致）。</summary>
    private const int MaxProducts = 6;

    /// <summary>参与推荐的关键词个数上限（与 RecommendationMerger 的合并上限一致）。</summary>
    private const int MaxKeywords = 5;

    /// <summary>
    /// 语义命中门控阈值（相似度下限，0–1，越大越相关）：只有相似度 ≥ 本值的召回才被当作
    /// 「本轮有推荐依据」，才能把面板推向语义路径（<see cref="ShouldPush"/> 的并集判据之一）。
    ///
    /// <para><b>标定锚点</b>（真机 bge-small-zh-v1.5，回归与标定见工单 B2）：
    /// 「T恤有吗」对种子商品 #2「有机棉T恤」的相似度须 ≥ 本值（**要推**——L8 修复点）；
    /// 闲聊「你好呀，今天心情不错」对任何商品的相似度须 &lt; 本值（**不能推**，spec R2 场景 1）。</para>
    ///
    /// <para><b>与检索层的关系</b>：本常量与 Infrastructure 的
    /// <c>ProductSemanticSearch.MinSimilarity</c>（`src/AIShop.Infrastructure/Rag/ProductSemanticSearch.cs`，现值 0.5f）
    /// 是**同一契约的两个位置**——检索层已先滤掉 &lt; 0.5 的弱匹配，本常量把该契约**显式化在门控处**：
    /// 底层阈值若被下调，门控不会跟着静默放行（此处仍按 0.5 拦截）。**两处须同步调整**
    /// （本批次禁改 Infrastructure，故只在此处显式声明）。</para>
    /// </summary>
    internal const float SemanticMatchThreshold = 0.5f;

    /// <summary>
    /// 偏好关键词缓存的 TTL。记忆写入是**轮后异步**（MemoryContextProvider 后台 AddAsync），
    /// 主动失效无收益；TTL 到期自然回源。
    /// </summary>
    private static readonly TimeSpan PreferenceCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>身份缺失时的返回体：说明性 message + 空商品，不抛异常、不写任何数据。</summary>
    private static readonly RecommendationPayload IdentityMissingPayload =
        new("无法确定用户身份", false, [], []);

    /// <summary>
    /// 返回「结合当前对话内容与用户偏好」的商品推荐，供推荐面板展示。
    /// 用户名由执行流注入（<see cref="ICurrentUserAccessor"/>），模型只填它本来就有的 <paramref name="query"/>。
    /// </summary>
    /// <param name="query">用户当前想问的内容（自然语言，可为空；空/不匹配时退化为偏好 + 精选兜底）。</param>
    public async Task<string> RecommendProductsAsync(
        [Description("用户当前想问的内容（自然语言），可为空")] string? query = null)
    {
        // 身份缺失 → 安全降级（spec R10 场景 3）
        if (currentUserAccessor.CurrentUser is null)
            return JsonSerializer.Serialize(IdentityMissingPayload, JsonOptions);

        var semanticHits = QualifiedHits(await SearchRelatedAsync(query, CancellationToken.None));
        var payload = await BuildPayloadAsync(query, semanticHits, CancellationToken.None);
        return JsonSerializer.Serialize(payload ?? IdentityMissingPayload, JsonOptions);
    }

    /// <summary>
    /// 推送入口（供 AG-UI 轮末装饰器调用）：按**同一个 builder** 算推荐负载，仅在门控通过时返回可推送的负载。
    ///
    /// 与工具入口的三点差异（其余逐字共用）：
    /// <list type="number">
    /// <item>身份缺失返回 <c>null</c> ——**不**复用 <see cref="IdentityMissingPayload"/>，
    /// 「无法确定用户身份」是说明性文案、不是推荐，推给面板会让面板被无意义内容覆盖（spec R4 场景 2）。</item>
    /// <item>门控（<see cref="ShouldPush"/>）不通过返回 <c>null</c>，由调用方按「不推送」处理。</item>
    /// <item>返回 <see cref="JsonElement"/> 而非字符串——<c>CUSTOM</c> 事件的 payload 形状是**对象**。</item>
    /// </list>
    /// 本入口**不改变**<see cref="RecommendProductsAsync"/> 的返回值与 schema，也**不新增**任何被禁依赖
    /// （<see cref="IProductSemanticSearch"/> 是本地 bge ONNX 检索、非大模型，且为可选注入）。
    /// </summary>
    /// <param name="query">推荐依据（本轮用户消息，或本轮模型调用 <c>recommend_products</c> 时传入的 query）。</param>
    /// <param name="ct">取消令牌（透传到偏好读取与语义检索）。</param>
    public async Task<JsonElement?> TryBuildPushPayloadAsync(string? query, CancellationToken ct = default)
    {
        if (currentUserAccessor.CurrentUser is null) return null;

        var semanticHits = QualifiedHits(await SearchRelatedAsync(query, ct));
        var payload = await BuildPayloadAsync(query, semanticHits, ct);
        if (payload is null || !ShouldPush(MatchKeywords(query), semanticHits, payload)) return null;

        // SerializeToElement：结果自带文档，无 JsonDocument 的释放陷阱（与 Serialize 同源同选项）
        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }

    /// <summary>
    /// 推送门控（纯函数，便于两条分支各自被独立验收；spec R2 口径）：
    /// **（关键词命中 ∪ 语义命中）** 且 **推荐列表非空**。
    ///
    /// <para>前半句是 B1 的放宽：白名单只有 23 组，「T恤有吗」这类购物意图明确的说法不在词表内，
    /// 旧判据（仅关键词命中）会漏推（盘点 L8）。现改为**并集**——语义检索有达阈值命中即放行（修 L8），
    /// 关键词命中照旧放行（关键词路径仍是降级面：bge 模型缺失 / 向量库不可用时行为不变）。</para>
    ///
    /// <para>后半句的存在理由：命中但无商品可推时负载带 <c>hasRecommendation=false</c>，
    /// 若照推会把面板从「上一次的卡片」改成兜底文案 —— 那就不是「保持上一次」了。</para>
    ///
    /// <paramref name="semanticHits"/> 须是**已过 <see cref="SemanticMatchThreshold"/>** 的命中
    /// （调用方经 <see cref="QualifiedHits"/> 过滤），本函数不再判分。
    /// </summary>
    internal static bool ShouldPush(
        IReadOnlyCollection<string> currentKeywords,
        IReadOnlyCollection<ProductSearchHit> semanticHits,
        RecommendationPayload payload)
        => payload.Products.Count > 0 && (currentKeywords.Count > 0 || semanticHits.Count > 0);

    /// <summary>
    /// 推荐负载构建：工具入口与推送入口的**唯一**口径来源，五步原样搬移
    /// （身份校验 → <see cref="MatchKeywords"/> → 偏好关键词 → <see cref="RecommendationService.Build"/>
    /// → 投影 + <see cref="RecommendationReasons"/>）。关键词来源、Top-6 截断、reason 派生规则全部照旧。
    ///
    /// <para>唯一新增的一步是 <b>B1 内容来源第二层</b>：<b>仅当</b>关键词路径算不出商品
    /// （<c>Recommended.Count == 0</c>）**且**语义有命中时，才用命中商品的类别反推关键词再 Build
    /// （<see cref="DeriveKeywords"/>）——关键词路径能算出商品时，内容一字不改（回归硬约束）。</para>
    ///
    /// 身份缺失返回 <c>null</c>，由调用方各自决定降级形态（工具入口给说明性 payload，推送入口给 null）。
    /// </summary>
    /// <param name="query">本轮推荐依据。</param>
    /// <param name="semanticHits">已过阈值的语义命中（调用方算好传入，避免同一轮重复检索）。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<RecommendationPayload?> BuildPayloadAsync(
        string? query,
        IReadOnlyList<ProductSearchHit> semanticHits,
        CancellationToken ct)
    {
        var username = currentUserAccessor.CurrentUser;
        if (username is null) return null;

        var currentKeywords = MatchKeywords(query);
        // 偏好关键词（缓存命中即用，miss 才直读记忆存储；读失败降级为空，不阻断）
        var prefKeywords = await GetPreferenceKeywordsAsync(username, ct);

        using var scope = scopeFactory.CreateScope();
        var recommendationEngine = scope.ServiceProvider.GetRequiredService<RecommendationService>();

        // 关键词路径优先（回归硬约束）：关键词能选出商品时，内容与 B1 之前逐字节一致。
        var basisKeywords = currentKeywords;
        var recommendation = recommendationEngine.Build(basisKeywords, prefKeywords);

        // 内容来源第二层（L8）：关键词路径选不出商品、但语义有命中 → 用命中商品的类别反推关键词兜底。
        // 反推词即本轮的「依据」，故 reason 也按它派生（否则语义路径的商品 reason 恒为空串、标签缺失）。
        if (recommendation.Recommended.Count == 0 && semanticHits.Count > 0)
        {
            basisKeywords = DeriveKeywords(semanticHits);
            recommendation = recommendationEngine.Build(basisKeywords, prefKeywords);
        }

        var products = recommendation.Recommended
            .Take(MaxProducts)
            .Select(product => new RecommendedProduct(
                product.Id,
                product.Name,
                product.Category,
                product.Price,
                product.Emoji,
                RecommendationReasons.Build(product, basisKeywords, prefKeywords)))
            .ToList();

        return new RecommendationPayload(
            recommendation.Message,
            recommendation.HasRecommendation,
            recommendation.MatchedCategories ?? [],
            products);
    }

    /// <summary>
    /// 语义检索本轮依据（B1-b）：输入自然语言，返回**原始**命中（未过阈值，由
    /// <see cref="QualifiedHits"/> 统一过滤）。
    /// </summary>
    /// <remarks>
    /// <para>返回空集合的三种情形（均**不**改行为）：未注入语义检索（宿主未启用 RAG / 直构造）、
    /// <paramref name="query"/> 为空、检索调用抛异常。</para>
    ///
    /// <para><b>try/catch 的边界（硬约束）</b>：只包住**检索调用本身**——偏好读取（<c>memoryCache</c> /
    /// <c>memoryStore</c>）与 <see cref="RecommendationService.Build"/> 的异常必须照常外泄，
    /// 否则会吞掉既有的「推荐计算失败」降级路径（spec R4 场景 1 的 Warning 与异常类型断言）。
    /// <see cref="OperationCanceledException"/> 正常传播（调用方取消）。</para>
    /// </remarks>
    private async Task<IReadOnlyList<ProductSearchHit>> SearchRelatedAsync(string? query, CancellationToken ct)
    {
        if (semanticSearch is null || string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            return await semanticSearch.SearchAsync(query, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 降级：bge 模型缺失 / 向量库不可用 → 退化为纯关键词门控，不阻断本轮推送
            Log.Warning(ex, "推荐语义检索失败，本轮退化为关键词门控（依据：{Query}）", query);
            return [];
        }
    }

    /// <summary>按 <see cref="SemanticMatchThreshold"/> 过滤语义命中：只有相关度达标的召回才构成推荐依据。</summary>
    private static IReadOnlyList<ProductSearchHit> QualifiedHits(IReadOnlyList<ProductSearchHit> hits)
        => hits.Where(hit => hit.Score >= SemanticMatchThreshold).ToList();

    /// <summary>
    /// 内容来源第二层（B1-d / L8 修复）：关键词路径选不出商品、但语义检索有命中时，
    /// 用命中商品的<b>类别</b>反推候选关键词，喂给 <see cref="RecommendationService.Build"/> 兜底
    /// （复用其多商品 + <see cref="RecommendationReasons"/> 派生口径）。
    /// </summary>
    /// <remarks>
    /// 为什么不能直接把用户原句当关键词：<c>ProductCatalog.SplitProducts</c> 的匹配面是
    /// <c>product.Tags ∪ {product.Category}</c> 的**精确 Ordinal 相等**，「T恤有吗」与目录里任何
    /// tag / 类别串都不相等，喂进去必然算不出商品（空转）。而命中商品携带的类别是目录内**真实存在**的串，
    /// 例：命中 #2「有机棉T恤」（类别「服装」）→ 反推「服装」→ 召回 #1 与 #2。
    /// 只取类别、不取名称（名称不是 tag/类别，精确匹配必然落空）。
    /// 保序去重：向量召回已按相关度降序，先出现的类别在 <c>SplitProducts</c> 里优先级更高。
    /// </remarks>
    private static string[] DeriveKeywords(IReadOnlyList<ProductSearchHit> hits)
        => hits.Select(hit => hit.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxKeywords)
            .ToArray();

    /// <summary>
    /// 把购物/推荐工具注册为 Agent 可调用的 AI 工具。
    /// </summary>
    public List<AITool> CreateTools()
    {
        return
        [
            // 方法组注册（非 lambda）：保留 RecommendProductsAsync 的参数默认值，query 才是**可选**参数。
            // lambda 注册会丢失默认值 → schema 把 query 标成 required → 模型不传时调用失败。
            AIFunctionFactory.Create(
                (Func<string?, Task<string>>)RecommendProductsAsync,
                "recommend_products",
                "结合当前对话与用户偏好返回推荐商品，供推荐面板展示。调用后用自然语言向用户介绍推荐结果，不要输出 JSON、不要输出商品编号。")
        ];
    }

    /// <summary>
    /// 偏好关键词（design §8.3）：<see cref="IMemoryCache"/> 命中直接返回；miss 才直读记忆存储。
    /// 读失败（返回 null）降级为空偏好且**不写缓存**——瞬时故障不占用 5 分钟 TTL，下次调用仍会重试。
    /// </summary>
    private async Task<string[]> GetPreferenceKeywordsAsync(string username, CancellationToken ct)
    {
        var cacheKey = PreferenceCacheKey(username);

        if (memoryCache.TryGetValue<string[]>(cacheKey, out var cached) && cached is not null)
            return cached;

        var keywords = await ReadPreferenceKeywordsAsync(username, ct);
        if (keywords is null) return [];

        memoryCache.Set(cacheKey, keywords, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PreferenceCacheTtl,
        });

        return keywords;
    }

    /// <summary>
    /// 直读记忆存储（按用户名过滤的 SQLite 列表）抽取偏好关键词；读失败返回 <c>null</c> 并记一条 Warning。
    ///
    /// <see cref="IMemoryStore.GetAllAsync"/> 返回 <c>IAsyncEnumerable&lt;Memory&gt;</c>（**非 List**），
    /// 必须 <c>await foreach</c> 消费完整个序列后再抽词。
    /// 关键词抽取与 <paramref name="query"/> 共用同一套 <see cref="ProductKeywordMap"/> 匹配规则，避免规则分叉。
    /// </summary>
    private async Task<string[]?> ReadPreferenceKeywordsAsync(string username, CancellationToken ct)
    {
        var matched = new List<string>();

        try
        {
            await foreach (var memory in memoryStore.GetAllAsync(new MemoryFilter { UserId = username }, ct))
            {
                matched.AddRange(MatchedKeys(memory.Text));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 降级：记忆库不可用（表未建 / 路径不可写 / 解析失败）时不阻断推荐，退化为「对话关键词 + 精选兜底」
            Log.Warning(ex, "记忆偏好读取失败，本次按无偏好处理（用户 {Username}）", username);
            return null;
        }

        return TopKeywords(matched);
    }

    /// <summary>偏好缓存的键（按用户名键控，同一用户的不同会话共享）。</summary>
    private static string PreferenceCacheKey(string username) => $"reco_prefkw_{username}";

    /// <summary>
    /// 关键词抽取：对文本跑 <see cref="ProductKeywordMap"/> 白名单匹配（key 命中，或任一展开命中，忽略大小写）。
    /// 对话关键词与记忆文本共用本方法，保证两处规则不分叉。
    /// </summary>
    private static string[] MatchKeywords(string? text) => TopKeywords(MatchedKeys(text));

    /// <summary>命中 <see cref="ProductKeywordMap"/> 的白名单 key（未排序、未去重，交由 <see cref="TopKeywords"/> 收敛）。</summary>
    private static IEnumerable<string> MatchedKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return ProductKeywordMap.Entries
            .Where(entry => ContainsIgnoreCase(text, entry.Key) || HasExpansionHit(text, entry.Value))
            .Select(entry => entry.Key);
    }

    /// <summary>
    /// 去重 → 序数排序 → Take(5)。
    /// 排序是为了**输出确定性**：结果不随字典枚举顺序 / 记忆存储的枚举顺序（<c>ORDER BY updated_at DESC</c>）变化。
    /// </summary>
    private static string[] TopKeywords(IEnumerable<string> keys)
        => keys.Distinct(StringComparer.Ordinal)
            .OrderBy(keyword => keyword, StringComparer.Ordinal)
            .Take(MaxKeywords)
            .ToArray();

    private static bool HasExpansionHit(string text, string[] expansions)
        => expansions.Any(expansion => ContainsIgnoreCase(text, expansion));

    private static bool ContainsIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>工具返回体（camelCase 序列化；message / hasRecommendation / categories 直接取 RecommendationService 口径）。</summary>
    internal sealed record RecommendationPayload(
        string Message,
        bool HasRecommendation,
        IReadOnlyList<string> Categories,
        IReadOnlyList<RecommendedProduct> Products);

    /// <summary>推荐商品条目（含确定性派生的推荐理由）。</summary>
    internal sealed record RecommendedProduct(
        int Id,
        string Name,
        string Category,
        decimal Price,
        string Emoji,
        string Reason);
}
