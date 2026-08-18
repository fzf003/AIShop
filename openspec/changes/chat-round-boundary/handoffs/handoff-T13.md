# handoff-T13 — /api/chat catch 重构应用层重试（方案 A catch 重构）

## 工单

- 工单 ID：T13（实现，本 commit）
- 状态：已完成
- 所属变更：chat-round-boundary Phase 6 追加项——方案 A（应用层重试），设计来源 `docs/design/agent-failure-handling-design.md` §3.1
- blockedBy：T12（分类器 `IsRetryableAgentFailure`，已提交 bbe3496）；依赖的测试工单 T13T1/T13T2 另行实施（不在本 commit 范围）

## 改动文件清单

1. `src/AIShop.Api/Features/Chat/ChatEndpoints.cs`
   - `/api/chat` catch 块在 `catch (KeyNotFoundException)` 与既有通用兜底 catch 之间**新增** `catch (Exception ex) when (IsRetryableAgentFailure(ex, ct))` 分支（首次失败 → `logger.Warning` 不置 Activity Error → 换 `router.ActiveModel` 重试一次 → 成功用重试结果 / 失败才 OTel Error + 兜底）
   - 未改动：`catch (KeyNotFoundException)` 独立 catch（400「不支持的模型」不重试）、通用 `catch (Exception)` 兜底路径（R11 OTel Error 语义）

## 关键设计决策

1. **catch 次序**：`KeyNotFoundException` 独立 catch 保持在最前 → 新增可重试分支 → 通用兜底 catch 殿后。`IsRetryableAgentFailure` 分类器本身对 `KeyNotFoundException` 返回 false（T12 实现），双重保证其不落入重试路径。
2. **重试策略**：严格「重试一次」——`var retryModel = router.ActiveModel;` 首次若用非默认模型则换到默认模型；已用默认模型则同模型再试。重试调用 `router.GetAgent(retryModel).RunChatAsync(sid, req.Message?.Trim() ?? "", req.Username, preferences: preferencesText, ct)`，产生新 run_id、新轮。
3. **重试自身的 try/catch**：重试调用包独立 try/catch——重试成功直接复用 `result`（走后续关键词匹配/推荐逻辑）；重试也失败才 `Activity.Current.SetStatus(Error)` + `AddEvent("exception")` + `logger.Error` + 兜底 `new AgentChatResult("抱歉，暂时无法处理您的请求，请重试。", [], null)`，与 R11 被吞异常进 OTel span 语义一致。
4. **首次失败不污染 span**：可重试分支仅 `logger.Warning` 记录首次失败，不置 Activity Error——重试成功则请求 span 保持正常状态（不做无谓的错误标记）。
5. **`agentSw` 计时口径**：可重试分支不 `Stop()`，Warning 用 `ElapsedMilliseconds` 读取首次失败耗时；重试成功后由外层既有 `agentSw.Stop()`（`AgentCall` 总耗时日志）记录含重试的总时长；仅重试失败时在分支内 `Stop()` 记录失败时刻。
6. **非重试异常零改动**：通用 `catch (Exception ex)` 保持原样——既有 `Chat_WhenAgentThrows_SetsActivityErrorAndReturnsFallback`（`InvalidOperationException`）测试语义不变、通过。

## 遗留问题

- **无阻塞遗留**：T13 实现完成，测试工单 T13T1（重试成功/双失败路径）与 T13T2（非重试异常不重试/KeyNotFoundException 不重试）为独立工单，另行实施。
- 工作树中 `appsettings.json`、`ShoppingAssistantAgentRunTests.cs`、`.claude/agent-memory/*`、`.vs/*` 等会话前已存在的改动与本工单无关，未纳入 commit。
- `tasks.md` T13 行 `[ ]` → `[x]` 勾选：本 commit 已同步勾选。

## 测试情况

- `dotnet build AIShop.sln`（TreatWarningsAsErrors 已在 Directory.Build.props 全局开启）：**0 错误 0 警告**。
- 所属测试类全量：`dotnet test tests/AIShop.Api.Tests --filter "FullyQualifiedName~ChatEndpointsTests"` —— **28/28 通过**（含既有 R11 `Chat_WhenAgentThrows_*`、`Chat_WithNonExistentModel_ReturnsBadRequest`、T12T 分类器用例等，无回归），9s。
- 测试资源清理：无临时 DB 文件残留；未启动 Api/Aspire 进程。
