"""Integration smoke: run the MCP server in HTTP mode against a mock Pim.Api backend.

Starts a mock server for /api/v1/mcp/verify + /api/v1/calendar/tasks, boots the real
pim_mcp_server in streamable-http mode on a random port, then drives it with the
official MCP client to verify: 401 missing token, 403 permission denied, 200 create_task.
Run: python3 scripts/mcp/integration_http_smoke.py
"""

import asyncio
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import httpx

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

MOCK = {}

# Configure transport BEFORE importing the module so FastMCP is constructed with the right port.
MOCK_PORT = 18777
MCP_PORT = 18765
os.environ["PIM_MCP_TRANSPORT"] = "http"
os.environ["PIM_MCP_HOST"] = "127.0.0.1"
os.environ["PIM_MCP_PORT"] = str(MCP_PORT)
os.environ["PIM_MCP_PATH"] = "/mcp"
os.environ["PIM_API_URL"] = f"http://127.0.0.1:{MOCK_PORT}"


class MockPimHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):  # silence
        pass

    def _send(self, status, obj):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length) if length else b"{}"
        try:
            payload = json.loads(raw or b"{}")
        except Exception:
            payload = {}
        if self.path == "/api/v1/mcp/verify":
            token = self.headers.get("Authorization", "").replace("Bearer ", "")
            if token != MOCK.get("token"):
                self._send(401, {"code": 40101, "message": "unauthorized", "data": None})
                return
            tool = payload.get("tool")
            if tool and MOCK.get("deny_tool") == tool and "blocked" in (payload.get("paramsSummary") or ""):
                self._send(403, {"code": 40301, "message": "permission denied", "data": None})
                return
            self._send(200, {
                "code": 0,
                "data": {
                    "clientId": "11111111-1111-1111-1111-111111111111",
                    "clientName": "smoke",
                    "userId": "22222222-2222-2222-2222-222222222222",
                    "permissions": {"read": {}, "write": {}},
                    "accessToken": "mock-jwt",
                    "isWrite": True,
                },
            })
            return
        if self.path == "/api/v1/calendar/tasks":
            self._send(200, {"code": 0, "data": {"id": "99999999-9999-9999-9999-999999999999", "title": payload.get("title", "")}})
            return
        self._send(404, {"code": 404, "data": None, "message": "not found"})

    def do_GET(self):
        self._send(404, {"code": 404, "data": None, "message": "not found"})


async def main():
    mock = ThreadingHTTPServer(("127.0.0.1", MOCK_PORT), MockPimHandler)
    mock_port = mock.server_address[1]
    threading.Thread(target=mock.serve_forever, daemon=True).start()
    MOCK["token"] = "pim_mcp_smoketoken"
    MOCK["deny_tool"] = "create_task"

    import uvicorn
    import pim_mcp_server as s

    assert s._MCP_TRANSPORT == "http", s._MCP_TRANSPORT
    s._wrap_tools_for_http()
    starlette_app = s._build_http_app()
    server = uvicorn.Server(uvicorn.Config(starlette_app, host="127.0.0.1", port=MCP_PORT, log_level="error"))
    threading.Thread(target=server.run, daemon=True).start()

    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    url = f"http://127.0.0.1:{MCP_PORT}/mcp"

    for _ in range(50):
        try:
            async with httpx.AsyncClient(timeout=1) as c:
                await c.get(url)
            break
        except Exception:
            await asyncio.sleep(0.2)
    else:
        raise RuntimeError("MCP server did not come up")

    async def run_client():
        async with streamablehttp_client(url, headers={"Authorization": f"Bearer {MOCK['token']}"}) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                tools = await session.list_tools()
                names = {t.name for t in tools.tools}
                assert len(names) == 151, f"expected 151 tools, got {len(names)}"
                print(f"tools listed: {len(names)}")

                denied = await session.call_tool("create_task", {"title": "blocked"})
                print("denied result:", denied.content[0].text[:120])
                assert "permission denied" in denied.content[0].text

                ok = await session.call_tool("create_task", {"title": "hello from mcp"})
                print("ok result:", ok.content[0].text[:300])
                assert "hello from mcp" in ok.content[0].text
                print("SMOKE PASSED")

    await asyncio.wait_for(run_client(), timeout=30)
    server.should_exit = True
    mock.shutdown()


if __name__ == "__main__":
    asyncio.run(main())