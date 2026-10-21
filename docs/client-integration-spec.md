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
  运行时存在但**不在契约**；`/mcp` 是 **MCP Streamable HTTP**，契约里只声明了 `GET`/`DELETE`/`PATCH`，
  但运行时**支持 `POST` JSON-RPC**（`MapMcp`）—— 这是「契约没写、行为存在」的一处，客户端按 MCP 规范实现，
  细则见 §8.4。这些都不是给普通 UI 用的。 <!-- drift-ignore: /health/live --> <!-- drift-ignore: /health/ready --> <!-- drift-ignore: /metrics -->

## 2. 传输与封装

| 项 | 约定 |
|---|---|
| 基地址 | 默认 `http://127.0.0.1:5858`；域 API 统一前缀 `/api/v1/` |
| 成功封装 | `{ code, message, data, timestamp }`，`code = 0` 表示成功；客户端只看 `data` |
| 列表分页 | `{ items[], totalCount, page, pageSize, totalPages }`（注意是 `totalCount`，不是 `total`） |
| 分页默认与上限 | `page` 从 1 起算，缺失或 <1 一律按 1；`pageSize` 由服务端**夹紧**而非报错：日历 `events`/`tasks` 默认 50、上限 100；文件搜索/列表上限 100；移动端时间线默认 5000、上限 50000。客户端不要假设「传多少就返回多少」，用响应里的 `pageSize`/`totalPages` 翻页 |
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
| 登录失败 | 用户名/密码错误 → **401 且 body 为空**（不是 `ApiResponse`，不要按 JSON 解析）；账号停用 → **403** + `{code:40030}` |
| 登录限流 | 同 IP 15 分钟内失败 ≥5 次 → **429 且 body 为空** + `Retry-After: 900`（秒）；按头退避，等待期不要重试 |
| 注册 | `POST /api/v1/auth/register`：**默认关闭**；库非空时返回 403 `code=40304`。首个用户自动成为 admin |
| 访问令牌寿命 | 约 15 分钟（以 `expiresAt` 为准，不要写死） |
| 刷新 | `POST /api/v1/auth/refresh`，旧 refreshToken 立即吊销（轮换）；refreshToken 有效期 7 天 |
| 401 处理 | **任一请求收到首个 401 → 并发请求共享同一次刷新**，刷新成功后重放原请求一次；刷新失败 → 清空令牌并回登录页。**不要**对每个请求各自刷新（会造成刷新风暴，并因令牌轮换把彼此踢下线） |
| 登出 | 纯客户端清令牌，无服务端调用 |
| 令牌存储 | 移动端：系统安全存储（Android Keystore / DPAPI），**不要**放明文 SharedPreferences / 普通文件；内嵌 WebView 不落盘（§8.2） |
| 角色 | `admin` / `user`；管理端点（`/api/v1/admin/*`、`/api/v1/ai/*`）仅 admin |
| OpsKey（运维，非 UI） | `/api/v1/ops/*` **只认** `X-PIM-Ops-Key: <key>` 请求头（不认 Bearer JWT、也不认 admin 角色）：服务端未配置 key → **503** `{code:50301}`；缺失/错误 → **401** `{code:40101}`。`/metrics` 是唯一例外：admin JWT **或** OpsKey（`X-PIM-Ops-Key` 或 `Authorization: Bearer <opsKey>`），否则 401 `{code:40101}`。这些都不是给原生 UI 调用的 |

## 4. 业务码

- **全量码表在 [`docs/business-error-codes.md`](business-error-codes.md)**（与 `contract/openapi.json` 同等约束力的补充契约）：
  由 `scripts/ci/extract-business-error-codes.py` 从源码机器生成，`--check` 在 CI 里防止表落后于源码；
  每个码都给了 HTTP 状态、定义位置、消息与「客户端建议动作」。**客户端以该表为唯一枚举来源**。
- 客户端**只按 `code` 分支**，`message` 仅用于展示；表里没有的码按「通用失败」处理并上报诊断日志。
  同一个码在不同路径上可能对应不同 HTTP 状态（表里写成 `400 / 409`），**不要用 HTTP 状态反推业务码**。
- 例如：`2057` 任务状态非法；`40304` 公开注册关闭（403）；`40101` 认证缺失/无效（401，MCP、Ops 与 `/metrics` 共用）；
  `42901` 限流（429，MCP 与 Ops 共用）。源码里的前导零（`02057`）只是可读性，客户端收到的是整数 `2057`。
- 例外：登录密码错误返回 **401 空 body**、登录限流返回 **429 空 body + `Retry-After`**（见 §3），
  这两个响应没有 `ApiResponse` 体，必须按状态码/响应头处理。

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
| ≤ 4 MB | 两者 | 服务端代理直传（multipart） | `POST /api/v1/files/items/upload`，字段：`providerId`（UUID 文本）、`path`（**完整文件路径，含文件名**）、`file`（文件本身）。例：`path=/Photos/2026/a.jpg` |
| > 4 MB（≤ 2 GB） | OneDrive | Microsoft Graph 分片直传 | ① `POST /api/v1/files/items/upload-session`（JSON：`providerId`（必填，UUID）、`path`（**目标目录，不含文件名**）、`fileName`）→ `uploadUrl`；② 客户端直接 PUT 到 `uploadUrl`：**不带 Authorization 头**，10 MiB 一分片，`Content-Range: bytes s-e/total`；**202** 表示继续（按 `nextExpectedRanges` 续传），**201/200** 表示完成；每分片最多重试 2 次；③ 见「完成登记」 |
| > 4 MB（≤ 2 GB） | Google Drive | Drive resumable session 直传 | ① 同一个 `POST /api/v1/files/items/upload-session` → `uploadUrl` 是 **Google 的 resumable session URI**（服务端创建时带了 `Origin`，浏览器可跨域直传）；`expirationDateTime` 为 `null`（Drive 不返回，实际约一周）；② PUT 到该 URI：分片 `Content-Range: bytes s-e/total`；**一次传完**用 `Content-Range: bytes 0-(total-1)/total` + 完整 body（`bytes */total` 是**不带 body 的状态查询**，不是上传）；**308 + `Range` 头**表示继续（按已收字节续传），**200/201** 表示完成；③ 见「完成登记」 |
| > 2 GB | 两者 | 客户端直接拒绝并提示 | — |

- **`path` 的两种语义（最容易传错）**：
  - 直传 `POST /api/v1/files/items/upload`：`path` = **完整文件路径**（服务端在最后一个 `/` 处拆成目录 + 文件名）；
  - `upload-session` 与 `upload-session/complete`：`path` = **目标目录**，文件名由独立的 `fileName` 传入。
    把 `/Photos/a.jpg` 传给 upload-session 会被当成「目录名 a.jpg」。
- **完成登记（两种提供方相同）**：`POST /api/v1/files/items/upload-session/complete`（JSON：`providerId`、`path`（目标目录）、`fileName` 均必填，`uploadedItemId` 可选；
  **Google 场景建议必带 `uploadedItemId`** —— 完成响应里的真实条目 id，重名改名时按路径回读会登记到旧文件）。
- 队列行为：**串行**（一次一个文件）；逐文件进度/失败/重试；断点信息持久化（App 重启可续）。
- 其它单次 multipart：
  - 快速记录附件 `POST /api/v1/quick-notes/attachments`：multipart 只有**一个文件字段 `file`**
    （不是 `attachment`/`fileData`，缺了返回 400「缺少 file 文件字段」）；**服务端上限 4 MB**，超出返回业务码 `5331`；
  - ICS 导入 `POST /api/v1/calendar/import-ics`（**契约已声明 multipart**）。

### 6.2 下载与预览

| 通道 | 端点 | 客户端处理 |
|---|---|---|
| 直链 JSON（200 + `{url}`） | `GET /api/v1/files/items/{id}/download-url`、`GET /api/v1/files/items/{id}/preview-url` | 交给系统浏览器/下载器打开；> 100 MB 先确认大小。响应是 JSON，**不是**文件体 |
| **302 到网盘域** | `GET /api/v1/files/items/{id}/download`、`/content`、`/thumbnail`（可带 `?size=`） | 契约里这三个只有 302：`Location` 是网盘的下载地址 —— OneDrive 是预授权直链；Google 是 `webContentLink`（**依赖浏览器里已有的 Google 会话**，无头 HTTP 客户端跟随时会拿到登录页 HTML）。**不要**自己带 Bearer 跟随 —— 那是网盘域，我们的 JWT 无效（把 401/HTML 当成本服务端故障）；交给系统浏览器/下载器 |
| **200 或 302（按状态码分派）** | `GET /api/v1/quick-notes/attachments/{id}/download` | 规则是**按能力而不是按提供方名字**：能给出真正免密直链的提供方（当前是 OneDrive，**将来接入的任何直链提供方同样如此**）→ **302**（`Location` 为预授权直链，同上处理）；没有免密直链的（**Google 刻意不使用 `webContentLink`**，因为它依赖浏览器里的 Google 会话）→ **200 + 字节流**（服务端代理）。客户端**必须按状态码分支**，不要按提供方名字猜：302 交给系统浏览器/下载器，**不要**带 Bearer 跟随；200 时按字节流保存（若把 302 的空体当文件存，会得到 0 字节文件）。契约同时声明了 200 与 302 |
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
- **幂等**：上报端点的去重行为**逐端点不同**，见下表 —— **不是**「所有上报端点都去重」。
  客户端应生成稳定的 `clientItemKey` / `uid` 并在重试时复用；服务端行为以下表为准：

  | 端点 | 去重键（同一用户/设备内） | 重复时的表现 |
  |---|---|---|
  | `POST /api/v1/mobile/usage/events`（事件） | `(deviceId, packageName, eventType, eventTimestampUtc, className)` | HTTP 200，该条 `outcome="skipped"`、`code="duplicate"`，`accepted/skipped/rejected/failed` 计数相应变化 |
  | `POST /api/v1/mobile/usage/events`（汇总） | `(deviceId, packageName, windowStartUtc, windowEndUtc, sourceKind)` | 同上 |
  | `POST /api/v1/mobile/location/points`、`POST /api/v1/mobile/location/points/batch` | `(deviceId, recordedAtUtc, latitude, longitude)` | 条目级三种结果：已存在的**可用**点 → `outcome="skipped"`、`code="duplicate"`；坏精度的重复点 → `outcome="rejected"`、`code="unusable-accuracy"`；**先落库的坏精度点被后来的好精度点升级** → `outcome="accepted"`（不是 duplicate） |
  | `POST /api/v1/mobile/forensics/events` | `(deviceId, clientItemKey)`，**`clientItemKey` 必填** | 重复计入 `skipped`；缺失/空 → 该条 `rejected` |
  | `POST /api/v1/pc/keystats/samples` | `(pimDeviceId, sampledAtUtc)`，时间**截断到分钟** | upsert：同一分钟覆盖，不报错、不累加 |
  | `POST /api/v1/pc/keystats/upload` | `(deviceId, date)` —— **整天快照**，键是请求里的 `date`（不是 `sampledAtUtc`） | **整天删除后重插（整体覆盖，不是合并）**：同一天再传一次会替换当天的键鼠快照。客户端同一天只应上传一次最终快照；要按分钟增量上报请用 `/pc/keystats/samples` |
  | `POST /api/v1/pc/tracker/upload` | `(deviceId, timestamp, duration, eventType, appName, browser, instanceId)` | 命中去重集合的行被丢弃；响应 `data` 是**新增**条数 |
  | `POST /api/v1/pc/aw/upload`（旧通道） | 自然键 `(timestamp, duration, eventType, appName, windowTitle, afkStatus)`，只在**本次批次的时间窗**内比对 | 重复行丢弃；响应 `data` 是新增条数。**并发重复没有唯一约束兜底**（旧通道不带 `bucketId`/`sourceEventId`，`ux_pc_aw_events_source` 对空值不生效），客户端不要并发重发同一批 |
  | `POST /api/v1/pc/aw/upload-complete` | `(pimDeviceId, bucketId, sourceEventId)` | 重复 **覆盖更新**已有行（不是跳过）；响应 `data` 是**新增**条数 —— 重复批次返回 0 不代表失败 |
  | `POST /api/v1/pc/browser-tt/upload` | **无去重** | `focusMs`/`visitCount`/`runMs`/`mediaMs` 直接**累加**：同一批重复提交会把指标翻倍，`tick` 也会重复入库。客户端必须自己保证每批只发一次（重试前先确认上一批的结果） |

  **「重复」是成功语义**：响应仍是 2xx + 完整 `ApiResponse`，客户端把 `skipped/duplicate` 当作已送达，
  **不要**重试或计入失败。批量请求里单条失败不影响其它条目，按 `items[]` 逐条处理。
  `/pc/browser-tt/upload` 是唯一没有去重的上报端点 —— 对它必须「先确认、再重试」，不能无条件重试。
- **按请求幂等（pim-api#484，未合入）**：写操作可选带 `Idempotency-Key` 请求头，同 key 重放返回首次响应
  （24 小时内）；合入后才可用。它与上面的**业务键去重是两套机制**：业务键不依赖请求头，现在就能用。

## 8. 采集端

### 8.1 上报接口（契约内）

| 端 | 端点 | 说明 |
|---|---|---|
| 手机 | `POST /api/v1/mobile/devices/register` | 设备注册（首次登录后） |
| 手机 | `POST /api/v1/mobile/sync/gaps` | 询问服务端缺哪些时间段（补传） |
| 手机 | `POST /api/v1/mobile/usage/events` | 应用使用事件（批量） |
| 手机 | `POST /api/v1/mobile/forensics/events` | 取证事件 |
| 手机 | `POST /api/v1/mobile/location/points`、`POST /api/v1/mobile/location/points/batch` | 位置点（单条 / 批量） |
| PC | `POST /api/v1/daemon/heartbeat`、`POST /api/v1/daemon/planned-offline` | 守护心跳 / 计划离线 |
| PC | `POST /api/v1/endpoints/{deviceId}/heartbeat`、`POST /api/v1/endpoints/{deviceId}/notification-actions` | 端点心跳 / 通知动作 |
| PC | `POST /api/v1/pc/tracker/upload`、`POST /api/v1/pc/keystats/upload`、`POST /api/v1/pc/keystats/samples`、`POST /api/v1/pc/browser-tt/upload`、`POST /api/v1/pc/aw/upload` | 窗口事件、键鼠统计、浏览器标签页、活跃窗口 |
| PC | `POST /api/v1/pc/aw/upload-complete` | **完整版**活跃窗口上报（`pimDeviceId` + `bucket` + `events[]`，可带 `awInfo`）：与 `/pc/aw/upload` 是两条独立通道，按端上数据源二选一，**不要同时上报同一批事件**；单次最多 **500** 条，客户端必须自行分片（超出上限不是业务错误，会被当成 5xx，不应靠它做控制流）。响应 `data` 是**新增**条数：重复命中的行会覆盖更新但不计数，**不能用它判断整批是否落库** |
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

### 8.4 MCP 服务端（Streamable HTTP）

原生客户端若要做「MCP 设置」页或内嵌 AI 工具调用，需要连的是 **Streamable HTTP**，不是普通 REST 端点：

| 项 | 值 |
|---|---|
| 路径 | `/mcp`（服务端可配置；`/mcp/` 会被 **308** 重定向到 `/mcp`，保留方法与 body） |
| 鉴权 | 除 `OPTIONS` 外都必须带 `Authorization: Bearer <accessToken>`；缺失/无效 → `401` JSON `{code:40101,message:...}` |
| `POST /mcp` | JSON-RPC 主通道：`initialize`、`notifications/initialized`、`tools/list`、`tools/call`、`ping`。请求 `Accept` 需同时接受 `application/json` 与 `text/event-stream`；响应可能是二者之一 |
| 会话 | `initialize` 成功时服务端在响应头返回 `Mcp-Session-Id`；后续请求必须带回该头。`DELETE /mcp` + 该头用于结束会话 |
| `GET /mcp` | SSE 事件流（服务端推送）；`Accept: text/event-stream` |
| 无会话/无载荷 | 对 `GET`/`DELETE`/`PATCH` 的空请求返回 `400` JSON `{code:40001,...}`（不会落到 SPA HTML） |
| 工具集合 | 与 `contract/mcp-tools.json` 一致（见仓库 `src/modules/Pim.Module.Mcp/Contract/`），工具内部调用同一套 REST 端点；`tools/call` 的返回是 MCP 文本内容块，内容为 REST 的 `ApiResponse` 信封 |
| 本地替代 | 同一工具集也支持 stdio（`dotnet Pim.Api.dll --mcp-stdio`），供本机 Claude Code / Codex 使用；原生客户端通常用 HTTP |

> 这部分的形状**不在 `contract/openapi.json` 里**（OpenAPI 表达不了 JSON-RPC），以 MCP 规范
> （Streamable HTTP transport）与服务端实现为准；上面的路径/头/状态码是与本服务端绑定的确定事实。

## 9. 界面能力清单（类型级）

> 只描述「有什么模块、调用哪些端点」，不规定布局与视觉。**每个具体端点都写全 `/api/v1/...` 路径**
> （不用 `.../x` 或相对简写），这样漂移检查器能逐条校验；`/api/v1/pc/*` 这类 `*` 前缀只出现在
> 「整组端点都属于该页面」的摘要里，检查器对前缀只校验「其下至少有一个端点」，不代表逐条覆盖。
>
> 本表是**类型级摘要**：它覆盖「每个页面对应哪些 API 能力」。具体布局、弹窗层级与视觉细节属于产品设计，
> 不是 API 契约的一部分。API 行为（认证、分页、错误、上传、幂等等）以 §2–§8、§10 与
> `contract/openapi.json` 为准；**原生客户端实现所有服务端交互都不需要读 pim-web 源码**。

| 页面（Web 路由） | 主要模块（类型） | 主要端点 |
|---|---|---|
| 今日 `/today` | 分区注册表驱动的仪表盘（列表卡 + 指标卡 + 图表） | `/api/v1/today/sections`、`/api/v1/today/sections/{id}` |
| 日历 `/calendar` | 日历网格（时间轴/月）、图层过滤、拖放排期、收件箱侧板、日程/任务编辑弹窗 | `/api/v1/calendar/events`、`/api/v1/calendar/calendars`、`/api/v1/calendar/layers`、`/api/v1/calendar/tasks/{id}/plan` |
| 工作台 `/workbench` | 指标卡行、AI 排程建议卡、待确认卡、同步状态卡 | `/api/v1/calendar/ai-placeholders`、`/api/v1/operations/confirmations/pending`、`/api/v1/calendar/outlook/*` |
| 任务 `/tasks` | 筛选条 + 任务树 + 列表、多选批操作、分段编辑面板、清单 | `/api/v1/calendar/tasks`、`/api/v1/calendar/task-books`、`/api/v1/calendar/tasks/{id}/segments`、`/api/v1/calendar/tasks/{id}/checklist`（读取见 pim-api#482） <!-- drift-ignore: GET /api/v1/calendar/tasks/{id}/checklist --> |
| 确认中心 `/confirmations` | 主从面板、两步武装确认 | `/api/v1/operations/confirmations/*` |
| 快速记录 `/quick-notes` | 瀑布流卡片墙、可拖拽编辑弹窗、附件 | `/api/v1/quick-notes`、`/api/v1/quick-notes/attachments` |
| 文件 `/files` | 三栏浏览器（树 + 列表 + 预览）、上传队列、快照与单条恢复 | `/api/v1/files/*`（**没有回收站列表**，见 §11 第 7 条） |
| 手机记录 `/mobile-records` | 热力图、图表、时间线、设备存活视图 | `/api/v1/mobile/analytics/*`、`/api/v1/mobile/liveness/*` |
| 历史位置 `/location-history` | 地图轨迹、频繁地点、区段点 | `/api/v1/mobile/location/*` |
| 电脑记录 `/pc-tracker` | 热力图、时间线、生产力指标、分类建议 | `/api/v1/pc/*` |
| 提醒/报告/习惯 | 列表 + 规则表单 + 发送历史 | `/api/v1/calendar/reminders`、`/api/v1/calendar/reports`、`/api/v1/calendar/habits` |
| 设置（二级） | 账号、数据可信度、同步、AI、MCP、用户管理（文件没有回收站，见 §11 第 7 条） | `/api/v1/auth/*`、`/api/v1/data-reliability/*`、`/api/v1/ai/*`、`/api/v1/mcp/*`、`/api/v1/admin/*` |
| 状态 `/status` | 组件状态列表、采集质量 | `/api/v1/status`、`/api/v1/endpoints` |

## 10. 版本与兼容

- `GET /api/client/shell/latest`（匿名）：返回 `androidUrl/windowsUrl/shellAndroidUrl/shellWindowsUrl` 及各自版本号，
  用于「有可用更新」提示。**在契约里**（`ClientShellLatestResponse`，匿名）。
- `GET /api/version`（匿名）：API 版本信息，**在契约里**（`ApiVersionResponse`，匿名）。
- 客户端应在启动时缓存一次版本信息，失败不阻塞；更新只做提示，不强制中断（避免个人网络环境下锁死）。
- 弃用策略：契约里 `deprecated` 标记目前为 **0**（可用 `grep -c '"deprecated": true' contract/openapi.json` 复核）；
  下线端点前必须在 PR 与 release note 声明。机器化的「基线里有、当前没有」告警由 PR #480 的缺口审计引入
  （**未合入**）。在它合入前，`master` 上**没有**针对 OpenAPI 的自动化端点消失门禁；
  本 PR 的 `check-spec-drift.py` 只校验**本规格文档**引用的端点，不覆盖客户端源码或其它文档。

## 11. 已知缺口与未覆盖项（诚实清单）

这些是**工作回答「只给契约够不够」时发现的缺口**；本规格不臆测答案：

1. **契约未覆盖的部分**：
   - `GET /api/v1/calendar/tasks/{id}/checklist`：任务清单此前只有写路由，读取接口由 pim-api#482 补齐（合入前契约里没有这一条）； <!-- drift-ignore: GET /api/v1/calendar/tasks/{id}/checklist -->
   - `/health/live`、`/health/ready`、`/metrics`：运行时存在，但不在契约（运维用途，客户端不需要）；
   - `/mcp` 的 JSON-RPC / SSE 语义：契约里只声明了 `GET`/`DELETE`/`PATCH`，**没有 `POST`（JSON-RPC 主通道）**；
     运行时的 `POST`、`Mcp-Session-Id`、SSE 与 `40001`/`40101` 行为已在 **§8.4** 写清，客户端按该节实现，
     不要依赖 OpenAPI 里不存在的 operation；
   - `stats/upload`：源码里未被任何 API 业务路径引用（模块未加载），删除 PR 为 pim-api#483（**未合入**）；
     无论合入与否，**客户端不要调用**。 <!-- drift-ignore: /api/v1/stats/upload -->
2. **契约缺 requestBody**：`files/items/upload`（multipart）、`quick-notes/attachments` 等写操作在契约里没有
   `requestBody`，字段名只能从实现反推。本 PR **不逐项计数**（机器清单随审计 PR #480 提供）；
   除 §6.1 已写明的字段名外，**字段形状以 `contract/openapi.json` 的 `components.schemas` 为准**——
   已声明的 requestBody 不需要读任何源码，未声明的仅限这里列出的少数 multipart 端点。
3. **业务日 / 时区**未作为契约字段声明；客户端只能依赖服务端返回的日期字段（§5.1）。
   为按日端点补 `businessDate`/`businessTimeZone`/`businessDayStartHour` 的是 pim-api#484（**未合入**）。
4. **幂等 / 去重键**未在契约中声明（只在本规格 §7 按实现记录）；增量拉取与删除墓碑不存在。
5. **推送**：没有服务端推送端点（无 WebPush/FCM 对接）；通知依赖客户端轮询提醒队列 `GET /api/v1/calendar/reminders`。
6. **桌面壳更新通道**：Windows 守护进程/浏览器扩展的安装包分发与签名不在本规格范围内。
7. **规格漂移（已量化、可复现）**：给 `pim-web/frontend-rebuild-spec/05-api-reference/` 的每个文件顶部加一行
   `<!-- drift-base: /api/v1 -->`（写法见 `client-integration/README.md`），再用本仓
   `scripts/ci/check-spec-drift.py --spec <file>` 逐个检查。按该仓 2026-10 基线实测：机器报 **39 条**，
   人工复核后**确属契约缺失的 18 条**如下（其余 21 条是同一端点的简写、前端路由、磁盘路径或 `drift-base`
   本身被当成裸路径；检查器现在会把 8 个文件里的 `/api/v1` 基地址写法各报一次，所以总数比早期版本多）。
   下面每一条都是**契约里不存在**的 web 文档陈旧条目，本规格用 `drift-ignore` 显式豁免，便于逐条复核：
   - `files.md`（10 条）：
     - 版本历史四条：`GET /api/v1/files/items/{id}/versions` <!-- drift-ignore: /api/v1/files/items/{}/versions -->、
       `GET /api/v1/files/items/{id}/versions/{versionId}/download` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/download -->、
       `POST /api/v1/files/items/{id}/versions/{versionId}/restore` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/restore -->、
       `POST /api/v1/files/items/{id}/versions/{versionId}/restore-preview` <!-- drift-ignore: /api/v1/files/items/{}/versions/{}/restore-preview -->
       —— 契约里的对应概念叫 **snapshots**：`GET /api/v1/files/items/{id}/snapshots`、
       `POST /api/v1/files/items/{id}/snapshots/{snapshotId}/restore`（没有 download / restore-preview）；
     - 回收站两条：`GET /api/v1/files/trash` <!-- drift-ignore: /api/v1/files/trash -->、
       `POST /api/v1/files/trash/{id}/restore` <!-- drift-ignore: /api/v1/files/trash/{}/restore --> ——
       **契约里没有文件回收站**：只有 `POST /api/v1/files/items/{id}/restore` 恢复单条，
       没有「列出已删除项」的端点；
     - `POST /api/v1/files/items/{id}/index` <!-- drift-ignore: /api/v1/files/items/{}/index --> —— 契约里没有；
     - `POST /api/v1/files/providers/{id}/test` <!-- drift-ignore: /api/v1/files/providers/{}/test --> ——
       契约里只有 `GET /api/v1/files/providers/{id}/binding-status`；
     - `POST /api/v1/files/providers/nextcloud` <!-- drift-ignore: POST /api/v1/files/providers/nextcloud --> ——
       契约里**没有**这个字面路径（`{id}` 是 uuid，`nextcloud` 不是合法 id），也没有 nextcloud 专用新增端点；只有通用模板
       `DELETE /api/v1/files/providers/{id}`（删除绑定）。支持的提供方是 onedrive / google；
     - `GET /api/v1/providers/{id}/sync-status` <!-- drift-ignore: /api/v1/providers/{}/sync-status -->（漏写 `files/`）
       —— 真实路径 `GET /api/v1/files/providers/{id}/sync-status`。
   - `pc-tracker.md`（1 条）：`GET /api/v1/pc/aw/heatmap` <!-- drift-ignore: /api/v1/pc/aw/heatmap --> 已随遗留端点下线；
     现存 `GET /api/v1/pc/heatmap/grid` 与 `GET /api/v1/pc/aw/timeline`。
   - `calendar.md`（1 条）：`GET /api/v1/calendar/tasks/{id}/checklist` <!-- drift-ignore: GET /api/v1/calendar/tasks/{}/checklist -->
     —— 契约里只有 `POST`；读取接口由 pim-api#482 补（未合入）。
   - `mobile.md`（1 条）：`POST /api/v1/mobile/apps/catalog-overrides` <!-- drift-ignore: POST /api/v1/mobile/apps/catalog-overrides -->
     —— 契约里该路径只有 `GET` / `PUT`。
   - `operations-status.md`（4 条）：`GET /health/live`、`GET /health/ready`、`GET /metrics`
     <!-- drift-ignore: /health/live --> <!-- drift-ignore: /health/ready --> <!-- drift-ignore: /metrics -->
     （运行时存在、不在契约，见 §1）、`POST /api/v1/stats/upload` <!-- drift-ignore: /api/v1/stats/upload -->（见 §11.1）。
   - `quick-notes-mcp.md`（1 条）：`POST /mcp` <!-- drift-ignore: POST /mcp --> —— 契约 `/mcp` 只声明
     `GET`/`DELETE`/`PATCH`（见 §11.1）。
   - 复核后**不算漂移**的其余 21 条：8 个文件里的基地址写法 `/api/v1`（不是端点本身）；
     auth 文档开头的 `/login`、`/refresh`、`/register`、`/me`（真实路径都带 `/auth` 前缀，属同端点简写）；
     `quick-notes-mcp.md` 的 `mcp/clients`（同端点简写，真实路径 `/api/v1/mcp/clients`）；
     `operations-status.md` 的 `/inspection`、`/sections`（同端点简写，真实路径分别带 `data-reliability/`、`today/` 前缀）；
     `calendar.md` 里 `detailUrl` 的 `/confirmations/{id}`、`/reminders/{id}`（**前端路由**，不是 API）；
     `pc-tracker.md` 的 `/tree`（正文转述 `/pc/categories/tree` 的别名）；
     `operations-status.md` 里 web 前端的 SPA 兜底路由（形如 `/{*file}`，不是 API 端点；套上基地址后机器会把它当成
     基地址下的一段路径）与瓦片缓存**目录** `/data/pim/cache/tiles`（磁盘路径，不是路由）。
   **原生客户端应以本规格与契约为准**，不要照搬 web 规格里这些条目。
8. **业务码**：码表已经机器化，见 [`docs/business-error-codes.md`](business-error-codes.md)（§4）。
   OpenAPI 只声明 HTTP 状态码与 `ApiResponse` 信封，业务 `code` 以该表为准；表里没有的码按
   「未知码 → 通用失败 + 上报诊断」处理。

---

## 变更记录

| 日期 | 变化 |
|---|---|
| 2026-10-20 | 初稿：从 Web 重建规格抽取与框架无关的部分，按契约 `pim-api@87b9079` 校正，列出已知缺口 |
