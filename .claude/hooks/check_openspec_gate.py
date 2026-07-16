#!/usr/bin/env python
"""
PreToolUse hook: 强制 OpenSpec 六阶段流程顺序，适配官方 OpenSpec CLI 目录结构
(exploration -> proposal -> design + delta spec -> tasks -> implementation -> verification)

官方 OpenSpec 目录结构（openspec init 生成）：
  openspec/changes/{id}/proposal.md
  openspec/changes/{id}/design.md
  openspec/changes/{id}/specs/{domain}/spec.md   <- delta spec，domain 从路径里直接解析，不写死
  openspec/changes/{id}/tasks.md
以下是我们自己在此基础上补充的产出物（官方 schema 不含，属于自定义扩展）：
  openspec/changes/{id}/exploration.md           <- STEP 0 调研
  openspec/changes/{id}/test-report.md           <- STEP 5 验证报告

拦截 Write/Edit 工具调用，检查：
1) 前置文件是否存在
2) 这次调用是不是由"该阶段指定的 subagent"发起的（读取 agent_type 字段）
3) 写 test-report.md 前，tasks.md 里的任务是否已全部勾选完成

agent_type 只有在 hook 于 subagent 内部触发时才会被填充；主对话（顶层 agent）
直接调用 Write/Edit 时该字段为空 —— 这正是我们用来分辨"是否跳过了 subagent"的依据。
"""
import sys
import json
import os
import re
import glob

# 项目根目录：hook 可能从子目录触发（如 src/AIShop.Api/），
# 所有路径都应相对于项目根目录解析，而非 cwd。
PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def _norm(path):
    """统一路径分隔符为正斜杠，确保跨平台路径比较一致。"""
    return path.replace("\\", "/")

# 每类文件对应"必须由哪个 subagent 来写"
EXPECTED_AGENT = {
    "proposal.md": "proposal-writer",
    "design.md": "spec-writer",
    "delta_spec": "spec-writer",   # openspec/changes/{id}/specs/{domain}/spec.md
    "tasks.md": "task-breaker",
    "test-report.md": "tester",
}
# exploration.md 按 SKILL.md 约定由主对话代为写入（Explore 本身只读），不做 agent_type 校验


def find_unchecked_tasks(tasks_path):
    """解析 tasks.md，返回未完成任务（'- [ ] ...' 格式）的行列表。文件不存在时返回 None。"""
    if not os.path.exists(tasks_path):
        return None
    with open(tasks_path, encoding="utf-8") as f:
        content = f.read()
    return re.findall(r"^\s*-\s\[ \]\s.+$", content, flags=re.MULTILINE)


def has_any_delta_spec(base):
    """检查该 change 目录下是否至少存在一个 specs/{domain}/spec.md（domain 名不限）。"""
    return len(glob.glob(f"{base}/specs/*/spec.md")) > 0


def check_agent_identity(data, file_path, expected_agent_type):
    """校验调用方是否是预期的 subagent。返回 None 表示通过，否则返回拦截理由字符串。"""
    agent_type = data.get("agent_type")
    if agent_type != expected_agent_type:
        who = agent_type or "主对话（未委派给任何 subagent）"
        return (
            f"BLOCKED: {file_path} 必须由 @{expected_agent_type} 完成，"
            f"检测到实际调用方是 {who}。请改为委派给 @{expected_agent_type}，不要自己动手写。"
        )
    return None


def five_piece_missing(base):
    """返回该 change 缺少的核心产出物列表（exploration/proposal/design/delta-spec/tasks）。"""
    exploration_path = f"{base}/exploration.md"
    proposal_path = f"{base}/proposal.md"
    design_path = f"{base}/design.md"
    tasks_path = f"{base}/tasks.md"

    missing = [p for p in [exploration_path, proposal_path, design_path, tasks_path] if not os.path.exists(p)]
    if not has_any_delta_spec(base):
        missing.append(f"{base}/specs/{{domain}}/spec.md（至少一个）")
    return missing


def main():
    data = json.load(sys.stdin)
    file_path = data.get("tool_input", {}).get("file_path", "")

    # 从路径里解析 change_id，例如 openspec/changes/add-xxx/proposal.md -> add-xxx
    # 统一路径分隔符（Windows 反斜杠 → 正斜杠）
    normalized_path = file_path.replace("\\", "/")
    m = re.search(r"openspec/changes/([^/]+)/", normalized_path)
    change_id = m.group(1) if m else None

    # 提取相对路径部分（从 openspec/ 开始），用于与绝对路径转换后的相对部分比较
    # 统一使用正斜杠，避免 Windows 反斜杠导致路径比较失败
    rel_start = normalized_path.find("openspec/")
    rel_path = normalized_path[rel_start:] if rel_start >= 0 else normalized_path

    # file_path 不在 openspec/changes/{id}/ 路径下 -> 说明这是"实现代码"文件
    if not change_id:
        current_change_file = os.path.join(PROJECT_ROOT, "openspec/.current-change")
        if not os.path.exists(current_change_file):
            sys.exit(0)  # 没有活跃 change，不在管控范围内，放行

        with open(current_change_file, encoding="utf-8") as f:
            active_id = f.read().strip()
        if not active_id:
            sys.exit(0)

        base = _norm(os.path.join(PROJECT_ROOT, f"openspec/changes/{active_id}"))
        missing = five_piece_missing(base)
        if missing:
            print(f"BLOCKED: 变更 '{active_id}' 缺少前置产出物 {missing}，必须先完成 OpenSpec 规划阶段", file=sys.stderr)
            sys.exit(2)

        # 五件套齐全，说明进入了 STEP 4，实现代码必须来自 @implementer
        reason = check_agent_identity(data, normalized_path, "implementer")
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    base = _norm(os.path.join(PROJECT_ROOT, f"openspec/changes/{change_id}"))
    exploration_path = f"{base}/exploration.md"
    proposal_path = f"{base}/proposal.md"
    design_path = f"{base}/design.md"
    tasks_path = f"{base}/tasks.md"
    test_report_path = f"{base}/test-report.md"

    # 规则 0：exploration.md 自由写入（流程起点，无前置依赖）
    if rel_path == exploration_path:
        sys.exit(0)

    # 规则 1：写 proposal.md 前，必须先有 exploration.md，且调用方必须是 @proposal-writer
    if rel_path == proposal_path:
        if not os.path.exists(exploration_path):
            print(f"BLOCKED: 必须先完成 Explore 调研阶段 (缺少 {exploration_path})", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["proposal.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 2：写 design.md 前，必须先有 proposal.md，且调用方必须是 @spec-writer
    if rel_path == design_path:
        if not os.path.exists(proposal_path):
            print(f"BLOCKED: 缺少 {proposal_path}，必须先完成提案阶段", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["design.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 3：写 delta spec (specs/{domain}/spec.md) 前，必须先有 proposal.md，调用方必须是 @spec-writer
    # domain 名称从路径直接解析，不写死，任何 domain 名都适用同一条规则
    if re.match(rf"^{re.escape(base)}/specs/[^/]+/spec\.md$", rel_path):
        if not os.path.exists(proposal_path):
            print(f"BLOCKED: 缺少 {proposal_path}，必须先完成提案阶段", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["delta_spec"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 4：写 tasks.md 前，必须先有 design.md 和至少一个 delta spec，调用方必须是 @task-breaker
    if rel_path == tasks_path:
        missing = []
        if not os.path.exists(design_path):
            missing.append(design_path)
        if not has_any_delta_spec(base):
            missing.append(f"{base}/specs/{{domain}}/spec.md（至少一个）")
        if missing:
            print(f"BLOCKED: 缺少 {missing}，必须先完成设计与规范阶段", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["tasks.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 5：写 test-report.md 前，tasks.md 必须全部完成，调用方必须是 @tester
    if rel_path == test_report_path:
        unchecked = find_unchecked_tasks(tasks_path)
        if unchecked is None:
            print(f"BLOCKED: 缺少 {tasks_path}，必须先完成任务拆解与实现阶段", file=sys.stderr)
            sys.exit(2)
        if unchecked:
            preview = "; ".join(line.strip() for line in unchecked[:5])
            print(
                f"BLOCKED: 变更 '{change_id}' 仍有 {len(unchecked)} 项未完成任务 ({preview})，无法进入验证阶段",
                file=sys.stderr,
            )
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["test-report.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 6：同一 change 目录下的其他杂项文件，保守起见要求五件套齐全
    missing = five_piece_missing(base)
    if missing:
        print(f"BLOCKED: 缺少前置产出物 {missing}，必须先完成 OpenSpec 全部规划阶段", file=sys.stderr)
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()