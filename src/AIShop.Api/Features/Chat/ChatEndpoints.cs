using AIShop.Api.Agents;
using AIShop.Core.Entities;
using AIShop.Core.Interfaces;
using AIShop.Infrastructure.Data;
using AIShop.Infrastructure.Services;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;

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
            AppDbContext db,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.NotFound(new { detail = "User not found" });

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var history = await db.ChatMessages
                .Where(m => m.SessionId == Guid.Parse(sessionId))
                .OrderBy(m => m.Timestamp)
                .Select(m => new ChatMessageDto(m.Role, m.Content))
                .ToListAsync(ct);

            return Results.Ok(new LoginResponse(user.Username, user.DisplayName, sessionId, history));
        });

        api.MapPost("/chat", async (
            ChatRequest req,
            IUserRepository users,
            ISessionRepository sessions,
            AppDbContext db,
            IShoppingAssistantAgent shoppingAgent,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            // 1. Get response from agent (history loaded from SQLite by provider)
            AgentChatResult result;
            AgentSession? session = null;
            try
            {
                (result, session) = await shoppingAgent.RunChatAsync(sid, req.Message, ct);
            }
            catch
            {
                result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", [], null);
            }


            // 2. Save user message + assistant response to SQLite
            db.ChatMessages.Add(new Core.Entities.ChatMessage
            {
                SessionId = sid, Role = "user", Content = req.Message
            });
            db.ChatMessages.Add(new Core.Entities.ChatMessage
            {
                SessionId = sid, Role = "assistant", Content = result.Reply
            });
            await db.SaveChangesAsync(ct);

            // 2.5 Persist extracted preferences to session StateBag for next round
            if (result.Preferences is { Length: > 0 } && session is not null)
            {
                session.StateBag.SetValue("Preferences", string.Join("、", result.Preferences));
            }

            // 3. Validate keywords against white-list
            var validKeywords = (result.Keywords ?? [])
                .Where(k => ProductCatalog.KeywordMap.ContainsKey(k))
                .Distinct()
                .Take(5)
                .ToArray();

            // 4. Build recommendation response
            ChatReply chatReply;

            if (validKeywords.Length > 0)
            {
                var (recommended, others) = ProductCatalog.SplitProducts(validKeywords);
                var recDtos = recommended.Select(ToDto).ToList();
                var otherDtos = recommended.Length == 0
                    ? ProductCatalog.All.Take(6).Select(ToDto).ToList()
                    : others.Take(12).Select(ToDto).ToList();

                chatReply = new ChatReply(result.Reply, recDtos, otherDtos,
                    "根据您的兴趣，为您推荐：", HasRecommendation: true,
                    recDtos.Select(p => p.Category).Distinct().ToArray());
            }
            else
            {
                var fallback = ProductCatalog.All.Take(6).Select(ToDto).ToList();
                chatReply = new ChatReply(result.Reply,
                    RecommendedProducts: null,
                    OtherProducts: fallback,
                    "暂无特定推荐 — 浏览精选商品",
                    HasRecommendation: false,
                    MatchedCategories: null);
            }

            return Results.Ok(chatReply);
        });

        api.MapPost("/recommendations", async (
            RecommendationRequest req,
            IUserRepository users,
            ISessionRepository sessions,
            AppDbContext db,
            IShoppingAssistantAgent shoppingAgent,
            CancellationToken ct) =>
        {
            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
                return Results.Unauthorized();

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            // Load last user message and ask Agent for keyword matching
            var lastUserMessage = await db.ChatMessages
                .Where(m => m.SessionId == sid && m.Role == "user")
                .OrderByDescending(m => m.Timestamp)
                .Select(m => m.Content)
                .FirstOrDefaultAsync(ct);

            if (lastUserMessage is null)
                return Results.Ok(new RecommendationResponse(null, [], "暂无对话历史，请先聊天。", null));

            AgentChatResult result;
            try
            {
                (result, _) = await shoppingAgent.RunChatAsync(sid, lastUserMessage, ct);
            }
            catch
            {
                return Results.Ok(new RecommendationResponse(null, [], "推荐服务暂时不可用，请重试。", null));
            }
            var preferences = result.Keywords ?? [];

            var matched = ProductCatalog.MatchProducts(preferences);
            var dtos = matched.Select(ToDto).ToList();

            // 在这里加推荐的匹配后的类别
            var matchedCategories = dtos.Select(p => p.Category).Distinct().ToArray();

            return Results.Ok(new RecommendationResponse(
                dtos.FirstOrDefault(),
                dtos.Skip(1).ToList(),
                $"根据您的偏好{string.Join("、", preferences)}，为您推荐：",
                matchedCategories));
        });

        api.MapGet("/products", (IProductRepository products) =>
            Results.Ok(new { products = products.GetAll() }));
    }

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);
}
