---
name: skill-routing
description: 任务特征 → skill 的路由表。自上而下判定，首个命中即采用；仅路由已安装的 skill。
---

# Skill 路由规则

## 判定顺序
自上而下，首个命中即采用；多个命中时取优先级更高者；仍不确定先 /brainstorming 澄清。

## 变更流程（P0）
| 触发特征（可判定的输入信号） | 路由 | 反例（不适用） |
|---|---|---|
| 用户输入 `/openspec-workflow` 或"开始新功能/变更" | /openspec-workflow | 仅修 bug / 一行代码 |
| 单步 OpenSpec 操作（新建/应用/验证/归档变更） | /openspec-new-change、/openspec-apply-change、/openspec-verify-change、/openspec-archive-change | 非 OpenSpec 流程 |

## MAF / Agent 开发（P0）
| 触发特征 | 路由 | 反例 |
|---|---|---|
| 涉及 `Microsoft.Agents.*` 包、Agent 工作流/编排、Agent DI 注册失败 | /maf-reference | 普通 API/EF 代码 |

## 架构与设计（P1）
| 触发特征 | 路由 | 反例 |
|---|---|---|
| 新功能需求不明确，需探清意图与边界 | /brainstorming | 需求已明确 |
| 多步骤实施前需要技术计划 | /writing-plans 或 /plan | 单步小改 |
| 代码审查 / diff 检查 | /requesting-code-review 或 /pr-review | 仅语法错误，可直接修 |

## 质量与测试（P1）
| 触发特征 | 路由 | 反例 |
|---|---|---|
| Bug / 测试失败 / 异常行为排查 | /systematic-debugging | 根因已定位 |
| 项目内代码结构检查（未跑运行时） | /dotnet-inspect | 运行时行为问题 |
| 浏览器 E2E / 网站测试 | /playwright-cli | 纯单元测试场景 |

## 部署与发布（P2）
| 触发特征 | 路由 | 反例 |
|---|---|---|
| 功能完成，决定如何集成/收尾分支 | /finishing-a-development-branch | 仍在开发中 |
| worktree 分支合并 master 善后 | /worktree-merge | 无 worktree |
| Aspire 启动/编排/部署 | /aspire、/aspire-deployment 等 | 非 Aspire 环境 |

## 上下文管理（P2）
| 触发特征 | 路由 | 反例 |
|---|---|---|
| 保存/恢复进度 | OpenSpec 变更 + git 小步提交 + memory | — |
| 编写 backlog-ready 的 issue/spec | /spec 或 /openspec-workflow | 需求不明确 |

## 兜底规则
- 路由目标未安装/不可用 → 降级为直接实现，并提示缺失
- 跨类场景 → 按 P0 → P2 优先级裁决
