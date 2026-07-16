#!/usr/bin/env bash
# 会话结束检查：列出未提交改动和未完成任务
# 所有输出到 stdout 以便在终端显示

W=48

printf '┌─────────────────────────┬────────────────────────────────────────────────┐\n' >&1
printf '│  🚪 会话结束检查        │                                                    │\n' >&1
printf '├─────────────────────────┼────────────────────────────────────────────────┤\n' >&1

# ── 未提交改动 ──
changes="$(git status --short | head -5)"
if [ -z "$changes" ]; then
  printf '│ 未提交改动              │ %-*s│\n' $W '✅ 工作区干净 OK' >&1
else
  count=$(echo "$changes" | grep -c .)
  printf '│ 未提交改动（%d 条）    │ ⚠️  %-*s│\n' $count $((W-4)) '有未提交的改动' >&1
fi

# ── 待办工单 ──
todo="$(grep -l '\[ \]' openspec/changes/*/tasks.md 2>/dev/null | head -3)"
if [ -z "$todo" ]; then
  printf '│ 待办工单                │ %-*s│\n' $W '✅ 所有工单已完成 OK' >&1
else
  count=$(echo "$todo" | grep -c .)
  printf '│ 待办工单（%d 项未完成）  │ ⚠️  %-*s│\n' $count $((W-3)) "⚠️  有未完成工单" >&1
fi

printf '└─────────────────────────┴────────────────────────────────────────────────┘\n' >&1
