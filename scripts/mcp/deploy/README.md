# PIM MCP Server 生产部署（HTTP / Streamable HTTP）

> ⚠️ **已废弃（2026-09-01）**：MCP Server 已集成进 Pim.Api（.NET 8）进程内，
> `/mcp` Streamable HTTP 端点随 API 直接对外，**不再需要 Python 进程 / systemd 单元**。
> 仅 `openresty-mcp.conf` 仍需使用（`proxy_pass` 已指向 Pim.Api，不再指向 8080）。
> 本 README 保留作历史参考。

## 前置条件

- 仓库 master 已包含 `scripts/mcp/pim_mcp_server.py`（v3，151 工具）。
- 生产机 Python >= 3.10，安装依赖：
  ```bash
  python3 -m pip install mcp httpx uvicorn
  # mcp 1.x 或 2.x 均可（脚本自动适配）；mcp>=2 时 httpx 缺省会自动回退 httpx2
  ```
- 自检（部署前必跑）：
  ```bash
  python3 scripts/mcp/pim_mcp_server.py --check
  # 期望输出: OK tools read=101 write=50 total=151
  ```

## 1. systemd 单元

```bash
sudo cp scripts/mcp/deploy/pim-mcp.service /etc/systemd/system/
# 按实际环境修改 User/Group/WorkingDirectory/ExecStart
sudo systemctl daemon-reload
sudo systemctl enable --now pim-mcp
systemctl status pim-mcp
```

## 2. OpenResty 反代

把 `openresty-mcp.conf` 的 `location = /mcp` 加入生产站点 `pim.conf`（端口 15858）
的 server 块（1panel 场景用 `include proxy/mcp.conf;`）。**不要删掉/覆盖
`proxy/root.conf` 的 SPA 兜底**，`location = /mcp` 精确匹配天然优先于 `location ^~ /`。

```bash
nginx -t && nginx -s reload
```

## 3. 部署后验证

```bash
# 1) 未带 token 的 GET：应 401 JSON（而不是 200 text/html SPA 页面）
curl -k https://home.hsww.party:15858/mcp
# 期望: {"code":40101,"message":"missing bearer token",...}（HTTP 401）

# 2) MCP initialize（带 pim_mcp_* token，WebUI 设置 -> MCP 管理 生成）
curl -k -X POST https://home.hsww.party:15858/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer pim_mcp_<token>" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"verify","version":"1.0"}}}'
# 期望: JSON-RPC 响应（jsonrpc/result/serverInfo），不再是 405

# 3) 工具清单
curl -k -X POST https://home.hsww.party:15858/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Authorization: Bearer pim_mcp_<token>" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
# 期望: 151 个工具
```

## 回滚

- 停掉 MCP server：`sudo systemctl disable --now pim-mcp`。
- 移除反代片段并 reload：`nginx -t && nginx -s reload`（/mcp 将回到 SPA 兜底，
  即修复前行为，不影响 PIM 主站）。