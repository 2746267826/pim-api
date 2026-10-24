# 日历与任务域接口规格（/api/v1/calendar）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域路由组整体要求 JWT。

## 源码

- 后端路由与 DTO：`src/modules/Pim.Module.Calendar/CalendarModule.cs`（路由组 `CalendarEndpointPaths.Root = "/api/v1/calendar"`，`RequireAuthorization()`，CalendarModule.cs:75-76）；DTO 主要在 `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs` 与 `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs`；规划模型共享 DTO 在 `src/Pim.Core/Planning/PlanningDtos.cs`；确认单/审计 DTO 在 `src/Pim.Core/Operations/ConfirmationDtos.cs`、`src/Pim.Core/Audit/AuditVersionDtos.cs`；分页封装在 `src/Pim.Core/Common/PagedResult.cs:3-9`。
- 前端调用与 TS 类型：`src/client-web/src/api/calendar.ts`；类型集中在 `src/client-web/src/types/index.ts`。
- JSON 序列化为 camelCase；时间字段均为 ISO 8601 字符串（DateTimeOffset）。
- 本域无匿名 / Admin / OpsKey 端点，全部 JWT。

## 阅读约定

- 下文 `EventResponse`、`TaskResponse` 等大型结构在首次出现的端点处完整展开，其余端点以"字段同 …"引用，避免重复表格。
- "双态行为"指同一 URL 依据 Query 参数组合返回不同 data 结构（旧版全量数组 vs `PagedResult`），是 Web 重构必须掌握的兼容语义。

## 日历本

### GET /api/v1/calendar/calendars
- 用途：列出当前用户的日历/任务本（含 Outlook 绑定信息）。
- 认证：JWT
- Web 前端使用：是（日历页 CalendarPage、数据管理页 CalendarDataManager、侧边栏 Sidebar、日程编辑对话框 EventEditorDialog、任务编辑对话框 TaskEditorDialog）
- Path 参数 / Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | kind | string | 否 | 按日历类型过滤：`calendar`（日程本）/ `task`（任务本）；缺省返回全部 |
- Body：无
- 响应 data：`CalendarResponse[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 日历 ID |
  | name | string | 名称 |
  | color | string | 颜色（创建时默认 `#3B82F6`） |
  | kind | string | 类型：`calendar` / `task` |
  | isDefault | boolean | 是否该 kind 下首个日历（默认日历） |
  | eventCount | integer | 关联条目数 |
  | source | string | 来源，默认 `manual`；Outlook 同步日历为 outlook 来源 |
  | outlookCalendarBindingId | string(uuid)\|null | 关联 Outlook 日历绑定 ID（无绑定为 null） |
  | canEdit | boolean | 有 Outlook 绑定时取绑定.CanEdit，否则 true |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:339-342`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:11-20`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:44-64`；前端 `src/client-web/src/api/calendar.ts:276-280`；类型 `src/client-web/src/types/index.ts:15-25`

### POST /api/v1/calendar/calendars
- 用途：创建日历（kind=`task` 即任务本）。
- 认证：JWT
- Web 前端使用：是（侧边栏 Sidebar）
- Path 参数：无
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 是 | ≤100 字符（`[MaxLength(100)]`） |
  | color | string | 否 | ≤7 字符；缺省 `#3B82F6` |
  | kind | string | 否 | 缺省 `calendar` |
- 响应 data：`CalendarResponse`（字段同 GET /calendar/calendars，新建时 eventCount=0、source=manual）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:344-348`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:5-9`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:66-80`；前端 `src/client-web/src/api/calendar.ts:282-285`

### PUT /api/v1/calendar/calendars/{id}
- 用途：更新日历名称/颜色（后端复用 CreateCalendarRequest；kind 不参与更新）。
- 认证：JWT
- Web 前端使用：是（侧边栏 Sidebar）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 日历 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 是 | 新名称（记录 `[Required]`） |
  | color | string | 否 | 仅在非 null 时更新 |
  | kind | string | 否 | 后端忽略 |
- 响应 data：`CalendarResponse`
- 备注：前端发送 `{ name?, color? }`（api/calendar.ts:287-290），后端 record 要求 Name 必填；重命名场景前端始终带 name。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:350-353`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:82-92`；前端 `src/client-web/src/api/calendar.ts:287-290`

### POST /api/v1/calendar/calendars/{id}/delete-preview
- 用途：删除日历前预览影响范围（联同其下任务/日程数量）。
- 认证：JWT
- Web 前端使用：是（侧边栏 Sidebar 删除确认对话框）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 日历 ID |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`CalendarDeletePreviewResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | targetType | string | `calendar-book` / `task-book`（按 kind） |
  | targetId | string(uuid) | 目标日历 ID |
  | title | string | 日历名 |
  | operationKind | string | 操作类型标识 |
  | affectedCount | integer | 连带删除的活跃子条目数 |
  | samples | CalendarOperationSample[] | 子条目样例（最多 5 条，字段见 POST /calendar/tasks/batch-delete 响应 data 内 samples） |
  | summary | string | 中文摘要，如"删除 X 及 N 个活跃日程。" |
  | requiresStrictConfirmation | boolean | 恒为 true |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:355-357`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:232-241`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarDeleteService.cs:27-43`；前端 `src/client-web/src/api/calendar.ts:440-446`；类型 `src/client-web/src/types/index.ts:724-733`

### DELETE /api/v1/calendar/calendars/{id}
- 用途：软删除日历及其下全部任务/日程（进入回收站）。
- 认证：JWT
- Web 前端使用：是（侧边栏 Sidebar）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 日历 ID |
- Query 参数 / Body：无
- 响应 data：`CalendarOperationResult`（字段见 POST /calendar/tasks/batch-delete）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:359-361`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarDeleteService.cs:45-101`；前端 `src/client-web/src/api/calendar.ts:292-294`

### POST /api/v1/calendar/calendars/{id}/restore
- 用途：从回收站恢复日历（后端固定按 `type=calendar` 走恢复流程）。
- 认证：JWT
- Web 前端使用：否（前端未封装该调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 日历 ID |
- Query 参数：无
- Body：无（后端 `CalendarRestoreRequest?` 缺省时取 `new CalendarRestoreRequest()`，即 restoreAsCopy=false）
- 响应 data：`CalendarOperationResult`（字段见 POST /calendar/tasks/batch-delete）
- 备注：存在同 UID/同 SourceUid/同标题同时间冲突且未选恢复为副本时抛 02020"恢复存在冲突"。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:363-365`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:271`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:138-160`

## 日程事件

### GET /api/v1/calendar/events
- 用途：查询日程事件（支持时间窗口、搜索、日历过滤与分页；同一 URL 存在两种响应形态）。
- 认证：JWT
- Web 前端使用：是（日历页 CalendarPage 用 `start&end&page=1&pageSize=100`；数据管理页 CalendarDataManager 用分页/搜索参数）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | start | string(DateTimeOffset) | 否 | 窗口起点（含周期展开） |
  | end | string(DateTimeOffset) | 否 | 窗口终点 |
  | search | string | 否 | 按标题包含匹配 |
  | calendarId | string(uuid) | 否 | 按日历过滤 |
  | page | integer | 否 | 默认 1 |
  | pageSize | integer | 否 | 默认 50，clamp 1-100 |
- Body：无
- 响应 data（双态行为，判据：search、calendarId、page、pageSize 全部缺省）：
  - 四个参数全缺省（只带 start/end 或什么都不带）→ 旧版全量：data = `EventResponse[]`（按 OccurrenceStart 升序；`CalendarModule.cs:380-386`，为兼容旧 APK 保留）。
  - 任一参数出现 → data = `PagedResult<EventResponse>`（`CalendarModule.cs:388-389`）。日历页 `getEvents` 固定发送 `page=1&pageSize=100` 以命中分页分支并减少截断（api/calendar.ts:296-317，截断时 console.warn）；数据管理页 `getEventsPaged` 发送搜索/过滤分页参数（api/calendar.ts:813-827）。
  - EventResponse 字段展开（后端 DTO `CalendarDtos.cs:113-149`；前端类型 `types/index.ts:64-107`）：
    | 字段 | 类型 | 说明 |
    | --- | --- | --- |
    | id | string(uuid) | 事件 ID（周期实例为展开后的 OccurrenceId） |
    | calendarId | string(uuid) | 所属日历 |
    | uid | string | ICS UID |
    | title | string | 标题 |
    | description | string\|null | 描述（HTML 描述已净化） |
    | location | string\|null | 地点 |
    | dtStart | string(DateTimeOffset) | 开始时间 |
    | dtEnd | string(DateTimeOffset) | 结束时间 |
    | rrule | string\|null | RFC 5545 RRULE |
    | status | string | 状态（如 CONFIRMED/CANCELLED） |
    | source | string | 来源：`manual` / `outlook` / `outlook-graph` / `outlook-ics` |
    | originalEventId | string(uuid)\|null | 周期实例指向的母本 ID |
    | isAllDay | boolean | 默认 false |
    | timeZoneId | string\|null | 时区 |
    | sourceTimeZoneId | string\|null | 源时区 |
    | sourceUid | string\|null | Outlook/ICS 源 UID |
    | recurrenceId | string\|null | 周期实例 RecurrenceId（ISO8601） |
    | exDatesJson | string | 排除日期 JSON，默认 `[]` |
    | recurrenceMetadataJson | string | 周期元数据 JSON，默认 `{}` |
    | outlookCalendarBindingId | string(uuid)\|null | Outlook 绑定 ID |
    | outlookEventId | string\|null | Graph 事件 ID |
    | outlookEtag | string\|null | Graph ETag |
    | outlookEventType | string\|null | Graph 类型：`single` / `occurrence` / `exception` / `seriesMaster` |
    | outlookAdditionalInfo | OutlookAdditionalInfo\|null | Outlook 附加信息 |
    | outlookAdditionalInfo.groups[] | OutlookAdditionalInfoGroup[] | 分组：`{ key, label, items: [{ key, label, value }] }` |
    | outlookAdditionalInfo.hiddenFieldCount | integer | 被隐藏字段数 |
    | descriptionFormat | string\|null | `text` / `html` |
    | showAs | string\|null | busy/free 等 |
    | importance | string\|null | low/normal/high |
    | sensitivity | string\|null | normal/personal/private/confidential |
    | categories | string[]\|null | 分类 |
    | isReminderOn | boolean\|null | 是否开启提醒 |
    | reminderMinutesBeforeStart | integer\|null | 提前提醒分钟数 |
    | organizer | EventPerson\|null | `{ name, email }` |
    | attendees | EventAttendee[]\|null | `{ name, email, type }`，type 默认 `required` |
    | isOnlineMeeting | boolean\|null | 是否联机会议 |
    | onlineMeetingProvider | string\|null | 如 Teams |
    | onlineMeetingUrl | string\|null | 会议链接 |
    | externalLink | string\|null | 外部链接 |
    | attachmentReferences | EventAttachmentReference[]\|null | `{ kind, id, name, contentType, size, canDownload }`；kind 仅 `pimFile` 可由本端写入 |
    | isSeriesMaster | boolean | 是否周期母本，默认 false |
    | isException | boolean | 是否周期例外，默认 false |
    | seriesMasterId | string(uuid)\|null | 母本 ID |
    | isCancelled | boolean | 是否已取消，默认 false |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:370-390`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:113-149`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:104-168`；前端 `src/client-web/src/api/calendar.ts:296-317,804-827`

### POST /api/v1/calendar/events
- 用途：创建日程事件。
- 认证：JWT
- Web 前端使用：是（EventEditorDialog createEvent）
- Path 参数 / Query 参数：无
- Body：`CreateEventRequest`（前端发送 `Partial<UnifiedEventDraft>`，字段同名）
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | calendarId | string(uuid) | 是 | 目标日历；`Guid.Empty` 时自动落到/创建默认日历（CalendarService.cs:174-178）；Outlook 绑定日历禁止直接创建（02009） |
  | title | string | 是 | ≤255 字符 |
  | description | string | 否 | 纯文本或 html（按 descriptionFormat；html 会被净化） |
  | location | string | 否 | ≤500 字符 |
  | dtStart | string(DateTimeOffset) | 是 | 开始时间（转 UTC；结束必须晚于开始，02010） |
  | dtEnd | string(DateTimeOffset) | 是 | 结束时间 |
  | rrule | string | 否 | RFC 5545 RRULE |
  | uid | string | 否 | 缺省生成 `<uuid>@pim` |
  | isAllDay | boolean | 否 | 默认 false |
  | timeZoneId | string | 否 | IANA 时区 |
  | descriptionFormat | string | 否 | `text` / `html` |
  | showAs / importance / sensitivity | string | 否 | Outlook 统一字段 |
  | categories | string[] | 否 | 分类 |
  | isReminderOn | boolean | 否 | |
  | reminderMinutesBeforeStart | integer | 否 | |
  | organizer | EventPerson | 否 | `{ name, email }` |
  | attendees | EventAttendee[] | 否 | `{ name, email, type }` |
  | isOnlineMeeting | boolean | 否 | |
  | onlineMeetingProvider / onlineMeetingUrl / externalLink | string | 否 | |
  | attachmentReferences | EventAttachmentReference[] | 否 | 仅 `kind=pimFile` 允许客户端提交，其余报 02009 |
  | isSeriesMaster / isException | boolean | 否 | |
  | seriesMasterId | string(uuid) | 否 | |
  | recurrenceId | string | 否 | |
- 响应 data：`EventResponse`（字段同 GET /calendar/events）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:392-399`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:45-74`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:170-255`；前端 `src/client-web/src/api/calendar.ts:319-322`；类型 `src/client-web/src/types/index.ts:597-630`

### PUT /api/v1/calendar/events/{id}
- 用途：更新日程事件（支持周期范围 scope：本次/系列）。
- 认证：JWT
- Web 前端使用：是（EventEditorDialog updateEvent）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 事件 ID（周期实例场景前端解析为母本 ID） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | scope | string | 否 | `this`（仅本次实例）/ `series`（整个系列）；不传为普通更新（后端枚举 `UpdateEventScope` 定义 This/Series，实际按字符串比较） |
  | recurrenceId | string | 否 | 周期实例 RecurrenceId；非法格式报 02009；与 Body.RecurrenceId 冲突报 02009 |
  | originalEventId | string(uuid) | 否 | 合成实例场景的原始实例 ID |
- Body：`UpdateEventRequest`（字段同 CreateEventRequest，但 `isAllDay` 等布尔为可空；`calendarId`/`title`/`dtStart`/`dtEnd` 标记 `[Required]`）
- 响应 data：`EventResponse`
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:401-430`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:76-111`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:419-700`；前端 `src/client-web/src/api/calendar.ts:324-333`（scope 取值 `this`/`series`，见 EventEditorDialog.tsx:159-160）

### DELETE /api/v1/calendar/events/{id}
- 用途：删除日程事件（软删除；支持周期范围）。
- 认证：JWT
- Web 前端使用：是（EventEditorDialog deleteEvent）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 事件 ID |
- Query 参数：同 PUT（scope、recurrenceId、originalEventId）
- Body：无
- 响应 data：string（字面量 `"已删除"`）
- 备注：Outlook 绑定日程禁止此通道删除（02009，须走写回流程）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:432-441`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:719-900`、`Services/CalendarDeleteService.cs:103-130`；前端 `src/client-web/src/api/calendar.ts:335-343`

### POST /api/v1/calendar/events/{id}/restore
- 用途：从回收站恢复事件。
- 认证：JWT
- Web 前端使用：否（前端经通用回收站恢复端点 restoreRecycleItem 调用 `POST /calendar/recycle-bin/event/{id}/restore`，未用此专用端点）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 事件 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | restoreAsCopy | boolean | 否 | 默认 false |
- 响应 data：`CalendarOperationResult`（字段见 POST /calendar/tasks/batch-delete）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:443-448`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:138-160`

### POST /api/v1/calendar/events/batch-delete
- 用途：批量软删除事件。
- 认证：JWT
- Web 前端使用：是（数据管理页 CalendarDataManager）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | ids | string(uuid)[] | 是 | 事件 ID 列表 |
- 响应 data：`CalendarOperationResult`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | operation | string | 操作标识，如 `calendar.events.batch_delete` |
  | operationId | string(uuid) | 本次操作 ID（审计关联） |
  | affectedCount | integer | 实际删除数 |
  | affectedIds | string(uuid)[] | 被删条目 ID（含事件） |
  | samples | CalendarOperationSample[] | 样例（≤5 条）：`{ id, type, title, start, end, bookName }` |
  | message | string | 中文结果消息 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:450-454`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:293,223-250`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarDeleteService.cs:132-175`；前端 `src/client-web/src/api/calendar.ts:345-348`；类型 `src/client-web/src/types/index.ts:735-742`

### GET /api/v1/calendar/events/{eventId}/attachments/{attachmentId}/download
- 用途：下载 Outlook 附件二进制（服务端经 Graph 中转）。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts 未封装；移动端使用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | eventId | string(uuid) | 是 | 事件 ID |
  | attachmentId | string | 是 | Outlook 附件 ID（非 GUID） |
- Query 参数 / Body：无
- 响应 data：二进制文件流（`Results.File`，Content-Type 取 Graph 返回，文件名经 `GraphBinaryContent.SanitizeFileName` 处理）；非 `ApiResponse` 封装
- 备注：404 = 未登录上下文或附件不存在；409 = Outlook 需重新授权（body 为 `ApiResponse<string>`，code=02009，message=`Outlook 连接需要重新授权。`）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:456-477`；服务 `src/modules/Pim.Module.Calendar/Services/EventAttachmentService.cs:11,86-161`

## 任务

### GET /api/v1/calendar/tasks
- 用途：查询任务（支持收件箱、搜索、日历、状态、优先级、计划/截止时间过滤与分页；双态行为）。
- 认证：JWT
- Web 前端使用：是（任务清单页 TaskListPage、工作台 WorkbenchPage 用 getTasksPaged；收件箱面板 InboxPanel、图表 useExhibitionData 用旧版全量）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | inbox | boolean | 否 | 按收件箱标记过滤（`inbox=true` / `false`） |
  | search | string | 否 | 标题包含匹配 |
  | calendarId | string(uuid) | 否 | 按日历过滤 |
  | status | string | 否 | 精确匹配任务状态 |
  | priority | integer | 否 | 精确匹配优先级 |
  | plannedFrom / plannedTo | string(DateTimeOffset) | 否 | 计划开始（DtStart）范围 |
  | dueFrom / dueTo | string(DateTimeOffset) | 否 | 截止（Due）范围 |
  | page | integer | 否 | 默认 1 |
  | pageSize | integer | 否 | 默认 50，clamp 1-100 |
- Body：无
- 响应 data（双态行为，判据：除 inbox 外，search/calendarId/status/priority/plannedFrom/plannedTo/dueFrom/dueTo/page/pageSize 全部缺省，`CalendarModule.cs:495-497`）：
  - 全缺省（可仅带 inbox）→ 旧版全量：data = `TaskResponse[]`（按 SortOrder 升序，`CalendarService.cs:932-942`）。InboxPanel 发送 `/calendar/tasks`，useExhibitionData 发送 `/calendar/tasks?inbox=false`，均命中此分支。
  - 任一筛选/分页参数出现 → data = `PagedResult<TaskResponse>`（`CalendarService.cs:944-1000`；排序：未完成在前 → Due 非空在前 → Due 升序 → SortOrder）。
  - TaskResponse 字段展开（后端 DTO `CalendarDtos.cs:181-191`，映射 `CalendarService.cs:1391-1397`；前端类型 `types/index.ts:109-127`）：
    | 字段 | 类型 | 说明 |
    | --- | --- | --- |
    | id | string(uuid) | 任务 ID |
    | calendarId | string(uuid)\|null | 所属日历 |
    | uid | string | `<uuid>@pim` |
    | title | string | 标题 |
    | description | string\|null | 描述 |
    | priority | integer | 优先级（数值越大越优先） |
    | estimatedDuration | string\|null | 预计时长，`TimeSpan` "c" 格式（如 `01:30:00`） |
    | minimumSegment | string\|null | 最小可排段，同上格式 |
    | dtStart | string(DateTimeOffset)\|null | 计划开始 |
    | due | string(DateTimeOffset)\|null | 截止时间 |
    | status | string | 自由字符串；Web 取值 `COMPLETED` / `NEEDS-ACTION`；后端特殊处理 `COMPLETED`（写 CompletedAt）、`CANCELLED`（AI 排程排除） |
    | isInbox | boolean | 是否收件箱任务 |
    | sortOrder | integer | 排序号 |
    | subTasks | TaskResponse[] | 子任务（递归同结构） |
    | plannedEnd | string(DateTimeOffset)\|null | 计划结束 |
    | taskBookId | string(uuid)\|null | 所属任务本 |
    | percentComplete | integer | 完成百分比，默认 0 |
  - 备注：前端 TaskResponse 另声明可选 `checklistItems`（types/index.ts:126），后端 Web 响应不返回该字段。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:480-516`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:181-191`；前端 `src/client-web/src/api/calendar.ts:77-89,350-380`（`GetTasksParams`、`buildTasksPath`、`getTasks`、`getTasksPaged`）

### POST /api/v1/calendar/tasks
- 用途：创建任务。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog createTask；WorkbenchPage/TaskListPage 间接经 taskToMutationData 组装）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | calendarId | string(uuid) | 否 | 缺省且无 dtStart 时 IsInbox=true |
  | title | string | 是 | ≤255 字符 |
  | description | string | 否 | 纯文本（安全校验） |
  | priority | integer | 是 | 优先级 |
  | estimatedDuration | string | 否 | ISO 8601 时长（`PT1H30M`）或 `hh:mm:ss`；<1 分钟报 02011 |
  | minimumSegment | string | 否 | 同上格式 |
  | due | string(DateTimeOffset) | 否 | 截止 |
  | dtStart | string(DateTimeOffset) | 否 | 计划开始 |
  | status | string | 否 | 缺省由实体默认 |
  | plannedEnd | string(DateTimeOffset) | 否 | 计划结束（须晚于 dtStart，02010） |
  | taskBookId | string(uuid) | 否 | 任务本；不存在报 02003 |
  | percentComplete | integer | 否 | 缺省 0 |
- 响应 data：`TaskResponse`（字段同 GET /calendar/tasks）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:518-522`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:151-164`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:1002-1049`；前端 `src/client-web/src/api/calendar.ts:55-68,382-385`（`TaskMutationData`）

### PUT /api/v1/calendar/tasks/{id}
- 用途：更新任务（整体替换式：title/description/priority/due/时长/dtStart 均按请求写入）。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog updateTask）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：`UpdateTaskRequest`（字段同 POST /calendar/tasks 的 Body）
- 响应 data：`TaskResponse`
- 备注：`status=COMPLETED` 时写入 CompletedAt；提供 dtStart 或 calendarId 会将 IsInbox 置 false。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:567-570`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:1051-1107`；前端 `src/client-web/src/api/calendar.ts:387-390`、`taskToMutationData:396-412`

### DELETE /api/v1/calendar/tasks/{id}
- 用途：软删除任务（进入回收站）。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog deleteTask）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数 / Body：无
- 响应 data：`CalendarOperationResult`（字段见 POST /calendar/tasks/batch-delete）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:572-575`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarDeleteService.cs:177-195`；前端 `src/client-web/src/api/calendar.ts:414-416`

### POST /api/v1/calendar/tasks/{id}/restore
- 用途：从回收站恢复任务。
- 认证：JWT
- Web 前端使用：否（前端经 `POST /calendar/recycle-bin/task/{id}/restore` 调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | restoreAsCopy | boolean | 否 | 默认 false |
- 响应 data：`CalendarOperationResult`
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:577-582`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:138-160`

### POST /api/v1/calendar/tasks/{id}/move
- 用途：拖拽移动任务：改计划开始/时长/排序（仅改 PIM 事实，不经确认流）。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:392-394 封装了 moveTask，但 UI 未调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | scheduledStart | string(DateTimeOffset) | 否 | 新计划开始；提供时 IsInbox 置 false |
  | duration | string(TimeSpan) | 否 | 时长（`hh:mm:ss`）；与 scheduledStart 同给时推算 PlannedEnd |
  | newSortOrder | integer | 否 | 新排序号 |
  | plannedEnd | string(DateTimeOffset) | 否 | 直接指定计划结束 |
- 响应 data：string（字面量 `"已移动"`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:524-530`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:193-198`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:1231-1265`；前端 `src/client-web/src/api/calendar.ts:392-394`

### POST /api/v1/calendar/tasks/{id}/plan
- 用途：把任务排入计划（写 DtStart/PlannedEnd，可选改预计时长）。
- 认证：JWT
- Web 前端使用：是（日历页 CalendarPage 拖拽排程）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | plannedStart | string(DateTimeOffset) | 是 | 计划开始（转 UTC） |
  | plannedEnd | string(DateTimeOffset) | 否 | 计划结束（须晚于开始，02010） |
  | estimatedDuration | string | 否 | ISO 8601 时长；缺省保留原值 |
- 响应 data：`TaskResponse`（IsInbox 被置 false）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:532-537`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:302-306`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:1109-1133`；前端 `src/client-web/src/api/calendar.ts:448-454`

### POST /api/v1/calendar/tasks/batch-delete
- 用途：批量软删除任务。
- 认证：JWT
- Web 前端使用：是（任务清单页 TaskListPage）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | ids | string(uuid)[] | 是 | 任务 ID 列表 |
- 响应 data：`CalendarOperationResult`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | operation | string | `calendar.tasks.batch_delete` |
  | operationId | string(uuid) | 操作 ID |
  | affectedCount | integer | 删除数 |
  | affectedIds | string(uuid)[] | 被删 ID（含子任务） |
  | samples | CalendarOperationSample[] | `{ id, type: "task", title, start, end, bookName }` ≤5 条 |
  | message | string | 结果消息 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:584-588`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:293,243-250`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarDeleteService.cs:197-260`；前端 `src/client-web/src/api/calendar.ts:783-789`

### POST /api/v1/calendar/tasks/batch-update
- 用途：批量修改任务状态/优先级/日历。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:791-802 封装了 batchUpdateTasks，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | ids | string(uuid)[] | 是 | 任务 ID 列表（空列表/全空 GUID 时返回 affectedCount=0） |
  | status | string | 否 | 三者至少其一，否则"没有更新任务" |
  | priority | integer | 否 | |
  | calendarId | string(uuid) | 否 | 目标日历，设置后 IsInbox=false |
- 响应 data：`CalendarOperationResult`（operation=`calendar.tasks.batch_update`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:590-594`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:295-300`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:1135-1229`；前端 `src/client-web/src/api/calendar.ts:791-802`

## 任务清单与时间段

### GET /api/v1/calendar/tasks/{id}/checklist
- 用途：（预期）列出任务检查清单条目。
- 认证：JWT
- Web 前端使用：否（路由不存在）
- **未注册，调用将 404**：后端只注册了 POST/PUT/DELETE 三个 checklist 路由（CalendarModule.cs:176,184,193），不存在 GET 路由；前端亦未封装 GET 调用（仅 path 函数 calendarApiPaths.taskChecklist，api/calendar.ts:178-180）。
- 响应 data：无（路由不存在）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:176-199`（仅 POST/PUT/DELETE）；前端 `src/client-web/src/api/calendar.ts:178-180`

### POST /api/v1/calendar/tasks/{id}/checklist
- 用途：为任务新增检查清单条目。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog addTaskChecklistItem）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | title | string | 是 | ≤255 字符 |
  | sortOrder | integer | 否 | 缺省追加到末尾（按现有条目数） |
- 响应 data：`TaskChecklistItem`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 条目 ID |
  | taskId | string(uuid) | 所属任务 |
  | title | string | 标题 |
  | isDone | boolean | 是否完成（新建为 false） |
  | sortOrder | integer | 排序号 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:176-182`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:341-344`；共享 DTO `src/Pim.Core/Planning/PlanningDtos.cs:17-22`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:308-334`；前端 `src/client-web/src/api/calendar.ts:560-566`；类型 `src/client-web/src/types/index.ts:178-189`

### PUT /api/v1/calendar/tasks/{id}/checklist/{itemId}
- 用途：更新检查清单条目标题/完成状态。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog updateTaskChecklistItem）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
  | itemId | string(uuid) | 是 | 条目 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | title | string | 否 | 非 null 时更新（≤255） |
  | isDone | boolean | 否 | 非 null 时更新 |
- 响应 data：`TaskChecklistItem`（字段同 POST）
- 备注：条目不存在报 02037。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:184-191`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:346-349`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:336-363`；前端 `src/client-web/src/api/calendar.ts:572-582`

### DELETE /api/v1/calendar/tasks/{id}/checklist/{itemId}
- 用途：软删除检查清单条目。
- 认证：JWT
- Web 前端使用：是（TaskEditorDialog deleteTaskChecklistItem）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
  | itemId | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：string（任务 ID 的字符串形式，PlanningModelService.cs:383）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:193-199`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:365-384`；前端 `src/client-web/src/api/calendar.ts:568-570`

### GET /api/v1/calendar/tasks/{id}/segments
- 用途：列出任务执行时间段（时间线）。
- 认证：JWT
- Web 前端使用：是（schedule 组件 TaskSegmentEditor）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数 / Body：无
- 响应 data：`TaskExecutionSegmentResponse[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 时间段 ID |
  | taskId | string(uuid) | 任务 ID |
  | taskTitle | string | 任务标题 |
  | startsAt | string(DateTimeOffset) | 开始 |
  | endsAt | string(DateTimeOffset) | 结束 |
  | status | string | 状态（1-40 字符自由值） |
  | source | string | 来源（1-40 字符自由值） |
  | planningReason | string\|null | 排程理由 |
  | confirmationId | string(uuid)\|null | 关联确认单 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:539-544`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:316-326`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:871-892`；前端 `src/client-web/src/api/calendar.ts:456-461`；类型 `src/client-web/src/types/index.ts:401-411`

### POST /api/v1/calendar/tasks/{id}/segments
- 用途：为任务新增执行时间段（同时解除收件箱态、回填 DtStart/PlannedEnd）。
- 认证：JWT
- Web 前端使用：是（TaskSegmentEditor）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 任务 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | startsAt | string(DateTimeOffset) | 是 | 开始（转 UTC） |
  | endsAt | string(DateTimeOffset) | 是 | 结束（须晚于开始，02024） |
  | status | string | 是 | 1-40 字符（02026） |
  | source | string | 是 | 1-40 字符 |
  | planningReason | string | 否 | |
- 响应 data：`TaskExecutionSegmentResponse`（字段同 GET segments）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:546-555`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:308-314`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:831-869`；前端 `src/client-web/src/api/calendar.ts:463-472`；类型 `src/client-web/src/types/index.ts:393-399`

### DELETE /api/v1/calendar/tasks/{taskId}/segments/{segmentId}
- 用途：软删除执行时间段。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:474-476 封装了 deleteTaskExecutionSegment，但 UI 未调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | taskId | string(uuid) | 是 | 任务 ID |
  | segmentId | string(uuid) | 是 | 时间段 ID |
- Query 参数 / Body：无
- 响应 data：string（字面量 `"deleted"`）
- 备注：时间段不存在报 02025。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:557-565`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:894-909`；前端 `src/client-web/src/api/calendar.ts:474-476`

## 图层

### GET /api/v1/calendar/layers
- 用途：工作台/日历统一图层查询（日程、任务段、习惯、可用时段、AI 建议五层合一，含周期展开）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage、日历页 CalendarPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | start | string(DateTimeOffset) | 是 | 窗口起点 |
  | end | string(DateTimeOffset) | 是 | 窗口终点（end ≤ start 报 02027） |
  | layers | string | 否 | 逗号分隔图层名（events/task-segments/habits/availability/ai-placeholders，支持别名 event/task/habit/ai 等，`all` 即全部）；缺省全部 |
  | outlookOnly | boolean | 否 | 默认 false；true 时仅保留 outlook 来源（outlook/outlook-graph/outlook-ics） |
  | timezone | string | 否 | 兼容性接受但忽略（start/end 已带偏移） |
- Body：无
- 响应 data：`CalendarLayerResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | start | string(DateTimeOffset) | 回显窗口起点 |
  | end | string(DateTimeOffset) | 回显窗口终点 |
  | items | CalendarLayerItem[] | 图层条目（按 StartsAt/Layer/Title/ObjectId 排序） |
  | items[].id | string | 组合 ID：`event:{occurrenceId}` / `task-segment:{id}` / `habit:{id}` / `availability:{id}` / `ai-placeholder:{id}` |
  | items[].layer | string | `events` / `task-segments` / `habits` / `availability` / `ai-placeholders` |
  | items[].objectType | string | `event` / `task-segment` / `habit-occurrence` / `availability-window` / `ai-planning-placeholder` |
  | items[].objectId | string(uuid) | 对象 ID（event 层为 OccurrenceId） |
  | items[].title | string | 标题 |
  | items[].startsAt | string(DateTimeOffset) | 开始 |
  | items[].endsAt | string(DateTimeOffset) | 结束 |
  | items[].source | string | 来源 |
  | items[].status | string | 状态（availability 层为 kind 值） |
  | items[].color | string | events 取日历色；task-segments `#22C55E`；habits `#A855F7`；availability `#0EA5E9`；ai-placeholders `#F97316` |
  | items[].requiresConfirmation | boolean | task-segments/habits 取 ConfirmationId.HasValue；ai-placeholders 恒 true；其余 false |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:79-98`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:403-428`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:56-210`；前端 `src/client-web/src/api/calendar.ts:478-483`；类型 `src/client-web/src/types/index.ts:141,413-438`

## 项目与任务本

### GET /api/v1/calendar/projects
- 用途：列出领域项目。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:540-543 封装了 getProjects，但 UI 未调用）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`DomainProjectDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 项目 ID |
  | name | string | 名称 |
  | description | string\|null | 描述 |
  | status | string | 状态（创建默认 `Active`） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:152-155`；DTO `src/Pim.Core/Planning/PlanningDtos.cs:3-7`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:212-221`；前端 `src/client-web/src/api/calendar.ts:540-543`；类型 `src/client-web/src/types/index.ts:149-154`

### POST /api/v1/calendar/projects
- 用途：创建领域项目。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:545-548 封装了 createProject，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 是 | ≤255 字符（02034 校验 1-255） |
  | description | string | 否 | |
  | status | string | 否 | ≤40 字符，默认 `Active` |
- 响应 data：`DomainProjectDto`（字段同 GET /calendar/projects）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:157-163`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:328-332`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:223-243`；前端 `src/client-web/src/api/calendar.ts:545-548`；类型 `src/client-web/src/types/index.ts:156-160`

### GET /api/v1/calendar/task-books
- 用途：列出任务本（附未删除任务计数）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage、任务清单页 TaskListPage、侧边栏 Sidebar、TaskEditorDialog）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`TaskBookDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 任务本 ID |
  | domainProjectId | string(uuid)\|null | 关联领域项目 |
  | name | string | 名称 |
  | kind | string | 类型（创建默认 `task`） |
  | status | string | 状态（默认 `Active`） |
  | taskCount | integer | 未删除任务数 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:164-167`；DTO `src/Pim.Core/Planning/PlanningDtos.cs:9-15`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:245-275`；前端 `src/client-web/src/api/calendar.ts:550-553`；类型 `src/client-web/src/types/index.ts:162-169`

### POST /api/v1/calendar/task-books
- 用途：创建任务本。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:555-558 封装了 createTaskBook，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | domainProjectId | string(uuid) | 否 | 项目不存在报 02028 |
  | name | string | 是 | ≤255 字符 |
  | kind | string | 否 | ≤40 字符，默认 `task` |
  | status | string | 否 | ≤40 字符，默认 `Active` |
- 响应 data：`TaskBookDto`（新建时 taskCount=0）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:169-174`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:334-339`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:277-306`；前端 `src/client-web/src/api/calendar.ts:555-558`；类型 `src/client-web/src/types/index.ts:171-176`

## 习惯与可用时间

### GET /api/v1/calendar/habits
- 用途：列出习惯例程。
- 认证：JWT
- Web 前端使用：是（习惯页 HabitsPage、习惯热力图 HabitCalendarHeatmap、图表 useExhibitionData）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`HabitRoutineDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 习惯 ID |
  | title | string | 标题 |
  | cadence | string(enum) | 枚举 `HabitCadence`：`Daily` / `Weekly` / `Monthly` / `Custom`（无法解析时归为 Custom） |
  | source | string | 来源（创建默认 `manual`） |
  | status | string | 状态（创建默认 `Active`） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:201-204`；DTO `src/Pim.Core/Planning/PlanningDtos.cs:24-29`、枚举 `src/Pim.Core/Planning/PlanningEnums.cs:26-32`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:386-401`；前端 `src/client-web/src/api/calendar.ts:584-587`；类型 `src/client-web/src/types/index.ts:191-197`

### POST /api/v1/calendar/habits
- 用途：创建习惯例程。
- 认证：JWT
- Web 前端使用：是（schedule 组件 HabitRoutineEditor）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | title | string | 是 | ≤255 字符 |
  | description | string | 否 | |
  | cadence | string | 否 | ≤40 字符，默认 `Daily`；可解析为 HabitCadence 枚举 |
  | source | string | 否 | ≤40 字符，默认 `manual` |
  | status | string | 否 | ≤40 字符，默认 `Active` |
  | ruleJson | string | 否 | 规则 JSON，缺省 `{}` |
- 响应 data：`HabitRoutineDto`（字段同 GET /calendar/habits）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:206-211`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:351-358`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:403-426`；前端 `src/client-web/src/api/calendar.ts:589-592`；类型 `src/client-web/src/types/index.ts:199-206`

### POST /api/v1/calendar/habits/{id}/occurrences
- 用途：打卡/创建一次习惯发生记录。
- 认证：JWT
- Web 前端使用：否（前端未封装）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 习惯 ID（不存在报 02030） |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | startsAt | string(DateTimeOffset) | 是 | 开始（转 UTC） |
  | endsAt | string(DateTimeOffset) | 是 | 结束（须晚于开始，02029） |
  | status | string | 否 | ≤40 字符，默认 `Planned` |
  | source | string | 否 | ≤40 字符，默认 `manual` |
- 响应 data：`HabitOccurrenceDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 发生记录 ID |
  | habitRoutineId | string(uuid) | 习惯 ID |
  | startsAt | string(DateTimeOffset) | 开始 |
  | endsAt | string(DateTimeOffset) | 结束 |
  | status | string | 状态 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:213-219`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:360-365`；共享 DTO `src/Pim.Core/Planning/PlanningDtos.cs:31-36`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:428-460`

### GET /api/v1/calendar/availability
- 用途：列出可用时间窗口。
- 认证：JWT
- Web 前端使用：否（前端未封装）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`AvailabilityWindowDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 窗口 ID |
  | startsAt | string(DateTimeOffset) | 开始（升序） |
  | endsAt | string(DateTimeOffset) | 结束 |
  | kind | string | `available` / 非可用类型（非 available 在排程中视为忙时） |
  | source | string | 来源 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:221-224`；DTO `src/Pim.Core/Planning/PlanningDtos.cs:38-43`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:462-471`

### POST /api/v1/calendar/availability
- 用途：创建可用时间窗口。
- 认证：JWT
- Web 前端使用：否（前端未封装）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | title | string | 是 | ≤255 字符 |
  | startsAt | string(DateTimeOffset) | 是 | 开始 |
  | endsAt | string(DateTimeOffset) | 是 | 结束（须晚于开始，02031） |
  | kind | string | 否 | ≤40 字符，默认 `available` |
  | source | string | 否 | ≤40 字符，默认 `manual` |
- 响应 data：`AvailabilityWindowDto`（字段同 GET /calendar/availability）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:226-231`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:367-373`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:473-500`

## AI 排程占位

### POST /api/v1/calendar/ai-placeholders
- 用途：手工创建 AI 排程占位（Suggested 态，供确认流消费）。
- 认证：JWT
- Web 前端使用：否（前端未封装）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | title | string | 是 | ≤255 字符 |
  | startsAt | string(DateTimeOffset) | 是 | 开始 |
  | endsAt | string(DateTimeOffset) | 是 | 结束（须晚于开始，02032） |
  | reason | string | 是 | 排程理由 |
  | source | string | 否 | ≤40 字符，默认 `ai` |
- 响应 data：`AiPlanningPlaceholderDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 占位 ID |
  | title | string | 标题 |
  | startsAt | string(DateTimeOffset) | 开始 |
  | endsAt | string(DateTimeOffset) | 结束 |
  | reason | string | 理由 |
  | confirmationId | string(uuid)\|null | 关联确认单（新建为 null） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:233-238`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:375-381`；共享 DTO `src/Pim.Core/Planning/PlanningDtos.cs:45-51`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:502-530,1002-1009`

### GET /api/v1/calendar/ai-placeholders
- 用途：列出排程建议（工作台 AI 规划面板）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | status | string | 否 | 按状态过滤；缺省仅 `Suggested`（最多返回 50 条，按开始时间升序） |
- Body：无
- 响应 data：`AiPlanPlaceholderViewDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 占位 ID |
  | title | string | 标题 |
  | startsAt | string(DateTimeOffset) | 开始 |
  | endsAt | string(DateTimeOffset) | 结束 |
  | reason | string | 理由 |
  | status | string | `Suggested` / `PendingConfirmation` / `Dismissed` |
  | source | string | `ai` / `rule-engine` / `manual` 等 |
  | confirmationId | string(uuid)\|null | 关联确认单 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:240-244`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:389-397`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:533-549,1011-1020`；前端 `src/client-web/src/api/calendar.ts:864-868`；类型 `src/client-web/src/types/index.ts:1685-1694`

### POST /api/v1/calendar/ai-placeholders/generate
- 用途：生成排程建议（优先 AI 网关，失败/关闭回退规则引擎；建议只入库不直接改事实）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage generateAiPlan）
- Path 参数 / Query 参数：无
- Body（可整体省略，后端参数声明为可空并回退默认值）：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | horizonDays | integer | 否 | 规划天数，clamp 1-30，默认 7 |
  | taskIds | string(uuid)[] | 否 | 限定候选任务；缺省取"有预计时长且未完成"的前 10 个任务 |
- 响应 data：`GenerateAiPlanResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | source | string | `ai` / `rule-engine` / `none`（无可排任务时 none） |
  | placeholders | AiPlanPlaceholderViewDto[] | 新建占位（≤10 条，字段同 GET /calendar/ai-placeholders） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:246-250`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:383-386,399-401`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:573-745`；前端 `src/client-web/src/api/calendar.ts:870-873`；类型 `src/client-web/src/types/index.ts:1696-1704`

### POST /api/v1/calendar/ai-placeholders/{id}/confirm
- 用途：确认排程建议，进入 L2 确认流（占位转 PendingConfirmation）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage confirmAiPlaceholder）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 占位 ID（不存在报 02033） |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`OperationConfirmationDto`（字段见 POST /calendar/data-center/batch/request-confirmation；operationType=`calendar.ai_placeholder.confirm`，riskLevel=`L2PimFactChange`，过期 12 小时）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:252-256`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:775-829`；前端 `src/client-web/src/api/calendar.ts:875-878`；类型 `src/client-web/src/types/index.ts:672-700`

### POST /api/v1/calendar/ai-placeholders/{id}/dismiss
- 用途：忽略排程建议（Suggested/Dismissed → Dismissed，不进确认流）。
- 认证：JWT
- Web 前端使用：是（工作台 WorkbenchPage dismissAiPlaceholder）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 占位 ID |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`AiPlanPlaceholderViewDto`（字段同 GET /calendar/ai-placeholders，status=`Dismissed`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:258-262`；服务 `src/modules/Pim.Module.Calendar/Services/PlanningModelService.cs:552-567`；前端 `src/client-web/src/api/calendar.ts:880-883`

## 提醒

### GET /api/v1/calendar/reminders
- 用途：列出当前用户提醒。
- 认证：JWT
- Web 前端使用：是（提醒页 RemindersPage、今日页 TodayOpsSections）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`ReminderResponse[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 提醒 ID |
  | relatedObjectType | string | 关联对象类型（缺省归一为 `object`；`confirmation` 时 detailUrl 指向确认页） |
  | relatedObjectId | string(uuid) | 关联对象 ID |
  | title | string | 标题 |
  | body | string | 正文（缺省空串） |
  | triggerReason | string | 触发原因（缺省空串） |
  | riskLevel | string | 风险级（OperationRiskLevel 取值，默认 `L1LowRiskAction`） |
  | channels | string[] | 渠道列表 |
  | doNotDisturbStart | string\|null | 免打扰开始 |
  | doNotDisturbEnd | string\|null | 免打扰结束 |
  | scheduledAt | string(DateTimeOffset) | 计划触发时间（升序） |
  | status | string | `Open` / `Snoozed` / `Dismissed` |
- 备注：前端 `ReminderSummary`（types/index.ts:208-224）额外声明 `escalationPolicy`/`deliveryHistory`/`responseHistory` 可选字段，后端 Web 响应不返回。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:264-267`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:601-614`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:64-73,200-213`；前端 `src/client-web/src/api/calendar.ts:594-597`；类型 `src/client-web/src/types/index.ts:208-224`

### POST /api/v1/calendar/reminders
- 用途：创建提醒。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:599-602 封装了 createReminder，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | relatedObjectType | string | 否 | 缺省 `object` |
  | relatedObjectId | string(uuid) | 是 | 不能为空 GUID（02043） |
  | title | string | 是 | ≤255 字符 |
  | body | string | 否 | |
  | triggerReason | string | 否 | |
  | riskLevel | string | 否 | 默认 `L1LowRiskAction` |
  | channels | string[] | 否 | 去空白去重 |
  | doNotDisturbStart / doNotDisturbEnd | string | 否 | 免打扰时间窗 |
  | scheduledAt | string(DateTimeOffset) | 是 | 缺失报 02044（前端类型亦标必填） |
- 响应 data：`ReminderResponse`（字段同 GET /calendar/reminders，status=`Open`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:269-274`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:588-599`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:32-62`；前端 `src/client-web/src/api/calendar.ts:599-602`；类型 `src/client-web/src/types/index.ts:242-253`

### POST /api/v1/calendar/reminders/{id}/snooze
- 用途：贪睡（推迟触发时间）。
- 认证：JWT
- Web 前端使用：是（RemindersPage snoozeReminder）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 提醒 ID（不存在报 02041） |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | scheduledAt | string(DateTimeOffset) | 否 | 新触发时间；缺省为当前 UTC + 15 分钟 |
- Body：无（前端发送空对象 `{}`）
- 响应 data：`ReminderResponse`（status=`Snoozed`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:276-284`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:75-83`；前端 `src/client-web/src/api/calendar.ts:604-611`

### POST /api/v1/calendar/reminders/{id}/dismiss
- 用途：忽略提醒。
- 认证：JWT
- Web 前端使用：是（RemindersPage dismissReminder）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 提醒 ID |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`ReminderResponse`（status=`Dismissed`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:286-290`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:85-92`；前端 `src/client-web/src/api/calendar.ts:613-616`

### POST /api/v1/calendar/reminders/{id}/actions/{action}
- 用途：处理提醒按钮动作（open/snooze/dismiss；高风险提醒的其他动作被拒并要求打开详情）。
- 认证：JWT
- Web 前端使用：是（RemindersPage handleReminderAction）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 提醒 ID |
  | action | string | 是 | 动作名（归一为小写）：`open` / `snooze` / `dismiss` / 其他自定义 |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`ReminderActionResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | kind | string | `Executed` / `OpenDetailRequired` |
  | status | string | 提醒当前状态（Open/Snoozed/Dismissed） |
  | detailUrl | string\|null | `confirmation` 类型为 `/confirmations/{id}`，否则 `/reminders/{id}` |
- 备注：riskLevel 属于 {L2PimFactChange, L3ExternalSourceOrWriteback, L4BatchOrDestructiveGovernance} 且 action 非 open/snooze/dismiss 时，记录投递并返回 `OpenDetailRequired`，不执行动作。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:292-297`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:616-620`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:94-123,239-242`；前端 `src/client-web/src/api/calendar.ts:618-624`；类型 `src/client-web/src/types/index.ts:236-240`

### GET /api/v1/calendar/reminders/delivery-log
- 用途：查询提醒投递/响应日志（最近 100 条）。
- 认证：JWT
- Web 前端使用：是（RemindersPage getReminderDeliveryLog）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`ReminderDeliveryDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 投递记录 ID |
  | reminderId | string(uuid) | 提醒 ID |
  | channel | string | 渠道（Web 等） |
  | status | string | `Created` / `Executed` / `OpenDetailRequired` 等 |
  | payloadJson | string | 通知载荷 JSON（含 title/body/riskLevel/detailUrl/actions） |
  | createdAt | string(DateTimeOffset) | 投递时间（倒序） |
  | respondedAt | string(DateTimeOffset)\|null | 响应时间 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:299-302`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:622-641`；服务 `src/modules/Pim.Module.Calendar/Services/ReminderService.cs:145-162`；前端 `src/client-web/src/api/calendar.ts:626-629`；类型 `src/client-web/src/types/index.ts:226-234`

## 报告

### GET /api/v1/calendar/reports
- 用途：列出报告工件。
- 认证：JWT
- Web 前端使用：是（报告页 ReportsPage、今日页 TodayOpsSections）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`ReportArtifactDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 报告 ID |
  | kind | string | `Daily` / `Weekly` / `Monthly` / `Project` |
  | projectId | string(uuid)\|null | 关联项目 |
  | riskLevel | string | 恒 `L0AutomaticArtifact` |
  | contentMarkdown | string | Markdown 正文 |
  | metricsJson | string | 指标 JSON（tasks/completedTasks/events/reminders/habits 计数） |
  | generatedAt | string(DateTimeOffset) | 生成时间（倒序） |
  | status | string | `Active` / `Archived` |
- 备注：前端 `ReportArtifact`（types/index.ts:261-273）额外声明 `title`/`suggestions`/`confirmationId` 可选字段，后端 Web 响应不返回。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:304-307`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:649-658`；服务 `src/modules/Pim.Module.Calendar/Services/ReportService.cs:94-103,163-172`；前端 `src/client-web/src/api/calendar.ts:631-634`；类型 `src/client-web/src/types/index.ts:261-273`

### POST /api/v1/calendar/reports/generate
- 用途：生成一份报告（规则统计模板，非 AI）。
- 认证：JWT
- Web 前端使用：是（ReportsPage generateReport）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | kind | string | 是 | `Daily` / `Weekly` / `Monthly` / `Project`；其他报 02045 |
  | date | string(DateOnly) | 是 | 报告日期（`yyyy-MM-dd`） |
  | projectId | string(uuid) | 否 | Project 报告关联项目 |
- 响应 data：`ReportArtifactDto`（字段同 GET /calendar/reports，status=`Active`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:309-317`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:643-647`；服务 `src/modules/Pim.Module.Calendar/Services/ReportService.cs:31-92`；前端 `src/client-web/src/api/calendar.ts:641-644`；类型 `src/client-web/src/types/index.ts:255-259`

### GET /api/v1/calendar/reports/{id}
- 用途：获取单份报告。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:636-639 封装了 getReport，但 UI 未调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 报告 ID（不存在报 02044） |
- Query 参数 / Body：无
- 响应 data：`ReportArtifactDto`（字段同 GET /calendar/reports）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:319-323`；服务 `src/modules/Pim.Module.Calendar/Services/ReportService.cs:105-106,158-161`；前端 `src/client-web/src/api/calendar.ts:636-639`

### POST /api/v1/calendar/reports/{id}/archive
- 用途：归档报告（status → Archived）。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:646-649 封装了 archiveReport，但 UI 未调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 报告 ID |
- Query 参数 / Body：无
- 响应 data：`ReportArtifactDto`（status=`Archived`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:325-329`；服务 `src/modules/Pim.Module.Calendar/Services/ReportService.cs:108-115`；前端 `src/client-web/src/api/calendar.ts:646-649`

### POST /api/v1/calendar/reports/suggestions/{id}/request-action
- 用途：对报告建议发起动作确认（创建 L2 确认单，建议转 PendingConfirmation）。
- 认证：JWT
- Web 前端使用：是（ReportsPage requestReportSuggestionAction）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 建议（ReportSuggestion）ID（不存在报 02043） |
- Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`OperationConfirmationDto`（operationType=`report.suggestion.{action}`，riskLevel=`L2PimFactChange`，过期 6 小时，allowedActions=[confirm, reject]；字段结构见 POST /calendar/data-center/batch/request-confirmation）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:331-336`；服务 `src/modules/Pim.Module.Calendar/Services/ReportService.cs:117-156`；前端 `src/client-web/src/api/calendar.ts:651-657`

## 回收站

### GET /api/v1/calendar/recycle-bin
- 用途：分页列出回收站条目（日历/任务本/事件/任务）。
- 认证：JWT
- Web 前端使用：是（回收站页 RecycleBinPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | type | string | 否 | `all`（默认）/ `calendar` / `task-book` / `event` / `task` |
  | search | string | 否 | 按标题或所属本名包含匹配 |
  | deletedFrom | string(DateTimeOffset) | 否 | 删除时间下界 |
  | deletedTo | string(DateTimeOffset) | 否 | 删除时间上界 |
  | page | integer | 否 | 默认 1 |
  | pageSize | integer | 否 | 默认 50，clamp 1-100 |
- Body：无
- 响应 data：`PagedResult<CalendarRecycleBinItem>`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | items | CalendarRecycleBinItem[] | 条目列表（按 DeletedAt 倒序） |
  | items[].id | string(uuid) | 条目 ID |
  | items[].type | string | `calendar` / `task-book` / `event` / `task` |
  | items[].title | string | 标题 |
  | items[].deletedAt | string(DateTimeOffset) | 删除时间 |
  | items[].bookName | string\|null | 所属日历/任务本名 |
  | items[].start | string(DateTimeOffset)\|null | 事件 DtStart / 任务 DtStart |
  | items[].end | string(DateTimeOffset)\|null | 事件 DtEnd / 任务 PlannedEnd 或 Due |
  | items[].source | string | 来源（事件取实体 source，其余 `manual`） |
  | items[].deletedByOperationId | string(uuid)\|null | 删除操作 ID |
  | items[].deletedByOperationKind | string\|null | 删除操作类型 |
  | totalCount / page / pageSize / totalPages | integer | 分页元数据 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:597-607`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:273-284`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:30-133`；前端 `src/client-web/src/api/calendar.ts:70-75,418-423`（`RecycleBinParams`）；类型 `src/client-web/src/types/index.ts:763-774`

### POST /api/v1/calendar/recycle-bin/{type}/{id}/restore-preview
- 用途：恢复前预览（子条目数量、样例、冲突列表）。
- 认证：JWT
- Web 前端使用：是（RecycleBinPage previewRecycleRestore）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | type | string | 是 | `calendar`（或 `calendar-book`）/ `task-book` / `event` / `task`；其他报 02021 |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`CalendarRestorePreviewResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | targetType | string | 归一化后的类型 |
  | targetId | string(uuid) | 条目 ID |
  | title | string | 标题 |
  | restoreCount | integer | 将恢复的条目数（含子条目） |
  | samples | CalendarOperationSample[] | 样例 ≤5 条 |
  | conflicts | CalendarRestoreConflict[] | 冲突：`{ deletedId, deletedType, activeId, activeType, reason, title }`；reason 取 `same-uid` / `same-source-uid` / `same-title-time` |
  | canRestoreWithoutConflict | boolean | conflicts 为空即 true |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:609-614`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:252-269`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:135-136,178-510,512-523`；前端 `src/client-web/src/api/calendar.ts:425-430`；类型 `src/client-web/src/types/index.ts:744-761`

### POST /api/v1/calendar/recycle-bin/{type}/{id}/restore
- 用途：执行恢复（可恢复为副本；冲突时须副本或报错）。
- 认证：JWT
- Web 前端使用：是（RecycleBinPage restoreRecycleItem）
- Path 参数：同 restore-preview（type、id）
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | restoreAsCopy | boolean | 否 | 默认 false；对 calendar/task-book 传 true 报 02022（仅日程和任务支持副本）；有冲突且未选副本报 02020 |
- 响应 data：`CalendarOperationResult`（operation=`calendar.recycle_bin.restore` 或 `calendar.recycle_bin.restore_copy`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:616-622`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarRecycleBinService.cs:138-235`；前端 `src/client-web/src/api/calendar.ts:432-438`

## 数据中心

### POST /api/v1/calendar/data-center/query
- 用途：数据中心全对象检索（事件/任务/时间段/习惯/习惯发生/可用窗口等跨类型聚合搜索）。
- 认证：JWT
- Web 前端使用：是（数据中心页 DataCenterPage）
- Path 参数：无
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | search | string | 否 | 标题/描述/位置/来源/状态/所属名包含匹配 |
  | objectType | string | 否 | `event` / `task` / `task-segment` / `habit` / `habit-occurrence` / `availability` 等；缺省全部 |
  | source | string | 否 | 按来源过滤 |
  | pendingOnly | boolean | 是 | 前端固定发送（语义为仅看待确认对象；当前实现按分页/过滤处理） |
  | page | integer | 否 | 默认 1 |
  | pageSize | integer | 否 | 默认 50，clamp 1-100 |
- 响应 data：`DataCenterQueryResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | items | DataCenterItem[] | `{ objectType, objectId, title, source, status, startsAt, endsAt, summary }` |
  | items[].objectType | string | 对象类型 |
  | items[].objectId | string(uuid) | 对象 ID |
  | items[].title | string | 标题 |
  | items[].source | string | 来源 |
  | items[].status | string | 状态 |
  | items[].startsAt | string(DateTimeOffset)\|null | 开始 |
  | items[].endsAt | string(DateTimeOffset)\|null | 结束 |
  | items[].summary | string | 摘要 |
  | page | integer | 当前页 |
  | pageSize | integer | 页大小 |
  | totalCount | integer | 总数 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:100-104`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:430-455`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterQueryService.cs:33-124`；前端 `src/client-web/src/api/calendar.ts:485-491`；类型 `src/client-web/src/types/index.ts:440-465`

### POST /api/v1/calendar/data-center/batch/preview
- 用途：批量操作（如 archive）风险预览。
- 认证：JWT
- Web 前端使用：是（数据中心批量预览组件 DataCenterBatchPreview）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | action | string | 是 | 当前仅支持 `archive`（执行时其他值报 02049） |
  | objects | DataCenterObjectRef[] | 是 | `{ objectType, objectId }` 列表；空/全无效报 02047/02048 |
  | reason | string | 否 | 操作理由 |
- 响应 data：`DataCenterBatchPreviewResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | riskLevel | string | 恒 `L4BatchOrDestructiveGovernance` |
  | requiresStrictConfirmation | boolean | 恒 true |
  | summary | string | 多行摘要（影响对象数、类型、可恢复性） |
  | affectedObjectTypes | string[] | 去重排序后的对象类型 |
  | affectedCount | integer | 对象数 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:106-111`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:457-474`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:39-62`；前端 `src/client-web/src/api/calendar.ts:493-499`；类型 `src/client-web/src/types/index.ts:330-347`

### POST /api/v1/calendar/data-center/batch/request-confirmation
- 用途：为批量操作创建严格确认单（需二级确认 confirm-strict）。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:501-507 封装了 requestDataCenterBatchConfirmation，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：同 POST /calendar/data-center/batch/preview
- 响应 data：`OperationConfirmationDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 确认单 ID（execute 用） |
  | requestedByUserId | string(uuid)\|null | 发起人 |
  | operationType | string | `data-center.batch.{action}` |
  | summary | string | 摘要 |
  | riskLevel | string(enum) | `Low` / `Medium` / `High` / `L0AutomaticArtifact` / `L1LowRiskAction` / `L2PimFactChange` / `L3ExternalSourceOrWriteback` / `L4BatchOrDestructiveGovernance` |
  | source | string | `data-center` |
  | payloadJson | string | 请求载荷 JSON |
  | previewJson | string | 预览 JSON |
  | status | string(enum) | `Pending` / `Confirmed` / `Rejected` / `Expired` / `Executed` |
  | expiresAt | string(DateTimeOffset) | 过期时间（8 小时） |
  | createdAt / confirmedAt / executedAt | string(DateTimeOffset) | 时间戳（后两者可空） |
  | resultJson | string\|null | 执行结果 |
  | correlationId | string\|null | 关联 ID |
  | changedFields | string[]\|null | 变更字段（此处为受影响类型） |
  | allowedActions | string[]\|null | `[confirm-strict, reject]` |
  | objectType / objectId | string\|null | `data-center-batch` / null |
  | requiresSecondLevelConfirmation | boolean | true |
  | beforeJson / afterJson | string\|null | null |
  | requiresStrictConfirmation | boolean | true |
  | auditBatchId | string(uuid)\|null | 审计批次 |
  | aiRecommendation / externalEffect / recoveryPath | string\|null | 提示文案 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:113-118`；共享 DTO `src/Pim.Core/Operations/ConfirmationDtos.cs:26-53`、枚举 `src/Pim.Core/Operations/OperationEnums.cs:42-61`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:64-96`；前端 `src/client-web/src/api/calendar.ts:501-507`；类型 `src/client-web/src/types/index.ts:672-700`

### POST /api/v1/calendar/data-center/batch/execute
- 用途：执行已确认的批量操作。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:509-515 封装了 executeDataCenterBatch，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | confirmationId | string(uuid) | 是 | 确认单 ID（不存在报 02046） |
- 响应 data：`DataCenterBatchExecutionResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | confirmationId | string(uuid) | 确认单 ID |
  | status | string | `Executed` 等 |
  | affectedCount | integer | 实际影响对象数 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:120-125`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:476-484`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:98-113,177-260`；前端 `src/client-web/src/api/calendar.ts:509-515`；类型 `src/client-web/src/types/index.ts:349-353`

### GET /api/v1/calendar/data-center/audit/export
- 用途：导出当前用户审计版本数据（JSON 内容封装在响应内）。
- 认证：JWT
- Web 前端使用：是（DataCenterPage getAuditExport）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | start | string(DateTimeOffset) | 否 | 下界，缺省 DateTimeOffset.MinValue |
  | end | string(DateTimeOffset) | 否 | 上界，缺省 DateTimeOffset.MaxValue |
- Body：无
- 响应 data：`AuditExportResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | fileName | string | 导出文件名 |
  | contentType | string | MIME 类型 |
  | content | string | 导出内容（JSON 字符串） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:127-136`；共享 DTO `src/Pim.Core/Audit/AuditVersionDtos.cs:26-29`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:115-119`；前端 `src/client-web/src/api/calendar.ts:517-522`；类型 `src/client-web/src/types/index.ts:314-318`

### POST /api/v1/calendar/data-center/restore/preview
- 用途：按审计版本 ID 预览恢复（变更字段、前后值）。
- 认证：JWT
- Web 前端使用：是（DataCenterPage previewDataCenterRestore）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | auditVersionId | string(uuid) | 是 | 审计版本 ID |
  | reason | string | 否 | 理由（前端传"数据中心版本恢复预览"） |
- 响应 data：`RestorePreviewResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | objectType | string | 对象类型 |
  | objectId | string(uuid) | 对象 ID |
  | summary | string | 摘要 |
  | requiresConfirmation | boolean | 是否需确认 |
  | changedFields | string[] | 变更字段 |
  | beforeJson | string\|null | 前值 JSON |
  | afterJson | string\|null | 后值 JSON |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:138-143`；共享 DTO `src/Pim.Core/Audit/AuditVersionDtos.cs:17-24`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:121-124`；前端 `src/client-web/src/api/calendar.ts:524-530`；类型 `src/client-web/src/types/index.ts:320-328`

### POST /api/v1/calendar/data-center/restore/request-confirmation
- 用途：为审计版本恢复创建严格确认单。
- 认证：JWT
- Web 前端使用：否（api/calendar.ts:532-538 封装了 requestDataCenterRestoreConfirmation，但 UI 未调用）
- Path 参数 / Query 参数：无
- Body：同 POST /calendar/data-center/restore/preview
- 响应 data：`OperationConfirmationDto`（operationType=`data-center.restore`，riskLevel=`L4BatchOrDestructiveGovernance`，allowedActions=[confirm-strict, reject]，before/after 取预览值；字段结构见 POST /calendar/data-center/batch/request-confirmation）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:145-150`；服务 `src/modules/Pim.Module.Calendar/Services/DataCenterGovernanceService.cs:126-158`；前端 `src/client-web/src/api/calendar.ts:532-538`

## Outlook 同步

### GET /api/v1/calendar/outlook/settings
- 用途：读取 Outlook 连接设置（Client ID、状态、令牌健康度）。
- 认证：JWT
- Web 前端使用：是（同步页 SyncPage、工作台 WorkbenchPage、EventEditorDialog）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`OutlookSettingsResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | provider | string | 恒 `outlook` |
  | tenantId | string | `common`（未配置时也为 common） |
  | clientId | string\|null | Microsoft 应用 Client ID |
  | scopes | string | `Calendars.ReadWrite offline_access User.Read openid profile` |
  | status | string | 原始连接状态：`not-connected` / `waiting-for-user` / `connected` / `reauth-required` / `failed` |
  | tokenHealth | string | `missing` / `healthy` / `interaction-required` 等 |
  | lastSyncedAt | string(DateTimeOffset)\|null | 最近同步时间 |
  | lastError | string\|null | 最近错误 |
  | uiStatus | string | UI 映射：`not-connected→failed`、`waiting-for-user→waiting-auth`、`connected→connected`、`reauth-required→reauth-required`、其余 `failed`；未配置时 `not-configured` |
  | activeAuthorization | OutlookAuthorizationSessionResponse\|null | 本端点恒 null（字段见 POST /calendar/outlook/device-code） |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:694-704,1056-1083`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:505-516`；前端 `src/client-web/src/api/calendar.ts:659-664`；类型 `src/client-web/src/types/index.ts:467-478`

### PUT /api/v1/calendar/outlook/settings
- 用途：保存 Microsoft Client ID（无连接时创建连接行；TenantId/Scopes 固定）。
- 认证：JWT
- Web 前端使用：是（SyncPage updateOutlookSettings）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | clientId | string(uuid) | 是 | Azure 应用注册 Client ID（`UpdateOutlookClientIdRequest(Guid ClientId)`，需可解析为 GUID） |
- 响应 data：`OutlookSettingsResponse`（字段同 GET /calendar/outlook/settings）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:706-732`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:72`；前端 `src/client-web/src/api/calendar.ts:666-672`；类型 `src/client-web/src/types/index.ts:480-482`

### POST /api/v1/calendar/outlook/device-code
- 用途：发起设备码授权（创建授权会话并启动流程）。
- 认证：JWT
- Web 前端使用：是（SyncPage createOutlookDeviceCode）
- Path 参数 / Query 参数 / Body：无（前端发送空对象 `{}`）
- 响应 data：`OutlookAuthorizationSessionResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 会话 ID（轮询用） |
  | status | string | 会话状态（`starting` 等） |
  | verificationUri | string\|null | 微软验证页 |
  | userCode | string\|null | 用户码 |
  | expiresAt | string(DateTimeOffset)\|null | 过期时间 |
  | accountDisplayName | string\|null | 账户显示名 |
  | accountLoginHint | string\|null | 登录提示 |
  | errorCode | string\|null | 错误码 |
  | errorMessage | string\|null | 错误消息 |
  | recoveryAction | string\|null | 恒 null（后端 ToSessionResponse 固定传 null） |
- 备注：连接不存在报 02005 "Outlook is not connected."；Client ID 未配置报 02005 "Microsoft Client ID is not configured."。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:734-758,1085-1087`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:3-13`；前端 `src/client-web/src/api/calendar.ts:674-680`；类型 `src/client-web/src/types/index.ts:484-495`

### POST /api/v1/calendar/outlook/device-code/poll
- 用途：轮询设备码授权会话状态。
- 认证：JWT
- Web 前端使用：是（SyncPage pollOutlookDeviceCode）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | sessionId | string(uuid) | 是 | 授权会话 ID |
- 响应 data：`OutlookAuthorizationSessionResponse`（字段同 POST /calendar/outlook/device-code）
- 备注：会话不存在（或不属于当前用户）返回 HTTP 404，body `ApiResponse<string>` code=404 "Session not found."。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:760-774`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:73`；前端 `src/client-web/src/api/calendar.ts:682-688`

### POST /api/v1/calendar/outlook/device-code/{sessionId}/cancel
- 用途：取消进行中的设备码授权会话。
- 认证：JWT
- Web 前端使用：是（SyncPage cancelOutlookDeviceCode）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | sessionId | string(uuid) | 是 | 会话 ID |
- Query 参数 / Body：无
- 响应 data：string（字面量 `"已取消"`）
- 备注：会话不存在/无法取消返回 HTTP 404（code=404 "Session not found."）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:776-792`；前端 `src/client-web/src/api/calendar.ts:690-695`

### POST /api/v1/calendar/outlook/check
- 用途：检查 Outlook 连接健康度（调 Graph /me 并触发日历发现，更新状态）。
- 认证：JWT
- Web 前端使用：是（SyncPage checkOutlookConnection）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`OutlookSettingsResponse`（字段同 GET /calendar/outlook/settings；成功后 status=`connected`、tokenHealth=`healthy`；需重新授权时 status=`reauth-required`、tokenHealth=`interaction-required`；连接行不存在时返回未配置默认值）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:794-830`；前端 `src/client-web/src/api/calendar.ts:697-702`

### POST /api/v1/calendar/outlook/calendars/discover
- 用途：从 Microsoft Graph 发现日历并刷新绑定（手动动作，直接打 Graph）。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookDiscover）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`OutlookCalendarBindingResponse[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 绑定 ID |
  | pimCalendarId | string(uuid) | 对应 PIM 日历 ID |
  | graphCalendarId | string | Graph 日历 ID |
  | groupId | string\|null | Graph 日历组 ID |
  | groupName | string\|null | 组名 |
  | name | string | 日历名 |
  | color | string\|null | 颜色 |
  | ownerName | string\|null | 所有者名 |
  | ownerAddress | string\|null | 所有者邮箱 |
  | isDefault | boolean | 是否默认日历 |
  | canEdit | boolean | 是否可写 |
  | isSelected | boolean | 是否选中参与同步 |
  | remoteState | string | `active` / `remote-missing` 等 |
  | lastSyncedAt | string(DateTimeOffset)\|null | 最近同步 |
  | lastError | string\|null | 最近错误 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:832-837`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:15-20`；服务 `src/modules/Pim.Module.Calendar/Services/OutlookCalendarSyncService.cs`（DiscoverAsync，remoteState 写 `active`）；前端 `src/client-web/src/api/calendar.ts:731-736`；类型 `src/client-web/src/types/index.ts:497-513`

### GET /api/v1/calendar/outlook/calendars
- 用途：只读列出已存储的 Outlook 日历绑定（不打 Graph；同步页刷新 remote_state 用）。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookBindings）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`OutlookCalendarBindingResponse[]`（字段同 POST /calendar/outlook/calendars/discover）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:842-847`；前端 `src/client-web/src/api/calendar.ts:738-743`

### PUT /api/v1/calendar/outlook/calendars/selection
- 用途：保存参与同步的日历绑定选择。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookSelection）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | selectedBindingIds | string(uuid)[] | 是 | 选中的绑定 ID 列表 |
- 响应 data：`OutlookCalendarBindingResponse[]`（保存后的最新绑定列表，字段同 discover）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:849-859`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:76`；前端 `src/client-web/src/api/calendar.ts:745-751`

### POST /api/v1/calendar/outlook/sync
- 用途：手动触发 Outlook 日历同步批次。
- 认证：JWT
- Web 前端使用：是（SyncPage runOutlookSync，默认 `{ mode: 'normal' }`）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | mode | string | 是 | 枚举（服务端 AllowedModes）：`normal` / `full-resources` / `range-instances`；其他值拒绝 |
  | calendarBindingIds | string(uuid)[] | 否 | 限定同步的绑定；缺省按选中绑定 |
  | rangeStart | string(DateTimeOffset) | 否 | range-instances 模式窗口起点 |
  | rangeEnd | string(DateTimeOffset) | 否 | range-instances 模式窗口终点 |
  | retryOfBatchId | string(uuid) | 否 | 作为某失败批次的重试 |
- 响应 data：`OutlookSyncBatchResponse`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 批次 ID |
  | provider | string | `outlook` |
  | status | string | `running` / `completed` / `failed` / `interrupted` / `canceled` 等 |
  | readCount | integer | 读取数 |
  | createdCount | integer | 新建数 |
  | updatedCount | integer | 更新数 |
  | conflictCount | integer | 冲突数 |
  | confirmationCount | integer | 待确认数 |
  | failureCount | integer | 失败数 |
  | steps | OutlookSyncStep[] | `{ name, status, detail, at }` |
  | errorSummary | string\|null | 错误摘要 |
  | startedAt | string(DateTimeOffset) | 开始时间 |
  | finishedAt | string(DateTimeOffset)\|null | 结束时间 |
  | mode | string\|null | 同步模式（写回批次为 `writeback`） |
  | requestedWindowStart | string(DateTimeOffset)\|null | 请求窗口起点 |
  | requestedWindowEnd | string(DateTimeOffset)\|null | 请求窗口终点 |
  | perCalendarJson | string\|null | 每日历结果 JSON |
  | cancelRequested | boolean | 是否已请求取消 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:861-867,1089-1101`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:22-27`、`src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:537-563`；服务 `src/modules/Pim.Module.Calendar/Services/OutlookCalendarSyncService.cs:454-458`（AllowedModes）；前端 `src/client-web/src/api/calendar.ts:704-710`；类型 `src/client-web/src/types/index.ts:515-549`

### POST /api/v1/calendar/outlook/sync/{batchId}/cancel
- 用途：请求取消运行中的同步批次。
- 认证：JWT
- Web 前端使用：是（SyncPage cancelOutlookSync）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | batchId | string(uuid) | 是 | 批次 ID |
- Query 参数 / Body：无
- 响应 data：string（字面量 `"已取消"`；仅置 CancelRequested 标志）
- 备注：批次不存在/不在 running 态返回 HTTP 404（code=404 "Batch not found or not running."）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:869-886`；前端 `src/client-web/src/api/calendar.ts:724-729`

### GET /api/v1/calendar/outlook/sync/batches
- 用途：分页列出同步批次历史。
- 认证：JWT
- Web 前端使用：是（SyncPage getOutlookSyncBatchesPaged）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | page | integer | 否 | 默认 1（最小 1） |
  | pageSize | integer | 否 | 默认 20，clamp 1-100 |
- Body：无
- 响应 data：自定义分页对象（**不是** PagedResult，字段名为 `total`）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | items | OutlookSyncBatchResponse[] | 批次列表（字段同 POST /calendar/outlook/sync；按 StartedAt 倒序） |
  | total | integer | 总数（非 totalCount） |
  | page | integer | 当前页 |
  | pageSize | integer | 页大小 |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:888-916`；前端 `src/client-web/src/api/calendar.ts:262-267,712-717`；类型 `src/client-web/src/types/index.ts:584-589`（`OutlookSyncBatchPage`）

### POST /api/v1/calendar/outlook/events/writeback
- 用途：经确认流把 PIM 事件变更写回 Microsoft Graph（创建/更新/删除）。
- 认证：JWT
- Web 前端使用：是（EventEditorDialog writeOutlookEvent；经 authedFetch 显式允许 409/412 状态）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | operation | string | 是 | `create` / `update` / `delete` |
  | calendarBindingId | string(uuid) | 是 | 目标 Outlook 绑定 ID |
  | eventId | string(uuid) | 否 | update/delete 时的事件 ID |
  | draft | OutlookEventDraft | 否 | create/update 载荷（UnifiedEventDraft 全字段 + 可选 rRule/uid） |
  | scope | string | 是 | `instance` / `series` |
  | clientOperationId | string(uuid) | 是 | 客户端幂等操作 ID |
  | expectedEtag | string | 否 | 乐观并发校验用的 ETag |
  | originalEventId | string(uuid) | 否 | 周期实例原始 ID |
  | recurrenceId | string | 否 | 周期实例 RecurrenceId |
- 响应 data：`OutlookWriteResult`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | status | string | `created` / `updated` / `deleted` / `conflict` / `reauth-required` / `error` |
  | event | EventResponse\|null | 本次写回结果事件（delete 为 null） |
  | latestEvent | EventResponse\|null | 冲突时 Graph 最新事件快照 |
  | latestEtag | string\|null | 最新 ETag |
  | errorCode | string\|null | `CONFLICT` / `REAUTH_REQUIRED` / 其他错误码 |
  | errorMessage | string\|null | 中文错误消息 |
- 备注：冲突语义——服务端 Graph 返回 412 PreconditionFailed / 409 Conflict 时构造 `status=conflict, errorCode=CONFLICT` 的结果，路由以 HTTP 409 返回但 body 仍是 `ApiResponse` 成功封装（`Results.Conflict(ApiResponse<OutlookWriteResult>.Ok(result))`，CalendarModule.cs:925-926）；前端 authedFetch 将 409/412 列为允许状态后按 data 解析。需重新授权时 status=`reauth-required`、errorCode=`REAUTH_REQUIRED`（HTTP 200）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:918-928`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:53-70`、`src/client-web/src/types/index.ts:627-653`；服务 `src/modules/Pim.Module.Calendar/Services/OutlookEventWriteService.cs:185,337,480,575-576,680-683,711-713`；前端 `src/client-web/src/api/calendar.ts:753-760`

### POST /api/v1/calendar/outlook/disconnect
- 用途：断开 Outlook 连接（清令牌缓存、请求取消非写回同步批次）。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookDisconnect）
- Path 参数 / Query 参数 / Body：无
- 响应 data：string（字面量 `"已断开"`）
- 备注：连接行保留（status=`not-connected`、tokenHealth=`missing`）；不删除本地日历/事件（那由 DELETE local-data 完成）。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:930-968`；前端 `src/client-web/src/api/calendar.ts:776-781`

### GET /api/v1/calendar/outlook/local-data/preview
- 用途：预览断开后将清理的本地 Outlook 数据量。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookLocalDataPreview）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`OutlookLocalDataPreview`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | bindingCount | integer | 绑定数 |
  | calendarCount | integer | outlook 来源且未删除的日历数 |
  | eventCount | integer | outlook 来源且未删除的事件数 |
- 备注：连接不存在时返回 `(0, 0, 0)`。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:970-998`；DTO `src/modules/Pim.Module.Calendar/DTOs/OutlookSyncDtos.cs:74`；前端 `src/client-web/src/api/calendar.ts:762-767`；类型 `src/client-web/src/types/index.ts:591-595`

### DELETE /api/v1/calendar/outlook/local-data
- 用途：清理本地 Outlook 数据（软删 outlook 事件与日历、删绑定、清令牌缓存）。
- 认证：JWT
- Web 前端使用：是（SyncPage outlookLocalDataDelete）
- Path 参数 / Query 参数 / Body：无
- 响应 data：string（`"已清理"`；无连接时 `"无本地数据"`）
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:1000-1053`；前端 `src/client-web/src/api/calendar.ts:769-774`

## ICS

### POST /api/v1/calendar/import-ics
- 用途：导入 Outlook/通用 ICS 文件（解析、去重、落库）。
- 认证：JWT
- Web 前端使用：是（CalendarDataManager importIcs；用原生 fetch + FormData）
- Path 参数 / Query 参数：无
- Body（multipart/form-data）：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | file | file(.ics) | 是 | ICS 文本文件；缺失报 400（code=400 "缺少文件字段"）；非 multipart 报 400（"需要 multipart/form-data"） |
  | calendarId | string(uuid) | 否 | 目标日历；缺省按 kind 自动选择/创建默认日历 |
- 响应 data：`ImportReport`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | imported | integer | 成功导入数 |
  | skipped | integer | 跳过数 |
  | skippedReasons | object | 原因 → 次数映射（`Record<string, number>`） |
  | samples | ImportSkippedItem[] | 跳过样例：`{ reason, title, start, uid }` |
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:637-661`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:491-503`；服务 `src/modules/Pim.Module.Calendar/Services/CalendarService.cs:257-355`；前端 `src/client-web/src/api/calendar.ts:848-861`；类型 `src/client-web/src/types/index.ts:776-788`

### GET /api/v1/calendar/export-ics
- 用途：导出 ICS 文件下载（text/calendar，附件名 `pim-events.ics`）。
- 认证：JWT
- Web 前端使用：是（CalendarDataManager exportIcs；用原生 fetch 携带 Bearer 头下载 blob）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | start | string(DateTimeOffset) | 否 | 窗口起点，缺省 MinValue |
  | end | string(DateTimeOffset) | 否 | 窗口终点，缺省 MaxValue |
  | ids | string | 否 | 逗号分隔事件 ID 列表；给定后在窗口结果内过滤 |
- Body：无
- 响应 data：二进制 `text/calendar` 文件流（`Results.File`，非 ApiResponse 封装）
- 备注（前后端方法差异核查结论）：任务给定的疑点不成立——后端为 GET（CalendarModule.cs:663，`MapGet("/export-ics")`），前端 api/calendar.ts:829-846 的 `exportIcs` 同样使用 `fetch`（默认 GET，未指定 method）下载 blob；两端一致，无 POST 调用。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:663-691`；服务 `src/modules/Pim.Module.Calendar/Services/IcsService.cs`（ExportEvents）；前端 `src/client-web/src/api/calendar.ts:829-846`

## 排程

### POST /api/v1/calendar/schedule
- 用途：对给定任务集合运行排程引擎（greedy/csp/genetic 三算法），返回排程方案。
- 认证：JWT
- Web 前端使用：否（前端 api/calendar.ts 未封装该调用）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | taskIds | string(uuid)[] | 是 | 任务 ID 列表（仅估计时长非空的任务参与排程） |
- 响应 data：`ScheduleSolution[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | algorithmName | string | `greedy` / `csp` / `genetic` |
  | slots | ScheduledSlot[] | `{ taskId, title, start, end }` |
  | slots[].taskId | string(uuid) | 任务 ID |
  | slots[].title | string | 任务标题 |
  | slots[].start | string(DateTimeOffset) | 排程开始（窗口自当前时刻起 14 天） |
  | slots[].end | string(DateTimeOffset) | 排程结束 |
  | metrics | object | 指标映射：`tasks_scheduled` / `total_tasks` / `slots_allocated`（greedy、csp）、`fitness`（genetic）等 |
- 备注：可用时间窗口（kind=`available`）之外一律视为忙时；纯内存计算，不落库、不经确认流。AI 生成建议的规则引擎回退路径（PlanningModelService.GenerateWithEngineAsync）复用同一引擎。
- 来源：后端 `src/modules/Pim.Module.Calendar/CalendarModule.cs:625-634`；DTO `src/modules/Pim.Module.Calendar/DTOs/CalendarDtos.cs:200-202`；服务 `src/modules/Pim.Module.Calendar/Services/SchedulingEngine.cs:23-76`、`Services/SchedulingAlgorithms.cs:7-8`；前端无封装（grep `'/calendar/schedule'` 无命中）
