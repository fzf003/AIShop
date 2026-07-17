#!/usr/bin/env python
"""
PreToolUse hook: 强制流程顺序，同时支持三种 flow-mode：

  openspec  (默认，向后兼容原行为)
      exploration.md -> proposal.md -> design.md + specs/{domain}/spec.md
      -> tasks.md -> implementation -> test-report.md

  matt-pocock (matt-pocock-flow skill 使用)
      grill 记录（可直接写进 design.md 头部，无需独立文件）
      -> design.md + specs/{domain}/spec.md -> tasks.md
      -> implementation -> test-report.md
      不要求 exploration.md / proposal.md

  quick (轻量改动快速通道，见 matt-pocock-flow SKILL.md "快速路径判定")
      不要求任何规划产出物，但实现代码仍必须来自 @implementer，
      且仍然拦截 git commit / openspec archive（见 check_commit_gate.py /
      check_archive_gate.py），避免"跳过规划"被滥用成"跳过质量门槛"。

flow-mode 通过 openspec/changes/{id}/.flow-mode 文件声明，内容为
"openspec" / "matt-pocock" / "quick" 三选一（去除首尾空白）。
文件不存在时默认 "openspec"，与历史行为保持一致，老 change 目录不受影响。

拦截 Write/Edit 工具调用，检查：
1) 该 flow-mode 下的前置文件是否存在
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

# Windows 中文系统控制台默认代码页常是 GBK，而 Claude Code 传入的 stdin JSON
# 和本脚本要输出的中文 BLOCKED 提示都是 UTF-8。不显式 reconfigure 的话，
# 遇到中文内容会直接 UnicodeDecodeError/UnicodeEncodeError 把 hook 跑崩。
# Linux/macOS 下这行是无害的空操作（本来就是 UTF-8）。
for _stream in (sys.stdin, sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

# 项目根目录：hook 可能从子目录触发（如 src/AIShop.Api/），
# 所有路径都应相对于项目根目录解析，而非 cwd。
PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

VALID_MODES = {"openspec", "matt-pocock", "quick"}


def _norm(path):
    """统一路径分隔符为正斜杠，确保跨平台路径比较一致。"""
    return path.replace("\\", "/")


def read_flow_mode(base):
    """读取 openspec/changes/{id}/.flow-mode，非法/缺失时回退为 'openspec'。"""
    flow_file = f"{base}/.flow-mode"
    if not os.path.exists(flow_file):
        return "openspec"
    with open(flow_file, encoding="utf-8") as f:
        mode = f.read().strip()
    return mode if mode in VALID_MODES else "openspec"


# 每类文件对应"必须由哪个 subagent 来写"。matt-pocock/quick 模式复用同一批
# subagent（spec-writer 负责 design+spec，task-breaker 负责拆工单，
# tester 负责测试报告），不需要单独定义一套 agent 角色。
EXPECTED_AGENT = {
    "proposal.md": "proposal-writer",
    "design.md": "spec-writer",
    "delta_spec": "spec-writer",   # openspec/changes/{id}/specs/{domain}/spec.md
    "tasks.md": "task-breaker",
    "test-report.md": "tester",
}


def find_unchecked_tasks(tasks_path):
    """解析 tasks.md，返回未完成任务（'- [ ] ...' 格式）的行列表。文件不存在时返回 None。"""
    if not os.path.exists(tasks_path):
        return None
    with open(tasks_path, encoding="utf-8") as f:
        content = f.read()
    content = content.replace("\r\n", "\n")  # Windows CRLF 归一化，避免 \r 混进匹配结果
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


def required_planning_docs(base, mode):
    """按 flow-mode 返回该阶段应存在的规划前置文件列表（不含 delta spec，单独检查）。"""
    tasks_path = f"{base}/tasks.md"
    design_path = f"{base}/design.md"
    proposal_path = f"{base}/proposal.md"
    exploration_path = f"{base}/exploration.md"

    if mode == "openspec":
        return [exploration_path, proposal_path, design_path, tasks_path]
    if mode == "matt-pocock":
        # 无 exploration.md / proposal.md，grill 产出直接并入 design.md
        return [design_path, tasks_path]
    # quick：不要求任何规划文档，五件套检查直接跳过
    return []


def planning_missing(base, mode):
    """返回该 change 缺少的规划产出物列表（按 mode 决定要求哪些文件）。"""
    if mode == "quick":
        return []
    missing = [p for p in required_planning_docs(base, mode) if not os.path.exists(p)]
    if not has_any_delta_spec(base):
        missing.append(f"{base}/specs/{{domain}}/spec.md（至少一个）")
    return missing


def main():
    data = json.load(sys.stdin)
    file_path = data.get("tool_input", {}).get("file_path", "")

    # 从路径里解析 change_id，例如 openspec/changes/add-xxx/proposal.md -> add-xxx
    # 统一路径分隔符（Windows 反斜杠 → 正斜杠）。
    normalized_path = file_path.replace("\\", "/")
    m = re.search(r"openspec/changes/([^/]+)/", normalized_path)
    change_id = m.group(1) if m else None

    # 与 design_path / tasks_path 等目标路径统一按"绝对路径"比较。
    # 注意：这里不能再把 normalized_path 裁剪成从 'openspec/' 开始的相对路径
    # 去跟下面基于 PROJECT_ROOT 拼出来的绝对路径比较——那样两边永远不相等，
    # 会导致规则 0-5 全部失效、所有写入都落到最严格的规则 6（历史遗留 bug）。
    # 如果 file_path 本身是相对路径（例如从子目录以相对路径调用），
    # 统一转成基于 PROJECT_ROOT 的绝对路径再比较。
    if not os.path.isabs(normalized_path):
        normalized_path = _norm(os.path.join(PROJECT_ROOT, normalized_path))
    rel_path = normalized_path

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
        mode = read_flow_mode(base)
        missing = planning_missing(base, mode)
        if missing:
            print(
                f"BLOCKED: 变更 '{active_id}'（flow-mode={mode}）缺少前置产出物 {missing}，"
                f"必须先完成规划阶段",
                file=sys.stderr,
            )
            sys.exit(2)

        # 规划齐全（或 quick 模式豁免），实现代码必须来自 @implementer——
        # 三种 mode 都不例外，这是质量门槛，不是流程仪式，quick 模式也不能绕过。
        reason = check_agent_identity(data, normalized_path, "implementer")
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    base = _norm(os.path.join(PROJECT_ROOT, f"openspec/changes/{change_id}"))
    mode = read_flow_mode(base)
    exploration_path = f"{base}/exploration.md"
    proposal_path = f"{base}/proposal.md"
    design_path = f"{base}/design.md"
    tasks_path = f"{base}/tasks.md"
    test_report_path = f"{base}/test-report.md"

    # .flow-mode 本身自由写入（这是 Step 0 的第一个动作，不能有前置依赖）
    if rel_path == f"{base}/.flow-mode":
        sys.exit(0)

    # 规则 0：exploration.md 自由写入（openspec 模式流程起点，无前置依赖）
    if rel_path == exploration_path:
        sys.exit(0)

    # 规则 1：写 proposal.md 前，必须先有 exploration.md，且调用方必须是 @proposal-writer
    # （matt-pocock / quick 模式不产出 proposal.md，这条规则不会被触发到）
    if rel_path == proposal_path:
        if not os.path.exists(exploration_path):
            print(f"BLOCKED: 必须先完成 Explore 调研阶段 (缺少 {exploration_path})", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["proposal.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 2：写 design.md 前的前置条件按 mode 分支：
    #   openspec  -> 必须先有 proposal.md
    #   matt-pocock/quick -> 无前置文件要求（grill 产出直接写进 design.md）
    if rel_path == design_path:
        if mode == "openspec" and not os.path.exists(proposal_path):
            print(f"BLOCKED: 缺少 {proposal_path}，必须先完成提案阶段", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["design.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 3：写 delta spec (specs/{domain}/spec.md) 前的前置条件同样按 mode 分支
    if re.match(rf"^{re.escape(base)}/specs/[^/]+/spec\.md$", rel_path):
        if mode == "openspec" and not os.path.exists(proposal_path):
            print(f"BLOCKED: 缺少 {proposal_path}，必须先完成提案阶段", file=sys.stderr)
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["delta_spec"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 4：写 tasks.md 前，必须先有 design.md 和至少一个 delta spec，调用方必须是 @task-breaker
    # （quick 模式也要求，工单拆解不是重量级步骤，跳过它没有实际收益，反而丢失 blocking edges 信息）
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
                f"BLOCKED: 变更 '{change_id}' 仍有 {len(unchecked)} 项未完成任务 ({preview})，"
                f"无法进入验证阶段",
                file=sys.stderr,
            )
            sys.exit(2)
        reason = check_agent_identity(data, rel_path, EXPECTED_AGENT["test-report.md"])
        if reason:
            print(reason, file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    # 规则 6：同一 change 目录下的其他杂项文件（如 handoffs/handoff-*.md），
    # 保守起见要求该 mode 下的规划产出物齐全
    missing = planning_missing(base, mode)
    if missing:
        print(f"BLOCKED: 缺少前置产出物 {missing}，必须先完成规划阶段", file=sys.stderr)
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()