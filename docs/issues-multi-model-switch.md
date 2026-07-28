# 多模型切换 — 遗留问题清单

## P0：测试用例需要修复（提交前阻塞）

### SqliteChatHistoryProviderTests（2 个）

| 测试 | 原因 |
|------|------|
| `Store_TrimsWhenExceedsLimit` | 期望 52 条，实际 57 条。旧代码物理删除，新代码标记 `is_compacted=true`，总数不变。需改为断言 `is_compacted=0` 的数量为 50。 |
| `Store_TrimsToExactly50` | 同上，期望 52 条，实际 62 条。 |

### ChatEndpointsTests（5 个）

所有失败原因相同：集成测试用 Mock 替换了 `ModelRouter`，Mock 返回的 `AgentChatResult("模拟回复", ...)` 包含「回复内容」文本，但 `RunChatAsync` 走的是非 OpenAI 路径（`_isOpenAI == false`），在 `ShoppingAssistantAgent.RunChatAsync` 中 text 被 `IndexOf('{')` 解析 JSON 时找不到 `{` → `result` 为 null → `fakeResult` 的 text 不会被用于 Store → 用户消息存了但 assistant 回复没存 → 查询不到。

- `Chat_MessageGetsSavedToDb` — 期望 assistant 回复存在但不存在
- `Login_ReturnsSessionWithExistingHistory` — 同上
- `Agent_ShouldPreserveLast3Turns` — 同上
- `Recommendations_ReturnsResults` — 同上
- `History_IsPreserved_WhenSwitchingModels` — 同上

**修复方向**：给 Mock 的 `fakeResult` 设置 `Reply` 为 `{"Reply":"模拟回复","Keywords":[],"Preferences":[]}` 格式，让 JSON 解析通过。或者在测试中创建 MockAgent 时让 `RunChatAsync` 返回格式正确的 `AgentChatResult`。

---

## P1：SanitizingChatClient 死代码

`SanitizingChatClient.cs` 的清洗逻辑（4 步）已完整迁移到 `DeepSeekDelegatingChatClient`。当前无任何代码引用此类。

**动作**：删除此文件。

---

## P2：压缩触发条件边界

当前压缩策略：
1. `SaveChangesAsync` 提交新消息
2. 查询未压缩总数 `unCompressedCount`
3. 如果 `unCompressedCount > MaxStoredMessages(50)`，压缩超出的部分
4. 压缩时检查边界上 FCC→tool 配对是否完整

经测试正常（DB 中两个 session 各 50 条未压缩）。但未在大规模长对话下验证。

**建议**：下一步引入长对话压力测试，确认压缩+配对保护在 500+ 条消息下依然稳定。

---

## P3：PerServiceCallChatHistoryPersistence 的 Store 时机

当前 PerServiceCallPersistence 在 FICC 每次迭代后都 Store 全量消息。这意味着：

- 同一轮 Agent Run 中，Store 被触发 N 次（N = FICC 迭代次数）
- 消息以 FICC 迭代的中间状态（非完整配对）存储
- 跨模型切换时，Provide 加载出碎片化历史

**不建议现在改**——改动涉及 MAF 框架的 `ChatHistoryProvider` 生命周期理解。目前清洗器 + 压缩保护能兜住边界情况。如果后续跨模型切换又出现类似问题，再考虑在 Store 入口处做「只保留完整 user-assistant 配对」的过滤。

---

## P4：Instructions JSON 输出格式示例

已从 Schema 定义（`type`/`properties`/`required`）改为纯示例格式（`{"Reply":"...","Keywords":[...],"Preferences":[...]}`），Qwen 不再输出 Schema 定义。

当前示例中的关键词是 `["关键词1","关键词2"]`，可以改为更实际的示例如 `["耳机","巧克力"]`。

---

## P5：日志级别

- `裁剪旧消息` 日志：已改为 `Information` 级别（便于监控压缩是否正常）
- `裁剪旧消息 无需裁剪` 日志：保持 `Debug` 级别
- `DSDelegate` 入口日志：保持 `Information` 级别（便于跟踪 FICC 迭代）

---

## P6：干净数据库建议

当前 DB 遗留了大量旧 bug 产生的历史数据（双重 FICC 时的重复消息、孤儿 tool、is_compacted 标记不一致等）。虽然新代码能防御这些数据，但起始干净最好。

**建议**：上线时删除原 DB 文件，让 EF Core 重新创建。
