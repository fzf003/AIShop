using System.Diagnostics;
using AIShop.Api.Agents;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System.Text.RegularExpressions;

namespace AIShop.Api.Features.Chat;

public sealed record AgentChatResult(
    string Reply, string[] Keywords, string[]? Preferences);

public sealed record ChatRequest(string Username, string Message, string? Model = null);
public sealed record ChatReply(
    string Response,
    List<ProductDto>? RecommendedProducts,
    List<ProductDto>? OtherProducts,
    string? RecMessage,
    bool HasRecommendation,
    string[]? MatchedCategories);

public sealed record LoginRequest(string Username);
public sealed record LoginResponse(string Username, string DisplayName, string SessionId, List<ChatMessageDto> History, List<ModelInfo> Models);

public sealed record ChatMessageDto(string Role, string Content);

public sealed record RecommendationRequest(string Username, string? Provider);
public sealed record RecommendationResponse(ProductDto? BestMatch, List<ProductDto> Other, string Message, string[]? MatchedCategories);

public sealed record ProductDto(int Id, string Name, string Category, string[] Tags, decimal Price, string Emoji);

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").WithTags("Chat");

        api.MapPost("/login", async (
            LoginRequest req,
            IUserRepository users,
            ISessionRepository sessions,
            IChatMessageRepository chatRepo,
            ModelRouter router,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.NotFound(new { detail = "User not found" });

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var history = await chatRepo.GetSessionHistoryAsync(Guid.Parse(sessionId), ct: ct);

            return Results.Ok(new LoginResponse(
                user.Username, user.DisplayName, sessionId,
                history.Select(m => new ChatMessageDto(m.Role, m.Content)).ToList(),
                router.GetAvailableModels().ToList()));
        });

        api.MapPost("/chat", async (
            ChatRequest req,
            IUserRepository users,
            ISessionRepository sessions,
            IChatMessageRepository chatRepo,
            IProductCatalogService catalog,
            ModelRouter router,
            IMemoryCache cache,
            IPreferenceRepository prefRepo,
            IPreferenceQueue queue,
            CancellationToken ct) =>
        {
            var endpointSw = Stopwatch.StartNew();
            var logger = Log.ForContext("SourceContext", "Diagnose");

            // 验证必填参数
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { detail = "用户名不能为空" });

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            // 会话重建回填：从 DB 加载历史偏好，按权重 Top-5 生成顿号连接文本注入 Agent 上下文
            var prefs = await prefRepo.GetByUserIdAsync(user.Id, ct);
            var preferencesText = string.Join("、", RecommendationMerger.GetTopPreferenceKeywords(prefs?.KeywordsJson, 5));

            // 1. Get agent and response (history loaded from SQLite by provider)
            var agentSw = Stopwatch.StartNew();
            AgentChatResult result;
            try
            {
                var modelId = req.Model ?? router.ActiveModel;
                var agent = router.GetAgent(modelId);
                // R8：会话对象（第二元组元素）已不再被端点使用（agent_result_ 缓存已删），丢弃
                (result, _) = await agent.RunChatAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
            }
            catch (KeyNotFoundException knf)
            {
                logger.Error(knf, "[Diagnose] KeyNotFoundException in /chat: modelId={ModelId}", req.Model ?? router.ActiveModel);
                return Results.BadRequest(new { detail = "不支持的模型" });
            }
            catch (Exception ex)
            {
                agentSw.Stop();
                logger.Error(ex, "[Diagnose] /chat Agent调用失败 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                    agentSw.ElapsedMilliseconds, sid);
                result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", [], null);
            }
            agentSw.Stop();
            logger.Information("[Diagnose] /chat Agent调用 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                agentSw.ElapsedMilliseconds, sid);

            // 2. Save user message + assistant response to SQLite
            // 由 Agent 的 SqliteChatHistoryProvider.StoreChatHistoryAsync 自动处理，端点不再重复写入

            // 3. 关键词匹配：从用户输入直接匹配（不依赖模型结构化输出）
            // 匹配逻辑：消息中包含关键词 → 该关键词对应的所有标签相关产品都推荐
            // 支持关键词、标签、类别、商品名的多维度匹配
            var keywordSw = Stopwatch.StartNew();
            var userMsg = req.Message ?? "";
            var validKeywords = catalog.KeywordMap.Keys
                .Where(k => userMsg.Contains(k, StringComparison.OrdinalIgnoreCase)
                    || (catalog.KeywordMap.TryGetValue(k, out var tags)
                        && tags.Any(tag => userMsg.Contains(tag, StringComparison.OrdinalIgnoreCase))))
                .Distinct()
                .Take(5)
                .ToArray();

            // 如果消息关键词匹配失败，尝试从 AgentChatResult.Keywords 回退
            if (validKeywords.Length == 0 && result.Keywords is { Length: > 0 })
            {
                validKeywords = result.Keywords
                    .Where(k => catalog.KeywordMap.ContainsKey(k))
                    .Distinct()
                    .Take(5)
                    .ToArray();
            }
            keywordSw.Stop();

            // 4. Build recommendation response
            var productSw = Stopwatch.StartNew();
            ChatReply chatReply;

            // 推荐合并（design 4.3 / T17 RecommendationMerger）：当前消息关键词优先，
            // 不足 3 个时用偏好权重 Top-N 补齐到 ≤5，按序数忽略大小写去重。
            // 偏好词来自 DB，先经白名单过滤（P2-4），避免非法/空白偏好词合并后
            // SplitProducts 返回空推荐却仍标记 HasRecommendation=true。
            var prefKeywords = FilterValidPreferenceKeywords(prefs?.KeywordsJson, catalog);
            var merged = RecommendationMerger.MergeKeywords(validKeywords, prefKeywords);

            if (merged.Length == 0)
            {
                // 无当前关键词且无偏好 → All.Take(6) 兜底（HasRecommendation=false）
                var fallback = catalog.All.Take(6).Select(ToDto).ToList();
                chatReply = new ChatReply(SanitizeReply(result.Reply),
                    RecommendedProducts: null,
                    OtherProducts: fallback,
                    "暂无特定推荐 — 浏览精选商品",
                    HasRecommendation: false,
                    MatchedCategories: null);
            }
            else
            {
                var (recommended, others) = catalog.SplitProducts(merged);
                var recDtos = recommended.Select(ToDto).ToList();
                var otherDtos = recommended.Length == 0
                    ? catalog.All.Take(6).Select(ToDto).ToList()
                    : others.Take(12).Select(ToDto).ToList();

                chatReply = new ChatReply(SanitizeReply(result.Reply), recDtos, otherDtos,
                    "根据您的兴趣，为您推荐：", HasRecommendation: true,
                    recDtos.Select(p => p.Category).Distinct().ToArray());
            }

            productSw.Stop();

            // R8：聊天产物联动推荐栏 — 写用户维度推荐快照缓存（推荐以聊天产物为准）。
            // 先同步更新内存（/recommendations 立即读到最新推荐，与聊天 100% 一致），
            // 偏好异步入队落库保持现状；TTL 10min，miss 时 /recommendations 走偏好/精选兜底。
            cache.Set($"recommend_{req.Username}",
                new RecommendationSnapshot(
                    chatReply.RecommendedProducts,
                    chatReply.OtherProducts,
                    chatReply.MatchedCategories,
                    chatReply.HasRecommendation,
                    chatReply.RecMessage ?? ""),
                TimeSpan.FromMinutes(10));

            // 偏好异步写入：端点只入队轻量 UserPreferenceUpdate（权重累加在
            // PreferenceWriteHostedService worker 侧串行读-改-写完成），立即返回不等待落库；
            // DropOldest 语义下 TryEnqueue 仅在 channel 标记完成后才返回 false，此处 Warning 作为
            // channel 完成兜底；队列满挤掉最旧的观测日志已内聚到 PreferenceQueue.TryEnqueue（P2-3）
            if (result.Preferences is { Length: > 0 })
            {
                var update = new UserPreferenceUpdate(user.Id, result.Preferences);
                if (!queue.TryEnqueue(update))
                    Log.Warning("Preference queue completed, update dropped for {UserId}", user.Id);
            }

            endpointSw.Stop();
            logger.Information(
                "[Diagnose] /chat 总耗时 Total={TotalMs}ms Agent={AgentMs}ms " +
                "KeywordMatch={KeywordMs}ms ProductMatch={ProductMs}ms " +
                "SessionId={SessionId}",
                endpointSw.ElapsedMilliseconds, agentSw.ElapsedMilliseconds,
                keywordSw.ElapsedMilliseconds, productSw.ElapsedMilliseconds,
                sid);

            return Results.Ok(chatReply);
        });

        api.MapPost("/recommendations", async (
            RecommendationRequest req,
            IUserRepository users,
            IProductCatalogService catalog,
            IPreferenceRepository prefRepo,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var endpointSw = Stopwatch.StartNew();
            var logger = Log.ForContext("SourceContext", "Diagnose");

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            // R8：推荐以聊天产物为准 — 优先读 /api/chat 写入的用户维度快照缓存 recommend_{username}。
            // 命中 → 直接按快照构造 RecommendationResponse（与聊天 100% 一致），
            // 不再自行读 DB 最新消息做字面匹配（聊天产物即数据源）。
            var snapshot = cache.Get<RecommendationSnapshot>($"recommend_{req.Username}");
            if (snapshot is not null)
            {
                var cached = new RecommendationResponse(
                    snapshot.Recommended?.FirstOrDefault(),
                    snapshot.Other ?? [],
                    snapshot.Message,
                    snapshot.MatchedCategories);
                return Results.Ok(cached);
            }

            // miss（新用户 / 缓存过期）→ 偏好兜底：偏好关键词白名单过滤 + 合并（无当前消息关键词）。
            // 与 /chat 推荐分支同一套 FilterValidPreferenceKeywords / MergeKeywords / SplitProducts 口径。
            var prefs = await prefRepo.GetByUserIdAsync(user.Id, ct);
            var prefKeywords = FilterValidPreferenceKeywords(prefs?.KeywordsJson, catalog);

            var merged = RecommendationMerger.MergeKeywords([], prefKeywords);

            RecommendationResponse response;
            if (merged.Length > 0)
            {
                var (recommended, others) = catalog.SplitProducts(merged);
                if (recommended.Length > 0)
                {
                    // 有推荐（merged>0 且有商品）→ 提示语与内容一致；固定顺序，无 shuffle
                    var recDtos = recommended.Select(ToDto).ToList();
                    var otherDtos = others.Take(12).Select(ToDto).ToList();
                    response = new RecommendationResponse(
                        recDtos.FirstOrDefault(),
                        otherDtos,
                        "根据您的兴趣，为您推荐：",
                        recDtos.Select(p => p.Category).Distinct().ToArray());
                }
                else
                {
                    // merged>0 但无商品命中（如偏好词均未命中商品）→ 兜底精选，不显示「已推荐」空列表
                    response = new RecommendationResponse(
                        null,
                        catalog.All.Take(6).Select(ToDto).ToList(),
                        "为您精选商品",
                        null);
                }
            }
            else
            {
                // 无关键词无偏好 → All.Take(6) 固定顺序兜底（无 shuffle）。
                // 提示语与内容一致：有兜底商品说「为您精选商品」，仅当商品库完全为空才说「暂无特定推荐」
                //（消除 R6「暂无特定推荐」却列表有商品的矛盾）。
                var fallback = catalog.All.Take(6).Select(ToDto).ToList();
                response = new RecommendationResponse(
                    null,
                    fallback,
                    catalog.All.Count == 0 ? "暂无特定推荐" : "为您精选商品",
                    null);
            }

            endpointSw.Stop();
            logger.Information(
                "[Diagnose] /recommendations 总耗时 Total={TotalMs}ms 推荐栏（聊天快照缓存优先，miss 偏好兜底，无 Agent）",
                endpointSw.ElapsedMilliseconds);

            return Results.Ok(response);
        });

        api.MapGet("/models", (ModelRouter router) =>
            Results.Ok(router.GetAvailableModels()));

        api.MapGet("/products", (IProductRepository products) =>
            Results.Ok(new { products = products.GetAll() }));
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);

    // 商品 ID 标记正则（R4/R5）：预编译 + 显式 timeout（满足 S6444/S6354）。
    // 不使用裸 \d+（会误删价格/数量），只匹配带 # 前缀或 商品ID 前缀的 ID 形式。
    // R5 收紧：#\d+ 原来会误删非商品 ID 的「#数字」（如「订单号 #123456」「参见 #3 条款」），
    // 商品 ID 范围是 1-18（ProductSeedData.Products 的 Id 显式 1..18）。
    // 因此 # 前缀改走「\d+ 提取 + int.TryParse 校验 1..18 才删」，范围变化时只需同步 Min/MaxProductId。
    // 商品ID 前缀则保持任意数字——前缀本身即明确的产品 ID 标记（无歧义），收紧反而会回归 R4「隐藏商品 ID 展示」的诉求。
    private static readonly Regex HashIdPattern =
        new(@"#(?<id>\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex ProductIdLabelPattern =
        new(@"商品ID[\s:：]*\d+", RegexOptions.None, TimeSpan.FromSeconds(1));

    // 商品 ID 合法范围（R5）：与 ProductSeedData.Products 的 Id 1..18 一致；新增/删除商品导致范围变化时同步这里。
    private const int MinProductId = 1;
    private const int MaxProductId = 18;

    /// <summary>
    /// 清洗 LLM 回复文本（R4/R5）：去除「#5」「商品ID: 4」「商品ID：7」等商品 ID 展示，
    /// 避免对话历史向用户暴露商品 ID。只清洗 Reply 字符串，绝不触碰
    /// RecommendedProducts/OtherProducts 的 ProductDto.Id——前端加购依赖的结构化数据，不从文本解析。
    /// R5 收紧：# 前缀仅删 1-18 范围内的 ID（防误删「订单号 #123456」等非商品 ID 的 #数字）。
    /// </summary>
    private static string SanitizeReply(string? reply)
    {
        var text = reply ?? "";
        // 去 #5（1-18 内商品 ID）；#123456/#20（非 1-18）保留不删
        text = HashIdPattern.Replace(text, static match =>
            IsProductId(match.Groups["id"].Value) ? "" : match.Value);
        text = ProductIdLabelPattern.Replace(text, ""); // 去 商品ID 4 / 商品ID：4
        return text.Trim();                             // 清残留空格/标点
    }

    /// <summary>
    /// 判断 # 后的数字是否为合法商品 ID（R5）：落在 1-18 范围内才删除。
    /// </summary>
    private static bool IsProductId(string idText) =>
        int.TryParse(idText, out var id) && id is >= MinProductId and <= MaxProductId;

    /// <summary>
    /// 偏好关键词白名单过滤（P2-4）：偏好词来自 DB，可能含非法词/空白词。
    /// 仅保留非空白、且命中商品关键词白名单（KeywordMap）或任一商品 Tag 的词，
    /// 避免非法偏好词合并后 SplitProducts 返回空推荐却仍标记 HasRecommendation=true（空推荐 UX 退化）。
    /// </summary>
    private static string[] FilterValidPreferenceKeywords(string? keywordsJson, IProductCatalogService catalog)
    {
        var productTags = catalog.All.SelectMany(p => p.Tags).ToHashSet(StringComparer.Ordinal);
        return RecommendationMerger.GetTopPreferenceKeywords(keywordsJson, 5)
            .Where(kw => !string.IsNullOrWhiteSpace(kw)
                && (catalog.KeywordMap.ContainsKey(kw) || productTags.Contains(kw)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
