#!/usr/bin/env python3
"""Validate the structured PIM delivery ledger without touching the network.

The ledger is the single source of truth for delivery state; the rendered
Markdown is a generated view. This validator fails closed on missing items,
unknown enums, dangling references, mismatched counts, and any attempt to turn
stale, pending, or non-proof evidence into a PASS.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from collections import Counter
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

ITEM_ID = re.compile(r"^[A-RSZ]\d{2}$")
AC_ID = re.compile(r"^([A-RSZ]\d{2})-AC\d{2}$")
DECISION_ID = re.compile(r"^DEC-[A-Za-z0-9-]+$")
EVIDENCE_ID = re.compile(r"^EV-[A-Za-z0-9_-]+$")
PR_ID = re.compile(r"^PR-(pim-api|pim-android|pim-windows)-(\d+)$")
CI_ID = re.compile(r"^CI-(\d+)$")
FLOW_ID = re.compile(r"^RF-[A-Za-z0-9-]+$")
HASH = re.compile(r"^[0-9a-fA-F]{7,40}$")
ACCEPTANCE_SCOPE_VERSION = 2
ACCEPTANCE_SCOPE_TOTAL = 731
ACCEPTANCE_SCOPE_SHA256 = "e3402e18b210f89ffaed6e0528c7d43984f570d32fc2d88c71d38c4cae0256b1"
EXPECTED_SNAPSHOT_REPO = "2746267826/pim-api"
EXPECTED_SOURCE_PACKAGE = "PIM功能确认-最终交付-20261122.zip"
EXPECTED_SOURCE_PACKAGE_SHA256 = (
    "b488a01e6f317a2735f387a918787204791fb32a38ad9ce064a07166f4b8a864"
)
ITEM_ONLY_PASS_DECISION_TYPE = "item-only-pass"
FUTURE_CLOCK_SKEW = timedelta(minutes=5)

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
EXPECTED_BATCH_PREFIXES = {
    "B1": ["A", "B", "C"],
    "B2": ["D", "E", "F"],
    "B3": ["G", "H", "I"],
    "B4": ["J", "K"],
    "B5": ["L", "M", "N"],
    "B6": ["O", "P", "Q"],
    "B7": ["R", "S", "Z"],
}
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
ALLOWED_SNAPSHOT_BRANCHES = {"master"}
LEGACY_SNAPSHOT_FILE = "docs/product/pim-delivery-ledger-20261125.md"
CI_CONTRACT = {
    "pim-api": {
        "workflow": "CI",
        "jobs": {
            "CI Gate", "6 shards", "build-api", "build-docker", "build-mcp",
            "release", "web-build",
        },
    },
    "pim-android": {
        "workflow": "CI",
        "jobs": {"CI Gate", "Contract freshness", "build-android"},
    },
    "pim-windows": {
        "workflow": "CI",
        "jobs": {"CI Gate", "build-windows", "contract-freshness"},
    },
}

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
        self.batches_by_id: dict[str, dict] = {}
        self.prs: dict[str, dict] = {}
        self.ci: dict[str, dict] = {}
        self.snapshot: dict = {}
        self.snapshot_full_commit: str | None = None
        self.snapshot_commit_time: datetime | None = None
        self.snapshot_observed_time: datetime | None = None

    # -- helpers ---------------------------------------------------------
    def error(self, where: str, message: str) -> None:
        entry = f"{where}: {message}"
        if entry not in self.errors:
            self.errors.append(entry)

    def warn(self, where: str, message: str) -> None:
        self.warnings.append(f"{where}: {message}")

    def item_acceptance_criteria(self, item: dict) -> list[dict]:
        acs = item.get("acceptance_criteria")
        return acs if isinstance(acs, list) else []

    def known_ac_ids(self) -> set[str]:
        return {
            str(ac.get("id"))
            for item in self.items_by_id.values()
            for ac in self.item_acceptance_criteria(item)
            if isinstance(ac, dict)
        }

    def required_string(self, where: str, row: dict, field: str) -> str:
        value = row.get(field)
        if not isinstance(value, str) or not value.strip():
            self.error(where, f"缺少非空字符串 {field}")
            return ""
        return value

    def string_list(self, where: str, value, field: str,
                    *, allow_empty: bool = True) -> list[str]:
        if not isinstance(value, list):
            self.error(where, f"{field} 必须为数组")
            return []
        if not value and not allow_empty:
            self.error(where, f"{field} 不能为空")
        result: list[str] = []
        for entry in value:
            if not isinstance(entry, str) or not entry.strip():
                self.error(where, f"{field} 只能包含非空字符串，实际 {entry!r}")
                continue
            result.append(entry)
        if len(result) != len(set(result)):
            self.error(where, f"{field} 含重复值")
        return result

    def enum_value(self, value) -> str | None:
        return value if isinstance(value, str) else None

    def item_required_platforms(self, item: dict) -> list[str]:
        roles = item.get("platform_roles")
        if not isinstance(roles, dict):
            return []
        return [
            str(platform)
            for platform, role in roles.items()
            if isinstance(role, str) and role in {"required", "read_only"}
        ]

    def current_passed_flow_platforms(self, item_id: str) -> set[str]:
        covered: set[str] = set()
        for flow in self.flows.values():
            if flow.get("freshness") != "current" or flow.get("result") != "passed":
                continue
            flow_items = flow.get("items")
            if not isinstance(flow_items, list) or item_id not in flow_items:
                continue
            platforms = flow.get("platforms")
            if isinstance(platforms, list):
                covered.update(p for p in platforms if isinstance(p, str))
        return covered

    def is_superseded_stale_failure(self, evidence_id: str, item_id: str) -> bool:
        ev = self.evidence.get(evidence_id)
        if not ev or ev.get("type") != "real-flow" or ev.get("freshness") != "stale":
            return False
        matches = self.flow_by_evidence.get(evidence_id, [])
        if len(matches) != 1:
            return False
        flow = matches[0]
        if item_id not in (flow.get("items") or []):
            return False
        if self.enum_value(flow.get("result")) not in {"failed", "blocked"}:
            return False
        target_id = flow.get("superseded_by")
        if not isinstance(target_id, str):
            return False
        target = self.flows.get(target_id)
        if target is None or target.get("freshness") != "current" or target.get("result") != "passed":
            return False
        source_ac_ids = flow.get("ac_ids")
        target_ac_ids = target.get("ac_ids")
        if not isinstance(source_ac_ids, list) or not isinstance(target_ac_ids, list):
            return False
        source_acs = {ac_id for ac_id in source_ac_ids if isinstance(ac_id, str)}
        target_acs = {ac_id for ac_id in target_ac_ids if isinstance(ac_id, str)}
        return bool(source_acs) and source_acs.issubset(target_acs)

    def resolve_commit(self, where: str, value, field: str = "source_commit") -> str | None:
        commit = str(value or "")
        if not HASH.fullmatch(commit):
            self.error(where, f"{field} 必须是 7–40 位十六进制，实际 {value!r}")
            return None
        resolved = self.git("rev-parse", "--verify", f"{commit}^{{commit}}")
        if resolved.returncode != 0:
            self.error(where, f"{field} 无法解析为本地 git 对象：{commit}")
            return None
        return resolved.stdout.strip()

    def parse_current_time(self, where: str, value, field: str) -> datetime | None:
        parsed = self.parse_time(where, value, field)
        if parsed is None:
            return None
        text = str(value)
        if "T" not in text:
            self.error(where, f"current 的 {field} 必须是带时区的 ISO-8601 日期时间：{value!r}")
            return None
        return parsed

    def validate_current_time(self, where: str, value, field: str) -> datetime | None:
        parsed = self.parse_current_time(where, value, field)
        if parsed is None:
            return None
        self.validate_not_future(where, parsed, field)
        if self.snapshot_commit_time and parsed <= self.snapshot_commit_time:
            self.error(where, f"current 的 {field} 必须晚于 source_snapshot.commit_date")
        if self.snapshot_observed_time and parsed > self.snapshot_observed_time:
            self.error(where, f"current 的 {field} 晚于 source_snapshot.observed_at；先刷新快照")
        return parsed

    def validate_not_future(self, where: str, parsed: datetime, field: str) -> None:
        now = datetime.now(timezone.utc)
        if parsed.astimezone(timezone.utc) > now + FUTURE_CLOCK_SKEW:
            self.error(
                where,
                f"{field} 晚于当前 UTC 时间 {now.isoformat()}，拒绝未来时间",
            )

    def commit_is_snapshot(self, resolved: str | None) -> bool:
        return bool(resolved and self.snapshot_full_commit and resolved == self.snapshot_full_commit)

    def hash_matches_snapshot(self, value) -> bool:
        commit = str(value or "").lower()
        return bool(
            HASH.fullmatch(commit)
            and self.snapshot_full_commit
            and self.snapshot_full_commit.startswith(commit)
        )

    def master_ref(self) -> str | None:
        for ref in ("refs/remotes/origin/master", "refs/heads/master"):
            if self.git("show-ref", "--verify", "--quiet", ref).returncode == 0:
                return ref
        self.error("git", "找不到 master 引用（origin/master 或 refs/heads/master），归属校验 fail-closed")
        return None

    def check_merged_head_relation(
        self, where: str, head: str, merge_commit: str, number: int,
    ) -> None:
        head_resolved = self.git("rev-parse", "--verify", f"{head}^{{commit}}")
        if head_resolved.returncode != 0:
            self.error(where, f"merged PR head 无法解析为本地 git 对象：{head}")
            return
        merge_resolved = self.git("rev-parse", "--verify", f"{merge_commit}^{{commit}}")
        if merge_resolved.returncode != 0:
            self.error(where, f"merge_commit 无法解析：{merge_commit}")
            return
        resolved_head = head_resolved.stdout.strip()
        resolved_merge = merge_resolved.stdout.strip()
        if resolved_head == resolved_merge:
            return
        if self.git("merge-base", "--is-ancestor",
                    resolved_head, resolved_merge).returncode == 0:
            return
        head_tree = self.git("rev-parse", "--verify", f"{resolved_head}^{{tree}}")
        merge_tree = self.git("rev-parse", "--verify", f"{resolved_merge}^{{tree}}")
        if head_tree.returncode == 0 and merge_tree.returncode == 0 \
                and head_tree.stdout.strip() == merge_tree.stdout.strip():
            return
        subject = self.git("show", "-s", "--format=%s", resolved_merge)
        if subject.returncode == 0 and f"(#{number})" in subject.stdout:
            return
        self.error(
            where,
            "merged PR head 与 merge_commit 没有可验证关系"
            "（相等、祖先、相同 tree 或 merge 提交声明该 PR 编号均不成立）",
        )

    def load(self, name: str) -> dict:
        path = self.root / "docs/product/delivery" / name
        if not path.exists():
            self.error(str(path), "文件不存在")
            return {}
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            self.error(str(path), f"JSON 解析失败：{exc}")
            return {}
        if not isinstance(data, dict):
            self.error(str(path), "顶层必须是 JSON 对象")
            return {}
        return data

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

    def is_current_real_evidence(self, evidence_id: str, item_id: str,
                                 ac_id: str | None = None) -> bool:
        ev = self.evidence.get(evidence_id)
        if not ev:
            return False
        if ev.get("type") != "real-flow" or self.enum_value(ev.get("level")) not in PROOF_LEVELS:
            return False
        if ev.get("freshness") != "current":
            return False
        matches = self.flow_by_evidence.get(evidence_id, [])
        if len(matches) != 1:
            return False
        flow = matches[0]
        if ev.get("source_commit") != flow.get("source_commit"):
            return False
        if not self.hash_matches_snapshot(ev.get("source_commit")):
            return False
        return (
            flow.get("freshness") == "current"
            and item_id in (flow.get("items") or [])
            and ac_id is not None
            and ac_id in (ev.get("ac_ids") or [])
            and ac_id in (flow.get("ac_ids") or [])
        )

    def is_current_proof(self, evidence_id: str, item_id: str, ac_id: str) -> bool:
        if not self.is_current_real_evidence(evidence_id, item_id, ac_id):
            return False
        return self.flow_by_evidence[evidence_id][0].get("result") == "passed"

    def proof_platforms(self, evidence_id: str, item_id: str, ac_id: str) -> set[str]:
        if not self.is_current_proof(evidence_id, item_id, ac_id):
            return set()
        flow = self.flow_by_evidence[evidence_id][0]
        platforms = flow.get("platforms")
        if not isinstance(platforms, list):
            return set()
        return {platform for platform in platforms if isinstance(platform, str)}

    # -- checks ----------------------------------------------------------
    def run(self) -> bool:
        try:
            return self._run()
        except (AttributeError, KeyError, TypeError, ValueError) as exc:
            self.error(
                "validator",
                f"数据结构畸形，校验中止：{type(exc).__name__}: {exc}；请修复字段类型后重试",
            )
            return self.report()

    def _run(self) -> bool:
        ledger = self.load("ledger.json")
        prs_doc = self.load("prs.json")
        ci_doc = self.load("ci-runs.json")
        flows_doc = self.load("real-flows.json")
        evidence_doc = self.load("evidence.json")
        decisions_doc = self.load("decisions.json")
        if not ledger:
            self.error(
                "ledger.json",
                "台账为空或缺少必需结构；至少需要 scope、source_snapshot、batches、items，"
                "不能把空对象当作成功",
            )

        self.prs = self.index(prs_doc.get("prs", []), "id", "prs.json")
        self.ci = self.index(ci_doc.get("runs", []), "id", "ci-runs.json")
        self.flows = self.index(flows_doc.get("flows", []), "id", "real-flows.json")
        self.evidence = self.index(evidence_doc.get("evidence", []), "id", "evidence.json")
        self.decisions = self.index(decisions_doc.get("decisions", []), "id", "decisions.json")
        prs, ci = self.prs, self.ci
        items = ledger.get("items", [])
        if not isinstance(items, list):
            self.error("ledger.json:items", "items 必须为数组")
            items = []
        for item in items:
            if not isinstance(item, dict):
                self.error("ledger.json:items", "items 只能包含对象")
                continue
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
        if not isinstance(rows, list):
            self.error(source, f"{key} 列表必须为数组")
            return {}
        out = {}
        for row in rows:
            if not isinstance(row, dict):
                self.error(source, "条目必须为对象")
                continue
            ident = row.get(key)
            if not isinstance(ident, str) or not ident:
                self.error(source, f"条目缺少 {key}")
                continue
            if ident in out:
                self.error(source, f"重复 {key}：{ident}")
            out[ident] = row
        return out

    def check_scope(self, ledger):
        scope = ledger.get("scope", {})
        items = ledger.get("items", [])
        if not isinstance(scope, dict):
            self.error("ledger.json:scope", "scope 必须为对象")
            scope = {}
        if not isinstance(items, list):
            self.error("ledger.json:items", "items 必须为数组")
            items = []
        items = [item for item in items if isinstance(item, dict)]
        if scope.get("total") != 161:
            self.error("ledger.json:scope", f"total 必须为 161，实际 {scope.get('total')!r}")
        if scope.get("core_total") != 160:
            self.error("ledger.json:scope", f"core_total 必须为 160，实际 {scope.get('core_total')!r}")
        if scope.get("supplemental_ids") != ["S01"]:
            self.error("ledger.json:scope", "supplemental_ids 必须恰为 ['S01']")
        expected_core = sorted(prefix for prefix in EXPECTED_PREFIX_COUNTS if prefix not in {"S", "Z"})
        core_prefixes = self.string_list(
            "ledger.json:scope", scope.get("core_prefixes"), "core_prefixes")
        if sorted(core_prefixes) != expected_core:
            self.error("ledger.json:scope", f"core_prefixes 必须为 {expected_core}")
        reverse_prefixes = self.string_list(
            "ledger.json:scope", scope.get("reverse_prefixes"), "reverse_prefixes")
        if reverse_prefixes != ["Z"]:
            self.error("ledger.json:scope", "reverse_prefixes 必须为 ['Z']")
        if scope.get("counts_by_prefix") != EXPECTED_PREFIX_COUNTS:
            self.error("ledger.json:scope",
                       f"counts_by_prefix 与固定范围不符：期望 {EXPECTED_PREFIX_COUNTS}，实际 {scope.get('counts_by_prefix')}")
        if scope.get("acceptance_scope_version") != ACCEPTANCE_SCOPE_VERSION:
            self.error("ledger.json:scope",
                       f"acceptance_scope_version 必须为 {ACCEPTANCE_SCOPE_VERSION}，"
                       f"实际 {scope.get('acceptance_scope_version')!r}")
        if scope.get("acceptance_criteria_total") != ACCEPTANCE_SCOPE_TOTAL:
            self.error("ledger.json:scope",
                       f"acceptance_criteria_total 必须为 {ACCEPTANCE_SCOPE_TOTAL}，"
                       f"实际 {scope.get('acceptance_criteria_total')!r}")
        if scope.get("source_package") != EXPECTED_SOURCE_PACKAGE:
            self.error(
                "ledger.json:scope",
                f"source_package 必须固定为 {EXPECTED_SOURCE_PACKAGE!r}，"
                f"实际 {scope.get('source_package')!r}",
            )
        if scope.get("source_package_sha256") != EXPECTED_SOURCE_PACKAGE_SHA256:
            self.error(
                "ledger.json:scope",
                f"source_package_sha256 必须固定为 {EXPECTED_SOURCE_PACKAGE_SHA256}，"
                f"实际 {scope.get('source_package_sha256')!r}",
            )

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
        raw_statuses = [item.get("status") for item in items]
        if any(not isinstance(status, str) for status in raw_statuses):
            self.error("ledger.json:items", "status 必须为非空字符串")
        actual_status_counts = Counter(
            status for status in raw_statuses if isinstance(status, str))
        if not isinstance(scope.get("counts_by_status"), dict):
            self.error("ledger.json:scope", "counts_by_status 必须为对象")
        expected_status_counts = {status: actual_status_counts.get(status, 0) for status in sorted(STATUSES)}
        if scope.get("counts_by_status") != expected_status_counts:
            self.error("ledger.json:scope",
                       f"counts_by_status 与 items 实际计数不符：期望 {expected_status_counts}，"
                       f"实际 {scope.get('counts_by_status')}")
        self.check_acceptance_scope(items)

        platforms = self.string_list(
            "ledger.json:platforms", ledger.get("platforms"), "platforms", allow_empty=False)
        if sorted(platforms) != sorted(PLATFORMS):
            self.error("ledger.json:platforms", f"平台列表必须为 {PLATFORMS}")
        role_values = self.string_list(
            "ledger.json:platform_role_values",
            ledger.get("platform_role_values"), "platform_role_values", allow_empty=False)
        if role_values != sorted(ROLES):
            self.error("ledger.json:platform_role_values", f"角色枚举必须为 {sorted(ROLES)}")
        if not isinstance(ledger.get("batch_close_rules"), list) or not ledger.get("batch_close_rules"):
            self.error("ledger.json:batch_close_rules", "批次关闭条件不能为空")
        evidence_levels = ledger.get("evidence_levels")
        if not isinstance(evidence_levels, dict) or set(evidence_levels) != LEVELS:
            self.error("ledger.json:evidence_levels", f"证据等级必须恰好覆盖 {sorted(LEVELS)}")
        elif any(not isinstance(text, str) or not text.strip() for text in evidence_levels.values()):
            self.error("ledger.json:evidence_levels", "证据等级说明必须为非空字符串")
        self.check_source_snapshot(ledger.get("source_snapshot") or {})

    def check_acceptance_scope(self, items):
        """Freeze the complete per-item AC ID set, not just its cardinality."""
        mapping = {}
        duplicate_ids = []
        total = 0
        for item in items:
            item_id = str(item.get("id"))
            acs = item.get("acceptance_criteria")
            if not isinstance(acs, list):
                self.error(f"ledger.json:item:{item_id}", "acceptance_criteria 必须为数组")
                continue
            frozen_acs = []
            ids = []
            for ac in acs:
                if not isinstance(ac, dict):
                    self.error(f"ledger.json:item:{item_id}", "acceptance_criteria 只能包含对象")
                    continue
                ac_id = str(ac.get("id"))
                ac_text = ac.get("text")
                if not isinstance(ac_text, str) or not ac_text.strip():
                    self.error(f"ledger.json:item:{item_id}", f"{ac_id} 缺少 text")
                    ac_text = "<invalid>"
                ids.append(ac_id)
                frozen_acs.append([ac_id, ac_text])
                if ids.count(ac_id) > 1:
                    duplicate_ids.append(ac_id)
            mapping[item_id] = sorted(frozen_acs, key=lambda entry: entry[0])
            total += len(ids)
        if total != ACCEPTANCE_SCOPE_TOTAL:
            self.error("ledger.json:scope",
                       f"acceptance criteria 总数必须为冻结值 {ACCEPTANCE_SCOPE_TOTAL}，实际 {total}")
        if duplicate_ids:
            self.error("ledger.json:scope",
                       f"acceptance criteria ID 重复，不能靠调整计数通过：{sorted(set(duplicate_ids))}")
        payload = json.dumps(mapping, ensure_ascii=False, sort_keys=True,
                             separators=(",", ":")).encode("utf-8")
        digest = hashlib.sha256(payload).hexdigest()
        if digest != ACCEPTANCE_SCOPE_SHA256:
            self.error(
                "ledger.json:scope",
                f"acceptance criteria 完整 ID/text 集与冻结权威映射 "
                f"v{ACCEPTANCE_SCOPE_VERSION} 不一致："
                f"期望 {ACCEPTANCE_SCOPE_SHA256}，实际 {digest}；新增/删除/替换 AC 必须显式升级权威映射",
            )

    def check_source_snapshot(self, snapshot):
        where = "ledger.json:source_snapshot"
        if not isinstance(snapshot, dict):
            self.error(where, "source_snapshot 必须为对象")
            return
        for field in ("repo", "branch", "commit", "commit_date", "observed_at",
                      "migrated_at", "note"):
            self.required_string(where, snapshot, field)
        self.snapshot = snapshot
        repo = str(snapshot.get("repo", ""))
        if not re.fullmatch(r"[^/\s]+/[^/\s]+", repo):
            self.error(where, f"repo 必须为 owner/name，实际 {repo!r}")
        elif repo != EXPECTED_SNAPSHOT_REPO:
            self.error(
                where,
                f"repo 必须固定为 {EXPECTED_SNAPSHOT_REPO}，实际 {repo!r}",
            )
        branch = str(snapshot.get("branch", ""))
        if not branch:
            return
        if branch not in ALLOWED_SNAPSHOT_BRANCHES:
            self.error(where,
                       f"branch 必须属于明确允许集合 {sorted(ALLOWED_SNAPSHOT_BRANCHES)}，实际 {branch!r}")
            return
        commit = str(snapshot.get("commit", ""))
        if not HASH.fullmatch(commit):
            self.error(where, f"commit 必须是 7–40 位十六进制，实际 {commit!r}")
            return
        commit_time = self.parse_time(where, snapshot.get("commit_date"), "commit_date")
        observed_time = self.parse_time(where, snapshot.get("observed_at"), "observed_at")
        self.parse_date(where, snapshot.get("migrated_at"), "migrated_at")
        self.snapshot_commit_time = commit_time
        self.snapshot_observed_time = observed_time
        if commit_time:
            self.validate_not_future(where, commit_time, "commit_date")
        if observed_time:
            self.validate_not_future(where, observed_time, "observed_at")
        if commit_time and observed_time and observed_time < commit_time:
            self.error(where, "observed_at 不能早于 commit_date")

        if self.git_root is None:
            self.error(where, "无法定位 git 仓库，不能验证 source_snapshot.commit")
            return
        full_commit = self.resolve_commit(where, commit, "commit")
        if full_commit is None:
            return
        self.snapshot_full_commit = full_commit
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
        available_refs: list[tuple[str, str]] = []
        for candidate in refs:
            if self.git("show-ref", "--verify", "--quiet", candidate).returncode != 0:
                continue
            resolved_ref = self.git("rev-parse", "--verify", f"{candidate}^{{commit}}")
            if resolved_ref.returncode != 0:
                self.error(where, f"无法解析声明分支引用 {candidate}")
                return
            available_refs.append((candidate, resolved_ref.stdout.strip()))
        if not available_refs:
            self.error(where,
                       f"找不到声明分支引用 {branch}（检查 refs/remotes/origin/{branch} 或 refs/heads/{branch}）；"
                       "current/PASS 归属校验失败即关闭")
            return
        ref_name, ref_tip = available_refs[0]
        if full_commit != ref_tip:
            self.error(
                where,
                f"commit {commit} 必须等于声明分支 {branch} 当前 tip "
                f"{ref_tip}（{ref_name}）；旧快照不能继续用于 current/PASS",
            )

    def check_prs(self, prs):
        for pid, pr in prs.items():
            where = f"prs.json:{pid}"
            m = PR_ID.match(pid)
            if not m:
                self.error(where, "PR id 必须为 PR-<repo>-<number>")
                continue
            for field in ("id", "repo", "number", "url", "state", "head",
                          "merge_commit", "state_freshness", "observed_at",
                          "state_verified_by"):
                if field not in pr:
                    self.error(where, f"缺少必填字段 {field}")
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
            state = self.enum_value(pr.get("state"))
            state_freshness = self.enum_value(pr.get("state_freshness"))
            verified_by = self.enum_value(pr.get("state_verified_by"))
            if state not in PR_STATES:
                self.error(where, f"非法 state：{pr.get('state')!r}")
            if state_freshness not in {"current", "stale"}:
                self.error(where, f"state_freshness 必须为 current/stale，实际 {pr.get('state_freshness')!r}")
            if state_freshness == "current":
                self.validate_current_time(where, pr.get("observed_at"), "observed_at")
            else:
                self.required_string(where, pr, "observed_at")
            if verified_by not in PR_VERIFIED_BY:
                self.error(where, f"非法 state_verified_by：{pr.get('state_verified_by')!r}")
            for field in ("head", "merge_commit"):
                value = pr.get(field)
                if value is not None and (
                    not isinstance(value, str) or not HASH.fullmatch(value)
                ):
                    self.error(where, f"{field} 必须是 7–40 位十六进制，实际 {value!r}")
            if state == "merged":
                head = pr.get("head")
                merge_commit = pr.get("merge_commit")
                if not isinstance(head, str) or not head.strip():
                    self.error(where, "merged PR 必须给出非空 head")
                if not isinstance(merge_commit, str) or not merge_commit.strip():
                    self.error(where, "merged PR 必须给出 merge_commit")
                if state_freshness == "current" and verified_by == "legacy-ledger":
                    self.error(where, "current 的 merged PR 不能由 legacy-ledger 验证")
                if repo == "pim-api" and isinstance(head, str) and HASH.fullmatch(head) \
                        and isinstance(merge_commit, str) and HASH.fullmatch(merge_commit):
                    self.check_merged_head_relation(
                        where, head, merge_commit, number)
                    resolved = self.git(
                        "rev-parse", "--verify", f"{merge_commit}^{{commit}}")
                    if resolved.returncode == 0:
                        ref = self.master_ref()
                        if ref and self.git("merge-base", "--is-ancestor",
                                            resolved.stdout.strip(), ref).returncode != 0:
                            self.error(where, "merge_commit 不属于 master")
            elif pr.get("merge_commit"):
                self.error(where, f"{pr.get('state')} PR 不应带 merge_commit")
            if state in {"open", "draft"} and not pr.get("head"):
                self.error(where, f"{pr.get('state')} PR 必须给出 head")
            if state_freshness == "current" and verified_by == "legacy-ledger":
                self.error(where, "current 状态必须由 git-history 或 github-api 验证")
            if state_freshness == "current" and state in {"open", "draft", "closed"} \
                    and not pr.get("head"):
                self.error(where, "current 实现 PR 必须给出可比较的 head")
            if state_freshness == "current" and state == "merged" \
                    and not pr.get("head"):
                self.error(where, "current merged PR 必须给出非空 head")

    def check_ci(self, ci, prs):
        for cid, run in ci.items():
            where = f"ci-runs.json:{cid}"
            m = CI_ID.match(cid)
            if not m:
                self.error(where, "CI id 必须为 CI-<run_id>")
                continue
            for field in ("id", "run_id", "repo", "repository", "workflow", "pr",
                          "head", "conclusion", "jobs", "observed_at", "freshness",
                          "head_is_current", "source"):
                if field not in run:
                    self.error(where, f"缺少必填字段 {field}")
            if run.get("run_id") != m.group(1):
                self.error(where, "run_id 与 id 不一致")
            repo = run.get("repo")
            if not isinstance(repo, str) or not repo.strip():
                self.error(where, "repo 必须为非空字符串")
                repo = None
            expected_repository = f"2746267826/{repo}" if repo else None
            if expected_repository and run.get("repository") != expected_repository:
                self.error(where, f"repository 必须为 {expected_repository}")
            self.required_string(where, run, "workflow")
            self.required_string(where, run, "source")
            pr_ref = self.enum_value(run.get("pr"))
            if pr_ref is None or pr_ref not in prs:
                self.error(where, f"引用了不存在的 PR：{run.get('pr')!r}")
            else:
                pr = prs[pr_ref]
                if repo and pr.get("repo") != repo:
                    self.error(where, f"repo {repo!r} 与 PR 仓库 {pr.get('repo')!r} 不一致")
                if run.get("head_is_current") and run.get("head") != pr.get("head"):
                    self.error(where, "head_is_current=true 但 head 与当前 PR head 不一致")
                if run.get("freshness") == "current" and run.get("head") != pr.get("head"):
                    self.error(where, "current CI head 必须与关联 PR head 一致")
                if run.get("freshness") == "current" \
                        and pr.get("state_freshness") != "current":
                    self.error(where, "current CI 关联的 PR 必须 state_freshness=current")
            freshness = self.enum_value(run.get("freshness"))
            conclusion = self.enum_value(run.get("conclusion"))
            if freshness not in FRESHNESS:
                self.error(where, f"非法 freshness：{run.get('freshness')!r}")
            if conclusion not in {"success", "failure", "cancelled", "in_progress", "skipped"}:
                self.error(where, f"非法 conclusion：{run.get('conclusion')!r}")
            if not isinstance(run.get("head_is_current"), bool):
                self.error(where, "head_is_current 必须为布尔值")
            if freshness == "current" and run.get("head_is_current") is not True:
                self.error(where, "current CI 必须 head_is_current=true")
            head = run.get("head")
            if not isinstance(head, str) or not head.strip():
                self.error(where, "head 不能为空")
            elif not HASH.fullmatch(head):
                self.error(where, f"head 必须是 7–40 位十六进制，实际 {run.get('head')!r}")
            if freshness == "stale" and run.get("head_is_current"):
                self.error(where, "stale run 不能标记为当前 head")
            raw_jobs = run.get("jobs")
            jobs = self.string_list(where, raw_jobs, "jobs", allow_empty=False)
            if isinstance(raw_jobs, list) and len(jobs) != len(raw_jobs):
                self.error(where, "jobs 含非法成员")
            if freshness == "current":
                self.validate_current_time(where, run.get("observed_at"), "observed_at")
                contract = CI_CONTRACT.get(repo)
                if contract is None:
                    self.error(
                        where,
                        f"current CI 的 repo {repo!r} 不在固定允许集合 "
                        f"{sorted(CI_CONTRACT)}",
                    )
                else:
                    if run.get("workflow") != contract["workflow"]:
                        self.error(
                            where,
                            f"current CI workflow 必须为 {contract['workflow']!r}，"
                            f"实际 {run.get('workflow')!r}",
                        )
                    unknown_jobs = sorted(set(jobs) - contract["jobs"])
                    if unknown_jobs:
                        self.error(
                            where,
                            f"current CI jobs 含未登记 job：{unknown_jobs}",
                        )
                    if "CI Gate" not in jobs:
                        self.error(where, "current CI jobs 必须包含 CI Gate")
            else:
                self.required_string(where, run, "observed_at")

    def check_flows(self):
        for fid, flow in self.flows.items():
            where = f"real-flows.json:{fid}"
            if not FLOW_ID.match(fid):
                self.error(where, "真实流程 id 必须为 RF-*")
            for field in ("title", "items", "ac_ids", "platforms", "result",
                          "freshness", "environment", "observed_at", "artifacts",
                          "limits", "evidence"):
                if field not in flow:
                    self.error(where, f"缺少必填字段 {field}")
            self.required_string(where, flow, "title")
            result = self.enum_value(flow.get("result"))
            freshness = self.enum_value(flow.get("freshness"))
            if result not in FLOW_RESULTS:
                self.error(where, f"非法 result：{flow.get('result')!r}")
            if freshness not in FRESHNESS:
                self.error(where, f"非法 freshness：{flow.get('freshness')!r}")
            if freshness == "current":
                self.validate_current_time(where, flow.get("observed_at"), "observed_at")
                resolved = self.resolve_commit(where, flow.get("source_commit"))
                if resolved and not self.commit_is_snapshot(resolved):
                    self.error(where, "current flow.source_commit 必须等于 source_snapshot.commit")
            else:
                self.required_string(where, flow, "observed_at")
                if flow.get("source_commit") is not None:
                    self.resolve_commit(where, flow.get("source_commit"))
            items = self.string_list(where, flow.get("items"), "items", allow_empty=False)
            for item_id in items:
                if item_id not in self.items_by_id:
                    self.error(where, f"引用不存在的 item：{item_id}")
            ac_ids = self.string_list(where, flow.get("ac_ids"), "ac_ids", allow_empty=False)
            actual_ac_ids = self.known_ac_ids()
            for ac_id in ac_ids:
                if not AC_ID.match(ac_id):
                    self.error(where, f"非法 AC ID：{ac_id!r}")
                elif ac_id not in actual_ac_ids:
                    self.error(where, f"引用不存在的 AC：{ac_id}")
            owners = {ac_id.split("-AC", 1)[0] for ac_id in ac_ids if AC_ID.match(ac_id)}
            if set(items) != owners:
                self.error(where, f"items 必须恰好等于 ac_ids 的归属条目：{sorted(set(items))} != {sorted(owners)}")
            platforms = self.string_list(where, flow.get("platforms"), "platforms", allow_empty=False)
            for platform in platforms:
                if platform not in PLATFORMS:
                    self.error(where, f"非法 platform：{platform!r}")
            artifacts = self.string_list(where, flow.get("artifacts"), "artifacts")
            if result in {"passed", "passed_legacy", "failed", "blocked"}:
                environment = flow.get("environment")
                if not isinstance(environment, str) or not environment.strip():
                    self.error(where, f"{flow.get('result')} 流程必须记录 environment")
                if not artifacts:
                    self.error(where, f"{flow.get('result')} 流程必须记录非空 artifacts")
            elif flow.get("environment") is not None \
                    and not isinstance(flow.get("environment"), str):
                self.error(where, "environment 必须为字符串或 null")
            limits = flow.get("limits")
            if not isinstance(limits, str) or not limits.strip():
                self.error(where, "必须写明未验证边界 limits")

            evid = flow.get("evidence")
            if not isinstance(evid, str) or not evid:
                self.error(where, "缺少 evidence 引用")
                continue
            ev = self.evidence.get(evid)
            if ev is None:
                self.error(where, f"悬空 evidence：{evid!r}")
                continue
            if ev.get("type") != "real-flow":
                self.error(where, f"evidence {evid} 类型不是 real-flow")
            if self.enum_value(ev.get("level")) not in PROOF_LEVELS:
                self.error(where, f"evidence {evid} 必须是 E4/E5/E6")
            if ev.get("freshness") != freshness:
                self.error(where, "flow 与 evidence 的 freshness 不一致")
            ev_items = self.string_list(f"{where}:evidence", ev.get("items"), "items")
            if set(ev_items) != set(items):
                self.error(where, f"flow.items 与 evidence.items 不一致：{items} != {ev.get('items')}")
            ev_ac_ids = self.string_list(f"{where}:evidence", ev.get("ac_ids"), "ac_ids")
            if set(ev_ac_ids) != set(ac_ids):
                self.error(where, f"flow.ac_ids 与 evidence.ac_ids 不一致：{ac_ids} != {ev_ac_ids}")
            if ev.get("source_commit") != flow.get("source_commit"):
                self.error(where, "flow.source_commit 与 evidence.source_commit 不一致")
            for item_id in items:
                item = self.items_by_id.get(item_id)
                if item is not None and evid not in (item.get("evidence") or []):
                    self.error(where, f"item {item_id} 未反向引用流程证据 {evid}")
            if result == "passed":
                if freshness != "current":
                    self.error(where, "passed 真实流程必须是 current freshness")
                if ev.get("freshness") != "current":
                    self.error(where, "passed 真实流程的 evidence 必须是 current")
                if not flow.get("environment"):
                    self.error(where, "passed 真实流程必须记录环境")
                if not artifacts:
                    self.error(where, "passed 真实流程必须记录产物")
            if result == "passed_legacy":
                if freshness != "stale":
                    self.error(where, "passed_legacy 必须是 stale freshness")
                if not flow.get("environment"):
                    self.error(where, "passed_legacy 必须记录当时的执行环境")
            if result == "not-run" and freshness != "not-verified":
                self.error(where, "not-run 流程必须是 not-verified freshness")
            superseded_by = flow.get("superseded_by")
            if superseded_by is not None:
                target = self.flows.get(str(superseded_by))
                if target is None:
                    self.error(where, f"superseded_by 指向不存在的流程：{superseded_by!r}")
                else:
                    if freshness != "stale":
                        self.error(where, "superseded_by 只能用于 stale 的旧流程")
                    if target.get("freshness") != "current" or target.get("result") != "passed":
                        self.error(where, "superseded_by 必须指向 current passed 流程")
                    if not set(ac_ids).issubset(set(target.get("ac_ids") or [])):
                        self.error(where, "superseded_by 未覆盖被取代流程的全部 AC")

        for evid, matches in self.flow_by_evidence.items():
            if len(matches) != 1:
                self.error("real-flows.json", f"evidence {evid} 必须恰好对应一个真实流程，实际 {len(matches)} 个")

    def check_decisions(self):
        referenced_by_items: dict[str, set[str]] = {}
        for item_id, item in self.items_by_id.items():
            decisions = item.get("decisions")
            if not isinstance(decisions, list):
                self.error(f"ledger.json:item:{item_id}", "decisions 必须为数组")
                continue
            for did in decisions:
                if not isinstance(did, str):
                    self.error(f"ledger.json:item:{item_id}", f"decisions 只能包含字符串：{did!r}")
                    continue
                referenced_by_items.setdefault(did, set()).add(item_id)
        for did, dec in self.decisions.items():
            where = f"decisions.json:{did}"
            if not DECISION_ID.match(did):
                self.error(where, "decision id 必须为 DEC-*")
            for field in ("id", "type", "status", "approved_by", "rationale",
                          "source", "items", "ac_ids", "evidence"):
                if field not in dec:
                    self.error(where, f"缺少必填字段 {field}")
            status = self.enum_value(dec.get("status"))
            if status not in {"approved", "pending", "recorded", "rejected"}:
                self.error(where, f"非法 status：{dec.get('status')!r}")
            self.required_string(where, dec, "type")
            self.required_string(where, dec, "rationale")
            self.required_string(where, dec, "source")
            if status == "approved":
                self.required_string(where, dec, "approved_by")
            elif dec.get("approved_by") is not None \
                    and not isinstance(dec.get("approved_by"), str):
                self.error(where, "approved_by 必须为字符串或 null")
            if status == "pending" and dec.get("approved_by"):
                self.error(where, "pending 决定不应有 approved_by")
            items = self.string_list(where, dec.get("items"), "items")
            for item_id in items:
                if item_id not in self.items_by_id:
                    self.error(where, f"悬空 item：{item_id}")
                elif did not in (self.items_by_id[item_id].get("decisions") or []):
                    self.error(where, f"item {item_id} 未反向引用决定 {did}")
            for item_id in referenced_by_items.get(did, set()):
                if item_id not in items:
                    self.error(where, f"item {item_id} 引用了决定，但决定 items 未包含该 item")
            ac_ids = self.string_list(where, dec.get("ac_ids"), "ac_ids")
            known_ac_ids = self.known_ac_ids()
            ac_owners: set[str] = set()
            for ac_id in ac_ids:
                if not AC_ID.match(ac_id):
                    self.error(where, f"非法 AC ID：{ac_id!r}")
                    continue
                if ac_id not in known_ac_ids:
                    self.error(where, f"悬空 AC：{ac_id}")
                    continue
                ac_owners.add(ac_id.split("-AC", 1)[0])
            if not ac_owners.issubset(set(items)):
                self.error(
                    where,
                    f"ac_ids 的归属 item 未包含在 items 中：{sorted(ac_owners - set(items))}",
                )
            if status == "pending" and len(ac_ids) > 1:
                self.error(
                    where,
                    f"pending 决定不能绑定多个 AC，实际 {ac_ids}；请拆分待裁决事项",
                )
            evid = dec.get("evidence")
            if not isinstance(evid, str) or not evid:
                self.error(where, "缺少非空 evidence 引用")
                continue
            ev = self.evidence.get(evid)
            if ev is None:
                self.error(where, f"悬空 evidence：{evid!r}")
            else:
                if ev.get("type") != "decision":
                    self.error(where, f"evidence {evid} 类型不是 decision")
                ev_items = ev.get("items")
                if not isinstance(ev_items, list):
                    self.error(where, "decision evidence.items 必须为数组")
                else:
                    ev_items = self.string_list(where, ev_items, "decision evidence.items")
                if isinstance(ev_items, list) and set(ev_items) != set(items):
                    self.error(where, f"decision.items 与 evidence.items 不一致：{items} != {ev.get('items')}")
                for item_id in items:
                    item = self.items_by_id.get(item_id)
                    if item is not None and evid not in (item.get("evidence") or []):
                        self.error(where, f"item {item_id} 未在 evidence 中引用决定证据 {evid}")
        for did, dec in self.decisions.items():
            if dec.get("status") != "pending":
                continue
            where = f"decisions.json:{did}"
            ac_ids = dec.get("ac_ids")
            if not isinstance(ac_ids, list):
                continue
            for ac_id in ac_ids:
                if not isinstance(ac_id, str) or not AC_ID.match(ac_id):
                    continue
                item_id = ac_id.split("-AC", 1)[0]
                item = self.items_by_id.get(item_id)
                if item is None:
                    continue
                ac = next(
                    (candidate for candidate in self.item_acceptance_criteria(item)
                     if isinstance(candidate, dict) and candidate.get("id") == ac_id),
                    None,
                )
                blocked = ac.get("blocked") if isinstance(ac, dict) else None
                if not isinstance(blocked, dict) or blocked.get("decision_id") != did:
                    self.error(
                        where,
                        f"pending 决定绑定 {ac_id}，但该 AC 的 blocked.decision_id 不是 {did}",
                    )

    def check_evidence(self, ci):
        decision_evidence = {
            dec.get("evidence") for dec in self.decisions.values()
            if isinstance(dec.get("evidence"), str)
        }
        for evid, ev in self.evidence.items():
            where = f"evidence.json:{evid}"
            if not EVIDENCE_ID.match(evid):
                self.error(where, "evidence id 必须以 EV- 开头")
            for field in ("id", "type", "level", "freshness", "claim", "source",
                          "observed_at", "items", "limits"):
                if field not in ev:
                    self.error(where, f"缺少必填字段 {field}")
            ev_type = self.enum_value(ev.get("type"))
            level = self.enum_value(ev.get("level"))
            freshness = self.enum_value(ev.get("freshness"))
            if ev_type not in EVIDENCE_TYPES:
                self.error(where, f"非法 evidence type：{ev_type!r}")
            if level not in LEVELS:
                self.error(where, f"非法 evidence level：{ev.get('level')!r}")
            if freshness not in FRESHNESS:
                self.error(where, f"非法 freshness：{ev.get('freshness')!r}")
            self.required_string(where, ev, "claim")
            self.required_string(where, ev, "source")
            limits = ev.get("limits")
            if not isinstance(limits, str) or not limits.strip():
                self.error(where, "缺少 limits")
            items = self.string_list(where, ev.get("items"), "items")
            if freshness == "current":
                self.validate_current_time(where, ev.get("observed_at"), "observed_at")
                resolved = self.resolve_commit(where, ev.get("source_commit"))
                if resolved and not self.commit_is_snapshot(resolved):
                    self.error(where, "current evidence.source_commit 必须等于 source_snapshot.commit")
            else:
                self.required_string(where, ev, "observed_at")
                if ev.get("source_commit") is not None:
                    self.resolve_commit(where, ev.get("source_commit"))
            for item_id in items:
                if item_id not in self.items_by_id:
                    self.error(where, f"引用不存在的 item：{item_id}")
            for forbidden in ("platforms", "flows"):
                if forbidden in ev:
                    self.error(where,
                               f"证据不得声明未文档化的 {forbidden} 字段；平台覆盖只能来自 real-flows.json")
            if ev_type == "ci":
                if level != "E3":
                    self.error(where, "CI 证据必须为 E3")
                ci_run = self.enum_value(ev.get("ci_run"))
                run = ci.get(ci_run) if ci_run else None
                if run is None:
                    self.error(where, f"悬空 ci_run：{ev.get('ci_run')!r}")
                else:
                    if not isinstance(run.get("jobs"), list) or not run.get("jobs"):
                        self.error(where, "关联 CI run 缺少 jobs")
                    for item_id in items:
                        item = self.items_by_id.get(item_id)
                        if item is not None and run.get("pr") not in (item.get("prs") or []):
                            self.error(where, f"CI 证据关联了未引用该 PR 的 item：{item_id}")
                    if freshness == "current":
                        if run.get("freshness") != "current" or run.get("head_is_current") is not True:
                            self.error(where, "current CI 证据必须指向 current 且 head_is_current=true 的 run")
            if ev_type == "real-flow":
                if level not in PROOF_LEVELS:
                    self.error(where, "real-flow 证据必须为 E4/E5/E6")
                ac_ids = self.string_list(where, ev.get("ac_ids"), "ac_ids", allow_empty=False)
                actual_ac_ids = self.known_ac_ids()
                for ac_id in ac_ids:
                    if not AC_ID.match(ac_id):
                        self.error(where, f"非法 AC ID：{ac_id!r}")
                    elif ac_id not in actual_ac_ids:
                        self.error(where, f"引用不存在的 AC：{ac_id}")
                owners = {ac_id.split("-AC", 1)[0] for ac_id in ac_ids if AC_ID.match(ac_id)}
                if set(items) != owners:
                    self.error(where, f"items 必须恰好等于 ac_ids 的归属条目：{sorted(set(items))} != {sorted(owners)}")
                matches = self.flow_by_evidence.get(evid, [])
                if len(matches) != 1:
                    self.error(where, f"real-flow 证据必须恰好被一个流程引用，实际 {len(matches)} 个")
                else:
                    flow = matches[0]
                    flow_items = flow.get("items")
                    flow_ac_ids = flow.get("ac_ids")
                    normalized_flow_items = {
                        item_id for item_id in flow_items
                        if isinstance(item_id, str)
                    } if isinstance(flow_items, list) else set()
                    normalized_flow_ac_ids = {
                        ac_id for ac_id in flow_ac_ids
                        if isinstance(ac_id, str)
                    } if isinstance(flow_ac_ids, list) else set()
                    if normalized_flow_items != set(items):
                        self.error(where, "real-flow evidence.items 与流程 items 不一致")
                    if normalized_flow_ac_ids != set(ac_ids):
                        self.error(where, "real-flow evidence.ac_ids 与流程 ac_ids 不一致")
            if ev_type == "decision":
                if level != "E0":
                    self.error(where, "decision 证据必须为 E0")
                if evid not in decision_evidence:
                    self.error(where, "decision 证据未被任何 decision 引用")
                if freshness == "current":
                    self.error(where, "decision E0 证据不得冒充 current 执行证据；请使用 stale 并保留决议来源")
            if level in PROOF_LEVELS and ev_type != "real-flow":
                self.error(where, "E4/E5/E6 证据必须是 real-flow")

    def check_batches(self, ledger):
        batches = ledger.get("batches", [])
        if not isinstance(batches, list):
            self.error("ledger.json:batches", "batches 必须为数组")
            return
        if any(not isinstance(batch, dict) for batch in batches):
            self.error("ledger.json:batches", "batches 只能包含对象")
            batches = [batch for batch in batches if isinstance(batch, dict)]
        self.batches_by_id = {str(batch.get("id")): batch for batch in batches}
        bad_ids = [batch.get("id") for batch in batches if not isinstance(batch.get("id"), str)]
        if bad_ids:
            self.error("ledger.json:batches", f"批次 id 必须为非空字符串：{bad_ids!r}")
        ids = [batch.get("id") for batch in batches if isinstance(batch.get("id"), str)]
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
            for field in ("id", "title", "goal"):
                self.required_string(where, batch, field)
            batch_status = self.enum_value(batch.get("status"))
            if batch_status not in {"open", "closed", "blocked"}:
                self.error(where, f"非法 status：{batch.get('status')!r}")
            batch_items = self.string_list(where, batch.get("items"), "items", allow_empty=False)
            members.extend(batch_items)
            for item_id in batch_items:
                if item_id not in self.items_by_id:
                    self.error(where, f"悬空 item：{item_id}")
            actual_prefixes = sorted({str(item_id)[0] for item_id in batch_items if item_id})
            expected_prefixes = EXPECTED_BATCH_PREFIXES.get(str(batch.get("id")))
            declared_prefixes = self.string_list(where, batch.get("prefixes"), "prefixes", allow_empty=False)
            if sorted(declared_prefixes) != actual_prefixes:
                self.error(where, f"prefixes 与实际 items 不符：期望 {actual_prefixes}，实际 {batch.get('prefixes')}")
            if expected_prefixes is not None and actual_prefixes != expected_prefixes:
                self.error(where,
                           f"批次前缀映射与固定契约不符：期望 {expected_prefixes}，实际 {actual_prefixes}")
            self.string_list(where, batch.get("close_criteria"), "close_criteria", allow_empty=False)
            present = [self.items_by_id[item_id] for item_id in batch_items if item_id in self.items_by_id]
            expected_status = self.expected_batch_status(present)
            if batch_status != expected_status:
                self.error(where, f"status 与 items 重算结果不符：期望 {expected_status}，实际 {batch.get('status')!r}")
            if batch_status == "closed":
                self.check_closed_batch(where, present)

        if len(members) != len(set(members)):
            self.error("ledger.json:batches", "同一 item 不能属于多个批次")
        if set(members) != set(self.items_by_id):
            self.error("ledger.json:batches",
                       f"批次并未恰好覆盖全部 item：缺少 {sorted(set(self.items_by_id) - set(members))}，"
                       f"多出 {sorted(set(members) - set(self.items_by_id))}")

    def expected_batch_status(self, items):
        statuses = [
            status for item in items
            if isinstance((status := item.get("status")), str)
        ]
        if any(status in {"FAIL", "NOT-VERIFIED"} for status in statuses):
            return "open"
        if any(status == "BLOCKED" for status in statuses):
            return "blocked"
        if statuses and all(status in {"PASS", "APPROVED-EXCEPTION"} for status in statuses):
            return "closed"
        return "open"

    def check_closed_batch(self, where, items):
        for item in items:
            self.check_closed_dependencies(where, item, set())
            item_id = str(item.get("id"))
            required = self.item_required_platforms(item)
            if not required:
                self.error(
                    where,
                    f"closed 批次中的 {item_id} 必须至少声明一个 required/read_only 平台，"
                    "不能把全部适用平台改为 coordination",
                )
                continue
            if item.get("status") == "PASS":
                if item.get("freshness") != "current":
                    self.error(where, f"PASS 项 {item.get('id')} 不是 current freshness，批次不能关闭")
                acs = self.item_acceptance_criteria(item)
                if any(ac.get("blocked") for ac in acs if isinstance(ac, dict)):
                    self.error(where, f"PASS 项 {item.get('id')} 仍有 AC 级 BLOCKED，批次不能关闭")
                if not all(ac.get("verified") for ac in acs if isinstance(ac, dict)):
                    self.error(where, f"PASS 项 {item.get('id')} 仍有未验证 AC，批次不能关闭")
            elif item.get("status") == "APPROVED-EXCEPTION":
                covered = self.current_passed_flow_platforms(item_id)
                missing = [platform for platform in required if platform not in covered]
                if missing:
                    self.error(
                        where,
                        f"APPROVED-EXCEPTION 项 {item_id} 缺少 current passed 平台证据：{missing}；"
                        "范围例外不能绕过批次关闭门禁",
                    )

    def check_closed_dependencies(self, where: str, item: dict, stack: set[str]) -> None:
        """Walk the whole dependency closure, including approved exceptions."""
        item_id = str(item.get("id"))
        if item_id in stack:
            self.error(where, f"依赖关系存在环：{' -> '.join([*stack, item_id])}")
            return
        stack = {*stack, item_id}
        deps = item.get("dependencies")
        if not isinstance(deps, list):
            return
        for dep in deps:
            target_id = str(dep).split(":", 1)[-1]
            target = self.items_by_id.get(target_id)
            if target is None or self.enum_value(target.get("status")) not in {"PASS", "APPROVED-EXCEPTION"}:
                self.error(where, f"closed 批次中 {item_id} 的依赖 {dep} 未满足")
                continue
            target_batch = target.get("batch")
            if target_batch != item.get("batch"):
                target_batch_status = self.batches_by_id.get(str(target_batch), {}).get("status")
                if target_batch_status != "closed":
                    self.error(where, f"closed 批次中 {item_id} 的跨批次依赖 {dep} 所在批次未 closed")
            self.check_closed_dependencies(where, target, stack)

    def check_items(self, ledger, prs, ci):
        items = ledger.get("items", [])
        if not isinstance(items, list):
            self.error("ledger.json:items", "items 必须为数组")
            return
        if len(items) != 161:
            self.error("ledger.json:items", f"必须恰好 161 项，实际 {len(items)}")
        seen = set()
        for item in items:
            if not isinstance(item, dict):
                self.error("ledger.json:items", "items 只能包含对象")
                continue
            if not isinstance(item.get("id"), str) or not item.get("id").strip():
                self.error("ledger.json:items", f"item id 必须为非空字符串：{item.get('id')!r}")
            item_id = str(item.get("id", "<missing>"))
            where = f"ledger.json:item:{item_id}"
            if not ITEM_ID.match(str(item_id)):
                self.error(where, f"非法 ID：{item_id!r}")
            if item_id in seen:
                self.error(where, "重复 ID")
            seen.add(item_id)
            for field in ITEM_FIELDS:
                if field not in item:
                    self.error(where, f"缺少必填字段 {field}")
            self.required_string(where, item, "title")
            self.required_string(where, item, "implementation")
            self.required_string(where, item, "next")
            self.required_string(where, item, "owner")
            basis = self.required_string(where, item, "implementation_basis").strip()
            if basis and not (
                basis.startswith("legacy-snapshot-") or basis.startswith("current-")
            ):
                self.error(where, "implementation_basis 必须以 legacy-snapshot- 或 current- 开头")
            if item.get("freshness") == "current" and basis.startswith("legacy-snapshot-"):
                self.error(where, "current 项不能继续使用 legacy implementation_basis")
            if not isinstance(item.get("collaborators"), list):
                self.error(where, "collaborators 必须为数组")
            else:
                self.string_list(where, item.get("collaborators"), "collaborators")
            status = self.enum_value(item.get("status"))
            legacy_status = self.enum_value(item.get("legacy_status"))
            freshness = self.enum_value(item.get("freshness"))
            if status not in STATUSES:
                self.error(where, f"非法 status：{item.get('status')!r}")
            if legacy_status not in STATUSES:
                self.error(where, f"非法 legacy_status：{item.get('legacy_status')!r}")
            if freshness not in FRESHNESS:
                self.error(where, f"非法 freshness：{item.get('freshness')!r}")
            legacy_source = item.get("legacy_source")
            if not isinstance(legacy_source, dict):
                self.error(where, "legacy_source 必须为对象")
            else:
                for field in ("file", "line", "section"):
                    if field not in legacy_source:
                        self.error(where, f"legacy_source 缺少 {field}")
                self.check_legacy_source(where, item_id, item.get("legacy_status"), legacy_source)
            if freshness == "stale" and status != legacy_status:
                self.error(where, "stale 项的 status 必须等于 legacy_status，不能静默改写历史")
            self.check_platforms(where, item, ledger)
            self.check_acceptance(where, item)
            self.check_ac_references(where, item)
            self.check_dependencies(where, item)
            self.check_item_refs(where, item, prs)
            self.check_status_rules(where, item, prs)
            self.check_batch_membership(where, item, ledger)
        self.check_dependency_cycles()

    def check_legacy_source(self, where: str, item_id, legacy_status, source: dict):
        file_value = source.get("file")
        line_value = source.get("line")
        if not isinstance(file_value, str) or not file_value.strip():
            self.error(where, "legacy_source.file 必须为非空字符串")
            return
        if file_value != LEGACY_SNAPSHOT_FILE:
            self.error(
                where,
                f"legacy_source.file 必须固定为历史快照 {LEGACY_SNAPSHOT_FILE}，实际 {file_value!r}",
            )
            return
        path = (self.root / file_value).resolve()
        try:
            path.relative_to(self.root.resolve())
        except ValueError:
            self.error(where, "legacy_source.file 必须位于仓库内")
            return
        if not path.is_file():
            self.error(where, f"legacy_source.file 不存在：{file_value}")
            return
        if not isinstance(line_value, int) or line_value < 1:
            self.error(where, "legacy_source.line 必须为正整数")
            return
        section_value = source.get("section")
        if not isinstance(section_value, str) or not section_value.strip():
            self.error(where, "legacy_source.section 必须为非空字符串")
            return
        lines = path.read_text(encoding="utf-8").splitlines()
        if line_value > len(lines):
            self.error(where, f"legacy_source.line 超出行数：{line_value} > {len(lines)}")
            return
        row = lines[line_value - 1]
        if not row.startswith(f"| {item_id} |"):
            self.error(where, f"legacy_source 行 {line_value} 不是 {item_id} 的旧台账行")
            return
        actual_section = self.section_at_line(lines, line_value)
        if actual_section != section_value:
            self.error(
                where,
                f"legacy_source.section 与第 {line_value} 行所在章节不符："
                f"{section_value!r} != {actual_section!r}",
            )
        fields = row.split("|")
        actual = fields[-3].strip() if len(fields) >= 3 else ""
        if actual != legacy_status:
            self.error(where,
                       f"legacy_status 与旧快照不一致：台账 {actual!r} != legacy_status {legacy_status!r}")
        audit_line = source.get("audit_line")
        audit_section = source.get("audit_section")
        if audit_line is None and audit_section is None:
            return
        if not isinstance(audit_line, int) or audit_line < 1:
            self.error(where, "legacy_source.audit_line 必须为正整数")
            return
        if not isinstance(audit_section, str) or not audit_section.strip():
            self.error(where, "legacy_source.audit_section 必须为非空字符串")
            return
        if audit_line > len(lines):
            self.error(where, f"legacy_source.audit_line 超出行数：{audit_line} > {len(lines)}")
            return
        audit_row = lines[audit_line - 1]
        if not audit_row.startswith(f"| {item_id} |"):
            self.error(where, f"legacy_source.audit_line {audit_line} 不是 {item_id} 的审计行")
            return
        actual_audit_section = self.section_at_line(lines, audit_line)
        if actual_audit_section != audit_section:
            self.error(
                where,
                f"legacy_source.audit_section 与第 {audit_line} 行所在章节不符："
                f"{audit_section!r} != {actual_audit_section!r}",
            )

    @staticmethod
    def section_at_line(lines: list[str], line_number: int) -> str | None:
        for index in range(line_number - 1, -1, -1):
            match = re.match(r"^#{2,6}\s+(.+?)\s*$", lines[index])
            if match:
                return match.group(1)
        return None

    def check_dependency_cycles(self):
        visited: set[str] = set()
        active: set[str] = set()
        reported: set[str] = set()

        def visit(item_id: str):
            if item_id in visited:
                return
            if item_id in active:
                if item_id not in reported:
                    self.error(f"ledger.json:item:{item_id}", "依赖关系存在环")
                    reported.add(item_id)
                return
            active.add(item_id)
            item = self.items_by_id.get(item_id, {})
            for dep in item.get("dependencies") or []:
                target = str(dep).split(":", 1)[-1]
                if target in self.items_by_id:
                    visit(target)
            active.remove(item_id)
            visited.add(item_id)

        for item_id in self.items_by_id:
            visit(item_id)

    def check_platforms(self, where, item, ledger):
        roles = item.get("platform_roles")
        if not isinstance(roles, dict):
            self.error(where, "platform_roles 必须为对象")
            return
        declared_raw = ledger.get("platforms")
        declared = set(PLATFORMS)
        if isinstance(declared_raw, list):
            declared = {platform for platform in declared_raw if isinstance(platform, str)}
        missing = declared - set(roles)
        extra = set(roles) - declared
        if missing:
            self.error(where, f"platform_roles 缺少平台：{sorted(missing)}")
        if extra:
            self.error(where, f"platform_roles 含未知平台：{sorted(extra)}")
        for platform, role in roles.items():
            if not isinstance(role, str) or role not in ROLES:
                self.error(where, f"平台 {platform} 的角色非法：{role!r}")
        active = [
            platform for platform, role in roles.items()
            if isinstance(role, str) and role in {"required", "read_only"}
        ]
        if not active and not any(role == "coordination" for role in roles.values()):
            self.error(where, "至少一个平台必须是 required/read_only/coordination")
        platform_scope = self.enum_value(item.get("platform_scope"))
        if platform_scope not in {"declared", "unconfirmed"}:
            self.error(where, f"非法 platform_scope：{item.get('platform_scope')!r}")
        if platform_scope == "unconfirmed" and item.get("status") != "BLOCKED":
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
            if not isinstance(ac, dict):
                self.error(where, "acceptance_criteria 只能包含对象")
                continue
            ac_id = str(ac.get("id", ""))
            if not AC_ID.match(ac_id):
                self.error(where, f"非法验收条件 ID：{ac_id!r}")
                continue
            if not ac_id.startswith(item_id + "-AC"):
                self.error(where, f"验收条件 {ac_id} 不属于该项")
            if ac_id in seen:
                self.error(where, f"验收条件 ID 重复：{ac_id}")
            seen.add(ac_id)
            self.required_string(where, ac, "text")
            if not isinstance(ac.get("verified"), bool):
                self.error(where, f"{ac_id} verified 必须为布尔值")
            refs = self.string_list(where, ac.get("evidence", []), f"{ac_id}.evidence")
            for ref in refs:
                if ref not in self.evidence:
                    self.error(where, f"{ac_id} 悬空 evidence：{ref!r}")
            qualified = [ref for ref in refs if self.is_current_proof(ref, item_id, ac_id)]
            if ac.get("verified"):
                if not qualified:
                    self.error(where, f"{ac_id} 标记 verified 但没有覆盖该 AC 的 current passed E4/E5/E6 真实流程证据")
                for ref in refs:
                    if not self.is_current_proof(ref, item_id, ac_id):
                        self.error(where, f"{ac_id} 的 verified 证据不合格或已过期：{ref!r}")
            blocked = ac.get("blocked")
            if blocked is not None:
                if not isinstance(blocked, dict):
                    self.error(where, f"{ac_id} blocked 必须为对象")
                    continue
                blocked_ids.append(ac_id)
                if ac.get("verified"):
                    self.error(where, f"{ac_id} 已 BLOCKED，不能同时标记 verified")
                reason = blocked.get("reason")
                if not isinstance(reason, str) or not reason.strip():
                    self.error(
                        where,
                        f"{ac_id} BLOCKED 无原因：blocked.reason 必须为非空字符串",
                    )
                unblock = blocked.get("unblock")
                if not isinstance(unblock, list) or not unblock:
                    self.error(where, f"{ac_id} BLOCKED 缺少 unblock 条件")
                else:
                    self.string_list(where, unblock, f"{ac_id}.blocked.unblock",
                                     allow_empty=False)
                did = blocked.get("decision_id")
                if not isinstance(did, str) or not did:
                    self.error(where, f"{ac_id} blocked.decision_id 必须为非空字符串")
                    continue
                dec = self.decisions.get(did)
                if dec is None:
                    self.error(where, f"{ac_id} 的 blocked.decision_id 悬空：{did!r}")
                else:
                    bound_ac_ids = dec.get("ac_ids")
                    if bound_ac_ids != [ac_id]:
                        self.error(
                            where,
                            f"{ac_id} 的阻塞决定 {did} 必须精确绑定该 AC："
                            f"ac_ids={bound_ac_ids!r} != {[ac_id]!r}",
                        )
                    if dec.get("status") != "pending":
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 必须为 pending，实际 {dec.get('status')!r}")
                    if item_id not in (dec.get("items") or []):
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 未双向引用 item {item_id}")
                    if did not in (item.get("decisions") or []):
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 未列入 item.decisions")
                    if dec.get("evidence") not in (item.get("evidence") or []):
                        self.error(where, f"{ac_id} 的阻塞决定 {did} 证据未列入 item.evidence")
            elif not ac.get("verified"):
                pending_ids.append(ac_id)

        if item.get("status") == "PASS":
            if blocked_ids:
                self.error(where, f"PASS 项存在 AC 级 BLOCKED：{blocked_ids}")
            if pending_ids:
                self.error(where, f"PASS 项存在未验证/待定 AC：{pending_ids}")
            if not all(isinstance(ac, dict) and ac.get("verified") for ac in acs):
                self.error(where, "PASS 项必须验证全部 acceptance criteria")

    def check_ac_references(self, where, item):
        """Every explicit X-ACnn mention must resolve to that item's AC list."""
        texts = [item.get("title", ""), item.get("implementation", ""),
                 item.get("dependency_notes", ""), item.get("next", ""),
                 item.get("legacy_next", "")]
        evidence_refs = item.get("evidence")
        if not isinstance(evidence_refs, list):
            evidence_refs = []
        for evid in evidence_refs:
            if isinstance(evid, str):
                texts.append(str(self.evidence.get(evid, {}).get("claim", "")))
        blob = " ".join(str(t) for t in texts)
        for match in re.finditer(r"\b([A-RSZ]\d{2})-AC(\d+)\b", blob):
            owner_id, number = match.group(1), int(match.group(2))
            ref = f"{owner_id}-AC{number:02d}"
            target = self.items_by_id.get(owner_id)
            if target is None:
                self.error(where, f"验收条件引用指向不存在的条目：{match.group(0)}")
                continue
            ac_ids = {
                ac.get("id")
                for ac in self.item_acceptance_criteria(target)
                if isinstance(ac, dict)
            }
            if ref not in ac_ids:
                self.error(where, f"悬空 AC 引用：{match.group(0)}（应为 {ref}）")
            dependencies = item.get("dependencies")
            if not isinstance(dependencies, list):
                dependencies = []
            if owner_id != item.get("id") and f"ITEM:{owner_id}" not in dependencies:
                self.warn(where, f"引用了其他条目的验收条件 {match.group(0)} 但未声明 ITEM:{owner_id} 依赖")

    def check_dependencies(self, where, item):
        deps = item.get("dependencies", [])
        if not isinstance(deps, list):
            self.error(where, "dependencies 必须为数组")
            return
        invalid = [dep for dep in deps if not isinstance(dep, str)]
        if invalid:
            self.error(where, f"依赖必须写成字符串 ITEM:<id>，实际 {invalid!r}")
            deps = [dep for dep in deps if isinstance(dep, str)]
        if len(deps) != len(set(deps)):
            self.error(where, "dependencies 含重复值")
        for dep in deps:
            if not dep.startswith("ITEM:"):
                self.error(where, f"依赖必须写成 ITEM:<id>，实际 {dep!r}")
                continue
            target = dep.split(":", 1)[1]
            if target == item.get("id"):
                self.error(where, "依赖不能指向自身")
            elif target not in self.items_by_id:
                self.error(where, f"悬空依赖：{dep}")

    def check_item_refs(self, where, item, prs):
        pr_refs = self.string_list(where, item.get("prs", []), "prs")
        ev_refs = self.string_list(where, item.get("evidence", []), "evidence")
        dec_refs = self.string_list(where, item.get("decisions", []), "decisions")
        for pid in pr_refs:
            if pid not in prs:
                self.error(where, f"悬空 PR：{pid!r}")
            else:
                pr = prs[pid]
                if self.enum_value(pr.get("state")) not in PR_STATES:
                    self.error(where, f"PR {pid} 状态非法")
        for evid in ev_refs:
            if evid not in self.evidence:
                self.error(where, f"悬空 evidence：{evid!r}")
            elif item.get("id") not in (self.evidence[evid].get("items") or []):
                self.error(where, f"evidence {evid} 未反向包含该 item")
        for did in dec_refs:
            if did not in self.decisions:
                self.error(where, f"悬空 decision：{did!r}")
            elif item.get("id") not in (self.decisions[did].get("items") or []):
                self.error(where, f"decision {did} 未反向包含该 item")
        legacy_refs = self.string_list(where, item.get("legacy_acceptance_refs", []),
                                       "legacy_acceptance_refs")
        ac_ids = {
            ac.get("id")
            for ac in self.item_acceptance_criteria(item)
            if isinstance(ac, dict)
        }
        for ref in legacy_refs:
            if ref not in ac_ids:
                self.error(where, f"legacy AC 引用悬空：{ref}")

    def check_status_rules(self, where, item, prs):
        status = item.get("status")
        item_id = str(item.get("id"))
        refs = item.get("evidence", [])
        if not isinstance(refs, list):
            self.error(where, "evidence 必须为数组")
            refs = []
        if status == "PASS":
            if item.get("freshness") != "current":
                self.error(where, "stale PASS：PASS 项必须是 current freshness")
            for ref in refs:
                if not isinstance(ref, str):
                    continue
                ev = self.evidence.get(ref, {})
                if ev.get("type") != "decision" \
                        and ev.get("freshness") != "current" \
                        and not self.is_superseded_stale_failure(ref, item_id):
                    self.error(where, f"PASS 引用了非 current 证据：{ref}")
                if ev.get("type") == "ci" and ev.get("freshness") != "current":
                    self.error(where, f"PASS 使用了 stale CI：{ref}")
                if ev.get("type") == "ci" and ev.get("freshness") == "current":
                    run = self.ci.get(ev.get("ci_run"), {})
                    if run.get("freshness") != "current" or run.get("head_is_current") is not True:
                        self.error(where, f"PASS 的 current CI 证据 {ref} 未绑定当前 head")
                    if run.get("conclusion") != "success":
                        self.error(where, f"PASS 的 current CI 证据 {ref} 不是成功 run")
            for flow in self.flows.values():
                flow_items = flow.get("items")
                if not isinstance(flow_items, list):
                    flow_items = []
                if item_id in flow_items \
                        and flow.get("freshness") == "current" \
                        and self.enum_value(flow.get("result")) in {"failed", "blocked"}:
                    self.error(where,
                               f"PASS 不能与同一 item 的 current {flow.get('result')} 流程共存："
                               f"{flow.get('id')}；旧失败必须标为 stale/superseded")
                if item_id in flow_items \
                        and flow.get("freshness") == "stale" \
                        and self.enum_value(flow.get("result")) in {"failed", "blocked"} \
                        and not flow.get("superseded_by"):
                    self.error(where,
                               f"PASS 仍有未声明 superseded_by 的 stale {flow.get('result')} 流程："
                               f"{flow.get('id')}")
            required = self.item_required_platforms(item)
            if not required:
                self.error(
                    where,
                    "PASS 必须至少声明一个 required/read_only 平台；"
                    "不能把全部适用平台改为 coordination 来清空覆盖集合",
                )
            covered = set()
            for ac in self.item_acceptance_criteria(item):
                if not isinstance(ac, dict):
                    continue
                ac_id = str(ac.get("id"))
                for ref in ac.get("evidence") or []:
                    covered.update(self.proof_platforms(ref, item_id, ac_id))
            missing = [p for p in required if p not in covered]
            if missing:
                self.error(where, f"PASS 缺少由 AC 关联 passed flow 覆盖的 required 平台：{missing}")
            pr_refs = item.get("prs")
            if not isinstance(pr_refs, list):
                pr_refs = []
            if not pr_refs:
                authorized = []
                for did in item.get("decisions") or []:
                    if not isinstance(did, str):
                        continue
                    dec = self.decisions.get(did)
                    if not dec or dec.get("type") != ITEM_ONLY_PASS_DECISION_TYPE:
                        continue
                    if dec.get("status") != "approved":
                        continue
                    if dec.get("items") != [item_id] or dec.get("ac_ids") != []:
                        continue
                    if dec.get("evidence") not in refs:
                        continue
                    authorized.append(did)
                if len(authorized) != 1:
                    self.error(
                        where,
                        "PASS 没有实现 PR 时必须恰好有一个 approved "
                        f"{ITEM_ONLY_PASS_DECISION_TYPE} 决定，且该决定只绑定本 item、"
                        "无 AC 绑定、证据已列入 item.evidence",
                    )
            for pid in pr_refs:
                if not isinstance(pid, str):
                    continue
                pr = prs.get(pid, {})
                if pr.get("state_freshness") != "current":
                    self.error(where, f"PASS 依赖的 PR {pid} 不是 current 状态")
                if pr.get("state") != "merged":
                    self.error(where, f"PASS 依赖的实现 PR {pid} 必须 merged，实际 {pr.get('state')!r}")
                merge_commit = str(pr.get("merge_commit") or "")
                if not HASH.fullmatch(merge_commit):
                    self.error(where, f"PASS 依赖的 PR {pid} 缺少可解析 merge_commit")
                elif pr.get("repo") == "pim-api":
                    resolved = self.git("rev-parse", "--verify", f"{merge_commit}^{{commit}}")
                    if resolved.returncode != 0:
                        self.error(where, f"PASS 依赖的 PR {pid} merge_commit 无法解析：{merge_commit}")
                    else:
                        ref = self.master_ref()
                        if ref and self.git("merge-base", "--is-ancestor",
                                            resolved.stdout.strip(), ref).returncode != 0:
                            self.error(where, f"PASS 依赖的 PR {pid} merge_commit 不属于 master")
                        if self.snapshot_full_commit is None:
                            self.error(
                                where,
                                f"PASS 依赖的 PR {pid} merge_commit 无法对照 source_snapshot.commit",
                            )
                        elif self.git(
                            "merge-base", "--is-ancestor",
                            resolved.stdout.strip(), self.snapshot_full_commit,
                        ).returncode != 0:
                            self.error(
                                where,
                                f"PASS 依赖的 PR {pid} merge_commit 晚于 source_snapshot.commit；"
                                "必须先刷新快照再 PASS",
                            )

        if status == "BLOCKED":
            blocked_reason = item.get("blocked_reason")
            if not isinstance(blocked_reason, str) or not blocked_reason.strip():
                self.error(where, "BLOCKED 无原因：blocked_reason 不能为空")
            unblock = item.get("unblock_conditions")
            if not isinstance(unblock, list) or not any(
                isinstance(entry, str) and entry.strip() for entry in unblock
            ):
                self.error(where, "BLOCKED 缺少可执行的 unblock_conditions")
            else:
                self.string_list(where, unblock, "unblock_conditions", allow_empty=False)
            pending = []
            decision_refs = item.get("decisions")
            if not isinstance(decision_refs, list):
                decision_refs = []
            for did in decision_refs:
                if not isinstance(did, str):
                    continue
                dec = self.decisions.get(did)
                if dec is None:
                    continue
                if dec.get("status") == "pending":
                    pending.append(did)
                    if item_id not in (dec.get("items") or []):
                        self.error(where, f"BLOCKED 的 pending decision {did} 未双向引用该 item")
                    if dec.get("evidence") not in refs:
                        self.error(where, f"BLOCKED 的 pending decision {did} 证据未列入 item.evidence")
            if not pending:
                self.error(where, "BLOCKED item 必须引用至少一个 pending decision")
        elif item.get("blocked_reason"):
            self.error(where, "只有 BLOCKED 项可以包含 blocked_reason")

        if status == "APPROVED-EXCEPTION":
            exc = item.get("exception")
            if not isinstance(exc, dict):
                self.error(where, "APPROVED-EXCEPTION 无批准：缺少 exception")
                return
            did = exc.get("decision_id")
            if not isinstance(did, str) or not did:
                self.error(where, "APPROVED-EXCEPTION decision_id 必须为非空字符串")
                return
            dec = self.decisions.get(did)
            if not dec:
                self.error(where, f"APPROVED-EXCEPTION 引用了不存在的决定：{did!r}")
                return
            if dec.get("status") != "approved":
                self.error(where, f"APPROVED-EXCEPTION 的批准记录状态是 {dec.get('status')!r}，不是 approved")
            if dec.get("type") != "scope-exception":
                self.error(
                    where,
                    f"APPROVED-EXCEPTION 的批准决定 {did} 必须为 "
                    f"type=scope-exception，实际 {dec.get('type')!r}",
                )
            exc_approved_by = exc.get("approved_by")
            dec_approved_by = dec.get("approved_by")
            if not isinstance(exc_approved_by, str) or not exc_approved_by.strip() \
                    or not isinstance(dec_approved_by, str) or not dec_approved_by.strip():
                self.error(where, "APPROVED-EXCEPTION 无批准：缺少 approved_by")
            approved_in = exc.get("approved_in")
            if not isinstance(approved_in, str) or not approved_in.strip():
                self.error(where, "APPROVED-EXCEPTION 缺少批准出处 approved_in")
            if dec.get("evidence") not in refs:
                self.error(where, "APPROVED-EXCEPTION 必须在 evidence 中引用批准记录")
            if item.get("id") not in (dec.get("items") or []):
                self.error(where, "APPROVED-EXCEPTION 的批准决定未双向引用该 item")
            if any(
                ac.get("verified")
                for ac in self.item_acceptance_criteria(item)
                if isinstance(ac, dict)
            ):
                self.error(where, "范围例外项不能把验收条件标为 verified")
        elif item.get("exception"):
            self.error(where, "只有 APPROVED-EXCEPTION 项可以包含 exception")

        current_real = [
            ref for ref in refs
            if (
                isinstance(ref, str)
                and
                self.evidence.get(ref, {}).get("type") == "real-flow"
                and self.evidence.get(ref, {}).get("freshness") == "current"
                and self.evidence.get(ref, {}).get("source_commit")
                and item_id in (self.evidence.get(ref, {}).get("items") or [])
            )
        ]
        if status in {"FAIL", "NOT-VERIFIED"} and item.get("freshness") == "current" and not current_real:
            self.error(where, "current freshness 但没有 current 真实证据")

    def check_batch_membership(self, where, item, ledger):
        batch_rows = ledger.get("batches")
        if not isinstance(batch_rows, list):
            return
        batches = {
            str(batch.get("id")): batch
            for batch in batch_rows
            if isinstance(batch, dict)
        }
        bid = item.get("batch")
        if not isinstance(bid, str) or bid not in batches:
            self.error(where, f"引用了不存在的批次：{bid!r}")
            return
        batch_items = batches[bid].get("items")
        if not isinstance(batch_items, list):
            batch_items = []
        if item.get("id") not in batch_items:
            self.error(where, f"批次 {bid} 的 items 未包含 {item.get('id')}")

    def check_cross_file(self, ledger):
        for evid, ev in self.evidence.items():
            ev_items = ev.get("items")
            if not isinstance(ev_items, list):
                continue
            for item_id in ev_items:
                if not isinstance(item_id, str):
                    continue
                item = self.items_by_id.get(item_id)
                if item is None:
                    self.error(f"evidence.json:{evid}", f"引用不存在的 item：{item_id}")
                    continue
                item_evidence = item.get("evidence")
                if not isinstance(item_evidence, list) or evid not in item_evidence:
                    self.error(f"evidence.json:{evid}", f"item {item_id} 未反向引用该证据")
        for flow_id, flow in self.flows.items():
            flow_items = flow.get("items")
            if not isinstance(flow_items, list):
                continue
            for item_id in flow_items:
                if not isinstance(item_id, str):
                    continue
                item = self.items_by_id.get(item_id)
                if item is None:
                    self.error(f"real-flows.json:{flow_id}", f"引用不存在的 item：{item_id}")
                    continue
                item_evidence = item.get("evidence")
                if not isinstance(item_evidence, list) or flow.get("evidence") not in item_evidence:
                    self.error(f"real-flows.json:{flow_id}",
                               f"item {item_id} 未反向引用流程证据 {flow.get('evidence')!r}")

    def check_render_view(self, ledger):
        rendered_view = ledger.get("rendered_view")
        if not isinstance(rendered_view, dict):
            self.error("ledger.json:rendered_view", "rendered_view 必须为对象")
            return
        rel = rendered_view.get("path")
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
        proc = subprocess.run(
            [sys.executable, str(renderer), "--root", str(self.root), "--stdout"],
            capture_output=True, text=True)
        if proc.returncode != 0:
            self.error("scripts/ci/render-delivery-ledger.py",
                       f"渲染失败：{(proc.stderr or proc.stdout).strip()}")
            return
        rendered = proc.stdout
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
