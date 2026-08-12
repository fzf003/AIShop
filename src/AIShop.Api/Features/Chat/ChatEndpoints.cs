using System.Diagnostics;
using AIShop.Api.Agents;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System.Security.Cryptography;
using System.Text;

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
            AgentSession? session = null;
            try
            {
                var modelId = req.Model ?? router.ActiveModel;
                var agent = router.GetAgent(modelId);
                (result, session) = await agent.RunChatAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
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


            // 2.1 Cache agent result for /recommendations to avoid duplicate LLM call
            var chatHash = GetMessageHash(req.Message ?? "");
            var chatCacheKey = $"agent_result_{req.Username}_{chatHash}";
            cache.Set(chatCacheKey, (result, session), TimeSpan.FromMinutes(5));

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
                chatReply = new ChatReply(result.Reply ?? "",
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

                chatReply = new ChatReply(result.Reply ?? "", recDtos, otherDtos,
                    "根据您的兴趣，为您推荐：", HasRecommendation: true,
                    recDtos.Select(p => p.Category).Distinct().ToArray());
            }

            productSw.Stop();

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
            ISessionRepository sessions,
            IChatMessageRepository chatRepo,
            IProductCatalogService catalog,
            ModelRouter router,
            IMemoryCache cache,
            IPreferenceRepository prefRepo,
            CancellationToken ct) =>
        {
            var endpointSw = Stopwatch.StartNew();
            var logger = Log.ForContext("SourceContext", "Diagnose");

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            // 端点层偏好注入（P2-8）：/recommendations 缓存命中路径不调 RunChatAsync，
            // Agent 层 StateBag 偏好回填仅 /chat 生效；这里加载偏好后用
            // RecommendationMerger.MergeKeywords 合并，与 /chat 口径一致，避免两端推荐不一致。
            var prefs = await prefRepo.GetByUserIdAsync(user.Id, ct);

            // Use default agent for recommendations
            var defaultAgent = router.GetDefaultAgent();

            // Load last user message and ask Agent for keyword matching
            var lastUserMessage = await chatRepo.GetLastUserMessageAsync(sid, ct);

            if (lastUserMessage is null)
                return Results.Ok(new RecommendationResponse(null, [], "暂无对话历史，请先聊天。", null));

            // Build cache key: {prefix}_{username}_{sha256(message)[..16]}
            var hash = GetMessageHash(lastUserMessage.Content);
            var cacheKey = $"reco_{req.Username}_{hash}";
            var agentResultCacheKey = $"agent_result_{req.Username}_{hash}";

            if (cache.TryGetValue(cacheKey, out RecommendationResponse? cached) && cached is not null)
            {
                endpointSw.Stop();
                logger.Information("[Diagnose] /recommendations CacheHit=true SessionId={SessionId} Total={ElapsedMs}ms",
                    sid, endpointSw.ElapsedMilliseconds);
                return Results.Ok(cached);
            }

            // Try to reuse agent result cached by /chat endpoint to avoid duplicate LLM call
            AgentChatResult? agentResult = null;
            AgentSession? agentSession = null;
            var agentSw = new Stopwatch();
            if (cache.TryGetValue(agentResultCacheKey, out var cachedAgentTuple) && cachedAgentTuple is not null)
            {
                var tuple = ((AgentChatResult Result, AgentSession? Session))cachedAgentTuple;
                agentResult = tuple.Result;
                agentSession = tuple.Session;
                logger.Information("[Diagnose] /recommendations AgentCacheHit=true SessionId={SessionId}", sid);
            }

            if (agentResult is null)
            {
                agentSw.Start();
                try
                {
                    (agentResult, agentSession) = await defaultAgent.RunChatAsync(sid, lastUserMessage.Content, req.Username, ct: ct);
                }
                catch (Exception ex)
                {
                    agentSw.Stop();
                    logger.Error(ex, "[Diagnose] /recommendations Agent调用失败 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                        agentSw.ElapsedMilliseconds, sid);
                    return Results.Ok(new RecommendationResponse(null, [], "推荐服务暂时不可用，请重试。", null));
                }
                agentSw.Stop();
                logger.Information("[Diagnose] /recommendations Agent调用 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                    agentSw.ElapsedMilliseconds, sid);
            }

            // Validate keywords against white-list (same logic as /chat endpoint)
            var keywordSw = Stopwatch.StartNew();
            var validKeywords = (agentResult.Keywords ?? [])
                .Where(k => catalog.KeywordMap.ContainsKey(k))
                .Distinct()
                .Take(5)
                .ToArray();
            keywordSw.Stop();

            var productSw = Stopwatch.StartNew();
            RecommendationResponse response;

            // 推荐合并（design 4.3 / T17 RecommendationMerger）：与 /chat 同一口径——
            // 当前关键词（Agent Keywords 白名单过滤）优先，不足 3 个时用偏好权重 Top-N 补齐到 ≤5。
            // 偏好词同样先经白名单过滤（P2-4），避免非法/空白偏好词产生空推荐。
            var prefKeywords = FilterValidPreferenceKeywords(prefs?.KeywordsJson, catalog);
            var merged = RecommendationMerger.MergeKeywords(validKeywords, prefKeywords);

            if (merged.Length > 0)
            {
                var (recommended, others) = catalog.SplitProducts(merged);
                var recDtos = recommended.Select(ToDto).ToList();
                var otherDtos = recommended.Length == 0
                    ? catalog.All.Take(6).Select(ToDto).ToList()
                    : others.Take(12).Select(ToDto).ToList();

                response = new RecommendationResponse(
                    recDtos.FirstOrDefault(),
                    otherDtos,
                    "根据您的兴趣，为您推荐：",
                    recDtos.Select(p => p.Category).Distinct().ToArray());
            }
            else
            {
                var fallback = catalog.All.Take(6).Select(ToDto).ToList();
                response = new RecommendationResponse(
                    null,
                    fallback,
                    "暂无特定推荐 — 浏览精选商品",
                    null);
            }

            productSw.Stop();

            // Only cache non-empty results (recommended or fallback)
            if (response.BestMatch is not null || response.Other.Count > 0)
            {
                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                };
                cache.Set(cacheKey, response, cacheOptions);
            }

            endpointSw.Stop();
            logger.Information(
                "[Diagnose] /recommendations 总耗时 Total={TotalMs}ms Agent={AgentMs}ms " +
                "KeywordMatch={KeywordMs}ms ProductMatch={ProductMs}ms " +
                "SessionId={SessionId}",
                endpointSw.ElapsedMilliseconds, agentSw.ElapsedMilliseconds,
                keywordSw.ElapsedMilliseconds, productSw.ElapsedMilliseconds,
                sid);

            return Results.Ok(response);
        });

        api.MapGet("/models", (ModelRouter router) =>
            Results.Ok(router.GetAvailableModels()));

        api.MapGet("/products", (IProductRepository products) =>
            Results.Ok(new { products = products.GetAll() }));
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);

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

    private static string GetMessageHash(string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
