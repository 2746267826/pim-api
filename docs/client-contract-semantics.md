# 客户端契约语义（破坏性变更）

> 本文件是 `contract/openapi.json` 里**表达不了、但对客户端有约束力**的三条语义的权威定义。
> 三条都是**有意破坏兼容**的决定（2026-10-20），理由与迁移方式见各节。客户端（web / android / windows）
> 必须在同批升级里按本文件改动；存量旧版本的回退方式见 §兼容窗口。

## 1. 业务日由服务端声明（Business day is server-declared）

**决定**：凡是「按日」的端点，服务端在响应里显式返回业务日口径；客户端**不得自行推算**业务日。

| 字段（所有按日端点响应，运行时真实下发） | 类型 | 含义 |
|---|---|---|
| `businessDate` | `string`（`yyyy-MM-dd`） | 该响应覆盖的业务日 |
| `businessTimeZone` | `string` | IANA ID，当前恒为 `Asia/Shanghai` |
| `businessDayStartHour` | `integer` | 业务日起算小时；**按分区/端点而定**（见下），不能假定恒为 `4` |

- **窗口语义**：业务日 D 覆盖 `[D businessDayStartHour, D+1 businessDayStartHour)`，
  时区为 `businessTimeZone`（左闭右开）。客户端必须用响应里的 `businessDate + businessDayStartHour`
  推算窗口，**不要**假定起算小时固定。
- **例外：`/api/v1/mobile/summary` 的 `businessDate` 是「标签」而不是「窗口」**。传 `date` 时取该业务日，
  传 `rangeStart` 时取起点所在业务日，都未传时为当前业务日；但未传日期/范围时服务端**不做时间过滤**
  （返回历史全量），此时它**不**表示数据只覆盖这个窗口。需要「今天的汇总」时必须显式传 `date`。
- **取值**：`0` = 日历日 `[00:00, 24:00)`（`/today/sections` registry、`calendar` / `reminders` 等分区）；
  `4` = PC 业务日 `[04:00, 次日 04:00)`（`pc.activity` / `pc.quality` 分区、`/api/v1/pc/summary`）。
  两者按分区各取各的：同一个 `/today/sections` 响应里 registry 的 `businessDayStartHour` 是 `0`，
  而 `pc.*` 分区自己的信封是 `4`。
- 权威实现：`Pim.Core/Common/BusinessDay.cs`（PC 业务日 D = `[D 04:00, D+1 04:00)`，Asia/Shanghai，左闭右开）；
  对外口径常量在 `Pim.Core/Common/BusinessDateEnvelope.cs`。
- **这三个字段是运行时字段，不是「只写进文档的占位」**：由 `TodaySectionRegistryDto`、`TodaySectionDto`、
  `PcSummaryResponse`、`MobileUsageSummaryResponse` 上的同名属性序列化而来，因此在契约里是 `required`。
  契约与运行时的一致性由 `tests/Pim.UnitTests/Contract/ContractSemanticsTests.cs` 双向断言
  （swagger 里必须有，真实响应里也必须有）。
- **破坏点**：此前契约里只有 `date-time`，客户端各自实现切日（Web 前端 7 处写死 `+08:00`、安卓用 UTC 取日），互相不一致。
- **客户端迁移**：删掉本地的「+08:00」「8 * 3600_000」类常量与 `DateTime.Today`；日期展示、日期选择、今日/本周范围
  一律取服务端返回值。离线时用「最后一次收到的 `businessDate` + `businessDayStartHour`」按同算法推算，并在 UI 上标注可能不准。
- **服务端范围**（当前全部）：`/api/v1/today/sections`、`/api/v1/today/sections/{sectionId}`、
  `/api/v1/pc/summary`、`/api/v1/mobile/summary`。端点清单在
  `BusinessDayFieldsDocumentFilter.Endpoints` 里集中维护；端点被改名/删除时 swagger 生成**直接失败**，
  不会静默少字段。
- **PC 业务日**：`pc.activity` / `pc.quality` 两个分区按 PC 业务日（04:00 切日）取数，
  所以 `/today/sections/{pc.*}` 信封上的 `businessDate` **等于载荷里的 `businessDate`**，可能比同一次请求
  里其它分区的 `businessDate` 早一天（例如 02:00 请求）。registry 同时给出 `date`（日历日）与
  `pcBusinessDate`（PC 业务日），客户端不要假设三者总相等。

## 2. 写操作幂等（Idempotent writes）

**决定**：**使用 JWT 认证**的写操作（POST/PUT/PATCH/DELETE）接受可选请求头 `Idempotency-Key`
（客户端生成，建议 UUIDv4，同一逻辑操作重试时必须复用同一个 key）。服务端语义：

1. 键的作用域是「用户 + 方法 + 路径模板 + key」；不同用户之间互不影响。
   **注意模板作用域的含义**：`/tasks/{a}` 与 `/tasks/{b}` 落在**同一条**记录上，
   因此**每一次逻辑操作都必须生成新 key**，不能拿一个 key 去操作多个资源（否则第二个请求会拿到第一个的响应而不执行）。
2. 首次请求成功后，把**该次的成功响应**缓存 24 小时：状态码 + 响应体 + 需要随重放返回的响应头
   （`Location` / `ETag` / `Last-Modified`）。响应体上限 1 MiB：**超过上限的响应不缓存**
   （占位随即释放，同 key 重试会真正重新执行）。不能只缓存状态码 —— 那会让重放返回「2xx + 空正文」，
   客户端既反序列化失败，也无法用同一个 key 重新执行。
3. 24 小时内同 key 重放：返回缓存的响应，不重复执行副作用；响应头 `Idempotent-Replayed: true` 标明这是重放。
4. 首次请求仍在执行中时重放：返回 `409` + `code=40902`（契约里已为该端点声明 409），客户端应退避后重试。
   **同一个 409 可能有两种 body**：端点自身的业务 409（例如 Outlook writeback）与这里的 `{code:40902}`。
   契约里两者共用一个状态码，**schema 声明为 `oneOf: [业务响应, StringApiResponse]`**，客户端必须按 body 的
   `code` 区分。
   `Idempotency-Key` 超过 128 个字符时返回 `400` + `code=40010`（独立码，不复用 40002）。
   该 400 已对全部 161 个参与幂等的 operation 声明：原本缺 400 的 **12 个**新增声明，
   已有业务 400 的端点直接复用既有的 `StringApiResponse`（两种 400 恰好同一 schema，因此**不做** `oneOf`）；
   只有新增 400 的这 12 个 operation 会在 400 描述里写明 40010。
5. 首次请求失败（非 2xx）：不缓存，客户端可以用同一个 key 重试，服务端会重新执行。
6. 读操作忽略该请求头。
7. **只有唯一键冲突（PostgreSQL 23505）才会被解释为「另一个请求正在执行」**；其它存储错误（含接管过期行
   失败）一律降级为「没有幂等」并把请求正常放行 —— 不能把一次数据库抖动伪装成「另一个请求在执行」。
8. 「执行中」占位是**短租约**（`InProgressLease` = 10 分钟），成功写回时才延到 24 小时。所以进程在
   占位之后、写回之前被杀掉时，同一个 key 最多被锁 10 分钟，之后会被新的请求**接管**并重新执行；
   另有每小时一次的后台清理任务（`idempotency-cleanup`）删除过期行。
9. **租约围栏是原子的**：写回（`TryCompleteAsync`）与释放（`TryReleaseAsync`）都是**单语句条件更新/删除**，
   条件为 `id = ? AND state = 'in_progress' AND expires_at = <本次租约>`；被接管后旧执行者影响 0 行，
   不会覆盖或删除新属主的占位（`expires_at` 即 fencing token，无需额外列）。

**不参与幂等的端点**（契约里也不会声明该请求头，见 `IdempotencyKeyDocumentFilter`）：

- 匿名端点（`/api/v1/auth/login|register|refresh`、`/api/v1/mcp/verify` 等）与 OpsKey 端点（`/api/v1/ops/*`）：
  没有幂等作用域。判定依据是**端点元数据**（`IAllowAnonymous`，或没有任何 `IAuthorizeData`），
  **不是**当前请求有没有 JWT 用户 —— `UseAuthentication` 在匿名端点上也解析 `Authorization`，
  否则带着仍有效 access token 调 login/refresh 会把返回的新 token 缓存 24 小时；
- 响应里含**一次性凭据**的端点：缓存响应体等于把凭据落库 24 小时，且凭据过期后的重放会伪装成成功。
  当前清单（`IdempotencyMiddleware.NonReplayableRoutes`，新增同类端点必须同步加进去）：
  - `POST /api/v1/mcp/clients`（明文 token）；
  - `POST /api/v1/files/items/upload-session`（预授权上传 URL，分片 PUT 不带 Authorization）；
  - `POST /api/v1/files/providers/onedrive`（设备码 user code + verification URI）；
  - `POST /api/v1/files/providers/google/authorize`（带一次性 state 的授权地址）；
  - `POST /api/v1/files/items/{id}/share`（分享链接 `webUrl` 是可直接访问文件的 bearer 能力链接；
    服务层明确不写日志/审计，缓存等于明文落库；需要时用 `GET /api/v1/files/items/{id}/shares` 重新取）；
  - `POST /api/v1/calendar/outlook/device-code`（设备码 user code）；
  - `POST /api/v1/calendar/outlook/device-code/poll`（**有状态**轮询：`waiting-for-user` → `authorized`，
    每次都返回同一个 user code；缓存 24 小时会让客户端永远看到首次的等待状态，同样按一次性凭据处理）；
- JSON 404 兜底 `/api/{*path}`（文档里写作 `/api/{path}`）：不是业务端点，不写幂等记录，
  避免把 404 缓存 24 小时。

- **破坏点**：此前契约里没有任何重试去重约定，离线客户端重发会制造重复数据（手机端历史上出现过死循环重复上报）。
- **客户端迁移**：上传/采集类重试、用户可重复点击的「创建/确认」按钮，都必须带 key；重试时复用同一个值。
- 存储：服务端用独立表 `idempotency_records` 保存键与响应快照；簿记使用**独立 DbContext scope**，
  不会把业务 handler 未提交的变更顺带提交，也不会把业务请求的成败绑在幂等写库上（失败时降级并记日志）。

## 3. 单一响应形状（One shape per endpoint）

**决定**：同一路径+方法只返回**一种** `data` 形状。分页端点一律返回 `PagedResult<T>`，无例外。

- 现行 `GET /api/v1/calendar/events` 与 `GET /api/v1/calendar/tasks` 存在「不带分页/过滤参数 → 返回裸数组」的兼容分支；
  **本变更移除该分支**，任何调用都返回 `PagedResult<T>`，默认 `page=1`、`pageSize=50`（`pageSize` 有上限）。
- **影响面比「不带任何参数」更大**：旧分支的判断条件只看了 `search/calendarId/page/pageSize`，
  **完全不看 `start`/`end`**。所以最常见的日历视图 `GET /api/v1/calendar/events?start=…&end=…`（不带 page）
  以前返回**数组**，现在返回 `PagedResult` —— 这是升级时最容易炸的一处。
- **默认只返回 50 条**：不翻页的调用会静默丢数据。任何「取全量」的场景都必须按 `totalCount` 翻页。
- **破坏点**：Web 的「收件箱侧板」与安卓的「任务列表」目前依赖裸数组分支（`listAll`、`getTasks(inbox)`）；
  事件范围查询（见上）同样受影响。
- **客户端迁移**：
  - Web：删除 `tasksApi.listAll`，收件箱改为 `listPaged({ inbox: true, pageSize: 100 })` 并翻页到 `totalCount`；
  - Android：`getTasks(inbox)` 改为带 `page`/`pageSize` 的分页调用，按 `totalCount` 翻页；
  - 所有 `GET /calendar/events` 调用（含只带 `start`/`end` 的）都按 `PagedResult` 解析并翻页；
  - 任何「无参获取全量」的用法都会在升级后得到对象而不是数组——**必须同批发版**。

## 兼容窗口（rollout window）

- 三条变更同批部署；**不做**运行时开关或双写。旧客户端在升级前必须完成对应改动。
- 发布顺序：pim-api（本仓）→ 客户端三仓同批发布；服务端发版说明里列出本文件链接。
- 回滚：仅回滚 pim-api 版本即可恢复旧行为（旧版本不含本变更）。

## 回归门禁

- **本 PR 自带**：`tests/Pim.UnitTests/Contract/ContractSemanticsTests.cs` 断言
  - 四个按日端点的 schema 与**真实响应**都含业务日三字段，且 `businessDate` 与既有 `date` 同源；
  - `Idempotency-Key` 只出现在运行时真的会生效的写操作上，且写操作声明了 409 与 `Idempotent-Replayed`；
  - 分页端点只声明一种成功形状。
- `tests/Pim.UnitTests/Api/IdempotencyRealDbTests.cs` 在**真实 PostgreSQL**（`PIM_TEST_CONN`）上验证
  并发同 key 只执行一次、过期记录可被接管；`SingleResponseShapeE2ETests` 钉住「只带 `start`/`end`」的事件查询形状。
- **跨 PR 依赖**：仓库级的「契约缺口审计」门禁（`scripts/ci/audit-contract-gaps.sh` +
  `docs/contract-baseline.json`）由 PR #480 引入，本分支尚未包含；#480 合入后，
  按日端点缺字段 / 写操作缺 `Idempotency-Key` 会被该门禁拦下。
