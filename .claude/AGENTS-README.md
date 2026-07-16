## AIShop 多 Agent 工作流快速开始

### 🎯 三阶段工作流

你的项目配置了 **三个专用 Agent**，自动完成从设计、开发到测试的全流程：

| 阶段 | Agent | 触发方式 | 交付物 |
|------|-------|--------|--------|
| 🏗️ 设计 | 架构师Agent | `[arch-agent]` | 设计文档、API规范 |
| 💻 开发 | 开发Agent | `[dev-agent]` | 代码、功能实现 |
| ✅ 测试 | 测试Agent | `[test-agent]` | 测试、覆盖率报告 |

### 📋 快速示例

**想要添加一个新功能？**

```
用户请求1：
[arch-agent] 请设计用户积分系统
- 用户操作后获得积分
- 积分可以兑换商品
- 需要与推荐系统集成

↓ 架构师输出设计文档...

用户请求2：
[dev-agent] 按照设计文档实现积分系统

↓ 开发者写代码、编译验证...

用户请求3：
[test-agent] 为积分系统编写完整的集成测试

↓ 测试者运行测试、评估覆盖率...
```

### 📁 Agent 配置位置

```
.claude/
  agents/
    architect.md    ← 架构师 Agent 定义
    developer.md    ← 开发 Agent 定义
    tester.md       ← 测试 Agent 定义
  settings.json     ← 工作流配置（Agent 触发规则）
  WORKFLOW.md       ← 详细工作流指南
```

### 🚀 常用命令

```
[arch-agent] <需求描述>      # 触发架构师：设计、API规范、ADR
[dev-agent] <开发任务>       # 触发开发者：实现、编码、端点
[test-agent] <测试任务>      # 触发测试者：测试、Mock、覆盖率
```

### ⚠️ 重要规则

1. **严格顺序** — 设计 → 开发 → 测试，不能跳过
2. **设计冻结** — 开发开始后，设计更改需要通知架构师重新评估
3. **代码冻结** — 测试开始后，代码改动需要重新测试
4. **交付物检查** — 每阶段完成后确认对应输出物是否完整

### 📖 详细指南

完整工作流文档请查看：[.claude/WORKFLOW.md](.claude/WORKFLOW.md)

### 🔗 快速导航

- **Agent 定义**：[.claude/agents/](.claude/agents/)
- **工作流配置**：[.claude/settings.json](.claude/settings.json)
- **项目规范**：[AGENTS.md](../AGENTS.md)
- **技术指南**：[CLAUDE.md](../CLAUDE.md)

---

**提示**：如果看到 Agent 没有自动触发，检查是否：
1. 使用了正确的触发关键字 (如 `[arch-agent]`)
2. 完成了前置阶段 (设计 → 开发 → 测试)
3. settings.json 中的工作流配置是否生效
