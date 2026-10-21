# PIM 客户端集成规格（Client Integration Spec）

> **目的**：让 Android / Windows / 其它原生客户端的开发者，**不读前端源码**也能实现一个功能对等的 PIM 客户端。
> 本文与 [`contract/openapi.json`](../contract/openapi.json) 是**同等约束力**的两份契约：契约钉死「形状」，
> 本文钉死「形状之外、客户端必须知道的行为」。二者冲突时：**形状以 OpenAPI 为准，行为以本文为准**，并在 PR 中修正漂移。
>
> **状态：草稿（Draft）**，维护方式与校验规则见 [`client-integration/README.md`](client-integration/README.md)。取材自 `pim-web/frontend-rebuild-spec/`（Web 客户端重建规格，2026-09 基线），
> 已去除 React / TanStack Query 等框架实现细节，并按契约（`pim-api@87b9079`）与各仓 AGENTS/README 校正。
> 标注 **〔待确认〕** 的条目在现有文档或源码中找不到单一权威来源，**不要当作事实使用**。

## 文档地图

| 章节 | 内容 | 读者 |
|---|---|---|
| §1 适用边界 | 哪些端点是 API，哪些属于客户端本机 | 所有人 |
| §2 传输与封装 | 基地址、`ApiResponse`、`PagedResult`、camelCase、错误语义 | 所有人 |
| §3 认证与会话 | 令牌寿命、单次刷新、401 规则、限流 | 所有人 |
| §4 业务码 | 码→动作对照（示例；全量码表仍是缺口，见 §11 第 8 条） | 所有人 |
| §5 数据口径 | 业务日、时区、指标量纲、双态端点 —— **最容易写错的部分** | 所有人 |
| §6 上传 / 下载 | ≤4MB 直传、分片直传（Graph / Drive resumable）、302 / Blob / 直链、ICS | 所有人 |
| §7 同步与离线 | 增量拉取、补传、幂等、重试与退避 | 所有人 |
| §8 采集端（Android / Windows 守护） | 采集策略、本机桥协议、上报契约 | 采集端开发者 |
| §9 界面能力清单 | 页面 → 模块（类型级） → 调用端点 | 产品 / 前端 |
| §10 版本与兼容 | 客户端版本检查、壳更新、弃用策略 | 所有人 |
| §11 已知缺口与未覆盖项 | 契约里没有、本文也不敢断言的东西 | 所有人 |

---

## 1. 适用边界

- **属于 API**：`contract/openapi.json` 中 `paths` 下的一切（operation 数量以契约文件为准，本文不写死数字）。
- **不属于 API、但客户端必须实现**：
  - **Android 原生桥**（§8.2）：WebView 内嵌页 ↔ 壳的 `postMessage` 协议，只在本机发生；
  - **Windows 守护进程本地监听**（§8.3）：`http://localhost:15601/browser/*`，只回环可达（本机协议，不属于 pim-api 契约）；
  - **浏览器扩展 ↔ 守护进程**（§8.3）：扩展把活动标签页 POST 给守护进程，再由守护进程上报服务端。
- **协议面**：`/api/version`、`/health` **在契约里**（形状最简单）；`/health/live`、`/health/ready`、`/metrics`
  运行时存在但**不在契约**；`/mcp`（MCP Streamable HTTP）契约里只有 `GET`/`DELETE`/`PATCH`，没有 `POST` 与
  JSON-RPC 定义。这些都不是给普通 UI 用的，见 §11.1。 <!-- drift-ignore: /health/live --> <!-- drift-ignore: /health/ready --> <!-- drift-ignore: /metrics -->

## 2. 传输与封装

| 项 | 约定 |
|---|---|
| 基地址 | 默认 `http://127.0.0.1:5858`；域 API 统一前缀 `/api/v1` |
| 成功封装 | `{ code, message, data, timestamp }`，`code = 0` 表示成功；客户端只看 `data` |
| 列表分页 | `{ items[], totalCount, page, pageSize, totalPages }`（注意是 `totalCount`，不是 `total`） |
| JSON | camelCase；时间为 ISO 8601（`date-time`） |
| 未匹配路径 | 任意未匹配的 `/api` 路径（含契约外的路径）返回 **JSON 404** `{code:404, message:"接口不存在: …"}`，而不是 SPA 的 HTML |
| 错误 | 业务错误是 **HTTP 状态码 + `ApiResponse` 体**，不是 ProblemDetails；`data` 多为 null |
| 关联 ID | 请求头 `X-Correlation-Id` 会被透传/生成；客户端建议每个用户操作生成一个并打日志 |
| 截断 | 运维类端点（`/api/v1/ops/*`）在结果被截断时返回 `206` + `X-Truncated: true` |

> **给原生客户端的硬规则**：不要假设「2xx 一定是 JSON」。文件下载/导出类端点可能返回
> `application/octet-stream`、`text/calendar`、`image/png`，或 **302 重定向**（§6）。按响应的
> `Content-Type` 分派，并把「HTML 响应」一律视为错误（说明请求落到了 SPA fallback）。

## 3. 认证与会话

| 项 | 约定 |
|---|---|
| 方案 | JWT Bearer，`Authorization: Bearer <accessToken>` |
| 获取 | `POST /api/v1/auth/login` → `{ accessToken, refreshToken, expiresAt, user }` |
| 注册 | `POST /api/v1/auth/register`：**默认关闭**；库非空时返回 403 `code=40304`。首个用户自动成为 admin |
| 访问令牌寿命 | 约 15 分钟（以 `expiresAt` 为准，不要写死） |
| 刷新 | `POST /api/v1/auth/refresh`，旧 refreshToken 立即吊销（轮换）；refreshToken 有效期 7 天 |
| 401 处理 | **任一请求收到首个 401 → 并发请求共享同一次刷新**，刷新成功后重放原请求一次；刷新失败 → 清空令牌并回登录页。**不要**对每个请求各自刷新（会造成刷新风暴，并因令牌轮换把彼此踢下线） |
| 登出 | 纯客户端清令牌，无服务端调用 |
| 令牌存储 | 移动端：系统安全存储（Android Keystore / DPAPI），**不要**放明文 SharedPreferences / 普通文件；内嵌 WebView 不落盘（§8.2） |
| 登录限流 | 同 IP 15 分钟内失败 ≥5 次 → 429 + `Retry-After: 900`（秒）；按头退避，不要在等待期重试 |
| 角色 | `admin` / `user`；管理端点（`/api/v1/admin/*`、`/api/v1/ai/*`）仅 admin。`/metrics` 例外：接受 admin JWT **或** OpsKey（`X-PIM-Ops-Key` / Bearer），未授权返回 401 `code=40101`（运维用途，不是给原生 UI 的） |

## 4. 业务码

- **全量码表目前不在本仓**：码由各模块源码里的 `ApiResponse.Error(code, …)` 就地定义，尚无机器生成的清单。
  这是已知缺口（§11 第 8 条）；在补齐前，客户端**不要**把本文示例当成完整枚举。
- 客户端**只按 `code` 分支**，`message` 仅用于展示；未知码按「通用失败」处理并上报诊断日志。
- 例如：`code=02057` 任务状态非法（400，消息里带允许值）；`40304` 公开注册关闭；`42901` 运维限流。

## 5. 数据口径（最容易写错）

### 5.1 业务日（Business Day）

- 需求背景：系统业务日以 **04:00（Asia/Shanghai）切日**，即凌晨 4 点前仍算前一天。权威实现见后端
  `Pim.Core/Common/BusinessDay.cs`。**〔待确认〕** 是否所有时间类端点都按此口径返回——目前后端存在多处各自实现。
- **客户端不得自行推算业务日**：能用服务端返回的日期就优先用服务端给的
  （例如今日分区注册表里的 `pcBusinessDate`）；必须本地计算时，唯一实现放在客户端的 `businessDay` 模块，
  参数化「时区 + 切日时刻」，不要散落在各页面。
- 时区与切日时刻目前**没有**在契约中声明为端点字段——这是已知缺口（§11）。

### 5.2 指标量纲

- 同名指标（如 `activeMinutes`、`intensityScore`）在不同端点可能由不同服务计算，**历史上存在口径不一致**
  （例如 `intensityScore` 出现过三种量纲）。客户端展示时：**同一屏只用来自同一端点的数值**，不要跨端点拼接比较。

### 5.3 双态端点（同一 URL 随参数返回不同结构）

- 已知（**当前 master 仍然存在**；pim-api#484 **未合入**，合入后才与契约的单形态一致）：
  - `GET /api/v1/calendar/events`：`search` / `calendarId` / `page` / `pageSize` **全为空** → 裸数组；否则 `PagedResult`。
    判断条件**不看** `start` / `end`，所以只带日期范围的事件查询也走数组分支。
  - `GET /api/v1/calendar/tasks`：`search` / `calendarId` / `status` / `priority` / `plannedFrom` / `plannedTo` /
    `dueFrom` / `dueTo` / `page` / `pageSize` **全为空** → 裸数组；否则 `PagedResult`。
    **`inbox` 不在判断条件里**，所以只带 `inbox=true` 仍返回数组。
- **原生客户端的做法**：一律显式带 `page` / `pageSize`（哪怕只按 `inbox` 过滤），这样两个端点都恒为 `PagedResult`；
  需要全量（如侧栏收件箱）时循环翻页到 `totalCount`。不要依赖「不带参数返回全量」的行为。

## 6. 上传 / 下载

### 6.1 文件上传（OneDrive / Google Drive 提供方）

| 大小 | 提供方 | 通道 | 步骤 |
|---|---|---|---|
| ≤ 4 MB | 两者 | 服务端代理直传 | `POST /api/v1/files/items/upload`，multipart 字段名取自实现（`FilesModule.cs` 的 `CreateUploadItemAsync`）：`providerId`（UUID）、`path`、`file`。契约目前未声明该 requestBody（见 §11.2），但字段名是确定的 |
| > 4 MB（≤ 2 GB） | OneDrive | Microsoft Graph 分片直传 | ① `POST /api/v1/files/items/upload-session`（JSON：**`providerId`（必填，UUID）**、`path`、`fileName`，见 `CreateUploadSessionRequest`）→ 预授权 `uploadUrl`；② 客户端直接 PUT 到 `uploadUrl`：**不带 Authorization 头**，10 MiB 一分片，`Content-Range: bytes s-e/total`；**202** 表示继续（按 `nextExpectedRanges` 续传），**201/200** 表示完成；每分片最多重试 2 次；③ 见「完成登记」 |
| > 4 MB（≤ 2 GB） | Google Drive | Drive resumable session 直传 | ① 同一个 `POST /api/v1/files/items/upload-session` → `uploadUrl` 是 **Google 的 resumable session URI**（服务端创建时带了 `Origin`，浏览器可跨域直传）；`expirationDateTime` 为 `null`（Drive 不返回，实际约一周）；② PUT 到该 URI：`Content-Range: bytes s-e/total`（单次可用 `bytes */total` + 全量）；**308 + `Range` 头**表示继续（按已收字节续传），**200/201** 表示完成；③ 见「完成登记」 |
| > 2 GB | 两者 | 客户端直接拒绝并提示 | — |

- **完成登记（两种提供方相同）**：`POST /api/v1/files/items/upload-session/complete`（JSON：**`providerId`、`path`、`fileName` 均必填**，`uploadedItemId` 可选）。
- 队列行为：**串行**（一次一个文件）；逐文件进度/失败/重试；断点信息持久化（App 重启可续）。
- 其它单次 multipart：快速记录附件 `POST /api/v1/quick-notes/attachments`（**服务端上限 4 MB**，超出返回业务码 `5331`；契约未声明该 requestBody，见 §11.2）；ICS 导入 `POST /api/v1/calendar/import-ics`（**契约已声明 multipart**）。

### 6.2 下载与预览

| 通道 | 端点 | 客户端处理 |
|---|---|---|
| 直链 JSON（200 + `{url}`） | `GET /api/v1/files/items/{id}/download-url`、`GET /api/v1/files/items/{id}/preview-url` | 交给系统浏览器/下载器打开；> 100 MB 先确认大小。响应是 JSON，**不是**文件体 |
| **302 到网盘域** | `GET /api/v1/files/items/{id}/download`、`/content`、`/thumbnail`（可带 `?size=`） | 契约里这三个只有 302：`Location` 是网盘的下载地址 —— OneDrive 是预授权直链；Google 是 `webContentLink`（**依赖浏览器里已有的 Google 会话**，无头 HTTP 客户端跟随时会拿到登录页 HTML）。**不要**自己带 Bearer 跟随 —— 那是网盘域，我们的 JWT 无效（把 401/HTML 当成本服务端故障）；交给系统浏览器/下载器 |
| **200 或 302（按状态码分派）** | `GET /api/v1/quick-notes/attachments/{id}/download` | **OneDrive 附件 → 302**（`Location` 为预授权直链，同上处理）；**Google / 其它提供方 → 200 + 字节流**（服务端代理）。客户端必须按状态码分支：302 交给系统浏览器/下载器，**不要**带 Bearer 跟随；200 时按字节流保存（若把 302 的空体当文件存，会得到 0 字节文件）。契约同时声明了 200 与 302 |
| 200 + 字节流（需 Bearer） | `GET /api/v1/calendar/export-ics`（`text/calendar`）、`GET /api/v1/mobile/devices/{deviceId}/export`（`application/json` 附件） | 带 Bearer 取成字节流（建议 < 100 MB）；**不要**再对返回内容做二次下载 |
| 200 + JSON（导出数据） | `GET /api/v1/calendar/data-center/audit/export` | 是 JSON 体，不是文件下载；按 `AuditExportResponse` 解析 |

## 7. 同步与离线

- **拉取**：默认轮询（Web 客户端白天 5 分钟 / 夜间 30 分钟；原生客户端可按系统省电策略调整，但**离开前台后应降频或停止**）。
  没有 WebSocket / SignalR 实时通道。
- **增量**：**〔待确认〕** 目前没有全量的「按 updatedAt 增量拉取 + 删除墓碑」机制。
  后端 issue #457 只覆盖 **MCP 读工具**（`updatedSince` / `syncCursor` / `deletedIds`），**不是 REST API 的路线图**；
  原生客户端不能据此排期，REST 侧目前没有对应的跟踪 issue。
  在它落地前，漏刷问题靠「全量按时间窗拉取 + 首屏刷新」解决。
- **上报（采集端）**：允许积压、逐条上传、不丢点（见 `pim-android/README.md`：只做精度过滤，不抽稀、不静默裁剪）。
- **重试**：429 按 `Retry-After`；5xx 指数退避（上限 5 分钟）；4xx（除 401/429）不重试。
- **幂等**：**〔待确认〕** 上报类端点是否按客户端 `uid` 去重（如事件、位置点）没有在契约中声明；
  客户端应生成稳定 `uid` 并在重试时复用，避免后端未来去重时产生歧义。
  另有一个按请求去重的通用机制在 pim-api#484（**未合入**）：写操作可选带 `Idempotency-Key` 请求头，
  同 key 重放返回首次响应。合入后才可用；在它落地前，重试去重仍只能靠业务侧的 `uid`。

## 8. 采集端

### 8.1 上报接口（契约内）

| 端 | 端点 | 说明 |
|---|---|---|
| 手机 | `POST /api/v1/mobile/devices/register` | 设备注册（首次登录后） |
| 手机 | `POST /api/v1/mobile/sync/gaps` | 询问服务端缺哪些时间段（补传） |
| 手机 | `POST /api/v1/mobile/usage/events` | 应用使用事件（批量） |
| 手机 | `POST /api/v1/mobile/forensics/events` | 取证事件 |
| 手机 | `POST /api/v1/mobile/location/points`、`.../points/batch` | 位置点（单条 / 批量） |
| PC | `POST /api/v1/daemon/heartbeat`、`/daemon/planned-offline` | 守护心跳 / 计划离线 |
| PC | `POST /api/v1/endpoints/{deviceId}/heartbeat`、`/notification-actions` | 端点心跳 / 通知动作 |
| PC | `POST /api/v1/pc/tracker/upload`、`/pc/keystats/upload`、`/pc/keystats/samples`、`/pc/browser-tt/upload`、`/pc/aw/upload` | 窗口事件、键鼠统计、浏览器标签页、活跃窗口 |
| PC | `GET /api/v1/endpoints`、`GET /api/v1/endpoints/{deviceId}/collection-quality` | 端点列表与采集质量 |

> 上报端点的字段形状以契约生成的 DTO 为准；服务端是否接受乱序/重复批次见 §7「幂等」。

### 8.2 Android 原生桥（WebView 内嵌页 ↔ 壳）

- 仅 `/embed/android/today`、`/embed/android/tracks` 两个内嵌页使用；通过 `window.pimAndroid` `postMessage` 交换**非敏感**状态。
- 消息类型：`native.state.request`（页→原生，拉采集状态：是否持续采集/触发原因/下个位置/电池优化，30 秒轮询）、
  `page.report`（页→原生，上报页面数据状态：成功/失败/空，30 秒重试）。
- `token.request` / `token.refresh` 仅为兼容保留，固定回复 `auth_unavailable`；**令牌不进入页面、DOM、storage 或桥消息**，
  内嵌页的三条业务 GET 由原生代发。
- **原生客户端不应嵌入 WebView 来复用 Web UI**：产品目标是原生界面，这部分协议仅用于说明历史内嵌页的行为。

### 8.3 Windows 守护进程与浏览器扩展

- 守护进程本地监听 `http://localhost:15601/`（端口可配置）：`GET /browser/ping`、`POST /browser/heartbeat`、`POST /browser/site/heartbeat`。
  <!-- drift-ignore: /browser/ping --> <!-- drift-ignore: /browser/heartbeat --> <!-- drift-ignore: /browser/site/heartbeat -->
  （`drift-ignore` 按**精确路径**豁免，不支持前缀；本机守护进程协议不属于 pim-api 契约。）
  **只接受回环地址**（非回环 403），**无鉴权**（已知安全缺口：本机任意进程可伪造浏览器记录）。
- 浏览器扩展把活动标签页 POST 给守护进程，守护进程再用同一 JWT 上报 `pc/browser-tt/upload`。
- 守护进程是 **API 的客户端**，不是第二个后端；它默认服务端地址与 API 默认地址一致（`http://127.0.0.1:5858`）。

## 9. 界面能力清单（类型级）

> 只描述「有什么模块、调用哪些端点」，不规定布局与视觉。完整模块表（含每页的弹窗/抽屉、数据行为）
> 见 `pim-web/frontend-rebuild-spec/02-pages-and-modules.md` 与 `03-data-behaviors.md`。

| 页面（Web 路由） | 主要模块（类型） | 主要端点前缀 |
|---|---|---|
| 今日 `/today` | 分区注册表驱动的仪表盘（列表卡 + 指标卡 + 图表） | `/api/v1/today/sections`、`/api/v1/today/sections/{id}` |
| 日历 `/calendar` | 日历网格（时间轴/月）、图层过滤、拖放排期、收件箱侧板、日程/任务编辑弹窗 | `/api/v1/calendar/events`、`/calendars`、`/layers`、`/tasks/{id}/plan` |
| 工作台 `/workbench` | 指标卡行、AI 排程建议卡、待确认卡、同步状态卡 | `/api/v1/calendar/ai-placeholders`、`/api/v1/operations/confirmations/pending`、`/api/v1/calendar/outlook/*` |
| 任务 `/tasks` | 筛选条 + 任务树 + 列表、多选批操作、分段编辑面板、清单 | `/api/v1/calendar/tasks`、`/task-books`、`/tasks/{id}/segments`、`/tasks/{id}/checklist`（读取见 pim-api#482） |
| 确认中心 `/confirmations` | 主从面板、两步武装确认 | `/api/v1/operations/confirmations/*` |
| 快速记录 `/quick-notes` | 瀑布流卡片墙、可拖拽编辑弹窗、附件 | `/api/v1/quick-notes`、`/attachments` |
| 文件 `/files` | 三栏浏览器（树 + 列表 + 预览）、上传队列、快照与单条恢复 | `/api/v1/files/*`（**没有回收站列表**，见 §11 第 7 条） |
| 手机记录 `/mobile-records` | 热力图、图表、时间线、设备存活视图 | `/api/v1/mobile/analytics/*`、`/mobile/liveness/*` |
| 历史位置 `/location-history` | 地图轨迹、频繁地点、区段点 | `/api/v1/mobile/location/*` |
| 电脑记录 `/pc-tracker` | 热力图、时间线、生产力指标、分类建议 | `/api/v1/pc/*` |
| 提醒/报告/习惯 | 列表 + 规则表单 + 发送历史 | `/api/v1/calendar/reminders`、`/reports`、`/habits` |
| 设置（二级） | 账号、数据可信度、同步、AI、MCP、用户管理（文件没有回收站，见 §11 第 7 条） | `/api/v1/auth`、`/data-reliability`、`/ai`、`/mcp`、`/admin` |
| 状态 `/status` | 组件状态列表、采集质量 | `/api/v1/status`、`/endpoints` |

## 10. 版本与兼容

- `GET /api/client/shell/latest`（匿名）：返回 `androidUrl/windowsUrl/shellAndroidUrl/shellWindowsUrl` 及各自版本号，
  用于「有可用更新」提示。**在契约里**（`ClientShellLatestResponse`，匿名）。
- `GET /api/version`（匿名）：API 版本信息，**在契约里**（`ApiVersionResponse`，匿名）。
- 客户端应在启动时缓存一次版本信息，失败不阻塞；更新只做提示，不强制中断（避免个人网络环境下锁死）。
- 弃用策略：**〔待确认〕** 契约里 `deprecated` 标记目前为 0；下线端点前必须在 PR 与 release note 声明。
  机器化的「基线里有、当前没有」告警由 PR #480 的缺口审计引入（**未合入**）；在本仓当前 master 上，
  只有本 PR 的 `check-spec-drift.py` 会拦住「规格引用了已消失的端点」。

## 11. 已知缺口与未覆盖项（诚实清单）

这些是**工作回答「只给契约够不够」时发现的缺口**；本规格不臆测答案：

1. **契约未覆盖的部分**：
   - `GET /api/v1/calendar/tasks/{id}/checklist`：任务清单此前只有写路由，读取接口由 pim-api#482 补齐（合入前契约里没有这一条）； <!-- drift-ignore: /api/v1/calendar/tasks/{id}/checklist -->
   - `/health/live`、`/health/ready`、`/metrics`：运行时存在，但不在契约（运维用途，客户端不需要）；
   - `/mcp` 的 JSON-RPC / SSE 语义：契约里只声明了 `GET`/`DELETE`/`PATCH`，**没有 `POST`（JSON-RPC 主通道）**，
     也没有方法定义与事件格式；
   - `stats/upload`：源码里未被任何 API 业务路径引用（模块未加载），删除 PR 为 pim-api#483（**未合入**）；
     无论合入与否，**客户端不要调用**。 <!-- drift-ignore: /api/v1/stats/upload -->
2. **契约缺 requestBody**：`files/items/upload`（multipart）、`quick-notes/attachments` 等写操作在契约里没有
   `requestBody`，字段名只能从实现或 Swagger 的 Try-it-out 反推。本 PR **不逐项计数**（机器清单随审计 PR #480 提供，
   未合入）；需要字段名时请以对应 DTO 的源码为准。
3. **业务日 / 时区**未作为契约字段声明；客户端只能依赖服务端返回的日期字段（§5.1）。
   为按日端点补 `businessDate`/`businessTimeZone`/`businessDayStartHour` 的是 pim-api#484（**未合入**）。
4. **幂等 / 去重键**未在契约中声明；增量拉取与删除墓碑不存在（§7）。
5. **推送**：没有服务端推送端点（无 WebPush/FCM 对接）；通知依赖客户端轮询提醒队列 `GET /api/v1/calendar/reminders`。
6. **桌面壳更新通道**：Windows 守护进程/浏览器扩展的安装包分发与签名不在本规格范围内。
7. **规格漂移（已量化、可复现）**：给 `pim-web/frontend-rebuild-spec/05-api-reference/` 的每个文件顶部声明
   基地址 `/api/v1`（即一行值为 `/api/v1` 的 `drift-base` 注释，写法见 `client-integration/README.md`），
   再用本仓 `scripts/ci/check-spec-drift.py --spec <file>` 逐个检查：机器报 **28 条**，人工复核后
   **确属契约缺失的 18 条**如下（其余 10 条是同一端点的简写、前端路由或非 API 路径）。下面每一条都是**契约里不存在**的
   web 文档陈旧条目，本规格用 `drift-ignore` 显式豁免，便于逐条复核：
   - `files.md`（10 条）：
     - 版本历史四条：`GET /api/v1/files/items/{id}/versions` <!-- drift-ignore: /api/v1/files/items/{}/versions -->、
       `GET /api/v1/files/items/{id}/versions/{versionId}/download` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/download -->、
       `POST /api/v1/files/items/{id}/versions/{versionId}/restore` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/restore -->、
       `POST /api/v1/files/items/{id}/versions/{versionId}/restore-preview` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/restore-preview -->
       —— 契约里的对应概念叫 **snapshots**：`GET /api/v1/files/items/{id}/snapshots`、
       `POST .../snapshots/{snapshotId}/restore`（没有 download / restore-preview）；
     - 回收站两条：`GET /api/v1/files/trash` <!-- drift-ignore: /api/v1/files/trash -->、
       `POST /api/v1/files/trash/{id}/restore` <!-- drift-ignore: /api/v1/files/trash/{}/restore --> ——
       **契约里没有文件回收站**：只有 `POST /api/v1/files/items/{id}/restore` 恢复单条，
       没有「列出已删除项」的端点；
     - `POST /api/v1/files/items/{id}/index` <!-- drift-ignore: /api/v1/files/items/{}/index --> —— 契约里没有；
     - `POST /api/v1/files/providers/{id}/test` <!-- drift-ignore: /api/v1/files/providers/{}/test --> ——
       契约里只有 `GET /api/v1/files/providers/{id}/binding-status`；
     - `POST /api/v1/files/providers/nextcloud` <!-- drift-ignore: /api/v1/files/providers/nextcloud --> ——
       契约里**没有**这个字面路径，也没有 nextcloud 专用新增端点；只有通用模板
       `DELETE /api/v1/files/providers/{id}`（删除绑定）。支持的提供方是 onedrive / google；
     - `GET /api/v1/providers/{id}/sync-status` <!-- drift-ignore: /api/v1/providers/{}/sync-status -->（漏写 `files/`）
       —— 真实路径 `GET /api/v1/files/providers/{id}/sync-status`。
   - `pc-tracker.md`（1 条）：`GET /api/v1/pc/aw/heatmap` <!-- drift-ignore: /api/v1/pc/aw/heatmap --> 已随遗留端点下线；
     现存 `GET /api/v1/pc/heatmap/grid` 与 `GET /api/v1/pc/aw/timeline`。
   - `calendar.md`（1 条）：`GET /api/v1/calendar/tasks/{id}/checklist` <!-- drift-ignore: /api/v1/calendar/tasks/{}/checklist -->
     —— 契约里只有 `POST`；读取接口由 pim-api#482 补（未合入）。
   - `mobile.md`（1 条）：`POST /api/v1/mobile/apps/catalog-overrides` <!-- drift-ignore: /api/v1/mobile/apps/catalog-overrides -->
     —— 契约里该路径只有 `GET` / `PUT`。
   - `operations-status.md`（4 条）：`GET /health/live`、`GET /health/ready`、`GET /metrics`
     <!-- drift-ignore: /health/live --> <!-- drift-ignore: /health/ready --> <!-- drift-ignore: /metrics -->
     （运行时存在、不在契约，见 §1）、`POST /api/v1/stats/upload` <!-- drift-ignore: /api/v1/stats/upload -->（见 §11.1）。
   - `quick-notes-mcp.md`（1 条）：`POST /mcp` <!-- drift-ignore: /mcp --> —— 契约 `/mcp` 只声明
     `GET`/`DELETE`/`PATCH`（见 §11.1）。
   - 复核后**不算漂移**的 10 条：auth 文档开头的 `/login`、`/refresh`、`/register`、`/me`、`/password`（真实路径都带
     `/auth` 前缀，属同端点简写）；`calendar.md` 里 `detailUrl` 的 `/confirmations/{id}`、`/reminders/{id}`
     （是**前端路由**，不是 API）；`pc-tracker.md` 的 `/tree`（正文转述 `/pc/categories/tree` 的别名）；
     `operations-status.md` 里 web 前端的 SPA 兜底路由（形如 `/{*file}`，不是 API 端点；套上基地址后机器会把它当成
     基地址下的一段路径）与瓦片缓存**目录** `/data/pim/cache/tiles`（磁盘路径，不是路由）。
   **原生客户端应以本规格与契约为准**，不要照搬 web 规格里这些条目。
8. **业务码未机器化**：业务码（`code`）由各模块源码里的 `ApiResponse.Error(code, …)` 就地定义，
   契约只声明 HTTP 状态码与 `ApiResponse` 信封，**不含码表**；本仓也没有生成码表的脚本。
   §4 的示例只是最常见的几条。客户端必须按「未知码 → 通用失败 + 上报诊断」处理，
   不要把本文示例当成完整枚举（重复见 §1 导入表与 §4）。

---

## 变更记录

| 日期 | 变化 |
|---|---|
| 2026-10-20 | 初稿：从 Web 重建规格抽取与框架无关的部分，按契约 `pim-api@87b9079` 校正，列出已知缺口 |
