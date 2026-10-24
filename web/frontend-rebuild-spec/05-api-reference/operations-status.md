# 运营治理、数据可信度、系统状态与杂项接口规格（operations / data-reliability / status / daemon / endpoints / today / search / tiles / version / health / metrics）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域说明：operations / data-reliability / status / daemon / endpoints / today / search 路由组整体要求 JWT（各文件 `RequireAuthorization()`）；tiles、version、client-shell、health* 为匿名；metrics 要求 Admin 或 OpsKey。枚举序列化：`DaemonSourceState` 标注 `JsonStringEnumConverter` 序列化为字符串（OperationEnums.cs:63-70）；其余枚举（`PimHealthStatus`、`StatusComponentKind`、`OperationConfirmationStatus`、`OperationRiskLevel`）无转换器，按数字序列化（前端 status.ts:54-62 对健康状态做了数字→字符串归一化，见各节备注）。`DomainException` 经全局中间件映射：40401/4004/4006/5104/5300/5304/5305→404、40101→401、40301~40303→403、42901→429、50301→503、其余（含 3001/3005/01002/02056）→400（ExceptionMiddleware.cs:120-128）。

---

## 运营确认（Operations / Confirmations）

### GET /api/v1/operations/confirmations/pending
- 用途：列出当前用户名下的全部待确认操作。
- 认证：JWT（operations 组整体 `RequireAuthorization()`，OperationsEndpoints.cs:13）
- Web 前端使用：是（确认中心页 /confirmations、工作台页、今日页待确认节 TodayOpsSections）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`OperationConfirmationDto[]`（ConfirmationDtos.cs:26-53）
  | 字段 | 类型 | 说明 |
  | id | string (UUID) | 确认记录 ID |
  | requestedByUserId | string\|null (UUID) | 请求发起用户（为 null 表示系统发起，所有用户可见） |
  | operationType | string | 操作类型 |
  | summary | string | 操作摘要 |
  | riskLevel | number | 风险等级枚举：`Low=0` \| `Medium=1` \| `High=2` \| `L0AutomaticArtifact=10` \| `L1LowRiskAction=11` \| `L2PimFactChange=12` \| `L3ExternalSourceOrWriteback=13` \| `L4BatchOrDestructiveGovernance=14`（OperationEnums.cs:51-61） |
  | source | string | 来源 |
  | payloadJson | string | 操作负载 JSON |
  | previewJson | string | 预览 JSON |
  | status | number | 状态枚举：`Pending=0` \| `Confirmed=1` \| `Rejected=2` \| `Expired=3` \| `Executed=4`（OperationEnums.cs:42-49） |
  | expiresAt | string (ISO-8601) | 过期时间 |
  | createdAt | string (ISO-8601) | 创建时间 |
  | confirmedAt | string\|null (ISO-8601) | 确认时间 |
  | executedAt | string\|null (ISO-8601) | 执行时间 |
  | resultJson | string\|null | 执行结果 JSON |
  | correlationId | string\|null | 关联 ID |
  | changedFields | string[]\|null | 变更字段列表 |
  | allowedActions | string[]\|null | 允许的动作列表 |
  | objectType | string\|null | 关联对象类型 |
  | objectId | string\|null (UUID) | 关联对象 ID |
  | requiresSecondLevelConfirmation | boolean | 是否需二级确认 |
  | beforeJson | string\|null | 变更前快照 |
  | afterJson | string\|null | 变更后快照 |
  | requiresStrictConfirmation | boolean | 是否需严格确认 |
  | auditBatchId | string\|null (UUID) | 审计批次 ID |
  | aiRecommendation | string\|null | AI 建议 |
  | externalEffect | string\|null | 外部影响说明 |
  | recoveryPath | string\|null | 恢复路径 |
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:15`；前端 `src/client-web/src/api/operations.ts:40`（路径 operations.ts:11-13）、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:43`、`src/client-web/src/pages/WorkbenchPage.tsx`、`src/client-web/src/components/today/TodayOpsSections.tsx:80`
- 备注（前后端差异）：前端类型 `OperationConfirmation`（types/index.ts:672-701）把 riskLevel/status 声明为 PascalCase 字符串（types/index.ts:655-670），后端实际输出数字；确认页 `riskLevel === 'L4BatchOrDestructiveGovernance'` 判断（ConfirmationsPage.tsx:66）对数字值恒为 false，重建前端时需以后端数字为准或加归一化。

### GET /api/v1/operations/confirmations/{id}
- 用途：查询单条确认记录详情。
- 认证：JWT
- Web 前端使用：是（确认中心页详情抽屉）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 确认记录 ID（路由约束 `{id:guid}`） |
- Query 参数：无
- Body：无
- 响应 data：`OperationConfirmationDto`（字段同 pending 列表项）
- 错误：不存在抛 `DomainException(3001)` → HTTP 400 code=3001 `Confirmation record does not exist.`（OperationsEndpoints.cs:31）；已分配他人（requestedByUserId 非空且不等于当前用户）抛 `DomainException(3005)` → HTTP 400 code=3005（OperationsEndpoints.cs:33-36）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:24`；前端 `src/client-web/src/api/operations.ts:47`、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:59`
- 备注：requestedByUserId 为 null（系统发起）的记录任何登录用户可读（OperationsEndpoints.cs:33）。

### POST /api/v1/operations/confirmations/{id}/confirm
- 用途：普通确认（一次点击确认通过）。
- 认证：JWT
- Web 前端使用：是（确认中心页"确认"按钮）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 确认记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`，operations.ts:57）
- 响应 data：`OperationConfirmationDto`（确认后状态，字段同列表项）
- 错误：业务异常经 DomainException → 400（如记录不存在 3001）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:41`；前端 `src/client-web/src/api/operations.ts:54`、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:67-71`
- 备注：确认动作链路由前端按记录属性分发——requiresStrictConfirmation 走 /confirm-strict，requiresSecondLevelConfirmation 走 /confirm-second-level（ConfirmationsPage.tsx:67-71）。

### POST /api/v1/operations/confirmations/{id}/confirm-second-level
- 用途：二级确认（高危操作的第二道确认）。
- 认证：JWT
- Web 前端使用：是（确认中心页二级确认流程）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 确认记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`，operations.ts:65）
- 响应 data：`OperationConfirmationDto`（字段同列表项）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:51`；前端 `src/client-web/src/api/operations.ts:62`、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:71`
- 备注：无

### POST /api/v1/operations/confirmations/{id}/confirm-strict
- 用途：严格确认（需输入关键词等严格校验后的确认动作）。
- 认证：JWT
- Web 前端使用：是（确认中心页严格确认流程）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 确认记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`，operations.ts:72）
- 响应 data：`OperationConfirmationDto`（字段同列表项）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:61`；前端 `src/client-web/src/api/operations.ts:70`、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:67`
- 备注：严格校验（关键词输入比对）在前端完成，端点本身只接收确认动作。

### POST /api/v1/operations/confirmations/{id}/reject
- 用途：拒绝确认（操作作废）。
- 认证：JWT
- Web 前端使用：是（确认中心页"拒绝"按钮）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 确认记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`，operations.ts:80）
- 响应 data：`OperationConfirmationDto`（拒绝后状态，字段同列表项）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:71`；前端 `src/client-web/src/api/operations.ts:78`、调用方 `src/client-web/src/pages/ConfirmationsPage.tsx:9`
- 备注：无

## 审计时间线（Operations / Audit）

### GET /api/v1/operations/audit/{objectType}/{objectId}
- 用途：查询某对象的全量审计版本时间线（仅本人产生的版本，按时间升序）。
- 认证：JWT
- Web 前端使用：是（审计时间线页 /audit/:objectType/:objectId）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | objectType | string | 是 | 对象类型（URL 编码） |
  | objectId | string (UUID) | 是 | 对象 ID（路由约束 `{objectId:guid}`） |
- Query 参数：无
- Body：无
- 响应 data：`AuditTimelineResponse`（AuditVersionDtos.cs:15）
  | 字段 | 类型 | 说明 |
  | items | object[] | 版本列表（按 CreatedAt, Id 升序，AuditVersionService.cs:60-61） |
  | items[].id | string (UUID) | 版本 ID |
  | items[].objectType | string | 对象类型 |
  | items[].objectId | string (UUID) | 对象 ID |
  | items[].confirmationId | string\|null (UUID) | 关联确认记录 ID |
  | items[].source | string | 来源 |
  | items[].actor | string | 操作者（服务端写入 "system"） |
  | items[].beforeJson | string | 变更前快照（经 AuditSnapshotSanitizer 脱敏） |
  | items[].afterJson | string | 变更后快照（同上） |
  | items[].changedFieldsJson | string | 变更字段 JSON 数组字符串 |
  | items[].createdAt | string (ISO-8601) | 创建时间 |
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:81`、`src/Pim.Infrastructure/Audit/AuditVersionService.cs:51-66`；前端 `src/client-web/src/api/operations.ts:86`（路径 operations.ts:29-31）、调用方 `src/client-web/src/pages/AuditTimelinePage.tsx:37`
- 备注：查询按 `UserId == 当前用户` 过滤（AuditVersionService.cs:59），无法看到他人版本。

### POST /api/v1/operations/audit/{auditVersionId}/restore-preview
- 用途：预览把对象恢复到指定审计版本的效果（不执行恢复）。
- 认证：JWT
- Web 前端使用：是（审计时间线页"恢复预览"）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | auditVersionId | string (UUID) | 是 | 审计版本 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`，operations.ts:96）
- 响应 data：`RestorePreviewResponse`（AuditVersionDtos.cs:17-24）
  | 字段 | 类型 | 说明 |
  | objectType | string | 对象类型 |
  | objectId | string (UUID) | 对象 ID |
  | summary | string | 恢复描述（形如 "Restore {type} {id} to audit version {versionId}."） |
  | requiresConfirmation | boolean | 恒为 true（恢复必须走确认流程） |
  | changedFields | string[] | 该版本变更的字段 |
  | beforeJson | string\|null | 版本变更前快照（脱敏后） |
  | afterJson | string\|null | 版本变更后快照（脱敏后） |
- 错误：版本不存在或不属于当前用户抛 `DomainException(02056)` → HTTP 400 code=02056 `Audit version does not exist.`（AuditVersionService.cs:76）
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:93`、`src/Pim.Infrastructure/Audit/AuditVersionService.cs:68-88`；前端 `src/client-web/src/api/operations.ts:93`、调用方 `src/client-web/src/pages/AuditTimelinePage.tsx:42`
- 备注：本端点只做预览；实际恢复经确认中心流程执行。

### GET /api/v1/operations/audit/export
- 用途：导出时间范围内的审计版本（JSON 内容随响应返回，最多最近 5000 条）。
- 认证：JWT
- Web 前端使用：是（审计时间线页"导出"按钮）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | start | string (ISO-8601) | 否 | 起始时间，缺省 DateTimeOffset.MinValue |
  | end | string (ISO-8601) | 否 | 结束时间，缺省 DateTimeOffset.MaxValue；end < start 时自动对调（AuditVersionService.cs:96） |
- Body：无
- 响应 data：`AuditExportResponse`（AuditVersionDtos.cs:26-29）
  | 字段 | 类型 | 说明 |
  | fileName | string | 恒为 "audit-export.json" |
  | contentType | string | 恒为 "application/json" |
  | content | string | 完整 JSON 内容（`AuditVersionDto[]` 序列化字符串，按时间升序，AuditVersionService.cs:108-112） |
- 来源：后端 `src/Pim.Api/Endpoints/OperationsEndpoints.cs:104`、`src/Pim.Infrastructure/Audit/AuditVersionService.cs:90-113`；前端 `src/client-web/src/api/operations.ts:101`（路径 operations.ts:35-37）、调用方 `src/client-web/src/pages/AuditTimelinePage.tsx:46`
- 备注：仅导出当前用户的版本（UserId 过滤）；上限 5000 条防 OOM，超出时保留最近 5000 条（AuditVersionService.cs:97-107）。

## 数据可信度（Data Reliability）

### GET /api/v1/data-reliability/inspection
- 用途：读取最近一次数据可信度体检报告（不做全量扫库）。
- 认证：JWT（data-reliability 组整体 `RequireAuthorization()`，DataReliabilityEndpoints.cs:26）
- Web 前端使用：是（数据可信度页）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`DataReliabilityInspectionReport`（DataReliabilityInspectionReport.cs:67-82）
  | 字段 | 类型 | 说明 |
  | inspectedAtUtc | string (ISO-8601) | 本次体检时间 |
  | version | number | 报告版本 |
  | elapsedMilliseconds | number | 体检耗时 |
  | status | string | 总体状态 |
  | redCount | number | 红灯尺子数 |
  | yellowCount | number | 黄灯尺子数 |
  | greenCount | number | 绿灯尺子数 |
  | unknownCount | number | 未知尺子数 |
  | totalViolations | number | 违规总数 |
  | newViolations | number | 新增违规数 |
  | historicalViolations | number | 历史违规数 |
  | notices | object (map) | 公告键值对（string → string） |
  | rules | object[] | 13 条尺子结论数组（结构见下） |
  | rules[].code | string | 尺子编号 |
  | rules[].invariantCode | string | 关联不变量编号 |
  | rules[].key | string | 键 |
  | rules[].order | number | 展示顺序 |
  | rules[].name | string | 尺子名称 |
  | rules[].group | string | 分组键 |
  | rules[].groupLabel | string | 分组显示名 |
  | rules[].status | string | 状态 |
  | rules[].statusLabel | string | 状态显示名 |
  | rules[].detail | string | 结论详情 |
  | rules[].currentValue | number\|null | 当前值 |
  | rules[].currentValueUnit | string\|null | 当前值单位 |
  | rules[].currentValueLabel | string\|null | 当前值显示 |
  | rules[].threshold | string | 阈值描述 |
  | rules[].criterion | string | 判据原文 |
  | rules[].rationale | string | 设立理由 |
  | rules[].relatedIssues | number[] | 关联 issue 编号 |
  | rules[].totalViolations | number | 该尺子违规总数 |
  | rules[].newViolations | number | 新增违规 |
  | rules[].historicalViolations | number | 历史违规 |
  | rules[].earliestOccurrenceUtc | string\|null (ISO-8601) | 最早违规时间 |
  | rules[].latestOccurrenceUtc | string\|null (ISO-8601) | 最近违规时间 |
  | rules[].samples | string[] | 样本摘要 |
  | rules[].thresholdFallback | boolean | 是否使用回退阈值 |
  | rules[].thresholdNote | string\|null | 回退阈值说明 |
  | rules[].coveredLayers | string\|null | 覆盖层说明 |
  | rules[].trend | string | 趋势 |
  | rules[].trendDelta | number\|null | 趋势差值 |
  | rules[].trendBaselineUtc | string\|null (ISO-8601) | 趋势基线时间 |
  | rules[].threeState | object\|null | S2 三态分布（仅 S2）：inputActiveSeconds/mediaActiveSeconds/suspectedUnclosedSeconds/totalSeconds(double)、inputActiveCount/mediaActiveCount/suspectedUnclosedCount(int)、declaredGapSeconds(double) |
  | rules[].scanTruncated | boolean | 扫描是否被截断 |
  | message | string | 汇总消息 |
  | deviceLiveness | object[]\|null | 设备存活区块（独立展示，不参与红黄绿统计；结构 DeviceLivenessInspectionItem） |
- 来源：后端 `src/Pim.Api/Endpoints/DataReliabilityEndpoints.cs:28`、`src/Pim.Core/Invariants/DataReliabilityInspectionReport.cs:67-82`；前端 `src/client-web/src/api/dataReliability.ts:23`（路径 dataReliability.ts:11-21）、调用方 `src/client-web/src/pages/DataReliabilityPage.tsx:28`
- 备注：无

### POST /api/v1/data-reliability/inspection/refresh
- 用途：手动触发一次完整体检并返回新报告。
- 认证：JWT
- Web 前端使用：是（数据可信度页"重新体检"按钮）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`DataReliabilityInspectionReport`（结构同 GET /inspection）
- 来源：后端 `src/Pim.Api/Endpoints/DataReliabilityEndpoints.cs:36`；前端 `src/client-web/src/api/dataReliability.ts:30`、调用方 `src/client-web/src/pages/DataReliabilityPage.tsx`
- 备注：GET 读最近结果、本端点才真正重算（DataReliabilityEndpoints.cs:10-12 注释）。

### GET /api/v1/data-reliability/rules/{code}/violations
- 用途：导出某条尺子的完整违规清单（下钻用）。
- 认证：JWT
- Web 前端使用：是（数据可信度页尺子弹窗 DataReliabilityRuleDialog）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | code | string | 是 | 尺子编号（URL 编码）；未知编号返回 400 code=40044 `未知的尺子编号`（DataReliabilityEndpoints.cs:50-54） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | limit | number | 否 | 导出条数上限，默认 2000，钳制 1~5000（DataReliabilityEndpoints.cs:20-22、56） |
- Body：无
- 响应 data：`DataReliabilityViolationExport`（DataReliabilityInspectionReport.cs:97-102）
  | 字段 | 类型 | 说明 |
  | ruleCode | string | 尺子编号 |
  | generatedAtUtc | string (ISO-8601) | 导出时间 |
  | totalCount | number | 违规总数 |
  | truncated | boolean | 是否因 limit 截断 |
  | items | object[] | 违规明细数组 |
  | items[].ruleCode | string | 尺子编号 |
  | items[].id | string | 违规记录 ID |
  | items[].deviceId | string | 设备 ID |
  | items[].occurredAtUtc | string (ISO-8601) | 业务发生时间 |
  | items[].fields | object (map) | 关键字段键值对（string → string） |
- 来源：后端 `src/Pim.Api/Endpoints/DataReliabilityEndpoints.cs:44`；前端 `src/client-web/src/api/dataReliability.ts:37`（默认 limit=2000，dataReliability.ts:9）、调用方 `src/client-web/src/components/data-reliability/DataReliabilityRuleDialog.tsx:32`
- 备注：无

## 系统状态（Status）

### GET /api/v1/status/summary
- 用途：返回系统整体健康摘要（状态灯 + 一句话消息）。
- 认证：JWT（status 组整体 `RequireAuthorization()`，StatusEndpoints.cs:10）
- Web 前端使用：是（侧边栏状态指示器 SidebarStatusIndicator）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`SystemStatusSummaryDto`（StatusDtos.cs:3-7）
  | 字段 | 类型 | 说明 |
  | status | number | 健康状态枚举：`Unknown=0` \| `Healthy=1` \| `Warning=2` \| `Critical=3`（OperationEnums.cs:4-11） |
  | label | string | 状态标签 |
  | message | string | 消息 |
  | checkedAt | string (ISO-8601) | 检查时间 |
- 来源：后端 `src/Pim.Api/Endpoints/StatusEndpoints.cs:12`；前端 `src/client-web/src/api/status.ts:136`（路径 status.ts:6）、调用方 `src/client-web/src/components/status/SidebarStatusIndicator.tsx:16`
- 备注（前后端差异）：前端 `normalizeHealthStatus` 同时接受数字与字符串并归一化（status.ts:10-15、54-62），数字 0-3 映射 Unknown/Healthy/Warning/Critical；前端展示文案为 未知/正常/有警告/故障（status.ts:19-24）。

### GET /api/v1/status
- 用途：返回系统健康详情（摘要 + 各组件状态 + 下一步建议）。
- 认证：JWT
- Web 前端使用：是（状态页 /status）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`SystemStatusDetailDto`（StatusDtos.cs:18-21）
  | 字段 | 类型 | 说明 |
  | summary | object | 同 GET /status/summary 的 SystemStatusSummaryDto |
  | components | object[] | 组件状态数组 |
  | components[].key | string | 组件键 |
  | components[].name | string | 组件名 |
  | components[].kind | number | 组件类别枚举：`Api=0` \| `Database=1` \| `Storage=2` \| `TextExtraction=3` \| `Daemon=4` \| `ActivityWatch=5` \| `KeyStats=6` \| `BackgroundJobs=7`（OperationEnums.cs:13-23） |
  | components[].status | number | 健康状态枚举（同 summary.status） |
  | components[].message | string | 消息 |
  | components[].checkedAt | string (ISO-8601) | 检查时间 |
  | components[].details | object (map) | 附加明细（string → string） |
  | nextSteps | string[] | 建议动作列表 |
- 来源：后端 `src/Pim.Api/Endpoints/StatusEndpoints.cs:20`（路由 `/`，即 `/api/v1/status`）；前端 `src/client-web/src/api/status.ts:141`（路径 status.ts:7，请求 `/status/` 带尾斜杠）、调用方 `src/client-web/src/pages/StatusPage.tsx:162`
- 备注（前后端差异）：前端请求路径为 `/status/`（尾斜杠），后端路由 `MapGet("/")` 两者均可匹配；components[].kind 后端输出数字，前端 `normalizeKind` 对纯数字返回空字符串再由 `getComponentKindLabel` 显示原始 kind（status.ts:74-77、91-94），重建时应改为可读映射。

## 守护进程心跳（Daemon）

### POST /api/v1/daemon/heartbeat
- 用途：Windows 守护进程上报心跳（版本、上传队列、采集源状态）。
- 认证：JWT（daemon 组整体 `RequireAuthorization()`，DaemonEndpoints.cs:10）；实际消费方 Windows 守护进程
- Web 前端使用：否（上报方为 Windows 守护进程 PimDaemon；Web 不调用）
- Path 参数：无
- Query 参数：无
- Body（`DaemonHeartbeatRequest`，DaemonHeartbeatDtos.cs:3-15）：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | daemonKind | string | 是 | 守护进程类别（windows 等） |
  | version | string | 是 | 守护进程版本 |
  | serverUrl | string | 是 | 守护进程配置的服务端地址 |
  | lastSuccessfulUploadAt | string\|null (ISO-8601) | 否 | 最近成功上传时间 |
  | lastAttemptedUploadAt | string\|null (ISO-8601) | 否 | 最近尝试上传时间 |
  | lastError | string\|null | 否 | 最近错误 |
  | uploadQueueCount | number\|null | 否 | 待上传队列长度 |
  | activityWatchState | string | 是 | ActivityWatch 采集源状态枚举：`Unknown` \| `Available` \| `Unavailable` \| `Paused`（OperationEnums.cs:63-70，JsonStringEnumConverter 序列化为字符串） |
  | keyStatsState | string | 是 | 键鼠统计源状态（同上枚举） |
  | collectionPaused | boolean | 是 | 是否暂停采集 |
  | statusJson | string | 是 | 附加状态 JSON |
- 响应 data：`DaemonHeartbeatDto`（DaemonHeartbeatDtos.cs:17-32；在请求字段基础上新增 receivedAt/plannedOfflineAt/offlineReason）
  | 字段 | 类型 | 说明 |
  | （请求全部字段） | — | deviceId/daemonKind/version/serverUrl/lastSuccessfulUploadAt/lastAttemptedUploadAt/lastError/uploadQueueCount/activityWatchState/keyStatsState/collectionPaused/statusJson |
  | receivedAt | string (ISO-8601) | 服务端接收时间 |
  | plannedOfflineAt | string\|null (ISO-8601) | 计划下线时间 |
  | offlineReason | string\|null | 下线原因 |
- 来源：后端 `src/Pim.Api/Endpoints/DaemonEndpoints.cs:12`；前端无调用（上报方 `src/client-windows/Pim.Client.Core/Models/DaemonHeartbeatDtos.cs`）
- 备注：无

### POST /api/v1/daemon/planned-offline
- 用途：守护进程计划下线（如系统挂起/关机）时上报原因。
- 认证：JWT；实际消费方 Windows 守护进程
- Web 前端使用：否（同上）
- Path 参数：无
- Query 参数：无
- Body（`PlannedOfflineRequest`，DaemonHeartbeatDtos.cs:34-38）：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | daemonKind | string | 是 | 守护进程类别 |
  | reason | string\|null | 否 | 下线原因 |
  | occurredAt | string\|null (ISO-8601) | 否 | 发生时间（服务端用其做陈旧守卫：迟到 > 容忍窗口的请求不落库，DaemonHeartbeatService.cs:74-84） |
- 响应 data：`DaemonHeartbeatDto \| null`（最新心跳记录；该设备无任何历史心跳时为 null，DaemonHeartbeatDtos.cs:43）
- 来源：后端 `src/Pim.Api/Endpoints/DaemonEndpoints.cs:21`、`src/Pim.Infrastructure/Operations/DaemonHeartbeatService.cs:70-84`；前端无调用（上报方 `src/client-windows/Pim.Client.Core/Models/PlannedOfflineDtos.cs`）
- 备注：无

### GET /api/v1/daemon/heartbeats
- 用途：列出全部守护进程最新心跳（多设备视图）。
- 认证：JWT
- Web 前端使用：是（状态页 /status 将其与 /mobile/devices 合并成设备列表）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`DaemonHeartbeatDto[]`（字段同 POST /daemon/heartbeat 响应）
- 来源：后端 `src/Pim.Api/Endpoints/DaemonEndpoints.cs:30`；前端 `src/client-web/src/api/status.ts:164`（类型 DaemonHeartbeat status.ts:146-162）、调用方 `src/client-web/src/pages/StatusPage.tsx:202`
- 备注：前端 status.ts 捕获异常返回空数组兜底（status.ts:165-170）；消费方为状态页合并 /mobile/devices（statusDeviceModel.ts）。

## 端点状态（Endpoints）

### GET /api/v1/endpoints
- 用途：列出当前用户全部端点设备的状态（Android/桌面端外壳）。
- 认证：JWT（endpoints 组整体 `RequireAuthorization()`，EndpointEndpoints.cs:11）
- Web 前端使用：是（端点外壳页 /endpoint-shell）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`EndpointStatusDto[]`（EndpointDtos.cs:3-10）
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备唯一标识 |
  | platform | string | 平台 |
  | appVersion | string\|null | 应用版本 |
  | uploadStatus | string | 上传状态（Healthy/Unknown 等） |
  | collectionCacheCount | number | 本地采集缓存条数 |
  | onlineOnlyBlockedCount | number | 需 Web 确认被阻塞的动作数 |
  | lastHeartbeatAt | string\|null (ISO-8601) | 最近心跳时间 |
- 来源：后端 `src/Pim.Api/Endpoints/EndpointEndpoints.cs:13`；前端 `src/client-web/src/api/endpoints.ts:26`（路径 endpoints.ts:11-24）、调用方 `src/client-web/src/pages/EndpointShellPage.tsx:40`
- 备注：前端类型 `EndpointStatus`（types/index.ts:355-362）与后端一致。

### POST /api/v1/endpoints/{deviceId}/heartbeat
- 用途：端点设备上报/刷新心跳（可携带平台、版本、上传状态、缓存数）。
- 认证：JWT；主要消费方为端点设备自身（外壳页手动触发亦用此端点）
- Web 前端使用：是（端点外壳页"手动心跳"调试按钮）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body（`EndpointHeartbeatRequest`，EndpointDtos.cs:12-16）：
  | 字段 | 类型 | 必填 | 说明 |
  | platform | string | 是 | 平台 |
  | appVersion | string\|null | 否 | 应用版本 |
  | uploadStatus | string\|null | 否 | 上传状态 |
  | collectionCacheCount | number\|null | 否 | 本地采集缓存条数（负值按 0 处理，EndpointStatusService.cs:259-260） |
- 响应 data：`EndpointStatusDto`（字段同 GET /endpoints 列表项）
- 来源：后端 `src/Pim.Api/Endpoints/EndpointEndpoints.cs:21`、`src/Pim.Infrastructure/Endpoints/EndpointStatusService.cs:54`；前端 `src/client-web/src/api/endpoints.ts:31`、调用方 `src/client-web/src/pages/EndpointShellPage.tsx:63`
- 备注：无

### GET /api/v1/endpoints/{deviceId}/collection-quality
- 用途：查看端点设备采集质量（上传状态异常即计 1 个问题）。
- 认证：JWT
- Web 前端使用：是（端点外壳页质量检查）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`EndpointCollectionQualityDto`（EndpointDtos.cs:18-23）
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备唯一标识 |
  | platform | string | 平台 |
  | uploadStatus | string | 上传状态 |
  | issueCount | number | 问题数（uploadStatus 非 Healthy 且非 Unknown 时 +1，EndpointStatusService.cs:90-94） |
  | checkedAt | string (ISO-8601) | 检查时间 |
- 来源：后端 `src/Pim.Api/Endpoints/EndpointEndpoints.cs:31`、`src/Pim.Infrastructure/Endpoints/EndpointStatusService.cs:82-107`；前端 `src/client-web/src/api/endpoints.ts:39`、调用方 `src/client-web/src/pages/EndpointShellPage.tsx:57`
- 备注：无

### POST /api/v1/endpoints/{deviceId}/notification-actions
- 用途：端点侧通知动作处理——低风险直接执行，高风险返回"需打开 Web 确认详情"。
- 认证：JWT
- Web 前端使用：是（端点外壳页模拟通知动作调试）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body（`EndpointNotificationActionRequest`，EndpointDtos.cs:25-30）：
  | 字段 | 类型 | 必填 | 说明 |
  | action | string | 是 | 动作名（空白返回 Rejected + "Notification action is required."，EndpointStatusService.cs:117-125） |
  | riskLevel | string | 是 | 风险等级（低风险直接执行，EndpointStatusService.cs:128-134） |
  | confirmationId | string\|null | 否 | 关联确认记录 ID |
  | relatedObjectType | string\|null | 否 | 关联对象类型 |
  | relatedObjectId | string\|null | 否 | 关联对象 ID |
- 响应 data：`EndpointNotificationActionResponse`（EndpointDtos.cs:32-35）
  | 字段 | 类型 | 说明 |
  | result | string | `Executed`（低风险已执行）\| `OpenDetailRequired`（高风险需 Web 确认，同时 onlineOnlyBlockedCount +1）\| `Rejected`（action 为空） |
  | detailUrl | string\|null | Web 确认详情链接（高风险时返回） |
  | message | string\|null | 说明文案 |
- 来源：后端 `src/Pim.Api/Endpoints/EndpointEndpoints.cs:40`、`src/Pim.Infrastructure/Endpoints/EndpointStatusService.cs:109-147`；前端 `src/client-web/src/api/endpoints.ts:46`、调用方 `src/client-web/src/pages/EndpointShellPage.tsx:77`
- 备注：每次动作均记录到 notification_actions 供审计（EndpointStatusService.cs:145、189-193）。

## 今日聚合（Today）

### GET /api/v1/today/sections
- 用途：返回今日页全部区块的注册表（ID/kind/状态/链接，不含区块数据体）。
- 认证：JWT（today 组整体 `RequireAuthorization()`，TodayEndpoints.cs:23）；聚合结果走 IAggregateResultCache 缓存
- Web 前端使用：是（今日页 /today 及 Android 内嵌今日页）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日期（YYYY-MM-DD 或可解析日期时间）；格式非法返回 400 code=400 `Invalid Today date. Expected YYYY-MM-DD or a parseable date/time value.`（TodayEndpoints.cs:67-70） |
  | force | boolean | 否 | true 时跳过聚合缓存直接重算（TodayEndpoints.cs:30、MemoryAggregateResultCache.cs:37-38）；前端不传 |
- Body：无
- 响应 data：`TodaySectionRegistryDto`（TodayDtos.cs:32-36）
  | 字段 | 类型 | 说明 |
  | date | string | 今日日期（YYYY-MM-DD） |
  | pcBusinessDate | string | PC 业务日（04:00 界） |
  | generatedAt | string (ISO-8601) | 生成时间 |
  | sections | object[] | 区块注册项数组 |
  | sections[].id | string | 区块 ID（如 calendar.schedule、pc.activity、operations.confirmations） |
  | sections[].kind | string | 区块类别（与 id 同构的 kind 标识） |
  | sections[].status | string | 状态枚举：`available` \| `normal` \| `empty` \| `warning` \| `critical` \| `unavailable`（TodayDtos.cs:3-11） |
  | sections[].links | object[] | 链接数组 `[{ rel: 'self'|'details'|'api', href }]`（TodayDtos.cs:13-18、22） |
- 来源：后端 `src/Pim.Api/Endpoints/TodayEndpoints.cs:25`、`src/Pim.Core/Today/TodayDtos.cs:32-36`；前端 `src/client-web/src/api/today.ts:10`（路径 today.ts:4-8）、调用方 `src/client-web/src/pages/TodayPage.tsx:84`
- 备注：缓存 TTL 按时段变化（06 点前 30 分钟、其余 5 分钟，MemoryAggregateResultCache.cs:30）；注册节由 15 个 ITodaySectionProvider 构成（Program.cs:163-176）。

### GET /api/v1/today/sections/{sectionId}
- 用途：返回单个今日区块的完整数据（含 data 载荷）。
- 认证：JWT；聚合缓存同上
- Web 前端使用：是（今日页按注册表逐节拉取，仅 kind 前缀 `pc.` / `operations.` 的节开启定时轮询）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | sectionId | string | 是 | 区块 ID（URL 编码，TodayEndpointPaths.Section 转义，TodayEndpoints.cs:15-16） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日期（同 GET /sections） |
  | force | boolean | 否 | 跳过缓存重算 |
- Body：无
- 响应 data：`TodaySectionDto`（TodayDtos.cs:38-45）
  | 字段 | 类型 | 说明 |
  | id | string | 区块 ID |
  | kind | string | 区块类别 |
  | status | string | 同注册表状态枚举 |
  | generatedAt | string (ISO-8601) | 生成时间 |
  | data | object | 区块数据载荷（随 kind 不同而异，前端以 `TodaySection<TData>` 泛型消费） |
  | links | object[] | 链接数组（同注册表） |
  | error | object\|null | 失败信息 `{ code: string, message: string }`（TodayDtos.cs:24） |
- 错误：404 code=404 `今日模块不存在。`（未知 sectionId，TodayEndpoints.cs:60-62）；date 非法 400（同上）
- 来源：后端 `src/Pim.Api/Endpoints/TodayEndpoints.cs:44`；前端 `src/client-web/src/api/today.ts:14`、调用方 `src/client-web/src/components/today/TodaySectionHost.tsx:115-120`
- 备注：前端轮询规则——`item.kind.startsWith('pc.') || item.kind.startsWith('operations.')` 时按 deferred 间隔轮询，其余节不轮询（TodaySectionHost.tsx:119）；未在 Web 端注册的 kind 显示"未知区块"（TodaySectionHost.tsx:122-124）。

## 全局搜索（Search）

### GET /api/v1/search
- 用途：跨模块关键字搜索（聚合全部 ISearchProvider 结果，标题命中优先）。
- 认证：JWT（search 组整体 `RequireAuthorization()`，SearchEndpoints.cs:10-11）
- Web 前端使用：否（Web 前端未调用；实际消费方为 MCP 工具 search_pim，McpToolTable.cs:223）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | q | string | 是 | 关键字；空白时返回空分页结果（SearchEndpoints.cs:20-22） |
  | type | string | 否 | 逗号分隔的类型过滤（如 `calendar,event`，大小写不敏感，SearchEndpoints.cs:25-26、34-35） |
  | limit | number | 否 | 返回上限，默认 20，最大 100（`Math.Min(limit ?? 20, 100)`，SearchEndpoints.cs:24） |
- Body：无
- 响应 data：`PagedResult<SearchResult>`（PagedResult.cs:3-9；SearchResult ISearchProvider.cs:9-16）
  | 字段 | 类型 | 说明 |
  | items | object[] | 搜索结果数组（page 恒为 1，非真分页） |
  | items[].moduleName | string | 来源模块名 |
  | items[].type | string | 结果类型 |
  | items[].id | string | 对象 ID |
  | items[].title | string | 标题 |
  | items[].snippet | string | 摘要 |
  | items[].url | string | 前端跳转链接 |
  | totalCount | number | 命中总数（过滤后） |
  | page | number | 恒为 1 |
  | pageSize | number | 等于生效的 limit |
  | totalPages | number | 1（total=0 时为 0） |
- 来源：后端 `src/Pim.Api/Search/SearchEndpoints.cs:13`（注意：文件位于 `src/Pim.Api/Search/`，非 Endpoints 目录）、注册 `src/Pim.Api/Program.cs:353`；前端无调用
- 备注：排序规则为标题包含关键字（忽略大小写）者靠前（SearchEndpoints.cs:37-39）；结果不足 limit 时全部返回。

## 地图瓦片（Tiles）

### GET /api/v1/tiles/{z}/{x}/{y}.png
- 用途：OSM 地图瓦片代理（带 7 天磁盘缓存，供 Leaflet 地图使用）。
- 认证：匿名（`.AllowAnonymous()`，TileEndpoints.cs:31）
- Web 前端使用：是（历史位置地图 HistoricalLocationLeafletMap 等 Leaflet 图层的瓦片源）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | z | number | 是 | 缩放级别 0~19（TileCoordinate.cs:7-8） |
  | x | number | 是 | 列号（0 ≤ x < 2^z） |
  | y | number | 是 | 行号（0 ≤ y < 2^z） |
- Query 参数：无
- Body：无
- 响应：二进制 `image/png`（Results.File）；响应头 `Cache-Control: public, max-age=604800, immutable`（7 天不可变缓存）、`X-PIM-Tile-Cache: HIT`（命中磁盘缓存）\| `MISS`（回源下载）（TileEndpoints.cs:23-25）
- 错误：坐标非法 400 `{ message: "Invalid tile coordinates." }`（非 ApiResponse 封装，TileEndpoints.cs:17-18）；上游失败 502 空 body（TileUpstreamException → `Results.StatusCode(502)`，TileEndpoints.cs:27-30）
- 来源：后端 `src/Pim.Api/Endpoints/TileEndpoints.cs:9`、`src/Pim.Api/Tiles/TileService.cs:33-69`（上游默认 `https://tile.openstreetmap.org`，缓存目录 `/data/pim/cache/tiles`，TTL 7 天，TileOptions.cs:5-7）；前端 `src/client-web/src/components/mobile/HistoricalLocationLeafletMap.tsx:169`
- 备注：上游响应须为 PNG 且 Content-Type 为 image/*，否则按上游失败抛 502（TileService.cs:110-116）；客户端中途取消（地图拖动）被中间件记为 499 且不计 5xx（ExceptionMiddleware.cs:70-80 注释）。

## 版本（Version / Client Shell）

### GET /api/version
- 用途：返回 API 自身版本与 GitHub 最新 release 快照（更新检查）。
- 认证：匿名（`.AllowAnonymous()`，VersionEndpoints.cs:41）
- Web 前端使用：是（"关于"卡片/页脚版本信息 AboutPimCard，经 useVersionInfo）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`ApiVersionResponse`（VersionEndpoints.cs:5-14）
  | 字段 | 类型 | 说明 |
  | version | string | API 程序集 InformationalVersion（缺省 "0.0.0(unknown)"） |
  | capabilities | string[] | 能力开关：`mobileItemResultsV1`、`androidEmbedV1`（VersionEndpoints.cs:18-20） |
  | latestVersion | string\|null | GitHub release 最新版本号 |
  | checkedAt | string\|null (ISO-8601) | 快照刷新时间 |
  | error | string\|null | GitHub 拉取失败信息 |
  | windowsVersion | string\|null | Windows 安装包版本（有 WindowsUrl 时才输出） |
  | androidVersion | string\|null | Android 安装包版本 |
  | shellWindowsVersion | string\|null | Windows 外壳版本 |
  | shellAndroidVersion | string\|null | Android 外壳版本 |
- 来源：后端 `src/Pim.Api/Endpoints/VersionEndpoints.cs:24`、快照 `src/Pim.Api/Services/GitHubReleaseService.cs:11-21`；前端 `src/client-web/src/api/version.ts:2`、调用方 `src/client-web/src/hooks/useVersionInfo.ts:22`
- 备注：前端 useVersionInfo 用本地版本与 latestVersion 的最后一段数字比较判断更新（useVersionInfo.ts:7-12、24）；本端点不经 ApiResponse 封装（裸对象）。

### GET /api/client/shell/latest
- 用途：返回各客户端（Windows/Android 主程序与外壳）最新版本与下载地址。
- 认证：匿名（`.AllowAnonymous()`，ClientShellModule.cs:56）；旧别名 `/api/v1/client/shell/latest` 同映射（ClientShellModule.cs:57-59，兼容旧版 Android APK）
- Web 前端使用：否（前端 api 层有封装 `getClientLatest`（version.ts:6-8）但无页面调用；实际消费方为 Android/Windows 客户端的更新检查）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：匿名对象（GitHub release 快照优先，无有效快照时回退配置 ClientShellOptions；ClientShellModule.cs:23-53）
  | 字段 | 类型 | 说明 |
  | windowsVersion | string\|null | Windows 主程序版本 |
  | windowsUrl | string\|null | Windows 下载地址 |
  | androidVersion | string\|null | Android 主程序版本 |
  | androidUrl | string\|null | Android 下载地址 |
  | shellWindowsVersion | string\|null | Windows 外壳版本 |
  | shellWindowsUrl | string\|null | Windows 外壳下载地址 |
  | shellAndroidVersion | string\|null | Android 外壳版本 |
  | shellAndroidUrl | string\|null | Android 外壳下载地址 |
  | checkedAt | string\|null (ISO-8601) | 快照刷新时间 |
  | error | string\|null | 拉取失败信息 |
- 来源：后端 `src/Pim.Api/Modules/ClientShell/ClientShellModule.cs:56-59`；前端 `src/client-web/src/api/version.ts:6`
- 备注：快照有效性判定——任一组件有 URL 才采用快照（防止只有版本号没有下载地址，ClientShellModule.cs:23）；本端点不经 ApiResponse 封装。

## 统计上报（Stats，旧版 Android）

### POST /api/v1/stats/upload
- 用途：旧版应用使用统计批量上报（Android 客户端使用）。
- 认证：JWT（StatsModule.cs:29 `RequireAuthorization`）
- Web 前端使用：否（消费方为 Android 客户端）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备标识 |
  | entries[] | AppUsageEntry[] | 是 | 使用条目批量（空数组直接返回 0） |
  | entries[].packageName | string | 是 | 应用包名 |
  | entries[].startTime | long | 是 | 起始时间戳 |
  | entries[].endTime | long | 是 | 结束时间戳 |
  | entries[].durationMs | long | 是 | 使用时长（毫秒） |
  | entries[].lastTimeUsed | long | 是 | 最近使用时间戳 |
- 响应 data：`int` —— 入库条目数
- 来源：后端 `src/modules/Pim.Module.Stats/StatsModule.cs:31-45`、`src/modules/Pim.Module.Stats/DTOs/StatsDtos.cs`
- 备注：批量整体入库（StatsService.IngestBatchAsync）。

## 健康检查与指标（Health / Metrics）

### GET /health
- 用途：极简存活探测（容器编排用）。
- 认证：匿名（Program.cs:305）
- Web 前端使用：否（基础设施探活用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`{ status: "healthy", timestamp: <ISO-8601> }`（固定 healthy，仅证明进程可响应）
- 来源：后端 `src/Pim.Api/Program.cs:305`
- 备注：不经 ApiResponse 封装。

### GET /health/live
- 用途：ASP.NET Core liveness 探针（进程存活，不检查依赖）。
- 认证：匿名（Program.cs:308-311，`Predicate = _ => false` 不执行任何检查）
- Web 前端使用：否（基础设施探活用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：ASP.NET Core HealthCheck 默认 JSON（`{ status: "Healthy"|"Unhealthy", ... }`）
- 来源：后端 `src/Pim.Api/Program.cs:308-311`
- 备注：无

### GET /health/ready
- 用途：readiness 探针（检查各依赖组件，返回健康明细）。
- 认证：匿名（Program.cs:312-316）
- Web 前端使用：否（基础设施探活用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：PimHealthChecks.WriteReadyResponse 自定义 JSON（PimHealthChecks.cs:114-130）
  | 字段 | 类型 | 说明 |
  | status | string | `Healthy` \| `Degraded` \| `Unhealthy`（HealthReportStatus.ToString()） |
  | checks | object[] | 各组件检查明细 |
  | checks[].name | string | 组件名 |
  | checks[].status | string | `Healthy` \| `Degraded` \| `Unhealthy` |
  | checks[].description | string\|null | 描述 |
  | checks[].durationMs | number | 该项检查耗时（1 位小数） |
  | timestamp | string (ISO-8601) | 响应生成时间 |
- 来源：后端 `src/Pim.Api/Program.cs:312-316`、`src/Pim.Api/Health/PimHealthChecks.cs:114-130`
- 备注：仅执行带 `ready` 标签的检查（Program.cs:314）。

### GET /metrics
- 用途：Prometheus 文本指标（供 Grafana 抓取）。
- 认证：Admin JWT 或 OpsKey——请求头 `X-PIM-Ops-Key`，或 `Authorization: Bearer <opsKey>`；二者皆无返回 401 `{"code":40101,"message":"MetricsAuthRequired"}`（Program.cs:319-338）
- Web 前端使用：否（消费方为 Prometheus/Grafana 抓取器与运维脚本）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应：Prometheus 文本格式（prometheus-net `UseMetrics` 输出），非 ApiResponse 封装
- 来源：后端 `src/Pim.Api/Program.cs:319-338`（`MapMetrics` + 端点过滤器鉴权）
- 备注：Admin 判定用 `http.User.IsInRole("admin")`（JWT 已在认证中间件解析，Program.cs:322）。

## 路由兜底与静态托管

### ANY /api/{*path}（未匹配 API 路由）
- 用途：所有未匹配的 /api/* 请求返回 JSON 404，避免落入 SPA fallback 返回 HTML 导致前端 `Unexpected token '<'`（Program.cs:404-405 注释，白屏修复 #2）。
- 认证：匿名（`.AllowAnonymous()`）
- Web 前端使用：是（前端错误提示直接展示其中的 message）
- Path 参数：`{*path}` 通配任意未匹配 API 路径
- Query 参数：无
- Body：无
- 响应 data：HTTP 404，裸对象（非 ApiResponse 封装）
  | 字段 | 类型 | 说明 |
  | code | number | 404 |
  | message | string | `"接口不存在: <请求路径>"` |
  | data | null | 无 |
  | timestamp | string (ISO-8601) | 响应时间 |
- 方法：GET、POST、PUT、DELETE、PATCH、OPTIONS、HEAD（Program.cs:406）
- 来源：后端 `src/Pim.Api/Program.cs:406-408`
- 备注：该 catch-all 必须位于 SPA fallback 之前注册；两者之间不得插入其它 fallback，否则未知 /api 路径会再次返回 HTML（Program.cs:405 注释）。

### GET /{*file}（SPA fallback）
- 用途：非 API 的未匹配路由统一回退到 index.html，由 React Router 接管前端路由。
- 认证：匿名（`.AllowAnonymous()`）
- Web 前端使用：是（浏览器直接访问 /confirmations、/settings/users 等深链时的入口）
- Path 参数：`{*file}` 通配任意非 API 文件路径
- Query 参数：无
- Body：无
- 响应：`text/html`（wwwroot/index.html 内容）
- 来源：后端 `src/Pim.Api/Program.cs:421`（`MapFallbackToFile("index.html")`）；静态文件服务 Program.cs:240-241（`UseDefaultFiles` + `UseStaticFiles`）
- 备注：前端 api/client.ts 检测到接口返回 HTML 时抛出"接口返回了 HTML 而非 JSON"错误并提示路径错误或后端未启动（client.ts:185-191、275-280）——这正是命中本 fallback 的典型症状。
