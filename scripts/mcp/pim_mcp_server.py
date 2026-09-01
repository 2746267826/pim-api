"""
PIM MCP v3 - Read + Write server for AI Agent
Exposes 151 tools: 101 read-only (Calendar / PcTracker / Mobile / QuickNotes / Files / Core/Infra)
plus 50 write tools (Calendar 30 + QuickNotes 8 + Files 6 + PcTracker 4 + Mobile 2).

Transports:
- stdio (default, v2-compatible): Bearer pass-through. Client obtains JWT via
  POST /api/v1/auth/login and calls with PIM_ACCESS_TOKEN env or a token file
  (PIM_TOKEN_FILE / .token next to script) with mtime+size hot reload and optional
  auto-refresh via PIM_REFRESH_TOKEN (issue #174). Audits real userId.
- HTTP (Streamable HTTP, Phase 3): PIM_MCP_TRANSPORT=http. Every request carries
  `Authorization: Bearer <pim_mcp_* token>`. Each tool call is authorized via
  POST /api/v1/mcp/verify: token + tool-level permission check + connection
  activity/audit tracking; on success a short-lived user JWT is issued and used
  for the actual REST call. Missing/invalid/revoked token -> 401, denied write
  permission -> 403 "permission denied: <tool>".

Conventions:
- time: start/end ISO8601 UTC, timezone IANA default Asia/Shanghai, max span 366 days
- pagination: page>=1, pageSize 1..100 default 20
- redactUrls: True hashes any field containing 'url' to 12-char sha256 hex (urlHash), False returns raw
- response >50KB adds truncated/nextPage hint
"""

import base64
import hashlib
import json
import os
import re
import time
import threading
import asyncio
import contextvars
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
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

# HTTP transport settings (Streamable HTTP). Only used when PIM_MCP_TRANSPORT=http.
_MCP_TRANSPORT = os.getenv("PIM_MCP_TRANSPORT", "stdio").strip().lower()
_MCP_HOST = os.getenv("PIM_MCP_HOST", "0.0.0.0")
_MCP_PORT = int(os.getenv("PIM_MCP_PORT", "8080"))
_MCP_PATH = os.getenv("PIM_MCP_PATH", "/mcp")

# Token file candidates are checked in order; first existing file wins unless
# PIM_TOKEN_FILE explicitly points to a path (even if not yet created).
_TOKEN_FILE_CANDIDATES: List[str] = []

def _get_token_file_candidates() -> List[str]:
    cands: List[str] = []
    for env_name in ("PIM_TOKEN_FILE", "PIM_TOKEN_PATH"):
        v = os.getenv(env_name)
        if v and v.strip():
            cands.append(v.strip())
    # Adjacent to this script: run.py wrapper writes .token here per issue #174
    cands.append(os.path.join(os.path.dirname(__file__), ".token"))
    cands.append(os.path.join(os.path.dirname(__file__), ".pim-token"))
    cands.append(os.path.expanduser("~/.pim/token"))
    cands.append(os.path.expanduser("~/.pim/token.json"))
    cands.append(os.path.expanduser("~/.config/pim/token"))
    cands.append(os.path.expanduser("~/.config/pim/token.json"))
    return cands

# Cache for file-backed tokens: tracks mtime+size to avoid re-reading unchanged files.
_token_file_cache: Dict[str, Any] = {"access_token": None, "refresh_token": None, "mtime": 0, "size": 0, "path": None}
_token_file_lock = threading.Lock()
# Async single-flight for refresh: at most one concurrent POST /auth/refresh
_refresh_lock = asyncio.Lock()
_refresh_inflight: Optional[asyncio.Future] = None

mcp = FastMCP("pim-mcp-server", host=_MCP_HOST, port=_MCP_PORT, streamable_http_path=_MCP_PATH)


# ---------- HTTP mode: per-request identity via /api/v1/mcp/verify ----------
#
# In HTTP mode every tool call is first authorized against Pim.Api's /verify
# endpoint with the raw `pim_mcp_*` token from the Authorization header.
# /verify performs tool-level permission checks, records connection activity
# and audit, and returns a short-lived user JWT used for the actual REST call.
# stdio mode is untouched: it keeps the env/file token pass-through.

# Request-scoped identity: {accessToken, clientId, clientName, permissions}
_current_identity: contextvars.ContextVar[Optional[Dict[str, Any]]] = contextvars.ContextVar(
    "pim_mcp_identity", default=None
)

# Set of write tool names (Phase 3). Read tools are everything else.
_WRITE_TOOL_NAMES: set = set()


def _http_mode() -> bool:
    return _MCP_TRANSPORT in ("http", "streamable-http")


def _get_raw_request_token() -> Optional[str]:
    """Extract the raw Bearer token from the current HTTP request (mcp_token or user JWT)."""
    if not _http_mode():
        return None
    try:
        ctx = mcp.get_context()
        rc = ctx.request_context
        if rc is None:
            return None
        req = rc.request
        auth = getattr(req, "headers", None).get("authorization", "")
    except Exception:
        return None
    if not auth:
        return None
    return _strip_bearer(auth)


async def _call_verify(raw_token: str, tool: Optional[str], params_summary: Optional[str]) -> Optional[Dict[str, Any]]:
    """Call Pim.Api /api/v1/mcp/verify with the raw mcp token. Returns response data dict or None on network error."""
    try:
        headers = {"Authorization": f"Bearer {raw_token}", "Content-Type": "application/json"}
        body: Dict[str, Any] = {}
        if tool:
            body["tool"] = tool
        if params_summary:
            body["paramsSummary"] = params_summary
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(f"{PIM_API_URL}/api/v1/mcp/verify", json=body, headers=headers)
            try:
                data = resp.json()
            except Exception:
                data = {"raw": resp.text[:400], "status": resp.status_code}
            if resp.status_code >= 400:
                if isinstance(data, dict) and "error" in data:
                    return {"error": str(data.get("error")), "code": resp.status_code}
                if isinstance(data, dict) and "message" in data:
                    return {"error": str(data.get("message")), "code": resp.status_code}
                return {"error": f"HTTP {resp.status_code}: {resp.text[:400]}", "code": resp.status_code}
            inner = data.get("data") if isinstance(data, dict) else None
            if isinstance(inner, dict):
                return inner
            return {"error": "unexpected verify response", "code": 500}
    except Exception as e:
        return {"error": f"verify request failed: {e}", "code": 500}


def _summarize_params(args: Dict[str, Any]) -> Optional[str]:
    """Truncated JSON summary of tool arguments for audit (never contains tokens)."""
    if not args:
        return None
    try:
        text = json.dumps(args, ensure_ascii=False, default=str, sort_keys=True)
    except Exception:
        return None
    return text if len(text) <= 500 else text[:500]


async def _resolve_http_identity(tool: str, args: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    """Authorize a tool call in HTTP mode. Returns the identity dict, or an error dict to short-circuit."""
    raw = _get_raw_request_token()
    if not raw:
        return {
            "error": "missing bearer token: call MCP with Authorization: Bearer <pim_mcp_* token>. "
                     "Generate a token in WebUI Settings -> MCP 管理.",
            "code": 401,
        }
    data = await _call_verify(raw, tool, _summarize_params(args))
    if data is None:
        return {"error": "verify failed: no response", "code": 500}
    return data


def _wrap_tools_for_http() -> None:
    """Wrap every registered tool so HTTP mode authorizes + enforces permissions before the call."""
    for tool in mcp._tool_manager.list_tools():  # noqa: SLF001
        if getattr(tool, "_pim_wrapped", False):
            continue
        name = tool.name
        orig = tool.fn

        async def wrapped(_name: str = name, _orig: Any = orig, **kwargs: Any) -> Any:
            data = await _resolve_http_identity(_name, kwargs)
            if "error" in data:
                return data
            reset = _current_identity.set(data)
            try:
                return await _orig(**kwargs)
            finally:
                _current_identity.reset(reset)

        tool.fn = wrapped
        tool._pim_wrapped = True  # noqa: SLF001


def _register_write_tool_names(*names: str) -> None:
    _WRITE_TOOL_NAMES.update(names)


def _is_write_tool(name: str) -> bool:
    return name in _WRITE_TOOL_NAMES


def _list_tools_meta() -> List[Dict[str, str]]:
    """Test/debug helper: metadata for every registered tool."""
    return [
        {"name": t.name, "group": "write" if t.name in _WRITE_TOOL_NAMES else "read"}
        for t in mcp._tool_manager.list_tools()  # noqa: SLF001
    ]


# ---------- helpers: auth, time, pagination, redaction, api ----------

def _decode_jwt_exp(token: str) -> Optional[int]:
    """Decode JWT exp claim without verification. Returns None if not a JWT or no exp."""
    try:
        parts = token.strip().split(".")
        if len(parts) < 2:
            return None
        payload_b64 = parts[1].strip()
        # Correct padding: (-len % 4) pads 0..3, handles len%4==1 gracefully (will fail decode -> None)
        payload_b64 += "=" * ((-len(payload_b64)) % 4)
        payload_json = base64.urlsafe_b64decode(payload_b64.encode("utf-8"))
        data = json.loads(payload_json)
        exp = data.get("exp")
        if isinstance(exp, (int, float)):
            return int(exp)
        if isinstance(exp, str):
            try:
                return int(exp.strip())
            except Exception:
                return None
        return None
    except Exception:
        return None


def _is_token_expired(token: str, leeway_seconds: int = 60) -> bool:
    """True if JWT exp is within leeway_seconds from now. Non-JWT tokens are treated as not expired."""
    exp = _decode_jwt_exp(token)
    if exp is None:
        return False
    return exp < time.time() + leeway_seconds


def _strip_bearer(v: str) -> str:
    vv = v.strip()
    if vv.lower().startswith("bearer "):
        return vv[7:].strip()
    return vv


def _read_token_file(path: str) -> Tuple[Optional[str], Optional[str]]:
    """Read token file. Supports plain JWT, or JSON with accessToken/refreshToken."""
    try:
        p = Path(path)
        if not p.is_file():
            return None, None
        # Size guard to avoid DoS via huge file
        try:
            if p.stat().st_size > 32 * 1024:
                return None, None
        except Exception:
            pass
        raw = p.read_text(encoding="utf-8").strip()
        if not raw:
            return None, None
        stripped = raw.lstrip()
        # Handle BOM/whitespace before JSON
        if stripped.startswith("{"):
            try:
                data = json.loads(stripped)
                # Unified lookup without duplication
                def _pick(d: Dict[str, Any], *keys: str) -> Optional[str]:
                    for k in keys:
                        v = d.get(k)
                        if isinstance(v, str) and v.strip():
                            return v
                    return None

                at = _pick(data, "accessToken", "access_token", "token")
                rt = _pick(data, "refreshToken", "refresh_token")
                # Also handle nested {data:{accessToken}} shape
                if not at and isinstance(data.get("data"), dict):
                    nested = data["data"]
                    if isinstance(nested, dict):
                        at = at or _pick(nested, "accessToken", "access_token", "token")
                        rt = rt or _pick(nested, "refreshToken", "refresh_token")
                if at:
                    at = _strip_bearer(at)
                    rt = _strip_bearer(rt) if isinstance(rt, str) and rt.strip() else None
                    if at:
                        return at, rt
            except Exception:
                pass
            # JSON but not recognized shape -> fall through to plain
        # Plain token (maybe with bearer prefix or newline separated)
        # Two-line heuristic: treat any two non-empty lines as (access, refresh); no JWT check required
        lines = [ln.strip() for ln in raw.splitlines() if ln.strip()]
        if len(lines) >= 2:
            first = _strip_bearer(lines[0])
            second = _strip_bearer(lines[1])
            # Only treat as pair if first looks like a token (non-empty after strip)
            if first:
                return first, second if second else None
        # Single token: strip bearer prefix if present, then take first whitespace token
        single = _strip_bearer(raw.strip())
        token = single.split()[0] if single else None
        return (token if token else None), None
    except Exception:
        return None, None


def _try_load_token_from_file() -> Tuple[Optional[str], Optional[str]]:
    """Load token from file with mtime+size cache. Returns (access, refresh)."""
    candidates = _get_token_file_candidates()
    for cand in candidates:
        try:
            p = Path(cand)
            if not p.is_file():
                continue
            try:
                st = p.stat()
                mtime = st.st_mtime
                size = st.st_size
            except Exception:
                continue
            with _token_file_lock:
                cached_path = _token_file_cache.get("path")
                cached_mtime = _token_file_cache.get("mtime", 0)
                cached_size = _token_file_cache.get("size", 0)
                if cached_path == cand and cached_mtime == mtime and cached_size == size and _token_file_cache.get("access_token"):
                    return _token_file_cache.get("access_token"), _token_file_cache.get("refresh_token")
            # Read outside lock to avoid blocking, but re-check mtime/size inside lock before committing
            at, rt = _read_token_file(cand)
            if at:
                with _token_file_lock:
                    # Re-stat to detect race where file was rewritten between our initial stat and read
                    try:
                        st2 = p.stat()
                        mtime2 = st2.st_mtime
                        size2 = st2.st_size
                    except Exception:
                        mtime2, size2 = mtime, size
                    _token_file_cache["access_token"] = at
                    _token_file_cache["refresh_token"] = rt
                    _token_file_cache["mtime"] = mtime2
                    _token_file_cache["size"] = size2
                    _token_file_cache["path"] = cand
                return at, rt
        except Exception:
            continue
    return None, None


def _get_refresh_token() -> Optional[str]:
    # Prefer file's refresh if it exists and env is stale (file fresher after rotation).
    # Strategy: check file first via cache/mtime, then env, but favor non-expired / fresher signal.
    # Simplicity: file cache is already mtime-validated; check it before env fallback.
    # However env is explicit operator intent, so if both present we prefer file when its mtime is newer.
    file_rt: Optional[str] = None
    with _token_file_lock:
        file_rt = _token_file_cache.get("refresh_token")
    if not file_rt:
        _, file_rt = _try_load_token_from_file()
    for env_name in ("PIM_REFRESH_TOKEN",):
        v = os.getenv(env_name)
        if v and v.strip():
            env_rt = _strip_bearer(v)
            # If file has a refresh and its backing file was recently updated, prefer file
            # (covers rotation where server invalidates old refresh after one use)
            if file_rt and file_rt != env_rt:
                # Prefer file's value when file exists (likely after _persist updated it)
                # unless env was explicitly set after file (no reliable clock, prefer file for safety)
                return file_rt
            return env_rt
    # Also check legacy aliases for backward compat but log via return
    for env_name in ("PIM_REFRESH", "MCP_REFRESH_TOKEN"):
        v = os.getenv(env_name)
        if v and v.strip():
            return _strip_bearer(v)
    if file_rt:
        return file_rt
    return None


def _is_path_allowlisted(path: str) -> bool:
    """Check if path is in the allowlisted candidates (prevents arbitrary overwrite)."""
    try:
        rp = Path(path).resolve()
        # Block system directories even if operator sets PIM_TOKEN_FILE maliciously
        blocked_prefixes = (Path("/etc"), Path("/bin"), Path("/sbin"), Path("/usr"), Path("/root"), Path("/var"))
        for bp in blocked_prefixes:
            try:
                if rp.is_relative_to(bp):
                    # Allow only if explicitly under /var/tmp or /tmp for tests? No, block.
                    return False
            except Exception:
                # Python <3.9 fallback
                if str(rp).startswith(str(bp) + "/"):
                    return False
        candidates = _get_token_file_candidates()
        rps = str(rp)
        for c in candidates:
            try:
                rc = str(Path(c).resolve())
                if rps == rc:
                    return True
            except Exception:
                continue
        default_token = str(Path(os.path.join(os.path.dirname(__file__), ".token")).resolve())
        if rps == default_token:
            return True
        return False
    except Exception:
        return False


def _atomic_write_text(path: Path, content: str) -> None:
    """Atomic write via temp file + rename, with 0o600 permissions."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    # Ensure parent exists with 0o700
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        try:
            os.chmod(path.parent, 0o700)
        except Exception:
            pass
    except Exception:
        pass
    tmp.write_text(content, encoding="utf-8")
    try:
        os.chmod(tmp, 0o600)
    except Exception:
        pass
    # fsync temp file
    try:
        with open(tmp, "rb") as f:
            try:
                os.fsync(f.fileno())
            except Exception:
                pass
    except Exception:
        pass
    tmp.replace(path)
    try:
        os.chmod(path, 0o600)
    except Exception:
        pass


def _persist_refreshed_tokens(access_token: str, refresh_token: Optional[str]) -> None:
    """Persist refreshed tokens to file and env cache. Best-effort, never raises."""
    try:
        # Update env for current process so subsequent _get_token sees fresh value
        os.environ["PIM_ACCESS_TOKEN"] = access_token
        if refresh_token:
            os.environ["PIM_REFRESH_TOKEN"] = refresh_token
        with _token_file_lock:
            _token_file_cache["access_token"] = access_token
            if refresh_token:
                _token_file_cache["refresh_token"] = refresh_token
        # Try to write back to the token file that was previously used, or the first candidate
        candidates = _get_token_file_candidates()
        target: Optional[str] = None
        with _token_file_lock:
            target = _token_file_cache.get("path")
        if not target:
            target = candidates[0] if candidates else None
        if not target:
            return
        if not _is_path_allowlisted(target):
            return
        p = Path(target)
        explicit = os.getenv("PIM_TOKEN_FILE") or os.getenv("PIM_TOKEN_PATH")
        should_write = p.is_file() or (explicit and target == explicit.strip())
        if not should_write and target.endswith(".token"):
            should_write = True
        if not should_write:
            return
        # Preserve format: if existing file was JSON, write JSON; else plain
        try:
            if p.is_file():
                try:
                    if p.stat().st_size > 32 * 1024:
                        return
                    raw = p.read_text(encoding="utf-8").strip()
                except Exception:
                    raw = ""
                if raw.lstrip().startswith("{"):
                    try:
                        data = json.loads(raw.lstrip())
                        if isinstance(data, dict):
                            if "accessToken" in data:
                                data["accessToken"] = access_token
                            elif "access_token" in data:
                                data["access_token"] = access_token
                            elif "token" in data:
                                data["token"] = access_token
                            else:
                                data["accessToken"] = access_token
                            if refresh_token:
                                if "refreshToken" in data:
                                    data["refreshToken"] = refresh_token
                                elif "refresh_token" in data:
                                    data["refresh_token"] = refresh_token
                                else:
                                    data["refreshToken"] = refresh_token
                            exp = _decode_jwt_exp(access_token)
                            if exp and "expiresAt" in data:
                                data["expiresAt"] = datetime.fromtimestamp(exp, tz=timezone.utc).isoformat()
                            content = json.dumps(data, ensure_ascii=False, indent=2)
                            _atomic_write_text(p, content + "\n")
                            with _token_file_lock:
                                try:
                                    st = p.stat()
                                    _token_file_cache["mtime"] = st.st_mtime
                                    _token_file_cache["size"] = st.st_size
                                except Exception:
                                    pass
                            return
                    except Exception:
                        pass
                else:
                    # Detect prior two-line format to preserve refresh
                    try:
                        lines = [ln.strip() for ln in raw.splitlines() if ln.strip()]
                        was_two_line = len(lines) >= 2
                    except Exception:
                        was_two_line = False
                    if was_two_line and refresh_token:
                        content = access_token + "\n" + refresh_token + "\n"
                    elif refresh_token and p.suffix == ".token":
                        # For .token plain, also persist refresh as second line for restart resilience
                        content = access_token + "\n" + refresh_token + "\n"
                    else:
                        content = access_token + "\n"
                    _atomic_write_text(p, content)
                    with _token_file_lock:
                        try:
                            st = p.stat()
                            _token_file_cache["mtime"] = st.st_mtime
                            _token_file_cache["size"] = st.st_size
                            _token_file_cache["path"] = str(p)
                        except Exception:
                            pass
                    return
            # New file (explicit path or .token): write plain or json based on extension
            if p.suffix == ".json":
                data: Dict[str, Any] = {"accessToken": access_token}
                if refresh_token:
                    data["refreshToken"] = refresh_token
                exp = _decode_jwt_exp(access_token)
                if exp:
                    data["expiresAt"] = datetime.fromtimestamp(exp, tz=timezone.utc).isoformat()
                _atomic_write_text(p, json.dumps(data, ensure_ascii=False, indent=2) + "\n")
            else:
                if refresh_token:
                    content = access_token + "\n" + refresh_token + "\n"
                else:
                    content = access_token + "\n"
                _atomic_write_text(p, content)
            with _token_file_lock:
                try:
                    st = p.stat()
                    _token_file_cache["mtime"] = st.st_mtime
                    _token_file_cache["size"] = st.st_size
                    _token_file_cache["path"] = str(p)
                except Exception:
                    pass
        except Exception:
            pass
    except Exception:
        pass


async def _refresh_access_token(refresh_token: str) -> Optional[Tuple[str, Optional[str]]]:
    """Call POST /api/v1/auth/refresh. Returns (new_access, new_refresh) or None."""
    # Use raw PIM_API_URL, no auth header needed (refresh token is in body)
    url = f"{PIM_API_URL}/api/v1/auth/refresh"
    try:
        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(url, json={"refreshToken": refresh_token})
            if resp.status_code >= 400:
                return None
            try:
                data = resp.json()
            except Exception:
                return None
            # ApiResponse shape: {code:0, data:{accessToken, refreshToken, expiresAt}}
            payload = data.get("data") if isinstance(data, dict) else None
            if not payload and isinstance(data, dict) and "accessToken" in data:
                payload = data
            if not isinstance(payload, dict):
                return None
            new_at = payload.get("accessToken") or payload.get("access_token") or payload.get("token")
            new_rt = payload.get("refreshToken") or payload.get("refresh_token")
            if not new_at or not isinstance(new_at, str):
                return None
            new_at = new_at.strip()
            if new_at.lower().startswith("bearer "):
                new_at = new_at[7:].strip()
            if isinstance(new_rt, str):
                new_rt = new_rt.strip()
                if new_rt.lower().startswith("bearer "):
                    new_rt = new_rt[7:].strip()
            else:
                new_rt = None
            return new_at, new_rt
    except Exception:
        return None


def _get_token() -> Optional[str]:
    # HTTP mode: use the JWT issued by /verify for the current request (set by the tool wrapper).
    identity = _current_identity.get()
    if identity:
        at = identity.get("accessToken")
        if isinstance(at, str) and at:
            return at
    # HTTP mode must never fall back to env/file tokens: those are user JWTs that bypass
    # the per-client permission model. Missing identity -> caller gets a 401.
    if _http_mode():
        return None

    # Env fallback for stdio transport (with expiry-aware file reload)
    # 1) Collect env token if present
    env_token: Optional[str] = None
    for env_name in ("PIM_ACCESS_TOKEN", "PIM_TOKEN", "MCP_BEARER_TOKEN", "BEARER_TOKEN", "PIM_JWT"):
        v = os.getenv(env_name)
        if v and v.strip():
            vv = v.strip()
            if vv.lower().startswith("bearer "):
                vv = vv[7:].strip()
            env_token = vv
            break

    # 2) Try file-backed token (checks mtime, supports plain JWT or JSON)
    file_token, _file_refresh = _try_load_token_from_file()

    # Decision: prefer non-expired token; if both expired, prefer file (likely fresher via external writer)
    def _valid(tok: Optional[str]) -> bool:
        return tok is not None and not _is_token_expired(tok)

    if env_token and _valid(env_token):
        return env_token
    if file_token and _valid(file_token):
        return file_token
    # If one is non-expired but the other is expired/missing, we already returned the good one.
    # If both are expired but one exists, return file first (external writer likely updated it), else env.
    if file_token:
        return file_token
    if env_token:
        return env_token
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
    _retry_on_401: bool = True,
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
                # 401 retry with refresh token (for stdio long-lived process)
                if resp.status_code == 401 and _retry_on_401:
                    # Serialize refresh to avoid thundering herd; other 401s will wait and then reuse the new token.
                    async with _refresh_lock:
                        # Another coroutine may have already refreshed while we waited for the lock
                        current = _get_token()
                        if current and current != token and not _is_token_expired(current):
                            return await _call_api(method, path, params, json_body, redact_urls, _retry_on_401=False)
                        # Re-check file in case external writer updated token while we were in-flight
                        fresh_file_token, _ = _try_load_token_from_file()
                        if fresh_file_token and fresh_file_token != token and not _is_token_expired(fresh_file_token):
                            return await _call_api(method, path, params, json_body, redact_urls, _retry_on_401=False)
                        refresh_tok = _get_refresh_token()
                        if refresh_tok:
                            refreshed = await _refresh_access_token(refresh_tok)
                            if refreshed:
                                new_at, new_rt = refreshed
                                _persist_refreshed_tokens(new_at, new_rt)
                                return await _call_api(method, path, params, json_body, redact_urls, _retry_on_401=False)
                        # Fallback: if fresh file appeared after refresh attempt, try once more with it
                        fresh2, _ = _try_load_token_from_file()
                        if fresh2 and fresh2 != token and not _is_token_expired(fresh2):
                            return await _call_api(method, path, params, json_body, redact_urls, _retry_on_401=False)
                # pass through ApiResponse error
                if isinstance(data, dict) and "error" not in data:
                    return {"error": f"HTTP {resp.status_code}: {resp.text[:800]}", "details": data, "code": resp.status_code}
                if isinstance(data, dict):
                    # ensure code field
                    if "code" not in data:
                        data["code"] = resp.status_code
                    # Add hint for 401 to guide agent to re-login
                    if resp.status_code == 401 and "missing bearer" not in str(data.get("error", "")).lower():
                        hint = " token expired or invalid; re-login via POST /api/v1/auth/login or set PIM_REFRESH_TOKEN for auto-refresh"
                        if isinstance(data.get("error"), str) and hint.lower() not in data["error"].lower():
                            data = dict(data)
                            data["error"] = data["error"] + hint
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


async def _call_api_multipart(
    path: str,
    file_field: str,
    file_name: Optional[str],
    file_content: bytes,
    form_fields: Optional[Dict[str, Any]] = None,
    params: Optional[Dict[str, Any]] = None,
    _retry_on_401: bool = True,
) -> Any:
    """Multipart/form-data upload (import_ics, upload_file, upload_quick_note_attachment)."""
    # Note: the 401 auto-refresh branch below is stdio-only. In HTTP mode _get_token()
    # never returns an env/file token, so the refresh path is effectively skipped.
    token = _get_token()
    if not token:
        return {
            "error": "missing bearer token: call MCP with Authorization: Bearer <PIM JWT>. Obtain token via POST /api/v1/auth/login {\"username\",\"password\"} -> accessToken",
            "code": 401,
        }
    headers = {"Authorization": f"Bearer {token}"}
    if not path.startswith("/"):
        path = "/" + path
    if not path.startswith("/api/"):
        if path.startswith("/v1/"):
            path = "/api" + path
        else:
            path = "/api/v1" + path
    url = f"{PIM_API_URL}{path}"
    clean_params: Optional[Dict[str, Any]] = None
    if params:
        clean_params = {k: v for k, v in params.items() if v is not None}
        if not clean_params:
            clean_params = None
    files: Dict[str, Tuple[Optional[str], bytes]] = {file_field: (file_name, file_content)}
    data_fields: Dict[str, Any] = {k: v for k, v in (form_fields or {}).items() if v is not None}
    try:
        async with httpx.AsyncClient(timeout=60) as client:
            resp = await client.post(url, params=clean_params, files=files, data=data_fields, headers=headers)
            try:
                data = resp.json()
            except Exception:
                data = {"raw": resp.text[:800], "status": resp.status_code}
            if resp.status_code >= 400:
                if resp.status_code == 401 and _retry_on_401:
                    async with _refresh_lock:
                        current = _get_token()
                        if current and current != token and not _is_token_expired(current):
                            return await _call_api_multipart(path, file_field, file_name, file_content, form_fields, params, _retry_on_401=False)
                        fresh_file_token, _ = _try_load_token_from_file()
                        if fresh_file_token and fresh_file_token != token and not _is_token_expired(fresh_file_token):
                            return await _call_api_multipart(path, file_field, file_name, file_content, form_fields, params, _retry_on_401=False)
                        refresh_tok = _get_refresh_token()
                        if refresh_tok:
                            refreshed = await _refresh_access_token(refresh_tok)
                            if refreshed:
                                new_at, new_rt = refreshed
                                _persist_refreshed_tokens(new_at, new_rt)
                                return await _call_api_multipart(path, file_field, file_name, file_content, form_fields, params, _retry_on_401=False)
                if isinstance(data, dict) and "error" not in data:
                    return {"error": f"HTTP {resp.status_code}: {resp.text[:800]}", "details": data, "code": resp.status_code}
                if isinstance(data, dict):
                    if "code" not in data:
                        data["code"] = resp.status_code
                    return data
                return {"error": f"HTTP {resp.status_code}: {resp.text[:800]}", "details": data, "code": resp.status_code}
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


# ===================== Calendar Writes (30) =====================
# Phase 3 write tools. Each requires the `write` permission bit for its tool name
# in the client's permission set (enforced by the HTTP wrapper via /verify).

def _b64_to_bytes(value: str, name: str) -> Any:
    """Decode base64 string. Returns bytes or an error dict."""
    try:
        return base64.b64decode(value, validate=True)
    except Exception:
        return {"error": f"{name} must be valid base64", "code": 400}


@mcp.tool()
async def create_event(
    calendarId: str,
    title: str,
    dtStart: str,
    dtEnd: str,
    description: Optional[str] = None,
    location: Optional[str] = None,
    rRule: Optional[str] = None,
    uid: Optional[str] = None,
    isAllDay: bool = False,
    timeZoneId: Optional[str] = None,
    showAs: Optional[str] = None,
    importance: Optional[str] = None,
    sensitivity: Optional[str] = None,
    categories: Optional[List[str]] = None,
    isReminderOn: bool = False,
    reminderMinutesBeforeStart: Optional[int] = None,
    organizer: Optional[Dict[str, Any]] = None,
    attendees: Optional[List[Dict[str, Any]]] = None,
    isOnlineMeeting: bool = False,
    onlineMeetingProvider: Optional[str] = None,
    onlineMeetingUrl: Optional[str] = None,
    externalLink: Optional[str] = None,
) -> Any:
    """Create a calendar event. Requires write permission create_event. Returns the created EventResponse."""
    for f in ("calendarId", "title", "dtStart", "dtEnd"):
        if not locals().get(f):
            return {"error": f"{f} is required", "code": 400}
    body = _clean_params(
        calendarId=calendarId, title=title, dtStart=dtStart, dtEnd=dtEnd,
        description=description, location=location, rRule=rRule, uid=uid,
        isAllDay=isAllDay, timeZoneId=timeZoneId, showAs=showAs,
        importance=importance, sensitivity=sensitivity, categories=categories,
        isReminderOn=isReminderOn, reminderMinutesBeforeStart=reminderMinutesBeforeStart,
        organizer=organizer, attendees=attendees, isOnlineMeeting=isOnlineMeeting,
        onlineMeetingProvider=onlineMeetingProvider, onlineMeetingUrl=onlineMeetingUrl,
        externalLink=externalLink)
    return await _call_api("POST", "/api/v1/calendar/events", json_body=body)


@mcp.tool()
async def update_event(
    eventId: str,
    calendarId: str,
    title: str,
    dtStart: str,
    dtEnd: str,
    scope: Optional[str] = None,
    recurrenceId: Optional[str] = None,
    originalEventId: Optional[str] = None,
    description: Optional[str] = None,
    location: Optional[str] = None,
    rRule: Optional[str] = None,
    isAllDay: Optional[bool] = None,
    timeZoneId: Optional[str] = None,
    showAs: Optional[str] = None,
    importance: Optional[str] = None,
    sensitivity: Optional[str] = None,
    categories: Optional[List[str]] = None,
    isReminderOn: Optional[bool] = None,
    reminderMinutesBeforeStart: Optional[int] = None,
) -> Any:
    """Update a calendar event. scope=This/Series with recurrenceId updates repeating events. Requires write permission update_event."""
    if not eventId:
        return {"error": "eventId is required", "code": 400}
    params = _clean_params(scope=scope, recurrenceId=recurrenceId, originalEventId=originalEventId)
    body = _clean_params(
        calendarId=calendarId, title=title, dtStart=dtStart, dtEnd=dtEnd,
        description=description, location=location, rRule=rRule, isAllDay=isAllDay,
        timeZoneId=timeZoneId, showAs=showAs, importance=importance,
        sensitivity=sensitivity, categories=categories, isReminderOn=isReminderOn,
        reminderMinutesBeforeStart=reminderMinutesBeforeStart)
    return await _call_api("PUT", f"/api/v1/calendar/events/{eventId}", params=params, json_body=body)


@mcp.tool()
async def delete_event(
    eventId: str,
    scope: Optional[str] = None,
    recurrenceId: Optional[str] = None,
    originalEventId: Optional[str] = None,
) -> Any:
    """Delete a calendar event (moves to recycle bin). scope=This/Series for repeating events. Requires write permission delete_event."""
    if not eventId:
        return {"error": "eventId is required", "code": 400}
    params = _clean_params(scope=scope, recurrenceId=recurrenceId, originalEventId=originalEventId)
    return await _call_api("DELETE", f"/api/v1/calendar/events/{eventId}", params=params)


@mcp.tool()
async def restore_event(eventId: str, restoreAsCopy: bool = False) -> Any:
    """Restore a deleted event from the recycle bin. Requires write permission restore_event."""
    if not eventId:
        return {"error": "eventId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/calendar/events/{eventId}/restore", json_body={"restoreAsCopy": restoreAsCopy})


@mcp.tool()
async def batch_delete_events(ids: List[str]) -> Any:
    """Batch delete events by ids (move to recycle bin). Requires write permission batch_delete_events."""
    if not ids:
        return {"error": "ids is required", "code": 400}
    return await _call_api("POST", "/api/v1/calendar/events/batch-delete", json_body={"ids": ids})


@mcp.tool()
async def create_task(
    title: str,
    priority: Optional[int] = None,
    calendarId: Optional[str] = None,
    description: Optional[str] = None,
    estimatedDuration: Optional[str] = None,
    minimumSegment: Optional[str] = None,
    due: Optional[str] = None,
    dtStart: Optional[str] = None,
    status: Optional[str] = None,
    plannedEnd: Optional[str] = None,
) -> Any:
    """Create a task. estimatedDuration/minimumSegment use formats like '1h30m'. Requires write permission create_task."""
    if not title:
        return {"error": "title is required", "code": 400}
    body = _clean_params(
        title=title, priority=priority, calendarId=calendarId, description=description,
        estimatedDuration=estimatedDuration, minimumSegment=minimumSegment, due=due,
        dtStart=dtStart, status=status, plannedEnd=plannedEnd)
    return await _call_api("POST", "/api/v1/calendar/tasks", json_body=body)


@mcp.tool()
async def update_task(
    taskId: str,
    title: Optional[str] = None,
    priority: Optional[int] = None,
    calendarId: Optional[str] = None,
    description: Optional[str] = None,
    estimatedDuration: Optional[str] = None,
    minimumSegment: Optional[str] = None,
    due: Optional[str] = None,
    dtStart: Optional[str] = None,
    status: Optional[str] = None,
    plannedEnd: Optional[str] = None,
) -> Any:
    """Update a task (status/priority/due dates). Requires write permission update_task."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    body = _clean_params(
        title=title, priority=priority, calendarId=calendarId, description=description,
        estimatedDuration=estimatedDuration, minimumSegment=minimumSegment, due=due,
        dtStart=dtStart, status=status, plannedEnd=plannedEnd)
    return await _call_api("PUT", f"/api/v1/calendar/tasks/{taskId}", json_body=body)


@mcp.tool()
async def delete_task(taskId: str) -> Any:
    """Delete a task (moves to recycle bin). Requires write permission delete_task."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/calendar/tasks/{taskId}")


@mcp.tool()
async def restore_task(taskId: str, restoreAsCopy: bool = False) -> Any:
    """Restore a deleted task from the recycle bin. Requires write permission restore_task."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/calendar/tasks/{taskId}/restore", json_body={"restoreAsCopy": restoreAsCopy})


@mcp.tool()
async def move_task(
    taskId: str,
    scheduledStart: Optional[str] = None,
    duration: Optional[str] = None,
    newSortOrder: Optional[int] = None,
    plannedEnd: Optional[str] = None,
) -> Any:
    """Move a task (change project or ordering). duration is a TimeSpan string like '01:30:00'. Requires write permission move_task."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    body = _clean_params(scheduledStart=scheduledStart, duration=duration, newSortOrder=newSortOrder, plannedEnd=plannedEnd)
    return await _call_api("POST", f"/api/v1/calendar/tasks/{taskId}/move", json_body=body)


@mcp.tool()
async def plan_task(
    taskId: str,
    plannedStart: str,
    plannedEnd: Optional[str] = None,
    estimatedDuration: Optional[str] = None,
) -> Any:
    """Schedule a task onto the calendar (which day it will be done). Requires write permission plan_task."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    if not plannedStart:
        return {"error": "plannedStart is required", "code": 400}
    body = _clean_params(plannedStart=plannedStart, plannedEnd=plannedEnd, estimatedDuration=estimatedDuration)
    return await _call_api("POST", f"/api/v1/calendar/tasks/{taskId}/plan", json_body=body)


@mcp.tool()
async def create_task_segment(
    taskId: str,
    startsAt: str,
    endsAt: str,
    status: str,
    source: str,
    planningReason: Optional[str] = None,
) -> Any:
    """Add an execution segment (a concrete time block) to a task. Requires write permission create_task_segment."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    body = _clean_params(startsAt=startsAt, endsAt=endsAt, status=status, source=source, planningReason=planningReason)
    if "startsAt" not in body or "endsAt" not in body or "status" not in body or "source" not in body:
        return {"error": "startsAt, endsAt, status and source are required", "code": 400}
    return await _call_api("POST", f"/api/v1/calendar/tasks/{taskId}/segments", json_body=body)


@mcp.tool()
async def delete_task_segment(taskId: str, segmentId: str) -> Any:
    """Delete a task execution segment. Requires write permission delete_task_segment."""
    if not taskId or not segmentId:
        return {"error": "taskId and segmentId are required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/calendar/tasks/{taskId}/segments/{segmentId}")


@mcp.tool()
async def add_task_checklist_item(taskId: str, title: str, sortOrder: Optional[int] = None) -> Any:
    """Add a checklist item to a task. Requires write permission add_task_checklist_item."""
    if not taskId:
        return {"error": "taskId is required", "code": 400}
    if not title:
        return {"error": "title is required", "code": 400}
    body = _clean_params(title=title, sortOrder=sortOrder)
    return await _call_api("POST", f"/api/v1/calendar/tasks/{taskId}/checklist", json_body=body)


@mcp.tool()
async def batch_delete_tasks(ids: List[str]) -> Any:
    """Batch delete tasks by ids (move to recycle bin). Requires write permission batch_delete_tasks."""
    if not ids:
        return {"error": "ids is required", "code": 400}
    return await _call_api("POST", "/api/v1/calendar/tasks/batch-delete", json_body={"ids": ids})


@mcp.tool()
async def batch_update_tasks(
    ids: List[str],
    status: Optional[str] = None,
    priority: Optional[int] = None,
    calendarId: Optional[str] = None,
) -> Any:
    """Batch update tasks (status/priority/calendar). At least one of status/priority/calendarId is required. Requires write permission batch_update_tasks."""
    if not ids:
        return {"error": "ids is required", "code": 400}
    body = _clean_params(ids=ids, status=status, priority=priority, calendarId=calendarId)
    if len(body) == 1:
        return {"error": "at least one of status/priority/calendarId is required", "code": 400}
    return await _call_api("POST", "/api/v1/calendar/tasks/batch-update", json_body=body)


@mcp.tool()
async def create_task_book(
    name: str,
    domainProjectId: Optional[str] = None,
    kind: Optional[str] = None,
    status: Optional[str] = None,
) -> Any:
    """Create a task book (a container for tasks). Requires write permission create_task_book."""
    if not name:
        return {"error": "name is required", "code": 400}
    body = _clean_params(name=name, domainProjectId=domainProjectId, kind=kind, status=status)
    return await _call_api("POST", "/api/v1/calendar/task-books", json_body=body)


@mcp.tool()
async def create_project(name: str, description: Optional[str] = None, status: Optional[str] = None) -> Any:
    """Create a domain project (a top-level domain for tasks). Requires write permission create_project."""
    if not name:
        return {"error": "name is required", "code": 400}
    body = _clean_params(name=name, description=description, status=status)
    return await _call_api("POST", "/api/v1/calendar/projects", json_body=body)


@mcp.tool()
async def schedule_tasks(taskIds: List[str]) -> Any:
    """Run the scheduling engine to place tasks onto the calendar. Requires write permission schedule_tasks."""
    if not taskIds:
        return {"error": "taskIds is required", "code": 400}
    return await _call_api("POST", "/api/v1/calendar/schedule", json_body={"taskIds": taskIds})


@mcp.tool()
async def create_reminder(
    relatedObjectType: str,
    relatedObjectId: str,
    title: str,
    scheduledAt: str,
    body: Optional[str] = None,
    triggerReason: Optional[str] = None,
    riskLevel: Optional[str] = None,
    channels: Optional[List[str]] = None,
    doNotDisturbStart: Optional[str] = None,
    doNotDisturbEnd: Optional[str] = None,
) -> Any:
    """Create a reminder for a related object (e.g. a task). Requires write permission create_reminder."""
    for f in ("relatedObjectType", "relatedObjectId", "title", "scheduledAt"):
        if not locals().get(f):
            return {"error": f"{f} is required", "code": 400}
    payload = _clean_params(
        relatedObjectType=relatedObjectType, relatedObjectId=relatedObjectId,
        title=title, scheduledAt=scheduledAt, body=body, triggerReason=triggerReason,
        riskLevel=riskLevel, channels=channels,
        doNotDisturbStart=doNotDisturbStart, doNotDisturbEnd=doNotDisturbEnd)
    return await _call_api("POST", "/api/v1/calendar/reminders", json_body=payload)


@mcp.tool()
async def snooze_reminder(reminderId: str, scheduledAt: Optional[str] = None) -> Any:
    """Snooze a reminder (defaults to +15 minutes). Requires write permission snooze_reminder."""
    if not reminderId:
        return {"error": "reminderId is required", "code": 400}
    params = _clean_params(scheduledAt=scheduledAt)
    return await _call_api("POST", f"/api/v1/calendar/reminders/{reminderId}/snooze", params=params)


@mcp.tool()
async def dismiss_reminder(reminderId: str) -> Any:
    """Dismiss/close a reminder. Requires write permission dismiss_reminder."""
    if not reminderId:
        return {"error": "reminderId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/calendar/reminders/{reminderId}/dismiss")


@mcp.tool()
async def create_habit(
    title: str,
    description: Optional[str] = None,
    cadence: Optional[str] = None,
    source: Optional[str] = None,
    status: Optional[str] = None,
    ruleJson: Optional[str] = None,
) -> Any:
    """Create a habit routine. Requires write permission create_habit."""
    if not title:
        return {"error": "title is required", "code": 400}
    body = _clean_params(title=title, description=description, cadence=cadence, source=source, status=status, ruleJson=ruleJson)
    return await _call_api("POST", "/api/v1/calendar/habits", json_body=body)


@mcp.tool()
async def create_habit_occurrence(
    habitId: str,
    startsAt: str,
    endsAt: str,
    status: Optional[str] = None,
    source: Optional[str] = None,
) -> Any:
    """Log a habit occurrence (check-in). Requires write permission create_habit_occurrence."""
    if not habitId:
        return {"error": "habitId is required", "code": 400}
    if not startsAt or not endsAt:
        return {"error": "startsAt and endsAt are required", "code": 400}
    body = _clean_params(startsAt=startsAt, endsAt=endsAt, status=status, source=source)
    return await _call_api("POST", f"/api/v1/calendar/habits/{habitId}/occurrences", json_body=body)


@mcp.tool()
async def create_availability_window(
    title: str,
    startsAt: str,
    endsAt: str,
    kind: Optional[str] = None,
    source: Optional[str] = None,
) -> Any:
    """Create an availability window (a block of free time usable for scheduling). Requires write permission create_availability_window."""
    for f in ("title", "startsAt", "endsAt"):
        if not locals().get(f):
            return {"error": f"{f} is required", "code": 400}
    body = _clean_params(title=title, startsAt=startsAt, endsAt=endsAt, kind=kind, source=source)
    return await _call_api("POST", "/api/v1/calendar/availability", json_body=body)


@mcp.tool()
async def import_ics(icsContent: str, calendarId: Optional[str] = None) -> Any:
    """Import an ICS calendar file (pass the raw ICS text). Requires write permission import_ics."""
    if not icsContent:
        return {"error": "icsContent is required", "code": 400}
    fields = {"calendarId": calendarId} if calendarId else None
    return await _call_api_multipart(
        "/api/v1/calendar/import-ics",
        file_field="file",
        file_name="import.ics",
        file_content=icsContent.encode("utf-8"),
        form_fields=fields)


@mcp.tool()
async def create_calendar(name: str, color: Optional[str] = None, kind: Optional[str] = None) -> Any:
    """Create a calendar. Requires write permission create_calendar."""
    if not name:
        return {"error": "name is required", "code": 400}
    body = _clean_params(name=name, color=color, kind=kind)
    return await _call_api("POST", "/api/v1/calendar/calendars", json_body=body)


@mcp.tool()
async def update_calendar(calendarId: str, name: str, color: Optional[str] = None, kind: Optional[str] = None) -> Any:
    """Update a calendar. Requires write permission update_calendar."""
    if not calendarId:
        return {"error": "calendarId is required", "code": 400}
    if not name:
        return {"error": "name is required", "code": 400}
    body = _clean_params(name=name, color=color, kind=kind)
    return await _call_api("PUT", f"/api/v1/calendar/calendars/{calendarId}", json_body=body)


@mcp.tool()
async def delete_calendar(calendarId: str) -> Any:
    """Delete a calendar (recycle bin). Requires write permission delete_calendar."""
    if not calendarId:
        return {"error": "calendarId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/calendar/calendars/{calendarId}")


@mcp.tool()
async def restore_calendar(calendarId: str) -> Any:
    """Restore a deleted calendar from the recycle bin. Requires write permission restore_calendar."""
    if not calendarId:
        return {"error": "calendarId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/calendar/calendars/{calendarId}/restore")


# ===================== QuickNotes Writes (8) =====================

@mcp.tool()
async def create_quick_note(
    contentMarkdown: str,
    source: Optional[str] = None,
    attachmentIds: Optional[List[str]] = None,
) -> Any:
    """Create a quick note. Requires write permission create_quick_note."""
    if not contentMarkdown or not contentMarkdown.strip():
        return {"error": "contentMarkdown is required", "code": 400}
    body = _clean_params(contentMarkdown=contentMarkdown, source=source, attachmentIds=attachmentIds)
    return await _call_api("POST", "/api/v1/quick-notes", json_body=body)


@mcp.tool()
async def update_quick_note(
    noteId: str,
    contentMarkdown: Optional[str] = None,
    status: Optional[str] = None,
    attachmentIds: Optional[List[str]] = None,
) -> Any:
    """Update a quick note (content/status/attachments). Requires write permission update_quick_note."""
    if not noteId:
        return {"error": "noteId is required", "code": 400}
    body = _clean_params(contentMarkdown=contentMarkdown, status=status, attachmentIds=attachmentIds)
    return await _call_api("PUT", f"/api/v1/quick-notes/{noteId}", json_body=body)


@mcp.tool()
async def delete_quick_note(noteId: str) -> Any:
    """Delete a quick note. Requires write permission delete_quick_note."""
    if not noteId:
        return {"error": "noteId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/quick-notes/{noteId}")


@mcp.tool()
async def archive_quick_note(noteId: str) -> Any:
    """Archive a quick note. Requires write permission archive_quick_note."""
    if not noteId:
        return {"error": "noteId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/quick-notes/{noteId}/archive")


@mcp.tool()
async def restore_quick_note(noteId: str, status: Optional[str] = None) -> Any:
    """Restore an archived quick note (default status 'inbox'). Requires write permission restore_quick_note."""
    if not noteId:
        return {"error": "noteId is required", "code": 400}
    body = _clean_params(status=status)
    return await _call_api("POST", f"/api/v1/quick-notes/{noteId}/restore", json_body=body)


@mcp.tool()
async def process_quick_note(noteId: str) -> Any:
    """Process a quick note through AI/rules (sets status to processed). Requires write permission process_quick_note."""
    if not noteId:
        return {"error": "noteId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/quick-notes/{noteId}/process")


@mcp.tool()
async def upload_quick_note_attachment(fileContentBase64: str, fileName: str) -> Any:
    """Upload an attachment for a quick note (base64 content). Returns attachment id to reference in create/update note. Requires write permission upload_quick_note_attachment."""
    if not fileContentBase64 or not fileName:
        return {"error": "fileContentBase64 and fileName are required", "code": 400}
    content = _b64_to_bytes(fileContentBase64, "fileContentBase64")
    if isinstance(content, dict):
        return content
    return await _call_api_multipart(
        "/api/v1/quick-notes/attachments",
        file_field="file",
        file_name=fileName,
        file_content=content)


@mcp.tool()
async def delete_quick_note_attachment(attachmentId: str) -> Any:
    """Delete a quick note attachment. Requires write permission delete_quick_note_attachment."""
    if not attachmentId:
        return {"error": "attachmentId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/quick-notes/attachments/{attachmentId}")


# ===================== Files Writes (6) =====================

@mcp.tool()
async def upload_file(providerId: str, path: str, fileContentBase64: str, fileName: str) -> Any:
    """Upload a file to a provider path (base64 content). Requires write permission upload_file."""
    for f in ("providerId", "path", "fileContentBase64", "fileName"):
        if not locals().get(f):
            return {"error": f"{f} is required", "code": 400}
    content = _b64_to_bytes(fileContentBase64, "fileContentBase64")
    if isinstance(content, dict):
        return content
    return await _call_api_multipart(
        "/api/v1/files/items/upload",
        file_field="file",
        file_name=fileName,
        file_content=content,
        form_fields={"providerId": providerId, "path": path})


@mcp.tool()
async def move_file(fileId: str, destinationPath: str) -> Any:
    """Move a file to a new path. Requires write permission move_file."""
    if not fileId:
        return {"error": "fileId is required", "code": 400}
    if not destinationPath:
        return {"error": "destinationPath is required", "code": 400}
    return await _call_api("POST", f"/api/v1/files/items/{fileId}/move", json_body={"destinationPath": destinationPath})


@mcp.tool()
async def rename_file(fileId: str, name: str) -> Any:
    """Rename a file. Requires write permission rename_file."""
    if not fileId:
        return {"error": "fileId is required", "code": 400}
    if not name:
        return {"error": "name is required", "code": 400}
    return await _call_api("POST", f"/api/v1/files/items/{fileId}/rename", json_body={"name": name})


@mcp.tool()
async def delete_file(fileId: str) -> Any:
    """Delete a file (moves to trash). Requires write permission delete_file."""
    if not fileId:
        return {"error": "fileId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/files/items/{fileId}")


@mcp.tool()
async def restore_file(fileId: str, trashId: str) -> Any:
    """Restore a file from trash. trashId identifies the trash entry. Requires write permission restore_file."""
    if not fileId:
        return {"error": "fileId is required", "code": 400}
    if not trashId:
        return {"error": "trashId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/files/trash/{fileId}/restore", params={"trashId": trashId})


@mcp.tool()
async def index_file(fileId: str) -> Any:
    """Trigger file indexing for RAG search. Requires write permission index_file."""
    if not fileId:
        return {"error": "fileId is required", "code": 400}
    return await _call_api("POST", f"/api/v1/files/items/{fileId}/index")


# ===================== PcTracker Writes (4) =====================

@mcp.tool()
async def create_category(
    appPattern: str,
    categoryName: str,
    color: str,
    priority: Optional[int] = None,
) -> Any:
    """Create a PC activity category (maps an app pattern to a category). Requires write permission create_category."""
    for f in ("appPattern", "categoryName", "color"):
        if not locals().get(f):
            return {"error": f"{f} is required", "code": 400}
    if priority is None:
        return {"error": "priority is required", "code": 400}
    body = _clean_params(appPattern=appPattern, categoryName=categoryName, color=color, priority=priority)
    return await _call_api("POST", "/api/v1/pc/categories", json_body=body)


@mcp.tool()
async def update_categories_order(items: List[Dict[str, Any]]) -> Any:
    """Reorder PC categories. items: [{id, parentId?, sortOrder}]. Requires write permission update_categories_order."""
    if not items:
        return {"error": "items is required", "code": 400}
    return await _call_api("PUT", "/api/v1/pc/categories/reorder", json_body={"items": items})


@mcp.tool()
async def delete_category(categoryId: str) -> Any:
    """Delete a PC category. Requires write permission delete_category."""
    if not categoryId:
        return {"error": "categoryId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/pc/categories/{categoryId}")


@mcp.tool()
async def seed_categories() -> Any:
    """Seed default PC categories. Requires write permission seed_categories."""
    return await _call_api("POST", "/api/v1/pc/categories/seed")


# ===================== Mobile Writes (2) =====================

@mcp.tool()
async def create_mobile_goal(
    limitSeconds: int,
    scope: Optional[str] = None,
    packageName: Optional[str] = None,
    lifeCategory: Optional[str] = None,
    label: Optional[str] = None,
    isEnabled: bool = True,
) -> Any:
    """Create a mobile usage goal (daily limit). Requires write permission create_mobile_goal."""
    if limitSeconds is None:
        return {"error": "limitSeconds is required", "code": 400}
    body = _clean_params(
        scope=scope, packageName=packageName, lifeCategory=lifeCategory,
        label=label, limitSeconds=limitSeconds, isEnabled=isEnabled)
    return await _call_api("POST", "/api/v1/mobile/analytics/goals", json_body=body)


@mcp.tool()
async def delete_mobile_goal(goalId: str) -> Any:
    """Delete a mobile usage goal. Requires write permission delete_mobile_goal."""
    if not goalId:
        return {"error": "goalId is required", "code": 400}
    return await _call_api("DELETE", f"/api/v1/mobile/analytics/goals/{goalId}")


_register_write_tool_names(
    "create_event", "update_event", "delete_event", "restore_event", "batch_delete_events",
    "create_task", "update_task", "delete_task", "restore_task", "move_task", "plan_task",
    "create_task_segment", "delete_task_segment", "add_task_checklist_item", "batch_delete_tasks",
    "batch_update_tasks", "create_task_book", "create_project", "schedule_tasks",
    "create_reminder", "snooze_reminder", "dismiss_reminder",
    "create_habit", "create_habit_occurrence",
    "create_availability_window", "import_ics", "create_calendar", "update_calendar",
    "delete_calendar", "restore_calendar",
    "create_quick_note", "update_quick_note", "delete_quick_note", "archive_quick_note",
    "restore_quick_note", "process_quick_note", "upload_quick_note_attachment",
    "delete_quick_note_attachment",
    "upload_file", "move_file", "rename_file", "delete_file", "restore_file", "index_file",
    "create_category", "update_categories_order", "delete_category", "seed_categories",
    "create_mobile_goal", "delete_mobile_goal",
)


# ---------- entrypoint ----------
class _RequireBearer:
    """Starlette middleware: in HTTP mode reject any MCP request without an Authorization: Bearer header.

    OPTIONS (CORS preflight) is allowed through. The guard only applies to the configured MCP
    path, leaving room for future non-MCP endpoints. Unauthorized responses are JSON
    `{code: 40101, message: ...}` matching the API error envelope.
    """

    def __init__(self, app: Any) -> None:
        self.app = app

    async def __call__(self, scope: Any, receive: Any, send: Any) -> None:
        if scope["type"] == "http" and scope.get("path", "").startswith(_MCP_PATH):
            method = (scope.get("method") or "").upper()
            if method != "OPTIONS":
                headers = {
                    k.decode("latin-1").lower(): v.decode("latin-1")
                    for k, v in scope.get("headers", [])
                }
                if not headers.get("authorization", "").strip().lower().startswith("bearer "):
                    from starlette.responses import JSONResponse

                    response = JSONResponse(
                        {"code": 40101, "message": "missing bearer token", "data": None},
                        status_code=401,
                    )
                    await response(scope, receive, send)
                    return
        await self.app(scope, receive, send)


def _build_http_app() -> Any:
    """Starlette app for HTTP mode with a bearer-required guard in front of FastMCP."""
    app = mcp.streamable_http_app()
    return _RequireBearer(app)


def _run_check() -> None:
    """Self-check: verify tool inventory (101 read + 50 write) without starting a server."""
    tools = _list_tools_meta()
    read = [t for t in tools if t["group"] == "read"]
    write = [t for t in tools if t["group"] == "write"]
    names = {t["name"] for t in tools}
    if len(names) != len(tools):
        raise SystemExit(f"--check failed: duplicate tool names ({len(names)} unique / {len(tools)} total)")
    missing = _WRITE_TOOL_NAMES - names
    if missing:
        raise SystemExit(f"--check failed: write tools missing from registration: {sorted(missing)}")
    if len(write) != len(_WRITE_TOOL_NAMES):
        raise SystemExit(f"--check failed: expected {len(_WRITE_TOOL_NAMES)} write tools, got {len(write)}")
    print(f"OK tools read={len(read)} write={len(write)} total={len(tools)}")


def main() -> None:
    if "--check" in os.sys.argv[1:]:
        _run_check()
        return
    if _http_mode():
        import uvicorn

        _wrap_tools_for_http()
        uvicorn.run(_build_http_app(), host=_MCP_HOST, port=_MCP_PORT, log_level="info")
    else:
        mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
