# 04 API 通用约定（Conventions）

> 面向客户端开发者的后端 HTTP 面总览。字段级规格见 [05-api-reference/](05-api-reference/)。
>
> 阅读说明：`05-api-reference/` 各端点节标题为可读性统一**省略了路由参数约束**（如 `{id:guid}` 写作 `{id}`）；实际约束以各节"来源"行所引源码为准。

## 1. 架构与基地址

- 单一 ASP.NET Core 主机（默认 `http://127.0.0.1:5858`），模块化路由组（Calendar / Files / Mcp / Mobile / PcTracker / QuickNotes / Stats）+ 顶层端点（Auth/Admin/AI/Operations/Status/Today/Ops 等）。
- 域 API 统一前缀 **`/api/v1`**；前端以同源方式访问（生产：SPA 由本 API 伺服；开发：`/api` 代理到 5858）。
- 版本前缀之外的例外面：`/api/version`、`/api/client/shell/latest`（含旧别名 `/api/v1/client/shell/latest`）、`/health*`、`/metrics`、`/mcp`、`/hangfire`。

## 2. 响应封装与格式

- 统一封装 `ApiResponse<T>`：`{ code, message, data, timestamp }`；`code=0` 表示成功；下文"响应 data"均指 `data` 字段。
- JSON 为 camelCase。
- 列表端点返回 `PagedResult<T>`：`{ items[], totalCount, page, pageSize, totalPages }`（`Pim.Core/Common/PagedResult.cs`）。
- 错误：业务错误按 HTTP 状态码 + 封装返回（例如 OneDrive Graph 失败 → 404/429/502 code 5390；参数错误 → 400 code 40000；未处理异常 → 500 code 01001；客户端中止 → 499）。**不是** ProblemDetails。
- 未匹配的 `/api/*` 路径返回 **JSON 404** `{code:404, message:"接口不存在: …"}`（先于 SPA fallback）。
- 截断类响应：`206 Partial Content` + `X-Truncated: true`（运维日志/SQL 查询端点）。
- 关联 ID：请求头 `X-Correlation-Id` 被透传/生成。

## 3. 认证与角色

| 项 | 约定 |
|---|---|
| 方案 | JWT Bearer（RS256），Header `Authorization: Bearer <accessToken>` |
| 令牌获取 | POST /api/v1/auth/login（返回 accessToken + refreshToken + user）；POST /auth/refresh 轮换（旧 refresh 吊销，7 天有效期）。公开注册默认关闭：库非空时 POST /auth/register 返回 403 code=40304 |
| 访问令牌寿命 | 约 15 分钟；客户端在任意请求首个 401 时自动刷新（共享单次刷新，防并发风暴） |
| 角色 | `admin` / `user`；`users` 表为空时首个注册用户为 `admin`；表非空时需管理员创建（或后端开启注册开关 `Auth:AllowPublicRegistration`）；无 admin 时启动期提升最早有效用户 |
| 默认策略 | **无全局 fallback**：端点默认匿名，除非显式要求认证 |
| 已知例外（现状） | `GET /api/v1/pc/*` 读组当前未挂认证（匿名可读）；`GET /api/v1/tiles/{z}/{x}/{y}.png` 匿名；`/api/version`、`/api/client/shell/latest` 匿名；`/api/v1/mcp/verify` 用 MCP 客户端 token（非 JWT） |
| 运维密钥 | `/api/v1/ops/*` 全部要求 `X-PIM-Ops-Key` 请求头；未配置 → 503 OpsDisabled；无效 → 401 OpsKeyMissingOrInvalid；另有每 IP 限流 → 429 |
| 登录限流 | 每 IP 15 分钟内失败 ≥5 次 → 429 + `Retry-After: 900` |
| MCP 受限令牌 | 带 `mcp_tool` 声明的 JWT 只能访问其权限矩阵内的端点（越权 403 code 40302） |
| CORS | 白名单（默认本机 5173/5858），AllowCredentials |

## 4. multipart / 二进制 / 流式端点索引

| 端点 | 形态 |
|---|---|
| POST /api/v1/calendar/import-ics | multipart（file + calendarId） |
| POST /api/v1/files/items/upload | multipart（providerId、path、file） |
| POST /api/v1/quick-notes/attachments | multipart（file） |
| POST /api/v1/pc/browser-tt/import | JSON Body（content） |
| GET /api/v1/calendar/events/{eventId}/attachments/{attachmentId}/download | 二进制文件流（带文件名；409=需重新授权） |
| GET /api/v1/calendar/export-ics | `text/calendar` 下载 |
| GET /api/v1/mobile/devices/{id}/export | JSON 文件下载 |
| GET /api/v1/quick-notes/attachments/{id}/download | 302 到 OneDrive 直链，或代理文件字节 |
| GET /api/v1/files/items/{id}/download、/content、/thumbnail | 302 重定向到微软直链（前端不调用本端点，改用 download-url；仅 /content、/thumbnail 用带认证 fetch→Blob，见 03 §5） |
| GET /api/v1/tiles/{z}/{x}/{y}.png | PNG 流（OSM 代理，7 天不可变缓存，`X-PIM-Tile-Cache: HIT/MISS`） |
| POST /mcp（GET 同路径） | JSON-RPC（POST）/ SSE 事件流（GET）——系统内唯一流式面 |

## 5. 非 REST 面

| 面 | 路径 | 认证 | 说明 |
|---|---|---|---|
| MCP Streamable HTTP | `/mcp`（可配置） | Bearer（普通 JWT 或 MCP 受限 token） | POST=JSON-RPC；GET=SSE 流；`/mcp/`→308；无会话的 GET/DELETE/PATCH→400；OPTIONS 允许 CORS；另有 stdio 模式（非 HTTP） |
| Hangfire 仪表盘 | `/hangfire` | admin JWT 或 OpsKey 或 Basic（配置项） | 存储未配置时禁用 |
| 健康检查 | `/health`、`/health/live`、`/health/ready` | 匿名 | ready 返回各组件明细 |
| Prometheus 指标 | `/metrics` | Admin 或 OpsKey | 文本格式 |
| 静态文件 / SPA | `wwwroot` + fallback `index.html` | 匿名 | 前端构建产物由 API 伺服 |
| Windows 守护进程本地监听 | `http://localhost:15601/`（浏览器桥：GET /browser/ping、POST /browser/heartbeat、POST /browser/site/heartbeat） | 无（仅回环，403 非回环） | **属于 Windows 客户端而非本 API**；Web 前端不访问它，仅列出供了解 |

## 6. 与其他客户端的关系

- **Android 客户端**：消费 `/api/v1/mobile/*` 的上报端点（设备注册、使用事件、位置点、取证事件）与统计上传（`/api/v1/stats/upload`）；也消费认证端点。
- **Windows 守护进程（PimDaemon）**：是 API 的客户端（非第二个后端）；用同一 JWT 认证，上报 `daemon/heartbeat`、`daemon/planned-offline`、`pc/keystats/*`、`pc/tracker/*`、`pc/browser-tt/upload`，并轮询 `/api/v1/endpoints`。
- **MCP 客户端**：经 `/api/v1/mcp/verify` 换受限 token，走 `/mcp` Streamable HTTP。
- Web 前端只与本 API 同源通信，不直连守护进程或任何第三方。

## 7. 聚合缓存与 force 参数

重型分析端点（Today 分区、Mobile 分析/位置、PC 汇总/聚合）在服务端有聚合结果缓存；所有这些端点接受布尔参数 `force` 以绕过缓存（前端在手动刷新时传 `force=true`）。

## 8. 重建建议的客户端层形态（参考，非强制）

- 一个 `apiFetch` 封装：基地址 `/api/v1`、自动 JSON 头（上传除外）、Bearer 注入、`{code,message,data}` 解包、204/空体→undefined、非 JSON 响应识别为可读错误（SPA fallback 保护）、首个 401 触发共享单次刷新。
- 少数"裸 fetch"场景：版本信息（匿名）、文件下载 Blob（带认证头）、Graph 分片 PUT（不带认证头）。
