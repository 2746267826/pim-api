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
    return json.loads((root / "docs/product/delivery" / name).read_text(encoding="utf-8"))


def esc(value: str) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ").strip()


def short_list(values, limit=None):
    values = [v for v in values if v]
    if limit and len(values) > limit:
        return ", ".join(values[:limit]) + f" 等 {len(values)} 项"
    return "、".join(values) if values else "—"


def platform_summary(roles: dict) -> str:
    parts = []
    for platform in ("api", "mcp", "web", "android", "windows", "release"):
        role = roles.get(platform, "not_applicable")
        if role == "required":
            parts.append(PLATFORM_LABEL[platform])
        elif role == "read_only":
            parts.append(f"{PLATFORM_LABEL[platform]}(只读)")
        elif role == "coordination":
            parts.append(f"{PLATFORM_LABEL[platform]}(协调)")
    return "；".join(parts) if parts else "—"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--output", default=None)
    args = parser.parse_args()

    root = Path(args.root).resolve()
    ledger = load(root, "ledger.json")
    prs = {p["id"]: p for p in load(root, "prs.json")["prs"]}
    runs = {r["id"]: r for r in load(root, "ci-runs.json")["runs"]}
    flows = {f["id"]: f for f in load(root, "real-flows.json")["flows"]}
    evidence = {e["id"]: e for e in load(root, "evidence.json")["evidence"]}
    decisions = {d["id"]: d for d in load(root, "decisions.json")["decisions"]}

    items = ledger["items"]
    scope = ledger["scope"]
    snapshot = ledger["source_snapshot"]
    batches = ledger["batches"]

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
    w(f"数据快照：`{snapshot['repo']}@{snapshot['commit']}`（{snapshot['commit_date']}）。"
      f"迁移时间 {snapshot['migrated_at']}。")
    w("")
    w(f"{snapshot['note']}")
    w("")

    status_counts = Counter(i["status"] for i in items)
    freshness_counts = Counter(i["freshness"] for i in items)

    w("## 摘要 / Summary")
    w("")
    w(f"- 范围：交付包 A–R、Z 共 {scope['core_total']} 项，加补充项 S01，共 **{scope['total']}** 项。")
    w(f"- 状态：PASS {status_counts.get('PASS', 0)}，FAIL {status_counts.get('FAIL', 0)}，"
      f"NOT-VERIFIED {status_counts.get('NOT-VERIFIED', 0)}，BLOCKED {status_counts.get('BLOCKED', 0)}，"
      f"APPROVED-EXCEPTION {status_counts.get('APPROVED-EXCEPTION', 0)}。")
    w(f"- Freshness：current {freshness_counts.get('current', 0)}，stale {freshness_counts.get('stale', 0)}。"
      " 所有迁移项均为 stale：旧台账证据没有被重新认定为当前通过。")
    w(f"- 证据：{len(evidence)} 条（CI {sum(1 for e in evidence.values() if e['type'] == 'ci')}、"
      f"真实流程 {sum(1 for e in evidence.values() if e['type'] == 'real-flow')}、"
      f"决议 {sum(1 for e in evidence.values() if e['type'] == 'decision')}）。")
    w(f"- 真实流程：{len(flows)} 条记录，其中 current 通过 "
      f"{sum(1 for f in flows.values() if f['result'] == 'passed')} 条。")
    w(f"- PR：{len(prs)} 条，merged {sum(1 for p in prs.values() if p['state'] == 'merged')}，"
      f"open {sum(1 for p in prs.values() if p['state'] == 'open')}。")
    w("")
    w("本视图不宣称任何产品功能完成；`PASS 0` 表示当前结构中没有可用的 current 端到端证据。")
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
        w(f"| {prefix} | {label} | {scope['counts_by_prefix'].get(prefix, 0)} |")
    w(f"| **合计** | A–R、Z + S01 | **{scope['total']}** |")
    w("")

    w("## 平台矩阵 / Platform matrix")
    w("")
    w("| 平台 | 必需 required | 只读 read_only | 协调 coordination | 不适用 not_applicable |")
    w("| --- | ---: | ---: | ---: | ---: |")
    for platform in ledger["platforms"]:
        counts = Counter(i["platform_roles"].get(platform) for i in items)
        w(f"| {PLATFORM_LABEL[platform]} | {counts.get('required', 0)} | {counts.get('read_only', 0)} | "
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
        w(f"| {batch['id']} | {esc(batch['title'])} | {len(batch['items'])} | {batch['status']} | "
          f"{esc('；'.join(batch['close_criteria']))} |")
    w("")
    for batch in batches:
        status = Counter(next(i for i in items if i["id"] == item_id)["status"] for item_id in batch["items"])
        w(f"### {batch['id']} {batch['title']}")
        w("")
        w(f"目标：{batch['goal']}")
        w("")
        w("状态分布：" + "，".join(f"{s} {status.get(s, 0)}" for s in STATUS_ORDER))
        w("")
        w("关闭条件：")
        for criterion in batch["close_criteria"]:
            w(f"- {criterion}")
        w("")

    w("## 全量表 / Full table")
    w("")
    w("`implementation` 与 `legacy_status` 保留旧台账原文（历史基线），不代表当前 head 已完成实现；"
      "当前判断以 `status`、`freshness` 与证据为准。")
    w("")
    w("| 编号 | 标题 | 批次 | 平台角色 | 状态 | Freshness | PR | 证据 | 下一工单 |")
    w("| --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for item in items:
        prs_text = short_list([f"#{prs[p]['number']}" if p in prs else p for p in item["prs"]], limit=4)
        ev_text = short_list(item["evidence"], limit=3)
        w(f"| {item['id']} | {esc(item['title'])} | {item['batch']} | "
          f"{esc(platform_summary(item['platform_roles']))} | {item['status']} | {item['freshness']} | "
          f"{esc(prs_text)} | {esc(ev_text)} | {esc(item['next'])} |")
    w("")

    w("## 阻塞与例外 / Blocked and exceptions")
    w("")
    w("| 编号 | 状态 | 原因 | 解除 / 批准 |")
    w("| --- | --- | --- | --- |")
    for item in items:
        if item["status"] == "BLOCKED":
            w(f"| {item['id']} | BLOCKED | {esc(item['blocked_reason'])} | "
              f"{esc('；'.join(item['unblock_conditions']))} |")
        elif item["status"] == "APPROVED-EXCEPTION":
            exc = item["exception"]
            approval = f"{exc['approved_by']} / {exc['approved_in']}"
            w(f"| {item['id']} | APPROVED-EXCEPTION | {esc(exc['rationale'])} | {esc(approval)} |")
    w("")

    w("## PR 索引 / PR index")
    w("")
    w("| PR | 仓库 | 状态 | head | merge | 状态 freshness | 引用项 |")
    w("| --- | --- | --- | --- | --- | --- | --- |")
    for pr in sorted(prs.values(), key=lambda p: (p["repo"], p["number"])):
        used = [i["id"] for i in items if pr["id"] in i["prs"]]
        w(f"| [#{pr['number']}]({pr['url']}) | {pr['repo']} | {pr['state']} | "
          f"`{pr['head'] or '—'}` | `{pr['merge_commit'] or '—'}` | {pr['state_freshness']} | "
          f"{esc(short_list(used, limit=6))} |")
    w("")

    w("## CI 索引 / CI index")
    w("")
    w("| Run | PR | head | 结论 | 关键 job | freshness |")
    w("| --- | --- | --- | --- | --- | --- |")
    for run in sorted(runs.values(), key=lambda r: int(r["run_id"]), reverse=True):
        w(f"| `{run['run_id']}` | {run['pr']} | `{run['head']}` | {run['conclusion']} | "
          f"{esc(', '.join(run['jobs']))} | {run['freshness']} |")
    w("")

    w("## 真实流程索引 / Real-flow index")
    w("")
    w("| 流程 | 覆盖项 | 平台 | 结果 | freshness | 环境 | 边界 |")
    w("| --- | --- | --- | --- | --- | --- | --- |")
    for flow in flows.values():
        w(f"| {flow['id']} | {esc(short_list(flow['items']))} | "
          f"{esc(short_list([PLATFORM_LABEL[p] for p in flow['platforms']]))} | "
          f"{FLOW_LABEL.get(flow['result'], flow['result'])} | {flow['freshness']} | "
          f"{esc(flow['environment'] or '—')} | {esc(flow['limits'])} |")
    w("")

    w("## 决策索引 / Decision index")
    w("")
    w("| 决定 | 类型 | 状态 | 批准人 | 涉及项 | 依据 |")
    w("| --- | --- | --- | --- | --- | --- |")
    for dec in decisions.values():
        w(f"| {dec['id']} | {dec['type']} | {dec['status']} | {dec['approved_by'] or '—'} | "
          f"{esc(short_list(dec['items']))} | {esc(dec['source'])} |")
    w("")

    w("## 证据等级 / Evidence levels")
    w("")
    w("| 等级 | 含义 | 条数 |")
    w("| --- | ---: | ---: |")
    level_counts = Counter(e["level"] for e in evidence.values())
    for level, meaning in ledger["evidence_levels"].items():
        w(f"| {level} | {esc(meaning)} | {level_counts.get(level, 0)} |")
    w("")

    w("## 续跑协议 / Resume protocol")
    w("")
    w("1. 先更新 `source_snapshot`，记录三仓 `origin/master` SHA；旧 head 只能用于定位。")
    w("2. 只改结构化源：item 的 `status`、`freshness`、`evidence`、`prs`、`next`，然后重新渲染。")
    w("3. `PASS` 需要 current freshness 的 E4/E5/E6 证据覆盖全部 required 平台；CI 绿不算 PASS。")
    w("4. `BLOCKED` 必须写 `blocked_reason` 与 `unblock_conditions`；解除后改回 FAIL/NOT-VERIFIED。")
    w("5. `APPROVED-EXCEPTION` 必须引用 `approved` 状态的 decision，且不得把验收条件标为 verified。")
    w("6. 旧证据保持 `stale`，迁移时保留 `legacy_status` 与 `legacy_source`，不得静默改写。")
    w("")
    w("---")
    w("")
    w("验证命令：")
    w("")
    w("```bash")
    w("python3 scripts/ci/validate-delivery-ledger.py")
    w("python3 scripts/ci/render-delivery-ledger.py")
    w("python3 scripts/ci/validate-delivery-ledger.py")
    w("```")

    text = "\n".join(out) + "\n"
    output = Path(args.output) if args.output else root / ledger["rendered_view"]["path"]
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8")
    print(f"rendered {output} ({len(items)} items, {len(text.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
