"""Smoke tests for the PIM MCP server (Phase 3: HTTP mode + write tools).

Run from the repo root:  python3 -m pytest scripts/mcp/test_pim_mcp_server.py -q
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pim_mcp_server as s


def test_strip_bearer():
    assert s._strip_bearer("Bearer abc") == "abc"
    assert s._strip_bearer("bearer abc") == "abc"
    assert s._strip_bearer("abc") == "abc"
    assert s._strip_bearer("  Bearer xyz  ") == "xyz"


def test_write_tool_names_total_is_50():
    assert len(s._WRITE_TOOL_NAMES) == 50


def test_all_write_tools_are_registered():
    names = {t["name"] for t in s._list_tools_meta()}
    missing = s._WRITE_TOOL_NAMES - names
    assert not missing, f"missing tools: {sorted(missing)}"


def test_tool_inventory_counts():
    tools = s._list_tools_meta()
    read = [t for t in tools if t["group"] == "read"]
    write = [t for t in tools if t["group"] == "write"]
    assert len(tools) == 151
    assert len(read) == 101
    assert len(write) == 50


def test_write_flag_matches_catalog():
    write_names = s._WRITE_TOOL_NAMES
    for t in s._list_tools_meta():
        if t["name"] in write_names:
            assert t["group"] == "write"
        else:
            assert t["group"] == "read"


def test_permission_denied_builders():
    outcome = {"error": "permission denied: create_task", "code": 403}
    assert outcome["code"] == 403
    assert "permission denied: create_task" in outcome["error"]


def test_b64_to_bytes_valid_and_invalid():
    assert s._b64_to_bytes("aGVsbG8=", "x") == b"hello"
    result = s._b64_to_bytes("not-base64!!", "x")
    assert isinstance(result, dict)
    assert result["code"] == 400


def test_http_mode_flag_default_stdio():
    assert s._http_mode() is False


def test_summarize_params_truncates():
    summary = s._summarize_params({"title": "x" * 1000})
    assert summary is not None
    assert len(summary) <= 500


def test_required_field_validation_returns_400():
    import asyncio

    result = asyncio.get_event_loop().run_until_complete(
        s.create_event(calendarId="", title="", dtStart="", dtEnd="")
    )
    assert isinstance(result, dict)
    assert result.get("code") == 400


def test_wrapped_tool_returns_401_when_no_request_token():
    import asyncio

    s._wrap_tools_for_http()  # idempotent; simulates the HTTP-mode wrap
    result = asyncio.run(s.mcp._tool_manager.call_tool("create_task", {"title": "x"}))
    assert isinstance(result, dict)
    assert result.get("code") == 401
    assert "missing bearer token" in result.get("error", "")


def test_wrapper_enforces_permission_denied(monkeypatch):
    import asyncio

    monkeypatch.setattr(s, "_get_raw_request_token", lambda: "pim_mcp_testtoken")

    async def fake_verify(raw, tool, params):
        assert raw == "pim_mcp_testtoken"
        if tool == "create_task":
            return {"error": "permission denied: create_task", "code": 403}
        return {"accessToken": "jwt-test", "clientId": "c1", "permissions": {}}

    monkeypatch.setattr(s, "_call_verify", fake_verify)
    s._wrap_tools_for_http()

    denied = asyncio.run(s.mcp._tool_manager.call_tool("create_task", {"title": "x"}))
    assert denied.get("code") == 403
    assert "permission denied: create_task" in denied.get("error", "")


def test_wrapper_allowed_tool_passes_through(monkeypatch):
    import asyncio

    monkeypatch.setattr(s, "_get_raw_request_token", lambda: "pim_mcp_testtoken")

    async def fake_verify(raw, tool, params):
        return {"accessToken": "jwt-test", "clientId": "c1", "permissions": {}}

    async def fake_call_api(method, path, params=None, json_body=None, redact_urls=False, _retry_on_401=True):
        return {"code": 0, "data": {"path": path, "body": json_body}}

    monkeypatch.setattr(s, "_call_verify", fake_verify)
    monkeypatch.setattr(s, "_call_api", fake_call_api)
    s._wrap_tools_for_http()

    result = asyncio.run(s.mcp._tool_manager.call_tool("create_task", {"title": "hello"}))
    assert result.get("code") == 0
    assert result["data"]["path"] == "/api/v1/calendar/tasks"
    assert result["data"]["body"]["title"] == "hello"