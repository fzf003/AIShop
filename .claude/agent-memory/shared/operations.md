# 操作/流程经验

> 编排层与工具链经验，跨变更生效。与 `glossary.md`（领域术语）互补：
> glossary 记"项目是什么"，本文件记"操作时要注意什么"。
> 每次踩坑后追加一行，下次遇到同类操作直接规避。

## 归档 / OpenSpec 操作

| 经验 | 说明 |
|------|------|
| `openspec archive` 中文 spec 需 `--no-validate` | 当前 openspec CLI 强校验每条 ADDED 需求必须含 `SHALL`/`MUST` + 至少一个 scenario。本仓库 spec 是中文 Given/When/Then 风格（历史归档同样无 SHALL/MUST），校验必失败。归档用 `openspec archive {name} --skip-specs --no-validate --yes`。注意 `--skip-specs` 只跳过"同步主 spec"，不跳过格式校验，两者需同用 |
| 归档前确认 `tasks.md` 全 `[x]` + `test-report.md` 存在 | 这是 `.claude/hooks/check_archiregate.py`（注意文件名拼写是 archiregate 非 archivegate）的拦截条件；不满足时 `openspec archive` 被 BLOCK |
| Windows 移动/删除被锁目录报 EBUSY | 常见诱因：并行 claude 会话的 cwd 停在该目录。规避：① `cp -r` 复制到目标 + `diff -r` 校验内容一致（绕过 rename 锁，只读操作）；② 源目录空壳等持有会话退出后 `rmdir`；③ 不要强杀其它 claude 进程。归档内容完整性以复制成功为准，空壳目录不影响归档语义 |

## git 操作

| 经验 | 说明 |
|------|------|
| `.gitignore` 对已跟踪文件无效 | 新增 ignore 规则后，若文件已被 git 跟踪（`git ls-files` 可见），规则不生效。需 `git rm --cached` 取消跟踪（文件保留磁盘）。已两处验证：`.claude/agents`、`openspec/changes/*/handoffs/`（openspec/ 在 .gitignore 但历史 commit 顺带入库） |
| commitgate / check_gateway 是 Claude Code PreToolUse hook | 不是 git hook，`git commit --no-verify` **无效**。hook 拦截基于 Bash 命令匹配（`git commit` / `openspec archive` 等），要绕过只能改 hook 配置（需用户确认）或修测试。commitgate 每次 commit 强制全量 `dotnet build` + `dotnet test`（各 300s 超时） |
| `tasks.md` 仅限 @task-breaker 编辑 | `check_gateway.py` 校验 Write 的 agent_type。主对话 / workflow-subagent / implementer 写 tasks.md 会被 BLOCK。因此 workflow 脚本里 implementer 的"标记 [x]"步骤实际会失败——勾选需统一由编排方委派 @task-breaker 收尾 |

## Workflow 脚本（.claude/workflows/*.js）

| 经验 | 说明 |
|------|------|
| 模板字符串内不能嵌套反引号 | `buildPrompt` 用模板字符串（`` ` ``）包裹，内部命令若再用反引号包命令会提前终止字符串，Workflow 解析器报错（`node --check` 可能查不出，Workflow 二次解析才暴露）。命令示例直接写字面文本，不用反引号 |
| 支持按阶段续跑 | 给 TASKS 加 phase 过滤：`const ACTIVE = args?.phase ? TASKS.filter(t => t.phase === args.phase) : TASKS`，后续引用改 ACTIVE。已完成变更追加新阶段（如 Phase 6）时，用 `Workflow({scriptPath, args:{phase:'...'}})` 只跑新工单，不重复已 commit 的旧工单 |
| implementer 本地测试用 `--filter` | 用户可要求"单元测试只跑影响到的"：在 buildPrompt 里让 implementer 用 `dotnet test --filter "FullyQualifiedName~<TestClass>"` 跑相关类，不跑全量（30s+）。但 commit 时 commitgate hook 仍会全量兜底 |

## 测试 / 环境

| 经验 | 说明 |
|------|------|
| 测试读测试输出目录的 appsettings.json | `Host.CreateApplicationBuilder()`（ServiceDefaultsDebugTests 用）会加载 `tests/.../bin/Debug/net10.0/appsettings.json`——它从 `src/AIShop.Api/appsettings.json` 复制而来。若生产配置含 `AgentTelemetry:Debug: true` 等，测试结果受生产配置影响。测试应隔离自己的配置 |
| Windows curl 中文请求体需 UTF-8 | 终端默认 GBK，`curl -d '{"message":"中文"}'` 会以 GBK 编码发送 → 服务端严格 UTF-8 解码失败报 400/500。用 `--data-binary @file` 传 UTF-8 文件 + `-H "Content-Type: application/json; charset=utf-8"` |
| MAF `DisableCompaction=true` 只关 MAF 的 CompactionProvider | HarnessAgent 反编译确认：DisableCompaction=true → compactionStrategy=null → CompactionProvider=null → 不使用 InMemoryChatHistoryProvider+ChatReducer。**不影响本项目自研的 `is_compacted` 存储压缩**（run_id 整轮整切，SqliteChatHistoryProvider 内），两套机制独立。MAF 的"上下文压缩（摘要/token 裁剪）"与本项目"存储压缩（硬截断）"是两回事 |

## 遗留 / 已知问题（归档前应确认是否处理）

| 项 | 说明 |
|------|------|
| ModelRouter 判断不一致 | `cfg.Name` vs `cfg.Model` 两处判断不同源：qwen 槽位 Model 改为非 qwen 模型名（如 deepseek-v4-pro-0813）时，`CreateChatClient` 按 Name 走 OpenAI 路径（httpClient=null），`DeepSeekDelegatingChatClient` 按 Model 名识别为 DeepSeek → 抛「DeepSeek 路径要求 _httpClient 非 null」。建议统一判断源 |
| CS8019 unused using | `ModelRouter.cs` 的 `using System.Net.Sockets;` 历史遗留，LSP 报但 MSBuild 未拦（0 警告），无引用可安全删除 |
| `AgentTelemetry.Debug` 误提交 | `appsettings.json` 的 `Debug: true` 曾致 2 测试失败，后已恢复（未提交改动仅本地模型名） |
