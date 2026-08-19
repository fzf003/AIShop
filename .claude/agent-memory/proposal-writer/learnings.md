# proposal-writer 经验记忆

> 每次完成提案后自动追加。启动时读取，帮助理解项目上下文。

## 项目领域术语

<!-- 记录从 exploration.md 中发现的核心概念 -->

- **run_id**：`chat_messages` 表的轮次分组标识（`ChatMessageRecord.RunId: Guid?`，列 `run_id` TEXT）。一次 `RunChatAsync` 的所有消息共享同一值（由 Agent 生成写 `Session.StateBag("RunId")`，Store 时读取打标，FICC 多次 Store 共享）。只负责**分组识别轮次**，不承担排序（`id` 仍是组内顺序锚点）；`run_id=NULL` 历史行每行自成一组走旧行为
- **is_final**：`chat_messages` 表的轮次终点标记（`ChatMessageRecord.IsFinal: bool`，列 `is_final` INTEGER 0/1）。`AnyAsync(m => m.RunId==runId && m.IsFinal)` 即该轮完成。写入双路径：Store 内判定为主（末条纯文本 assistant 即迭代结束）+ Run 后兜底补标（`MarkRoundFinalAsync(runId)`）。语义以「终点标记」为准——通常为最终回复行，仅调工具场景可为 tool/FCC 行
- **未完成轮**：组内无 `is_final=true` 行的轮（FICC 迭代耗尽 / 异常中断 / 仅调工具未输出文本）。压缩时整组保留，受独立上限 5 个约束（超限强制压最旧并记 Warning）；计数口径只统计 `run_id` 非空且 ≥2 行的组，`run_id=NULL` 历史行不计入
- **整轮整切压缩**：压缩由「按条数硬切保留最新 50 条 + ±1 相邻推断保护」改为「按 run_id 整轮整切」——最近 K=12 个完整轮次（纯文本轮 2 行/轮、含工具轮约 4 行/轮，容量约等于旧 50 条）+ 条数硬上限 4K + 未完成轮上限 5。整轮同压使 FCC↔tool 配对结构性不分离，删除 ±1 推断逻辑

## 架构约束

<!-- 发现的必须遵守的架构规则 -->

- ProductCatalog 无状态纯逻辑类应注册为 Singleton 而非 Scoped，节省对象创建和 GC 开销
- Infrastructure 层不得依赖任何 Agent/AI 框架包（Microsoft.Agents.*、Microsoft.Extensions.AI.*），Agent 相关类型需迁移到 Api 层
- IUnitOfWork 在仅有一个仓储的项目规模下属于过度抽象，SaveChangesAsync 直接在仓储接口上定义
- 全局异常处理中间件 UseExceptionHandler 必须在所有 MapXxxEndpoints 之前注册
- McpServer 作为独立可执行程序允许例外引用 Infrastructure

## 设计模式

<!-- 记录从提案中提炼的可复用模式 -->

- **前端分类过滤模式**：当展示数据量小（<= 50 条）且数据已在前端就绪时，纯前端过滤（分类/搜索/排序）比后端新增查询参数方案改动量更小、交付更快，零后端风险
- **Agent Singleton 优化模式**：对于无状态 AI Agent，将所有依赖验证为 Singleton 后改为 AddSingleton 注册，可避免每次请求重建 Agent 的昂贵开销（200-500ms），降幅达 100x
- **合并变更模式**：两个互不干扰的优化方向（纯前端 + 纯后端）可合并为一个变更管理，降低管理开销，仍允许独立实施和独立验证
- **轮次边界数据模型模式**：需要「识别数据分组 + 判断分组完成」时，用「分组标识（只分组不排序）+ 完成标记（显式终点）」两列正交互补——`run_id` 回答「属于哪一轮」，`is_final` 回答「是否正常收尾」，查询退化为 `AnyAsync` 简单谓词，不再依赖运行时推断或相邻 id 推断。配套：分组值由上层（Agent）生成写会话状态（StateBag）经多次写入共享；写入口收敛到单一 Provider（`MarkRoundFinalAsync`）；压缩按分组整切以保配对结构性完整

## 常用备选方案

<!-- 本次未采用但值得记录的方案 -->

- **IUnitOfWork 模式**：被否定理由为单一仓储时过度抽象
- **保留静态类 + IServiceProvider 门面**：被否定理由为服务定位器反模式，无法 Mock
- **条件编译保留 Infrastructure MAF 引用**：被否定理由为复杂度高、无法编译验证
- **后端新增搜索/分类/排序 API**：被否定理由为 18 条数据不值得服务端过滤，增加交付周期
- **保持 Scoped + 缓存 BuildInstructions 结果**：被否定理由为不如改 Singleton 彻底，引入额外缓存逻辑增加复杂度
- **仅加 run_id、完成判断仍靠「末条纯文本 assistant」推断**：被否定理由为 `MaximumIterationsPerRequest` 耗尽时末条是带 tool_calls 的 assistant，推断失效——正是要修的问题本身
- **轮次计数器（round 数字自增）替代 Guid**：被否定理由为 Store 每次新开 DbContext，会话级计数器需读上一轮再自增、多次 FICC 迭代易错；Guid 只分组不排序更简单
- **仅扩展 ±1 相邻推断多保护几个边界**：被否定理由为无法吸收历史碎片（双重 FICC 重复消息、孤儿 tool 让相邻 id 不是配对另一半），推断规则越堆越难维护；按 run_id 组内配对是结构性解法
- **schema 迁移用强制清库替代幂等 ALTER**：被否定理由为与「代码兼容」承诺冲突；幂等 ALTER（`PRAGMA table_info` + `ADD COLUMN`，SQLite 无 ADD COLUMN IF NOT EXISTS）成本低，可与「部署清库」叠加（代码兼容为防御，清库得干净数据）
