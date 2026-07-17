#!/usr/bin/env python
"""
PreToolUse hook（matcher: Bash）：拦截 `openspec archive`，归档前强制校验：
1) tasks.md 全部工单已勾选 [x]
2) test-report.md 存在
未满足条件时 BLOCKED，不允许归档。

之前的问题：check_openspec_gate.py 只在写 test-report.md 之前做了 tasks.md
完整性校验，但归档动作本身（openspec archive）没有任何拦截——如果开发者手滑
在 test-report.md 生成前就手动跑了 archive 命令，规划期的 gate 完全拦不住。
这个 hook 补上"归档"这个动作本身的质量门槛。
"""
import sys
import json
import os
import re

# Windows 中文系统控制台默认代码页常是 GBK，显式 reconfigure 避免中文内容
# 导致 UnicodeDecodeError/UnicodeEncodeError。Linux/macOS 下无害。
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ARCHIVE_PATTERN = re.compile(r"(^|[;&|]\s*)openspec\s+archive\b")


def find_unchecked_tasks(tasks_path):
    if not os.path.exists(tasks_path):
        return None
    with open(tasks_path, encoding="utf-8") as f:
        content = f.read()
    content = content.replace("\r\n", "\n")  # Windows CRLF 归一化
    return re.findall(r"^\s*-\s\[ \]\s.+$", content, flags=re.MULTILINE)


def main():
    data = json.load(sys.stdin)
    command = data.get("tool_input", {}).get("command", "")

    if not ARCHIVE_PATTERN.search(command):
        sys.exit(0)

    # 从命令行里提取 change-name（`openspec archive {change-name} ...`）；
    # 拿不到就回退到 openspec/.current-change
    m = re.search(r"openspec\s+archive\s+([^\s]+)", command)
    change_id = m.group(1) if m else None
    if not change_id or change_id.startswith("-"):
        current_change_file = os.path.join(PROJECT_ROOT, "openspec/.current-change")
        if os.path.exists(current_change_file):
            with open(current_change_file, encoding="utf-8") as f:
                change_id = f.read().strip()

    if not change_id:
        print("BLOCKED: 无法确定要归档的 change-name，且没有 openspec/.current-change 记录。", file=sys.stderr)
        sys.exit(2)

    base = os.path.join(PROJECT_ROOT, f"openspec/changes/{change_id}")
    tasks_path = os.path.join(base, "tasks.md")
    test_report_path = os.path.join(base, "test-report.md")

    unchecked = find_unchecked_tasks(tasks_path)
    if unchecked is None:
        print(f"BLOCKED: 缺少 {tasks_path}，无法归档一个还没有工单拆解的变更。", file=sys.stderr)
        sys.exit(2)
    if unchecked:
        preview = "; ".join(line.strip() for line in unchecked[:5])
        print(
            f"BLOCKED: 变更 '{change_id}' 仍有 {len(unchecked)} 项未完成工单 ({preview})，禁止归档。",
            file=sys.stderr,
        )
        sys.exit(2)

    if not os.path.exists(test_report_path):
        print(f"BLOCKED: 缺少 {test_report_path}，必须先完成 QA 测试并产出测试报告再归档。", file=sys.stderr)
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()