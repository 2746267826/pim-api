# 移动设备域接口规格（/api/v1/mobile）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域路由组整体要求 JWT。说明：本域同时服务 Android 客户端上报（注册/使用事件/位置点/取证事件），Web 前端主要消费查询/管理端。
>
> 补充（源码核实）：路由组在 `MapGroup(MobileEndpointPaths.Root).RequireAuthorization()`（MobileModule.cs:61-62，`Root = "/api/v1/mobile"`，MobileModule.cs:585），本域**没有任何匿名端点**。业务日口径：`BusinessDay`（src/Pim.Core/Common/BusinessDay.cs:16-79）—— 业务日 D = `[D 04:00, D+1 04:00)`（Asia/Shanghai，左闭右开），`date` 参数解析为该窗口后按 UTC 查询。序列化约定：ASP.NET Core 默认 camelCase；C# record 的只读别名属性（如 `MobileDeviceDto.DeviceHash`）也会被 System.Text.Json 序列化输出（在对应节注明）。`force=true` 时跳过聚合结果缓存（IAggregateResultCache）直接重算；goals / catalog-overrides / category-rules 的写操作会整体清空前缀 `/api/v1/mobile/` 的聚合缓存（MobileModule.cs:424、451、463、474、490、502、514）。业务错误 `DomainException` 经全局中间件映射：04004→HTTP 404，其余（04000/04001/04002 等）→HTTP 400（ExceptionMiddleware.cs:120-128）。

---

## 设备管理与详情

### GET /api/v1/mobile/devices
- 用途：列出当前用户的全部 Android 设备（客户端设备库视图，含注册信息与最近活跃时间）。
- 认证：JWT
- Web 前端使用：是（状态页 StatusPage、使用分析页 MobileRecordsPage、历史位置页 HistoricalLocationPage）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`MobileDeviceDto[]`（MobileDtos.cs:22-38，映射 MobileDeviceService.cs:70-86）
  | 字段 | 类型 | 说明 |
  | id | string (UUID) | 设备库主键 |
  | deviceId | string | 客户端设备唯一标识（注册时传入，≤128 字符） |
  | androidIdHash | string\|null | Android ID 哈希 |
  | displayName | string | 设备别名 |
  | manufacturer | string | 厂商 |
  | brand | string | 品牌 |
  | model | string | 型号 |
  | androidVersion | string | 系统版本 |
  | sdkInt | number | Android API Level |
  | appVersion | string | Android 客户端版本 |
  | metadataJson | string | 注册元数据 JSON 字符串（含 deviceKind、smallestScreenWidthDp、pendingUpload 等，默认 "{}"） |
  | firstSeenAt | string (ISO-8601) | 首次注册时间（实体 RegisteredAtUtc） |
  | lastSeenAt | string (ISO-8601) | 最近上报/注册刷新时间 |
  | lastHeartbeatAt | string\|null | 恒为 null（映射函数未赋值，MobileDeviceService.cs:67-70） |
  | lastSyncAt | string\|null | 恒为 null（同上） |
  | isActive | boolean | 恒为 true（同上） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:64`；前端 `src/client-web/src/api/mobile.ts:753`（路径常量 mobile.ts:196）
- 备注：record 别名只读属性 `deviceHash`（=androidIdHash）、`osVersion`（=androidVersion）、`apiLevel`（=sdkInt）也会一并序列化输出（MobileDtos.cs:74-76），前端类型 `MobileDevice`（mobile.ts:265-282）未声明。列表按 lastSeenAtUtc 降序。

### GET /api/v1/mobile/devices/manage
- 用途：设备管理页列表，附带每台设备的数据量统计、存储估算与健康状态。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | sortBy | string | 否 | `"data"` 按会话+事件数降序；缺省/其他值按 lastSeenAtUtc 降序（DeviceManagementService.cs:61-62） |
- Body：无
- 响应 data：`DeviceListDto[]`（DeviceManagementService.cs:540）
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备唯一标识 |
  | displayName | string | 设备别名 |
  | brand | string | 品牌 |
  | model | string | 型号 |
  | osVersion | string | 系统版本 |
  | appVersion | string | 客户端版本 |
  | registeredAtUtc | string (ISO-8601) | 注册时间 |
  | lastSeenAtUtc | string (ISO-8601) | 最近活跃时间 |
  | isOnline | boolean | 当前时间 - lastSeenAtUtc < 5 分钟（DeviceManagementService.cs:56） |
  | sessionCount | number | 使用会话总数 |
  | eventCount | number | 使用事件总数 |
  | locationCount | number | 定位点总数 |
  | summaryCount | number | 使用汇总总数 |
  | earliest | string\|null | 最早会话开始时间 |
  | latest | string\|null | 最新会话开始时间 |
  | storageEstimateKb | number | 存储估算 KB（session×0.5 + event×0.3 + location×0.2 + summary×0.4，DeviceManagementService.cs:50） |
  | syncStatus | string | 同步状态枚举：`normal`（<1h）\| `delayed`（>1h）\| `disconnected`（>1天）（DeviceManagementService.cs:490-495） |
  | dataQuality | string | `normal` \| `abnormal`（存在 >8 小时的异常会话，DeviceManagementService.cs:40、496） |
  | storagePressure | string | `normal` \| `pending`（metadataJson.pendingUpload > 0，DeviceManagementService.cs:497-506） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:69`、`src/modules/Pim.Module.Mobile/Services/DeviceManagementService.cs:24`；前端 `src/client-web/src/api/mobile.ts:926`
- 备注：前端类型 `DeviceListItem`（mobile.ts:919-925）与后端一一对应。

### GET /api/v1/mobile/devices/{deviceId}/detail
- 用途：单设备详情（设备全量注册信息 + 数据量统计 + 最近 10 条同步批次 + 7 天在线时间线）。
- 认证：JWT
- Web 前端使用：是（设备详情页 DeviceDetailPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`DeviceDetailDto`（DeviceManagementService.cs:541）
  | 字段 | 类型 | 说明 |
  | device | object | MobileDeviceEntity 全量序列化（MobileDeviceEntity.cs:9-67） |
  | device.id | string (UUID) | 主键 |
  | device.userId | string (UUID) | 所属用户 |
  | device.deviceId | string | 设备唯一标识 |
  | device.deviceHash | string | Android ID 哈希 |
  | device.displayName | string | 设备别名 |
  | device.manufacturer / device.brand / device.model | string | 厂商/品牌/型号 |
  | device.osVersion | string | 系统版本 |
  | device.apiLevel | number | Android API Level |
  | device.appVersion | string | 客户端版本 |
  | device.metadataJson | string | 元数据 JSON 字符串 |
  | device.registeredAtUtc | string | 注册时间 |
  | device.lastSeenAtUtc | string | 最近活跃时间 |
  | device.createdAt / device.updatedAt | string | 行创建/更新时间 |
  | stats | object | 数据量统计（私有 record DeviceStats，DeviceManagementService.cs:536） |
  | stats.sessionCount | number | 会话总数 |
  | stats.eventCount | number | 事件总数 |
  | stats.locationCount | number | 定位点总数 |
  | stats.summaryCount | number | 汇总总数 |
  | stats.anomalousSessionCount | number | >8 小时的异常会话数 |
  | stats.earliest | string\|null | 最早会话开始 |
  | stats.latest | string\|null | 最新会话开始 |
  | stats.storageEstimateKb | number | 存储估算 KB（DeviceManagementService.cs:532） |
  | syncHistory | DeviceSyncHistoryDto[] | 最近 10 条同步批次（DeviceManagementService.cs:72） |
  | syncHistory[].batchId | string | 客户端批次 ID |
  | syncHistory[].createdAt | string | 批次创建时间 |
  | syncHistory[].acceptedCount | number | 接受条数 |
  | syncHistory[].status | string | 批次状态（见 GET /mobile/summary 备注） |
  | healthTimeline | string[] | 7 天在线标签，每项 `"yyyy-MM-dd:online"` 或 `"yyyy-MM-dd:offline"`，今天在最前（DeviceManagementService.cs:510-520） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:71`、`Services/DeviceManagementService.cs:66`；前端 `src/client-web/src/api/mobile.ts:942`
- 备注：设备不存在时 DomainException 04004 → HTTP 404（"设备不存在"）。healthTimeline 只有"当天有活跃"一天为 online（DeviceManagementService.cs:517），其余 6 天恒 offline——是简化推断而非真实历史。前端类型为 `any`。

### POST /api/v1/mobile/devices/{deviceId}/rename
- 用途：重命名设备别名。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：`DeviceRenameRequest`（MobileModule.cs:542）
  | 字段 | 类型 | 必填 | 说明 |
  | displayName | string | 是 | 新别名；非空且 ≤50 字符，否则 DomainException 04000 → HTTP 400（"别名长度需 1-50"，DeviceManagementService.cs:79） |
- 响应 data：`DeviceDto`
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备唯一标识 |
  | displayName | string | 更新后的别名 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:73`、`Services/DeviceManagementService.cs:77`；前端 `src/client-web/src/api/mobile.ts:929`
- 备注：设备不存在 → 04004 → HTTP 404。前端丢弃响应体（`.then(() => {})`）。

### POST /api/v1/mobile/devices/merge/preview
- 用途：合并设备前的预览：每台设备（源+目标）的数据条数与总条数。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage，合并对话框）
- Path 参数：无
- Query 参数：无
- Body：`DeviceMergeRequest`（MobileModule.cs:543）
  | 字段 | 类型 | 必填 | 说明 |
  | sourceDeviceIds | string[] | 是 | 源设备 ID 列表；null/空数组 → DomainException 04001 → HTTP 400（"至少需要选择一台源设备"，DeviceManagementService.cs:93） |
  | targetDeviceId | string | 是 | 目标设备 ID |
- 响应 data：`DeviceMergePreviewDto`（DeviceManagementService.cs:545）
  | 字段 | 类型 | 说明 |
  | items | DeviceMergeItemDto[] | 每台设备的行数 |
  | items[].deviceId | string | 设备 ID（含目标设备） |
  | items[].dataCount | number | 该设备 session+event+location+summary 总行数 |
  | total | number | 全部设备行数合计 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:75`、`Services/DeviceManagementService.cs:89`；前端 `src/client-web/src/api/mobile.ts:932`
- 备注：部分设备不存在或不属于当前用户 → 04004 → HTTP 404。前端返回类型 `any`。

### POST /api/v1/mobile/devices/merge
- 用途：执行设备合并：源设备数据迁入目标设备后删除源设备。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage，合并对话框）
- Path 参数：无
- Query 参数：无
- Body：`DeviceMergeRequest`（同 merge/preview）
  | 字段 | 类型 | 必填 | 说明 |
  | sourceDeviceIds | string[] | 是 | 源设备 ID 列表；空 → 04001 → HTTP 400；包含 targetDeviceId → 04001 → HTTP 400（DeviceManagementService.cs:119-120） |
  | targetDeviceId | string | 是 | 目标设备 ID |
- 响应 data：`string`，固定 `"merged"`（MobileModule.cs:80）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:77`、`Services/DeviceManagementService.cs:114`；前端 `src/client-web/src/api/mobile.ts:935`
- 备注：合并规则——events/summaries/sync_batches/取证事件按唯一键去重（目标已有行优先），其余整行迁移；丢弃原因按天按原因**相加**；App 名称库按新鲜度取舍保留一行；派生数据（timeline blocks / aggregates / materialization）双端全部删除待重新物化（DeviceManagementService.cs:137-245）。

### GET /api/v1/mobile/devices/{deviceId}/delete-preview
- 用途：删除设备前的预览：将级联删除的各类数据条数。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage，删除确认框）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`DeviceDeletePreviewDto`（DeviceManagementService.cs:546）
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备唯一标识 |
  | displayName | string | 设备别名 |
  | sessionCount | number | 将删除的会话数 |
  | eventCount | number | 将删除的事件数 |
  | locationCount | number | 将删除的定位点数 |
  | summaryCount | number | 将删除的汇总数 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:82`、`Services/DeviceManagementService.cs:408`；前端 `src/client-web/src/api/mobile.ts:938`
- 备注：设备不存在 → 04004 → HTTP 404。前端返回类型 `any`。

### DELETE /api/v1/mobile/devices/{deviceId}
- 用途：删除设备及其全部移动端数据（事件/会话/汇总/定位/批次/块/聚合/物化/App 目录/取证事件/丢弃统计）。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`string`，固定 `"deleted"`（MobileModule.cs:87）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:84`、`Services/DeviceManagementService.cs:417`；前端 `src/client-web/src/api/mobile.ts:941`
- 备注：设备不存在 → 04004 → HTTP 404；最近 30 分钟内仍有 pending/processing/syncing 批次 → 04002 → HTTP 400（"设备正在同步，禁止删除"，DeviceManagementService.cs:438-448）。

### GET /api/v1/mobile/devices/{deviceId}/export
- 用途：导出单设备数据为 JSON 文件下载（各表最新 5000 条 + truncated 标记）。
- 认证：JWT
- Web 前端使用：是（设备管理页 DeviceManagementPage；因需 Authorization 下载二进制，前端用原生 fetch→blob 而非 apiGet）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：无
- Body：无
- 响应：**非 ApiResponse 封装**——`application/json` 文件流（`Results.File`），响应头 `Content-Disposition` 文件名 `pim-export-{别名净化}-{yyyyMMdd}.json`（DeviceManagementService.cs:477-480）。文件体结构：
  | 字段 | 类型 | 说明 |
  | device | string | 设备 deviceId |
  | sessions | MobileUsageSessionEntity[] | 会话，按 StartUtc 降序最多 5000 条 |
  | events | MobileUsageEventEntity[] | 事件，按 EventTimestampUtc 降序最多 5000 条 |
  | locations | MobileLocationPointEntity[] | 定位点，按 RecordedAtUtc 降序最多 5000 条 |
  | summaries | MobileUsageSummaryEntity[] | 汇总，按 WindowStartUtc 降序最多 5000 条 |
  | truncated | boolean | 任一表达到 5000 条上限（DeviceManagementService.cs:481） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:89`、`Services/DeviceManagementService.cs:467`；前端 `src/client-web/src/api/mobile.ts:943`
- 备注：设备不存在 → 04004 → HTTP 404。前端从 `localStorage.accessToken`（回退 `pim_token`）取 token 放入 `Authorization` 头（mobile.ts:943）。

### POST /api/v1/mobile/devices/register
- 用途：Android 客户端设备注册（存在则更新注册信息并刷新 lastSeenAt）。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileDeviceRegisterRequest`（MobileDtos.cs:5-15）
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | androidIdHash | string | 否 | Android ID 哈希（映射到 deviceHash，null 时存空串） |
  | displayName | string | 是 | 设备别名 |
  | manufacturer | string | 是 | 厂商 |
  | brand | string | 是 | 品牌 |
  | model | string | 是 | 型号 |
  | androidVersion | string | 是 | 系统版本（映射 OsVersion） |
  | sdkInt | number | 是 | API Level（映射 ApiLevel） |
  | appVersion | string | 是 | 客户端版本 |
  | metadataJson | string | 是 | 元数据 JSON 字符串（空/白值存 "{}"） |
- 响应 data：`MobileDeviceDto`（字段同 GET /mobile/devices 的元素）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:95`、`Services/MobileDeviceService.cs:22`；前端 `src/client-web/src/api/mobile.ts` 无封装（mobileApiPaths 无 register）
- 备注：同 deviceId 重复注册为幂等更新。metadataJson 中 `deviceKind`（phone/tablet）或 `smallestScreenWidthDp`（≥600 判平板）会被存活页的机型分块读取（MobileLivenessService.cs:473-510）。

---

## 设备上报（Android 客户端）

### POST /api/v1/mobile/sync/gaps
- 用途：客户端查询指定窗口内服务端还缺哪些数据段，用于断点补传。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileGapRequest`（MobileDtos.cs:79-83）
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | rangeStartUtc | string (ISO-8601) | 是 | 查询窗口起点（早于 14 天前会被夹紧到 now-14d） |
  | rangeEndUtc | string (ISO-8601) | 是 | 查询窗口终点（晚于 now 被夹紧到 now） |
  | capabilityJson | string | 是 | 客户端能力 JSON（原样回显到每个窗口的 sourcePreference） |
- 响应 data：`MobileGapResponse`（MobileDtos.cs:97-99）
  | 字段 | 类型 | 说明 |
  | maxBackfillStartUtc | string | 允许补传的最早时刻（now - 14 天，MobileGapService.cs:12、40） |
  | windows | MobileGapWindowDto[] | 缺口窗口列表（每窗口=1 天切片，缺口 <5 分钟不报） |
  | windows[].windowStartUtc | string | 缺口起点 |
  | windows[].windowEndUtc | string | 缺口终点 |
  | windows[].reason | string | 枚举：`missing-tail`（尾部缺失）\| `partial-day`（当天部分缺失）\| `fallback-only`（仅 fallback 汇总覆盖）\| `missing-day`（整日无数据）（MobileGapService.cs:151-163） |
  | windows[].sourcePreference | string | 回显请求的 capabilityJson |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:101`、`Services/MobileGapService.cs:37`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：覆盖判据 = 非fallback汇总窗口 + 无失败条目的 completed/completed-with-errors 批次窗口 + 事件源窗口（MobileGapService.cs:56-76）。

### POST /api/v1/mobile/usage/events
- 用途：使用事件 + 应用元数据 + fallback 汇总的批量上报主通道（写入 mobile_sync_batches 并逐条入库）。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileUsageEventsUploadRequest`（MobileDtos.cs:101-108）
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | clientBatchId | string | 是 | 客户端批次幂等 ID |
  | sourceWindowStartUtc | string (ISO-8601) | 是 | 采集窗口起点 |
  | sourceWindowEndUtc | string (ISO-8601) | 是 | 采集窗口终点 |
  | apps | MobileAppMetadataDto[] | 是 | 应用元数据数组 |
  | apps[].packageName | string | 是 | 包名 |
  | apps[].displayName | string | 是 | 应用名（≤256 字符） |
  | apps[].versionName | string\|null | 否 | 版本名（≤128） |
  | apps[].versionCode | number | 是 | 版本号（≥0） |
  | apps[].isSystemApp | boolean | 是 | 是否系统应用 |
  | apps[].categoryName | string\|null | 否 | 系统分类名（≤128） |
  | apps[].installerPackageName | string\|null | 否 | 安装来源包名 |
  | apps[].firstInstallTimeUtc | string\|null | 否 | 首次安装时间 |
  | apps[].lastUpdateTimeUtc | string\|null | 否 | 最近更新时间（不得早于首次安装） |
  | apps[].rawJson | string | 是 | 设备端原始 JSON |
  | apps[].clientItemKey | string\|null | 否 | 条目幂等键（缺省服务端派生） |
  | events | MobileUsageEventDto[] | 是 | 使用事件数组 |
  | events[].packageName | string | 是 | 包名 |
  | events[].eventType | string | 是 | 事件类型（如 ACTIVITY_RESUMED / ACTIVITY_PAUSED，≤64） |
  | events[].eventTimestampUtc | string (ISO-8601) | 是 | 事件时间 |
  | events[].className | string\|null | 否 | 组件类名（≤512） |
  | events[].collectedAtUtc | string (ISO-8601) | 是 | 采集时间 |
  | events[].rawJson | string | 是 | 原始 JSON |
  | events[].clientItemKey | string\|null | 否 | 条目幂等键 |
  | fallbackSummaries | MobileUsageSummaryDto[] | 是 | fallback 汇总数组 |
  | fallbackSummaries[].packageName | string | 是 | 包名 |
  | fallbackSummaries[].windowStartUtc | string | 是 | 汇总窗口起点 |
  | fallbackSummaries[].windowEndUtc | string | 是 | 汇总窗口终点 |
  | fallbackSummaries[].totalTimeForegroundMs | number | 是 | 前台毫秒数 |
  | fallbackSummaries[].lastTimeUsedUtc | string\|null | 否 | 最近使用时间 |
  | fallbackSummaries[].sourceKind | string | 是 | 来源标识（含 fallback/summary 字样即按 fallback 处理） |
  | fallbackSummaries[].rawJson | string | 是 | 原始 JSON |
  | fallbackSummaries[].clientItemKey | string\|null | 否 | 条目幂等键 |
- 响应 data：`MobileUsageIngestResult`（MobileDtos.cs:162-168）
  | 字段 | 类型 | 说明 |
  | batchId | string | 服务端批次 ID |
  | acceptedCount | number | 接受条数 |
  | skippedCount | number | 跳过（重复）条数 |
  | rejectedCount | number | 拒绝条数 |
  | failedCount | number | 失败条数 |
  | itemResults | MobileIngestItemResult[] | 逐条结果 |
  | itemResults[].clientItemKey | string | 条目幂等键 |
  | itemResults[].entityType | string | `app-metadata` \| `usage-event` \| `usage-summary` |
  | itemResults[].outcome | string | `accepted` \| `skipped` \| `rejected` \| `failed` |
  | itemResults[].code | string | 结果码：`accepted`/`duplicate`/校验拒绝码（`invalid-display-name`、`invalid-version-name`、`invalid-version-code`、`invalid-category-name`、`invalid-installer-package`、`invalid-time`、`invalid-event-type`、`invalid-class-name` 等） |
  | itemResults[].message | string | 结果说明 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:107`、`Services/MobileUsageIngestService.cs`（校验码 :626-664，计数 :507-510）；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：幂等键 = (user, device, clientItemKey)，重复提交命中唯一约束按 skipped 处理。

### POST /api/v1/mobile/forensics/events
- 用途：取证事件批量上报（进程退出原因/强停/心跳等存活取证 + 按天按原因的定位丢弃统计）。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileForensicsUploadRequest`（MobileForensicDtos.cs:28-32）
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | batchId | string\|null | 否 | 批次幂等 ID |
  | events | MobileForensicEventUploadItem[]\|null | 否 | 取证事件数组 |
  | events[].clientItemKey | string | 是 | 事件幂等键 |
  | events[].eventType | string | 是 | 事件类型：`process-exit` \| `force-stop` \| `heartbeat`（Pim.Core/Liveness/DeviceLivenessModels.cs:15-21） |
  | events[].occurredAtUtc | string (ISO-8601) | 是 | 发生时间 |
  | events[].payloadJson | string\|null | 否 | 设备端负载 JSON（jsonb 原样保存，服务端不做字段级校验） |
  | droppedReasonSummaries | MobileDroppedReasonSummaryItem[]\|null | 否 | 丢弃统计数组 |
  | droppedReasonSummaries[].localDate | string | 是 | 本地日 `yyyy-MM-dd` |
  | droppedReasonSummaries[].reason | string | 是 | 丢弃原因标识 |
  | droppedReasonSummaries[].count | number | 是 | 条数（同键重复上报覆盖不累加，MobileEntityConfigurations.cs:37-38） |
- 响应 data：`MobileForensicsIngestResult`（MobileForensicDtos.cs:35-43）
  | 字段 | 类型 | 说明 |
  | acceptedCount | number | 接受条数 |
  | skippedCount | number | 跳过条数 |
  | rejectedCount | number | 拒绝条数 |
  | failedCount | number | 失败条数 |
  | acceptedKeys | string[] | 接受的 clientItemKey 列表 |
  | skippedKeys | string[] | 跳过的 clientItemKey 列表 |
  | rejectedKeys | string[] | 拒绝的 clientItemKey 列表 |
  | acceptedDroppedReasonCount | number | 接受的丢弃统计行数 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:114`、`Services/MobileForensicIngestService.cs`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：幂等键 = (user, device, clientItemKey)（MobileEntityConfigurations.cs:25-26）；设备合并时取证事件随之迁移（DeviceManagementService.cs:226-231）。

### POST /api/v1/mobile/location/points
- 用途：单个定位点上报。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileLocationPointRequest`（MobileDtos.cs:186-201）
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识 |
  | recordedAtUtc | string (ISO-8601) | 是 | 定位时间 |
  | latitude | number | 是 | 纬度 |
  | longitude | number | 是 | 经度 |
  | horizontalAccuracyMeters | number | 是 | 水平精度（米）；<50 记 usable，≥50 记 rejected（MobileLocationService.cs:17、131） |
  | provider | string | 是 | 定位来源（gps/network/fused 等） |
  | sourceKind | string | 是 | 上报来源标识 |
  | altitudeMeters | number\|null | 否 | 海拔 |
  | verticalAccuracyMeters | number\|null | 否 | 垂直精度 |
  | speedMetersPerSecond | number\|null | 否 | 速度 |
  | speedAccuracyMetersPerSecond | number\|null | 否 | 速度精度 |
  | bearingDegrees | number\|null | 否 | 方向角 |
  | bearingAccuracyDegrees | number\|null | 否 | 方向角精度 |
  | isAutoSubmitted | boolean | 是 | 是否自动提交 |
  | rawJson | string | 是 | 原始 JSON |
- 响应 data：`MobileLocationPointDto`（MobileDtos.cs:220-238；字段同 GET /mobile/location/history 的 points 元素）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:164`、`Services/MobileLocationService.cs:44`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：坐标非法 → 条目级 `rejected`（`invalid-coordinates`）；同自然键已存在则按重复/精度升级处理。

### POST /api/v1/mobile/location/points/batch
- 用途：定位点批量补传（客户端积压时一次上传多点，逐条返回结果）。
- 认证：JWT
- Web 前端使用：否（调用方：Android 客户端）
- Path 参数：无
- Query 参数：无
- Body：`MobileLocationPointsUploadRequest`（MobileDtos.cs:207-208）
  | 字段 | 类型 | 必填 | 说明 |
  | points | MobileLocationPointRequest[] | 是 | 定位点数组（元素字段同 POST /mobile/location/points 的 Body） |
- 响应 data：`MobileLocationPointsUploadResult`（MobileDtos.cs:214-218）
  | 字段 | 类型 | 说明 |
  | acceptedCount | number | 接受条数 |
  | skippedCount | number | 重复跳过条数 |
  | rejectedCount | number | 拒绝条数 |
  | itemResults | MobileIngestItemResult[] | 逐条结果（entityType 固定 `location-point`；code：`accepted`/`duplicate`/`invalid-device-id`/`invalid-coordinates`/`unusable-accuracy`，MobileLocationService.cs:113-155、390-397） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:171`、`Services/MobileLocationService.cs:101`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：precision ≥50m 的点入库为 quality=rejected，不算失败。

---

## 使用摘要与时间线

### GET /api/v1/mobile/summary
- 用途：按业务日（或任意 UTC 窗口）返回使用时长总览、应用排行与同步批次摘要。
- 认证：JWT
- Web 前端使用：是（Android 今日嵌入页 AndroidTodayEmbedPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日 `yyyy-MM-dd`；解析成功时按 `[D 04:00, D+1 04:00)` Asia/Shanghai 换算为 UTC 窗口（BusinessDay.cs:36-48），优先于 rangeStartUtc/rangeEndUtc（MobileModule.cs:550-561） |
  | deviceId | string | 否 | 过滤单设备；缺省跨设备汇总 |
  | rangeStartUtc | string (ISO-8601) | 否 | 显式窗口起点（date 缺省时生效） |
  | rangeEndUtc | string (ISO-8601) | 否 | 显式窗口终点 |
- Body：无
- 响应 data：`MobileUsageSummaryResponse`（MobileDtos.cs:368-380；构建 MobileUsageQueryService.cs:106-118）
  | 字段 | 类型 | 说明 |
  | date | string | 业务日标签（窗口起点或今天换算的业务日 `yyyy-MM-dd`，MobileUsageQueryService.cs:454-455） |
  | deviceId | string\|null | 查询的设备 ID |
  | generatedAt | string | 服务端生成时间 |
  | totalForegroundSeconds | number | 前台总秒数（去重后汇总求和） |
  | fallbackForegroundSeconds | number | 其中 fallback 汇总来源的秒数 |
  | appSwitchCount | number | 窗口内会话总数（按会话条数，不按应用去重） |
  | appsUsed | number | 去重包名数 |
  | completeness | number | 0-1 完整度：纯 fallback 取 0.65，减去失败批次惩罚（每批次 0.1，上限 0.3）（MobileUsageQueryService.cs:441-449） |
  | lastSyncAt | string\|null | 最近批次完成时间（无批次为 null） |
  | appRanking | MobileAppUsageSummaryDto[] | 应用排行（按时长降序前 50） |
  | appRanking[].packageName | string | 包名 |
  | appRanking[].displayName | string | 应用名（目录缺失时回退包名） |
  | appRanking[].categoryName | string\|null | 分类名 |
  | appRanking[].foregroundSeconds | number | 前台秒数 |
  | appRanking[].sessionCount | number | 会话数 |
  | appRanking[].launchCount | number | 启动数（eventType 含 FOREGROUND 的事件数） |
  | appRanking[].lastUsedAt | string\|null | 最近使用时间 |
  | appRanking[].source | string | `events`（任一非 fallback 行）\| `fallback` |
  | appRanking[].share | number | 占总时长比例（0-1） |
  | syncBatches | MobileSyncBatchSummaryDto[] | 最近 20 条批次（按 createdAt 降序） |
  | syncBatches[].id | string (UUID) | 批次主键 |
  | syncBatches[].deviceId | string | 设备 ID |
  | syncBatches[].clientBatchId | string | 客户端批次 ID |
  | syncBatches[].sourceWindowStartUtc | string | 采集窗口起点 |
  | syncBatches[].sourceWindowEndUtc | string | 采集窗口终点 |
  | syncBatches[].submittedAtUtc | string | 完成时间（CompletedAtUtc ?? CreatedAt） |
  | syncBatches[].status | string | 批次状态：`pending` \| `completed` \| `failed` \| `completed-with-errors`（历史遗留）等（MobileSyncBatchStatus.cs:16-23） |
  | syncBatches[].acceptedEventCount | number | 接受事件数 |
  | syncBatches[].skippedEventCount | number | 跳过事件数 |
  | syncBatches[].rejectedItemCount | number | 拒绝条目数 |
  | syncBatches[].acceptedLocationCount | number | 窗口内非 rejected 定位点数（按窗口匹配回填） |
  | syncBatches[].rejectedLocationCount | number | 窗口内 rejected 定位点数 |
  | syncBatches[].errorMessage | string\|null | 批次错误信息 |
  | qualityIssueCount | number | 失败批次计数（failedCount>0 或 status=failed） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:177`、`Services/MobileUsageQueryService.cs:23`；前端 `src/client-web/src/api/mobile.ts:757`（路径构造 mobile.ts:197-201）
- 备注：汇总行先按 (设备, 包名小写, 本地小时) 去重保留最大值（MobileUsageQueryService.cs:465-477）。

### GET /api/v1/mobile/timeline
- 用途：按业务日返回会话与 fallback 汇总交织的合并时间线（服务端分页，避免单日数千条静默截断）。
- 认证：JWT
- Web 前端使用：否（mobile.ts 已封装 getMobileTimeline 但当前无页面调用；数据消费方曾为使用分析时间线，现由 timeline-blocks 端点替代）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日 `yyyy-MM-dd`（04:00 CST 切分，同 /mobile/summary） |
  | deviceId | string | 否 | 过滤单设备 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 显式窗口（date 缺省时生效） |
  | page | number | 否 | 页码，从 1 起；<1 视为 1（MobileDtos.cs:335） |
  | pageSize | number | 否 | 每页条数，默认 5000、夹紧到 [1, 50000]（MobileDtos.cs:309-316、338-339） |
- Body：无
- 响应 data：`MobileTimelineResponse`（MobileDtos.cs:414-427；构建 MobileUsageQueryService.cs:121-237）
  | 字段 | 类型 | 说明 |
  | date | string | 业务日标签 |
  | deviceId | string\|null | 查询的设备 ID |
  | generatedAt | string | 服务端生成时间 |
  | sessions | MobileTimelineItemDto[] | 当前页内 kind=session 的子集（向后兼容字段） |
  | fallbackSummaries | MobileTimelineItemDto[] | 当前页内 kind=fallback 的子集（向后兼容字段） |
  | items | MobileTimelineItemDto[] | 当前页合并流（全天有序，逐页拼接不乱序） |
  | items[].id | string | 行 ID（GUID 的 "N" 格式） |
  | items[].kind | string | `session` \| `fallback` |
  | items[].deviceId | string | 设备 ID |
  | items[].packageName | string | 包名 |
  | items[].displayName | string | 应用名（目录缺失回退包名） |
  | items[].start | string | 开始时间（会话 StartUtc / 汇总 WindowStartUtc） |
  | items[].end | string\|null | 结束时间（会话未结束时为 null） |
  | items[].durationSeconds | number | 时长秒数（fallback 不超过窗口长度） |
  | items[].source | string | `events`（session）\| `fallback`（汇总） |
  | items[].confidence | number | session=1，fallback=0.6（MobileUsageQueryService.cs:303-339） |
  | items[].reason | string | session 为空串，fallback 为 `"汇总数据"` |
  | page | number | 当前页码 |
  | pageSize | number | 当前页大小 |
  | totalCount | number | 合并流（sessions+fallbackSummaries）总条数 |
  | sessionTotalCount | number | 仅 sessions 总条数 |
  | fallbackTotalCount | number | 仅 fallbackSummaries 总条数 |
  | hasMore | boolean | 是否还有下一页 |
  | truncated | boolean | 与 hasMore 同义（true 表示"你看到的不是全部"） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:188`、`Services/MobileUsageQueryService.cs:121`；前端 `src/client-web/src/api/mobile.ts:761`（路径构造 mobile.ts:202-208）
- 备注：分页偏移 skip=(page-1)×pageSize 超过 200000（MaxReadableOffset）时返回 HTTP 400（明确错误，MobileModule.cs:204-208、MobileDtos.cs:332）。跳过页会返回空数组但 totalCount 等总数仍有效。

---

## 位置分析

### GET /api/v1/mobile/location/history
- 用途：原始定位点历史查询（地图描线用），按最大精度过滤并剔除 rejected 点。
- 认证：JWT
- Web 前端使用：否（mobile.ts 已封装 getMobileLocationHistory / mobileApiPaths.locations 但当前无页面调用；历史位置页走 location/analytics 端点）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 否 | 过滤单设备 |
  | start | string (ISO-8601) | 否 | 窗口起点（rangeStartUtc 的别名，start ?? rangeStartUtc） |
  | end | string (ISO-8601) | 否 | 窗口终点（rangeEndUtc 的别名） |
  | maxAccuracyMeters | number | 否 | 最大水平精度，>0 时生效，默认 50（MobileModule.cs:223） |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 同 start/end 的规范名（不传则不过滤时间） |
- Body：无
- 响应 data：`MobileLocationHistoryResponse`（MobileDtos.cs:429-434）
  | 字段 | 类型 | 说明 |
  | start | string\|null | 实际生效的窗口起点 |
  | end | string\|null | 实际生效的窗口终点 |
  | deviceId | string\|null | 查询的设备 ID |
  | maxAccuracyMeters | number | 实际生效的最大精度 |
  | points | MobileLocationPointDto[] | 定位点数组（过滤条件：horizontalAccuracy < maxAccuracy 且 quality ≠ rejected，MobileLocationService.cs:338-340） |
  | points[].id | string (UUID) | 点主键 |
  | points[].deviceId | string | 设备 ID |
  | points[].recordedAtUtc | string | 定位时间 |
  | points[].submittedAtUtc | string | 服务端落库时间（=recordedAtUtc 时为无独立提交时间） |
  | points[].latitude | number | 纬度 |
  | points[].longitude | number | 经度 |
  | points[].horizontalAccuracyMeters | number | 水平精度 |
  | points[].provider | string | 定位来源 |
  | points[].sourceKind | string | 上报来源标识 |
  | points[].altitudeMeters | number\|null | 海拔 |
  | points[].verticalAccuracyMeters | number\|null | 垂直精度 |
  | points[].speedMetersPerSecond | number\|null | 速度 |
  | points[].speedAccuracyMetersPerSecond | number\|null | 速度精度 |
  | points[].bearingDegrees | number\|null | 方向角 |
  | points[].bearingAccuracyDegrees | number\|null | 方向角精度 |
  | points[].isAutoSubmitted | boolean | 是否自动提交 |
  | points[].quality | string | 点质量：后端实际只写 `usable` \| `rejected`（MobileLocationService.cs:22-23） |
  | points[].rawJson | string | 原始 JSON |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:211`、`Services/MobileLocationService.cs:320`；前端 `src/client-web/src/api/mobile.ts:770`（路径构造 mobile.ts:209-222）
- 备注：points[].source（=sourceKind）别名属性也会序列化输出（MobileDtos.cs:272）。前端类型含 `'high'` 质量枚举值（mobile.ts:371），但后端从未写入该值，属前端遗留。

### GET /api/v1/mobile/location/analytics/overview
- 用途：位置数据总览指标（点数/可用率/里程/停留/精度/质量旗标），走聚合缓存。
- 认证：JWT
- Web 前端使用：是（历史位置页 HistoricalLocationPage、Android 今日嵌入页 AndroidTodayEmbedPage）
- Path 参数：无
- Query 参数（公共组 `MobileLocationEndpointQuery`，MobileModule.cs:635-643；另加 force）：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc | string (ISO-8601) | 否 | 窗口起点；与 rangeEndUtc 都缺省时为"本地今天往前 7 天"（MobileLocationQueryService.cs:68-92） |
  | rangeEndUtc | string (ISO-8601) | 否 | 窗口终点；只给起点则默认 +7 天，只给终点则默认 -7 天 |
  | timezone | string | 否 | IANA 时区，默认 `Asia/Shanghai` |
  | deviceId | string | 否 | 过滤单设备 |
  | maxAccuracyMeters | number | 否 | 可用点精度上限，>0 生效，默认 50（MobileLocationQueryService.cs:36） |
  | includeRejected | boolean | 否 | 是否纳入 rejected 点参与统计，默认 false |
  | cursor | number/string | 否 | 本端点不使用（仅 segment points 使用） |
  | pageSize | number | 否 | 本端点不使用 |
  | force | boolean | 否 | true 跳过聚合缓存直接重算（MobileModule.cs:243） |
- Body：无
- 响应 data：`MobileLocationAnalyticsOverviewResponse`（MobileLocationAnalyticsDtos.cs:27-39；构建 MobileLocationAggregationService.cs:38-68）
  | 字段 | 类型 | 说明 |
  | range | MobileAnalyticsRangeDto | 实际生效的查询范围 |
  | range.rangeStartUtc | string | 窗口起点 |
  | range.rangeEndUtc | string | 窗口终点 |
  | range.timezone | string | 时区 |
  | range.localStartDate | string | 本地起始日 `yyyy-MM-dd` |
  | range.localEndDate | string | 本地结束日 `yyyy-MM-dd` |
  | generatedAt | string | 服务端生成时间 |
  | pointCount | number | 原始点数 |
  | usablePointCount | number | 可用点数 |
  | rejectedPointCount | number | rejected 点数（pointCount - usablePointCount） |
  | activeSpanSeconds | number | 首末可用点跨度秒数 |
  | distanceMeters | number | 按设备累计总里程（米，保留 1 位） |
  | stayCount | number | 停留段数 |
  | longestStaySeconds | number | 最长停留秒数 |
  | averageAccuracyMeters | number | 平均精度（米，保留 1 位） |
  | qualityIssueCount | number | rejected 点数 + 大间隙数 |
  | qualityFlags | string[] | 质量旗标（含 `jump-point` 等） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:238`、`Services/MobileLocationAggregationService.cs:38`；前端 `src/client-web/src/api/mobile.ts:774`（路径构造 mobile.ts:223-224）
- 备注：缓存键为完整请求 URL（AggregateResultCacheKeys.Build）；force=true 时不读缓存并回写。

### GET /api/v1/mobile/location/analytics/tracks
- 用途：轨迹列表：每条轨迹含 move/stay 分段与路径点（DBSCAN 聚合产物）。
- 认证：JWT
- Web 前端使用：是（历史位置页 HistoricalLocationPage、Android 今日嵌入页 AndroidTodayEmbedPage）
- Path 参数：无
- Query 参数：同 GET /mobile/location/analytics/overview 的公共组 `MobileLocationEndpointQuery` + force
- Body：无
- 响应 data：`MobileLocationTrackDto[]`（MobileLocationAnalyticsDtos.cs:70-81）
  | 字段 | 类型 | 说明 |
  | id | string | 轨迹 ID |
  | deviceId | string | 设备 ID |
  | startUtc / endUtc | string | 轨迹起止 |
  | distanceMeters | number | 轨迹里程 |
  | durationSeconds | number | 轨迹时长 |
  | pointCount | number | 点数 |
  | segmentCount | number | 分段数 |
  | bounds | MobileGeoBoundsDto\|null | 地理包围盒 |
  | bounds.minLatitude / minLongitude / maxLatitude / maxLongitude | number | 四至 |
  | qualityFlags | string[] | 质量旗标 |
  | segments | MobileLocationSegmentDto[] | 分段列表 |
  | segments[].id | string | 分段 ID（segment detail / segment points 用它定位） |
  | segments[].trackId | string | 所属轨迹 ID |
  | segments[].deviceId | string | 设备 ID |
  | segments[].kind | string | 分段类型：`move` \| `stay` \| `gap` \| `low-confidence` 等 |
  | segments[].startUtc / endUtc | string | 分段起止 |
  | segments[].localStart / localEnd | string | 本地起止时刻 |
  | segments[].durationSeconds | number | 分段时长 |
  | segments[].distanceMeters | number | 分段里程 |
  | segments[].pointCount | number | 点数 |
  | segments[].averageSpeedMetersPerSecond | number | 平均速度 |
  | segments[].averageAccuracyMeters / maxAccuracyMeters | number | 平均/最大精度 |
  | segments[].quality | string | 分段质量（usable/rejected 同口径） |
  | segments[].qualityFlags | string[] | 质量旗标 |
  | segments[].bounds | MobileGeoBoundsDto\|null | 分段包围盒 |
  | segments[].path | MobileLocationPathPointDto[] | 路径点 |
  | segments[].path[].id | string | 点 ID |
  | segments[].path[].recordedAtUtc | string | 时间 |
  | segments[].path[].latitude / longitude | number | 坐标 |
  | segments[].path[].horizontalAccuracyMeters | number | 精度 |
  | segments[].path[].quality | string | 点质量 |
  | segments[].path[].qualityFlags | string[] | 质量旗标 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:251`、`Services/MobileLocationAggregationService.cs:70`；前端 `src/client-web/src/api/mobile.ts:782`（路径构造 mobile.ts:225-226）
- 备注：move 段同时被 movement-stats 复用计算里程（MobileFrequentPlaceDtos.cs:15-20）。

### GET /api/v1/mobile/location/analytics/frequent-places
- 用途：常去地点（DBSCAN 聚类 + 家判定：夜间点最多的簇）。
- 认证：JWT
- Web 前端使用：是（历史位置页 HistoricalLocationPage）
- Path 参数：无
- Query 参数：同 GET /mobile/location/analytics/overview 的公共组 `MobileLocationEndpointQuery` + force
- Body：无
- 响应 data：`MobileFrequentPlacesResponse`（MobileFrequentPlaceDtos.cs:11-13；构建 MobileFrequentPlaceService.cs:47、121-130）
  | 字段 | 类型 | 说明 |
  | home | MobileFrequentPlaceDto\|null | 判定为"家"的地点（无聚类时为 null） |
  | places | MobileFrequentPlaceDto[] | 全部常去地点（含 home，按点数降序、纬度升序） |
  | home（及 places[] 元素）.centerLatitude | number | 聚类中心纬度 |
  | （同上）.centerLongitude | number | 聚类中心经度 |
  | （同上）.radiusMeters | number | 聚类半径（夹紧到 [0,500] 米，保留 1 位） |
  | （同上）.pointCount | number | 簇内点数 |
  | （同上）.visitDayCount | number | 覆盖的本地日数 |
  | （同上）.isHome | boolean | 是否家 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:283`、`Services/MobileFrequentPlaceService.cs:37`；前端 `src/client-web/src/api/mobile.ts:801`（路径构造 mobile.ts:231-232）
- 备注：家=夜间点最多的簇（平局取点数多），无夜间点时退化为点数最多的簇（MobileFrequentPlaceService.cs:122-127）。前端会把 home 合并进 places 并按坐标去重（HistoricalLocationPage.tsx:155-166）。

### GET /api/v1/mobile/location/analytics/movement-stats
- 用途：出行统计（以"家"为锚：外出次数/时长/单次明细/总里程/最高速度/逐日汇总）。
- 认证：JWT
- Web 前端使用：是（历史位置页 HistoricalLocationPage）
- Path 参数：无
- Query 参数：同 GET /mobile/location/analytics/overview 的公共组 `MobileLocationEndpointQuery` + force
- Body：无
- 响应 data：`MobileMovementStatsResponse`（MobileFrequentPlaceDtos.cs:35-42；构建 MobileMovementStatsService.cs:45-76）
  | 字段 | 类型 | 说明 |
  | homeCenter | object\|null | 家中心坐标（无家时为 null，此时外出统计全为 0） |
  | homeCenter.latitude / homeCenter.longitude | number | 纬度/经度 |
  | outingCount | number | 外出次数 |
  | outingSeconds | number | 外出总秒数 |
  | outings | MobileOutingDto[] | 单次外出明细 |
  | outings[].startUtc / endUtc | string | 外出起止 |
  | outings[].seconds | number | 外出时长秒数 |
  | distanceMeters | number | 总里程（全部 move 段求和，保留 1 位） |
  | maxSpeedMetersPerSecond | number\|null | 最高速度（由可用点与 move 段计算） |
  | perDay | MobileMovementStatsDayDto[] | 逐日汇总 |
  | perDay[].date | string | 本地日 `yyyy-MM-dd` |
  | perDay[].outingCount | number | 当日外出次数 |
  | perDay[].outingSeconds | number | 当日外出秒数 |
  | perDay[].distanceMeters | number | 当日里程 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:296`、`Services/MobileMovementStatsService.cs:45`；前端 `src/client-web/src/api/mobile.ts:809`（路径构造 mobile.ts:233-234）
- 备注：无家地点时仍返回 distanceMeters 与 perDay（移动统计不依赖家）。

### GET /api/v1/mobile/location/analytics/segments/{segmentId}
- 用途：单个分段详情（从 tracks 的聚合结果中按 ID 查找）。
- 认证：JWT
- Web 前端使用：否（mobile.ts 已封装 getMobileLocationAnalyticsSegment 但当前无页面调用；历史位置页直接消费 tracks 内嵌的 segments）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | segmentId | string | 是 | 分段 ID（URL 编码，来自 tracks[].segments[].id） |
- Query 参数：同 GET /mobile/location/analytics/overview 的公共组 `MobileLocationEndpointQuery` + force
- Body：无
- 响应 data：`MobileLocationSegmentDto`（字段同 tracks[].segments[] 元素，见上）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:264`、`Services/MobileLocationAggregationService.cs:99`；前端 `src/client-web/src/api/mobile.ts:788`（路径构造 mobile.ts:227-228）
- 备注：找不到分段返回 HTTP 404（"Location segment not found."，MobileModule.cs:278-280）。

### GET /api/v1/mobile/location/analytics/segments/{segmentId}/points
- 用途：分段的原始定位点游标分页（长分段按页取点）。
- 认证：JWT
- Web 前端使用：是（历史位置页 HistoricalLocationPage，原始点表分页）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | segmentId | string | 是 | 分段 ID（URL 编码） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 窗口（默认最近 7 天，同公共组） |
  | timezone | string | 否 | 默认 `Asia/Shanghai` |
  | deviceId | string | 否 | 过滤单设备 |
  | maxAccuracyMeters | number | 否 | 默认 50 |
  | includeRejected | boolean | 否 | 默认 false |
  | cursor | string | 否 | 上一页返回的 nextCursor（= 最后一个点的 UUID）；不传从第一页开始（MobileLocationAggregationService.cs:134-138） |
  | pageSize | number | 否 | 默认 50，夹紧到 [1,200]（MobileLocationQueryService.cs:9-10、32） |
- Body：无
- 响应 data：`MobileLocationSegmentPointPageDto`（MobileLocationAnalyticsDtos.cs:83-86）
  | 字段 | 类型 | 说明 |
  | items | MobileLocationPointDto[] | 当前页定位点（字段同 GET /mobile/location/history 的 points 元素） |
  | nextCursor | string\|null | 下一页游标（最后一点的 ID；hasMore=false 时为 null） |
  | hasMore | boolean | 是否还有下一页 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:309`、`Services/MobileLocationAggregationService.cs:117-151`；前端 `src/client-web/src/api/mobile.ts:792`（路径构造 mobile.ts:229-230）
- 备注：cursor 语义——游标即"上一页最后一点的 Id"，服务端在段内点数组中定位该 ID 后取其后 pageSize 条。本端点**不走聚合缓存**（模块中未包 cache）。前端 pageSize 固定 200（=上限），用 cursor 栈实现上一页回退：pageIndex>0 时取 cursorStack[pageIndex-1]，切换分段时清栈（HistoricalLocationPage.tsx:103-127）。

---

## 使用分析

### GET /api/v1/mobile/quality
- 用途：移动端采集质量体检（心跳/使用覆盖/同步/定位/应用元数据/数据可信度六组件 + 问题清单）。
- 认证：JWT
- Web 前端使用：是（状态页 StatusPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日 `yyyy-MM-dd`（04:00 CST 切分，同 /mobile/summary；MobileModule.cs:339 复用 BuildSummaryQuery） |
  | deviceId | string | 否 | 过滤单设备 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 显式窗口 |
- Body：无
- 响应 data：`MobileQualityResponse`（MobileDtos.cs:436-460；构建 MobileQualityService.cs:151-185）
  | 字段 | 类型 | 说明 |
  | overallStatus | string | PimHealthStatus 枚举：`unknown` \| `healthy` \| `warning` \| `critical`（OperationEnums.cs:5-11；序列化为数字或名称取决于 JSON 枚举转换器，前端按 PimHealthStatus 处理） |
  | label | string | 总体状态中文标签（"Android 采集正常"等） |
  | message | string | 总体说明 |
  | checkedAt | string | 检查时间 |
  | components | MobileQualityComponentDto[] | 组件状态列表 |
  | components[].key | string | 组件键：`android-heartbeat` \| `mobile-usage-coverage` \| `mobile-sync` \| `mobile-location` \| `mobile-app-metadata` \| `data_reliability`（MobileQualityService.cs:152-174、213-221、585） |
  | components[].name | string | 组件中文名 |
  | components[].status | string | 同 overallStatus 枚举 |
  | components[].message | string | 组件说明 |
  | components[].checkedAt | string | 检查时间 |
  | components[].details | Record<string,string> | 组件明细键值（如 missingPackages、redRules 等） |
  | issues | MobileQualityIssueDto[] | 问题清单 |
  | issues[].code | string | 问题码（`mobile-heartbeat-missing`/`mobile-heartbeat-stale`/`mobile-heartbeat-error`/`mobile-heartbeat-upload-queue`/`mobile-usage-missing`/`mobile-app-metadata-missing`/数据可信度尺子规则码等） |
  | issues[].severity | string | 同 overallStatus 枚举 |
  | issues[].componentKey | string | 归属组件键 |
  | issues[].message | string | 问题描述 |
  | issues[].nextStep | string\|null | 建议动作 |
  | nextSteps | string[] | 去重后的建议动作列表 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:331`、`Services/MobileQualityService.cs`；前端 `src/client-web/src/api/mobile.ts:817`（路径构造 mobile.ts:235-239）
- 备注：清单中的 `dateFrom/dateTo` 参数在**后端实际未注册**，真实参数为 rangeStartUtc/rangeEndUtc（MobileModule.cs:331-337）。

### GET /api/v1/mobile/analytics/overview
- 用途：使用分析总览（总时长/日均/分类峰值/App 数/切换数/完整度/质量摘要/目标进度/异常/建议）。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage、Android 今日嵌入页 AndroidTodayEmbedPage）
- Path 参数：无
- Query 参数（公共组 `MobileAnalyticsEndpointQuery`，MobileModule.cs:603-616；另加 force）：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc | string (ISO-8601) | 否 | 窗口起点；两端都缺省为"本地今天-6 天 00:00 → 明天 00:00"（7 天，MobileAnalyticsQueryService.cs:81-95） |
  | rangeEndUtc | string (ISO-8601) | 否 | 窗口终点；只给起点默认 +7 天，只给终点默认 -7 天；start > end 抛错 |
  | timezone | string | 否 | IANA 时区，默认 `Asia/Shanghai` |
  | deviceId | string | 否 | 过滤单设备 |
  | category | string | 否 | 生活分类过滤（取值：`编程/折腾`、`学习`、`视频`、`聊天`、`文档`、`游戏`、`其他`；`工具/系统` 仅系统噪音，不进用户可选列表，MobileAnalyticsDtos.cs:17-43） |
  | packageName | string | 否 | 包名过滤 |
  | source | string | 否 | 来源过滤（events/fallback） |
  | includeSystemNoise | boolean | 否 | 是否纳入系统噪音，默认 false |
  | minDurationSeconds | number | 否 | 最短时长阈值，默认 1 秒、下限 0（MobileAnalyticsDtos.cs:8） |
  | granularity | string | 否 | 粒度：`hour`（默认）\| `30m` \| `15m` \| `day`（非法值回退 hour，MobileAnalyticsQueryService.cs:12-18、54-58） |
  | cursor | string | 否 | 本端点不使用 |
  | page / pageSize | number | 否 | 本端点不使用（pageSize 默认 50、上限 200，MobileAnalyticsDtos.cs:6-7） |
  | force | boolean | 否 | true 跳过聚合缓存（MobileModule.cs:352） |
- Body：无
- 响应 data：`MobileAnalyticsOverviewResponse`（MobileAnalyticsDtos.cs:110-125；构建 MobileUsageAggregationService.cs:36-106）
  | 字段 | 类型 | 说明 |
  | range | MobileAnalyticsRangeDto | 实际生效范围（字段同位置分析 range） |
  | generatedAt | string | 生成时间 |
  | isStale | boolean | 参与聚合的行是否含过期物化数据 |
  | totalForegroundSeconds | number | 去重并集总前台秒数 |
  | dailyAverageSeconds | number | 日均秒数（总秒数 ÷ 本地日数，最少 1 天） |
  | previousPeriodChange | number | 恒为 0（占位，未实现环比） |
  | highestUseLocalDate | string\|null | 使用最长的本地日 |
  | peakLocalHour | number\|null | 使用最长的本地小时（0-23） |
  | appCount | number | 去重包名数 |
  | switchOrPickupCount | number | 行数（会话条数，近似切换/拿起次数） |
  | completeness | number | 1 - fallback 占比（0-1，四舍五入 2 位） |
  | quality | MobileAnalyticsQualitySummaryDto | 质量摘要（含系统噪音行参与计算） |
  | quality.usageEventsCoverage | number | 事件覆盖率（1 - fallback 占比） |
  | quality.fallbackShare | number | fallback 占比 |
  | quality.missingMetadataAppCount | number | 缺元数据应用数（qualityFlags 含 missing-metadata 的去重包名数） |
  | quality.systemNoiseShare | number | 系统噪音占比 |
  | quality.shortEventShare | number | 短事件占比（flags 含 short-event-noise） |
  | quality.failedOrPartialSyncBatchCount | number | 失败/部分失败批次数 |
  | quality.lastSyncAt | string\|null | 最近同步时间 |
  | quality.qualityFlags | string[] | 去重质量旗标（默认过滤噪音时额外追加 `hidden-system-noise`） |
  | goalProgress | MobileGoalProgressDto\|null | 第一条启用目标的进度（无目标为 null） |
  | goalProgress.key | string | 目标键 |
  | goalProgress.label | string | 目标名 |
  | goalProgress.limitSeconds | number | 限额秒数 |
  | goalProgress.usedSeconds | number | 已用秒数 |
  | goalProgress.isOverLimit | boolean | 是否超限 |
  | goalProgress.remainingSeconds | number | 剩余秒数 |
  | anomalies | MobileAnomalyDto[] | 异常列表 |
  | anomalies[].code | string | `night-use`（22 点后仍有使用）\| `long-total`（总时长 >6 小时）（MobileUsageAggregationService.cs:735-757） |
  | anomalies[].severity | string | `"Warning"` |
  | anomalies[].title | string | 标题（"夜间使用偏高"/"总使用时长偏高"） |
  | anomalies[].evidence | string | 证据文案 |
  | anomalies[].drilldownTarget | string | 下钻目标（`heatmap:night` / `overview:total`） |
  | suggestions | MobileSuggestionDto[] | 建议列表 |
  | suggestions[].code | string | `top-category-review` |
  | suggestions[].text | string | 建议文案 |
  | suggestions[].drilldownTarget | string | `category:{分类名}` |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:347`、`Services/MobileUsageAggregationService.cs:36`；前端 `src/client-web/src/api/mobile.ts:821`（路径构造 mobile.ts:240-241）
- 备注：totalSeconds 为 0 时 completeness 为 0。

### GET /api/v1/mobile/analytics/heatmap
- 用途：使用热力图桶（本地日 × 本地小时 × 分类的前台秒数），小时/日粒度按桶真实长度封顶。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage）
- Path 参数：无
- Query 参数：同 GET /mobile/analytics/overview 的公共组 `MobileAnalyticsEndpointQuery`（granularity 在此生效：15m/30m/hour/day）+ force
- Body：无
- 响应 data：`MobileHeatmapBucketDto[]`（MobileAnalyticsDtos.cs:127-134；构建 MobileUsageAggregationService.cs:105-176）
  | 字段 | 类型 | 说明 |
  | bucketStartUtc | string | 桶起点（UTC） |
  | bucketEndUtc | string | 桶终点（UTC） |
  | localDate | string | 本地日 `yyyy-MM-dd` |
  | localHour | number | 本地小时（day 粒度为 0） |
  | lifeCategory | string | 生活分类（同 category 枚举） |
  | foregroundSeconds | number | 桶内前台秒数（hour/day 粒度按桶真实长度做比例封顶；15m/30m 不封顶） |
  | qualityFlags | string[] | 质量旗标 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:360`、`Services/MobileUsageAggregationService.cs:105`；前端 `src/client-web/src/api/mobile.ts:827`（路径构造 mobile.ts:242-243）
- 备注：行按物化表 mobile_usage_aggregates 的 hour 粒度读取再重切桶（MobileUsageAggregationService.cs:529-616）。

### GET /api/v1/mobile/analytics/charts
- 用途：一次返回 8 张预设图表（分类占比/Top App/每日趋势/小时分布/分类趋势/切换趋势/周期对比/目标进度）。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage）
- Path 参数：无
- Query 参数：同 GET /mobile/analytics/overview 的公共组 `MobileAnalyticsEndpointQuery` + force
- Body：无
- 响应 data：`MobileAnalyticsChartDto[]`（MobileAnalyticsDtos.cs:146-151；固定输出 MobileUsageAggregationService.cs:260-267）
  | 字段 | 类型 | 说明 |
  | key | string | 图表键：`category-share` \| `top-apps` \| `daily-total` \| `hour-distribution` \| `category-trend` \| `switch-trend` \| `comparison` \| `goal-marker` |
  | title | string | 中文标题（"分类占比"/"Top App"/"每日趋势"/"小时分布"/"分类趋势"/"切换趋势"/"周期对比"/"目标进度"） |
  | chartType | string | 同 key 的图表类型 |
  | unit | string | `seconds` \| `count` \| `ratio`（comparison 为 ratio） |
  | points | MobileAnalyticsChartPointDto[] | 数据点（comparison / goal-marker 固定为空数组） |
  | points[].key | string | 点键 |
  | points[].label | string | 显示标签 |
  | points[].value | number | 数值（秒数或计数） |
  | points[].foregroundSeconds | number\|null | 前台秒数 |
  | points[].lifeCategory | string\|null | 分类 |
  | points[].packageName | string\|null | 包名 |
  | points[].localDate | string\|null | 本地日 |
  | points[].localHour | number\|null | 本地小时 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:373`、`Services/MobileUsageAggregationService.cs:255`；前端 `src/client-web/src/api/mobile.ts:833`（路径构造 mobile.ts:244-245）
- 备注：图表顺序固定为上表 key 顺序。

### GET /api/v1/mobile/analytics/timeline-blocks
- 用途：聚合时间块分页（把连续同类使用会话折叠成"块"），支持页码分页与 cursor 分页双模式。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage，时间线块视图）
- Path 参数：无
- Query 参数：同 GET /mobile/analytics/overview 的公共组 `MobileAnalyticsEndpointQuery` + force（cursor/page/pageSize 在此生效；pageSize 默认 50、上限 200）
- Body：无
- 响应 data：`MobileTimelineBlockPageDto`（MobileAnalyticsDtos.cs:173-180；构建 MobileTimelineBlockService.cs:36-74）
  | 字段 | 类型 | 说明 |
  | items | MobileTimelineBlockDto[] | 当前页块（按 startUtc 降序） |
  | items[].id | string | 块 ID（服务端编码的 Base64Url 载荷，含起止时间/分类/会话 ID 集） |
  | items[].startUtc / endUtc | string | 块起止 |
  | items[].localStart / localEnd | string | 本地起止 |
  | items[].lifeCategory | string | 主分类 |
  | items[].foregroundSeconds | number | 块内前台秒数 |
  | items[].sessionCount | number | 会话数 |
  | items[].appCount | number | 应用数 |
  | items[].topApps | MobileTimelineBlockAppDto[] | Top 应用 |
  | items[].topApps[].packageName | string | 包名 |
  | items[].topApps[].displayName | string | 应用名 |
  | items[].topApps[].foregroundSeconds | number | 前台秒数 |
  | items[].qualityFlags | string[] | 质量旗标 |
  | items[].sourceMix | Record<string,number>\|null | 来源构成（来源→秒数） |
  | items[].includesSystemNoise | boolean | 块是否含系统噪音 |
  | nextCursor | string\|null | 下一页游标（Base64Url(JSON `{startUtc,id}`)，MobileTimelineBlockService.cs:583-589、715） |
  | hasMore | boolean | 是否还有下一页 |
  | page | number | 页码（cursor 模式下仍回显请求值） |
  | pageSize | number | 页大小 |
  | totalCount | number | 块总数 |
  | totalPages | number | 总页数 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:386`、`Services/MobileTimelineBlockService.cs:36`；前端 `src/client-web/src/api/mobile.ts:837`（路径构造 mobile.ts:246-247）
- 备注：cursor 模式——传 cursor 时忽略 page 偏移，按 (startUtc,id) 键续读；不传 cursor 时按 page 跳页。两种模式互斥，nextCursor 始终可用于下一页。

### GET /api/v1/mobile/analytics/timeline-blocks/{blockId}/sessions
- 用途：展开一个时间块内的使用会话明细。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage，块展开）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | blockId | string | 是 | 块 ID（URL 编码，来自 timeline-blocks 的 items[].id） |
- Query 参数：同 GET /mobile/analytics/overview 的公共组 `MobileAnalyticsEndpointQuery`（用于重建块上下文的过滤条件，需与取块时一致）+ force
- Body：无
- 响应 data：`MobileTimelineBlockSessionDto[]`（MobileAnalyticsDtos.cs:182-193）
  | 字段 | 类型 | 说明 |
  | id | string | 会话 ID |
  | deviceId | string | 设备 ID |
  | packageName | string | 包名 |
  | displayName | string | 应用名 |
  | startUtc | string | 开始 |
  | endUtc | string\|null | 结束（未结束为 null） |
  | durationSeconds | number | 时长 |
  | lifeCategory | string | 分类 |
  | source | string | `events` \| `fallback` |
  | confidence | number | 置信度 |
  | qualityFlags | string[] | 质量旗标 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:399`、`Services/MobileTimelineBlockService.cs:76`；前端 `src/client-web/src/api/mobile.ts:843`（路径构造 mobile.ts:248-249）
- 备注：blockId 无法从当前查询重建时回退解码其内嵌载荷取会话 ID 集（MobileTimelineBlockService.cs:92-100）。

### GET /api/v1/mobile/analytics/sessions/{sessionId}/events
- 用途：单个会话的原始使用事件明细。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage，会话事件抽屉）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | sessionId | string | 是 | 会话 ID（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`MobileSessionEventDto[]`（MobileAnalyticsDtos.cs:195-203）
  | 字段 | 类型 | 说明 |
  | id | string | 事件 ID |
  | sessionId | string | 所属会话 ID |
  | deviceId | string | 设备 ID |
  | packageName | string | 包名 |
  | eventType | string | 事件类型（ACTIVITY_RESUMED 等） |
  | eventTimeUtc | string | 事件时间 |
  | className | string\|null | 组件类名 |
  | rawJson | string | 原始事件 JSON |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:406`、`Services/MobileTimelineBlockService.cs`（GetSessionEventsAsync）；前端 `src/client-web/src/api/mobile.ts:852`（路径构造 mobile.ts:250-251）
- 备注：无分页，返回该会话全部事件。

---

## 存活检测

### GET /api/v1/mobile/liveness/overview
- 用途：Web「设备存活」子页首屏：全部设备按机型（手机/平板/未分类）分块的存活摘要。
- 认证：JWT
- Web 前端使用：是（使用分析页 MobileRecordsPage 的设备存活面板）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc | string (ISO-8601) | 否 | 窗口起点；缺省为终点往前 7 天（MobileLivenessService.cs:48-70） |
  | rangeEndUtc | string (ISO-8601) | 否 | 窗口终点；缺省 now（超过 now 一律夹紧到 now） |
- Body：无
- 响应 data：`MobileLivenessOverviewResponse`（MobileForensicDtos.cs:87-93；构建 MobileLivenessService.cs:72-96）
  | 字段 | 类型 | 说明 |
  | rangeStartUtc / rangeEndUtc | string | 实际生效窗口 |
  | expectedHeartbeatIntervalMinutes | number | 应有心跳节奏，固定 15（DeviceLivenessModels.cs:58） |
  | phones | MobileDeviceLivenessDto[] | 手机块 |
  | tablets | MobileDeviceLivenessDto[] | 平板块 |
  | unclassified | MobileDeviceLivenessDto[] | 未分类机型块 |
  | phones[]（及 tablets[]/unclassified[] 元素）.deviceId | string | 设备 ID |
  | （同上）.displayName | string | 别名 |
  | （同上）.deviceKind | string | `phone` \| `tablet` \| `unknown`（DeviceKinds，MobileLivenessService.cs:515-524） |
  | （同上）.deviceKindLabel | string | `手机` \| `平板` \| `未分类机型` |
  | （同上）.hasData | boolean | 区间内是否有任何存活证据 |
  | （同上）.conclusion | string | 结论文案：无数据为 `"无数据/未上报：该区间内没有任何存活证据。"`；无 ≥30 分钟静默为 `"存活连续：…"`；最长静默 ≥1 小时为 `"存活有缺口：最长静默 N 分钟，区间内共 M 段 ≥30 分钟静默。"`；否则 `"存活有短暂中断：最长静默 N 分钟（未达 1 小时）。"`（DeviceLivenessCalculator.cs:176-200） |
  | （同上）.coverageByHour | number\|null | 按小时覆盖率（0-1，无数据为 null） |
  | （同上）.coverageByExpectedHeartbeat | number\|null | 按应有心跳覆盖率（0-1，上限 100%） |
  | （同上）.observedHours / totalHours | number | 有证据小时数 / 区间总小时数 |
  | （同上）.observedHeartbeats / expectedHeartbeats | number | 心搏条数 / 应有心跳条数 |
  | （同上）.expectedHeartbeatIntervalMinutes | number | 15 |
  | （同上）.longestSilenceMinutes | number | 最长静默分钟数 |
  | （同上）.longestSilenceStartUtc / longestSilenceEndUtc | string\|null | 最长静默起止 |
  | （同上）.longestSilenceSeverity | string | `none` \| `warning`（≥30 分钟）\| `critical`（≥1 小时）（SilenceSeverities，DeviceLivenessModels.cs:38-43） |
  | （同上）.hasSilenceOverOneHour | boolean | 是否存在 ≥1 小时静默 |
  | （同上）.silences | MobileSilenceWindowDto[] | 静默段列表（仅 ≥30 分钟段） |
  | （同上）.silences[].startUtc / endUtc | string | 静默起止 |
  | （同上）.silences[].minutes | number | 时长分钟 |
  | （同上）.silences[].severity | string | 同上枚举 |
  | （同上）.silences[].severityLabel | string | `严重（≥1 小时）` \| `警告（≥30 分钟）` \| `未达标记线`（MobileLivenessService.cs:460-464） |
  | （同上）.causes | MobileLivenessCauseDto[] | 死因分布 |
  | （同上）.causes[].cause | string | `unknown` \| `no-record` \| `force-stop` \| `reboot` \| `sentinel-cleared-permission`（MobileLivenessCauseClassifier.cs:19-23） |
  | （同上）.causes[].label | string | 中文标签（"系统未给出原因"/"系统未提供退出记录"/"疑似强停"/"设备重启"/"哨兵被清空（权限变更）"） |
  | （同上）.causes[].count | number | 次数 |
  | （同上）.causes[].inference | string\|null | 推断依据（未知原因必带） |
  | （同上）.lastEventAtUtc | string\|null | 最近存活证据时间 |
  | （同上）.coverageByHourDefinition | string | 按小时覆盖率定义原文（页面可读，DeviceLivenessModels.cs:61-62） |
  | （同上）.coverageByExpectedHeartbeatDefinition | string | 按应有心跳覆盖率定义原文（DeviceLivenessModels.cs:65-66） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:121`、`Services/MobileLivenessService.cs:72`；前端 `src/client-web/src/api/mobile.ts:856`（路径构造 mobile.ts:259-260）
- 备注：从未上报的设备也出现并标"无数据/未上报"（以设备表为准）；机型判定优先 metadataJson.deviceKind，回退 smallestScreenWidthDp ≥600。

### GET /api/v1/mobile/devices/{deviceId}/liveness
- 用途：单设备存活摘要块（与 overview 同一口径的独立入口）。
- 认证：JWT
- Web 前端使用：否（无前端调用；Web 存活页统一走 /mobile/liveness/overview）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 同 /mobile/liveness/overview |
- Body：无
- 响应 data：`MobileDeviceLivenessDto`（字段同 overview 的 phones[] 元素）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:129`、`Services/MobileLivenessService.cs:111`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：无数据设备返回 200 + hasData=false（空态，不返回 0%）；设备根本不存在才返回 HTTP 404（"设备不存在。"，MobileModule.cs:137-142）。

### GET /api/v1/mobile/devices/{deviceId}/liveness/events
- 用途：单设备存活事件（取证事件）分页明细，可查看原始 JSON。
- 认证：JWT
- Web 前端使用：是（使用分析页的设备存活面板组件 DeviceLivenessPanel）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 窗口（默认最近 7 天） |
  | page | number | 否 | 页码，从 1 起 |
  | pageSize | number | 否 | 默认 50，夹紧到 [1,200]（MobileLivenessService.cs:29-31、138） |
- Body：无
- 响应 data：`MobileLivenessEventPageDto`（MobileForensicDtos.cs:111-116）
  | 字段 | 类型 | 说明 |
  | items | MobileLivenessEventDto[] | 当前页事件 |
  | items[].id | string (UUID) | 事件主键 |
  | items[].eventType | string | `process-exit` \| `force-stop` \| `heartbeat` |
  | items[].eventTypeLabel | string | 中文类型标签 |
  | items[].occurredAtUtc | string | 发生时间 |
  | items[].reason | string\|null | 退出原因原始值 |
  | items[].reasonLabel | string\|null | 原因中文标签 |
  | items[].inference | string\|null | 推断依据 |
  | items[].importance | number\|null | 重要性 |
  | items[].pssKb | number\|null | PSS 内存（KB） |
  | items[].rssKb | number\|null | RSS 内存（KB） |
  | items[].description | string\|null | 描述 |
  | items[].payloadJson | string | 原始负载 JSON |
  | page | number | 当前页 |
  | pageSize | number | 页大小 |
  | totalCount | number | 总条数 |
  | totalPages | number | 总页数 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:144`、`Services/MobileLivenessService.cs:126`；前端 `src/client-web/src/api/mobile.ts:860`（路径构造 mobile.ts:261-262）
- 备注：无。

### GET /api/v1/mobile/devices/{deviceId}/dropped-reasons
- 用途：单设备"定位点被丢弃原因"的按天统计（REQ-9）。
- 认证：JWT
- Web 前端使用：否（无前端调用；数据由 Android 端上报、服务端聚合）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备唯一标识（URL 编码） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | rangeStartUtc / rangeEndUtc | string (ISO-8601) | 否 | 窗口（默认最近 7 天） |
- Body：无
- 响应 data：`MobileDroppedReasonResponse`（MobileForensicDtos.cs:125-130）
  | 字段 | 类型 | 说明 |
  | deviceId | string | 设备 ID |
  | rangeStartUtc / rangeEndUtc | string | 实际窗口 |
  | items | MobileDroppedReasonDailyDto[] | 按天按原因的统计 |
  | items[].localDate | string | 本地日 `yyyy-MM-dd` |
  | items[].reason | string | 丢弃原因标识（客户端上报值） |
  | items[].count | number | 条数 |
  | totalCount | number | 行数合计 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:155`、`Services/MobileLivenessService.cs:161`；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注：无。

---

## 目录与规则

### GET /api/v1/mobile/apps/catalog-overrides
- 用途：列出 App 目录人工覆盖（显示名/分类/系统噪音/短事件隐藏）。
- 认证：JWT
- Web 前端使用：否（mobile.ts 已封装 getMobileAppCatalogOverrides 但当前无页面调用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`MobileAppCatalogOverrideDto[]`（MobileAnalyticsDtos.cs:205-212）
  | 字段 | 类型 | 说明 |
  | packageName | string | 包名 |
  | displayNameOverride | string\|null | 显示名覆盖 |
  | lifeCategory | string | 生活分类（同 category 枚举） |
  | isSystemNoise | boolean | 是否系统噪音 |
  | hideShortEvents | boolean | 是否隐藏短事件 |
  | createdAt / updatedAt | string\|null | 创建/更新时间（可能缺省） |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:439`、`Services/MobileAppCatalogOverrideService.cs`；前端 `src/client-web/src/api/mobile.ts:869`（路径构造 mobile.ts:252）
- 备注：无。

### PUT /api/v1/mobile/apps/catalog-overrides
- 用途：按 Body 中 packageName 新建或更新覆盖（保存入口）。
- 认证：JWT
- Web 前端使用：否（封装 saveMobileAppCatalogOverride 走本端点，当前无页面调用）
- Path 参数：无
- Query 参数：无
- Body：`MobileAppCatalogOverrideUpsertRequest`（MobileAnalyticsDtos.cs:214-219）
  | 字段 | 类型 | 必填 | 说明 |
  | packageName | string | 是 | 包名 |
  | displayNameOverride | string\|null | 否 | 显示名覆盖 |
  | lifeCategory | string | 是 | 生活分类 |
  | isSystemNoise | boolean | 是 | 是否系统噪音 |
  | hideShortEvents | boolean | 是 | 是否隐藏短事件 |
- 响应 data：`MobileAppCatalogOverrideDto`（字段同 GET 元素）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:444`、`Services/MobileAppCatalogOverrideService.cs`；前端 `src/client-web/src/api/mobile.ts:873`
- 备注：**清单中的 `POST /mobile/apps/catalog-overrides` 后端未注册，调用将 404**——本域统一用 PUT 表达"新建或更新"（MobileModule.cs:444-465），前端亦只用 PUT（mobile.ts:873-880）。写后清空 `/api/v1/mobile/` 前缀聚合缓存。

### PUT /api/v1/mobile/apps/catalog-overrides/{packageName}
- 用途：按路径 packageName 新建或更新覆盖（Body 的 packageName 以路径为准）。
- 认证：JWT
- Web 前端使用：否（同上）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | packageName | string | 是 | 包名（URL 编码；覆盖 Body 中同名取值，MobileModule.cs:462） |
- Query 参数：无
- Body：`MobileAppCatalogOverrideUpsertRequest`（同 PUT 集合版）
- 响应 data：`MobileAppCatalogOverrideDto`
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:455`；前端 `src/client-web/src/api/mobile.ts:873`（saveMobileAppCatalogOverride 实际调用此路径，mobile.ts:253-254）
- 备注：同集合版 PUT。

### DELETE /api/v1/mobile/apps/catalog-overrides/{packageName}
- 用途：删除一条覆盖。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | packageName | string | 是 | 包名（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`string`——成功回显 packageName，未命中为空串（MobileModule.cs:475）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:467`；前端 `src/client-web/src/api/mobile.ts:882`（路径构造 mobile.ts:253-254）
- 备注：写后清空聚合缓存。

### GET /api/v1/mobile/apps/category-rules
- 用途：列出 App 分类规则（按规则类型匹配包名，决定生活分类）。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`MobileAppCategoryRuleDto[]`（MobileAnalyticsDtos.cs:221-231）
  | 字段 | 类型 | 说明 |
  | id | string | 规则 ID |
  | ruleType | string | 规则类型（精确/前缀等匹配方式，由服务端解释） |
  | pattern | string | 匹配模式（包名模式） |
  | lifeCategory | string | 生活分类 |
  | priority | number | 优先级 |
  | isEnabled | boolean | 是否启用 |
  | displayNameOverride | string\|null | 显示名覆盖 |
  | isSystemNoise | boolean\|null | 是否系统噪音 |
  | createdAt / updatedAt | string\|null | 创建/更新时间 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:478`、`Services/MobileAppCatalogOverrideService.cs`；前端 `src/client-web/src/api/mobile.ts:886`（路径构造 mobile.ts:255）
- 备注：无。

### POST /api/v1/mobile/apps/category-rules
- 用途：新建分类规则。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：无
- Query 参数：无
- Body：`MobileAppCategoryRuleUpsertRequest`（MobileAnalyticsDtos.cs:233-240）
  | 字段 | 类型 | 必填 | 说明 |
  | ruleType | string | 是 | 规则类型 |
  | pattern | string | 是 | 匹配模式 |
  | lifeCategory | string | 是 | 生活分类 |
  | priority | number | 是 | 优先级 |
  | isEnabled | boolean | 是 | 是否启用 |
  | displayNameOverride | string\|null | 否 | 显示名覆盖 |
  | isSystemNoise | boolean\|null | 否 | 是否系统噪音 |
- 响应 data：`MobileAppCategoryRuleDto`
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:483`、`Services/MobileAppCatalogOverrideService.cs`；前端 `src/client-web/src/api/mobile.ts:890`（路径构造 mobile.ts:255）
- 备注：写后清空聚合缓存。

### PUT /api/v1/mobile/apps/category-rules/{ruleId}
- 用途：更新分类规则。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | ruleId | string | 是 | 规则 ID（URL 编码） |
- Query 参数：无
- Body：`MobileAppCategoryRuleUpsertRequest`（同 POST）
- 响应 data：`MobileAppCategoryRuleDto`
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:494`；前端 `src/client-web/src/api/mobile.ts:896`（路径构造 mobile.ts:256）
- 备注：无。

### DELETE /api/v1/mobile/apps/category-rules/{ruleId}
- 用途：删除分类规则。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | ruleId | string | 是 | 规则 ID（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`string`——成功回显 ruleId，未命中为空串（MobileModule.cs:514）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:506`；前端 `src/client-web/src/api/mobile.ts:903`（路径构造 mobile.ts:256）
- 备注：写后清空聚合缓存。

### GET /api/v1/mobile/analytics/goals
- 用途：列出使用目标（总时长/分类/包名的每日限额）。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`MobileUsageGoalDto[]`（MobileAnalyticsDtos.cs:242-251）
  | 字段 | 类型 | 说明 |
  | id | string | 目标 ID |
  | scope | string | 作用域：`total-daily` \| `category-daily` \| `app-daily` |
  | packageName | string\|null | 包名（scope=app-daily 时） |
  | lifeCategory | string\|null | 分类（scope=category-daily 时） |
  | label | string | 显示名 |
  | limitSeconds | number | 每日限额秒数 |
  | isEnabled | boolean | 是否启用 |
  | createdAt / updatedAt | string | 创建/更新时间 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:412`、`Services/MobileUsageGoalService.cs`；前端 `src/client-web/src/api/mobile.ts:907`（路径构造 mobile.ts:257）
- 备注：scope 取值为前端类型推断（mobile.ts:730），服务端按字符串保存与解释。

### POST /api/v1/mobile/analytics/goals
- 用途：新建或保存使用目标。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：无
- Query 参数：无
- Body：`MobileUsageGoalUpsertRequest`（MobileAnalyticsDtos.cs:253-259）
  | 字段 | 类型 | 必填 | 说明 |
  | scope | string | 是 | `total-daily` \| `category-daily` \| `app-daily` |
  | packageName | string\|null | 否 | 包名（app-daily 时必带） |
  | lifeCategory | string\|null | 否 | 分类（category-daily 时必带） |
  | label | string | 是 | 显示名 |
  | limitSeconds | number | 是 | 每日限额秒数 |
  | isEnabled | boolean | 是 | 是否启用 |
- 响应 data：`MobileUsageGoalDto`
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:417`、`Services/MobileUsageGoalService.cs`；前端 `src/client-web/src/api/mobile.ts:911`（路径构造 mobile.ts:257）
- 备注：写后清空 `/api/v1/mobile/` 前缀聚合缓存（MobileModule.cs:424）。

### DELETE /api/v1/mobile/analytics/goals/{goalId}
- 用途：删除使用目标。
- 认证：JWT
- Web 前端使用：否（封装存在，无页面调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | goalId | string | 是 | 目标 ID（URL 编码） |
- Query 参数：无
- Body：无
- 响应 data：`string`——成功回显 goalId，未命中为空串（MobileModule.cs:436）
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:428`；前端 `src/client-web/src/api/mobile.ts:915`（路径构造 mobile.ts:258）
- 备注：写后清空聚合缓存。

### GET /api/v1/mobile/apps/missing-metadata
- 用途：列出"使用过但缺应用元数据"的待补包清单（供回填任务定向补数）。
- 认证：JWT
- Web 前端使用：否（无前端调用；质量面板文案提示调用该端点补数）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 否 | 过滤单设备 |
  | rangeStartUtc | string (ISO-8601) | 否 | 窗口起点 |
  | rangeEndUtc | string (ISO-8601) | 否 | 窗口终点 |
  | limit | number | 否 | 返回包数上限，默认 200（MobileModule.cs:328） |
- Body：无
- 响应 data：`MobileMissingAppMetadataResponse`（MobileDtos.cs:492-497；构建 MobileQualityService.cs:660-666）
  | 字段 | 类型 | 说明 |
  | deviceId | string\|null | 规范化后的设备 ID |
  | rangeStartUtc / rangeEndUtc | string | 实际窗口 |
  | missingPackageCount | number | 缺元数据包总数 |
  | packages | MissingAppMetadataPackageDto[] | 待补包列表（按 foregroundSeconds 降序、包名升序，截取 limit） |
  | packages[].packageName | string | 包名 |
  | packages[].eventCount | number | 事件条数 |
  | packages[].lastUsedAtUtc | string\|null | 最近使用时间 |
  | packages[].foregroundMs | number | 前台毫秒数 |
- 来源：后端 `src/modules/Pim.Module.Mobile/MobileModule.cs:316`、`Services/MobileQualityService.cs`（GetMissingAppMetadataAsync）；前端 `src/client-web/src/api/mobile.ts` 无封装
- 备注："缺元数据"口径 = 窗口内出现过的包 − 全设备目录已有的包（同一用户口径相减，MobileQualityService.cs:139-150）。
