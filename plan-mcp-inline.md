# 计划：MCP Server 进程内集成进 Pim.Api（工单 pim-mcp-inline-20260901）

> 目标：MCP Server 集成进 Pim.Api（.NET 8）进程内，`/mcp` 作为 Streamable HTTP 端点，行为与 Python v3 完全等价（151 工具、认证、错误码、审计、脱敏、分页、权限），Python 独立服务退役。验收：Hermes config.yaml 一行不改，151 工具可用；GET /mcp 无 token → 401 JSON；initialize/tools/list 正常；CI 全绿。

## 架构决策

- **协议层**：微软官方 `ModelContextProtocol.AspNetCore` 2.2.0（含 Core 包）。`MapMcp("/mcp")` 提供 Streamable HTTP（GET=SSE / POST=JSON-RPC）；stdio 用 `WithStdioServerTransport`。协议版本协商支持 2025-03-26（已验证 SDK SupportedProtocolVersions 包含）。
- **工具契约**：`mcp-tools.json`（embedded resource）= 从 Python 服务器 dump 的 151 个工具 name/description/inputSchema 原样保存，`tools/list` 直接返回 → 100% schema 等价，杜绝抄写错误。另有测试断言 151 名称 == McpToolCatalog。
- **工具执行**：`McpToolTable`（C# 规格表：方法/路由/参数位置/特殊处理标记）+ `McpToolExecutor`（校验→认证→进程内 REST→脱敏/截断/本地分页/特殊兜底）。**进程内 REST**：`McpInProcessClient` = HttpMessageHandler 直接调用 app 的 RequestDelegate（同一进程、同一 DI、同一中间件链，无网络、无二次 token 换发 HTTP 开销）。认证复用 `McpClientService.VerifyAsync`（进程内直调）→ scoped JWT → 复用 `McpScopedTokenMiddleware` 端点越权封死。
- **行为等价细则**（对照 Python 逐条）：
  - 401 守卫：MCP 路径无 `Authorization: Bearer`（OPTIONS 除外）→ 401 `{code:40101,message:"missing bearer token",data:null}`（JSON，非 SPA）
  - `/mcp/` → 308 归正（preserveMethod）
  - 工具级认证失败返回工具结果 dict `{"error": ..., "code": <httpStatus>}`（verify 的 401/403/400 映射为 HTTP 状态码）
  - 脱敏：url 字段 → urlHash（sha256 前 12 hex），title 保留；redactUrls 默认 true（6 工具 + get_file_open_link 恒脱敏）
  - 分页：page>=1、pageSize 1..100；本地分页工具（8 个）补 page/pageSize/total
  - 时间：ISO8601 UTC 解析、max span 366 天、timezone 默认 Asia/Shanghai、date 参数 YYYY-MM-DD 校验
  - 截断：>50KB → truncated/nextPage/_note
  - 写入工具必填校验、base64 解码（multipart 上传 3 个工具）
  - 特殊工具：get_event_by_id/get_task_by_id（fallback）、get_habit_occurrences、get_task_checklist、get_calendar_by_id、get_mobile_location_latest、get_quick_note_attachment_meta（HEAD）、get_system_health/get_version（匿名兜底）、search_calendar_*（400 兜底 /search）
- **stdio 模式**：`dotnet Pim.Api.dll --mcp-stdio` 独立进程，工具注册表与 HTTP 共享；token 透传（PIM_ACCESS_TOKEN / PIM_TOKEN_FILE 等，支持 JSON/plain，含 401 刷新逻辑）；审计到人由 REST 端点自身完成。
- **配置**：`MCP:Enabled`（默认 true）、`MCP:Path`（默认 /mcp）。不引入新容器/systemd。

## 改动文件

### src/modules/Pim.Module.Mcp/（新增）
| 文件 | 内容 |
|---|---|
| `Contract/mcp-tools.json` | 151 工具契约（name/description/inputSchema） |
| `Services/McpToolTable.cs` | 151 工具规格表（方法/路由/参数位置/标记） |
| `Services/McpInProcessClient.cs` | 进程内 HttpMessageHandler → RequestDelegate + HttpClient |
| `Services/McpToolExecutor.cs` | 参数校验/认证/调用/脱敏/截断/本地分页/特殊工具 |
| `Services/McpServerFactory.cs` | SDK McpServer（ListTools/CallTool 自定义 handler） |
| `Services/McpServerBootstrap.cs` | 守卫中间件 + 308 + MapMcp + stdio 运行 |
| `Services/McpStdioTokenSource.cs` | stdio token 解析（env/file）+ 401 刷新 |
| `McpOptions.cs` | 配置绑定 |
| `Pim.Module.Mcp.csproj` | + ModelContextProtocol.AspNetCore 2.2.0 |

### src/Pim.Api/
- `Program.cs`：MCP bootstrap（guard/308/MapMcp 或 --mcp-stdio）
- `appsettings.json`：MCP 配置节

### tests/Pim.UnitTests/Mcp/（新增）
- `McpToolContractTests.cs`：151 名称==McpToolCatalog；schema required/enum 抽查；无重复
- `McpToolExecutorTests.cs`：分页/时间/日期/base64 校验、脱敏、截断、本地分页
- `McpInProcessClientTests.cs`：分发正确性（path/query/body/headers/308）
- `McpProtocolIntegrationTests.cs`（WebApplicationFactory<Program>）：无 token GET/POST /mcp → 401 JSON；假 token → 401；initialize→tools/list→tools/call；低权限 → 403 工具结果；/mcp/ 308；写工具权限审计

### 部署/文档
- `scripts/mcp/deploy/openresty-mcp.conf`：proxy_pass → Pim.Api
- `docs/mcp.md`：架构图进程内、systemd 章节退役、Python 标注
- `scripts/mcp/deploy/pim-mcp.service`：废弃标注（README）
- Python 源码保留 + docs 标注（删除与否用户定）

## 验证

1. `dotnet build Pim.sln` / `dotnet test Pim.sln`（含新增 MCP 测试）
2. 151 工具名与 Python dump 自动 diff = 0（测试断言）
3. 三视角 review（sol/terra/flash）迭代至无问题
4. push + PR + GitHub CI 全绿