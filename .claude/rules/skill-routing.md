---
name: skill-routing
description: 技能路由规则，描述各场景应调用的技能
---

# Skill 路由规则

## MAF / Agent 开发
- MAF/Agent 开发、写 Agent 代码/工作流 → 调用 /maf-reference
- Agent 相关 Bug 排查/DI 注册失败 → 调用 /maf-reference

## 变更流程
- 新功能/变更启动 → 调用 /openspec-workflow
- 单步 opsx 操作 → 调用 /opsx:new, /opsx:apply, /opsx:verify 等

## 架构与设计
- 产品创意/头脑风暴 → 调用 /office-hours
- 战略/范围界定 → 调用 /plan-ceo-review
- 架构设计 → 调用 /plan-eng-review
- 设计系统/方案评审 → 调用 /design-consultation 或 /plan-design-review
- 全流程评审 → 调用 /autoplan

## 质量与测试
- Bug/错误排查 → 调用 /investigate
- QA/网站测试 → 调用 /qa 或 /qa-only
- 代码审查/diff 检查 → 调用 /review

## 部署与发布
- 发布/部署/提 PR → 调用 /ship 或 /land-and-deploy

## 上下文管理
- 保存进度 → 调用 /context-save
- 恢复上下文 → 调用 /context-restore
- 编写 backlog-ready 的 issue/spec → 调用 /spec
