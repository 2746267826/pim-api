# 手写模型 vs 契约：偏差核对报告 / Hand-written models vs contract: deviation report

- 基线仓：`pim-web@origin/master` = `16c7b5a41f6e0f108465575c693e365392d4127c`
- 契约基线：`pim-api@master` = `940fe42face873e48c7c36af030d8ca838791471`（938271 字节，sha256 `4dd7eafb…af53`）
- 本轮范围：**只报告，不修**。不改任何手写 DTO / 产品逻辑 / 路由 / 鉴权。
- 状态词只用：`PASS` / `FAIL` / `BLOCKED` / `NOT-VERIFIED` / `APPROVED-EXCEPTION`。

证据分级：

- **[契约字节]** 可直接在新快照里复核。
- **[源码]** 本仓源码行级可复核。
- **[运行期] NOT-VERIFIED** 需要真实 API 响应 / 真实设备才能判定，本 PR 不外呼、不结论。

---

## 0. 本报告明确不主张的事

- `client-web/src/api/generated.ts` 当前**没有任何产品代码 import**（`git grep "api/generated" client-web/src` 为空）。
  因此 `tsc` / `vite build` / `vitest`（146 用例）全绿**不能**证明「手写模型符合契约」，它们只能证明
  「本次刷新没有破坏既有回归」。契约符合性由 `web/scripts/ci/check-contract.sh` 的「从根契约重生成并逐字节比对」+ 本报告的人工逐条比对负责。
- 「客户端统一注入 Bearer」只能支撑「客户端默认带 Bearer 与契约声明一致」，**不能**推出逐端点的授权语义正确。
- 本报告不修改产品行为，出现运行期风险时也不在本次修复。

---

## 1. 快照级事实（[契约字节]）

| 项 | old（`origin/master` 已提交快照） | new（本次刷新） |
|---|---|---|
| 字节 / sha256 | 258542 / `1cf261973833…1aec98` | 938271 / `4dd7eafb4938…f8d108c4f82af53` |
| pathItem / operation | 272 / 315 | 272 / 315（**增 0 删 0**） |
| component schemas | 112 | 564（+452） |
| 带 security 的 operation | 0 | 299 |
| 带响应 content 的 operation | 0 | 312 |

共有 schema（112 个）的字段级差异（本报告自行重算，方法：递归展开 `properties`）：

| 差异类别 | 数量 | 说明 |
|---|---|---|
| `nullable: true` 被移除 | 165 | 缺省即 `false`；**没有任何 false→true**，也没有显式改成 `false` 的新增字段 |
| schema 的 `required[]` 增加条目 | 102 | **没有任何 schema 减少 required 条目** |
| 新增字段级 `enum` | 4 | `DataCenterBatchOperationRequest.action`、`RestoreQuickNoteRequest.status`、`UpdateQuickNoteRequest.status`、`UpdateUserRoleRequest.role` |
| `format` 变化 | 1 | `RegisterRequest.email` 增加 `format: email` |

方向一致：old 快照是「宽松/不完整」的导出，new 快照是完整导出，**不是后端把字段改窄**。

---

## 2. 逐条偏差（每条给出命中类别 + 落到 `METHOD /path`）

命中类别取自：**字段名 / 类型 / nullability / enum / security**。状态只对本轮可判定的部分给结论。

### W-1 `rrule` ↔ `rRule`（字段名）— **FAIL**（确认偏差，本轮不修）

| 方向 | 本仓位置 | 契约 | 影响的 operation |
|---|---|---|---|
| 写（上行） | `features/calendar/components/event-editor-dialog.tsx:198`（提交 `rrule`）、`:142`、`:166` | 请求体 `CreateEventRequest.rRule` / `UpdateEventRequest.rRule` | `POST /api/v1/calendar/events`、`PUT /api/v1/calendar/events/{id}`、`POST /api/v1/calendar/outlook/events/writeback` |
| 读（下行） | `features/calendar/types.ts:45`（`rrule: string \| null`）、`calendar-page.tsx:175`、`event-list-model.ts:98`、`entity-detail-dialogs.tsx:158`、`settings/pages/settings-pages.tsx:958` | 响应 `EventResponse.rRule`（`{nullable:true, type:string}`） | `GET /api/v1/calendar/events`（分页 `items[]`）、`POST /api/v1/calendar/events`（201）、`PUT /api/v1/calendar/events/{id}`（200）、`POST /api/v1/calendar/outlook/events/writeback`（`OutlookWriteResult.event` / `latestEvent`） |

- 契约里**全 schema 无 `rrule`**（只有 `rRule`）。[契约字节]
- 契约里 `/api/v1/calendar/events/{id}` **只有 `PUT` / `DELETE`，没有 `GET`**；单事件读取走 `GET /api/v1/calendar/events`（分页）。[契约字节]
- **写方向**：ASP.NET Core 的模型绑定默认大小写不敏感 → 大概率仍能生效。**未验证**，标 `NOT-VERIFIED`。
- **读方向**：这是与写方向**不同**的风险 —— 响应键是 `rRule`，客户端读 `event.rrule`。是否真的拿到 `undefined`
  取决于服务端序列化器是否输出 camelCase 之外的别名。**需要真实响应样例才能判定 → `NOT-VERIFIED`**。
  一旦成立，后果是「重复规则不显示 / 误判为单次事件」（纯前端展示层错误）。
- 附带：`scripts/mock-api.mjs` 用的是 `rrule`，与真实 API 不一致（mock 会掩盖读方向问题）。

### W-2 `nullable` vs `required` / 键缺省（nullability）— **口径澄清，非产品缺陷**

契约里两者是**不同概念**，不能互相替代：

- `nullable: false` 只禁止**显式 `null`**；
- `required` 决定**键是否可以缺失**；`required` 不含某字段时，该键可以整个缺席。

本仓手写类型普遍写 `X | null`（例：`features/pc/types.ts` 的 `keystats: KeystatsSummary | null`、
`features/calendar/types.ts:61,62` 的 `organizer: EventPerson | null` / `attendees: EventPerson[] | null`）。
契约里这些字段是 `nullable:false` + `required:false`：

- 客户端类型更宽（允许 null）→ 只是**比契约宽松**，不会因为契约变化而崩；`X | null` 覆盖了「显式 null」，
  但**没有**建模「键缺失」与「显式 null」的区别（TS 里两者都是 `undefined` / `null` 两种态）。
- 本轮 165 条 `nullable:true → 移除`是**导出完整化**（缺省即 false），不是「后端新增了必填」。

状态：`PASS`（口径已澄清，无运行期阻断结论）；「键缺失」在运行期的实际影响 `NOT-VERIFIED`。

### W-3 `DataCenterItem.objectType` 封闭联合（enum，客户端过严）— **FAIL**（记录，不修）

- 本仓：`features/calendar/types.ts:300-311` 定义 11 个字面量联合 `DataCenterObjectType`，`:315` 用它标注 `DataCenterItem.objectType`。
- 契约：`DataCenterItem.objectType` = `{type: string}`，**没有 enum**。[契约字节]
- 命中类别：**enum**。影响 operation：`POST /api/v1/calendar/data-center/query`。
- 性质：客户端**比契约更严**。契约未声明枚举时，服务端返回第 12 种取值才会暴露问题 →
  **运行期 `NOT-VERIFIED`**，本轮不判为产品 FAIL。

### W-4 `PcSummaryResponse.appRanking` 缺失（字段名 / 覆盖缺口）— **FAIL**（记录，不修）

- 契约：`PcSummaryResponse.required = [appRanking, categories, heatmap, sessions, timeline]`，`appRanking` 是 `AppRankingItem[]`。[契约字节]
- 本仓：`features/pc/types.ts:71-89` 的 `PcSummaryResponse` **没有 `appRanking`**（该文件里也没有）。
  `appRanking` 只出现在**另一个**手写类型里：`embed/android/android-embed-pages.tsx:140`（Android 嵌入页的独立结构）。
- 命中类别：**字段名**（响应字段只声明子集）。影响 operation：`GET /api/v1/pc/summary`。
- 影响：PC 页拿不到应用排行数据；属覆盖缺口，不是形状破坏。

### W-5 `KeystatsSummary.topKeys` 可选性不一致（nullability / 覆盖缺口）— **FAIL**（记录，不修）

- 契约：`KeystatsSummary.required` **包含** `topKeys`（`KeyCountItem[]`）。[契约字节]
- 本仓：`features/pc/types.ts:21` 写成 `topKeys?: { keyName; count; share }[]`（可选）。
- 命中类别：**nullability**（required 缺失口径）。影响 operation：`GET /api/v1/pc/summary`（`keystats`）。
- 影响：客户端多写了 `?`，比契约宽松；运行期是否真的总是存在 → `NOT-VERIFIED`。

### W-6 `EventResponse.attendees` 元素类型（类型）— **NOT-VERIFIED**

- 契约：`EventResponse.attendees` = `EventAttendeeDto[]`（`nullable:true`），元素的 `required = [email]`，
  另有 `name`（nullable）与 `type`。[契约字节]
- 本仓：`features/calendar/types.ts:29` 的 `EventPerson`（`name?` / `email?` / `type?`，**全部可选**），`:62` 用它标注 `attendees`。
- 命中类别：**类型 / nullability**。影响 operation（响应含 `EventResponse`）：`GET /api/v1/calendar/events`、
  `POST /api/v1/calendar/events`、`PUT /api/v1/calendar/events/{id}`、`POST /api/v1/calendar/outlook/events/writeback`。
- 形状疑似等价（差值主要是「可选 vs required」），但**需要真实响应样例才能判定字段是否一一对应 → `NOT-VERIFIED`**。

### W-7 `EventResponse` 响应字段只声明子集（字段名 / 覆盖缺口）— **FAIL**（记录，不修）

- 本仓 `EventResponse`（`features/calendar/types.ts:36` 起）相对契约缺少 5 个属性：
  `attachmentReferences`、`exDatesJson`、`outlookAdditionalInfo`、`rRule`、`recurrenceMetadataJson`
  （其中 `rRule` 就是 W-1）。[契约字节] + [源码]
- 命中类别：**字段名**。影响 operation：同 W-6 的 GET/写回系列。
- 影响：纯覆盖缺口（少读），不破坏已有字段。

---

## 3. 明确排除（不得读成缺口）

| # | 现象 | 排除理由 |
|---|---|---|
| E-1 | TS `number` ↔ 契约 `integer` / `int32` | **口径等价**。TS 没有整数类型，`number` 是正确的建模；`format` 只用于文档。 |
| E-2 | `Record<string, number>` / `Map<>` ↔ 契约 `type: object` + `additionalProperties` | **口径等价**。`MobileQualityComponentDto.details` 一类属此，不是 typeMismatch。 |
| E-3 | 字符串 enum 的 `$ref` ↔ 客户端 `string` / `String` | **口径等价**。契约侧 enum 的 `$ref` 解析后就是 string；客户端用 `string` 是宽松不是错。`DaemonHeartbeatRequest.activityWatchState/keyStatsState` 属此。 |
| E-4 | 嵌套对象被客户端「抬升」/扁平化（`PcSummaryResponse.metrics`、`keystats`） | **解析器口径**：客户端把嵌套结构拆成独立 interface，与契约同一形状，不是字段缺失。 |
| E-5 | `TaskResponse.checklistItems`（`features/calendar/types.ts:102-103`，`task-editor-dialog.tsx:127`、`entity-detail-dialogs.tsx:263` 消费） | **文档化、知情的前端可选扩展**，不是契约漂移缺陷。源码注释即写明「前端可选声明；后端 TaskResponse 不返回（05/calendar.md 备注）」；仓内规格 `frontend-rebuild-spec/05-api-reference/calendar.md:353,495-501` 记录同一结论，组件空态文案也写明「后端 TaskResponse 不返回清单项」。**契约事实**保留为陈述：`TaskResponse` 无该字段、`GET .../checklist` 在读方向不存在（只有 `POST /api/v1/calendar/tasks/{id}/checklist`、`PUT`/`DELETE /api/v1/calendar/tasks/{id}/checklist/{itemId}`）。是否补读取端点属**产品/需求决策**，须用户确认后另开独立工作。 |
| E-6 | `MobileQualityResponse` / `PcQualityResponse` 的 `issues/components/nextSteps` | **解析器误报**：客户端内联对象字面量与契约 `$ref` 指同一形状，已剔除。 |
| E-7 | 客户端「比契约更严」的封闭联合（W-3） | 契约未声明 enum 时**不判产品 FAIL**；仅在服务端真返回新取值时才是运行期问题 → `NOT-VERIFIED`。 |

---

## 4. security 维度

契约事实 [契约字节]：

- 315 个 operation 中 **299 个带 `security`**：`bearerAuth` 292 个 + **`opsKey` 7 个**；16 个匿名（无顶层 `security`）。
- `opsKey` 的 7 个 operation 全在 `/api/v1/ops/*`：`GET /api/v1/ops/db/describe`、`POST /api/v1/ops/db/query`、
  `GET /api/v1/ops/db/tables`、`GET /api/v1/ops/health`、`GET /api/v1/ops/logs/files`、`GET /api/v1/ops/logs/query`、
  `GET /api/v1/ops/logs/tail`（用 `X-PIM-Ops-Key` 头）。

本仓消费面（可复算的方法：抽取 `client-web/src/**/*.ts(x)`（排除 `generated.ts`）里的 `/api/...` 路径字面量，
把模板变量归一化后与契约 `paths` 做正则匹配）：

- 179 个不同字面量 → 命中 **169 个 operation**，其中 **164 个带 security、5 个匿名**。
- 5 个匿名 operation 是：`POST /api/v1/auth/login`、`POST /api/v1/auth/register`、`POST /api/v1/auth/refresh`、
  `GET /api/version`、`GET /api/v1/tiles/{z}/{x}/{y}.png`。
- 客户端显式 `auth: false` 的三处 —— `api/auth.ts:14`（login）、`api/auth.ts:18`（register）、`api/version.ts:12`（version）
  —— **正好都是契约里的匿名端点** → 无「契约要求鉴权而客户端匿名调用」的错配。`PASS`。
- **`opsKey`：本仓对这 7 个 operation 零调用** → 无缺口，也无覆盖。`PASS`。

明确写出的不确定性：

- `/api/v1/pc/*` 在本契约里 **68/68 全部带 `bearerAuth`、匿名 0**；而上游 `docs/contract-mapping.md` 声称该读组「当前无鉴权」。
  两者冲突，本报告**以契约字节为准**，但**未读 pim-api 的模块注册源码逐行确认**该文档是滞后还是契约过度标注 →
  这是一条**显式不确定**（`NOT-VERIFIED`）。
- 「客户端统一注入 Bearer」不等于「逐端点授权语义验证通过」。鉴权/授权的运行期正确性不在本设施能力范围内。

---

## 5. 运行期未验证清单（`NOT-VERIFIED`，不得声称已验证）

| # | 项 | 需要什么才能判定 |
|---|---|---|
| NV-1 | W-1 **读方向**：服务端是否真的只输出 `rRule`（`event.rrule` 是否为 `undefined`） | 真实 API 响应样例 |
| NV-2 | W-1 **写方向**：`rrule` 是否被大小写不敏感绑定接受 | 真实 API 调用回读 |
| NV-3 | W-3：服务端是否返回 11 值之外的第 12 种 `objectType` | 真实数据 |
| NV-5 | W-5/W-6：`topKeys`、`attendees` 元素的真实存在性与形状 | 真实响应样例 |
| NV-6 | §4 的 `/api/v1/pc/*` 文档/契约冲突 | 读 pim-api 模块注册源码或真实 401/200 观测 |

---

## 6. 结论

- 端点集合零变化（315→315）→ **没有断链要补**，本次刷新不引入也不删除任何接口。
- 手写模型与契约的真实偏差集中在 **字段名**（W-1 `rrule`、W-4 `appRanking`、W-7 子集缺口）、
  **可选性口径**（W-5、W-2）与 **enum 过严**（W-3）；其中只有 W-1 有潜在运行期影响，且**读/写方向风险不同**。
- **本轮只报告不修**：修 W-1/W-3/W-5 会改变产品行为与已确认体验，须另开独立工作并重新评审。
- 本报告不构成「模型已符合契约」的结论；`generated.ts` 未被产品代码引用，构建/单测全绿只等于「未破坏回归」。
