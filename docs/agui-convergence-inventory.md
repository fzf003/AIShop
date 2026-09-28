# AG-UI 三变更收敛盘点（需求对齐版）

> 2026-09-20 生成。**对齐基准 = 用户原话**，不是 spec：
> 「**根据聊天内容实时推荐产品显示在推荐产品栏中**」＋「**任何模型都要能用**」。
>
> 本轮盘点方式：3 个只读 agent 各负责一个变更，**独立读源码/规范/测试**，
> 不采信 test-report 的结论节（只把它的「覆盖缺口」当作待验证输入，逐条自己动手复核）。
> 凡与报告结论不一致处，均在第五节标注「报告已过时」。
>
> HEAD = `be63394`。三变更 `tasks.md` 分别 184/162/138 个 `[x]`，**0 个未勾**；**三个都未归档**。

---

## 〇、先说结论

| 需求 | 结论 | 卡在哪 |
|---|---|---|
| 「…**显示在推荐产品栏**中」 | **达成** | 面板本体完整，DeepSeek 下真机 `.rcard=6` |
| 「根据**聊天内容**」 | **部分达成** | 实际是**23 组关键词子串匹配**，语义化意图不触发（L8） |
| 「**实时**」 | **达成（有前提）** | 前提是「本轮流能正常走完」——而流断时它恰好在最需要兜底的场景失效（L1） |
| 「**任何模型都要能用**」 | **未达成** | 默认模型 Mimo 下三项现象全中；被 `design.md:38` 写成「不在范围」→ **无人接** |

**一句话**：三份报告判 PASS 全部建立在「spec 覆盖」上；拿**需求**对，PASS 不成立。

---

## 一、逻辑不完整的（根因层，最重要）

### 高严重度

| # | 缺陷 | 证据 | 触发条件 | 影响 |
|---|---|---|---|---|
| **L1** | **推送挂在 `await foreach` 之后，任何流内异常 → 本轮无推荐 + 无终止事件**（**设计洞，非取舍**） | `RecommendationPushAgent.cs:84-120`（迭代器尾部推送，`await foreach` **无 try/catch**）；宿主失败兜底（发 `RunErrorEvent`）写在 `#if !NET10_0_OR_GREATER` 里，net10.0 走 `TypedResults.ServerSentEvents` → **不发终止事件**（安装包 DLL 内 `RunErrorEvent`/`StreamingError` 字符串命中数 = **0**）；`SaveSessionAfterStreamingAsync` 同为迭代器 → 该轮**会话快照也不落库** | 模型/网关在轮内抛异常。Mimo 的机制：MEAI `MaximumConsecutiveErrorsPerRequest` 默认 **3** × `AGUIShoppingAgent.MaximumToolIterations` = **3** —— **两个 3 撞在一起**，第 3 次仍失败即 rethrow | 本轮推荐**静默丢失**（首轮则永远占位）+ 客户端无终止信号 + 该轮快照不落库。`spec R4` 写的「MUST NOT 使流异常断开」只做到了「推荐计算失败不炸流」，**做不到「本轮本身失败时仍给出确定收尾」** |
| **L2** | **客户端「运行中」状态没有出口** | `ChatPanel.tsx:167`（`disabled={isRunning}`）；`agent.ts` 全文无 timeout / abort / watchdog；`endSession` 只断 store 订阅、**不 abort 在途请求**；服务端也无心跳/超时兜底 | 服务端**既不写终止帧、也不关连接、也不报错**（挂起） | 发送按钮**永久禁用，只能刷新页面**。SDK 在「正常收尾」与「报错」两种情况下都能自愈，**唯一不能自愈的就是挂起** |
| **L3** | **未挂标记的 POST 会静默写进 `steve` 的购物车，而现有测试全绿** | `AguiUsernameForwarder.cs:143-150`（AG-UI 分支对 **POST 无条件** `SetCurrentUser(username ?? DefaultUsername)`）+ `AguiCartEndpoints.cs:122-127`（仅判 `CurrentUser` 非空即放行）；违反 `spec.md:607`「MUST NOT 退化为按缺省用户处理」与 `design.md:671` 错误契约表；**反证只覆盖 GET**（`handoff-T13.md` 遗留 4 自述「非 POST 直接放行」） | `/cart` 组丢失 `AguiClientRestEndpoint` 标记，或任何未挂标记的写端点被映射（`MapGroup`/`WithMetadata` 被重构掉即命中） | 跨用户数据污染（商品写进 `steve` 的车）+ **返回 200 假成功**，静默无告警，**无任何测试会红** |
| **L4** | **一轮坏掉会毒化后续所有轮** | 客户端每轮**全量重发** `agent.messages`（`agent.ts:16-18`、`:294-299`）+ 持久化是**整体重写**；若流断在 `TOOL_CALL_START` 之后、`TOOL_CALL_RESULT` 之前，会持久化「assistant 带 toolCalls、无配对 tool 消息」的残缺历史。仓库自己命名此现象为「**历史毒化导致 500**」（`design.md:38`、`tasks.md:23` 原文） | 首轮中断于工具调用中间 | 后续轮次可能持续 400/500 —— **一次坏轮污染整个会话** |

### 中严重度

| # | 缺陷 | 证据 | 触发条件 | 影响 |
|---|---|---|---|---|
| **L5** | **跨账户污染** | `agent.ts:237`（`agent.subscribe({…})` 返回值**被丢弃**，无 dispose）；`store.ts:85-90`（`endSession` 只退订 store 侧）；`reco.ts:101-103`（`onCustomEvent` 直写**模块级全局** store）；`App.tsx:314`（`resetRecoContent()` 在 `closeToAccount` 里先执行） | 一轮在途（实测单轮 6–11s）时退出登录 / 切账户 | ① 新账户面板被**上一账户**的 CUSTOM 负载顶掉；② 随后本账户一次 store 通知会把旧负载**写进新账户的 localStorage** → 违反 spec「退出只清当前账户」 |
| **L6** | **前端三源有覆盖竞态，与 spec「后到者胜」相反** | `agent.ts:258-261`（CUSTOM **到达即同步写** store）vs `App.tsx:281-286`（工具结果要等 React 重渲染后 effect 才写）；store 是**字符串单值**（`reco.ts:43-46`） | React 批次合并/并发渲染让 effect 晚于 CUSTOM 到达；或两源**文本**不等（转义形态不同、模型多次调工具、最后一次 query 非法导致回退用户消息） | 后执行的 `setRecoFromToolResult(更早的内容)` 会**覆盖** CUSTOM。当前看不出来只因两源负载通常同构（`S6` 用 `JsonElement.DeepEquals` 证明语义相等）；`F6 R9-1` 用 `waitFor` 冲洗，**掩盖了时序** |
| **L7** | **门控判据同时存在漏推与误推** | `RecommendationToolProvider.cs:93/106-107`（`ShouldPush` 只看**本轮**关键词）+ `:120-127`（payload 用**合并**关键词，含偏好）——**两个口径用的是不同关键词集** | **漏推**：有记忆偏好的用户说「你好呀」→ payload 有 6 条，门控不通过 → 丢弃。**误推**：展开词表含「书/音乐/健康/运动/礼物/自然/早晨」等日常词，用户说「我最近在健身，**不过今天不买**」→ 命中 → 推送并顶掉上次内容 | 「闲聊轮保持上一次」在**被非购物语境命中**的句子上不成立（spec R2 只拦「未命中关键词」，没拦这个）。**额外浪费**：`TryBuildPushPayloadAsync` **先算完负载再判门控**，即每轮都跑一遍偏好读取 + 6 条投影 + reason 派生，然后大概率丢弃 |
| **L8** | **「根据聊天内容」实际是 23 组关键词子串匹配** | `ProductKeywordMap.cs`（23 组）；`MatchKeywords` → `MatchedKeys`（`RecommendationToolProvider.cs:223-230`，`ContainsIgnoreCase` 或展开词命中）；`LastUserText`（`RecommendationPushAgent.cs:136-137`，**只看本轮最后一条 user 消息**） | 用户说「**帮我挑一个**」「**有什么好东西推荐吗**」——推荐意图明确但**无白名单词** | 面板不动。需求原文「根据**聊天内容**实时推荐」退化成「命中 23 组词表才推荐」。**规格压根没覆盖这条**，也无变更承接 |
| **L9** | **CORS 校验与自身文案不符，静默失效的口子没堵** | `AguiCors.cs:66-79` 只校验「绝对地址 + scheme ∈ {http,https}」，异常文案（`:77-78`）却宣称「必须是含协议与端口、**无末尾斜杠**的绝对地址」——校验并不查末尾斜杠与路径 | 配 `Cors:AllowedOrigins:0 = "http://localhost:5173/"` | 启动通过、运行期**永不匹配**（浏览器 `Origin` 头不带尾斜杠）。而这恰恰是该校验存在的唯一理由（注释 `:59-64` 自陈「静默失效比启动即抛更难排查」） |
| **L10** | **关键词 Top-5 按 UTF-16 码点序截断：确定性达成，相关性未定义** | `RecommendationToolProvider.cs:236-240`（`Distinct(Ordinal).OrderBy(Ordinal).Take(5)`） | 单轮命中 >5 个白名单关键词 | 参与推荐的是「码点最小」的 5 个而非最相关的 5 个，**中文下近似随机丢弃**；注释自述目的只是「输出确定性」，未评估相关性损失 |
| **L11** | **REST 写端点无幂等约定，而加购是累加语义** | `AguiCartEndpoints.cs:138` + `CartEntities.cs:38`（`existing.Quantity += quantity`） | UI 双击 / HTTP 重试 / 网络重发 `POST /cart/items` | **数量翻倍**且返回 200 看起来正常；`design.md:774` 只要求前端「无推送时自行重新 GET /cart」，**未约定幂等键或重复提交防护** |
| **L12** | **CORS 命名策略全站生效，覆盖面超出 spec 声明** | `Program.cs:151-152`（`app.UseCors(PolicyName)`）+ `AguiCors.cs:42-45`（`AllowAnyMethod().AllowAnyHeader()`）；机制证据 `AguiCorsTests.cs:105-125`（`OPTIONS /` 被中间件 204 应答，与端点元数据无关） | 配置了白名单的部署 | 白名单 origin 同时获得 `/devui`、`/v1/entities`、`/v1/responses`、`/v1/conversations`、`/health` 的跨源放行与任意方法/头——spec（`spec.md:217`）只声明覆盖 4 个端点。契约外暴露面 |
| **L13** | **`?username=` 是存在性校验，不是任何形式的认证** | `AguiUsernameForwarder.cs:31-38`（身份由请求自称）+ `:219-232`（只查在不在表里）；`AguiCartEndpoints.cs:40-43` | `curl "http://<host>/cart?username=marla"`（读）、`-X DELETE`（清空）、`POST /cart/items`（加购）——**无需任何凭证**，三个用户名是公开种子数据 | 横向越权。这是**设计接受项**，但 spec 只把它描述成「客户端写错用户名」的**数据质量/UX 问题**（`spec.md:185`），**没有声明真实暴露面是「任何能连到该端口的第三方可改任意用户的购物车」**。注：老链 `api/cart/{username}` 同语义，**不是本变更新引入的漏洞类别**，但本变更把它第一次搬到 AguiHost 并**新增了 5 个写端点** |

### 低严重度（登记，不阻塞）

| # | 缺陷 | 证据 |
|---|---|---|
| **L14** | **契约与实际不符（跨变更语义断层）**：`agui-client` 文档写 CUSTOM「本期不消费」，代码却把它当**主来源** | 文档 `design.md:134`、`spec.md:35`、`spec.md:395-399`（R9 写死「数据来源 MUST 为 `recommend_products` 工具结果」）、`tasks.md:71/279` vs 代码 `RecoPanel.tsx:4`（「主来源 = CUSTOM；工具结果为**兼源**」）。**R9 的验收口径与实际实现互相矛盾，评审结论可被任意一方支撑** |
| **L15** | 面板副标题写「**每轮自动更新**」，但推送是**门控**的 | `RecoPanel.tsx:115-117`（Z2 改成的文案）vs spec R2「闲聊轮保持上一次」 |
| **L16** | 端口契约不一致且未提交 | `vite.config.ts:13`（`AGUI_HOST='localhost:64322'`，**未提交**）vs 同文件 `:8-12` 注释仍写 5299；`launchSettings.json` 实际是 `64321/64322`；`tasks.md`/`test-report` 全按 5299 记录 |
| **L17** | `ShouldPush` 无 null 守卫（`RecommendationToolProvider.cs:106-107` 直接解引用 `payload`）；装饰器无构造参数守卫（`RecommendationPushAgent.cs:57-61`） | 当前不可达（生产经 `GetRequiredService`、唯一调用点有前置守卫），但都是 `internal`，下一个人调用即 NRE |
| **L18** | JSON 多态注册是**宿主级**副作用（对全宿主 HTTP JSON 生效） | `AguiRecommendationStreamOptions.cs:70-93`（`Configure<HttpJsonOptions>` 全局加 `JsonDerivedType`）。当前无 REST 端点序列化 `AIContent`，故无副作用，`design.md:197-210` 已披露 |
| **L19** | 同一条 builder 的两个入口**取消语义不对称** | `RecommendationToolProvider.cs:70`（工具入口传 `CancellationToken.None`）vs `:88-96`（推送入口透传 `ct`）→ 客户端断开后，工具路径的偏好全表枚举仍跑完 |
| **L20** | 同一域并存两套购物车 API，契约有意不同 | 老链 `{"error":"用户不存在"}` + 路径参数 vs 新面 `{"detail":"User not found"}` + 查询参数；两库不共享 |
| **L21** | 端点防御分支与中间件「文案同源」声明不实 | `AguiCartEndpoints.cs:98/104` 用常量 vs `AguiUsernameForwarder.cs:229` 用硬编码字面量 `"User not found"`；中间件是**唯一**产出 404 的路径，而常量持有方是端点——「同一失败语义只有一个文案来源」被**反向实现** |
| **L22** | **推荐面板未跟上用户意图**（用户否定某品类或用词表外的说法表达偏好时，面板仍推会话关键词命中的东西） | 依据只取本轮用户消息（`RecommendationPushAgent` 的 `LastUserText`）；`ProductKeywordMap` 的 23 组里**没有「音箱」**而商品目录里有（`AguiProductSeedData` Id 24）；语义检索只作兜底、不参与排序。**已评估：当前约束下不做**（**用户 2026-09-28 追认：接受「不做」**），详见下方 L22 详述 |

---

### L22 详述：为什么做不到，以及结论

**现象**（真机，Marla）：用户在「我想早上吃早餐的时侯听点音乐」之后纠正「**要公放的，不想戴耳机**」——助手正确理解为音箱并加购，但**推荐面板仍是耳机打头、音箱垫底**。

**根因链**（逐环**实测**，非推断）：

| 环节 | 实测结果 |
|---|---|
| 模型理解用户意图 | ✅ **完全正确**（选了音箱、调 `add_to_cart(productId:24)`） |
| 模型主动表达意图 | ⚠️ 默认 prompt 下**不会**；调整 prompt 后可让它调 `recommend_products` 并传出 `"想要吃早餐时外放听音乐的音箱，不要耳机"` —— **含否定、指向明确，质量很高** |
| 词表识别该意图中的品类 | ❌ **`ProductKeywordMap` 里没有「音箱」**（商品目录里却有） |
| 最终结果 | 该 query 里的「音箱」「外放」**等于不存在**，音箱只能靠「音乐」的**展开词顺带**入选 → 排位按目录顺序 → **垫底** |

**关键结论：堵点是「词表不认识商品目录里真实存在的品类名」，不是模型能力、也不是语义检索能力。**

**已排除的方案**（都实测过）：

| 方案 | 实测结果 |
|---|---|
| 只改推荐依据（把模型回复并入 query） | **零效果** —— 关键词路径优先、语义兜底被跳过；六条场景里三条输出**逐字不变** |
| 加局部否定词表 | 属**穷举**，换种说法即失效（与既有 `NegationCues` 同病，本文档已自认「已知漏拦」） |
| 按语义分数重排 | 对**否定**无效（embedding 天然不理解否定），且会改动推荐排序口径 |
| 补词表 | **有效，但属禁区**（`src/AIShop.Core/StaticData/ProductKeywordMap.cs`） |

**为什么不做**：

1. **堵点唯一且落在禁区** —— 词表在 `src/AIShop.Core/`（用户划定的禁改区）；
2. **单靠补词表治不了根** —— 商品品类有限，但**用户的口语说法无限**；真正通用的是「让模型把口语翻译成品类词」（prompt 那条，已验有效）。**两者必须配套**；
3. 不配套时任何单边改动都是**零效果**（已实测）。

**解除条件**（满足任一条即可重启）：

- **放开 `ProductKeywordMap.cs`**（Core 的静态数据）→ 做「强化 prompt + 补品类词」，**对任何产品通用**，且**不需要改动推荐排序口径**；
- 或将来引入**允许额外 LLM 调用**的架构变更 → 可直接用 LLM 抽取意图（含否定），一步到位。

**可复用资产**（将来重启时直接用）：

- **强化 prompt 能让模型主动表达意图（含否定）**，实测 query 质量很高 —— 改动位置 `AGUIShoppingAgent.cs` 的 system prompt 第 6 条；
- **诊断手法**：临时探针直接打 `TryBuildPushPayloadAsync`，用**场景集**对照（本次 6 条：原句 / 换品类 / 换否定 / 加购后再问 / 弱依据 / 闲聊），**并与改动前基线并排对比**。

**裁决（matt-workflow Step 7.5 要求的「谁裁决、何时」）**

| 项 | 值 |
|---|---|
| 裁决人 | **用户（编排方）** |
| 裁决时间 | **2026-09-28** |
| 裁决 | **接受「不做」** |
| 依据 | 上述详述：堵点唯一且落在禁区 `src/AIShop.Core/StaticData/ProductKeywordMap.cs`；单边改动已实测零效果；**两者必须配套**，不配套时任何单边改动都无效 |

**影响面与知情接受**：仅在「用户用**词表外说法**或**带否定**表达偏好」时出现；日常说法（如「我想买跑步鞋」）不受影响（2026-09-28 B2-e 真机复核正常）。**助手自身理解与加购均正确**，属推荐面板「不跟手」的体验瑕疵，非功能错误。

**复活条件**（满足任一条即可重启，届时作**新变更**走 matt-workflow，不在本变更内夹带）：放开 `ProductKeywordMap.cs`；或引入允许额外 LLM 调用的架构。

---

## 二、没测试过的

### A. **空转断言**（最严重——测了等于没测）

| # | 描述 | 证据 | 后果 |
|---|---|---|---|
| **T1** | 「切模型：在途本轮不受影响」那条**断言的是测试自己构造的对象** | `ModelPicker.test.tsx:233-244`：<br>`const inFlightRound = { username: currentUsername(), model: currentModelId() }`<br>`expect(inFlightRound.model).toBe('gpt-4.1')` —— **不经过任何产品代码，恒真**。`agent.test.ts:250` 名字虽同，正文是**串行**两轮（第一轮 `await` 完才 `setModel`）。`handoff-C6.md` 遗留 2 明确要求「把构造落在真实调用点并补端到端断言」——**未落实** | 将来若改成「轮开始时快照 model 跨轮复用」或「切模型重建 HttpAgent」，**R6-5 会被静默破坏而全绿** |

### B. 高风险零覆盖（正对着真机事故的形态）

| # | 描述 | 证据 | 风险 |
|---|---|---|---|
| **T2** | **「流在 `RUN_FINISHED` 之前被截断」零断言**（客户端 + 服务端两边都零） | 客户端：`App.test.tsx:168-198`（R7-2）在 `stream.close()` 前**必定**先 `runFinished(...)`；`CartDrawer.test.tsx:335` 的「只开头不结束」流由用例主动放行收尾；`run-failure.test.ts` 只覆盖 HTTP 级 404/5xx。服务端：`RecommendationPushAgentTests.cs:196-238` 与 `AguiRecommendationPushTests.cs:372-412` 两条「异常降级」用例注入点都是 `ThrowingMemoryCache`（异常发生在**推送计算**、即装饰器 `try` 之内），而 `await foreach`（`RecommendationPushAgent.cs:84-99`）**没有任何用例让它抛** | **正是真机 Mimo 事故的形态**：`isRunning` 不复位 → 按钮永久禁用 → **整屏卡死，测试永远绿** |
| **T3** | **「工具调用失败」整条链路零覆盖** | `MockToolChatClient.cs:14-20`（脚本只有 `ToolName + 强类型 Arguments 字典 + FinalText`，`Decide()` L110-124 **恒产合法** `FunctionCallContent`）；`RecommendationPushAgentTests.cs:302-303` 辅助方法恒产合法 `query`；全 `tests/` 无一条让工具抛错或让 `arguments` 为 `null`/字符串 `"null"` | **Mimo 的失败形态（`arguments` = 字符串 `"null"`）在 608 个 .NET 用例里没有任何对应物**。「5 次坏 1 次」这种偶发在 CI 上**永远抓不到** |
| **T4** | **「内层迭代器抛异常」（= 流没走到末尾）服务端零用例** | 同 T2 服务端部分；亦无一条用例断言「流中断时服务端是否发终止事件」 | 本变更**最核心的失效路径**（L1）完全在覆盖之外 |
| **T5** | **未挂标记的 POST 路径无用例，且其真实行为与设计相反** | `AguiUsernameForwarderTests.cs:199-200` 只有 `UnmarkedGetRequest_PassesThroughWithoutInjectionOrLookup`（**GET**）；`AguiCartEndpointTests.cs:497-515` 的静态源码断言只查仓储、**不查 `.WithMetadata(...)` 是否存在** | 见 L3：静默写进 `steve` 购物车，**无任何测试会红** |

### C. 中风险缺口（报告已登记，经本次复核**仍然缺**）

| # | 描述 | 复核证据 |
|---|---|---|
| **T6** | `GET /models` 带 `forwardedProps.username` / `model` → 返回不变（R1 正文强于场景的残余） | `AguiModelsEndpointTests.cs` 全文仅 4 用例（`:52/81/101/119`），**无任何带请求体的 `/models` 请求** |
| **T7** | CORS 对新端点 `/products`、`/cart` 的覆盖 | `AguiCorsTests.cs` 只打 `GET /models`（`:52/75/95`）与 `OPTIONS /`（`:113`）；换端点级 `[EnableCors]`/`RequireCors` 时新端点会**静默失去放行头** |
| **T8** | REST 加购 ↔ AI 工具端到端 —— **已提交状态仍缺，只暂存区有** | `git show HEAD:...AguiE2ETests.cs \| grep -c RestAddToCart` = **0**；`git diff --cached --stat` = `+57` |
| **T9** | `recommend_products` × 工具迭代上限交互 | `AguiToolLoopGuardTests.cs:27-32` 的 `CreateCartTools()` **未传** `recommendationTools`；`:79` 只断言 `MaximumIterationsPerRequest == 3` |
| **T10** | S4 的 `OperationCanceledException` 分支无专门用例 | `RecommendationPushAgentTests.cs` 共 8 个 `[Fact]`，`grep "OperationCanceled\|Cancel"` 在该文件与 `RecommendationPushPayloadTests.cs` **零命中**。该 `when` 子句是本类**唯一的异常语义分叉**，去掉它则客户端断连时取消被吞掉（流不终止但连接已断），**所有用例仍全绿** |
| **T11** | 切账户 / 退出时「在途旧流」的副作用零断言 | 无用例构造「run 在途 → 退出登录 / 换账户」；见 L5 |
| **T12** | 「同轮两源」只在**语义**层断言，「**逐字节不同**时面板最终显示哪一份」未测 | `AguiRecommendationPushTests.cs:171-175` 注释承认转义形态不同；store 是字符串单值，`App.tsx:283` 按字符串相等判变化 |
| **T13** | 配置可用性无自动化验证（Endpoint/Model 配对、`ActiveModel` 真能跑） | `AppSettingsModelParityTests` 只比两文件键集合与 `Model` 值一致；`AguiModelsEndpointTests.cs:72` 是**硬编码**期望 → 改配置只需同步该行即绿。T10 式误配与 `be63394` 式改动**只能靠人工真机实测发现** |
| **T14** | 面板文案约束（「不得表述为登录校验/认证」「不要输出 JSON」）无断言 | 全 `tests/` grep 无「不要输出 JSON」「不要输出商品编号」（该文案只在 `RecommendationToolProvider.cs:159`）；「严禁表述为登录校验」在 `AguiUsernameValidationTests.cs:210` **只是注释** |
| **T15** | `IsExistingUserAsync` 404 文案硬编码 | `AguiUsernameForwarder.cs:229` 仍是字面量；由 `AguiCartEndpointTests.cs:520` 逐字节用例兜住 |

### D. 低风险 / 已披露

| # | 描述 |
|---|---|
| T16 | 真机刷新恢复无 tester 复验（jsdom 的 `unmount()` 代跑）——`localStorage` 真机的分区/隐私模式行为未验 |
| T17 | `RunCoreAsync`（非流式）路径无用例——将来 DevUI/OpenAI wire 出现非流式消费者，推荐会**静默缺失** |
| T18 | `reco.ts` store 允许任意文本（`'null'`/`'42'`），而 `readReco` 要求合法 JSON → 一次无害告警 + 刷新回占位 |
| T19 | `agui.reco` 写入次数与 `messages` 相等，只在「快照非空」区间成立（`if (reco !== null)`），无「从零开始」用例 |
| T20 | 「两条 `recommend_products` 结果、后者非法时保留前者」无用例 |
| T21 | 空清单分支（`GET /models` 返回 `[]`）无用例 |
| T22 | 验收标准 3（手动走查）无自动化形式，且报告里的「满足」来自**外部走查 agent**、tester 未独立复跑 |

---

## 三、没做完的

| # | 项 | 证据 | 归属 |
|---|---|---|---|
| **N1** | **Mimo 可用性（需求「任何模型都要能用」的核心）零改动** —— 且 **`design.md:21` 把「换模型（Mimo）时工具调用直接坏掉，面板同样空」列为本变更的动机之一**，而 `design.md:38` 又把它写成非目标 → **动机与范围自相矛盾** | `design.md:38`、`tasks.md:23`、`test-report.md:398` | **本变更**（`docs/agui-closeout-state.md:52` 已裁定「那句是错的」） |
| **N2** | **已拍板的「防历史毒化兜底」（把非法 `arguments` 规范成 `{}`）未实现** | `docs/agui-closeout-state.md:52`（A+C+防毒化，已拍板）；全仓 grep `Arguments is string` / 规范化 → **零命中**；`RecommendationPushAgent.ReadQuery` 对非法实参只「视为无效 → 回退用户消息」（`:127-133`），**不修正、不回报** | 本变更 |
| **N3** | **泄漏的 `<tool_call>` 模板清洗（方案 C）未实现**，且**已有清洗器恰好不管这类文本** | `ReplySanitizer.cs` 的四条正则全部只针对商品编号（`#N` / `商品Id:` / `价格标点`）；grep `tool_call` / `<function` 在 `ReplySanitizer.cs` 与 `ReplySanitizingChatClient.cs` → **零命中**。**清洗点现成，改动面极小** | 本变更 |
| **N4** | **项目里已有的工具调用修复中间件没挂到 AG-UI 路径** | `QwenToolCallFixClient.cs` 存在且**专门重建 `FunctionCallContent`（含「参数解析失败」容错，L80-95）**，但 `AguiModelClientFactory.cs:19` 注明「按需提炼属后续（MVP 不并入）」；`Program.cs:111-124` 的 keyed factory 未挂 | 本变更 |
| **N5** | **变更自身的文档未回灌/未收口** | `design.md:334`（§9 第 4 条「保持待实施期验证」，S1 探针 `332f334` 已实证可用）、`:318`（R1「未反编译确认」）、`:324/330`（R7 / 面板文案仍写「待确认」，Z2 `17dcaaa` 已改）、`:329`（门控「待实施期确认」，实际已落地）、`tasks.md:563` | 本变更 |
| **N6** | **注释层陈迹** | `handoff-Z3.md:65`（`RecoPanel.test.tsx` 文件头仍转述**已被 MODIFIED 的** R9 标题）、`handoff-Z2.md:31-32`（`App.tsx:105/216` 注释仍写「最近一次 `recommend_products` 的工具结果原文」） | 本变更 |
| **N7** | **真机浏览器走查从未跑在默认模型上** | `test-report.md:213`（走查用 **DeepSeek**）、`:417`（走查为外部 agent 执行）——「默认配置」这条**最关键的路径从无走查** | 收口流程 |
| **N8** | **spec delta 从未同步到 `openspec/specs/`；三变更均未归档** | `openspec/specs/` 下无对应 domain | 收尾动作 |
| **N9** | **`be63394` 的模型实验从未进入验证/文档** —— 我本次已实测 `kimi-k3` **HTTP 200 可用**（`qwen3.8-flash` 反而 **403 免费额度耗尽**），故**无运行时风险**；但 `spec.md:273/709`、`test-report.md:403` 仍写 `qwen3.8-flash`，对 HEAD 已为假；且 UI 卡片标题写「Qwen 3.7」、副标题写 `kimi-k3`，**自相矛盾** | `spec.md:273`、`:709`、`test-report.md:403`；`appsettings.json:11-12` | 无人接（规范未随改动更新） |
| **N10** | 工作区残留：`AguiE2ETests.cs`（G1，+57，**未提交**）、`vite.config.ts`（**未提交**）、仓库根 `obj/` 6 项反证备份 + `.s5_start.txt` | `git status --porcelain` | 跨变更 |

---

## 四、需求对齐总账

| 需求 | 变更 | 结论 | 证据 |
|---|---|---|---|
| 服务端能按聊天内容产出推荐 | support | **达成** | `RecommendationToolProvider` 全文件 + 测试 |
| 面板显示推荐 | client | **达成** | `RecoPanel.tsx`、真机 `.rcard=6`、F6 关系性断言 |
| 每轮实时更新、不依赖模型调工具 | reco | **达成**（前提：流能走完） | 真机帧序 `…CONTENT×20 → CUSTOM(44) → TEXT_MESSAGE_END(45) → RUN_FINISHED(46)`，**该轮模型只调了 `search_product` 未调 `recommend_products`** |
| 闲聊轮保持上一次 | reco | **达成（窄口径）** | `S2`、`S6`、`F6 R2-1`、走查 §C-2 |
| 刷新恢复 / 退出清理 | client | **达成** | `F5` 5 例 + 走查 §C-3/§C-5 |
| 「根据**聊天内容**」 | reco + support | **部分达成** | 只有「本轮文本子串命中 23 组词表」或「工具 query 命中」才算「内容」（L8） |
| **「任何模型都要能用」** | — | **未达成** | 默认模型 Mimo（`isDefault=true`）下三项现象全中；`design.md:38` 写成非目标 → **无变更认领** |

### 关于「任何模型都要能用」是**结构性**未达成的三条理由

1. **范围上就没打算做**：`design.md:38` / `tasks.md:23` 明确排除；但 `design.md:21` 又把它写成本变更的动机。**需求被范围声明挡在门外。**
2. **替代机制对「模型不听话」同样脆弱**：把依赖从「模型是否调工具」换成「**本轮流是否正常走完**」（`RecommendationPushAgent.cs:84-120`），而**模型/网关异常正是打断流的最常见原因**（L1）。
3. **已拍板的三条修法一条都没做**（N1/N2/N3），项目里现成的修复中间件也没挂（N4）。

---

## 五、报告已过时（下一轮评审**不要照抄**）

| 报告原话 | 实况 | 证据 |
|---|---|---|
| reco `test-report.md:360/395`「R1『位于文本结束后』**无自动化断言**，建议 @spec-writer 澄清」 | **已有断言**，且 spec 措辞已澄清 | `AguiRecommendationPushTests.cs:148-162`（`contentIndices[^1] < customIndex` + 正锚点）；`spec.md:31-33` 改成「位于本轮全部文本内容帧之后」；commit `3e85de3` |
| reco `test-report.md:394`「面板静态文案**未裁决**」 | **已闭环** | `RecoPanel.tsx:41/115` + commit `17dcaaa` |
| reco `test-report.md:401`「`obj/` 等临时产物待清理」 | 仍在 | `git status` |
| client `test-report.md:241`「spec.md 幂等措辞**未改**」 | **已改** | `spec.md:437` 现为「该操作**非幂等**…MUST NOT 链式或重复应用」 |
| client `test-report.md:242`「`tasks.md:428` checkbox **未翻**」 | **已翻** | `tasks.md:428` = `[x]` |
| support `test-report.md:403`「两文件 `git diff HEAD` 为空」 | 对 HEAD 已为假 | `be63394` 改了 `appsettings.json` ×2 + `AguiModelsEndpointTests.cs` |
| reco `handoff-S1.md` 门禁计数 565 | 按算术应为 576（差 11 = 并行 S2 的量） | 并行期工作区态快照 |

---

## 六、修复顺序建议（按「代价/收益」）

### 第一优先（同时是需求「任何模型都要能用」的必要条件）

1. **给「本轮」补确定收尾**（L1 + L2）
   把内层异常的传播路径与「推送」解耦；**无论成功失败，本轮都必须有一个终止事件**（或让宿主侧有一条 SSE 兜底）。
   —— **注意**：不能无脑 `catch` 吞掉内层异常（会掩盖模型故障），正解是**给本轮补确定收尾**，或把推送从「流末」改为「**流内旁路 + 独立通道**」。
2. **防历史毒化 + 非法 `arguments` 规范化**（N2 + L4）—— 已拍板、未实施，是「一次坏轮污染后续所有轮」的**唯一闸门**。
3. **挂上现成的 `QwenToolCallFixClient`**（N4）+ **给 `ReplySanitizer` 加 `<tool_call>` 规则**（N3）—— 清洗点现成，改动面极小。

### 第二优先（正确性 / 安全）

4. **修 L3**（未挂标记的 POST 静默写进 `steve`）+ 补 T5 用例。
5. **修 L5**（跨账户污染）：接住 `agent.subscribe` 的返回值并在 `endSession` dispose / abort。
6. **修 L9**（CORS 尾斜杠校验）—— 一行校验，堵住「静默失效」。

### 第三优先（口径 / 体验）

7. **L7 + L8 门控口径**（漏推 / 误推 / 语义化意图）—— 这条直接决定「根据**聊天内容**」这条需求是否真的算达成。
8. **L6 三源竞态**、**L10 关键词截断**、**L11 加购幂等**。

### 第四优先（测试补强）

9. **T1 空转断言**（改造成真断言）、**T2/T3/T4**（断流 / 工具失败 / 迭代器异常）、**T6–T15**。

### 第五优先（收尾）

10. **N9 文档回灌**（`spec.md:273/709`、`test-report.md:403` 的 `qwen3.8-flash` → 实况）、**N5/N6 注释与 design 回灌**、**N7 用默认模型重跑走查**、**N8 归档**、**N10 工作区清理**。

---

## 七、本次盘点的方法学说明

- 三个 agent **各自独立读源码**，明确被要求「不采信 test-report 的结论节」，只把报告的「覆盖缺口」当作**待验证输入**。
  结果：**推翻了 6 处报告结论**（见第五节），其中 4 处是「报告说没覆盖、其实已覆盖」或「报告说未闭环、其实已闭环」。
- 逐条标注了「**未能确认**」的项（如 L4 的 `@agui-ui/client` 落帧点、L2 的 OpenAI SDK 默认超时），**未编造**。
- **本次盘点仍然只对到「用户原话」这两句**。若需求比这两句更多（例如「推荐要准」「要能多轮记住偏好」「要有解释理由」），
  仍需用户补全后重对一轮 —— 这正是「规格对齐 ≠ 需求对齐」的教训本身。
