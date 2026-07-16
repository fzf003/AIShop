#!/usr/bin/env bash
# 会话启动检查：以表格形式展示分支、改动、变更、工单
# 所有输出到 stdout 以便在终端直接显示

W=48

printf '┌─────────────────────────┬────────────────────────────────────────────────┐\n' >&1
printf '│  📋 会话启动检查        │                                                    │\n' >&1
printf '├─────────────────────────┼────────────────────────────────────────────────┤\n' >&1

# ── 分支 ──
branch="$(git branch --show-current 2>/dev/null || echo '未知')"
printf '│ 分支                    │ %-*s│\n' $W "$branch" >&1

# ── 未提交改动 ──
changes="$(git status --short | head -10)"
if [ -z "$changes" ]; then
  printf '│ 未提交改动              │ %-*s│\n' $W '无' >&1
else
  count=$(echo "$changes" | grep -c .)
  printf '│ 未提交改动（%d 条）    │ ' $count >&1
  first=true
  rest=''
  while IFS= read -r line || [ -n "$line" ]; do
    if [ "$first" = true ]; then
      printf '%-*s│\n' $W "$line" >&1
      first=false
    else
      rest="$rest"$'\n'"$line"
    fi
  done <<< "$changes"
  if [ -n "$rest" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      [ -z "$line" ] && continue
      printf '│                        │ %-*s│\n' $W "$line" >&1
    done <<< "$rest"
  fi
fi

# ── 当前变更 ──
current="$(cat openspec/.current-change 2>/dev/null || echo '无进行中的变更')"
printf '│ 当前变更                │ %-*s│\n' $W "$current" >&1

# ── 待办工单 ──
todo="$(grep -E '\[ \]' openspec/changes/*/tasks.md 2>/dev/null | head -10)"
if [ -z "$todo" ]; then
  printf '│ 待办工单                │ %-*s│\n' $W '无未完成工单' >&1
else
  count=$(echo "$todo" | grep -c .)
  first=true
  while IFS= read -r line || [ -n "$line" ]; do
    desc="$(echo "$line" | sed 's/.*\[ \] //')"
    if [ "$first" = true ]; then
      printf '│ 待办工单（%d 项）      │ %-*s│\n' $count $W "$desc" >&1
      first=false
    else
      printf '│                        │ %-*s│\n' $W "$desc" >&1
    fi
  done <<< "$todo"
fi

printf '└─────────────────────────┴────────────────────────────────────────────────┘\n' >&1
