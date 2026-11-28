#!/usr/bin/env python3
"""Focused tests for the delivery ledger validator and renderer."""

from __future__ import annotations

import copy
import io
import json
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


class LedgerCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="pim-ledger-"))
        shutil.copytree(REPO / "docs/product/delivery", self.tmp / "docs/product/delivery")
        (self.tmp / "docs/product").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "docs/product/pim-delivery-ledger.md",
                    self.tmp / "docs/product/pim-delivery-ledger.md")
        (self.tmp / "scripts/ci").mkdir(parents=True, exist_ok=True)
        shutil.copy(REPO / "scripts/ci/render-delivery-ledger.py",
                    self.tmp / "scripts/ci/render-delivery-ledger.py")
        self.ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        self.evidence_path = self.tmp / "docs/product/delivery/evidence.json"
        self.flows_path = self.tmp / "docs/product/delivery/real-flows.json"
        self.prs_path = self.tmp / "docs/product/delivery/prs.json"
        self.decisions_path = self.tmp / "docs/product/delivery/decisions.json"
        self.view_path = self.tmp / "docs/product/pim-delivery-ledger.md"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def load(self, path):
        return json.loads(path.read_text(encoding="utf-8"))

    def save(self, path, data):
        path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    def run_validator(self):
        err = io.StringIO()
        out = io.StringIO()
        with redirect_stderr(err), redirect_stdout(out):
            ok = validator_module.Validator(self.tmp, git_root=REPO).run()
        self.last_stderr = err.getvalue()
        return ok

    def render(self):
        proc = subprocess.run(
            [sys.executable, str(self.tmp / "scripts/ci/render-delivery-ledger.py"),
             "--root", str(self.tmp), "--output", str(self.view_path)],
            capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)

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

    def upgrade_to_pass(self, item_id="A10"):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, item_id)
        self.assertEqual(item["prs"], [], "fixture item must not depend on PR state")
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        ev_id = f"EV-RF-TEST-{item_id}"
        flow_id = f"RF-TEST-{item_id}"
        platforms = [
            platform for platform, role in item["platform_roles"].items()
            if role in {"required", "read_only"}
        ]
        audit_id = item["evidence"][0]
        audit = next(row for row in evidence["evidence"] if row["id"] == audit_id)
        audit["freshness"] = "current"
        audit["observed_at"] = "2026-11-28T13:00:00+08:00"
        evidence["evidence"].append({
            "id": ev_id,
            "type": "real-flow",
            "level": "E4",
            "freshness": "current",
            "claim": f"{item_id} current real-flow fixture",
            "source": "test fixture",
            "observed_at": "2026-11-28T13:00:00+08:00",
            "items": [item_id],
            "limits": "test fixture only",
        })
        flows["flows"].append({
            "id": flow_id,
            "title": f"{item_id} current passed flow",
            "items": [item_id],
            "platforms": platforms,
            "result": "passed",
            "freshness": "current",
            "environment": "test fixture",
            "observed_at": "2026-11-28T13:00:00+08:00",
            "artifacts": ["fixture output"],
            "limits": "test fixture only",
            "evidence": ev_id,
        })
        item["status"] = "PASS"
        item["freshness"] = "current"
        item["implementation_basis"] = f"current-real-flow-{item_id}"
        item["evidence"] = [audit_id, ev_id]
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

    # -- negative cases --------------------------------------------------
    def test_duplicate_id(self):
        ledger = self.load(self.ledger_path)
        ledger["items"].append(copy.deepcopy(ledger["items"][0]))
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("重复 ID")

    def test_missing_acceptance_criteria(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"] = []
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("acceptance_criteria 不能为空")

    def test_stale_pass_is_rejected(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["status"] = "PASS"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("stale PASS")

    def test_blocked_without_reason(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "Q01").pop("blocked_reason", None)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("BLOCKED 无原因")

    def test_approved_exception_without_decision(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "B15").pop("exception", None)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("APPROVED-EXCEPTION 无批准")

    def test_approved_exception_with_pending_decision(self):
        ledger = self.load(self.ledger_path)
        decisions = self.load(self.decisions_path)
        dec = next(d for d in decisions["decisions"] if d["id"] == "DEC-B15")
        dec["status"] = "pending"
        dec["approved_by"] = None
        self.save(self.ledger_path, ledger)
        self.save(self.decisions_path, decisions)
        self.assert_fails_with("不是 approved")

    def test_dangling_evidence(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["evidence"].append("EV-DOES-NOT-EXIST")
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("悬空 evidence")

    def test_dangling_pr(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["prs"].append("PR-pim-api-999999")
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("悬空 PR")

    def test_invalid_platform_role(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A02")["platform_roles"]["android"] = "complete"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("角色非法")

    def test_missing_acceptance_text(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"][0]["text"] = ""
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("缺少 text")

    def test_verified_ac_without_current_proof(self):
        ledger = self.load(self.ledger_path)
        ac = self.item(ledger, "A01")["acceptance_criteria"][0]
        ac["verified"] = True
        ac["evidence"] = ["EV-A01-AUDIT"]
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("没有 current E4/E5/E6")

    def test_extra_item_breaks_exact_scope(self):
        ledger = self.load(self.ledger_path)
        extra = copy.deepcopy(ledger["items"][0])
        extra["id"] = "A99"
        extra["acceptance_criteria"][0]["id"] = "A99-AC01"
        ledger["items"].append(extra)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("ID 集合与固定范围不符")

    def test_dependency_cycle_self(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["dependencies"] = ["ITEM:A01"]
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("依赖不能指向自身")

    def test_legacy_status_rewritten_while_stale(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["status"] = "NOT-VERIFIED"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("stale 项的 status 必须等于 legacy_status")

    def test_rendered_view_manual_edit(self):
        self.view_path.write_text("# hand written\n", encoding="utf-8")
        self.assert_fails_with("缺少生成标记")

    def test_unknown_evidence_level(self):
        evidence = self.load(self.evidence_path)
        target = next(e for e in evidence["evidence"] if e["type"] == "ci")
        target["level"] = "E9"
        self.save(self.evidence_path, evidence)
        self.assert_fails_with("非法 evidence level")

    def test_stale_ci_marked_current_head(self):
        runs_path = self.tmp / "docs/product/delivery/ci-runs.json"
        runs = self.load(runs_path)
        runs["runs"][0]["head_is_current"] = True
        self.save(runs_path, runs)
        self.assert_fails_with("stale run 不能标记为当前 head")

    def test_blocked_acceptance_without_reason(self):
        ledger = self.load(self.ledger_path)
        for ac in self.item(ledger, "R05")["acceptance_criteria"]:
            if ac["id"] == "R05-AC05":
                ac["blocked"]["reason"] = ""
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("BLOCKED 无原因")

    def test_blocked_acceptance_without_unblock(self):
        ledger = self.load(self.ledger_path)
        for ac in self.item(ledger, "R05")["acceptance_criteria"]:
            if ac["id"] == "R05-AC05":
                ac["blocked"]["unblock"] = []
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("缺少 unblock 条件")

    def test_pass_requires_every_acceptance_criterion(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "A10")
        item["status"] = "PASS"
        item["freshness"] = "current"
        item["implementation_basis"] = "current-real-flow-A10"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("PASS 项存在未验证/待定 AC")

    def test_pass_rejects_blocked_acceptance(self):
        self.upgrade_to_pass()
        ledger = self.load(self.ledger_path)
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["verified"] = False
        ac["evidence"] = []
        ac["blocked"] = {
            "reason": "fixture blocker",
            "unblock": ["fixture unblock"],
            "decision_id": "DEC-R05-AC05",
        }
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("PASS 项存在 AC 级 BLOCKED")

    def test_pass_requires_all_required_platforms(self):
        self.upgrade_to_pass()
        flows = self.load(self.flows_path)
        target = next(f for f in flows["flows"] if f["id"] == "RF-TEST-A10")
        target["platforms"] = ["web", "android"]
        self.save(self.flows_path, flows)
        self.assert_fails_with("PASS 缺少 required 平台的 current 真实流程证据")

    def test_verified_ac_rejects_stale_ref(self):
        self.upgrade_to_pass()
        ledger = self.load(self.ledger_path)
        ac = self.item(ledger, "A10")["acceptance_criteria"][0]
        ac["evidence"].append("EV-A10-AUDIT")
        evidence = self.load(self.evidence_path)
        audit = next(e for e in evidence["evidence"] if e["id"] == "EV-A10-AUDIT")
        audit["freshness"] = "stale"
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.assert_fails_with("verified 证据不合格或已过期")

    def test_flow_and_evidence_items_must_match(self):
        flows = self.load(self.flows_path)
        flows["flows"][0]["items"].append("R05")
        self.save(self.flows_path, flows)
        self.assert_fails_with("flow.items 与 evidence.items 不一致")

    def test_decision_requires_item_reverse_reference(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "R05")["decisions"] = []
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("item R05 未反向引用决定 DEC-R05-AC05")

    def test_status_counts_are_recomputed(self):
        ledger = self.load(self.ledger_path)
        ledger["scope"]["counts_by_status"]["FAIL"] -= 1
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("counts_by_status 与 items 实际计数不符")

    def test_prefix_counts_are_recomputed(self):
        ledger = self.load(self.ledger_path)
        ledger["scope"]["counts_by_prefix"]["A"] -= 1
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("counts_by_prefix 与 items 实际计数不符")

    def test_false_closed_batch_is_rejected(self):
        ledger = self.load(self.ledger_path)
        next(b for b in ledger["batches"] if b["id"] == "B1")["status"] = "closed"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("status 与 items 重算结果不符")

    def test_snapshot_commit_must_belong_to_master(self):
        head = subprocess.run(
            ["git", "-C", str(REPO), "rev-parse", "HEAD"],
            capture_output=True, text=True, check=True).stdout.strip()
        commit_date = subprocess.run(
            ["git", "-C", str(REPO), "show", "-s", "--format=%cI", head],
            capture_output=True, text=True, check=True).stdout.strip()
        ledger = self.load(self.ledger_path)
        ledger["source_snapshot"]["commit"] = head
        ledger["source_snapshot"]["commit_date"] = commit_date
        ledger["source_snapshot"]["observed_at"] = "2026-11-28T13:30:00+08:00"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("不属于声明分支 master")

    def test_current_item_cannot_use_legacy_basis(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A10")["freshness"] = "current"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("current 项不能继续使用 legacy implementation_basis")

    def test_pr_requires_observation_time(self):
        prs = self.load(self.prs_path)
        prs["prs"][0]["observed_at"] = None
        self.save(self.prs_path, prs)
        self.assert_fails_with("缺少 observed_at")

    def test_not_verified_upgrades_with_current_real_flow(self):
        self.upgrade_to_pass()
        ok = self.run_validator()
        self.assertTrue(ok, self.last_stderr)
        ledger = self.load(self.ledger_path)
        self.assertEqual(next(i for i in ledger["items"] if i["id"] == "A10")["status"], "PASS")

    def test_current_failed_flow_can_keep_item_current(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "A10")
        item["status"] = "FAIL"
        item["freshness"] = "current"
        item["implementation_basis"] = "current-real-flow-A10-failed"
        evidence = self.load(self.evidence_path)
        flows = self.load(self.flows_path)
        ev_id = "EV-RF-TEST-A10-FAILED"
        evidence["evidence"].append({
            "id": ev_id,
            "type": "real-flow",
            "level": "E4",
            "freshness": "current",
            "claim": "A10 current failed-flow fixture",
            "source": "test fixture",
            "observed_at": "2026-11-28T13:00:00+08:00",
            "items": ["A10"],
            "limits": "test fixture only",
        })
        flows["flows"].append({
            "id": "RF-TEST-A10-FAILED",
            "title": "A10 current failed flow",
            "items": ["A10"],
            "platforms": ["api"],
            "result": "failed",
            "freshness": "current",
            "environment": "test fixture",
            "observed_at": "2026-11-28T13:00:00+08:00",
            "artifacts": [],
            "limits": "test fixture only",
            "evidence": ev_id,
        })
        item["evidence"].append(ev_id)
        self.recount_status(ledger)
        self.save(self.ledger_path, ledger)
        self.save(self.evidence_path, evidence)
        self.save(self.flows_path, flows)
        self.render()
        ok = self.run_validator()
        self.assertTrue(ok, self.last_stderr)


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

    def test_renderer_shows_blocked_ac_and_legacy_basis(self):
        output = self.tmp / "docs/product/pim-delivery-ledger.md"
        proc = subprocess.run(
            [sys.executable, str(self.tmp / "scripts/ci/render-delivery-ledger.py"),
             "--root", str(self.tmp), "--output", str(output)],
            capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        text = output.read_text(encoding="utf-8")
        self.assertIn("AC 级阻塞", text)
        self.assertIn("R05-AC05", text)
        self.assertIn("永久身份墓碑容量策略", text)
        self.assertIn("旧台账快照（非 current）", text)
        self.assertIn("NOT-VERIFIED", text)

    def test_renderer_distinguishes_current_basis_from_not_verified(self):
        ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        ledger = json.loads(ledger_path.read_text(encoding="utf-8"))
        item = next(i for i in ledger["items"] if i["id"] == "A10")
        item["implementation_basis"] = "current-real-flow-A10"
        ledger_path.write_text(
            json.dumps(ledger, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        output = self.tmp / "docs/product/pim-delivery-ledger.md"
        proc = subprocess.run(
            [sys.executable, str(self.tmp / "scripts/ci/render-delivery-ledger.py"),
             "--root", str(self.tmp), "--output", str(output)],
            capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)
        row = next(line for line in output.read_text(encoding="utf-8").splitlines()
                   if line.startswith("| A10 |"))
        self.assertIn("NOT-VERIFIED", row)
        self.assertIn("当前复核", row)


class RealLedgerTest(unittest.TestCase):
    def test_real_ledger_is_valid(self):
        err = io.StringIO()
        with redirect_stderr(err):
            ok = validator_module.Validator(REPO).run()
        self.assertTrue(ok, err.getvalue())


if __name__ == "__main__":
    unittest.main()
