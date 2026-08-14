# worktree-merge

worktree 分支开发/变更完成后，善后并合并到 master 的标准流程。

## 触发时机

- worktree 分支的开发/变更完成，准备合并到 master
- 会话收尾：归档、清理、合并

## 前提

- 变更已按 matt-workflow（或等价流程）完成，测试全绿
- 目标是把 worktree 分支合入 master

## Step 1: 善后检查清单

1. **归档变更**：`openspec archive {change} --skip-specs --yes`
   - 若报 spec 校验失败（ADDED/MODIFIED 需求 must contain SHALL or MUST / include at least one scenario）：委派 @spec-writer 补格式（语义不变），再归档
   - 归档 rename 报 EBUSY（目录被锁）：多半是编辑器（Typora 等）打开了变更文档，关闭后重试；仍锁则手动 `cp -r` 到 archive + 删除源
2. **清理临时文件**：`.claude/tmp/`、临时探针目录等（删除前确认归属，不删他人/历史文件）
3. **提交 agent-memory 经验**：`git add .claude/agent-memory/ && git commit`（各子 Agent 的跨 session 经验，保留供后续使用）
4. **清理废弃代码/表**：如废弃 DbSet（移除映射 + 引用，保持测试绿）
5. **停止应用**：`aspire stop`（检查无残留监听进程）

## Step 2: 合并到 master

```bash
# ① worktree 内：推送分支到远程
git push origin {worktree-branch}

# ② 主 checkout 内（master 所在目录）：
git fetch origin
git checkout master          # 确认当前在 master
git merge origin/{worktree-branch}   # 三路合并
dotnet build -warnaserror
dotnet test                  # 验证全绿
git push origin master       # 确认没问题后推送
```

## Step 3: 关键注意

- **master 被主 checkout 占用**：worktree 会话无法 `checkout master`（隔离），合并须在主 checkout 执行（或 PR）
- **git merge 三路合并保留两边**：master 现有内容 + worktree 全部代码/文档都保留，不会覆盖；agent-memory 随分支提交一起带上
- **合并前确认 master 干净**：`git status --short` 无代码未提交（避免与 master 分支其他 agent 的进行中工作冲突）
- **agent-memory 是项目级共享记忆**（`.claude/agent-memory/`，git 跟踪）：应随合并进 master，让 master 分支的 agent 也能读取经验，避免重复踩坑
- 归档前确认：tasks.md 全部 [x]、test-report.md 存在（check_archive_gate 会校验）

## 快速自查

- [ ] 变更已归档（openspec/changes/archive/ 下）
- [ ] `.current-change` 已清空
- [ ] 临时文件已清理
- [ ] agent-memory 已提交
- [ ] worktree 分支已推送 origin
- [ ] master 本地合并 + build/test 通过
- [ ] master 已推送远程
