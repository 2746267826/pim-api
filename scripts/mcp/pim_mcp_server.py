"""
PIM MCP v2 - Read-only server for AI Agent
Exposes 101 read-only tools covering Calendar / PcTracker / Mobile / QuickNotes / Files / Core/Infra.
Writes 0 - no create/update/delete/sync/import/batch-execute.

Auth: Bearer token pass-through (MCP context -> Pim.Api)
- Client obtains JWT via POST /api/v1/auth/login and calls MCP with
  Authorization: Bearer <token> (HTTP) or PIM_ACCESS_TOKEN env (stdio).
- MCP does not cache token, does not refresh, audits real userId on API side.

Conventions:
- time: start/end ISO8601 UTC, timezone IANA default Asia/Shanghai, max span 366 days
- pagination: page>=1, pageSize 1..100 default 20
- redactUrls: True hashes any field containing 'url' to 12-char sha256 hex (urlHash), False returns raw
- response >50KB adds truncated/nextPage hint
"""

import hashlib
import json
import os
import re
import asyncio
from datetime import datetime, timezone, timedelta
from typing import Any, Dict, List, Optional
from zoneinfo import ZoneInfo

import httpx

try:
    from mcp.server.fastmcp import FastMCP
except ImportError:
    try:
        from mcp.server.mcpserver import MCPServer as FastMCP  # type: ignore  # mcp 2.x
    except ImportError:
        from mcp.server.fastmcp import FastMCP  # type: ignore

PIM_API_URL = os.getenv("PIM_API_URL", "http://127.0.0.1:5858").rstrip("/")
DEFAULT_TIMEZONE = "Asia/Shanghai"

mcp = FastMCP("pim-mcp-server")


# ---------- helpers: auth, time, pagination, redaction, api ----------

def _get_token() -> Optional[str]:
    # Try FastMCP context (HTTP transport headers)
    try:
        from mcp.server.fastmcp import get_context  # type: ignore

        ctx = get_context()  # type: ignore
        if ctx is not None:
            # Attempt to read request headers from various possible locations
            req = None
            if hasattr(ctx, "request_context"):
                rc = getattr(ctx, "request_context")
                if rc is not None and hasattr(rc, "request"):
                    req = getattr(rc, "request")
                elif rc is not None and hasattr(rc, "headers"):
                    # some versions expose headers directly
                    hdrs = getattr(rc, "headers", None)
                    if hdrs:
                        for k, v in (hdrs.items() if hasattr(hdrs, "items") else []):
                            if k.lower() == "authorization" and isinstance(v, str) and v.lower().startswith("bearer "):
                                return v[7:].strip()
            if req is not None and hasattr(req, "headers"):
                hdrs = getattr(req, "headers")
                try:
                    # headers may be dict-like or Starlette Headers
                    if hasattr(hdrs, "get"):
                        auth = hdrs.get("authorization") or hdrs.get("Authorization")  # type: ignore
                        if auth and isinstance(auth, str) and auth.lower().startswith("bearer "):
                            return auth[7:].strip()
                    # fallback iteration
                    if not auth:
                        for k, v in (hdrs.items() if hasattr(hdrs, "items") else []):  # type: ignore
                            if k.lower() == "authorization" and isinstance(v, str) and v.lower().startswith("bearer "):
                                return v[7:].strip()
                except Exception:
                    pass
            # also try meta
            if hasattr(ctx, "meta") and isinstance(getattr(ctx, "meta"), dict):
                meta = getattr(ctx, "meta")
                auth = meta.get("authorization") or meta.get("Authorization")
                if auth and isinstance(auth, str) and auth.lower().startswith("bearer "):
                    return auth[7:].strip()
    except Exception:
        pass

    # Env fallback for stdio transport
    for env_name in ("PIM_ACCESS_TOKEN", "PIM_TOKEN", "MCP_BEARER_TOKEN", "BEARER_TOKEN", "PIM_JWT"):
        v = os.getenv(env_name)
        if v and v.strip():
            vv = v.strip()
            if vv.lower().startswith("bearer "):
                return vv[7:].strip()
            return vv
    return None


def _parse_iso8601(s: str) -> datetime:
    # Accept YYYY-MM-DD and ISO8601 with Z
    if not s or not isinstance(s, str):
        raise ValueError(f"invalid iso8601: {s}")
    # date-only YYYY-MM-DD -> midnight UTC
    if re.fullmatch(r"\d{4}-\d{2}-\d{2}", s):
        return datetime.fromisoformat(s + "T00:00:00+00:00")
    # Replace Z with +00:00
    iso = s.replace("Z", "+00:00")
    try:
        dt = datetime.fromisoformat(iso)
    except Exception as e:
        raise ValueError(f"invalid iso8601 '{s}': {e}") from e
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def _validate_time_range(start: Optional[str], end: Optional[str]) -> Optional[Dict[str, Any]]:
    if start is None or end is None:
        return None
    try:
        s = _parse_iso8601(start)
        e = _parse_iso8601(end)
    except ValueError as ve:
        return {"error": f"invalid time format: {ve}", "code": 400}
    if s > e:
        return {"error": "invalid time range: start must be <= end", "code": 400}
    if (e - s).days > 366:
        return {"error": "time range too large: max span 366 days", "code": 400}
    return None


def _validate_pagination(page: Optional[int], pageSize: Optional[int]) -> Optional[Dict[str, Any]]:
    if page is not None and page < 1:
        return {"error": "page must be >=1", "code": 400}
    if pageSize is not None and not (1 <= pageSize <= 100):
        return {"error": "pageSize must be between 1 and 100", "code": 400}
    return None


def _redact_value(url: str) -> str:
    return hashlib.sha256(url.encode("utf-8")).hexdigest()[:12]


def _apply_redact(obj: Any, redact: bool) -> Any:
    if not redact:
        return obj
    if isinstance(obj, dict):
        new_d: Dict[str, Any] = {}
        for k, v in obj.items():
            lk = k.lower()
            # redact any url-like or link-like field (url, openLink, downloadUrl, href) to avoid leaking
            is_url_like = "url" in lk or "link" in lk or lk == "href" or lk.endswith("href")
            if isinstance(v, str) and is_url_like:
                if lk == "url":
                    new_key = "urlHash"
                elif lk == "href":
                    new_key = "hrefHash"
                elif "url" in lk or "link" in lk or "href" in lk:
                    new_key = k + "Hash" if not k.endswith("Hash") else k
                else:
                    new_key = k + "Hash"
                new_d[new_key] = _redact_value(v) if v else v
            elif isinstance(v, (dict, list)):
                new_d[k] = _apply_redact(v, True)
            else:
                new_d[k] = v
        return new_d
    if isinstance(obj, list):
        return [_apply_redact(x, True) for x in obj]
    return obj


def _check_truncation(data: Any, params: Optional[Dict[str, Any]] = None) -> Any:
    try:
        serialized = json.dumps(data, ensure_ascii=False)
    except Exception:
        return data
    if len(serialized) > 50 * 1024:
        if isinstance(data, dict):
            # only for success responses; ignore errors
            if data.get("code", 0) != 0 and "error" in data:
                return data
            # also handle bare list case wrapped as dict via ApiResponse
            data = dict(data)  # shallow copy
            data["truncated"] = True
            # try to infer nextPage
            if "page" in data and isinstance(data.get("page"), int):
                data["nextPage"] = data["page"] + 1
            elif "data" in data and isinstance(data["data"], dict) and "page" in data["data"]:
                try:
                    data["nextPage"] = data["data"]["page"] + 1
                except Exception:
                    data["nextPage"] = 2
            elif isinstance(data.get("data"), list):
                try:
                    p = params.get("page") if params else None
                    data["nextPage"] = (int(p) + 1) if p else 2
                    data["_note"] = "response >50KB, list truncated suggestion nextPage"
                    return data
                except Exception:
                    data["nextPage"] = 2
            else:
                try:
                    p = params.get("page") if params else None
                    data["nextPage"] = (int(p) + 1) if p else 2
                except Exception:
                    data["nextPage"] = 2
            data["_note"] = "response >50KB, consider pagination with nextPage"
    return data


async def _call_api(
    method: str,
    path: str,
    params: Optional[Dict[str, Any]] = None,
    json_body: Optional[Any] = None,
    redact_urls: bool = False,
) -> Any:
    token = _get_token()
    if not token:
        return {
            "error": "missing bearer token: call MCP with Authorization: Bearer <PIM JWT>. Obtain token via POST /api/v1/auth/login {\"username\",\"password\"} -> accessToken",
            "code": 401,
        }
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    # Normalize path: ensure starts with /api/v1
    if not path.startswith("/"):
        path = "/" + path
    if not path.startswith("/api/"):
        # assume relative to /api/v1
        if path.startswith("/v1/"):
            path = "/api" + path
        else:
            path = "/api/v1" + path
    url = f"{PIM_API_URL}{path}"
    # Remove None params
    clean_params: Optional[Dict[str, Any]] = None
    if params:
        clean_params = {k: v for k, v in params.items() if v is not None}
        # Convert bool to lowercase string for query?
        # httpx handles, but ensure
        if not clean_params:
            clean_params = None
    try:
        async with httpx.AsyncClient(timeout=30) as client:
            if method.upper() == "GET":
                resp = await client.get(url, params=clean_params, headers=headers)
            elif method.upper() == "POST":
                resp = await client.post(url, params=clean_params, json=json_body, headers=headers)
            elif method.upper() == "PUT":
                resp = await client.put(url, params=clean_params, json=json_body, headers=headers)
            elif method.upper() == "DELETE":
                resp = await client.delete(url, params=clean_params, headers=headers)
            else:
                return {"error": f"unsupported method {method}", "code": 500}
            # Try json
            try:
                data = resp.json()
            except Exception:
                # maybe binary or text (ics)
                content_type = resp.headers.get("content-type", "")
                if "text/calendar" in content_type or "text/plain" in content_type or resp.headers.get("content-type", "").startswith("text/"):
                    return {"data": resp.text, "code": 0, "contentType": content_type}
                data = {"raw": resp.text[:2000], "status": resp.status_code}
            if resp.status_code >= 400:
                # pass through ApiResponse error
                if isinstance(data, dict) and "error" not in data:
                    return {"error": f"HTTP {resp.status_code}: {resp.text[:800]}", "details": data, "code": resp.status_code}
                if isinstance(data, dict):
                    # ensure code field
                    if "code" not in data:
                        data["code"] = resp.status_code
                    return data
                return {"error": f"HTTP {resp.status_code}: {resp.text[:800]}", "details": data, "code": resp.status_code}
            # success: apply redact if requested
            if redact_urls:
                # data may be ApiResponse wrapper {code, data, ...}
                # redact inner data
                if isinstance(data, dict) and "data" in data:
                    data = dict(data)
                    data["data"] = _apply_redact(data["data"], True)
                else:
                    data = _apply_redact(data, True)
            # truncation check
            data = _check_truncation(data, clean_params)
            return data
    except httpx.TimeoutException as e:
        return {"error": f"request timeout: {e}", "code": 504}
    except Exception as e:
        return {"error": f"request failed: {e}", "details": str(e), "code": 500}


def _clean_params(**kwargs: Any) -> Dict[str, Any]:
    return {k: v for k, v in kwargs.items() if v is not None}


# ===================== Calendar 31 =====================

@mcp.tool()
async def get_calendar_layers(
    start: str,
    end: str,
    layers: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
    redactUrls: bool = True,
) -> Any:
    """Get calendar layers (events+tasks+habits) overlay. Default layers=all when omitted. Covers full time range with timezone-aware day slicing. Useful for 'today what's up' view. Returns CalendarLayerResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    # layers=None defaults to all per C5
    params = _clean_params(start=start, end=end, layers=layers if layers else "all", timezone=timezone)
    # Note: outlookOnly removed per C4
    res = await _call_api("GET", "/api/v1/calendar/layers", params=params, redact_urls=redactUrls)
    return res


@mcp.tool()
async def query_data_center(
    search: Optional[str] = None,
    objectType: Optional[str] = None,
    source: Optional[str] = None,
    pendingOnly: bool = False,
    page: int = 1,
    pageSize: int = 20,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Universal query for calendar data center (read semantic POST). Supports search/objectType/source filters with pagination. Returns DataCenterQueryResponse."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    body = {
        "search": search,
        "objectType": objectType,
        "source": source,
        "pendingOnly": pendingOnly,
        "page": page,
        "pageSize": pageSize,
    }
    # remove Nones but keep pendingOnly
    body = {k: v for k, v in body.items() if v is not None}
    return await _call_api("POST", "/api/v1/calendar/data-center/query", json_body=body)


@mcp.tool()
async def preview_data_center_batch(
    action: str,
    objects: List[Dict[str, Any]],
    reason: Optional[str] = None,
) -> Any:
    """Preview data-center batch operation (read-only, does not execute). Returns risk level, affected count, summary."""
    body = {"action": action, "objects": objects}
    if reason:
        body["reason"] = reason
    return await _call_api("POST", "/api/v1/calendar/data-center/batch/preview", json_body=body)


@mcp.tool()
async def get_data_center_audit_export(
    start: Optional[str] = None,
    end: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Export audit log for data center in time range. Returns AuditExportResponse."""
    if start and end:
        err = _validate_time_range(start, end)
        if err:
            return err
    params = _clean_params(start=start, end=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/calendar/data-center/audit/export", params=params)


@mcp.tool()
async def preview_data_center_restore(
    auditVersionId: str,
    reason: Optional[str] = None,
) -> Any:
    """Preview restore from audit version (read-only, does not restore). Returns RestorePreview."""
    body = {"auditVersionId": auditVersionId}
    if reason:
        body["reason"] = reason
    return await _call_api("POST", "/api/v1/calendar/data-center/restore/preview", json_body=body)


@mcp.tool()
async def get_projects(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List domain projects. Returns DomainProject[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    # API currently returns list without pagination, but we pass through and handle locally
    res = await _call_api("GET", "/api/v1/calendar/projects")
    # Apply pagination locally if ApiResponse contains list
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
        res = _check_truncation(res, {"page": page})
    return res


@mcp.tool()
async def get_task_books(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List task books. Returns TaskBook[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    res = await _call_api("GET", "/api/v1/calendar/task-books")
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
        res = _check_truncation(res, {"page": page})
    return res


@mcp.tool()
async def get_habits(
    page: int = 1,
    pageSize: int = 20,
    start: Optional[str] = None,
    end: Optional[str] = None,
) -> Any:
    """List habit routines. Optional time filter. Returns HabitRoutine[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    res = await _call_api("GET", "/api/v1/calendar/habits")
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
        res = _check_truncation(res, {"page": page})
    return res


@mcp.tool()
async def get_availability_windows(
    start: Optional[str] = None,
    end: Optional[str] = None,
) -> Any:
    """List availability windows. Returns AvailabilityWindow[]. If start/end omitted, returns all."""
    if start and end:
        err = _validate_time_range(start, end)
        if err:
            return err
    # API has no start/end params, filter locally if needed
    res = await _call_api("GET", "/api/v1/calendar/availability")
    return res


@mcp.tool()
async def get_reminders(
    start: Optional[str] = None,
    end: Optional[str] = None,
) -> Any:
    """List reminders. Optional time range filter (applied locally if API ignores). Returns Reminder[]."""
    if start and end:
        err = _validate_time_range(start, end)
        if err:
            return err
    res = await _call_api("GET", "/api/v1/calendar/reminders")
    return res


@mcp.tool()
async def get_reminder_delivery_log(
    start: Optional[str] = None,
    end: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Get reminder delivery log. Returns ReminderDelivery[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    res = await _call_api("GET", "/api/v1/calendar/reminders/delivery-log")
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
        res = _check_truncation(res, {"page": page})
    return res


@mcp.tool()
async def get_reports(
    start: Optional[str] = None,
    end: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List report artifacts. Optional time range. Returns ReportArtifact[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    res = await _call_api("GET", "/api/v1/calendar/reports")
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
        res = _check_truncation(res, {"page": page})
    return res


@mcp.tool()
async def get_report(report_id: str) -> Any:
    """Get single report artifact by id. Returns ReportArtifact."""
    return await _call_api("GET", f"/api/v1/calendar/reports/{report_id}")


@mcp.tool()
async def get_calendars() -> Any:
    """List all calendars. Returns Calendar[]."""
    return await _call_api("GET", "/api/v1/calendar/calendars")


@mcp.tool()
async def get_events(
    start: Optional[str] = None,
    end: Optional[str] = None,
    calendarId: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
    redactUrls: bool = True,
) -> Any:
    """List calendar events with optional time range and calendar filter. Core query. Returns Event[] (PagedResult when page params sent)."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    params = _clean_params(start=start, end=end, calendarId=calendarId, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/calendar/events", params=params, redact_urls=redactUrls)


@mcp.tool()
async def get_tasks(
    status: Optional[str] = None,
    calendarId: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List tasks filtered by status and calendar. Returns Task[] (PagedResult when filters present)."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(status=status, calendarId=calendarId, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/calendar/tasks", params=params)


@mcp.tool()
async def get_task_segments(task_id: str) -> Any:
    """List execution segments for a task. Returns TaskExecutionSegment[]."""
    return await _call_api("GET", f"/api/v1/calendar/tasks/{task_id}/segments")


@mcp.tool()
async def get_recycle_bin(
    start: Optional[str] = None,
    end: Optional[str] = None,
    type: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List recycle-bin items. Optional type filter (calendar/event/task). Returns RecycleBinItem[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    params = _clean_params(type=type, search=None, deletedFrom=start, deletedTo=end, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/calendar/recycle-bin", params=params)


@mcp.tool()
async def preview_recycle_bin_restore(type: str, id: str) -> Any:
    """Preview recycle-bin restore (read-only, does not restore). Returns RestorePreview."""
    return await _call_api("POST", f"/api/v1/calendar/recycle-bin/{type}/{id}/restore-preview", json_body={})


@mcp.tool()
async def get_export_ics(
    start: str,
    end: str,
    calendarId: Optional[str] = None,
    ids: Optional[str] = None,
) -> Any:
    """Export ICS text for events in range. Returns ics text. calendarId filters by calendar (best-effort), ids is comma-separated event ids."""
    err = _validate_time_range(start, end)
    if err:
        return err
    # API expects ?start&end&ids (event ids), not calendarId directly.
    # If calendarId provided without ids, we try calendar-aware export:
    # First attempt direct export with ids if provided, else try with calendarId as ids fallback,
    # and if API ignores calendarId we still return full-range ICS (document limitation).
    effective_ids = ids or calendarId
    params = _clean_params(start=start, end=end, ids=effective_ids)
    # also pass calendarId for future API compat (ignored if unsupported)
    if calendarId and not ids:
        # keep calendarId as is for forward-compat, but also map to ids for current API
        params["calendarId"] = calendarId
    res = await _call_api("GET", "/api/v1/calendar/export-ics", params=params)
    return res


@mcp.tool()
async def get_outlook_settings() -> Any:
    """Get Outlook sync settings (read-only). Returns OutlookSettingsResponse."""
    return await _call_api("GET", "/api/v1/calendar/outlook/settings")


@mcp.tool()
async def get_outlook_sync_batches(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List Outlook sync batches. Returns SyncBatch[] with pagination."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/calendar/outlook/sync/batches", params=params)


@mcp.tool()
async def get_outlook_local_data_preview(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Preview local Outlook data counts (read-only). Returns Preview with binding/calendar/event counts."""
    # API preview currently takes no pagination, but we keep params for future compat
    return await _call_api("GET", "/api/v1/calendar/outlook/local-data/preview")


@mcp.tool()
async def get_event_by_id(event_id: str) -> Any:
    """Get single event by id. Tries direct fetch, falls back to filtered list. Returns Event. Note: fallback scans 730-day window page 1 only; if not found try broader search via search_pim."""
    # Try direct endpoint for forward-compat
    res = await _call_api("GET", f"/api/v1/calendar/events/{event_id}")
    if isinstance(res, dict) and res.get("code") in (404, 405):
        # fallback 1: global search
        try:
            search_res = await _call_api("GET", "/api/v1/search", params={"q": event_id, "type": "event", "limit": 20})
            if isinstance(search_res, dict) and "data" in search_res:
                data = search_res["data"]
                items = data.get("items") if isinstance(data, dict) and "items" in data else data.get("data", {}).get("items") if isinstance(data, dict) else []
                # search Res shape: PagedResult<SearchResult>
                maybe = data.get("items") if isinstance(data, dict) and "items" in data else []
                if isinstance(maybe, list):
                    for it in maybe:
                        if isinstance(it, dict) and str(it.get("id")) == event_id or event_id in str(it.get("title", "")):
                            # search result may contain object id in different field
                            pass
                # fallback 1b: data-center query by exact object
                dc = await _call_api("POST", "/api/v1/calendar/data-center/query", json_body={"search": event_id, "objectType": "event", "page": 1, "pageSize": 20})
                if isinstance(dc, dict) and "data" in dc:
                    dc_data = dc["data"]
                    dc_items = dc_data.get("items") if isinstance(dc_data, dict) else []
                    for it in dc_items if isinstance(dc_items, list) else []:
                        if isinstance(it, dict) and str(it.get("objectId")) == event_id:
                            return {"code": 0, "data": it, "note": "via data-center fallback"}
        except Exception:
            pass
        # fallback 2: broad range scan (limit 100, may miss beyond)
        now = datetime.now(timezone.utc)
        start = (now - timedelta(days=365)).isoformat().replace("+00:00", "Z")
        end = (now + timedelta(days=365)).isoformat().replace("+00:00", "Z")
        list_res = await _call_api("GET", "/api/v1/calendar/events", params={"start": start, "end": end, "page": 1, "pageSize": 100})
        if isinstance(list_res, dict) and "data" in list_res:
            data = list_res["data"]
            items = data.get("items") if isinstance(data, dict) and "items" in data else data if isinstance(data, list) else []
            for it in items if isinstance(items, list) else []:
                if isinstance(it, dict) and str(it.get("id")) == event_id:
                    return {"code": 0, "data": it}
            if isinstance(data, dict) and "items" in data:
                for it in data["items"]:
                    if str(it.get("id")) == event_id:
                        return {"code": 0, "data": it}
        return {"error": f"event {event_id} not found (fallback scanned 730-day window page 1 only, use broader get_events search if needed)", "code": 404}
    return res


@mcp.tool()
async def get_task_by_id(task_id: str) -> Any:
    """Get single task by id. Falls back to filtered list if direct endpoint missing. Returns Task. Fallback limited to page 1."""
    res = await _call_api("GET", f"/api/v1/calendar/tasks/{task_id}")
    if isinstance(res, dict) and res.get("code") in (404, 405):
        # fallback via search and data-center
        try:
            search_res = await _call_api("GET", "/api/v1/search", params={"q": task_id, "type": "task", "limit": 20})
            dc = await _call_api("POST", "/api/v1/calendar/data-center/query", json_body={"search": task_id, "objectType": "task", "page": 1, "pageSize": 20})
            if isinstance(dc, dict) and "data" in dc:
                dc_data = dc["data"]
                dc_items = dc_data.get("items") if isinstance(dc_data, dict) else []
                for it in dc_items if isinstance(dc_items, list) else []:
                    if isinstance(it, dict) and str(it.get("objectId")) == task_id:
                        return {"code": 0, "data": it, "note": "via data-center fallback"}
        except Exception:
            pass
        list_res = await _call_api("GET", "/api/v1/calendar/tasks", params={"page": 1, "pageSize": 100})
        if isinstance(list_res, dict) and "data" in list_res:
            data = list_res["data"]
            items = data.get("items") if isinstance(data, dict) and "items" in data else data if isinstance(data, list) else []
            for it in items if isinstance(items, list) else []:
                if isinstance(it, dict) and str(it.get("id")) == task_id:
                    return {"code": 0, "data": it}
            if isinstance(data, dict) and "items" in data:
                for it in data["items"]:
                    if str(it.get("id")) == task_id:
                        return {"code": 0, "data": it}
        return {"error": f"task {task_id} not found (fallback page 1 only)", "code": 404}
    if isinstance(res, dict) and "error" in res and "404" in str(res.get("error", "")):
        list_res = await _call_api("GET", "/api/v1/calendar/tasks", params={"page": 1, "pageSize": 100})
        if isinstance(list_res, dict) and "data" in list_res:
            data = list_res["data"]
            items = data.get("items") if isinstance(data, dict) and "items" in data else data if isinstance(data, list) else []
            for it in items if isinstance(items, list) else []:
                if isinstance(it, dict) and str(it.get("id")) == task_id:
                    return {"code": 0, "data": it}
        return res
    return res


@mcp.tool()
async def get_habit_occurrences(
    habit_id: str,
    start: str,
    end: str,
) -> Any:
    """List occurrences for a habit in range. Returns Occurrence[]. Uses data-center query as primary (no dedicated GET)."""
    err = _validate_time_range(start, end)
    if err:
        return err
    # No dedicated GET /habits/{id}/occurrences exists (only POST for creation). Use data-center query directly.
    # Keep a best-effort attempt for future API, but primary is data-center.
    try:
        res = await _call_api("GET", f"/api/v1/calendar/habits/{habit_id}/occurrences", params={"start": start, "end": end})
        if isinstance(res, dict) and res.get("code") in (404, 405):
            raise ValueError("fallback")
        if isinstance(res, dict) and "error" not in res:
            return res
    except Exception:
        pass
    fallback = await _call_api(
        "POST",
        "/api/v1/calendar/data-center/query",
        json_body={"search": habit_id, "objectType": "habit-occurrence", "page": 1, "pageSize": 50},
    )
    return fallback


@mcp.tool()
async def get_schedule_preview(
    taskIds: Optional[List[str]] = None,
) -> Any:
    """Get schedule preview (read-only). Returns SchedulePlan. If taskIds omitted, previews all pending tasks."""
    body = {"taskIds": taskIds or []}
    # Endpoint is POST /calendar/schedule (read preview)
    res = await _call_api("POST", "/api/v1/calendar/schedule", json_body=body)
    return res


@mcp.tool()
async def get_calendar_by_id(calendar_id: str) -> Any:
    """Get single calendar by id (via list filter). Returns Calendar."""
    res = await _call_api("GET", "/api/v1/calendar/calendars")
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        for cal in res["data"]:
            if isinstance(cal, dict) and str(cal.get("id")) == calendar_id:
                return {"code": 0, "data": cal}
        return {"error": f"calendar {calendar_id} not found", "code": 404}
    return res


@mcp.tool()
async def get_task_checklist(task_id: str) -> Any:
    """Get checklist items for a task. Returns ChecklistItem[]. Best-effort (no dedicated GET)."""
    # No dedicated GET /tasks/{id}/checklist exists (only POST for creation). Try GET for forward-compat, then fallback to task detail.
    try:
        res = await _call_api("GET", f"/api/v1/calendar/tasks/{task_id}/checklist")
        if isinstance(res, dict) and res.get("code") not in (404, 405) and "error" not in res:
            return res
        # if 404/405, fall through to fallback
    except Exception:
        pass
    # fallback: get task and extract checklist if embedded
    task_res = await _call_api("GET", f"/api/v1/calendar/tasks/{task_id}")
    # Also try via list fallback inside get_task_by_id logic
    if isinstance(task_res, dict) and "data" in task_res:
        data = task_res["data"]
        # PagedResult or single dict
        if isinstance(data, dict):
            if "checklist" in data or "checklistItems" in data:
                return {"code": 0, "data": data.get("checklist") or data.get("checklistItems")}
            # handle case where data is PagedResult containing items
            if "items" in data and isinstance(data["items"], list):
                for it in data["items"]:
                    if isinstance(it, dict) and str(it.get("id")) == task_id:
                        if "checklist" in it or "checklistItems" in it:
                            return {"code": 0, "data": it.get("checklist") or it.get("checklistItems")}
                        return {"error": "checklist not available for task", "code": 404, "details": {"task": it}}
        elif isinstance(data, list):
            for it in data:
                if isinstance(it, dict) and str(it.get("id")) == task_id:
                    if "checklist" in it or "checklistItems" in it:
                        return {"code": 0, "data": it.get("checklist") or it.get("checklistItems")}
                    return {"error": "checklist not available for task", "code": 404, "details": {"task": it}}
    # final fallback: try direct task fetch via list
    return {"error": "checklist endpoint not available and not embedded in task", "code": 404, "details": task_res}


@mcp.tool()
async def search_calendar_events(
    q: str,
    start: Optional[str] = None,
    end: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Search calendar events by keyword with optional time range. Returns Event[]. Uses /calendar/events?search and falls back to /search."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    params = _clean_params(search=q, start=start, end=end, page=page, pageSize=pageSize)
    res = await _call_api("GET", "/api/v1/calendar/events", params=params)
    # fallback to global search if no results or endpoint misbehaves
    if isinstance(res, dict) and res.get("code") == 400 and "search" in str(res.get("error", "")).lower():
        # try global search
        g_params = _clean_params(q=q, type="event", limit=pageSize)
        return await _call_api("GET", "/api/v1/search", params=g_params)
    return res


@mcp.tool()
async def search_calendar_tasks(
    q: str,
    start: Optional[str] = None,
    end: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Search calendar tasks by keyword with optional time range. Returns Task[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if start and end:
        err2 = _validate_time_range(start, end)
        if err2:
            return err2
    params = _clean_params(search=q, start=start, end=end, page=page, pageSize=pageSize)
    res = await _call_api("GET", "/api/v1/calendar/tasks", params=params)
    if isinstance(res, dict) and res.get("code") == 400 and "search" in str(res.get("error", "")).lower():
        g_params = _clean_params(q=q, type="task", limit=pageSize)
        return await _call_api("GET", "/api/v1/search", params=g_params)
    return res


# ===================== PcTracker 27 =====================

@mcp.tool()
async def get_pc_summary(
    date: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC summary for a day. Returns PcSummaryResponse. date format YYYY-MM-DD."""
    # validate date
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/summary", params=params)


@mcp.tool()
async def get_pc_detail(
    date: Optional[str] = None,
    dateFrom: Optional[str] = None,
    dateTo: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
    redactUrls: bool = True,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Get PC detail records with filters. Returns TypedDetailQueryResponse. Supports redactUrls for url hashing."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    # date validations if provided
    for d in [date, dateFrom, dateTo]:
        if d:
            try:
                datetime.strptime(d, "%Y-%m-%d")
            except Exception:
                return {"error": f"date {d} must be YYYY-MM-DD", "code": 400}
    params = _clean_params(dateFrom=dateFrom, dateTo=dateTo, date=date, timezone=timezone, page=page, pageSize=pageSize)
    # Note: API expects many filter params, we pass pagination and date range
    res = await _call_api("GET", "/api/v1/pc/detail", params=params, redact_urls=redactUrls)
    return res


@mcp.tool()
async def get_pc_timeline(
    date: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC timeline v1 (raw). Returns TimelineItem[] for a day."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/aw/timeline", params=params)


@mcp.tool()
async def get_pc_timeline_v2(
    date: str,
    timezone: str = DEFAULT_TIMEZONE,
    redactUrls: bool = True,
) -> Any:
    """Get PC timeline v2 (smoothed with classification). Returns TimelineV2Item[]. Preferred over v1."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/timeline/v2", params=params, redact_urls=redactUrls)


@mcp.tool()
async def get_pc_heatmap(
    start: str,
    end: str,
    dimension: str = "day",
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC heatmap grid. Returns HeatmapGridResponse. dimension=day."""
    try:
        s = _parse_iso8601(start)
        e = _parse_iso8601(end)
    except ValueError as ve:
        return {"error": str(ve), "code": 400}
    err = _validate_time_range(start, end)
    if err:
        return err
    # Use start/end as YYYY-MM-DD for this endpoint
    start_d = s.date().isoformat()
    end_d = e.date().isoformat()
    params = _clean_params(start=start_d, end=end_d, dimension=dimension, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/heatmap/grid", params=params)


@mcp.tool()
async def get_pc_activity_analysis(
    date: str,
    blockMinutes: int = 60,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC activity analysis for a day with blockMinutes (15/30/60). Returns PcActivityAnalysisResponse."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    if blockMinutes not in (15, 30, 60):
        return {"error": "blockMinutes must be 15, 30 or 60", "code": 400}
    params = _clean_params(date=date, blockMinutes=blockMinutes, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/activity-analysis", params=params)


@mcp.tool()
async def get_pc_quality(
    date: Optional[str] = None,
    dateFrom: Optional[str] = None,
    dateTo: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC data quality report. Returns PcQualityResponse. Supports single date or range."""
    for d in [date, dateFrom, dateTo]:
        if d:
            try:
                datetime.strptime(d, "%Y-%m-%d")
            except Exception:
                return {"error": f"date {d} must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, dateFrom=dateFrom, dateTo=dateTo, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/quality", params=params)


@mcp.tool()
async def get_pc_aw_heatmap(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC active-window heatmap (raw). Returns HeatmapBucket[]."""
    try:
        s = _parse_iso8601(start)
        e = _parse_iso8601(end)
    except ValueError as ve:
        return {"error": str(ve), "code": 400}
    err = _validate_time_range(start, end)
    if err:
        return err
    start_d = s.date().isoformat()
    end_d = e.date().isoformat()
    params = _clean_params(start=start_d, end=end_d, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/aw/heatmap", params=params)


@mcp.tool()
async def get_pc_keystats_range(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC keystats range. Returns KeystatsSummary[] per day."""
    try:
        s = _parse_iso8601(start)
        e = _parse_iso8601(end)
    except ValueError as ve:
        return {"error": str(ve), "code": 400}
    err = _validate_time_range(start, end)
    if err:
        return err
    start_d = s.date().isoformat()
    end_d = e.date().isoformat()
    params = _clean_params(start=start_d, end=end_d, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/keystats/range", params=params)


@mcp.tool()
async def get_pc_focus_blocks(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC focus blocks (deep work sessions) for range. Core for weekly report. Returns PcFocusBlocksResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(start=start, end=end, timezone=timezone)
    # API supports start/end as ISO or YYYY-MM-DD; we pass as received
    return await _call_api("GET", "/api/v1/pc/aggregation/focus-blocks", params=params)


@mcp.tool()
async def get_pc_app_usage(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
    limit: int = 20,
) -> Any:
    """Get PC app usage aggregation. Returns PcAppUsageResponse sorted by duration."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(start=start, end=end, timezone=timezone, limit=limit)
    return await _call_api("GET", "/api/v1/pc/aggregation/app-usage", params=params)


@mcp.tool()
async def get_pc_late_night(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC late-night usage aggregation. Returns PcLateNightResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(start=start, end=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/aggregation/late-night", params=params)


@mcp.tool()
async def get_pc_category_distribution(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC category time distribution for range. Returns PcCategoryDistributionResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(start=start, end=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/aggregation/category-distribution", params=params)


@mcp.tool()
async def get_pc_categories() -> Any:
    """List PC app category rules (flat). Returns AppCategoryRule[]."""
    return await _call_api("GET", "/api/v1/pc/categories")


@mcp.tool()
async def get_pc_category_tree() -> Any:
    """Get PC category tree (hierarchical). Returns CategoryTreeNode[]."""
    return await _call_api("GET", "/api/v1/pc/categories/tree")


@mcp.tool()
async def get_pc_category_dictionary() -> Any:
    """Get PC category dictionary (flat list with metadata). Returns CategoryDictionaryItemDto[]."""
    return await _call_api("GET", "/api/v1/pc/categories/dictionary")


@mcp.tool()
async def get_pc_productivity_dashboard(
    date: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC productivity dashboard for a day. Returns ProductivityDashboardDto."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/productivity/dashboard", params=params)


@mcp.tool()
async def get_pc_productivity_range(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get PC productivity range (weekly). Returns DailyProductivityDto[] per day."""
    err = _validate_time_range(start, end)
    if err:
        return err
    # API expects start/end as YYYY-MM-DD or ISO; convert to YYYY-MM-DD if needed
    try:
        s = _parse_iso8601(start).date().isoformat()
        e = _parse_iso8601(end).date().isoformat()
    except Exception:
        s, e = start, end
    params = _clean_params(start=s, end=e, timezone=timezone)
    return await _call_api("GET", "/api/v1/pc/productivity/range", params=params)


@mcp.tool()
async def get_classification_rules() -> Any:
    """List PC activity classification rules. Returns ActivityClassificationRuleDto[]."""
    return await _call_api("GET", "/api/v1/pc/classification/rules")


@mcp.tool()
async def get_classification_suggestions(date: str) -> Any:
    """List classification suggestions for a day. Returns ActivityClassificationSuggestionDto[]."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date)
    return await _call_api("GET", "/api/v1/pc/classification/suggestions", params=params)


@mcp.tool()
async def get_classification_queue(
    limit: int = 20,
    mode: str = "queue",
) -> Any:
    """Get classification labeling queue. Returns ActivityLabelingQueueResponse."""
    if limit < 1 or limit > 100:
        return {"error": "limit must be 1..100", "code": 400}
    params = _clean_params(limit=limit, mode=mode)
    return await _call_api("GET", "/api/v1/pc/classification/queue", params=params)


@mcp.tool()
async def get_classification_project_tags_recent(limit: int = 10) -> Any:
    """Get recent project tags for classification. Returns string[]."""
    if limit < 1 or limit > 100:
        return {"error": "limit must be 1..100", "code": 400}
    return await _call_api("GET", "/api/v1/pc/classification/project-tags/recent", params={"limit": limit})


@mcp.tool()
async def get_app_knowledge_apps(
    search: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List app-knowledge apps. Returns AppKnowledgeAppDto[] with optional search."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(search=search)
    res = await _call_api("GET", "/api/v1/pc/app-knowledge/apps", params=params)
    # pagination locally if needed
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
    return res


@mcp.tool()
async def get_app_knowledge_contexts(appId: str) -> Any:
    """Get contexts for an app-knowledge app. Returns AppKnowledgeContextDto[]."""
    return await _call_api("GET", f"/api/v1/pc/app-knowledge/apps/{appId}/contexts")


@mcp.tool()
async def get_app_signatures(
    search: Optional[str] = None,
    page: int = 1,
    pageSize: int = 50,
) -> Any:
    """List app signatures (process -> app mapping). Returns AppSignatureDto[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(search=search)
    res = await _call_api("GET", "/api/v1/pc/app-signatures", params=params)
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], list):
        data = res["data"]
        total = len(data)
        start_idx = (page - 1) * pageSize
        paged = data[start_idx : start_idx + pageSize]
        res = dict(res)
        res["data"] = paged
        res["page"] = page
        res["pageSize"] = pageSize
        res["total"] = total
    return res


@mcp.tool()
async def lookup_app_signature(processName: str) -> Any:
    """Lookup app signature by process name. Returns AppSignatureDto or 404."""
    return await _call_api("GET", f"/api/v1/pc/app-signatures/lookup/{processName}")


@mcp.tool()
async def get_classification_settings() -> Any:
    """Get classification settings. Returns ActivityClassificationSettingsDto."""
    return await _call_api("GET", "/api/v1/pc/classification/settings")


# ===================== Mobile 18 =====================

@mcp.tool()
async def get_mobile_summary(
    date: str,
    deviceId: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile usage summary for a day (separate from timeline). Returns MobileUsageSummaryResponse."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, deviceId=deviceId, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/summary", params=params)


@mcp.tool()
async def get_mobile_timeline(
    date: str,
    deviceId: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
    redactUrls: bool = True,
) -> Any:
    """Get mobile timeline for a day (app usage sessions). Returns MobileTimelineResponse."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, deviceId=deviceId, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/timeline", params=params, redact_urls=redactUrls)


@mcp.tool()
async def get_mobile_location_history(
    start: Optional[str] = None,
    end: Optional[str] = None,
    maxAccuracyMeters: float = 50,
    deviceId: Optional[str] = None,
) -> Any:
    """Get mobile location history in range. Returns MobileLocationHistoryResponse with points."""
    if start and end:
        err = _validate_time_range(start, end)
        if err:
            return err
    params = _clean_params(start=start, end=end, maxAccuracyMeters=maxAccuracyMeters, deviceId=deviceId)
    return await _call_api("GET", "/api/v1/mobile/location/history", params=params)


@mcp.tool()
async def get_mobile_location_latest(
    maxAccuracyMeters: float = 50,
    deviceId: Optional[str] = None,
) -> Any:
    """Get latest mobile location point. Returns single point (history with latest)."""
    params = _clean_params(maxAccuracyMeters=maxAccuracyMeters, deviceId=deviceId)
    res = await _call_api("GET", "/api/v1/mobile/location/history", params=params)
    # Extract latest point if history wrapper
    if isinstance(res, dict) and "data" in res and isinstance(res["data"], dict):
        data = res["data"]
        points = data.get("points") or data.get("items") or []
        if isinstance(points, list) and points:
            latest = points[-1] if isinstance(points, list) else points
            # Return with wrapper but highlight latest
            return {"code": 0, "data": latest, "meta": {"total": len(points)}}
    return res


@mcp.tool()
async def get_mobile_location_tracks(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
    maxAccuracyMeters: float = 50,
) -> Any:
    """Get mobile location tracks (clustered). Returns MobileLocationTrackDto[]."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone, maxAccuracyMeters=maxAccuracyMeters)
    return await _call_api("GET", "/api/v1/mobile/location/analytics/tracks", params=params)


@mcp.tool()
async def get_mobile_location_overview(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile location overview (distance, stay, movement). Returns MobileLocationAnalyticsOverviewResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/location/analytics/overview", params=params)


@mcp.tool()
async def get_mobile_location_frequent_places(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get frequent places derived from location. Returns MobileFrequentPlacesResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/location/analytics/frequent-places", params=params)


@mcp.tool()
async def get_mobile_location_movement_stats(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get movement stats (speed, distance, active time). Returns MobileMovementStatsResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/location/analytics/movement-stats", params=params)


@mcp.tool()
async def get_mobile_quality(
    date: str,
    deviceId: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile collection quality for a day. Returns MobileQualityResponse."""
    try:
        datetime.strptime(date, "%Y-%m-%d")
    except Exception:
        return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, deviceId=deviceId, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/quality", params=params)


@mcp.tool()
async def get_mobile_analytics_overview(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile analytics overview (usage). Core for weekly report. Returns MobileAnalyticsOverviewResponse."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/analytics/overview", params=params)


@mcp.tool()
async def get_mobile_analytics_heatmap(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile analytics heatmap (hourly). Returns MobileHeatmapBucketDto[]."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/analytics/heatmap", params=params)


@mcp.tool()
async def get_mobile_analytics_charts(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get mobile analytics charts data. Returns MobileAnalyticsChartDto[]."""
    err = _validate_time_range(start, end)
    if err:
        return err
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone)
    return await _call_api("GET", "/api/v1/mobile/analytics/charts", params=params)


@mcp.tool()
async def get_mobile_timeline_blocks(
    start: str,
    end: str,
    timezone: str = DEFAULT_TIMEZONE,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Get mobile timeline blocks (aggregated sessions). Returns MobileTimelineBlockPageDto."""
    err = _validate_time_range(start, end)
    if err:
        return err
    err2 = _validate_pagination(page, pageSize)
    if err2:
        return err2
    params = _clean_params(rangeStartUtc=start, rangeEndUtc=end, timezone=timezone, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/mobile/analytics/timeline-blocks", params=params)


@mcp.tool()
async def get_mobile_devices() -> Any:
    """List mobile devices (simple). Returns MobileDeviceDto[]."""
    return await _call_api("GET", "/api/v1/mobile/devices")


@mcp.tool()
async def get_mobile_devices_manage(
    sortBy: Optional[str] = None,
) -> Any:
    """List mobile devices with management info, optional sortBy. Returns DeviceListDto[]."""
    params = _clean_params(sortBy=sortBy)
    return await _call_api("GET", "/api/v1/mobile/devices/manage", params=params)


@mcp.tool()
async def get_mobile_apps_catalog_overrides() -> Any:
    """List mobile app catalog overrides. Returns MobileAppCatalogOverrideDto[]."""
    return await _call_api("GET", "/api/v1/mobile/apps/catalog-overrides")


@mcp.tool()
async def get_mobile_apps_category_rules() -> Any:
    """List mobile app category rules. Returns MobileAppCategoryRuleDto[]."""
    return await _call_api("GET", "/api/v1/mobile/apps/category-rules")


@mcp.tool()
async def get_mobile_goals() -> Any:
    """List mobile usage goals. Returns MobileUsageGoalDto[]."""
    return await _call_api("GET", "/api/v1/mobile/analytics/goals")


# ===================== QuickNotes 3 =====================

@mcp.tool()
async def get_quick_notes(
    status: Optional[str] = None,
    search: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List quick notes with optional status and search. Returns PagedResult<QuickNoteListItemDto>."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(status=status, search=search, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/quick-notes", params=params)


@mcp.tool()
async def get_quick_note(note_id: str) -> Any:
    """Get single quick note by id. Returns QuickNoteDetailDto."""
    return await _call_api("GET", f"/api/v1/quick-notes/{note_id}")


@mcp.tool()
async def get_quick_note_attachment_meta(attachment_id: str) -> Any:
    """Get quick note attachment metadata (not binary). Returns download info meta. If API streams binary, returns headers only."""
    # This endpoint normally streams file; for metadata we attempt HEAD or GET and return meta without consuming binary
    token = _get_token()
    if not token:
        return {"error": "missing bearer token: call MCP with Authorization: Bearer <PIM JWT>", "code": 401}
    headers = {"Authorization": f"Bearer {token}"}
    url = f"{PIM_API_URL}/api/v1/quick-notes/attachments/{attachment_id}/download"
    try:
        async with httpx.AsyncClient(timeout=15) as client:
            # Try HEAD first
            try:
                resp = await client.head(url, headers=headers)
                if resp.status_code < 400:
                    return {
                        "code": 0,
                        "data": {
                            "attachmentId": attachment_id,
                            "headers": dict(resp.headers),
                            "status": resp.status_code,
                            "note": "metadata from HEAD, binary not downloaded",
                        },
                    }
            except Exception:
                pass
            # Fallback GET with stream, only headers
            resp = await client.get(url, headers=headers)
            if resp.status_code >= 400:
                try:
                    data = resp.json()
                except Exception:
                    data = {"raw": resp.text[:500]}
                return {"error": f"HTTP {resp.status_code}: {resp.text[:500]}", "details": data, "code": resp.status_code}
            return {
                "code": 0,
                "data": {
                    "attachmentId": attachment_id,
                    "headers": dict(resp.headers),
                    "size": resp.headers.get("content-length"),
                    "contentType": resp.headers.get("content-type"),
                    "note": "binary not returned, only metadata per read-only policy",
                },
            }
    except Exception as e:
        return {"error": f"request failed: {e}", "code": 500}


# ===================== Files 8 =====================

@mcp.tool()
async def get_file_providers() -> Any:
    """List file providers (e.g. Nextcloud). Returns FileProviderDto[]."""
    return await _call_api("GET", "/api/v1/files/providers")


@mcp.tool()
async def get_files(
    folderId: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
    redactUrls: bool = True,
) -> Any:
    """List files with optional folder. Returns FileListResponse (paged)."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(path=folderId, page=page, pageSize=pageSize)
    # API uses 'path' as folder param, but we also support folderId alias
    if folderId:
        params["path"] = folderId
    return await _call_api("GET", "/api/v1/files/items", params=params, redact_urls=redactUrls)


@mcp.tool()
async def get_file(file_id: str) -> Any:
    """Get single file item by id. Returns FileItemDto."""
    return await _call_api("GET", f"/api/v1/files/items/{file_id}")


@mcp.tool()
async def get_file_versions(file_id: str) -> Any:
    """List versions for a file. Returns FileVersion[]."""
    return await _call_api("GET", f"/api/v1/files/items/{file_id}/versions")


@mcp.tool()
async def get_file_trash(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List file trash items. Returns ProviderTrashItem[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/files/trash", params=params)


@mcp.tool()
async def search_files(
    q: str,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """Search files (fulltext + semantic). Core for RAG. Returns FileSearchResponse (paged)."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(q=q, page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/files/search", params=params)


@mcp.tool()
async def get_file_suggestions(
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List file organization suggestions. Returns FileSuggestion[]."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    params = _clean_params(page=page, pageSize=pageSize)
    return await _call_api("GET", "/api/v1/files/suggestions", params=params)


@mcp.tool()
async def get_file_open_link(file_id: str) -> Any:
    """Get open link for a file (WebDAV/Nextcloud). Returns {openLink}. Note: may contain url, hashed if redactUrls implicit. Call with redact handling."""
    res = await _call_api("GET", f"/api/v1/files/items/{file_id}/open-link")
    # Apply redaction manually if needed; by default we hash urls
    if isinstance(res, dict) and "data" in res:
        # check if data contains url-like
        res = dict(res)
        res["data"] = _apply_redact(res["data"], True)
    return res


# ===================== Core/Infra 14 =====================

@mcp.tool()
async def get_today_sections(
    date: Optional[str] = None,
    timezone: str = DEFAULT_TIMEZONE,
) -> Any:
    """Get today sections registry (all sections for a day). Returns TodaySectionRegistryDto. date YYYY-MM-DD or omit for today."""
    if date:
        try:
            datetime.strptime(date, "%Y-%m-%d")
        except Exception:
            return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date, timezone=timezone)
    return await _call_api("GET", "/api/v1/today/sections", params=params)


@mcp.tool()
async def get_today_section(
    sectionId: str,
    date: Optional[str] = None,
) -> Any:
    """Get single today section by id. Returns TodaySectionDto."""
    if date:
        try:
            datetime.strptime(date, "%Y-%m-%d")
        except Exception:
            return {"error": "date must be YYYY-MM-DD", "code": 400}
    params = _clean_params(date=date)
    return await _call_api("GET", f"/api/v1/today/sections/{sectionId}", params=params)


@mcp.tool()
async def search_pim(
    q: str,
    type: Optional[str] = None,
    limit: int = 20,
) -> Any:
    """Global search across PIM (events, tasks, notes, files). Returns PagedResult<SearchResult>. type comma-separated e.g. 'event,task,note,file'."""
    if limit < 1 or limit > 100:
        return {"error": "limit must be 1..100", "code": 400}
    params = _clean_params(q=q, type=type, limit=limit)
    return await _call_api("GET", "/api/v1/search", params=params)


@mcp.tool()
async def get_system_status() -> Any:
    """Get system status (overall). Returns StatusResponse. Maps to GET /status."""
    return await _call_api("GET", "/api/v1/status")


@mcp.tool()
async def get_system_health() -> Any:
    """Get health check. Returns {status, timestamp}. Maps to GET /health (allow anonymous but we pass token)."""
    # Health is at root /health not under /api/v1, handle specially
    token = _get_token()
    if not token:
        # health may be anonymous, try without token
        try:
            async with httpx.AsyncClient(timeout=10) as client:
                resp = await client.get(f"{PIM_API_URL}/health")
                try:
                    data = resp.json()
                except Exception:
                    data = {"raw": resp.text[:500]}
                if resp.status_code >= 400:
                    return {"error": f"HTTP {resp.status_code}: {resp.text[:500]}", "code": resp.status_code, "details": data}
                return data
        except Exception as e:
            return {"error": f"health check failed: {e}", "code": 500}
    # with token, use _call_api but health is at /health
    try:
        async with httpx.AsyncClient(timeout=10) as client:
            resp = await client.get(f"{PIM_API_URL}/health", headers={"Authorization": f"Bearer {token}"})
            try:
                data = resp.json()
            except Exception:
                data = {"raw": resp.text[:500]}
            if resp.status_code >= 400:
                return {"error": f"HTTP {resp.status_code}: {resp.text[:500]}", "code": resp.status_code, "details": data}
            return data
    except Exception as e:
        return {"error": f"health check failed: {e}", "code": 500}


@mcp.tool()
async def get_status_summary() -> Any:
    """Get status summary (counts). Returns SummaryResponse. Maps to GET /status/summary."""
    return await _call_api("GET", "/api/v1/status/summary")


@mcp.tool()
async def get_ai_status() -> Any:
    """Get AI gateway status. Returns {enabled, model, health}. Maps to GET /ai/status."""
    return await _call_api("GET", "/api/v1/ai/status")


@mcp.tool()
async def get_ai_requests(
    from_time: Optional[str] = None,
    to: Optional[str] = None,
    module: Optional[str] = None,
    status: Optional[str] = None,
    page: int = 1,
    pageSize: int = 20,
) -> Any:
    """List AI requests with filters. Returns PagedResult<AiRequest>."""
    err = _validate_pagination(page, pageSize)
    if err:
        return err
    if from_time and to:
        err2 = _validate_time_range(from_time, to)
        if err2:
            return err2
    params = _clean_params(**{"from": from_time, "to": to, "module": module, "status": status, "page": page, "pageSize": pageSize})
    return await _call_api("GET", "/api/v1/ai/requests", params=params)


@mcp.tool()
async def get_ai_usage_summary(
    from_time: str,
    to: Optional[str] = None,
) -> Any:
    """Get AI usage summary in range. Returns {totalRequests, tokens, cost}. Maps to GET /ai/usage/summary?from&to."""
    if to:
        err = _validate_time_range(from_time, to)
        if err:
            return err
    else:
        # validate from_time alone is parseable
        try:
            _parse_iso8601(from_time)
        except ValueError as ve:
            return {"error": str(ve), "code": 400}
    params = _clean_params(**{"from": from_time, "to": to})
    return await _call_api("GET", "/api/v1/ai/usage/summary", params=params)


@mcp.tool()
async def get_audit_timeline(
    objectType: str,
    objectId: str,
) -> Any:
    """Get audit timeline for an object. Returns AuditVersion[] for objectType/objectId."""
    return await _call_api("GET", f"/api/v1/operations/audit/{objectType}/{objectId}")


@mcp.tool()
async def get_audit_export(
    start: Optional[str] = None,
    end: Optional[str] = None,
) -> Any:
    """Export audit log in range (operations). Returns AuditExport."""
    if start and end:
        err = _validate_time_range(start, end)
        if err:
            return err
    params = _clean_params(start=start, end=end)
    return await _call_api("GET", "/api/v1/operations/audit/export", params=params)


@mcp.tool()
async def get_confirmations_pending() -> Any:
    """List pending confirmations needing human approval. Returns OperationConfirmationDto[]."""
    return await _call_api("GET", "/api/v1/operations/confirmations/pending")


@mcp.tool()
async def get_endpoints() -> Any:
    """List registered endpoints/clients. Returns EndpointDto[]."""
    return await _call_api("GET", "/api/v1/endpoints")


@mcp.tool()
async def get_version() -> Any:
    """Get API version info. Returns {version, gitSha, buildTime}. Maps to GET /api/version."""
    token = _get_token()
    if not token:
        # version is anonymous, try without token
        try:
            async with httpx.AsyncClient(timeout=10) as client:
                resp = await client.get(f"{PIM_API_URL}/api/version")
                try:
                    data = resp.json()
                except Exception:
                    data = {"raw": resp.text[:500]}
                if resp.status_code >= 400:
                    return {"error": f"HTTP {resp.status_code}: {resp.text[:500]}", "code": resp.status_code, "details": data}
                return data
        except Exception as e:
            return {"error": f"version check failed: {e}", "code": 500}
    # with token, still call same
    try:
        async with httpx.AsyncClient(timeout=10) as client:
            resp = await client.get(f"{PIM_API_URL}/api/version", headers={"Authorization": f"Bearer {token}"})
            try:
                data = resp.json()
            except Exception:
                data = {"raw": resp.text[:500]}
            if resp.status_code >= 400:
                return {"error": f"HTTP {resp.status_code}: {resp.text[:500]}", "code": resp.status_code, "details": data}
            return data
    except Exception as e:
        return {"error": f"version check failed: {e}", "code": 500}


# ---------- entrypoint ----------
def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
