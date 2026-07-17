#!/usr/bin/env python
"""
PreToolUse hook（matcher: Bash）：拦截 `git commit`，commit 前强制跑
`dotnet build` + `dotnet test`，任一失败则 BLOCKED，不允许 commit。

背景：matt-pocock-flow / development-flow.md 里都写了"提交前确保 dotnet build
0 错误 + 测试通过"，但这只是文字要求，之前没有任何 hook 校验，agent 完全可能
谎报或跳过。原有 PostToolUse 只挂了 `dotnet format --verify-no-changes`，
且允许失败不阻断（`|| exit 0`），起不到质量门槛的作用。

只在检测到命令是 git commit 时才触发 build/test（避免每次 Bash 调用都跑一遍
build 拖慢开发），其余 Bash 命令直接放行。

超时保护：build/test 各设 180s 超时，避免 agent 卡在挂起的进程上无限等待。
"""
import sys
import json
import subprocess
import re

# Windows 中文系统控制台默认代码页常是 GBK，显式 reconfigure 避免中文内容
# 导致 UnicodeDecodeError/UnicodeEncodeError。Linux/macOS 下无害。
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

BUILD_TIMEOUT_SEC = 180
TEST_TIMEOUT_SEC = 180

# 匹配 git commit（含 git -C <dir> commit、git commit -m "..."、复合命令里的 git commit 等）
COMMIT_PATTERN = re.compile(r"(^|[;&|]\s*)git\s+(-C\s+\S+\s+)?commit\b")


def is_git_commit(command: str) -> bool:
    return bool(COMMIT_PATTERN.search(command))


def run(cmd):
    try:
        result = subprocess.run(
            cmd, shell=True, capture_output=True,
            encoding="utf-8", errors="replace",
            timeout=BUILD_TIMEOUT_SEC if "build" in cmd else TEST_TIMEOUT_SEC,
        )
        return result.returncode, (result.stdout + result.stderr)[-4000:]
    except subprocess.TimeoutExpired:
        return -1, f"命令超时（>{BUILD_TIMEOUT_SEC}s）：{cmd}"


def main():
    data = json.load(sys.stdin)
    command = data.get("tool_input", {}).get("command", "")

    if not is_git_commit(command):
        sys.exit(0)  # 不是 git commit，放行

    build_code, build_out = run("dotnet build --nologo --verbosity quiet")
    if build_code != 0:
        print(
            "BLOCKED: dotnet build 未通过，禁止提交。请先修复编译错误再 commit。\n"
            f"---- build 输出（末尾 4000 字符）----\n{build_out}",
            file=sys.stderr,
        )
        sys.exit(2)

    test_code, test_out = run("dotnet test --nologo --verbosity quiet")
    if test_code != 0:
        print(
            "BLOCKED: dotnet test 未通过，禁止提交。请先修复失败用例，或如果测试本身有问题，"
            "先跟开发者确认再决定是否跳过。\n"
            f"---- test 输出（末尾 4000 字符）----\n{test_out}",
            file=sys.stderr,
        )
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()