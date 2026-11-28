#!/usr/bin/env python3
"""Negative tests for validate-delivery-ledger.py.

Each case copies the real structured source into a temp directory, breaks one
rule, and asserts the validator fails with a message that names the problem.
"""

from __future__ import annotations

import copy
import io
import json
import shutil
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
        self.ledger_path = self.tmp / "docs/product/delivery/ledger.json"
        self.evidence_path = self.tmp / "docs/product/delivery/evidence.json"
        self.prs_path = self.tmp / "docs/product/delivery/prs.json"

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
            ok = validator_module.Validator(self.tmp).run()
        self.last_stderr = err.getvalue()
        return ok

    def assert_fails_with(self, needle):
        ok = self.run_validator()
        self.assertFalse(ok, "validator should fail")
        self.assertIn(needle, self.last_stderr)

    def item(self, ledger, item_id):
        return next(i for i in ledger["items"] if i["id"] == item_id)

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
        item = self.item(ledger, "A01")
        item["status"] = "PASS"
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("stale PASS")

    def test_blocked_without_reason(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "Q01")
        item.pop("blocked_reason", None)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("BLOCKED 无原因")

    def test_approved_exception_without_decision(self):
        ledger = self.load(self.ledger_path)
        item = self.item(ledger, "B15")
        item.pop("exception", None)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("APPROVED-EXCEPTION 无批准")

    def test_approved_exception_with_pending_decision(self):
        ledger = self.load(self.ledger_path)
        path = self.tmp / "docs/product/delivery/decisions.json"
        decisions = self.load(path)
        dec = next(d for d in decisions["decisions"] if d["id"] == "DEC-B15")
        dec["status"] = "pending"
        dec["approved_by"] = None
        self.save(path, decisions)
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

    def test_missing_acceptance_reference(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"][0]["text"] = ""
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("缺少 text")

    def test_verified_ac_without_current_evidence(self):
        ledger = self.load(self.ledger_path)
        self.item(ledger, "A01")["acceptance_criteria"][0]["verified"] = True
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("没有 current 证据")

    def test_extra_item_breaks_exact_scope(self):
        ledger = self.load(self.ledger_path)
        extra = copy.deepcopy(ledger["items"][0])
        extra["id"] = "A99"
        extra["acceptance_criteria"][0]["id"] = "A99-AC01"
        ledger["items"].append(extra)
        self.save(self.ledger_path, ledger)
        self.assert_fails_with("必须恰好 161 项")

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
        path = self.tmp / "docs/product/pim-delivery-ledger.md"
        path.write_text("# hand written\n", encoding="utf-8")
        self.assert_fails_with("缺少生成标记")

    def test_unknown_evidence_level(self):
        evidence = self.load(self.evidence_path)
        target = next(e for e in evidence["evidence"] if e["type"] == "ci")
        target["level"] = "E9"
        self.save(self.evidence_path, evidence)
        self.assert_fails_with("非法 evidence level")

    def test_stale_ci_marked_current_head(self):
        path = self.tmp / "docs/product/delivery/ci-runs.json"
        runs = self.load(path)
        runs["runs"][0]["head_is_current"] = True
        self.save(path, runs)
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


class RealLedgerTest(unittest.TestCase):
    def test_real_ledger_is_valid(self):
        err = io.StringIO()
        with redirect_stderr(err):
            ok = validator_module.Validator(REPO).run()
        self.assertTrue(ok, err.getvalue())


if __name__ == "__main__":
    unittest.main()
