# AIShop — AG-UI 三变更收口 · 状态交接

> 2026-09-20 13:00 更新（第 2 版）。**上下文压缩 / 换会话前的恢复点**。
> 上一版写于 2026-09-20 00:25，本版反映「收敛批次 C」开工后的状态。

---

## 〇、换会话前必读：为什么换

本会话（`281c7046`）**从 9/18 02:09 起跑了 2 天**，累积 **308 万 tokens / 11.7 MB jsonl**。
进程反复异常退出 → harness 用 `--resume` 自动拉起 → **重放最后一轮** → **重复派工**。

**已确认的 8 次并发事故，根因都在这**（不是「谁派工不小心」）。
证据：进程快照抓到**多个 `--resume 281c7046`**（12:40:53 一个、12:52:48 又一个，同一 sessionId）；
某个 agent 连续 3 次观测到「它刚报编译错，15 秒后有人恰好把那个错修掉」——**那个"别人"就是上一轮的自己**。

### 必须纠正的一条旧规则

> ❌ 旧规则：「派工前必查 `ListAgents`」
> **在这个场景下无效** —— `ListAgents` 只列**本进程**的 subagent，**跨实例不可见**。
> 每次查都是「干净」，然后照样撞车。

**新规则（待落地）**：**幂等派工锁**
```
派工前 → 写 obj/.dispatch-<ticket>   {ticket, timestamp, state: dispatched, pid}
恢复后 → 新实例先读它：state=dispatched 且未超时（< 40 min）→ 跳过派工
完成时 → 改 state: done
```
关键：基于**文件系统**（两个实例都看得见），而非进程内的 `ListAgents`。

---

## 一、四个变更的状态

| 变更 | 代码 | 报告 | 状态 |
|---|---|---|---|
| `agui-client` | ✅ 184/184 | ✅ | 未归档 · 2 处缺口待补（B 类） |
| `agui-client-support` | ✅ 162/162 | ✅ | 未归档 · G1 已补并提交 |
| `agui-reco-realtime` | ✅ 138/138 | ✅ | 未归档 · **收敛批次 C 在跑** |
| ~~（Mimo 修复）~~ | ❌ | — | **用户裁决「暂时放下」** |

**HEAD 见 `git log -1`。三个变更都未归档。**

---

## 二、用户的原始需求（**对比基准，不是 spec**）

> **「根据聊天内容实时推荐产品显示在推荐产品栏中」** + **「任何模型都要能用」**

| 需求 | 结论 |
|---|---|
| 「…显示在推荐产品栏中」 | ✅ 达成 |
| 「根据**聊天内容**」 | ⚠️ **部分达成** —— 实际是 **23 组关键词子串匹配**；「帮我挑一个」不触发，「我在健身但今天不买」会误触发（详见盘点 L7/L8） |
| 「**实时**」 | ⚠️ 达成，**前提是本轮流能正常走完**（L1 就是那个前提不成立时） |
| 「**任何模型都要能用**」 | ❌ **未达成** —— 默认模型 Mimo 下三项现象全中；`design.md:38` 把它写成非目标 → 无人接 |

---

## 三、完整盘点：`docs/agui-convergence-inventory.md`

**52 条**（逻辑不完整 21 + 没测试 22 + 没做完 10）。**按需求对齐得出，不是按 spec**。
方法：3 个只读 agent 各自独立读源码、**明确禁止采信报告结论**；结果**推翻了 6 处报告原话**
（其中 4 处是「报告说没覆盖、其实已覆盖」或「报告说未闭环、其实已闭环」）。
→ **报告本身不可全信。**

---

## 四、收敛批次 C（`agui-reco-realtime/tasks.md` 第九节，4 条）

| # | 工单 | 状态 |
|---|---|---|
| **C1** | 服务端：流内异常时本轮仍发确定收尾 | 🔄 **半成品，正在收尾** |
| **C2** | 客户端：`isRunning` 挂起兜底 | ⚪ 待开始（blockedBy C1） |
| **C3** | 非法 tool_call `arguments` 规范化为 `{}` | ⚪ 待开始 |
| **C4** | `ActiveModel` 由 `gpt-4.1` 改为 `qwen` | ⚪ 待开始 |

### C1 的半成品状态（**换会话后接手必读**）

**已做完的**：
- `src/AIShop.AguiHost/Recommendation/AguiStreamFailureContent.cs`（**新增**）—— 只携带数据的 `AIContent`，
  `ErrorCode = "agent_stream_failure"`、`DefaultMessage`、`Message` 属性。
- `src/AIShop.AguiHost/Recommendation/RecommendationPushAgent.cs`（**已改**）—— 用 `GetAsyncEnumerator`
  手工迭代（绕开「带 catch 的 try 内不能 yield」）→ 内层异常只记录不吞 → 推荐照常算（推送与异常解耦）
  → 补发 `AguiStreamFailureContent` → `ExceptionDispatchInfo.Capture(...).Throw()` 原样重抛。
- `tests/AIShop.AguiHost.Tests/AguiStreamCloseoutTests.cs`（**新增，可编译**）—— 红测试 + 增量读改写。

**还缺的（2 处编辑，都在 `AguiRecommendationStreamOptions.cs`）**：
1. `MapContent` 补 `AguiStreamFailureContent` → 单元素 `RunErrorEvent`（`Message` / `Code`）。
2. 多态注册补 `new JsonDerivedType(typeof(AguiStreamFailureContent), "aguiStreamFailure")`。

> ⚠️ **第 2 步有个会让修复白做的陷阱**：该文件第 80 行附近的幂等判断是 **early-return**
> （`if (DerivedTypes.Any(d => d.DerivedType == typeof(RecommendationPushContent))) return;`）——
> **照抄着在下面加第二个类型会被这次 `return` 直接跳过** → 未注册 → AG-UI 转换器的**无条件快照序列化**
> 抛 `NotSupportedException` → **失败路径一触发反而整条流断，比不修更糟**。
> 必须改成「按类型逐个判断、只补缺失的」。

### C1 已独立验证的三个技术结论（**不要重新验证**）

1. **终止帧确实能在 rethrow 打断连接之前送达客户端** —— 独立探针（不引用 AIShop 任何代码）实测
   Kestrel 真实 HTTP 与 WAF TestServer 两条路径都收到已送出的帧，**10 轮稳定（min=max=94 字符）**。方案成立。
2. **坑**：同场景用 `Content.ReadAsStringAsync()` 会抛 `HttpRequestException` 并**丢掉全部已送出帧**（观测面归零）。
   → 测试必须用 `HttpCompletionOption.ResponseHeadersRead` + **手动增量读**，并把传输异常当**预期结果**、不判失败。
3. `AGUI.Abstractions`（版本 **0.0.5**）的 `RunErrorEvent` 形状（已反射核实）：`public sealed`、
   **public 无参 ctor**、`Type` 是只读 virtual override、**`Message` / `Code` 为 get/set**。

### C1 未取得的证据
**RED 尚未跑**（被并发写者打断）。handoff-C1 必须含「补编辑前红 / 补后绿」两次实际输出，否则不算合格。

---

## 五、工作区未提交状态（**换会话后先核对**）

```
HEAD = 见 git log -1
 M src/AIShop.AguiHost/Recommendation/AguiRecommendationStreamOptions.cs   ← C1 半成品（doc 注释说映射 RUN_ERROR，代码没有）
 M src/AIShop.AguiHost/Recommendation/RecommendationPushAgent.cs           ← C1 已改
?? src/AIShop.AguiHost/Recommendation/AguiStreamFailureContent.cs          ← C1 新增
?? tests/AIShop.AguiHost.Tests/AguiStreamCloseoutTests.cs                  ← C1 红测试
 M src/AIShop.Web/vite.config.ts                                           ← 用户本地改动，勿动
?? tests/AIShop.AguiHost.Tests/.claude/                                    ← 早期并发实例留下的 hook shim
?? obj/  .s5_start.txt  final-reco-result.png  ...                         ← 待清理
 D openspec/changes/multi-model-agent/handoffs/*.md  multi-model-agent/tasks.md  shopping-cart/tasks.md
                                                                           ← 不是本会话删的，来由不明
```

**已提交的收口**：
- `5cdc245` test(G1) —— REST 加购 → AI `get_cart_summary` 读到同一份数据
- `da62fda` chore —— Api 配置出范围回退 + T8 一致性测试收窄为 AguiHost 形状校验
  → **`src/AIShop.Api/` 零净改动**

---

## 六、已建立的规则 / 边界

| 规则 | 出处 |
|---|---|
| **不建新变更** —— 所有收敛工单归到现有 3 个变更（用户裁决） | 本次会话 |
| **不碰 `AIShop.Api`** —— Api 出范围；`Core/`、`Service/ModelRouter.cs`、`Service/Clients/QwenToolCallFixClient.cs` 被 Api 老链使用，**禁改** | 用户 2026-09-20 |
| **AG-UI 专属落点** = `src/AIShop.Service/Agui/**`（只被 AguiHost 使用）+ `src/AIShop.AguiHost/**` | 代码依赖查证 |
| **工单粒度要小 + 每条必须有可执行验证方法**（达成才算合格） | 用户 2026-09-20 |
| **Step 7.5 需求对齐闭环**：报告出炉立刻「报告 × **需求**」对比 → 循环修复重测 → 最多 3 轮 → 超限停并报用户 | `matt-workflow/SKILL.md` |
| **对比的是「需求」不是「规格」** | SKILL.md 原则 15 |
| **覆盖缺口必须逐条裁决** | SKILL.md 原则 13 |
| ❌ **作废**：「派工前必查 `ListAgents`」（跨实例不可见，无效） | 本版纠正 |
| ✅ **待落地**：**幂等派工锁**（见第〇节） | 本版提出 |
| **发现并发写者时不要 `git checkout`**（会推对方进重试循环） | `operations.md` |
| **tasks.md > 45KB 禁止整写**（会静默丢内容）；用 Edit 定点追加，且 `@implementer` 有权改 | `operations.md` |
| **cwd 必须停在仓库根**（嵌套会让 hook 相对路径失效 → Edit 全挂） | 记忆 + agent 指令第 -1 步 |

---

## 七、下一步

1. **等 C1 收尾完成**（当前在跑）→ 验证它的 **RED→GREEN 两次实际输出**
2. **换会话**（本会话 308 万 tokens，继续用只会更频繁重启）
3. 新会话按本文件第〇/四/五节接手 → 继续 **C2 → C3 → C4**
4. 批次 C 全绿后：跑受影响全量 → 重出报告 → 回到 **Step 7.5**（报告 × 需求逐条对）
5. 之后按 `docs/agui-convergence-inventory.md` 的 **A/B/C/D/E 五档**继续（B 档 4 条 → C 档 8 条写明接受 → D 档测试补强 → E 档收尾）
6. **归档需用户显式指令**；`agui-client` 按用户旧规要等 Mimo 处理完

---

## 八、环境

- **模型**：`deepseek` ✅ / `qwen` 槽位 = **`kimi-k3`** ✅（实测 HTTP 200；原 `qwen3.8-flash` 已 **403 免费额度耗尽**）/ `gpt-4.1` = Mimo ⚠️ 工具调用不可靠
- **Aspire**：用户用 `aspire start` 起；**在跑时 `dotnet build` 会报 MSB3021 文件占用（不是编译错误）** → 需先停
- 看日志用 Aspire 遥测（`mcp__aspire__list_traces`，resource = `agui-*`）
