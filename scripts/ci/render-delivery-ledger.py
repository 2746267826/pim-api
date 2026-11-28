#!/usr/bin/env python3
"""Render the current PIM delivery ledger view from the structured source.

The Markdown output is generated; never edit it by hand. All state lives in
docs/product/delivery/*.json (ledger.json is the entry point).
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT_SENTINEL = "GENERATED FILE - DO NOT EDIT"
STATUS_ORDER = ["PASS", "FAIL", "NOT-VERIFIED", "BLOCKED", "APPROVED-EXCEPTION"]
STATUS_LABEL = {
    "PASS": "PASS",
    "FAIL": "FAIL",
    "NOT-VERIFIED": "NOT-VERIFIED",
    "BLOCKED": "BLOCKED",
    "APPROVED-EXCEPTION": "APPROVED-EXCEPTION",
}
ROLE_LABEL = {
    "required": "必需",
    "read_only": "只读",
    "coordination": "协调",
    "not_applicable": "—",
}
PLATFORM_LABEL = {
    "api": "API",
    "mcp": "MCP",
    "web": "Web",
    "android": "Android",
    "windows": "Windows",
    "release": "Release",
}
FLOW_LABEL = {
    "passed": "通过（current）",
    "passed_legacy": "历史通过（stale）",
    "failed": "失败",
    "not-run": "未执行",
    "blocked": "阻塞",
}


def load(root: Path, name: str) -> dict:
    data = json.loads((root / "docs/product/delivery" / name).read_text(encoding="utf-8"))
    return data if isinstance(data, dict) else {}


def rows_by_id(data: dict, key: str):
    rows = data.get(key)
    if not isinstance(rows, list):
        return {}
    return {
        str(row.get("id")): row
        for row in rows
        if isinstance(row, dict) and isinstance(row.get("id"), str)
    }


def esc(value: str) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ").strip()


def count_key(value):
    return value if isinstance(value, str) else "<invalid>"


def short_list(values, limit=None):
    values = [str(v) for v in values if v is not None and str(v)]
    if limit and len(values) > limit:
        return ", ".join(values[:limit]) + f" 等 {len(values)} 项"
    return "、".join(values) if values else "—"


def platform_summary(roles: dict) -> str:
    parts = []
    if not isinstance(roles, dict):
        roles = {}
    for platform in ("api", "mcp", "web", "android", "windows", "release"):
        role = roles.get(platform, "not_applicable")
        if role == "required":
            parts.append(PLATFORM_LABEL[platform])
        elif role == "read_only":
            parts.append(f"{PLATFORM_LABEL[platform]}(只读)")
        elif role == "coordination":
            parts.append(f"{PLATFORM_LABEL[platform]}(协调)")
    return "；".join(parts) if parts else "—"


def implementation_basis_label(item: dict) -> str:
    basis = str(item.get("implementation_basis", "")).strip()
    if basis.startswith("legacy-snapshot-"):
        return "旧台账快照（非 current）"
    if basis.startswith("current-"):
        return "当前复核"
    return "未识别"


def acceptance_summary(item: dict) -> str:
    acs = item.get("acceptance_criteria") or []
    if not isinstance(acs, list):
        acs = []
    verified = sum(1 for ac in acs if isinstance(ac, dict) and ac.get("verified"))
    blocked = sum(1 for ac in acs if isinstance(ac, dict) and ac.get("blocked"))
    summary = f"verified {verified}/{len(acs)}"
    if blocked:
        summary += f"；blocked {blocked}"
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--output", default=None)
    args = parser.parse_args()

    root = Path(args.root).resolve()
    ledger = load(root, "ledger.json")
    prs = rows_by_id(load(root, "prs.json"), "prs")
    runs = rows_by_id(load(root, "ci-runs.json"), "runs")
    flows = rows_by_id(load(root, "real-flows.json"), "flows")
    evidence = rows_by_id(load(root, "evidence.json"), "evidence")
    decisions = rows_by_id(load(root, "decisions.json"), "decisions")

    raw_items = ledger.get("items")
    items = [i for i in raw_items if isinstance(i, dict)] if isinstance(raw_items, list) else []
    item_by_id = {str(i.get("id")): i for i in items if isinstance(i.get("id"), str)}
    scope = ledger.get("scope") if isinstance(ledger.get("scope"), dict) else {}
    snapshot = ledger.get("source_snapshot") if isinstance(ledger.get("source_snapshot"), dict) else {}
    batch_rows = ledger.get("batches")
    batches = batch_rows if isinstance(batch_rows, list) else []

    out = []
    w = out.append

    w(f"<!-- {ROOT_SENTINEL} -->")
    w("<!-- 由 `scripts/ci/render-delivery-ledger.py` 从 `docs/product/delivery/` 生成；请修改结构化源，不要手工编辑本文件。 -->")
    w("")
    w("# PIM 全量交付台账（当前视图）")
    w("")
    w(f"> 生成视图 / Generated view。结构化事实源：")
    w(f"> - `docs/product/delivery/ledger.json`（161 项主表）")
    w(f"> - `docs/product/delivery/prs.json`、`ci-runs.json`、`real-flows.json`、`evidence.json`、`decisions.json`")
    w(f"> - 规则说明：`docs/product/delivery/README.md`")
    w("")
    w(f"数据快照：`{snapshot.get('repo', '—')}@{snapshot.get('commit', '—')}`"
      f"（{snapshot.get('commit_date', '—')}）。迁移时间 {snapshot.get('migrated_at', '—')}。")
    w("")
    w(f"{snapshot.get('note', '—')}")
    w("")

    status_counts = Counter(count_key(i.get("status")) for i in items)
    freshness_counts = Counter(count_key(i.get("freshness")) for i in items)

    w("## 摘要 / Summary")
    w("")
    w(f"- 范围：交付包 A–R、Z 共 {scope.get('core_total', '—')} 项，加补充项 S01，"
      f"共 **{scope.get('total', '—')}** 项。")
    w(f"- 状态：PASS {status_counts.get('PASS', 0)}，FAIL {status_counts.get('FAIL', 0)}，"
      f"NOT-VERIFIED {status_counts.get('NOT-VERIFIED', 0)}，BLOCKED {status_counts.get('BLOCKED', 0)}，"
      f"APPROVED-EXCEPTION {status_counts.get('APPROVED-EXCEPTION', 0)}。")
    w(f"- Freshness：current {freshness_counts.get('current', 0)}，stale {freshness_counts.get('stale', 0)}。"
      " current 表示已按当前 head 重新复核；stale 仍只能用于定位历史缺口。")
    evidence_by_type = Counter(count_key(e.get("type")) for e in evidence.values())
    evidence_type_text = "、".join(
        f"{label} {evidence_by_type.get(kind, 0)}"
        for kind, label in (
            ("audit-note", "审计记录"),
            ("ci", "CI"),
            ("real-flow", "真实流程"),
            ("decision", "决议"),
        )
    )
    w(f"- 证据：{len(evidence)} 条（{evidence_type_text}；分类合计 "
      f"{sum(evidence_by_type.values())}）。")
    w(f"- 真实流程：{len(flows)} 条记录，其中 current 通过 "
      f"{sum(1 for f in flows.values() if f.get('result') == 'passed')} 条。")
    item_acs = [
        acs for item in items
        if isinstance((acs := item.get("acceptance_criteria")), list)
    ]
    ac_total = sum(len(acs) for acs in item_acs)
    ac_verified = sum(
        1 for item in items
        for ac in (
            item.get("acceptance_criteria")
            if isinstance(item.get("acceptance_criteria"), list) else []
        )
        if isinstance(ac, dict) and ac.get("verified")
    )
    w(f"- AC：{ac_total} 条，verified {ac_verified} 条。")
    w(f"- PR：{len(prs)} 条，merged {sum(1 for p in prs.values() if p.get('state') == 'merged')}，"
      f"open {sum(1 for p in prs.values() if p.get('state') == 'open')}，"
      f"draft {sum(1 for p in prs.values() if p.get('state') == 'draft')}。")
    w("")
    w(f"本视图的产品功能 `PASS` 为 {status_counts.get('PASS', 0)}。"
      "`PASS` 只表示逐 AC 的 current E4/E5/E6 真实流程证据与平台覆盖均闭合；"
      "CI 绿、合并状态或 legacy 文本不构成验收。")
    w("")

    w("## 状态计数 / Status counts")
    w("")
    w("| 状态 | 数量 |")
    w("| --- | ---: |")
    for status in STATUS_ORDER:
        w(f"| {STATUS_LABEL[status]} | {status_counts.get(status, 0)} |")
    w(f"| **合计** | **{sum(status_counts.values())}** |")
    w("")
    w("| ID 前缀 | 范围 | 项数 |")
    w("| --- | --- | ---: |")
    groups = [
        ("A", "笔记列表与组织"), ("B", "编辑器"), ("C", "录音"), ("D", "手写"),
        ("E", "材料与文件输入"), ("F", "上传与同步"), ("G", "ASR 与转写"),
        ("H", "多模态整理"), ("I", "任务与日程建议"), ("J", "AI 配置与调用"),
        ("K", "文件与云盘"), ("L", "Android 原生客户端"), ("M", "Windows 原生客户端"),
        ("N", "Web 客户端"), ("O", "视觉与体验"), ("P", "采集与定位"),
        ("Q", "基础服务"), ("R", "MCP / API"), ("S", "Release 与发布安全（补充项）"),
        ("Z", "反向要求"),
    ]
    for prefix, label in groups:
        prefix_counts = scope.get("counts_by_prefix")
        if not isinstance(prefix_counts, dict):
            prefix_counts = {}
        w(f"| {prefix} | {label} | {prefix_counts.get(prefix, 0)} |")
    w(f"| **合计** | A–R、Z + S01 | **{scope.get('total', '—')}** |")
    w("")

    w("## 平台矩阵 / Platform matrix")
    w("")
    w("| 平台 | 必需 required | 只读 read_only | 协调 coordination | 不适用 not_applicable |")
    w("| --- | ---: | ---: | ---: | ---: |")
    for platform in (
        ledger.get("platforms") if isinstance(ledger.get("platforms"), list) else PLATFORM_LABEL
    ):
        counts = Counter(
            count_key(
                (i.get("platform_roles") or {}).get(platform)
                if isinstance(i.get("platform_roles"), dict) else None
            )
            for i in items
        )
        platform_label = PLATFORM_LABEL.get(str(platform), str(platform))
        w(f"| {platform_label} | {counts.get('required', 0)} | {counts.get('read_only', 0)} | "
          f"{counts.get('coordination', 0)} | {counts.get('not_applicable', 0)} |")
    w("")
    w("角色含义：`required` 必须在当前 head 验收；`read_only` 只消费不产出；"
      "`coordination` 只做契约/协调；`not_applicable` 不在范围内。")
    w("")

    w("## 批次 / Batches")
    w("")
    w("| 批次 | 主题 | 项数 | 状态 | 关闭条件 |")
    w("| --- | --- | ---: | --- | --- |")
    for batch in batches:
        if not isinstance(batch, dict):
            continue
        close_criteria = batch.get("close_criteria")
        if not isinstance(close_criteria, list):
            close_criteria = []
        batch_items = batch.get("items")
        if not isinstance(batch_items, list):
            batch_items = []
        w(f"| {batch.get('id', '—')} | {esc(batch.get('title', '—'))} | {len(batch_items)} | "
          f"{batch.get('status', '—')} | {esc('；'.join(str(c) for c in close_criteria))} |")
    w("")
    for batch in batches:
        if not isinstance(batch, dict):
            continue
        batch_items = batch.get("items")
        if not isinstance(batch_items, list):
            batch_items = []
        status = Counter(
            count_key(item_by_id[str(item_id)].get("status"))
            for item_id in batch_items
            if str(item_id) in item_by_id
        )
        w(f"### {batch.get('id', '—')} {batch.get('title', '—')}")
        w("")
        w(f"目标：{batch.get('goal', '—')}")
        w("")
        w("状态分布：" + "，".join(f"{s} {status.get(s, 0)}" for s in STATUS_ORDER))
        w("")
        w("关闭条件：")
        close_criteria = batch.get("close_criteria")
        if not isinstance(close_criteria, list):
            close_criteria = []
        for criterion in close_criteria:
            w(f"- {criterion}")
        w("")

    w("## 全量表 / Full table")
    w("")
    w("`implementation` 与 `legacy_status` 保留旧台账原文（历史基线），不代表当前 head 已完成实现；"
      "`实现依据` 会明确标出 legacy 快照，不能把旧文本冒充 current implementation。"
      "`NOT-VERIFIED` 表示当前 head 尚未验收，而不是“已经完成”。")
    w("")
    w("| 编号 | 标题 | 批次 | 平台角色 | 状态 | Freshness | 实现依据 | AC | PR | 证据 | 下一工单 |")
    w("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for item in items:
        item_prs = item.get("prs")
        if not isinstance(item_prs, list):
            item_prs = []
        item_evidence = item.get("evidence")
        if not isinstance(item_evidence, list):
            item_evidence = []
        prs_text = short_list([
            f"#{prs[p].get('number', '—')}" if p in prs else p
            for p in item_prs if isinstance(p, str)
        ], limit=4)
        ev_text = short_list(item_evidence, limit=3)
        w(f"| {item.get('id', '—')} | {esc(item.get('title', '—'))} | {item.get('batch', '—')} | "
          f"{esc(platform_summary(item.get('platform_roles')))} | {item.get('status', '—')} | "
          f"{item.get('freshness', '—')} | "
          f"{esc(implementation_basis_label(item))} | {esc(acceptance_summary(item))} | "
          f"{esc(prs_text)} | {esc(ev_text)} | {esc(item.get('next', '—'))} |")
    w("")

    w("## 阻塞与例外 / Blocked and exceptions")
    w("")
    w("| 编号 | 状态 | 原因 | 解除 / 批准 |")
    w("| --- | --- | --- | --- |")
    for item in items:
        if item.get("status") == "BLOCKED":
            unblock_conditions = item.get("unblock_conditions")
            if not isinstance(unblock_conditions, list):
                unblock_conditions = []
            w(f"| {item.get('id', '—')} | BLOCKED | {esc(item.get('blocked_reason', '—'))} | "
              f"{esc('；'.join(str(c) for c in unblock_conditions))} |")
        elif item.get("status") == "APPROVED-EXCEPTION":
            exc = item.get("exception")
            if not isinstance(exc, dict):
                exc = {}
            approval = f"{exc.get('approved_by', '—')} / {exc.get('approved_in', '—')}"
            w(f"| {item.get('id', '—')} | APPROVED-EXCEPTION | {esc(exc.get('rationale', '—'))} | {esc(approval)} |")
    w("")
    w("### AC 级阻塞 / Blocked acceptance criteria")
    w("")
    blocked_acs = [
        (item, ac)
        for item in items
        for ac in (item.get("acceptance_criteria") if isinstance(item.get("acceptance_criteria"), list) else [])
        if isinstance(ac, dict) and ac.get("blocked")
    ]
    if blocked_acs:
        w("| 条目 | 验收条件 | 条件 | 原因 | 解除条件 | 决策 |")
        w("| --- | --- | --- | --- | --- | --- |")
        for item, ac in blocked_acs:
            blocked = ac.get("blocked")
            if not isinstance(blocked, dict):
                blocked = {}
            unblock = blocked.get("unblock")
            if not isinstance(unblock, list):
                unblock = []
            w(f"| {item.get('id', '—')} | {ac.get('id', '—')} | {esc(ac.get('text', '—'))} | "
              f"{esc(blocked.get('reason', '—'))} | "
              f"{esc('；'.join(str(c) for c in unblock))} | {esc(blocked.get('decision_id', '—'))} |")
    else:
        w("无 / None")
    w("")

    w("## PR 索引 / PR index")
    w("")
    w("| PR | 仓库 | 状态 | head | merge | 状态 freshness | 观测时点 | 引用项 |")
    w("| --- | --- | --- | --- | --- | --- | --- | --- |")
    def pr_sort_key(pr):
        try:
            number = int(str(pr.get("number", "")))
        except ValueError:
            number = -1
        return str(pr.get("repo", "")), number

    for pr in sorted(prs.values(), key=pr_sort_key):
        pr_id = pr.get("id")
        used = []
        for item in items:
            item_prs = item.get("prs")
            if isinstance(item_prs, list) and pr_id in item_prs:
                used.append(str(item.get("id", "—")))
        w(f"| [#{pr.get('number', '—')}]({pr.get('url', '—')}) | {pr.get('repo', '—')} | "
          f"{pr.get('state', '—')} | `{pr.get('head') or '—'}` | `{pr.get('merge_commit') or '—'}` | "
          f"{pr.get('state_freshness', '—')} | "
          f"{esc(pr.get('observed_at', '—'))} | {esc(short_list(used, limit=6))} |")
    w("")

    w("## CI 索引 / CI index")
    w("")
    w("| Run | PR | head | 结论 | 关键 job | freshness | 观测时点 |")
    w("| --- | --- | --- | --- | --- | --- | --- |")
    def run_sort_key(run):
        try:
            return int(str(run.get("run_id", "")))
        except ValueError:
            return -1
    for run in sorted(runs.values(), key=run_sort_key, reverse=True):
        jobs = run.get("jobs")
        if not isinstance(jobs, list):
            jobs = []
        w(f"| `{run.get('run_id', '—')}` | {run.get('pr', '—')} | `{run.get('head', '—')}` | "
          f"{run.get('conclusion', '—')} | {esc(', '.join(str(j) for j in jobs))} | "
          f"{run.get('freshness', '—')} | {esc(run.get('observed_at', '—'))} |")
    w("")

    w("## 真实流程索引 / Real-flow index")
    w("")
    w("| 流程 | 证据 | 覆盖项 | AC | 平台 | 结果 | freshness | 观测时点 | 环境 | 边界 |")
    w("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for flow in flows.values():
        flow_items = flow.get("items")
        if not isinstance(flow_items, list):
            flow_items = []
        flow_ac_ids = flow.get("ac_ids")
        if not isinstance(flow_ac_ids, list):
            flow_ac_ids = []
        flow_platforms = flow.get("platforms")
        if not isinstance(flow_platforms, list):
            flow_platforms = []
        result = flow.get("result") if isinstance(flow.get("result"), str) else None
        w(f"| {flow.get('id', '—')} | {flow.get('evidence', '—')} | "
          f"{esc(short_list(flow_items))} | {esc(short_list(flow_ac_ids))} | "
          f"{esc(short_list([PLATFORM_LABEL.get(p, p) if isinstance(p, str) else str(p) for p in flow_platforms]))} | "
          f"{FLOW_LABEL.get(result, result or '—')} | {flow.get('freshness', '—')} | "
          f"{esc(flow.get('observed_at', '—'))} | {esc(flow.get('environment') or '—')} | "
          f"{esc(flow.get('limits', '—'))} |")
    w("")

    w("## 决策索引 / Decision index")
    w("")
    w("| 决定 | 类型 | 状态 | 批准人 | 涉及项 | 绑定 AC | 依据 |")
    w("| --- | --- | --- | --- | --- | --- | --- |")
    for dec in decisions.values():
        dec_items = dec.get("items")
        if not isinstance(dec_items, list):
            dec_items = []
        dec_ac_ids = dec.get("ac_ids")
        if not isinstance(dec_ac_ids, list):
            dec_ac_ids = []
        w(f"| {dec.get('id', '—')} | {dec.get('type', '—')} | {dec.get('status', '—')} | "
          f"{dec.get('approved_by') or '—'} | {esc(short_list(dec_items))} | "
          f"{esc(short_list(dec_ac_ids))} | {esc(dec.get('source', '—'))} |")
    w("")

    w("## 证据等级 / Evidence levels")
    w("")
    w("| 等级 | 含义 | current | stale | not-verified | unknown | 合计 |")
    w("| --- | --- | ---: | ---: | ---: | ---: | ---: |")
    level_counts = Counter(count_key(e.get("level")) for e in evidence.values())
    evidence_levels = ledger.get("evidence_levels")
    if not isinstance(evidence_levels, dict):
        evidence_levels = {}
    for level, meaning in evidence_levels.items():
        rows = [e for e in evidence.values() if e.get("level") == level]
        fresh = Counter(count_key(e.get("freshness")) for e in rows)
        w(f"| {level} | {esc(meaning)} | {fresh.get('current', 0)} | {fresh.get('stale', 0)} | "
          f"{fresh.get('not-verified', 0)} | {fresh.get('unknown', 0)} | {level_counts.get(level, 0)} |")
    w("")
    w("等级条数是存量统计，不等于 current 通过证据；必须结合 freshness 列阅读，")
    w("`stale`/`not-verified`/`unknown` 均不能支撑 PASS。")
    w("")

    w("## 续跑协议 / Resume protocol")
    w("")
    w("1. 先更新 `source_snapshot`，记录当前 `origin/master` SHA、commit date 与观测时点；旧 head 只能用于定位。")
    w("2. 只改结构化源：item、真实流程、证据、PR、决策必须双向引用，并重算 scope 计数、批次状态与视图。")
    w("3. `PASS` 需要全部 AC 已验证，每条 AC 都有 current E4/E5/E6 真实流程证据，且覆盖全部 required/read_only 平台。")
    w("4. 真实流程覆盖从 `real-flows.json` 的 `evidence` 引用反向推导；不得用未声明字段或伪造证据。")
    w("5. `BLOCKED`/AC 级阻塞必须写原因、解除条件与 pending decision；解除后改回 FAIL/NOT-VERIFIED。")
    w("6. `APPROVED-EXCEPTION` 必须引用 `approved` decision，且不得把验收条件标为 verified。")
    w("7. 旧证据保持 `stale`，legacy implementation 文本必须标为旧快照，不得冒充 current implementation。")
    w("")
    w("---")
    w("")
    w("验证命令：")
    w("")
    w("```bash")
    w("python3 scripts/ci/render-delivery-ledger.py")
    w("python3 scripts/ci/validate-delivery-ledger.py")
    w("python3 scripts/ci/test-validate-delivery-ledger.py")
    w("```")
    w("")
    w("脚本按“重渲染 → 校验 → 聚焦测试”执行；单独运行 validator 时会拒绝未同步的生成视图。")

    text = "\n".join(out) + "\n"
    rendered_view = ledger.get("rendered_view")
    if not isinstance(rendered_view, dict):
        rendered_view = {}
    output = Path(args.output) if args.output else root / rendered_view.get(
        "path", "docs/product/pim-delivery-ledger.md")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8")
    print(f"rendered {output} ({len(items)} items, {len(text.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (AttributeError, KeyError, TypeError, ValueError) as exc:
        print(
            f"delivery ledger render failed: malformed data: "
            f"{type(exc).__name__}: {exc}",
            file=sys.stderr,
        )
        sys.exit(1)
