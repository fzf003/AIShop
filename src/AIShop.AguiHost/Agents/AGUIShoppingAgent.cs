using AIShop.Service.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AIShop.AguiHost.Agents;

/// <summary>
/// AGUIShoppingAgent 装配（agui-host T4）：从零设计的新购物 <see cref="ChatClientAgent"/>。
/// 形态 = <c>chatClient.AsAIAgent(name: "AGUIShopping", instructions, tools)</c>，工具复用
/// <see cref="CartToolProvider.CreateTools()"/>（5 购物工具，与老 Agent 同源）。
/// 会话由 AG-UI <c>AgentSessionStore</c>（ThreadId）承载，无自管 session 字典、无 StateBag 偏好注入；
/// 不复用旧 ShoppingAssistantAgent 的 HarnessAgent 外壳与 <c>Reply + Keywords + Preferences</c> JSON 回复协议。
/// </summary>
/// <remarks>
/// AG-UI 为 preview 包（Microsoft.Agents.AI 1.20.0）：核心包 <b>没有 <c>WithTools</c></b> 扩展，
/// 挂工具经 <see cref="ChatClientExtensions.AsAIAgent"/> 的 <c>tools</c> 参数直接写入 Agent 默认
/// <see cref="ChatOptions"/>（API 面以本地镜像 preview 源码为准）。
/// </remarks>
internal static class AGUIShoppingAgent
{
    /// <summary>Agent 名称（AG-UI 会话/遥测标识）。</summary>
    internal const string AgentName = "AGUIShopping";

    /// <summary>
    /// 自然语言购物人设 instructions（面向 AG-UI 会话）：先 <c>search_product</c> 检索商品 → 命中后用工具输出中的商品
    /// 编号加购 → 查车/改量/移除。全程简体中文、工具驱动，明确不使用旧 <c>Reply/Keywords/Preferences</c> JSON 结构化回复协议。
    /// </summary>
    internal const string DefaultInstructions =
        """
        你是 AIShop 线上商城的购物助手。请始终使用简体中文回复，语气自然、简洁，直接帮用户办成事。

        你可以使用以下工具（参数与描述详见各工具说明）：
        - search_product：检索商品。用户表达想要/寻找某类商品时，先调用它搜索，再基于真实检索结果介绍，绝不凭空编造商品。
        - add_to_cart：把某件商品加入购物车（数量在现有基础上追加）。
        - update_cart_quantity：把购物车中某商品的数量设置成用户要求的最终值（如「只要 2 件」）。
        - get_cart_summary：查看当前购物车的内容与合计。
        - remove_from_cart：从购物车移除某个商品条目。

        请遵循以下工作方式：
        1. 用户提出购物/寻找请求 → 先 search_product，命中后在回复中说明商品名称、编号与价格，再按用户意图推进购买。
        2. 用户表达要买/加购某商品 → 用检索结果中的商品编号直接 add_to_cart，不要反复确认；
           已加购过的商品不要重复加购，如需调整数量改用 update_cart_quantity。
        3. 用户查看购物车 → get_cart_summary；改数量/移除 → 相应工具，不要用自然语言假装完成。
        4. 每次执行工具后给用户一句自然的文字反馈（加购成功、车内现有商品等），不要沉默，也不要用冗长解释替代行动。
        5. 用户身份与购物车由系统自动关联，无需向用户询问任何登录信息。
        """;

    /// <summary>
    /// 从 <paramref name="chatClient"/> 装配 AGUIShoppingAgent 的 <see cref="AIAgent"/>（运行时类型为
    /// <see cref="ChatClientAgent"/>）。供 AguiHost/Program.cs（T5）与宿主级测试复用；测试可注入 NSubstitute
    /// <see cref="IChatClient"/> 走离线链路。
    /// </summary>
    /// <param name="chatClient">底层对话客户端（AguiHost 装配时 = ModelRouter.GetDefaultChatClient() 的 DI 单例）。</param>
    /// <param name="cartTools">5 购物工具工厂（<see cref="CartToolProvider.CreateTools"/>，与老 Agent 同源）。</param>
    /// <param name="instructionsOverride">覆盖人设 instructions（测试注入）；null 时用 <see cref="DefaultInstructions"/>。</param>
    /// <returns>装配完成的新购物 Agent（<see cref="ChatClientAgent"/>，由 AG-UI AgentSessionStore 承载会话）。</returns>
    internal static AIAgent Create(IChatClient chatClient, CartToolProvider cartTools, string? instructionsOverride = null)
    {
        var instructions = instructionsOverride ?? DefaultInstructions;

        // AG-UI 官方宿主形态：AsAIAgent 位置签名 = (instructions, name, description, tools, ...)，
        // 故 name/instructions/tools 全部具名传递；tools 经 IList{AITool} 挂到 Agent 默认 ChatOptions。
        return chatClient.AsAIAgent(
            name: AgentName,
            instructions: instructions,
            tools: cartTools.CreateTools().ToList());
    }
}
