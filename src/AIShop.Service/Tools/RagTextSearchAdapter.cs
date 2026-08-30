using AIShop.Core.Interfaces;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIShop.Service.Tools;

/// <summary>
/// TextSearchProvider 适配器（design §5.7，Task 11，AK-1/AK-4）。
/// 把 <see cref="IRagSearchService.SearchKnowledgeAsync"/> 适配为 MAF <see cref="TextSearchProvider"/>
/// 的检索委托，产出挂在 <c>ShoppingAssistantAgent.AIContextProviders</c> 上的 <c>search_knowledge</c> 工具。
///
/// 为什么用 TextSearchProvider 承载而非手写 AIFunction：
/// ① AK-1——检索工具注册与调用链路完全复用 MAF 内建机制（Agent 侧零新机制）；
/// ② AK-4——<c>OnDemandFunctionCalling</c> 即 Tool 模式，检索能力全部经工具暴露，
///    不向 Instructions 注入任何静态指令/知识数据（知识文档只在该工具被调用时动态检索注入）。
/// </summary>
public static class RagTextSearchAdapter
{
    /// <summary>
    /// 构造 <c>search_knowledge</c> 工具承载用的 <see cref="TextSearchProvider"/>（design §5.7.3 推荐配置）。
    /// </summary>
    /// <param name="ragSearchService">混合检索服务（Core 接口，经 AddRag 注册）。</param>
    public static TextSearchProvider CreateTextSearchProvider(IRagSearchService ragSearchService)
    {
        var options = new TextSearchProviderOptions
        {
            // OnDemandFunctionCalling = 作为 Tool 由模型按需调用（AK-4「检索全部走 Tool」），
            // 不做 BeforeAIInvoke 自动注入——避免每轮对话都无谓检索、上下文 token 膨胀（§5.7.2）
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolName = "search_knowledge",
            FunctionToolDescription = "搜索商品知识/描述文档。用户问商品特点、说明、参数类问题时调用。",
            // 内置格式化器把检索结果注入上下文时的前后提示语（§5.7.3）；
            // 注意：提供 ContextFormatter 后这两个提示语会被忽略（二者互斥），首期用内置格式化器 + 中文提示语
            ContextPrompt = "【参考资料】根据以下知识回答用户问题：",
            CitationsPrompt = "如引用了参考资料，请注明来源文档。",
            // 会话状态在 StateBag 的键；未来多 TextSearchProvider 实例（多知识源）必须用不同键，
            // 避免会话状态互相覆盖（§5.7.4）
            StateKey = "RagProductSearch",
            // 遥测不记录检索内容（用户查询与检索结果属敏感数据）
            EnableSensitiveTelemetryData = false,
        };

        return new TextSearchProvider(
            // 检索委托：POC 实测（handoff-1 R11）注入工具的参数名固定为 userQuestion，
            // MAF 会把模型生成的 userQuestion 透传给本委托（写 query 会静默不触发工具调用）
            async (userQuestion, ct) =>
            {
                // 检索商品知识文档；top=3 约束注入上下文的条数（§5.7.2：token 增长主要靠 top 约束）。
                // domain 传 null：首期 collection 仅含 product 领域记录，检索范围天然限定该领域；
                // 未来多领域落地时按工具语义显式传 domain 即可（接口已预留）
                var hits = await ragSearchService.SearchKnowledgeAsync(userQuestion, top: 3, ct: ct);
                return hits.Select(h => new TextSearchProvider.TextSearchResult
                {
                    // SourceName 作为来源文档标题；Text 含标题/类别/描述，供内置格式化器注入上下文
                    SourceName = h.Title,
                    Text = $"{h.Title}（{h.Category}）：{h.Text}",
                });
            },
            options,
            NullLoggerFactory.Instance);
    }
}
