using System.ClientModel;
using System.Diagnostics;
using AIShop.Core.Entities;
using AIShop.Service;
using AIShop.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIShop.Api.Features.Chat;

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
public sealed record RecommendationResponse(
    ProductDto? BestMatch,
    List<ProductDto> Recommended,
    List<ProductDto> Other,
    string Message,
    string[]? MatchedCategories);

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
                // R10：/api/login 历史清洗——assistant 历史消息经 SanitizeReply 去商品 ID 展示
                //（user 消息原样保留；只清洗展示文本，不碰结构化数据/前端加购来源）
                history.Select(m => new ChatMessageDto(m.Role,
                    m.Role == "assistant" ? SanitizeReply(m.Content) : m.Content)).ToList(),
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
                // R11：被吞异常进 OTel span——catch 吞掉只 SetStatus(Error) 不产生 exception 事件；
                // RecordException 扩展在当前 DiagnosticSource 版本不可用，手动 AddEvent("exception") 等价。
                Activity.Current?.SetStatus(ActivityStatusCode.Error, knf.Message);
                Activity.Current?.AddEvent(new ActivityEvent("exception",
                    tags: new ActivityTagsCollection
                    {
                        { "exception.type", knf.GetType().FullName },
                        { "exception.message", knf.Message },
                    }));
                logger.Error(knf, "[Diagnose] KeyNotFoundException in /chat: modelId={ModelId}", req.Model ?? router.ActiveModel);
                return Results.BadRequest(new { detail = "不支持的模型" });
            }
            catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))
            {
                // T13（方案 A 应用层重试）：网络/超时/429 等临时性失败 → 换默认模型重试一次（新 run_id、新轮）。
                // 首次失败仅记 Warning，不置 Activity Error——重试成功即不污染 span；
                // 重试仍失败才走 R11 的 OTel Error + 兜底语义。
                logger.Warning(ex,
                    "[Diagnose] /chat Agent调用失败，换默认模型重试一次 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                    agentSw.ElapsedMilliseconds, sid);
                try
                {
                    // 重试一次：首次若用非默认模型则换到默认模型；已用默认模型则同模型再试
                    var retryModel = router.ActiveModel;
                    (result, _) = await router.GetAgent(retryModel).RunChatAsync(
                        sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
                }
                catch (Exception retryEx)
                {
                    agentSw.Stop();
                    // R11：重试也失败——被吞异常进 OTel span，置 Error + exception 事件后兜底返回
                    Activity.Current?.SetStatus(ActivityStatusCode.Error, retryEx.Message);
                    Activity.Current?.AddEvent(new ActivityEvent("exception",
                        tags: new ActivityTagsCollection
                        {
                            { "exception.type", retryEx.GetType().FullName },
                            { "exception.message", retryEx.Message },
                        }));
                    logger.Error(retryEx,
                        "[Diagnose] /chat 重试仍失败 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                        agentSw.ElapsedMilliseconds, sid);
                    result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", [], null);
                }
            }
            catch (Exception ex)
            {
                agentSw.Stop();
                // R11：被吞异常进 OTel span——catch 吞掉后兜底返回（不抛异常），错误详情默认不可见
                Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
                Activity.Current?.AddEvent(new ActivityEvent("exception",
                    tags: new ActivityTagsCollection
                    {
                        { "exception.type", ex.GetType().FullName },
                        { "exception.message", ex.Message },
                    }));
                logger.Error(ex, "[Diagnose] /chat Agent调用失败 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                    agentSw.ElapsedMilliseconds, sid);
                result = new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", [], null);
            }
            agentSw.Stop();
            logger.Information("[Diagnose] /chat Agent调用 AgentCall={ElapsedMs}ms SessionId={SessionId}",
                agentSw.ElapsedMilliseconds, sid);

            // 2. Save user message + assistant response to SQLite
            // 由 Agent 的 SqliteChatHistoryProvider.StoreChatHistoryAsync 自动处理，端点不再重复写入

            // 3. 推荐计算（复用 BuildChatReply：关键词匹配 + 偏好合并 + SplitProducts + 缓存写入 + 偏好入队）
            var userMsg = req.Message ?? "";
            var chatReply = BuildChatReply(result, userMsg, prefs, catalog, req.Username, user.Id, cache, queue);

            endpointSw.Stop();
            logger.Information(
                "[Diagnose] /chat 总耗时 Total={TotalMs}ms Agent={AgentMs}ms SessionId={SessionId}",
                endpointSw.ElapsedMilliseconds, agentSw.ElapsedMilliseconds,
                sid);

            return Results.Ok(chatReply);
        });

        // T21：SSE 流式端点 — 逐块推送 LLM 文本，结束后一次性发送推荐结果
        api.MapPost("/chat/stream", async (
            HttpContext httpContext,
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
            var logger = Log.ForContext("SourceContext", "Diagnose");

            if (string.IsNullOrWhiteSpace(req.Username))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { detail = "用户名不能为空" }, ct);
                return;
            }

            var user = await users.GetByUsernameAsync(req.Username, ct);
            if (user is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var sessionId = await sessions.GetOrCreateSessionIdAsync(user.Id, ct);
            var sid = Guid.Parse(sessionId);

            var prefs = await prefRepo.GetByUserIdAsync(user.Id, ct);
            var preferencesText = string.Join("、", RecommendationMerger.GetTopPreferenceKeywords(prefs?.KeywordsJson, 5));

            // 设置 SSE 响应头
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";

            var writer = new StreamWriter(httpContext.Response.Body);

            try
            {
                var modelId = req.Model ?? router.ActiveModel;

                // 尝试流式调用
                IAsyncEnumerable<ChatStreamChunk> streamChunks;
                try
                {
                    var agent = router.GetAgent(modelId);
                    streamChunks = agent.RunChatStreamAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
                }
                catch (KeyNotFoundException knf)
                {
                    Activity.Current?.SetStatus(ActivityStatusCode.Error, knf.Message);
                    Activity.Current?.AddEvent(new ActivityEvent("exception",
                        tags: new ActivityTagsCollection
                        {
                            { "exception.type", knf.GetType().FullName },
                            { "exception.message", knf.Message },
                        }));
                    logger.Error(knf, "[Diagnose] KeyNotFoundException in /chat/stream: modelId={ModelId}", modelId);
                    await WriteSseEventAsync(writer, "error", JsonSerializer.Serialize(new { message = "不支持的模型" }));
                    return;
                }
                catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))
                {
                    logger.Warning(ex,
                        "[Diagnose] /chat/stream Agent调用失败，换默认模型重试一次 AgentCall SessionId={SessionId}", sid);
                    try
                    {
                        var retryModel = router.ActiveModel;
                        streamChunks = router.GetAgent(retryModel).RunChatStreamAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
                    }
                    catch (Exception retryEx)
                    {
                        Activity.Current?.SetStatus(ActivityStatusCode.Error, retryEx.Message);
                        Activity.Current?.AddEvent(new ActivityEvent("exception",
                            tags: new ActivityTagsCollection
                            {
                                { "exception.type", retryEx.GetType().FullName },
                                { "exception.message", retryEx.Message },
                            }));
                        logger.Error(retryEx, "[Diagnose] /chat/stream 重试仍失败 SessionId={SessionId}", sid);
                        await WriteSseEventAsync(writer, "error", JsonSerializer.Serialize(new { message = "抱歉，暂时无法处理您的请求，请重试。" }));
                        return;
                    }
                }

                // 消费流式 chunk，发送 token 事件
                // T15：emittedAnyToken 守卫——已发出过 token 后发生可重试异常或流结束无完整结果时，
                // 只发 error 不降级 RunChatAsync（避免重复回复，前端已收到部分文本）；
                // 未发 token 时维持降级到 RunChatAsync（无内容输出，降级无副作用）
                AgentChatResult? finalResult = null;
                var emittedAnyToken = false;
                try
                {
                    await foreach (var chunk in streamChunks.WithCancellation(ct))
                    {
                        if (!chunk.IsComplete)
                        {
                            await WriteSseEventAsync(writer, "token",
                                JsonSerializer.Serialize(new { text = chunk.TextDelta }));
                            emittedAnyToken = true;
                        }
                        else
                        {
                            finalResult = chunk.FullResult;
                        }
                    }
                }
                catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))
                {
                    // 流式过程中的可重试异常
                    logger.Warning(ex, "[Diagnose] /chat/stream 流式异常 SessionId={SessionId}", sid);
                    if (emittedAnyToken)
                    {
                        // 已发 token：只发 error，不降级 RunChatAsync（避免重复回复）
                        await WriteSseEventAsync(writer, "error", JsonSerializer.Serialize(new { message = "抱歉，暂时无法处理您的请求，请重试。" }));
                        return;
                    }
                    // 未发 token：维持降级到 RunChatAsync
                    var agent = router.GetAgent(modelId);
                    var (result, _) = await agent.RunChatAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
                    finalResult = result;
                }

                if (finalResult is null)
                {
                    // 流式未返回完整结果
                    logger.Warning("[Diagnose] /chat/stream 无完整结果 SessionId={SessionId}", sid);
                    if (emittedAnyToken)
                    {
                        // 已发 token 但无 complete chunk：只发 error，不降级 RunChatAsync
                        await WriteSseEventAsync(writer, "error", JsonSerializer.Serialize(new { message = "抱歉，暂时无法处理您的请求，请重试。" }));
                        return;
                    }
                    // 未发 token：维持降级到 RunChatAsync
                    var agent = router.GetAgent(modelId);
                    var (result, _) = await agent.RunChatAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct);
                    finalResult = result;
                }

                // 发送 done 事件（完整 ChatReply JSON）
                var userMsg = req.Message ?? "";
                var chatReply = BuildChatReply(finalResult, userMsg, prefs, catalog, req.Username, user.Id, cache, queue);
                await WriteSseEventAsync(writer, "done", JsonSerializer.Serialize(chatReply));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[Diagnose] /chat/stream 未处理异常 SessionId={SessionId}", sid);
                await WriteSseEventAsync(writer, "error", JsonSerializer.Serialize(new { message = "抱歉，暂时无法处理您的请求，请重试。" }));
            }
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
                // R8.1：Recommended=快照完整推荐列表（含枕套等非首项，BestMatch 为 Recommended[0]），
                // 不变量 BestMatch==Recommended[0] 且 Recommended ∩ Other == ∅（SplitProducts 天然满足）。
                var cached = new RecommendationResponse(
                    snapshot.Recommended?.FirstOrDefault(),
                    snapshot.Recommended ?? [],
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
                        recDtos,
                        otherDtos,
                        "根据您的兴趣，为您推荐：",
                        recDtos.Select(p => p.Category).Distinct().ToArray());
                }
                else
                {
                    // merged>0 但无商品命中（如偏好词均未命中商品）→ 兜底精选，不显示「已推荐」空列表
                    response = new RecommendationResponse(
                        null,
                        [],
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
                    [],
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

    /// <summary>
    /// 判断 Agent 调用失败是否值得应用层重试（T12，方案 A 分类器）。
    /// 网络抖动（<see cref="HttpRequestException"/>）/ 超时（<see cref="TimeoutException"/>）/
    /// HttpClient 超时（<see cref="TaskCanceledException"/> 且调用方 ct 未取消）/
    /// OpenAI·Qwen 路径的 429 与 5xx（<see cref="ClientResultException"/>）
    /// 属临时性失败 → 值得重试；其余（含 <see cref="InvalidOperationException"/>、协议/解析类错误）
    /// 为确定性失败 → 不重试。
    /// <see cref="KeyNotFoundException"/> 不落入本分类器：T13 中由更前的独立 catch 优先处理（400「不支持的模型」），不重试。
    /// </summary>
    internal static bool IsRetryableAgentFailure(Exception ex, CancellationToken ct) =>
        ex switch
        {
            HttpRequestException => true,                                    // 网络抖动
            TimeoutException => true,                                        // 超时
            TaskCanceledException when !ct.IsCancellationRequested => true,   // HttpClient 超时（调用方 ct 未取消）
            ClientResultException cre => cre.Status is 429 or >= 500,         // OpenAI/Qwen 路径 429 或 5xx
            _ => false,                                                      // 其余（含 InvalidOperationException、KeyNotFoundException）
        };

    private static ProductDto ToDto(Product p) => new(p.Id, p.Name, p.Category, p.Tags, p.Price, p.Emoji);

    // 商品 ID 标记正则（R4/R5/R9）：预编译 + 显式 timeout（满足 S6444/S6354）。
    // 不使用裸 \d+（会误删价格/数量），只匹配带 # 前缀或 商品ID 前缀的 ID 形式。
    // R5 收紧：#\d+ 原来会误删非商品 ID 的「#数字」（如「订单号 #123456」「参见 #3 条款」），
    // 商品 ID 范围是 1-18（ProductSeedData.Products 的 Id 显式 1..18）。
    // 因此 # 前缀改走「\d+ 提取 + int.TryParse 校验 1..18 才删」，范围变化时只需同步 Min/MaxProductId。
    // 商品ID 前缀则保持任意数字——前缀本身即明确的产品 ID 标记（无歧义），收紧反而会回归 R4「隐藏商品 ID 展示」的诉求。
    // R9：Agent 指令固定唯一合法格式「商品Id:N」，本主正则精确删该格式（IgnoreCase 覆盖「商品id:N」）；
    // ProductIdLabelPattern 字符类扩入「为/是」作兜底，删「商品ID为4」「商品ID是4」等变体（字段泄漏案例）。
    private static readonly Regex HashIdPattern =
        new(@"#(?<id>\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex FixedIdPattern =
        new(@"商品Id[:：]\d+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex ProductIdLabelPattern =
        new(@"商品ID[\s:：为是]*\d+", RegexOptions.None, TimeSpan.FromSeconds(1));

    // 商品 ID 合法范围（R5）：与 ProductSeedData.Products 的 Id 1..18 一致；新增/删除商品导致范围变化时同步这里。
    private const int MinProductId = 1;
    private const int MaxProductId = 18;

    /// <summary>
    /// 清洗 LLM 回复文本（R4/R5/R9）：去除「#5」「商品ID: 4」「商品ID为4」「商品Id:4」等商品 ID 展示，
    /// 避免对话历史向用户暴露商品 ID。只清洗 Reply 字符串，绝不触碰
    /// RecommendedProducts/OtherProducts 的 ProductDto.Id——前端加购依赖的结构化数据，不从文本解析。
    /// R5 收紧：# 前缀仅删 1-18 范围内的 ID（防误删「订单号 #123456」等非商品 ID 的 #数字）。
    /// R9：删除顺序先精确删固定格式「商品Id:N」（Agent 指令唯一合法格式，IgnoreCase 覆盖小写 id），
    /// 再删 # 前缀（1-18 校验），最后用 ProductIdLabelPattern 兜底删「商品ID为4」等变体。
    /// </summary>
    private static string SanitizeReply(string? reply)
    {
        var text = reply ?? "";
        // R9：先精确删固定格式「商品Id:4」「商品Id：4」（IgnoreCase 覆盖「商品id:4」）
        text = FixedIdPattern.Replace(text, "");
        // 去 #5（1-18 内商品 ID）；#123456/#20（非 1-18）保留不删
        text = HashIdPattern.Replace(text, static match =>
            IsProductId(match.Groups["id"].Value) ? "" : match.Value);
        // R9 兜底：去「商品ID: 4」「商品ID为4」「商品ID是4」等变体
        text = ProductIdLabelPattern.Replace(text, "");
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

    /// <summary>
    /// 构建 ChatReply（T21 提取复用）：关键词匹配 + 偏好合并 + 推荐结果 + 缓存写入 + 偏好入队。
    /// /api/chat 与 /api/chat/stream 共用此方法，保证推荐结果一致。
    /// </summary>
    private static ChatReply BuildChatReply(
        AgentChatResult result, string userMsg, UserPreferences? prefs,
        IProductCatalogService catalog, string username, Guid userId,
        IMemoryCache cache, IPreferenceQueue queue)
    {
        // 关键词匹配：从用户输入直接匹配（不依赖模型结构化输出）
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

        // 推荐合并（design 4.3 / T17 RecommendationMerger）：当前消息关键词优先，
        // 不足 3 个时用偏好权重 Top-N 补齐到 ≤5，按序数忽略大小写去重。
        // 偏好词来自 DB，先经白名单过滤（P2-4），避免非法/空白偏好词合并后
        // SplitProducts 返回空推荐却仍标记 HasRecommendation=true。
        var prefKeywords = FilterValidPreferenceKeywords(prefs?.KeywordsJson, catalog);
        var merged = RecommendationMerger.MergeKeywords(validKeywords, prefKeywords);

        ChatReply chatReply;
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

        // R8：聊天产物联动推荐栏 — 写用户维度推荐快照缓存（推荐以聊天产物为准）。
        // 先同步更新内存（/recommendations 立即读到最新推荐，与聊天 100% 一致），
        // 偏好异步入队落库保持现状；TTL 10min，miss 时 /recommendations 走偏好/精选兜底。
        cache.Set($"recommend_{username}",
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
            var update = new UserPreferenceUpdate(userId, result.Preferences);
            if (!queue.TryEnqueue(update))
                Log.Warning("Preference queue completed, update dropped for {UserId}", userId);
        }

        return chatReply;
    }

    /// <summary>
    /// 写 SSE 事件到流（T21）：格式为 "event: {eventName}\ndata: {data}\n\n"。
    /// </summary>
    private static async Task WriteSseEventAsync(StreamWriter writer, string eventName, string data)
    {
        await writer.WriteAsync($"event: {eventName}\ndata: {data}\n\n");
        await writer.FlushAsync();
    }
}
