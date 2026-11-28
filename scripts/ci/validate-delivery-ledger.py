#!/usr/bin/env python3
"""Validate the structured PIM delivery ledger without touching the network.

The ledger is the single source of truth for delivery state; the rendered
Markdown is a generated view. This validator fails closed on missing items,
unknown enums, dangling references, and any attempt to turn stale evidence into
a PASS.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ITEM_ID = re.compile(r"^[A-RSZ]\d{2}$")
AC_ID = re.compile(r"^([A-RSZ]\d{2})-AC\d{2}$")
DECISION_ID = re.compile(r"^DEC-[A-Za-z0-9-]+$")
EVIDENCE_ID = re.compile(r"^EV-[A-Za-z0-9_-]+$")
PR_ID = re.compile(r"^PR-(pim-api|pim-android|pim-windows)-(\d+)$")
CI_ID = re.compile(r"^CI-(\d+)$")
FLOW_ID = re.compile(r"^RF-[A-Za-z0-9-]+$")

EXPECTED_PREFIX_COUNTS = {
    "A": 10, "B": 16, "C": 22, "D": 8, "E": 3, "F": 11, "G": 13, "H": 11,
    "I": 4, "J": 9, "K": 4, "L": 14, "M": 6, "N": 3, "O": 6, "P": 5,
    "Q": 8, "R": 5, "S": 1, "Z": 2,
}
PLATFORMS = ["api", "mcp", "web", "android", "windows", "release"]
ROLES = {"required", "read_only", "coordination", "not_applicable"}
STATUSES = {"PASS", "FAIL", "NOT-VERIFIED", "BLOCKED", "APPROVED-EXCEPTION"}
FRESHNESS = {"current", "stale", "unknown", "not-verified"}
LEVELS = {"E0", "E1", "E2", "E3", "E4", "E5", "E6"}
PROOF_LEVELS = {"E4", "E5", "E6"}
PR_STATES = {"open", "merged", "closed", "draft"}
FLOW_RESULTS = {"passed", "passed_legacy", "failed", "not-run", "blocked"}

ITEM_FIELDS = [
    "id", "title", "batch", "platform_roles", "platform_scope",
    "acceptance_criteria", "implementation", "implementation_basis", "dependencies", "dependency_notes",
    "owner", "collaborators", "prs", "evidence", "decisions", "status",
    "freshness", "next", "legacy_status", "legacy_source",
]


class Validator:
    def __init__(self, root: Path):
        self.root = root
        self.errors: list[str] = []
        self.warnings: list[str] = []

    # -- helpers ---------------------------------------------------------
    def error(self, where: str, message: str) -> None:
        entry = f"{where}: {message}"
        if entry not in self.errors:
            self.errors.append(entry)

    def warn(self, where: str, message: str) -> None:
        self.warnings.append(f"{where}: {message}")

    def load(self, name: str) -> dict:
        path = self.root / "docs/product/delivery" / name
        if not path.exists():
            self.error(str(path), "文件不存在")
            return {}
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            self.error(str(path), f"JSON 解析失败：{exc}")
            return {}

    # -- checks ----------------------------------------------------------
    def run(self) -> bool:
        ledger = self.load("ledger.json")
        prs_doc = self.load("prs.json")
        ci_doc = self.load("ci-runs.json")
        flows_doc = self.load("real-flows.json")
        evidence_doc = self.load("evidence.json")
        decisions_doc = self.load("decisions.json")
        if not ledger:
            return self.report()

        prs = self.index(prs_doc.get("prs", []), "id", "prs.json")
        ci = self.index(ci_doc.get("runs", []), "id", "ci-runs.json")
        flows = self.index(flows_doc.get("flows", []), "id", "real-flows.json")
        evidence = self.index(evidence_doc.get("evidence", []), "id", "evidence.json")
        decisions = self.index(decisions_doc.get("decisions", []), "id", "decisions.json")

        self.check_scope(ledger)
        self.check_prs(prs)
        self.check_ci(ci, prs, evidence)
        self.check_flows(flows, evidence)
        self.check_decisions(decisions, evidence)
        self.check_evidence(evidence)
        self.check_batches(ledger, prs)
        self.check_items(ledger, prs, evidence, decisions, flows, ci)
        self.check_cross_file(ledger, evidence)
        self.check_render_view(ledger)
        return self.report()

    def index(self, rows, key, source):
        out = {}
        for row in rows:
            ident = row.get(key)
            if not ident:
                self.error(source, f"条目缺少 {key}")
                continue
            if ident in out:
                self.error(source, f"重复 {key}：{ident}")
            out[ident] = row
        return out

    def check_scope(self, ledger):
        scope = ledger.get("scope", {})
        if scope.get("total") != 161:
            self.error("ledger.json:scope", f"total 必须为 161，实际 {scope.get('total')!r}")
        if scope.get("core_total") != 160:
            self.error("ledger.json:scope", f"core_total 必须为 160，实际 {scope.get('core_total')!r}")
        if scope.get("supplemental_ids") != ["S01"]:
            self.error("ledger.json:scope", "supplemental_ids 必须恰为 ['S01']")
        counts = scope.get("counts_by_prefix")
        if counts != EXPECTED_PREFIX_COUNTS:
            self.error("ledger.json:scope",
                       f"counts_by_prefix 与固定范围不符：期望 {EXPECTED_PREFIX_COUNTS}，实际 {counts}")
        if sorted(ledger.get("platforms", [])) != sorted(PLATFORMS):
            self.error("ledger.json:platforms", f"平台列表必须为 {PLATFORMS}")
        if ledger.get("platform_role_values") != sorted(ROLES):
            self.error("ledger.json:platform_role_values", f"角色枚举必须为 {sorted(ROLES)}")
        snapshot = ledger.get("source_snapshot", {})
        for field in ("repo", "branch", "commit", "observed_at", "migrated_at"):
            if not snapshot.get(field):
                self.error("ledger.json:source_snapshot", f"缺少 {field}")
        if not ledger.get("batch_close_rules"):
            self.error("ledger.json:batch_close_rules", "批次关闭条件不能为空")

    def check_prs(self, prs):
        for pid, pr in prs.items():
            where = f"prs.json:{pid}"
            m = PR_ID.match(pid)
            if not m:
                self.error(where, "PR id 必须为 PR-<repo>-<number>")
                continue
            repo, number = m.group(1), int(m.group(2))
            if pr.get("repo") != repo:
                self.error(where, f"repo 字段 {pr.get('repo')!r} 与 id 不一致")
            if pr.get("number") != number:
                self.error(where, "number 与 id 不一致")
            expected_repo = f"2746267826/{repo}"
            if pr.get("repository") != expected_repo:
                self.error(where, f"repository 必须为 {expected_repo}")
            if pr.get("url") != f"https://github.com/{expected_repo}/pull/{number}":
                self.error(where, "url 与仓库/编号不一致")
            if pr.get("state") not in PR_STATES:
                self.error(where, f"非法 state：{pr.get('state')!r}")
            if pr.get("state_freshness") not in FRESHNESS:
                self.error(where, f"非法 state_freshness：{pr.get('state_freshness')!r}")
            if pr.get("state") == "merged":
                if not pr.get("merge_commit") and pr.get("state_freshness") == "current":
                    self.error(where, "current 的 merged PR 必须给出 merge_commit")
                if not pr.get("merge_commit") and pr.get("state_verified_by") != "legacy-ledger":
                    self.error(where, "merge_commit 未知时必须注明 state_verified_by=legacy-ledger")
            elif pr.get("merge_commit"):
                self.error(where, f"{pr.get('state')} PR 不应带 merge_commit")
            if pr.get("state_freshness") == "current" and pr.get("state_verified_by") != "git-history":
                self.error(where, "current 状态必须由 git-history 验证")

    def check_ci(self, ci, prs, evidence):
        for cid, run in ci.items():
            where = f"ci-runs.json:{cid}"
            m = CI_ID.match(cid)
            if not m:
                self.error(where, "CI id 必须为 CI-<run_id>")
                continue
            if run.get("run_id") != m.group(1):
                self.error(where, "run_id 与 id 不一致")
            if run.get("pr") not in prs:
                self.error(where, f"引用了不存在的 PR：{run.get('pr')!r}")
            else:
                pr = prs[run["pr"]]
                if pr.get("repo") != run.get("repo"):
                    self.error(where, f"repo {run.get('repo')!r} 与 PR 仓库 {pr.get('repo')!r} 不一致")
                if run.get("head_is_current") and run.get("head") != pr.get("head"):
                    self.error(where, "head_is_current=true 但 head 与当前 PR head 不一致")
            if run.get("freshness") not in FRESHNESS:
                self.error(where, f"非法 freshness：{run.get('freshness')!r}")
            if run.get("conclusion") not in {"success", "failure", "cancelled", "in_progress", "skipped"}:
                self.error(where, f"非法 conclusion：{run.get('conclusion')!r}")
            if run.get("freshness") == "stale" and run.get("head_is_current"):
                self.error(where, "stale run 不能标记为当前 head")
            if not run.get("jobs"):
                self.error(where, "jobs 不能为空")

    def check_flows(self, flows, evidence):
        for fid, flow in flows.items():
            where = f"real-flows.json:{fid}"
            if not FLOW_ID.match(fid):
                self.error(where, "真实流程 id 必须为 RF-*")
            if flow.get("result") not in FLOW_RESULTS:
                self.error(where, f"非法 result：{flow.get('result')!r}")
            if flow.get("freshness") not in FRESHNESS:
                self.error(where, f"非法 freshness：{flow.get('freshness')!r}")
            if flow.get("result") == "passed":
                if flow.get("freshness") != "current":
                    self.error(where, "passed 真实流程必须是 current freshness")
                if not flow.get("environment"):
                    self.error(where, "passed 真实流程必须记录环境")
                if not flow.get("artifacts"):
                    self.error(where, "passed 真实流程必须记录产物")
            if flow.get("result") == "passed_legacy":
                if flow.get("freshness") == "current":
                    self.error(where, "passed_legacy 表示历史通过，freshness 不能是 current")
                if not flow.get("environment"):
                    self.error(where, "passed_legacy 必须记录当时的执行环境")
            if flow.get("result") == "not-run" and flow.get("freshness") == "current":
                self.error(where, "not-run 流程不能是 current")
            if not flow.get("limits"):
                self.error(where, "必须写明未验证边界 limits")
            ev = flow.get("evidence")
            if ev not in evidence:
                self.error(where, f"悬空 evidence：{ev!r}")
            elif evidence[ev].get("type") != "real-flow":
                self.error(where, f"evidence {ev} 类型不是 real-flow")

    def check_decisions(self, decisions, evidence):
        for did, dec in decisions.items():
            where = f"decisions.json:{did}"
            if not DECISION_ID.match(did):
                self.error(where, "decision id 必须为 DEC-*")
            if dec.get("status") not in {"approved", "pending", "recorded", "rejected"}:
                self.error(where, f"非法 status：{dec.get('status')!r}")
            if not dec.get("rationale"):
                self.error(where, "缺少 rationale")
            if not dec.get("source"):
                self.error(where, "缺少 source")
            if dec.get("status") == "approved" and not dec.get("approved_by"):
                self.error(where, "approved 决定必须记录 approved_by")
            if dec.get("status") == "pending" and dec.get("approved_by"):
                self.error(where, "pending 决定不应有 approved_by")
            ev = dec.get("evidence")
            if ev not in evidence:
                self.error(where, f"悬空 evidence：{ev!r}")

    def check_evidence(self, evidence):
        for evid, ev in evidence.items():
            where = f"evidence.json:{evid}"
            if not EVIDENCE_ID.match(evid):
                self.error(where, "evidence id 必须以 EV- 开头")
            if ev.get("level") not in LEVELS:
                self.error(where, f"非法 evidence level：{ev.get('level')!r}")
            if ev.get("freshness") not in FRESHNESS:
                self.error(where, f"非法 freshness：{ev.get('freshness')!r}")
            if not ev.get("claim"):
                self.error(where, "缺少 claim")
            if not ev.get("source"):
                self.error(where, "缺少 source")
            if not ev.get("observed_at"):
                self.error(where, "缺少 observed_at")
            if ev.get("type") == "ci" and ev.get("freshness") == "stale":
                if "E3" != ev.get("level"):
                    self.error(where, "stale CI 证据只能是 E3")
            if ev.get("type") == "real-flow" and ev.get("level") not in PROOF_LEVELS:
                self.error(where, "real-flow 证据必须为 E4/E5/E6")
            if ev.get("type") == "decision" and ev.get("level") != "E0":
                self.error(where, "decision 证据必须为 E0")

    def check_batches(self, ledger, prs):
        batches = ledger.get("batches", [])
        ids = [b.get("id") for b in batches]
        if len(ids) != len(set(ids)):
            self.error("ledger.json:batches", "批次 id 重复")
        if not ids:
            self.error("ledger.json:batches", "批次不能为空")
        for batch in batches:
            where = f"ledger.json:batch:{batch.get('id')}"
            for field in ("id", "title", "goal", "status", "items", "close_criteria"):
                if not batch.get(field):
                    self.error(where, f"缺少 {field}")
            if batch.get("status") not in {"open", "closed", "blocked"}:
                self.error(where, f"非法 status：{batch.get('status')!r}")

    def check_items(self, ledger, prs, evidence, decisions, flows, ci):
        items = ledger.get("items", [])
        if len(items) != 161:
            self.error("ledger.json:items", f"必须恰好 161 项，实际 {len(items)}")
        seen = set()
        by_id = {str(item.get("id")): item for item in items}
        for item in items:
            item_id = item.get("id", "<missing>")
            where = f"ledger.json:item:{item_id}"
            if not ITEM_ID.match(str(item_id)):
                self.error(where, f"非法 ID：{item_id!r}")
            if item_id in seen:
                self.error(where, "重复 ID")
            seen.add(item_id)
            for field in ITEM_FIELDS:
                if field not in item:
                    self.error(where, f"缺少必填字段 {field}")
            if not str(item.get("title", "")).strip():
                self.error(where, "title 不能为空")
            if not str(item.get("implementation", "")).strip():
                self.error(where, "implementation 不能为空")
            if not str(item.get("next", "")).strip():
                self.error(where, "next 不能为空")
            if not str(item.get("owner", "")).strip():
                self.error(where, "owner 不能为空")
            if not isinstance(item.get("collaborators"), list):
                self.error(where, "collaborators 必须为数组")
            if item.get("status") not in STATUSES:
                self.error(where, f"非法 status：{item.get('status')!r}")
            if item.get("legacy_status") not in STATUSES:
                self.error(where, f"非法 legacy_status：{item.get('legacy_status')!r}")
            if item.get("freshness") not in FRESHNESS:
                self.error(where, f"非法 freshness：{item.get('freshness')!r}")
            legacy_source = item.get("legacy_source") or {}
            for field in ("file", "line", "section"):
                if field not in legacy_source:
                    self.error(where, f"legacy_source 缺少 {field}")
            if item.get("freshness") == "stale" and item.get("status") != item.get("legacy_status"):
                self.error(where, "stale 项的 status 必须等于 legacy_status，不能静默改写历史")
            self.check_platforms(where, item, ledger)
            self.check_acceptance(where, item, evidence, decisions)
            self.check_ac_references(where, item, by_id, evidence)
            self.check_dependencies(where, item, by_id)
            self.check_item_refs(where, item, prs, evidence, decisions)
            self.check_status_rules(where, item, evidence, decisions, prs, flows, ci)
            self.check_batch_membership(where, item, ledger, by_id)

    def check_platforms(self, where, item, ledger):
        roles = item.get("platform_roles")
        if not isinstance(roles, dict):
            self.error(where, "platform_roles 必须为对象")
            return
        missing = set(ledger.get("platforms", PLATFORMS)) - set(roles)
        extra = set(roles) - set(ledger.get("platforms", PLATFORMS))
        if missing:
            self.error(where, f"platform_roles 缺少平台：{sorted(missing)}")
        if extra:
            self.error(where, f"platform_roles 含未知平台：{sorted(extra)}")
        for platform, role in roles.items():
            if role not in ROLES:
                self.error(where, f"平台 {platform} 的角色非法：{role!r}")
        active = [p for p, r in roles.items() if r in {"required", "read_only"}]
        if not active and not any(r == "coordination" for r in roles.values()):
            self.error(where, "至少一个平台必须是 required/read_only/coordination")
        if item.get("platform_scope") not in {"declared", "unconfirmed"}:
            self.error(where, f"非法 platform_scope：{item.get('platform_scope')!r}")
        if item.get("platform_scope") == "unconfirmed" and item.get("status") != "BLOCKED":
            self.error(where, "平台范围未确认的项必须为 BLOCKED")

    def check_acceptance(self, where, item, evidence, decisions):
        acs = item.get("acceptance_criteria")
        if not isinstance(acs, list) or not acs:
            self.error(where, "acceptance_criteria 不能为空")
            return
        seen = set()
        for ac in acs:
            ac_id = ac.get("id")
            if not ac_id or not AC_ID.match(str(ac_id)):
                self.error(where, f"非法验收条件 ID：{ac_id!r}")
                continue
            if not ac_id.startswith(item.get("id", "") + "-AC"):
                self.error(where, f"验收条件 {ac_id} 不属于该项")
            if ac_id in seen:
                self.error(where, f"验收条件 ID 重复：{ac_id}")
            seen.add(ac_id)
            if not str(ac.get("text", "")).strip():
                self.error(where, f"{ac_id} 缺少 text")
            if not isinstance(ac.get("verified"), bool):
                self.error(where, f"{ac_id} verified 必须为布尔值")
            refs = ac.get("evidence", [])
            if not isinstance(refs, list):
                self.error(where, f"{ac_id} evidence 必须为数组")
                continue
            for ref in refs:
                if ref not in evidence:
                    self.error(where, f"{ac_id} 悬空 evidence：{ref!r}")
            if ac.get("verified"):
                current = [r for r in refs if evidence.get(r, {}).get("freshness") == "current"]
                if not current:
                    self.error(where, f"{ac_id} 标记 verified 但没有 current 证据")
            blocked = ac.get("blocked")
            if blocked is not None:
                if ac.get("verified"):
                    self.error(where, f"{ac_id} 已 BLOCKED，不能同时标记 verified")
                if not str(blocked.get("reason", "")).strip():
                    self.error(where, f"{ac_id} BLOCKED 无原因：blocked.reason 不能为空")
                if not [u for u in blocked.get("unblock", []) if str(u).strip()]:
                    self.error(where, f"{ac_id} BLOCKED 缺少 unblock 条件")
                did = blocked.get("decision_id")
                if did not in decisions:
                    self.error(where, f"{ac_id} 的 blocked.decision_id 悬空：{did!r}")
                elif decisions[did].get("status") == "approved":
                    self.error(where, f"{ac_id} 的阻塞决定 {did} 已批准，应更新状态")
        if not any(ac.get("verified") for ac in acs) and item.get("status") == "PASS":
            self.error(where, "PASS 项必须至少有一条已验证验收条件")

    def check_ac_references(self, where, item, by_id, evidence):
        """Every explicit X-ACnn mention must resolve to that item's AC list.

        Mentions inside the item or its linked evidence are checked. Legacy
        forms such as ``R04-AC8`` normalize to the zero-padded ``R04-AC08``.
        """
        texts = [item.get("title", ""), item.get("implementation", ""),
                 item.get("dependency_notes", ""), item.get("next", ""),
                 item.get("legacy_next", "")]
        for evid in item.get("evidence", []):
            texts.append(str(evidence.get(evid, {}).get("claim", "")))
        blob = " ".join(str(t) for t in texts)
        for match in re.finditer(r"\b([A-RSZ]\d{2})-AC(\d+)\b", blob):
            owner_id, number = match.group(1), int(match.group(2))
            ref = f"{owner_id}-AC{number:02d}"
            target = by_id.get(owner_id)
            if target is None:
                self.error(where, f"验收条件引用指向不存在的条目：{match.group(0)}")
                continue
            ac_ids = {ac.get("id") for ac in target.get("acceptance_criteria", [])}
            if ref not in ac_ids:
                self.error(where, f"悬空 AC 引用：{match.group(0)}（应为 {ref}）")
            if owner_id != item.get("id") and f"ITEM:{owner_id}" not in item.get("dependencies", []):
                self.warn(where, f"引用了其他条目的验收条件 {match.group(0)} 但未声明 ITEM:{owner_id} 依赖")

    def check_dependencies(self, where, item, by_id):
        deps = item.get("dependencies", [])
        if not isinstance(deps, list):
            self.error(where, "dependencies 必须为数组")
            return
        for dep in deps:
            if not isinstance(dep, str) or not dep.startswith("ITEM:"):
                self.error(where, f"依赖必须写成 ITEM:<id>，实际 {dep!r}")
                continue
            target = dep.split(":", 1)[1]
            if target == item.get("id"):
                self.error(where, "依赖不能指向自身")
            elif target not in by_id:
                self.error(where, f"悬空依赖：{dep}")

    def check_item_refs(self, where, item, prs, evidence, decisions):
        for pid in item.get("prs", []):
            if pid not in prs:
                self.error(where, f"悬空 PR：{pid!r}")
            else:
                pr = prs[pid]
                if pr.get("state") not in PR_STATES:
                    self.error(where, f"PR {pid} 状态非法")
        for evid in item.get("evidence", []):
            if evid not in evidence:
                self.error(where, f"悬空 evidence：{evid!r}")
        for did in item.get("decisions", []):
            if did not in decisions:
                self.error(where, f"悬空 decision：{did!r}")
        legacy_refs = item.get("legacy_acceptance_refs", [])
        ac_ids = {ac.get("id") for ac in item.get("acceptance_criteria", [])}
        for ref in legacy_refs:
            if ref not in ac_ids:
                self.error(where, f"legacy AC 引用悬空：{ref}")

    def check_status_rules(self, where, item, evidence, decisions, prs, flows, ci):
        status = item.get("status")
        refs = item.get("evidence", [])
        current_proof = [
            r for r in refs
            if evidence.get(r, {}).get("freshness") == "current"
            and evidence.get(r, {}).get("level") in PROOF_LEVELS
        ]
        if status == "PASS":
            if item.get("freshness") != "current":
                self.error(where, "stale PASS：PASS 项必须是 current freshness")
            roles = item.get("platform_roles", {})
            required = [p for p, r in roles.items() if r in {"required", "read_only"}]
            covered = set()
            for r in current_proof:
                covered.update(evidence[r].get("platforms", []) or [])
                for flow_id in evidence[r].get("flows", []) or []:
                    covered.update((flows.get(flow_id) or {}).get("platforms", []))
                for run_id in evidence[r].get("ci_runs", []) or []:
                    run = ci.get(run_id) or {}
                    repo = run.get("repo", "pim-api")
                    covered.add("release" if repo == "pim-api" and run.get("workflow") == "release" else repo)
            missing = [p for p in required if p not in covered]
            if missing:
                self.error(where, f"PASS 缺少 required 平台的 current 真实证据：{missing}")
            for pid in item.get("prs", []):
                if prs.get(pid, {}).get("state_freshness") != "current":
                    self.error(where, f"PASS 依赖的 PR {pid} 不是 current 状态")
            for ref in refs:
                ev = evidence.get(ref, {})
                if ev.get("type") == "ci" and ev.get("freshness") != "current":
                    self.error(where, f"PASS 使用了 stale CI：{ref}")

        if status == "BLOCKED":
            if not str(item.get("blocked_reason", "")).strip():
                self.error(where, "BLOCKED 无原因：blocked_reason 不能为空")
            unblock = item.get("unblock_conditions")
            if not isinstance(unblock, list) or not [u for u in unblock if str(u).strip()]:
                self.error(where, "BLOCKED 缺少可执行的 unblock_conditions")
        elif item.get("blocked_reason"):
            self.error(where, "只有 BLOCKED 项可以包含 blocked_reason")

        if status == "APPROVED-EXCEPTION":
            exc = item.get("exception")
            if not isinstance(exc, dict):
                self.error(where, "APPROVED-EXCEPTION 无批准：缺少 exception")
                return
            did = exc.get("decision_id")
            dec = decisions.get(did)
            if not dec:
                self.error(where, f"APPROVED-EXCEPTION 引用了不存在的决定：{did!r}")
                return
            if dec.get("status") != "approved":
                self.error(where, f"APPROVED-EXCEPTION 的批准记录状态是 {dec.get('status')!r}，不是 approved")
            if not str(exc.get("approved_by", "")).strip() or not str(dec.get("approved_by", "")).strip():
                self.error(where, "APPROVED-EXCEPTION 无批准：缺少 approved_by")
            if not str(exc.get("approved_in", "")).strip():
                self.error(where, "APPROVED-EXCEPTION 缺少批准出处 approved_in")
            if dec.get("evidence") not in refs:
                self.error(where, "APPROVED-EXCEPTION 必须在 evidence 中引用批准记录")
            if any(ac.get("verified") for ac in item.get("acceptance_criteria", [])):
                self.error(where, "范围例外项不能把验收条件标为 verified")
        elif item.get("exception"):
            self.error(where, "只有 APPROVED-EXCEPTION 项可以包含 exception")

        if status in {"FAIL", "NOT-VERIFIED"} and item.get("freshness") == "current" and not current_proof:
            self.error(where, "current freshness 但没有 current 真实证据")

    def check_batch_membership(self, where, item, ledger, by_id):
        batches = {b.get("id"): b for b in ledger.get("batches", [])}
        bid = item.get("batch")
        if bid not in batches:
            self.error(where, f"引用了不存在的批次：{bid!r}")
            return
        if item.get("id") not in batches[bid].get("items", []):
            self.error(where, f"批次 {bid} 的 items 未包含 {item.get('id')}")
        for member in batches[bid].get("items", []):
            if member not in by_id:
                self.error(f"ledger.json:batch:{bid}", f"悬空 item：{member}")

    def check_cross_file(self, ledger, evidence):
        for evid, ev in evidence.items():
            for item_id in ev.get("items", []):
                item = next((i for i in ledger.get("items", []) if i.get("id") == item_id), None)
                if item is None:
                    self.error(f"evidence.json:{evid}", f"引用不存在的 item：{item_id}")
                elif evid not in item.get("evidence", []):
                    self.error(f"evidence.json:{evid}", f"item {item_id} 未反向引用该证据")

    def check_render_view(self, ledger):
        rel = (ledger.get("rendered_view") or {}).get("path")
        if rel != "docs/product/pim-delivery-ledger.md":
            self.error("ledger.json:rendered_view", "path 必须为 docs/product/pim-delivery-ledger.md")
            return
        path = self.root / rel
        if not path.exists():
            self.error(rel, "生成的当前视图不存在，请运行 render-delivery-ledger.py")
            return
        text = path.read_text(encoding="utf-8")
        if "GENERATED FILE - DO NOT EDIT" not in text:
            self.error(rel, "缺少生成标记；当前视图必须由 renderer 生成")
        section = re.search(r"^## 全量表 / Full table\n(.*?)(?=^## )", text, flags=re.M | re.S)
        if not section:
            self.error(rel, "缺少全量表章节")
            return
        rows = re.findall(r"^\| ([A-RSZ]\d{2}) \|", section.group(1), flags=re.M)
        if len(rows) != 161 or len(set(rows)) != 161:
            self.error(rel, f"全量表必须恰好 161 个唯一 ID，实际 {len(rows)} 行 / {len(set(rows))} 唯一")
        self.check_view_in_sync(path, text)

    def check_view_in_sync(self, path: Path, text: str) -> None:
        renderer = self.root / "scripts/ci/render-delivery-ledger.py"
        if not renderer.exists():
            self.error("scripts/ci/render-delivery-ledger.py", "缺少渲染器，无法证明视图可重新生成")
            return
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "rendered.md"
            proc = subprocess.run(
                [sys.executable, str(renderer), "--root", str(self.root), "--output", str(target)],
                capture_output=True, text=True)
            if proc.returncode != 0:
                self.error("scripts/ci/render-delivery-ledger.py",
                           f"渲染失败：{(proc.stderr or proc.stdout).strip()}")
                return
            rendered = target.read_text(encoding="utf-8")
        if rendered != text:
            self.error(str(path), "生成视图与结构化源不同步：请运行 render-delivery-ledger.py 并提交结果")

    def report(self) -> bool:
        for warning in self.warnings:
            print(f"WARN  {warning}", file=sys.stderr)
        if self.errors:
            for err in self.errors:
                print(f"FAIL  {err}", file=sys.stderr)
            print(f"\n共 {len(self.errors)} 个错误。", file=sys.stderr)
            return False
        print("delivery ledger OK: 161 items, references and status rules valid")
        return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="仓库根目录")
    args = parser.parse_args()
    return 0 if Validator(Path(args.root).resolve()).run() else 1


if __name__ == "__main__":
    sys.exit(main())
