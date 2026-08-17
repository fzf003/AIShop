# handoff-T7 — 压缩安全阀兜底：4K 硬上限 + 未完成轮上限 5 + 计数口径

## 工单

- 工单 ID：T7
- 状态：已完成
- 所属变更：chat-round-boundary（spec「条数硬上限 4K 兜底」「未完成轮上限 5 个兜底」「未完成轮计数口径只统计有效轮组」）
- blockedBy：T6（压缩按 run_id 整轮整切至 K=12 + 未完成轮整组保留，见 handoff-T6）

## 改动文件清单

- `src/AIShop.Api/Agents/SqliteChatHistoryProvider.cs`（StoreChatHistoryAsync 步骤 2 压缩逻辑扩展：新增两个安全阀阶段 + 两个常量；阶段 1 T6 逻辑保留）

## 关键设计决策

- **压缩三阶段结构**：保留 T6 阶段 1（完整轮压缩到 ≤ K=12），新增阶段 2（未完成轮上限 5）、阶段 3（条数硬上限 4K）。三个阶段依次累加 `compressIds`，最后一次性 `ExecuteUpdateAsync` 整轮标记 `is_compacted=true`，仍是非物理删除、整轮整切、FCC↔tool 配对不分离。
- **阶段 2 未完成轮上限（spec #11）**：`incompleteRounds.Count > 5` 时强制压最旧未完成轮（按组内最大 id 升序）直到 ≤ 5，并记 Warning。
- **计数口径（spec #12）**：阶段 2 统计只取 `run_id 非空 && 组内行数 ≥2` 的组；`run_id=NULL` 历史行不计入未完成轮（它们由阶段 1 完整轮口径处理），防止历史库瞬间超上限触发误压缩。
- **阶段 3 条数硬上限（spec #10）**：保留区未压缩条数 > 4096（4K 条）时，按最旧顺序取整轮前缀累计行数，取累计超过溢出量所需的最少整轮（整轮整切，末轮可能多压到 4K 以内），并记 Warning。存储安全阀优先于「未完成轮整组保留」——未完成轮在阶段 2 已压到上限内，阶段 3 作为最后兜底亦可被压（如单个未完成大轮超 4K）。
- **4K 值解析**：权威设计文档 `docs/design-chat-round-boundary.md` 写「条数硬上限（如 4K 条）」，结合 T7T1「seed 量大」与「防存储膨胀」语义，取 **4096 行**（标准 4K）。评审 G3「随 K 缩放」理解为「K 决定业务保留多少轮、硬上限兜存储上限」的分工，不采用 4×K=48（那等于保留容量，无安全阀意义）。
- **阶段 3 压缩顺序**：按 `MaxId` 升序（最旧优先），不区分完整/未完成——spec 语义「强制压最旧的整轮」。
- **常量**：`MaxIncompleteRounds = 5`、`MaxMessagesHardLimit = 4096`；`MaxCompletedRounds = 12` 不变。
- **SonarAnalyzer 规避**：阶段 3 初版 foreach 触发 S3267（循环应简化用 LINQ），改为 while 前缀累计 + `Take(takeCount).SelectMany` 单次 LINQ 消费，语义不变且过质量门禁（0 警告）。

## 遗留问题

- **T7T1 / T7T2 未实现**：本工单仅实现 + 临时验证（临时测试跑通后已移除未提交），专项测试用例（4K 硬上限 / 未完成轮上限 / NULL 计数口径）属后续独立测试工单，blockedBy T7。
- **tasks.md 勾选待办**：`tasks.md` 中 T7 行标 `[x]` 受 `.claude/hooks/check_gateway.py`（PreToolUse Write/Edit）门禁——该文件强制由 @task-breaker 编辑（检测到实际调用方非 task-breaker 即拦截），本子代理上下文无 task-breaker 可达，未强行绕过流程门禁；需由编排方委派 @task-breaker 完成 `tasks.md` T7 行 `[ ]` → `[x]`（仅该行，不动其他工单）。与 T1-T6 相同机制。
- **Warning 日志断言未覆盖**：阶段 2/3 的 Warning 日志未被测试断言（Serilog 需挂 sink），仅代码走查确认调用正确；T7T1/T7T2 可考虑挂测试 sink 断言。
- 代码库中 `appsettings.json`、`.claude/agent-memory/*`、`.vs/*` 存在与本变更无关的未提交改动，未纳入本工单 commit。

## 测试情况

- `dotnet build --warnaserror`：0 错误 0 警告。
- `dotnet test`：全部通过无回归 —— AIShop.Api.Tests 246 通过、AIShop.McpServer.Tests 11 通过，共 257，0 失败。
- 临时验证（已移除未提交，三用例全绿）：
  - `Store_SingleHugeRoundOver4K_ForceCompressesToHardLimit`：seed 5000 行完整轮 + Store 落新轮 → 阶段 3 强制压 5000 行大轮整轮，保留区 2 行 ≤ 4K。
  - `Store_MoreThan5IncompleteRounds_ForceCompressesOldestIncomplete`：6 个未完成轮 → 阶段 2 强制压最旧 1 个（id 1-2），其余 5 个未完成轮 + 新完整轮保留。
  - `Store_NullRunIdSingleRows_NotCountedTowardIncompleteLimit`：10 条 NULL 单行 + 5 未完成轮 → 未完成轮计数 5 ≤ 5 不触发强制压缩，全部未压缩（NULL 单行不计入口径）。
- 既有压缩相关测试（阶段 1 完整轮口径）全部通过，未因新增阶段改变其行为（阶段 2/3 仅在超限时触发）。
