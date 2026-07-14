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

public sealed record AgentChatResult(string Reply, string[] Keywords, string[]? Preferences);

public sealed record ChatRequest(string Username, string Message);
public sealed record ChatReply(
    string Response,
    List<ProductDto>? RecommendedProducts,
    List<ProductDto>? OtherProducts,
    string? RecMessage,
    bool HasRecommendation,
    string[]? MatchedCategories);

public sealed record LoginRequest(string Username);
public sealed record LoginResponse(string Username, string DisplayName, string SessionId, List<ChatMessageDto> History);

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
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.NotFound(new { detail = "User not found" });

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var history = await chatRepo.GetSessionHistoryAsync(Guid.Parse(sessionId), ct: ct);

            return Results.Ok(new LoginResponse(
                user.Username, user.DisplayName, sessionId,
                history.Select(m => new ChatMessageDto(m.Role, m.Content)).ToList()));
        });

        api.MapPost("/chat", async (
            ChatRequest req,
            IUserRepository users,
            ISessionRepository sessions,
            IChatMessageRepository chatRepo,
            IProductCatalogService catalog,
            IShoppingAssistantAgent shoppingAgent,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var endpointSw = Stopwatch.StartNew();
            var logger = Log.ForContext("SourceContext", "Diagnose");

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            // 1. Get response from agent (history loaded from SQLite by provider)
            var agentSw = Stopwatch.StartNew();
            AgentChatResult result;
            AgentSession? session = null;
            try
            {
                (result, session) = await shoppingAgent.RunChatAsync(sid, req.Message, ct);
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
            chatRepo.Add(new Core.Entities.ChatMessage
            {
                SessionId = sid, Role = "user", Content = req.Message
            });
            chatRepo.Add(new Core.Entities.ChatMessage
            {
                SessionId = sid, Role = "assistant", Content = result.Reply
            });
            await chatRepo.SaveChangesAsync(ct);

            // 2.1 Cache agent result for /recommendations to avoid duplicate LLM call
            var chatHash = GetMessageHash(req.Message);
            var chatCacheKey = $"agent_result_{req.Username}_{chatHash}";
            cache.Set(chatCacheKey, (result, session), TimeSpan.FromMinutes(5));

            // 2.5 Persist extracted preferences to session StateBag for next round
            if (result.Preferences is { Length: > 0 } && session is not null)
            {
                session.StateBag.SetValue("Preferences", string.Join("、", result.Preferences));
            }

            // 3. Validate keywords against white-list
            var keywordSw = Stopwatch.StartNew();
            var validKeywords = (result.Keywords ?? [])
                .Where(k => catalog.KeywordMap.ContainsKey(k))
                .Distinct()
                .Take(5)
                .ToArray();
            keywordSw.Stop();

            // 4. Build recommendation response
            var productSw = Stopwatch.StartNew();
            ChatReply chatReply;

            if (validKeywords.Length > 0)
            {
                var (recommended, others) = catalog.SplitProducts(validKeywords);
                var recDtos = recommended.Select(ToDto).ToList();
                var otherDtos = recommended.Length == 0
                    ? catalog.All.Take(6).Select(ToDto).ToList()
                    : others.Take(12).Select(ToDto).ToList();

                chatReply = new ChatReply(result.Reply, recDtos, otherDtos,
                    "根据您的兴趣，为您推荐：", HasRecommendation: true,
                    recDtos.Select(p => p.Category).Distinct().ToArray());
            }
            else
            {
                var fallback = catalog.All.Take(6).Select(ToDto).ToList();
                chatReply = new ChatReply(result.Reply,
                    RecommendedProducts: null,
                    OtherProducts: fallback,
                    "暂无特定推荐 — 浏览精选商品",
                    HasRecommendation: false,
                    MatchedCategories: null);
            }

            productSw.Stop();
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
            IShoppingAssistantAgent shoppingAgent,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var endpointSw = Stopwatch.StartNew();
            var logger = Log.ForContext("SourceContext", "Diagnose");

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

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
                var tuple = ((AgentChatResult Result, AgentSession? Session))cachedAgentTuple!;
                agentResult = tuple.Result;
                agentSession = tuple.Session;
                logger.Information("[Diagnose] /recommendations AgentCacheHit=true SessionId={SessionId}", sid);
            }

            if (agentResult is null)
            {
                agentSw.Start();
                try
                {
                    (agentResult, agentSession) = await shoppingAgent.RunChatAsync(sid, lastUserMessage.Content, ct);
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

            if (validKeywords.Length > 0)
            {
                var (recommended, others) = catalog.SplitProducts(validKeywords);
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

        api.MapGet("/products", (IProductRepository products) =>
            Results.Ok(new { products = products.GetAll() }));
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);

    private static string GetMessageHash(string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
