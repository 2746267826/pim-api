#!/usr/bin/env python3
"""Focused tests for the delivery ledger validator and renderer."""

from __future__ import annotations

import copy
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts/ci"))

import importlib.util

spec = importlib.util.spec_from_file_location(
    "delivery_validator", REPO / "scripts/ci/validate-delivery-ledger.py")
validator_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator_module)

FIXTURE_TIME = "2026-11-28T13:00:00+08:00"
SNAPSHOT_TIME = "2026-11-28T15:00:00+08:00"


class LedgerCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="pim-ledger-"))
        shutil.copytree(REPO / "docs/product/delivery", self.tmp / "docs/product/delivery")
        (self.tmp / "docs/product").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "docs/product/pim-delivery-ledger-20261125.md",
                    self.tmp / "docs/product/pim-delivery-ledger-20261125.md")
        shutil.copy(REPO / "docs/product/pim-delivery-ledger.md",
                    self.tmp / "docs/product/pim-delivery-ledger.md")
        (self.tmp / "scripts/ci").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "scripts/ci/render-delivery-ledger.py",
                    self.tmp / "scripts/ci/render-delivery-ledger.py")
        self.ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        self.evidence_path = self.tmp / "docs/product/delivery/evidence.json"
        self.flows_path = self.tmp / "docs/product/delivery/real-flows.json"
        self.prs_path = self.tmp / "docs/product/delivery/prs.json"
        self.ci_path = self.tmp / "docs/product/delivery/ci-runs.json"
        self.decisions_path = self.tmp / "docs/product/delivery/decisions.json"
        self.view_path = self.tmp / "docs/product/pim-delivery-ledger.md"
        self.git_root = self.tmp / "fixture-repo"
        self.git_root.mkdir()
        self.git("init", "-q")
        self.git("symbolic-ref", "HEAD", "refs/heads/master")
        self.git("config", "user.email", "ledger@example.invalid")
        self.git("config", "user.name", "Ledger Fixture")
        self.commit = self.make_commit("fixture base", "2026-11-28T08:00:00+08:00")
        self.second_commit = self.make_commit("fixture later", "2026-11-28T09:00:00+08:00")
        self.commit_date = subprocess.run(
            ["git", "-C", str(self.git_root), "show", "-s", "--format=%cI", self.commit],
            capture_output=True, text=True, check=True).stdout.strip()
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"].update({
            "branch": "master",
            "commit": self.commit,
            "commit_date": self.commit_date,
            "observed_at": SNAPSHOT_TIME,
        })
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_baseline_valid()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def git(self, *args):
        env = os.environ.copy()
        env["GIT_AUTHOR_DATE"] = "2026-11-28T08:00:00+08:00"
        env["GIT_COMMITTER_DATE"] = "2026-11-28T08:00:00+08:00"
        return subprocess.run(
            ["git", "-C", str(self.git_root), *args],
            capture_output=True, text=True, check=True, env=env)

    def make_commit(self, message, date):
        env = os.environ.copy()
        env["GIT_AUTHOR_DATE"] = date
        env["GIT_COMMITTER_DATE"] = date
        subprocess.run(
            ["git", "-C", str(self.git_root), "commit", "--allow-empty", "-q", "-m", message],
            capture_output=True, text=True, check=True, env=env)
        return subprocess.run(
            ["git", "-C", str(self.git_root), "rev-parse", "HEAD"],
            capture_output=True, text=True, check=True).stdout.strip()

    def load(self, path):
        return json.loads(path.read_text(encoding="utf-8"))

    def save(self, path, data):
        path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n",
                        encoding="utf-8")

    def render(self):
        proc = subprocess.run(
            [sys.executable, str(self.tmp / "scripts/ci/render-delivery-ledger.py"),
             "--root", str(self.tmp), "--output", str(self.view_path)],
            capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)

    def run_validator(self, git_root=None):
        err = io.StringIO()
        out = io.StringIO()
        with redirect_stderr(err), redirect_stdout(out):
            ok = validator_module.Validator(
                self.tmp, git_root=git_root or self.git_root).run()
        self.last_stderr = err.getvalue()
        return ok

    def assert_baseline_valid(self):
        ok = self.run_validator()
        self.assertTrue(ok, self.last_stderr)

    def assert_fails_with(self, needle):
        ok = self.run_validator()
        self.assertFalse(ok, "validator should fail")
        self.assertIn(needle, self.last_stderr)

    def item(self, ledger, item_id):
        return next(i for i in ledger["items"] if i["id"] == item_id)

    def recount_status(self, ledger):
        counts = {status: 0 for status in validator_module.STATUSES}
        for item in ledger["items"]:
            if item.get("status") in counts:
                counts[item["status"]] += 1
        ledger["scope"]["counts_by_status"] = counts

    def add_current_flow(self, ledger, evidence, flows, item_id, *,
                         suffix, result, ac_ids, platforms,
                         include_in_item_evidence=True):
        ev_id = f"EV-RF-TEST-{suffix}"
        flow_id = f"RF-TEST-{suffix}"
        evidence["evidence"].append({
            "id": ev_id,
            "type": "real-flow",
            "level": "E4",
            "freshness": "current",
            "claim": f"{item_id} current {result} fixture",
            "source": "test fixture",
            "source_commit": self.commit,
            "observed_at": FIXTURE_TIME,
            "items": [item_id],
            "ac_ids": list(ac_ids),
            "limits": "test fixture only; not product acceptance",
        })
        flows["flows"].append({
            "id": flow_id,
            "title": f"{item_id} current {result} flow",
            "items": [item_id],
            "ac_ids": list(ac_ids),
            "platforms": list(platforms),
            "result": result,
            "freshness": "current",
            "environment": "test fixture",
            "source_commit": self.commit,
            "observed_at": FIXTURE_TIME,
            "artifacts": ["fixture output"],
            "limits": "test fixture only; not product acceptance",
            "evidence": ev_id,
        })
        if include_in_item_evidence:
            self.item(ledger, item_id)["evidence"].append(ev_id)
        return ev_id, flow_id

    def attach_pr(self, ledger, prs, *, state, repo="pim-api", number=9999,
                  merge_commit=None, verified_by="github-api"):
        pid = f"PR-{repo}-{number}"
        pr = {
            "id": pid,
            "repo": repo,
            "number": number,
            "repository": f"2746267826/{repo}",
            "url": f"https://github.com/2746267826/{repo}/pull/{number}",
            "state": state,
            "head": self.second_commit,
            "merge_commit": merge_commit,
            "observed_at": FIXTURE_TIME,
            "state_freshness": "current",
            "state_verified_by": verified_by,
        }
        prs["prs"].append(pr)
        self.item(ledger, "A10")["prs"].append(pid)
        return pid

    def add_current_ci_evidence(self, ledger, evidence, ci, item_id, pr_id):
        run_id = "900000"
        evidence["evidence"].append({
            "id": f"EV-CI-{run_id}",
            "type": "ci",
            "level": "E3",
            "freshness": "current",
            "claim": "current CI fixture",
            "source": "test fixture",
            "source_commit": self.commit,
            "observed_at": FIXTURE_TIME,
            "items": [item_id],
            "ci_run": f"CI-{run_id}",
            "limits": "CI is not real-flow acceptance",
        })
        ci["runs"].append({
            "id": f"CI-{run_id}",
            "run_id": run_id,
            "repo": "pim-api",
            "repository": "2746267826/pim-api",
            "workflow": "CI",
            "pr": pr_id,
            "head": self.second_commit,
            "conclusion": "success",
            "jobs": ["CI Gate", "build-api"],
            "observed_at": FIXTURE_TIME,
            "freshness": "current",
            "head_is_current": True,
            "source": "test fixture",
        })
        self.item(ledger, item_id)["evidence"].append(f"EV-CI-{run_id}")
        return f"EV-CI-{run_id}"

    def upgrade_to_pass(self, item_id="A10"):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, item_id)
        self.assertEqual(item["prs"], [], "fixture item must not depend on PR state")
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        platforms = [
            platform for platform, role in item["platform_roles"].items()
            if role in {"required", "read_only"}
        ]
        ac_ids = [ac["id"] for ac in item["acceptance_criteria"]]
        audit_id = item["evidence"][0]
        audit = next(row for row in evidence["evidence"] if row["id"] == audit_id)
        audit["freshness"] = "current"
        audit["source_commit"] = self.commit
        audit["observed_at"] = FIXTURE_TIME
        ev_id, _ = self.add_current_flow(
            ledger, evidence, flows, item_id,
            suffix=item_id, result="passed", ac_ids=ac_ids, platforms=platforms)
        item["status"] = "PASS"
        item["freshness"] = "current"
        item["implementation_basis"] = f"current-real-flow-{item_id}"
        for ac in item["acceptance_criteria"]:
            ac.pop("blocked", None)
            ac["verified"] = True
            ac["evidence"] = [ev_id]
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        return ledger, evidence, flows

    # -- scope and AC freeze --------------------------------------------
    def test_duplicate_id(self):
        ledger = self.load(self.ledger_path)
        ledger["items"].append(copy.deepcopy(ledger["items"][0]))
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("重复 ID")

    def test_extra_item_breaks_exact_scope(self):
        ledger = self.load(self.ledger_path)
        extra = copy.deepcopy(ledger["items"][0])
        extra["id"] = "A99"
        extra["acceptance_criteria"][0]["id"] = "A99-AC01"
        ledger["items"].append(extra)
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("ID 集合与固定范围不符")

    def test_same_count_id_swap_breaks_exact_scope(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A10")["id"] = "A99"
        validator = validator_module.Validator(self.tmp, git_root=self.git_root)
        validator.check_scope(ledger)
        self.assertIn("ID 集合与固定范围不符", "\n".join(validator.errors))

    def test_missing_acceptance_criteria(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"] = []
        ledger["scope"]["acceptance_criteria_total"] = 726
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("acceptance_criteria 不能为空")

    def test_pass_fixture_cannot_delete_one_ac_after_recomputing_counts(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        item = self.item(ledger, "A10")
        removed = item["acceptance_criteria"].pop()["id"]
        flow = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")
        flow["ac_ids"].remove(removed)
        ev = next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")
        ev["ac_ids"].remove(removed)
        ledger["scope"]["acceptance_criteria_total"] = 730
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("完整 ID 集与冻结权威映射")

    def test_pass_fixture_cannot_swap_ac_id_at_same_count(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        item = self.item(ledger, "A10")
        old = item["acceptance_criteria"][-1]["id"]
        new = "A10-AC99"
        item["acceptance_criteria"][-1]["id"] = new
        for row in (next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10"),
                    next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")):
            row["ac_ids"][row["ac_ids"].index(old)] = new
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("完整 ID 集与冻结权威映射")

    # -- basic structural negatives ------------------------------------
    def test_stale_pass_is_rejected(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["status"] = "PASS"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("stale PASS")

    def test_blocked_without_reason(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "Q01")["blocked_reason"] = ""
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("BLOCKED 无原因")

    def test_blocked_item_requires_pending_decision(self):
        ledger = self.load(self.ledger_path)
        decisions = self.load(self.decisions_path)
        next(d for d in decisions["decisions"] if d["id"] == "DEC-Q01")["status"] = "recorded"
        self.save(self.ledger_path, ledger)
        self.save(self.decisions_path, decisions)
        self.render()
        self.assert_fails_with("BLOCKED item 必须引用至少一个 pending decision")

    def test_approved_exception_without_decision(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "B15")["exception"]["decision_id"] = "DEC-MISSING"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("引用了不存在的决定")

    def test_approved_exception_with_pending_decision(self):
        ledger = self.load(self.ledger_path)
        decisions = self.load(self.decisions_path)
        dec = next(d for d in decisions["decisions"] if d["id"] == "DEC-B15")
        dec["status"] = "pending"
        dec["approved_by"] = None
        self.save(self.ledger_path, ledger)
        self.save(self.decisions_path, decisions)
        self.render()
        self.assert_fails_with("不是 approved")

    def test_dangling_evidence(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["evidence"].append("EV-DOES-NOT-EXIST")
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("悬空 evidence")

    def test_dangling_pr(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["prs"].append("PR-pim-api-999999")
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("悬空 PR")

    def test_invalid_platform_role(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["platform_roles"]["android"] = "complete"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("角色非法")

    def test_missing_acceptance_text(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"][0]["text"] = ""
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("缺少 text")

    def test_verified_ac_without_current_proof(self):
        ledger = self.load(self.ledger_path)
        ac = self.item(ledger, "A01")["acceptance_criteria"][0]
        ac["verified"] = True
        ac["evidence"] = ["EV-A01-AUDIT"]
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed E4/E5/E6")

    def test_dependency_cycle_self(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["dependencies"] = ["ITEM:A01"]
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("依赖不能指向自身")

    def test_dependency_cycle(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["dependencies"] = ["ITEM:A02"]
        self.item(ledger, "A02")["dependencies"] = ["ITEM:A01"]
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("依赖关系存在环")

    def test_legacy_status_rewritten_while_stale(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["status"] = "NOT-VERIFIED"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("stale 项的 status 必须等于 legacy_status")

    def test_legacy_status_must_match_source_row(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "A01")
        item["legacy_status"] = "NOT-VERIFIED"
        item["status"] = "NOT-VERIFIED"
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("legacy_status 与旧快照不一致")

    def test_legacy_source_line_must_exist(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["legacy_source"]["line"] = 9999
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("legacy_source.line 超出行数")

    def test_legacy_source_section_must_be_nonempty(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["legacy_source"]["section"] = ""
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("legacy_source.section 必须为非空字符串")

    def test_batches_must_be_array_without_renderer_crash(self):
        ledger = self.load(self.ledger_path)
        ledger["batches"] = "not-an-array"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("batches 必须为数组")

    def test_rendered_view_manual_edit(self):
        self.view_path.write_text("# hand written\n", encoding="utf-8")
        self.assert_fails_with("缺少生成标记")

    def test_unknown_evidence_level(self):
        evidence = self.load(self.evidence_path)
        target = next(e for e in evidence["evidence"] if e["type"] == "ci")
        target["level"] = "E9"
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("非法 evidence level")

    def test_stale_ci_marked_current_head(self):
        runs = self.load(self.ci_path)
        runs["runs"][0]["head_is_current"] = True
        self.save(self.ci_path, runs)
        self.render()
        self.assert_fails_with("stale run 不能标记为当前 head")

    def test_blocked_acceptance_without_reason(self):
        ledger = self.load(self.ledger_path)
        for ac in self.item(ledger, "R05")["acceptance_criteria"]:
            if ac["id"] == "R05-AC05":
                ac["blocked"]["reason"] = ""
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("BLOCKED 无原因")

    def test_blocked_acceptance_without_unblock(self):
        ledger = self.load(self.ledger_path)
        for ac in self.item(ledger, "R05")["acceptance_criteria"]:
            if ac["id"] == "R05-AC05":
                ac["blocked"]["unblock"] = []
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("缺少 unblock 条件")

    def test_blocked_acceptance_requires_pending_decision(self):
        ledger = self.load(self.ledger_path)
        decisions = self.load(self.decisions_path)
        next(d for d in decisions["decisions"] if d["id"] == "DEC-R05-AC05")["status"] = "recorded"
        self.save(self.ledger_path, ledger)
        self.save(self.decisions_path, decisions)
        self.render()
        self.assert_fails_with("必须为 pending")

    # -- PASS closure ---------------------------------------------------
    def test_current_pass_positive(self):
        self.upgrade_to_pass()
        self.assert_baseline_valid()

    def test_pass_requires_every_acceptance_criterion(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "A10")
        item["status"] = "PASS"
        item["freshness"] = "current"
        item["implementation_basis"] = "current-real-flow-A10"
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("PASS 项存在未验证/待定 AC")

    def test_pass_rejects_blocked_acceptance(self):
        ledger, _, _ = self.upgrade_to_pass()
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["verified"] = False
        ac["evidence"] = []
        ac["blocked"] = {
            "reason": "fixture blocker",
            "unblock": ["fixture unblock"],
            "decision_id": "DEC-R05-AC05",
        }
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("PASS 项存在 AC 级 BLOCKED")

    def test_pass_requires_all_required_platforms(self):
        _, _, flows = self.upgrade_to_pass()
        target = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")
        target["platforms"] = ["web"]
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("PASS 缺少由 AC 关联 passed flow 覆盖的 required 平台")

    def test_verified_ac_rejects_stale_ref(self):
        ledger, evidence, _ = self.upgrade_to_pass()
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["evidence"].append("EV-A10-AUDIT")
        audit = next(e for e in evidence["evidence"] if e["id"] == "EV-A10-AUDIT")
        audit["freshness"] = "stale"
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("verified 证据不合格或已过期")

    def test_flow_and_evidence_items_must_match(self):
        flows = self.load(self.flows_path)
        flows["flows"][0]["items"].append("R05")
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("flow.items 与 evidence.items 不一致")

    def test_decision_requires_item_reverse_reference(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "R05")["decisions"] = []
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("item R05 未反向引用决定 DEC-R05-AC05")

    def test_status_counts_are_recomputed(self):
        ledger = self.load(self.ledger_path)
        ledger["scope"]["counts_by_status"]["FAIL"] -= 1
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("counts_by_status 与 items 实际计数不符")

    def test_prefix_counts_are_recomputed(self):
        ledger = self.load(self.ledger_path)
        ledger["scope"]["counts_by_prefix"]["A"] -= 1
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("counts_by_prefix 与 items 实际计数不符")

    def test_false_closed_batch_is_rejected(self):
        ledger = self.load(self.ledger_path)
        next(b for b in ledger["batches"] if b["id"] == "B1")["status"] = "closed"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("status 与 items 重算结果不符")

    def test_batch_prefix_mapping_is_fixed(self):
        ledger = self.load(self.ledger_path)
        b1 = next(b for b in ledger["batches"] if b["id"] == "B1")
        b2 = next(b for b in ledger["batches"] if b["id"] == "B2")
        a01, d01 = self.item(ledger, "A01"), self.item(ledger, "D01")
        a01["batch"], d01["batch"] = "B2", "B1"
        b1["items"][b1["items"].index("A01")] = "D01"
        b2["items"][b2["items"].index("D01")] = "A01"
        b1["prefixes"] = ["B", "C", "D"]
        b2["prefixes"] = ["A", "E", "F"]
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("批次前缀映射与固定契约不符")

    def test_close_criteria_text_is_not_frozen_but_must_be_unique(self):
        ledger = self.load(self.ledger_path)
        b1 = next(b for b in ledger["batches"] if b["id"] == "B1")
        b1["close_criteria"][0] = "fixture 描述性关闭条件"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_baseline_valid()

        b1["close_criteria"].append(b1["close_criteria"][0])
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("close_criteria 含重复值")

    def test_closed_batch_checks_approved_exception_dependencies(self):
        ledger = self.load(self.ledger_path)
        validator = validator_module.Validator(self.tmp, git_root=self.git_root)
        validator.items_by_id = {i["id"]: i for i in ledger["items"]}
        validator.batches_by_id = {b["id"]: b for b in ledger["batches"]}
        validator.check_closed_batch("test", [validator.items_by_id["C14"]])
        self.assertIn("依赖 ITEM:C12 未满足", "\n".join(validator.errors))

    def test_closed_batch_checks_cross_batch_dependency(self):
        ledger = self.load(self.ledger_path)
        validator = validator_module.Validator(self.tmp, git_root=self.git_root)
        validator.items_by_id = {i["id"]: copy.deepcopy(i) for i in ledger["items"]}
        validator.batches_by_id = {b["id"]: b for b in ledger["batches"]}
        validator.items_by_id["C12"]["status"] = "PASS"
        validator.check_closed_batch("test", [validator.items_by_id["F02"]])
        self.assertIn("跨批次依赖 ITEM:C12 所在批次未 closed", "\n".join(validator.errors))

    def test_closed_batch_checks_transitive_dependencies(self):
        ledger = self.load(self.ledger_path)
        validator = validator_module.Validator(self.tmp, git_root=self.git_root)
        validator.items_by_id = {i["id"]: copy.deepcopy(i) for i in ledger["items"]}
        validator.batches_by_id = {b["id"]: b for b in ledger["batches"]}
        validator.items_by_id["C12"]["status"] = "PASS"
        validator.items_by_id["C12"]["dependencies"] = ["ITEM:A01"]
        validator.check_closed_batch("test", [validator.items_by_id["C14"]])
        self.assertIn("依赖 ITEM:A01 未满足", "\n".join(validator.errors))

    def test_current_item_cannot_use_legacy_basis(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A10")["freshness"] = "current"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("current 项不能继续使用 legacy implementation_basis")

    def test_pr_requires_observation_time(self):
        prs = self.load(self.prs_path)
        prs["prs"][0]["observed_at"] = None
        self.save(self.prs_path, prs)
        self.render()
        self.assert_fails_with("observed_at")

    def test_current_failed_flow_can_keep_item_current(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "A10")
        item["status"] = "FAIL"
        item["freshness"] = "current"
        item["implementation_basis"] = "current-real-flow-A10-failed"
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-FAILED", result="failed",
            ac_ids=["A10-AC01"], platforms=["api"])
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_baseline_valid()

    def test_failed_flow_requires_environment_and_artifacts(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-FAILED-EMPTY", result="failed",
            ac_ids=["A10-AC01"], platforms=["api"])
        flow = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10-FAILED-EMPTY")
        flow["artifacts"] = []
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("failed 流程必须记录非空 artifacts")

        flow["artifacts"] = ["fixture output"]
        flow["environment"] = ""
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("failed 流程必须记录 environment")

    # -- AC-linked evidence and platform coverage ----------------------
    def test_generic_flow_does_not_cover_undeclared_ac(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        flow = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")
        ev = next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")
        flow["ac_ids"] = ["A10-AC01"]
        ev["ac_ids"] = ["A10-AC01"]
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed")

    def test_e1_cannot_substitute_for_ac_proof(self):
        ledger, evidence, _ = self.upgrade_to_pass()
        self.item(ledger, "A10")["acceptance_criteria"][0]["evidence"] = ["EV-A10-AUDIT"]
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed")

    def test_current_ci_cannot_substitute_for_ac_proof(self):
        ledger = self.load(self.ledger_path)
        prs = self.load(self.prs_path)
        evidence = self.load(self.evidence_path)
        ci = self.load(self.ci_path)
        pr_id = self.attach_pr(ledger, prs, state="open")
        ci_ev = self.add_current_ci_evidence(ledger, evidence, ci, "A10", pr_id)
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["verified"] = True
        ac["evidence"] = [ci_ev]
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.save(self.evidence_path, evidence)
        self.save(self.ci_path, ci)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed")

    def test_current_failed_flow_cannot_substitute_for_ac_proof(self):
        ledger = self.load(self.ledger_path)
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        ev_id, _ = self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-FAILED-AC", result="failed",
            ac_ids=["A10-AC01"], platforms=["api"])
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["verified"] = True
        ac["evidence"] = [ev_id]
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed")

    def test_current_blocked_flow_cannot_substitute_for_ac_proof(self):
        ledger = self.load(self.ledger_path)
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        ev_id, _ = self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-BLOCKED-AC", result="blocked",
            ac_ids=["A10-AC01"], platforms=["api"])
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["verified"] = True
        ac["evidence"] = [ev_id]
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("没有覆盖该 AC 的 current passed")

    def test_fake_evidence_platforms_are_rejected(self):
        _, evidence, _ = self.upgrade_to_pass()
        ev = next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")
        ev["platforms"] = ["android", "windows"]
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("平台覆盖只能来自 real-flows.json")

    def test_pass_rejects_item_level_failed_flow(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-ITEM-FAILED", result="failed",
            ac_ids=["A10-AC01"], platforms=["api"])
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("PASS 不能与同一 item 的 current failed 流程共存")

    def test_pass_rejects_unsuperseded_stale_failed_flow(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-ITEM-STALE-FAILED", result="failed",
            ac_ids=["A10-AC01"], platforms=["api"])
        flow = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10-ITEM-STALE-FAILED")
        flow["freshness"] = "stale"
        ev = next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10-ITEM-STALE-FAILED")
        ev["freshness"] = "stale"
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("未声明 superseded_by 的 stale failed")

    def test_platform_coverage_requires_ac_link(self):
        ledger, evidence, flows = self.upgrade_to_pass()
        main_flow = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")
        main_flow["platforms"] = ["web"]
        self.add_current_flow(
            ledger, evidence, flows, "A10",
            suffix="A10-UNLINKED", result="passed",
            ac_ids=["A10-AC01"], platforms=["android", "windows"])
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("PASS 缺少由 AC 关联 passed flow 覆盖的 required 平台")

    def test_old_current_observed_at_is_rejected(self):
        _, evidence, flows = self.upgrade_to_pass()
        old = "2026-11-27T13:00:00+08:00"
        next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")["observed_at"] = old
        next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")["observed_at"] = old
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("必须晚于 source_snapshot.commit_date")

    def test_invalid_current_observed_at_is_rejected(self):
        _, evidence, flows = self.upgrade_to_pass()
        next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")["observed_at"] = "not-a-date"
        next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")["observed_at"] = "not-a-date"
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("不是 ISO-8601 时间")

    def test_current_source_head_mismatch_is_rejected(self):
        _, evidence, flows = self.upgrade_to_pass()
        next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")["source_commit"] = self.second_commit
        next(e for e in evidence["evidence"] if e["id"] == "EV-RF-TEST-A10")["source_commit"] = self.second_commit
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("必须等于 source_snapshot.commit")

    # -- arrays, PRs, CI, branch ---------------------------------------
    def test_item_evidence_must_be_array(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A10")["evidence"] = {"bad": True}
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("evidence 必须为数组")

    def test_item_evidence_rejects_duplicate_members(self):
        ledger = self.load(self.ledger_path)
        ev = self.item(ledger, "A10")["evidence"][0]
        self.item(ledger, "A10")["evidence"].append(ev)
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("evidence 含重复值")

    def test_flow_items_reject_duplicate_members(self):
        flows = self.load(self.flows_path)
        flows["flows"][0]["items"].append(flows["flows"][0]["items"][0])
        self.save(self.flows_path, flows)
        self.render()
        self.assert_fails_with("items 含重复值")

    def test_evidence_items_must_be_array(self):
        evidence = self.load(self.evidence_path)
        next(e for e in evidence["evidence"] if e["type"] == "real-flow")["items"] = {"bad": True}
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("items 必须为数组")

    def test_pass_rejects_open_pr(self):
        ledger, _, _ = self.upgrade_to_pass()
        prs = self.load(self.prs_path)
        self.attach_pr(ledger, prs, state="open")
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.render()
        self.assert_fails_with("必须 merged")

    def test_pass_rejects_draft_pr(self):
        ledger, _, _ = self.upgrade_to_pass()
        prs = self.load(self.prs_path)
        self.attach_pr(ledger, prs, state="draft", number=9998)
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.render()
        self.assert_fails_with("必须 merged")

    def test_pass_rejects_closed_unmerged_pr(self):
        ledger, _, _ = self.upgrade_to_pass()
        prs = self.load(self.prs_path)
        self.attach_pr(ledger, prs, state="closed", number=9997)
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.render()
        self.assert_fails_with("必须 merged")

    def test_pass_accepts_merged_resolvable_pr(self):
        ledger, _, _ = self.upgrade_to_pass()
        prs = self.load(self.prs_path)
        self.attach_pr(ledger, prs, state="merged", number=9996,
                       merge_commit=self.commit, verified_by="git-history")
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.render()
        self.assert_baseline_valid()

    def test_current_ci_requires_current_head_flag(self):
        ledger = self.load(self.ledger_path)
        prs = self.load(self.prs_path)
        ci = self.load(self.ci_path)
        pr_id = self.attach_pr(ledger, prs, state="open")
        ci["runs"].append({
            "id": "CI-900001",
            "run_id": "900001",
            "repo": "pim-api",
            "repository": "2746267826/pim-api",
            "workflow": "CI",
            "pr": pr_id,
            "head": self.second_commit,
            "conclusion": "success",
            "jobs": ["CI Gate"],
            "observed_at": FIXTURE_TIME,
            "freshness": "current",
            "head_is_current": False,
            "source": "test fixture",
        })
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.save(self.ci_path, ci)
        self.render()
        self.assert_fails_with("current CI 必须 head_is_current=true")

    def test_current_ci_head_must_match_pr_head(self):
        ledger = self.load(self.ledger_path)
        prs = self.load(self.prs_path)
        ci = self.load(self.ci_path)
        pr_id = self.attach_pr(ledger, prs, state="open")
        ci["runs"].append({
            "id": "CI-900002",
            "run_id": "900002",
            "repo": "pim-api",
            "repository": "2746267826/pim-api",
            "workflow": "CI",
            "pr": pr_id,
            "head": self.commit,
            "conclusion": "success",
            "jobs": ["CI Gate"],
            "observed_at": FIXTURE_TIME,
            "freshness": "current",
            "head_is_current": True,
            "source": "test fixture",
        })
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.save(self.ci_path, ci)
        self.render()
        self.assert_fails_with("head_is_current=true 但 head 与当前 PR head 不一致")

    def test_pass_rejects_stale_ci_evidence(self):
        ledger, evidence, _ = self.upgrade_to_pass()
        evidence["evidence"].append({
            "id": "EV-CI-STALE-A10",
            "type": "ci",
            "level": "E3",
            "freshness": "stale",
            "claim": "stale CI fixture",
            "source": "test fixture",
            "observed_at": "2026-11-27",
            "items": ["A10"],
            "ci_run": "CI-45507678632",
            "limits": "stale CI is not PASS evidence",
        })
        self.item(ledger, "A10")["evidence"].append("EV-CI-STALE-A10")
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.render()
        self.assert_fails_with("PASS 引用了非 current 证据")

    def test_pass_rejects_failed_current_ci_evidence(self):
        ledger, _, _ = self.upgrade_to_pass()
        prs = self.load(self.prs_path)
        evidence = self.load(self.evidence_path)
        ci = self.load(self.ci_path)
        pr_id = self.attach_pr(ledger, prs, state="merged", number=9995,
                               merge_commit=self.commit, verified_by="git-history")
        self.add_current_ci_evidence(ledger, evidence, ci, "A10", pr_id)
        next(run for run in ci["runs"] if run["run_id"] == "900000")["conclusion"] = "failure"
        self.save(self.ledger_path, ledger)
        self.save(self.prs_path, prs)
        self.save(self.evidence_path, evidence)
        self.save(self.ci_path, ci)
        self.render()
        self.assert_fails_with("PASS 的 current CI 证据 EV-CI-900000 不是成功 run")

    def test_missing_master_branch_fails_closed(self):
        self.git("branch", "-m", "master", "main")
        self.assert_fails_with("找不到声明分支引用 master")

    def test_missing_snapshot_branch_fails(self):
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"].pop("branch")
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("缺少非空字符串 branch")

    def test_unresolvable_snapshot_commit_fails(self):
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["commit"] = "deadbeef"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("无法解析为本地 git 对象")

    def test_snapshot_observed_at_must_follow_commit(self):
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["observed_at"] = "2026-11-28T07:00:00+08:00"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("observed_at 不能早于 commit_date")

    def test_snapshot_observed_at_must_be_iso(self):
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["observed_at"] = "not-a-date"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("不是 ISO-8601 时间")

    def test_snapshot_commit_date_must_match_git(self):
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["commit_date"] = "2026-11-28T09:00:00+08:00"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("commit_date 与 git 记录不一致")

    def test_snapshot_branch_must_be_allowlisted(self):
        self.git("branch", "develop")
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["branch"] = "develop"
        self.save(self.ledger_path, ledger)
        self.render()
        self.assert_fails_with("branch 必须属于明确允许集合")


class RendererCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="pim-ledger-render-"))
        shutil.copytree(REPO / "docs/product/delivery", self.tmp / "docs/product/delivery")
        (self.tmp / "docs/product").mkdir(parents=True, exist_ok=True)
        (self.tmp / "scripts/ci").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "scripts/ci/render-delivery-ledger.py",
                    self.tmp / "scripts/ci/render-delivery-ledger.py")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def render(self):
        output = self.tmp / "docs/product/pim-delivery-ledger.md"
        proc = subprocess.run(
            [sys.executable, str(self.tmp / "scripts/ci/render-delivery-ledger.py"),
             "--root", str(self.tmp), "--output", str(output)],
            capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return output.read_text(encoding="utf-8")

    def test_renderer_shows_blocked_ac_details(self):
        text = self.render()
        row = next(line for line in text.splitlines()
                   if line.startswith("| R05 | R05-AC05 |"))
        self.assertIn("永久身份墓碑容量策略", row)
        self.assertIn("产品/运营确认永久墓碑保留期限、容量上限与告警/归档策略。", row)
        self.assertIn("按裁决实现容量上限与告警，并补真实 PostgreSQL 验收。", row)
        self.assertIn("DEC-R05-AC05", row)

    def test_renderer_distinguishes_current_basis_from_legacy(self):
        ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        ledger = json.loads(ledger_path.read_text(encoding="utf-8"))
        current = next(i for i in ledger["items"] if i["id"] == "A10")
        current["implementation_basis"] = "current-real-flow-A10"
        ledger_path.write_text(
            json.dumps(ledger, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        text = self.render()
        current_row = next(line for line in text.splitlines() if line.startswith("| A10 |"))
        legacy_row = next(line for line in text.splitlines() if line.startswith("| A01 |"))
        self.assertIn("当前复核", current_row)
        self.assertNotIn("旧台账快照（非 current）", current_row)
        self.assertIn("旧台账快照（非 current）", legacy_row)

    def test_evidence_summary_categories_sum_to_total(self):
        text = self.render()
        match = re.search(
            r"证据：(\d+) 条（审计记录 (\d+)、CI (\d+)、真实流程 (\d+)、决议 (\d+)；分类合计 (\d+)）",
            text)
        self.assertIsNotNone(match)
        total, audit, ci, flows, decisions, claim_total = map(int, match.groups())
        self.assertEqual(total, claim_total)
        self.assertEqual(total, audit + ci + flows + decisions)

    def test_evidence_level_table_includes_freshness_and_sums(self):
        text = self.render()
        rows = [line for line in text.splitlines()
                if re.match(r"^\| E[0-6] \|", line)]
        self.assertEqual(len(rows), 7)
        totals = 0
        current = 0
        for row in rows:
            cells = [cell.strip() for cell in row.strip("|").split("|")]
            current += int(cells[2])
            totals += int(cells[6])
        self.assertEqual(totals, 211)
        self.assertEqual(current, 0)
        summary = re.search(r"证据：(\d+) 条", text)
        self.assertEqual(int(summary.group(1)), totals)

    def test_renderer_tolerates_renderer_required_fields_before_validation(self):
        ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        flows_path = self.tmp / "docs/product/delivery/real-flows.json"
        ci_path = self.tmp / "docs/product/delivery/ci-runs.json"
        evidence_path = self.tmp / "docs/product/delivery/evidence.json"
        ledger = json.loads(ledger_path.read_text(encoding="utf-8"))
        ledger["source_snapshot"].pop("note")
        ledger_path.write_text(
            json.dumps(ledger, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        flows = json.loads(flows_path.read_text(encoding="utf-8"))
        flows["flows"][0].pop("environment")
        flows["flows"][0].pop("result")
        flows_path.write_text(
            json.dumps(flows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        runs = json.loads(ci_path.read_text(encoding="utf-8"))
        runs["runs"][0].pop("head")
        ci_path.write_text(
            json.dumps(runs, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        evidence["evidence"][0].pop("type")
        evidence_path.write_text(
            json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        text = self.render()
        self.assertIn("—", text)


class RealLedgerTest(unittest.TestCase):
    def test_real_ledger_is_valid(self):
        err = io.StringIO()
        with redirect_stderr(err):
            ok = validator_module.Validator(REPO).run()
        self.assertTrue(ok, err.getvalue())

    def test_real_ledger_smoke_counts(self):
        with tempfile.TemporaryDirectory(prefix="pim-ledger-real-") as tmp:
            output = Path(tmp) / "ledger.md"
            proc = subprocess.run(
                [sys.executable, str(REPO / "scripts/ci/render-delivery-ledger.py"),
                 "--root", str(REPO), "--output", str(output)],
                capture_output=True, text=True)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            text = output.read_text(encoding="utf-8")
        self.assertIn("- 状态：PASS 0，", text)
        self.assertIn("stale 161", text)
        self.assertIn("真实流程：18 条记录，其中 current 通过 0 条", text)
        self.assertIn("AC：731 条，verified 0 条", text)


if __name__ == "__main__":
    unittest.main()
