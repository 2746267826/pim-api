# 客户端契约语义（破坏性变更）

> 本文件是 `contract/openapi.json` 里**表达不了、但对客户端有约束力**的三条语义的权威定义。
> 三条都是**有意破坏兼容**的决定（2026-10-20），理由与迁移方式见各节。客户端（web / android / windows）
> 必须在同批升级里按本文件改动；存量旧版本的回退方式见 §兼容窗口。

## 1. 业务日由服务端声明（Business day is server-declared）

**决定**：凡是「按日」的端点，服务端在响应里显式返回业务日口径；客户端**不得自行推算**业务日。

| 字段（新增，所有按日端点响应） | 类型 | 含义 |
|---|---|---|
| `businessDate` | `string`（`yyyy-MM-dd`） | 该响应覆盖的业务日 |
| `businessTimeZone` | `string` | IANA ID，当前恒为 `Asia/Shanghai` |
| `businessDayStartHour` | `integer` | 业务日起算小时，当前恒为 `4` |

- 权威实现：`Pim.Core/Common/BusinessDay.cs`（业务日 D = `[D 04:00, D+1 04:00)`，Asia/Shanghai，左闭右开）。
- **破坏点**：此前契约里只有 `date-time`，客户端各自实现切日（Web 前端 7 处、安卓用 UTC 取日），互相不一致。
- **客户端迁移**：删掉本地的「+08:00」「8 * 3600_000」类常量与 `DateTime.Today`；日期展示、日期选择、今日/本周范围一律取服务端返回值。
- **服务端范围**：`/api/v1/today/sections`、`/api/v1/today/sections/{id}`、`/api/v1/pc/summary`、`/api/v1/mobile/summary`
  等按日端点——具体清单以契约中响应含 `businessDate` 的端点为准（由 CI 的契约缺口审计保证）。

## 2. 写操作幂等（Idempotent writes）

**决定**：所有写操作（POST/PUT/PATCH/DELETE）接受可选请求头 `Idempotency-Key`（客户端生成，建议 UUIDv4，
同一逻辑操作重试时必须复用同一个 key）。服务端语义：

1. 键的作用域是「用户 + 方法 + 路径模板 + key」；不同用户或不同端点之间互不影响；
2. 首次请求成功后，把**该次的成功响应**（状态码 + 响应体）缓存 24 小时；
3. 24 小时内同 key 重放：**原样返回缓存的响应**，不重复执行副作用；
4. 首次请求仍在执行中时重放：返回 `409` + `code=40902`（`IdempotentRequestInProgress`），客户端应退避后重试；
5. 首次请求失败（非 2xx）：不缓存，客户端可以用同一个 key 重试，服务端会重新执行；
6. 不接受 key 的读操作忽略该请求头。

- **破坏点**：此前契约里没有任何重试去重约定，离线客户端重发会制造重复数据（手机端历史上出现过死循环重复上报）。
- **客户端迁移**：上传/采集类重试、用户可重复点击的「创建/确认」按钮，都必须带 key；重试时复用。
- 存储：服务端用独立表保存键与响应快照，读写失败不得影响主操作（失败时按「不缓存」降级并记日志）。

## 3. 单一响应形状（One shape per endpoint）

**决定**：同一路径+方法只返回**一种** `data` 形状。分页端点一律返回 `PagedResult<T>`，无例外。

- 现行 `GET /api/v1/calendar/events` 与 `GET /api/v1/calendar/tasks` 存在「不带分页/过滤参数 → 返回裸数组」的兼容分支；
  **本变更移除该分支**，任何调用（含不带参数的调用）都返回 `PagedResult<T>`，默认 `page=1`、`pageSize=50`。
- **破坏点**：Web 的「收件箱侧板」与安卓的「任务列表」目前依赖裸数组分支（`listAll`、`getTasks(inbox)`）。
- **客户端迁移**：
  - Web：删除 `tasksApi.listAll`，收件箱改为 `listPaged({ inbox: true, pageSize: 100 })` 并翻页到 `totalCount`；
  - Android：`getTasks(inbox)` 改为带 `page`/`pageSize` 的分页调用，按 `totalCount` 翻页；
  - 任何「无参获取全量」的用法都会在升级后得到对象而不是数组——**必须同批发版**。

## 兼容窗口（rollout window）

- 三条变更同批部署；**不做**运行时开关或双写。旧客户端在升级前必须完成对应改动。
- 发布顺序：pim-api（本仓）→ 客户端三仓同批发布；服务端发版说明里列出本文件链接。
- 回滚：仅回滚 pim-api 版本即可恢复旧行为（旧版本不含本变更）。

## 回归门禁

- `scripts/ci/audit-contract-gaps.py` 在契约中缺少以下任一，审计直接报 **P1**：
  - 按日端点的响应 schema 没有 `businessDate`（清单见 `docs/contract-baseline.json` 的 `businessDayEndpoints`）；
  - 写操作没有声明 `Idempotency-Key` 参数（清单除外见 `idempotencyExempt`，必须逐条写理由）；
  - 同一路径+方法存在多个不同 `data` 形状（由导出结果与 describe 文本比对）。
