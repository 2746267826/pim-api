#!/usr/bin/env python3
"""Validate the structured PIM delivery ledger without touching the network.

The ledger is the single source of truth for delivery state; the rendered
Markdown is a generated view. This validator fails closed on missing items,
unknown enums, dangling references, mismatched counts, and any attempt to turn
stale, pending, or non-proof evidence into a PASS.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from collections import Counter
from datetime import date, datetime
from pathlib import Path

ITEM_ID = re.compile(r"^[A-RSZ]\d{2}$")
AC_ID = re.compile(r"^([A-RSZ]\d{2})-AC\d{2}$")
DECISION_ID = re.compile(r"^DEC-[A-Za-z0-9-]+$")
EVIDENCE_ID = re.compile(r"^EV-[A-Za-z0-9_-]+$")
PR_ID = re.compile(r"^PR-(pim-api|pim-android|pim-windows)-(\d+)$")
CI_ID = re.compile(r"^CI-(\d+)$")
FLOW_ID = re.compile(r"^RF-[A-Za-z0-9-]+$")
HASH = re.compile(r"^[0-9a-fA-F]{7,40}$")

EXPECTED_PREFIX_COUNTS = {
    "A": 10, "B": 16, "C": 22, "D": 8, "E": 3, "F": 11, "G": 13, "H": 11,
    "I": 4, "J": 9, "K": 4, "L": 14, "M": 6, "N": 3, "O": 6, "P": 5,
    "Q": 8, "R": 5, "S": 1, "Z": 2,
}
EXPECTED_ITEM_IDS = {
    f"{prefix}{number:02d}"
    for prefix, count in EXPECTED_PREFIX_COUNTS.items()
    for number in range(1, count + 1)
}
EXPECTED_BATCH_IDS = {f"B{number}" for number in range(1, 8)}
PLATFORMS = ["api", "mcp", "web", "android", "windows", "release"]
ROLES = {"required", "read_only", "coordination", "not_applicable"}
STATUSES = {"PASS", "FAIL", "NOT-VERIFIED", "BLOCKED", "APPROVED-EXCEPTION"}
FRESHNESS = {"current", "stale", "unknown", "not-verified"}
LEVELS = {"E0", "E1", "E2", "E3", "E4", "E5", "E6"}
PROOF_LEVELS = {"E4", "E5", "E6"}
PR_STATES = {"open", "merged", "closed", "draft"}
FLOW_RESULTS = {"passed", "passed_legacy", "failed", "not-run", "blocked"}
EVIDENCE_TYPES = {"audit-note", "ci", "decision", "real-flow"}
PR_VERIFIED_BY = {"git-history", "github-api", "legacy-ledger"}

ITEM_FIELDS = [
    "id", "title", "batch", "platform_roles", "platform_scope",
    "acceptance_criteria", "implementation", "implementation_basis", "dependencies", "dependency_notes",
    "owner", "collaborators", "prs", "evidence", "decisions", "status",
    "freshness", "next", "legacy_status", "legacy_source",
]


class Validator:
    def __init__(self, root: Path, git_root: Path | None = None):
        self.root = root
        self.git_root = git_root or self.find_git_root(root)
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self.evidence: dict[str, dict] = {}
        self.flows: dict[str, dict] = {}
        self.flow_by_evidence: dict[str, list[dict]] = {}
        self.decisions: dict[str, dict] = {}
        self.items_by_id: dict[str, dict] = {}

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

    def find_git_root(self, root: Path) -> Path | None:
        proc = subprocess.run(
            ["git", "-C", str(root), "rev-parse", "--show-toplevel"],
            capture_output=True, text=True)
        return Path(proc.stdout.strip()) if proc.returncode == 0 and proc.stdout.strip() else None

    def git(self, *args: str) -> subprocess.CompletedProcess:
        if self.git_root is None:
            return subprocess.CompletedProcess(["git"], 1, "", "not a git work tree")
        return subprocess.run(
            ["git", "-C", str(self.git_root), *args],
            capture_output=True, text=True)

    def parse_time(self, where: str, value, field: str) -> datetime | None:
        if not value:
            self.error(where, f"缺少 {field}")
            return None
        try:
            parsed = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        except ValueError:
            self.error(where, f"{field} 不是 ISO-8601 时间：{value!r}")
            return None
        if parsed.tzinfo is None:
            self.error(where, f"{field} 必须带时区：{value!r}")
            return None
        return parsed

    def parse_date(self, where: str, value, field: str) -> date | None:
        if not value:
            self.error(where, f"缺少 {field}")
            return None
        try:
            return date.fromisoformat(str(value))
        except ValueError:
            self.error(where, f"{field} 不是 ISO 日期：{value!r}")
            return None

    def is_current_real_evidence(self, evidence_id: str, item_id: str) -> bool:
        ev = self.evidence.get(evidence_id)
        if not ev:
            return False
        if ev.get("type") != "real-flow" or ev.get("level") not in PROOF_LEVELS:
            return False
        if ev.get("freshness") != "current":
            return False
        matches = self.flow_by_evidence.get(evidence_id, [])
        if len(matches) != 1:
            return False
        flow = matches[0]
        return (
            flow.get("freshness") == "current"
            and item_id in (flow.get("items") or [])
        )

    def is_current_proof(self, evidence_id: str, item_id: str) -> bool:
        if not self.is_current_real_evidence(evidence_id, item_id):
            return False
        return self.flow_by_evidence[evidence_id][0].get("result") == "passed"

    def proof_platforms(self, evidence_id: str, item_id: str) -> set[str]:
        if not self.is_current_proof(evidence_id, item_id):
            return set()
        flow = self.flow_by_evidence[evidence_id][0]
        return set(flow.get("platforms") or [])

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
        self.flows = self.index(flows_doc.get("flows", []), "id", "real-flows.json")
        self.evidence = self.index(evidence_doc.get("evidence", []), "id", "evidence.json")
        self.decisions = self.index(decisions_doc.get("decisions", []), "id", "decisions.json")
        items = ledger.get("items", [])
        for item in items:
            item_id = str(item.get("id"))
            self.items_by_id.setdefault(item_id, item)
        for flow in self.flows.values():
            evidence_id = flow.get("evidence")
            if evidence_id:
                self.flow_by_evidence.setdefault(evidence_id, []).append(flow)

        self.check_scope(ledger)
        self.check_prs(prs)
        self.check_ci(ci, prs)
        self.check_flows()
        self.check_decisions()
        self.check_evidence(ci)
        self.check_batches(ledger)
        self.check_items(ledger, prs, ci)
        self.check_cross_file(ledger)
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
        items = ledger.get("items", [])
        if scope.get("total") != 161:
            self.error("ledger.json:scope", f"total 必须为 161，实际 {scope.get('total')!r}")
        if scope.get("core_total") != 160:
            self.error("ledger.json:scope", f"core_total 必须为 160，实际 {scope.get('core_total')!r}")
        if scope.get("supplemental_ids") != ["S01"]:
            self.error("ledger.json:scope", "supplemental_ids 必须恰为 ['S01']")
        expected_core = sorted(prefix for prefix in EXPECTED_PREFIX_COUNTS if prefix not in {"S", "Z"})
        if sorted(scope.get("core_prefixes", [])) != expected_core:
            self.error("ledger.json:scope", f"core_prefixes 必须为 {expected_core}")
        if scope.get("reverse_prefixes") != ["Z"]:
            self.error("ledger.json:scope", "reverse_prefixes 必须为 ['Z']")
        if scope.get("counts_by_prefix") != EXPECTED_PREFIX_COUNTS:
            self.error("ledger.json:scope",
                       f"counts_by_prefix 与固定范围不符：期望 {EXPECTED_PREFIX_COUNTS}，实际 {scope.get('counts_by_prefix')}")

        ids = [str(item.get("id")) for item in items]
        id_set = set(ids)
        missing = sorted(EXPECTED_ITEM_IDS - id_set)
        unexpected = sorted(id_set - EXPECTED_ITEM_IDS)
        if missing or unexpected:
            self.error("ledger.json:items",
                       f"ID 集合与固定范围不符：缺少 {missing}，多出 {unexpected}")
        actual_prefix_counts = Counter(
            item_id[0] for item_id in ids
            if item_id and item_id[0] in EXPECTED_PREFIX_COUNTS
        )
        if dict(actual_prefix_counts) != scope.get("counts_by_prefix"):
            self.error("ledger.json:scope",
                       f"counts_by_prefix 与 items 实际计数不符：实际 {dict(actual_prefix_counts)}")
        actual_status_counts = Counter(item.get("status") for item in items)
        expected_status_counts = {status: actual_status_counts.get(status, 0) for status in sorted(STATUSES)}
        if scope.get("counts_by_status") != expected_status_counts:
            self.error("ledger.json:scope",
                       f"counts_by_status 与 items 实际计数不符：期望 {expected_status_counts}，"
                       f"实际 {scope.get('counts_by_status')}")

        if sorted(ledger.get("platforms", [])) != sorted(PLATFORMS):
            self.error("ledger.json:platforms", f"平台列表必须为 {PLATFORMS}")
        if ledger.get("platform_role_values") != sorted(ROLES):
            self.error("ledger.json:platform_role_values", f"角色枚举必须为 {sorted(ROLES)}")
        if not ledger.get("batch_close_rules"):
            self.error("ledger.json:batch_close_rules", "批次关闭条件不能为空")
        self.check_source_snapshot(ledger.get("source_snapshot") or {})

    def check_source_snapshot(self, snapshot):
        where = "ledger.json:source_snapshot"
        for field in ("repo", "branch", "commit", "commit_date", "observed_at", "migrated_at"):
            if not snapshot.get(field):
                self.error(where, f"缺少 {field}")
        repo = str(snapshot.get("repo", ""))
        if not re.fullmatch(r"[^/\s]+/[^/\s]+", repo):
            self.error(where, f"repo 必须为 owner/name，实际 {repo!r}")
        branch = str(snapshot.get("branch", ""))
        if not branch:
            return
        commit = str(snapshot.get("commit", ""))
        if not HASH.fullmatch(commit):
            self.error(where, f"commit 必须是 7–40 位十六进制，实际 {commit!r}")
            return
        commit_time = self.parse_time(where, snapshot.get("commit_date"), "commit_date")
        observed_time = self.parse_time(where, snapshot.get("observed_at"), "observed_at")
        self.parse_date(where, snapshot.get("migrated_at"), "migrated_at")
        if commit_time and observed_time and observed_time < commit_time:
            self.error(where, "observed_at 不能早于 commit_date")

        if self.git_root is None:
            self.error(where, "无法定位 git 仓库，不能验证 source_snapshot.commit")
            return
        resolved = self.git("rev-parse", "--verify", f"{commit}^{{commit}}")
        if resolved.returncode != 0:
            self.error(where, f"commit 无法解析为本地 git 对象：{commit}")
            return
        full_commit = resolved.stdout.strip()
        if commit_time:
            actual_date = self.git("show", "-s", "--format=%cI", full_commit)
            if actual_date.returncode == 0:
                try:
                    actual_time = datetime.fromisoformat(actual_date.stdout.strip())
                except ValueError:
                    self.error(where, f"无法解析 git commit date：{actual_date.stdout.strip()!r}")
                else:
                    if actual_time != commit_time:
                        self.error(where, f"commit_date 与 git 记录不一致：{commit_time.isoformat()} != {actual_time.isoformat()}")
        refs = [
            f"refs/remotes/origin/{branch}",
            f"refs/heads/{branch}",
        ]
        ref = next((candidate for candidate in refs
                    if self.git("show-ref", "--verify", "--quiet", candidate).returncode == 0), None)
        if ref is None:
            self.warn(where, f"找不到分支引用 {branch}，只验证 commit 可解析")
        elif self.git("merge-base", "--is-ancestor", full_commit, ref).returncode != 0:
            self.error(where, f"commit {commit} 不属于声明分支 {branch}")

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
            if pr.get("state_freshness") not in {"current", "stale"}:
                self.error(where, f"state_freshness 必须为 current/stale，实际 {pr.get('state_freshness')!r}")
            self.parse_time(where, pr.get("observed_at"), "observed_at")
            if pr.get("state_verified_by") not in PR_VERIFIED_BY:
                self.error(where, f"非法 state_verified_by：{pr.get('state_verified_by')!r}")
            for field in ("head", "merge_commit"):
                value = pr.get(field)
                if value is not None and not HASH.fullmatch(str(value)):
                    self.error(where, f"{field} 必须是 7–40 位十六进制，实际 {value!r}")
            if pr.get("state") == "merged":
                if not pr.get("merge_commit"):
                    self.error(where, "merged PR 必须给出 merge_commit")
                if pr.get("state_freshness") == "current" and pr.get("state_verified_by") == "legacy-ledger":
                    self.error(where, "current 的 merged PR 不能由 legacy-ledger 验证")
                if pr.get("state_verified_by") == "git-history" and repo == "pim-api":
                    merge_commit = str(pr.get("merge_commit", ""))
                    resolved = self.git("rev-parse", "--verify", f"{merge_commit}^{{commit}}")
                    if resolved.returncode != 0:
                        self.error(where, f"merge_commit 无法解析：{merge_commit}")
                    elif self.git("merge-base", "--is-ancestor", resolved.stdout.strip(), "refs/remotes/origin/master").returncode != 0:
                        self.error(where, "merge_commit 不属于 origin/master")
            elif pr.get("merge_commit"):
                self.error(where, f"{pr.get('state')} PR 不应带 merge_commit")
            if pr.get("state") in {"open", "draft"} and not pr.get("head"):
                self.error(where, f"{pr.get('state')} PR 必须给出 head")
            if pr.get("state_freshness") == "current" and pr.get("state_verified_by") == "legacy-ledger":
                self.error(where, "current 状态必须由 git-history 或 github-api 验证")

    def check_ci(self, ci, prs):
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
            if not run.get("observed_at"):
                self.error(where, "缺少 observed_at")

    def check_flows(self):
        for fid, flow in self.flows.items():
            where = f"real-flows.json:{fid}"
            if not FLOW_ID.match(fid):
                self.error(where, "真实流程 id 必须为 RF-*")
            if not str(flow.get("title", "")).strip():
                self.error(where, "缺少 title")
            if flow.get("result") not in FLOW_RESULTS:
                self.error(where, f"非法 result：{flow.get('result')!r}")
            if flow.get("freshness") not in FRESHNESS:
                self.error(where, f"非法 freshness：{flow.get('freshness')!r}")
            if not flow.get("observed_at"):
                self.error(where, "缺少 observed_at")
            items = flow.get("items")
            if not isinstance(items, list) or not items:
                self.error(where, "items 必须为非空数组")
                items = []
            if len(items) != len(set(items)):
                self.error(where, "items 含重复 ID")
            for item_id in items:
                if item_id not in self.items_by_id:
                    self.error(where, f"引用不存在的 item：{item_id}")
            platforms = flow.get("platforms")
            if not isinstance(platforms, list) or not platforms:
                self.error(where, "platforms 必须为非空数组")
                platforms = []
            for platform in platforms:
                if platform not in PLATFORMS:
                    self.error(where, f"非法 platform：{platform!r}")
            if len(platforms) != len(set(platforms)):
                self.error(where, "platforms 含重复值")

            evid = flow.get("evidence")
            if not evid:
                self.error(where, "缺少 evidence 引用")
                continue
            ev = self.evidence.get(evid)
            if ev is None:
                self.error(where, f"悬空 evidence：{evid!r}")
                continue
            if ev.get("type") != "real-flow":
                self.error(where, f"evidence {evid} 类型不是 real-flow")
            if ev.get("level") not in PROOF_LEVELS:
                self.error(where, f"evidence {evid} 必须是 E4/E5/E6")
            if ev.get("freshness") != flow.get("freshness"):
                self.error(where, "flow 与 evidence 的 freshness 不一致")
            if set(ev.get("items") or []) != set(items):
                self.error(where, f"flow.items 与 evidence.items 不一致：{items} != {ev.get('items')}")
            for item_id in items:
                item = self.items_by_id.get(item_id)
                if item is not None and evid not in (item.get("evidence") or []):
                    self.error(where, f"item {item_id} 未反向引用流程证据 {evid}")
            if flow.get("result") == "passed":
                if flow.get("freshness") != "current":
                    self.error(where, "passed 真实流程必须是 current freshness")
                if ev.get("freshness") != "current":
                    self.error(where, "passed 真实流程的 evidence 必须是 current")
                if not flow.get("environment"):
                    self.error(where, "passed 真实流程必须记录环境")
                if not flow.get("artifacts"):
                    self.error(where, "passed 真实流程必须记录产物")
            if flow.get("result") == "passed_legacy":
                if flow.get("freshness") != "stale":
                    self.error(where, "passed_legacy 必须是 stale freshness")
                if not flow.get("environment"):
                    self.error(where, "passed_legacy 必须记录当时的执行环境")
            if flow.get("result") == "not-run" and flow.get("freshness") != "not-verified":
                self.error(where, "not-run 流程必须是 not-verified freshness")
            if not str(flow.get("limits", "")).strip():
                self.error(where, "必须写明未验证边界 limits")

        for evid, matches in self.flow_by_evidence.items():
            if len(matches) != 1:
                self.error("real-flows.json", f"evidence {evid} 必须恰好对应一个真实流程，实际 {len(matches)} 个")

    def check_decisions(self):
        referenced_by_items: dict[str, set[str]] = {}
        for item_id, item in self.items_by_id.items():
            for did in item.get("decisions") or []:
                referenced_by_items.setdefault(did, set()).add(item_id)
        for did, dec in self.decisions.items():
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
            items = dec.get("items")
            if not isinstance(items, list):
                self.error(where, "items 必须为数组")
                items = []
            if len(items) != len(set(items)):
                self.error(where, "items 含重复 ID")
            for item_id in items:
                if item_id not in self.items_by_id:
                    self.error(where, f"悬空 item：{item_id}")
                elif did not in (self.items_by_id[item_id].get("decisions") or []):
                    self.error(where, f"item {item_id} 未反向引用决定 {did}")
            for item_id in referenced_by_items.get(did, set()):
                if item_id not in items:
                    self.error(where, f"item {item_id} 引用了决定，但决定 items 未包含该 item")
            evid = dec.get("evidence")
            ev = self.evidence.get(evid)
            if ev is None:
                self.error(where, f"悬空 evidence：{evid!r}")
            else:
                if ev.get("type") != "decision":
                    self.error(where, f"evidence {evid} 类型不是 decision")
                if set(ev.get("items") or []) != set(items):
                    self.error(where, f"decision.items 与 evidence.items 不一致：{items} != {ev.get('items')}")

    def check_evidence(self, ci):
        decision_evidence = {dec.get("evidence") for dec in self.decisions.values()}
        for evid, ev in self.evidence.items():
            where = f"evidence.json:{evid}"
            if not EVIDENCE_ID.match(evid):
                self.error(where, "evidence id 必须以 EV- 开头")
            ev_type = ev.get("type")
            if ev_type not in EVIDENCE_TYPES:
                self.error(where, f"非法 evidence type：{ev_type!r}")
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
            if not str(ev.get("limits", "")).strip():
                self.error(where, "缺少 limits")
            items = ev.get("items")
            if not isinstance(items, list):
                self.error(where, "items 必须为数组")
            else:
                for item_id in items:
                    if item_id not in self.items_by_id:
                        self.error(where, f"引用不存在的 item：{item_id}")
            if ev_type == "ci":
                if ev.get("level") != "E3":
                    self.error(where, "CI 证据必须为 E3")
                run = ci.get(ev.get("ci_run"))
                if run is None:
                    self.error(where, f"悬空 ci_run：{ev.get('ci_run')!r}")
                elif not set(run.get("jobs") or []):
                    self.error(where, "关联 CI run 缺少 jobs")
            if ev_type == "real-flow":
                if ev.get("level") not in PROOF_LEVELS:
                    self.error(where, "real-flow 证据必须为 E4/E5/E6")
                matches = self.flow_by_evidence.get(evid, [])
                if len(matches) != 1:
                    self.error(where, f"real-flow 证据必须恰好被一个流程引用，实际 {len(matches)} 个")
                elif set(matches[0].get("items") or []) != set(items or []):
                    self.error(where, "real-flow evidence.items 与流程 items 不一致")
            if ev_type == "decision":
                if ev.get("level") != "E0":
                    self.error(where, "decision 证据必须为 E0")
                if evid not in decision_evidence:
                    self.error(where, "decision 证据未被任何 decision 引用")
            if ev.get("level") in PROOF_LEVELS and ev_type != "real-flow":
                self.error(where, "E4/E5/E6 证据必须是 real-flow")

    def check_batches(self, ledger):
        batches = ledger.get("batches", [])
        ids = [b.get("id") for b in batches]
        if len(ids) != len(set(ids)):
            self.error("ledger.json:batches", "批次 id 重复")
        if set(ids) != EXPECTED_BATCH_IDS:
            self.error("ledger.json:batches", f"批次 ID 必须恰为 {sorted(EXPECTED_BATCH_IDS)}，实际 {ids}")
        members: list[str] = []
        for batch in batches:
            where = f"ledger.json:batch:{batch.get('id')}"
            for field in ("id", "title", "goal", "status", "items", "prefixes", "close_criteria"):
                if not batch.get(field):
                    self.error(where, f"缺少 {field}")
            if batch.get("status") not in {"open", "closed", "blocked"}:
                self.error(where, f"非法 status：{batch.get('status')!r}")
            batch_items = batch.get("items") or []
            if len(batch_items) != len(set(batch_items)):
                self.error(where, "items 含重复 ID")
            members.extend(batch_items)
            for item_id in batch_items:
                if item_id not in self.items_by_id:
                    self.error(where, f"悬空 item：{item_id}")
            actual_prefixes = sorted({str(item_id)[0] for item_id in batch_items if item_id})
            if sorted(batch.get("prefixes") or []) != actual_prefixes:
                self.error(where, f"prefixes 与实际 items 不符：期望 {actual_prefixes}，实际 {batch.get('prefixes')}")
            present = [self.items_by_id[item_id] for item_id in batch_items if item_id in self.items_by_id]
            expected_status = self.expected_batch_status(present)
            if batch.get("status") != expected_status:
                self.error(where, f"status 与 items 重算结果不符：期望 {expected_status}，实际 {batch.get('status')!r}")
            if batch.get("status") == "closed":
                self.check_closed_batch(where, present)

        if len(members) != len(set(members)):
            self.error("ledger.json:batches", "同一 item 不能属于多个批次")
        if set(members) != set(self.items_by_id):
            self.error("ledger.json:batches",
                       f"批次并未恰好覆盖全部 item：缺少 {sorted(set(self.items_by_id) - set(members))}，"
                       f"多出 {sorted(set(members) - set(self.items_by_id))}")

    def expected_batch_status(self, items):
        statuses = [item.get("status") for item in items]
        if any(status in {"FAIL", "NOT-VERIFIED"} for status in statuses):
            return "open"
        if any(status == "BLOCKED" for status in statuses):
            return "blocked"
        if statuses and all(status in {"PASS", "APPROVED-EXCEPTION"} for status in statuses):
            return "closed"
        return "open"

    def check_closed_batch(self, where, items):
        for item in items:
            item_id = item.get("id")
            if item.get("status") == "PASS":
                if item.get("freshness") != "current":
                    self.error(where, f"PASS 项 {item_id} 不是 current freshness，批次不能关闭")
                if any(ac.get("blocked") for ac in item.get("acceptance_criteria") or []):
                    self.error(where, f"PASS 项 {item_id} 仍有 AC 级 BLOCKED，批次不能关闭")
                if not all(ac.get("verified") for ac in item.get("acceptance_criteria") or []):
                    self.error(where, f"PASS 项 {item_id} 仍有未验证 AC，批次不能关闭")
                for dep in item.get("dependencies") or []:
                    target = self.items_by_id.get(str(dep).split(":", 1)[-1])
                    if target is None or target.get("status") not in {"PASS", "APPROVED-EXCEPTION"}:
                        self.error(where, f"closed 批次中 {item_id} 的依赖 {dep} 未满足")

    def check_items(self, ledger, prs, ci):
        items = ledger.get("items", [])
        if len(items) != 161:
            self.error("ledger.json:items", f"必须恰好 161 项，实际 {len(items)}")
        seen = set()
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
            basis = str(item.get("implementation_basis", "")).strip()
            if not basis:
                self.error(where, "implementation_basis 不能为空")
            elif not (basis.startswith("legacy-snapshot-") or basis.startswith("current-")):
                self.error(where, "implementation_basis 必须以 legacy-snapshot- 或 current- 开头")
            if item.get("freshness") == "current" and basis.startswith("legacy-snapshot-"):
                self.error(where, "current 项不能继续使用 legacy implementation_basis")
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
            self.check_acceptance(where, item)
            self.check_ac_references(where, item)
            self.check_dependencies(where, item)
            self.check_item_refs(where, item, prs)
            self.check_status_rules(where, item, prs)
            self.check_batch_membership(where, item, ledger)

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

    def check_acceptance(self, where, item):
        acs = item.get("acceptance_criteria")
        if not isinstance(acs, list) or not acs:
            self.error(where, "acceptance_criteria 不能为空")
            return
        item_id = str(item.get("id"))
        seen = set()
        blocked_ids: list[str] = []
        pending_ids: list[str] = []
        for ac in acs:
            ac_id = str(ac.get("id", ""))
            if not AC_ID.match(ac_id):
                self.error(where, f"非法验收条件 ID：{ac_id!r}")
                continue
            if not ac_id.startswith(item_id + "-AC"):
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
                refs = []
            for ref in refs:
                if ref not in self.evidence:
                    self.error(where, f"{ac_id} 悬空 evidence：{ref!r}")
            qualified = [ref for ref in refs if self.is_current_proof(ref, item_id)]
            if ac.get("verified"):
                if not qualified:
                    self.error(where, f"{ac_id} 标记 verified 但没有 current E4/E5/E6 真实流程证据")
                for ref in refs:
                    if not self.is_current_proof(ref, item_id):
                        self.error(where, f"{ac_id} 的 verified 证据不合格或已过期：{ref!r}")
            blocked = ac.get("blocked")
            if blocked is not None:
                blocked_ids.append(ac_id)
                if ac.get("verified"):
                    self.error(where, f"{ac_id} 已 BLOCKED，不能同时标记 verified")
                if not str(blocked.get("reason", "")).strip():
                    self.error(where, f"{ac_id} BLOCKED 无原因：blocked.reason 不能为空")
                if not [u for u in blocked.get("unblock", []) if str(u).strip()]:
                    self.error(where, f"{ac_id} BLOCKED 缺少 unblock 条件")
                did = blocked.get("decision_id")
                dec = self.decisions.get(did)
                if dec is None:
                    self.error(where, f"{ac_id} 的 blocked.decision_id 悬空：{did!r}")
                else:
                    if dec.get("status") == "approved":
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 已批准，应更新状态")
                    if item_id not in (dec.get("items") or []):
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 未双向引用 item {item_id}")
                    if did not in (item.get("decisions") or []):
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 未列入 item.decisions")
            elif not ac.get("verified"):
                pending_ids.append(ac_id)

        if item.get("status") == "PASS":
            if blocked_ids:
                self.error(where, f"PASS 项存在 AC 级 BLOCKED：{blocked_ids}")
            if pending_ids:
                self.error(where, f"PASS 项存在未验证/待定 AC：{pending_ids}")
            if not all(ac.get("verified") for ac in acs):
                self.error(where, "PASS 项必须验证全部 acceptance criteria")

    def check_ac_references(self, where, item):
        """Every explicit X-ACnn mention must resolve to that item's AC list."""
        texts = [item.get("title", ""), item.get("implementation", ""),
                 item.get("dependency_notes", ""), item.get("next", ""),
                 item.get("legacy_next", "")]
        for evid in item.get("evidence", []):
            texts.append(str(self.evidence.get(evid, {}).get("claim", "")))
        blob = " ".join(str(t) for t in texts)
        for match in re.finditer(r"\b([A-RSZ]\d{2})-AC(\d+)\b", blob):
            owner_id, number = match.group(1), int(match.group(2))
            ref = f"{owner_id}-AC{number:02d}"
            target = self.items_by_id.get(owner_id)
            if target is None:
                self.error(where, f"验收条件引用指向不存在的条目：{match.group(0)}")
                continue
            ac_ids = {ac.get("id") for ac in target.get("acceptance_criteria", [])}
            if ref not in ac_ids:
                self.error(where, f"悬空 AC 引用：{match.group(0)}（应为 {ref}）")
            if owner_id != item.get("id") and f"ITEM:{owner_id}" not in item.get("dependencies", []):
                self.warn(where, f"引用了其他条目的验收条件 {match.group(0)} 但未声明 ITEM:{owner_id} 依赖")

    def check_dependencies(self, where, item):
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
            elif target not in self.items_by_id:
                self.error(where, f"悬空依赖：{dep}")

    def check_item_refs(self, where, item, prs):
        for pid in item.get("prs", []):
            if pid not in prs:
                self.error(where, f"悬空 PR：{pid!r}")
            else:
                pr = prs[pid]
                if pr.get("state") not in PR_STATES:
                    self.error(where, f"PR {pid} 状态非法")
        for evid in item.get("evidence", []):
            if evid not in self.evidence:
                self.error(where, f"悬空 evidence：{evid!r}")
        for did in item.get("decisions", []):
            if did not in self.decisions:
                self.error(where, f"悬空 decision：{did!r}")
        legacy_refs = item.get("legacy_acceptance_refs", [])
        ac_ids = {ac.get("id") for ac in item.get("acceptance_criteria", [])}
        for ref in legacy_refs:
            if ref not in ac_ids:
                self.error(where, f"legacy AC 引用悬空：{ref}")

    def check_status_rules(self, where, item, prs):
        status = item.get("status")
        refs = item.get("evidence", [])
        if status == "PASS":
            if item.get("freshness") != "current":
                self.error(where, "stale PASS：PASS 项必须是 current freshness")
            for ref in refs:
                ev = self.evidence.get(ref, {})
                if ev.get("freshness") != "current":
                    self.error(where, f"PASS 引用了非 current 证据：{ref}")
                if ev.get("type") == "ci" and ev.get("freshness") != "current":
                    self.error(where, f"PASS 使用了 stale CI：{ref}")
            roles = item.get("platform_roles", {})
            required = [p for p, r in roles.items() if r in {"required", "read_only"}]
            covered = set()
            for ref in refs:
                covered.update(self.proof_platforms(ref, str(item.get("id"))))
            missing = [p for p in required if p not in covered]
            if missing:
                self.error(where, f"PASS 缺少 required 平台的 current 真实流程证据：{missing}")
            for pid in item.get("prs", []):
                if prs.get(pid, {}).get("state_freshness") != "current":
                    self.error(where, f"PASS 依赖的 PR {pid} 不是 current 状态")

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
            dec = self.decisions.get(did)
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
            if item.get("id") not in (dec.get("items") or []):
                self.error(where, "APPROVED-EXCEPTION 的批准决定未双向引用该 item")
            if any(ac.get("verified") for ac in item.get("acceptance_criteria", [])):
                self.error(where, "范围例外项不能把验收条件标为 verified")
        elif item.get("exception"):
            self.error(where, "只有 APPROVED-EXCEPTION 项可以包含 exception")

        current_real = [
            ref for ref in refs
            if self.is_current_real_evidence(ref, str(item.get("id")))
        ]
        if status in {"FAIL", "NOT-VERIFIED"} and item.get("freshness") == "current" and not current_real:
            self.error(where, "current freshness 但没有 current 真实证据")

    def check_batch_membership(self, where, item, ledger):
        batches = {b.get("id"): b for b in ledger.get("batches", [])}
        bid = item.get("batch")
        if bid not in batches:
            self.error(where, f"引用了不存在的批次：{bid!r}")
            return
        if item.get("id") not in batches[bid].get("items", []):
            self.error(where, f"批次 {bid} 的 items 未包含 {item.get('id')}")

    def check_cross_file(self, ledger):
        for evid, ev in self.evidence.items():
            for item_id in ev.get("items", []):
                item = self.items_by_id.get(item_id)
                if item is None:
                    self.error(f"evidence.json:{evid}", f"引用不存在的 item：{item_id}")
                elif evid not in item.get("evidence", []):
                    self.error(f"evidence.json:{evid}", f"item {item_id} 未反向引用该证据")
        for flow_id, flow in self.flows.items():
            for item_id in flow.get("items", []):
                item = self.items_by_id.get(item_id)
                if item is None:
                    self.error(f"real-flows.json:{flow_id}", f"引用不存在的 item：{item_id}")

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
        if "AC 级阻塞" not in text:
            self.error(rel, "缺少 AC 级阻塞章节")
        if "旧台账快照" not in text:
            self.error(rel, "缺少 legacy implementation 标识")
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
        print("delivery ledger OK: 161 exact IDs, counts, evidence chains and status rules valid")
        return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="仓库根目录")
    args = parser.parse_args()
    return 0 if Validator(Path(args.root).resolve()).run() else 1


if __name__ == "__main__":
    sys.exit(main())
