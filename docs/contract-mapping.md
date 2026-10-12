# 接口契约映射清单（批次 1：响应定义 / Response definitions）

> 由 `contract/openapi.json` 与源码自动核对生成。约定与例外见文末。

## 覆盖统计 / Coverage

- operation 总数：313
- 响应带 schema/content 的 operation：310
- 仅声明无 content 状态码（空体 401/403/302/重定向、协议端点）：其余

## 逐端点映射 / Per-operation mapping

### src/Pim.Api/Endpoints/AdminEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/admin/users` | 200:`AdminUserDtoIReadOnlyListApiResponse`、401、403、500:`StringApiResponse` |
| POST | `/api/v1/admin/users/{id}/role` | 200:`AdminUserDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/admin/users/{id}/status` | 200:`AdminUserDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/AiEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| POST | `/api/v1/ai/health-check` | 200:`AiStatusDtoApiResponse`、401、403、500:`StringApiResponse` |
| GET | `/api/v1/ai/requests` | 200:`AiRequestLogListItemDtoPagedResultApiResponse`、400:`StringApiResponse`、401、403、500:`StringApiResponse` |
| GET | `/api/v1/ai/requests/{id}` | 200:`AiRequestLogDetailDtoApiResponse`、401、403、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/ai/status` | 200:`AiStatusDtoApiResponse`、401、403、500:`StringApiResponse` |
| POST | `/api/v1/ai/test` | 200:`AiResultApiResponse`、401、403、500:`StringApiResponse` |
| GET | `/api/v1/ai/usage/summary` | 200:`AiUsageSummaryDtoApiResponse`、400:`StringApiResponse`、401、403、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/AuthEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| POST | `/api/v1/auth/login` | 200:`AuthResponseApiResponse`、400:`StringApiResponse`、401、403:`StringApiResponse`、429、500:`StringApiResponse` |
| GET | `/api/v1/auth/me` | 200:`UserInfoApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/auth/refresh` | 200:`AuthResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/auth/register` | 201:`AuthResponseApiResponse`、400:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/DaemonEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| POST | `/api/v1/daemon/heartbeat` | 200:`DaemonHeartbeatDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/daemon/heartbeats` | 200:`DaemonHeartbeatDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/daemon/planned-offline` | 200:`DaemonHeartbeatDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/DataReliabilityEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/data-reliability/inspection` | 200:`DataReliabilityInspectionReportApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/data-reliability/inspection/refresh` | 200:`DataReliabilityInspectionReportApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/data-reliability/rules/{code}/violations` | 200:`DataReliabilityViolationExportApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/EndpointEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/endpoints` | 200:`EndpointStatusDtoIReadOnlyListApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/endpoints/{deviceId}/collection-quality` | 200:`EndpointCollectionQualityDtoApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/endpoints/{deviceId}/heartbeat` | 200:`EndpointStatusDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/endpoints/{deviceId}/notification-actions` | 200:`EndpointNotificationActionResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/OperationsEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/operations/audit/export` | 200:`AuditExportResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/operations/audit/{auditVersionId}/restore-preview` | 200:`RestorePreviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/operations/audit/{objectType}/{objectId}` | 200:`AuditTimelineResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/operations/confirmations/pending` | 200:`OperationConfirmationDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/operations/confirmations/{id}` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/operations/confirmations/{id}/confirm` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/operations/confirmations/{id}/confirm-second-level` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/operations/confirmations/{id}/confirm-strict` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/operations/confirmations/{id}/reject` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/OpsDbEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/ops/db/describe` | 200:`ColumnInfoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429、500:`StringApiResponse`、503 |
| POST | `/api/v1/ops/db/query` | 200:`OpsDbQueryResultApiResponse`、206:`OpsDbQueryResultApiResponse`、400:`StringApiResponse`、401、403、429、500:`StringApiResponse`、503 |
| GET | `/api/v1/ops/db/tables` | 200:`TableInfoIReadOnlyListApiResponse`、401、403、429、500:`StringApiResponse`、503 |

### src/Pim.Api/Endpoints/OpsHealthEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/ops/health` | 200:`OpsHealthResponse`、401、403、429、500:`StringApiResponse`、503 |

### src/Pim.Api/Endpoints/OpsLogsEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/ops/logs/files` | 200:`LogFileInfoIReadOnlyListApiResponse`、401、429、500:`StringApiResponse`、503 |
| GET | `/api/v1/ops/logs/query` | 200:`OpsLogsResultApiResponse`、206:`OpsLogsResultApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429、500:`StringApiResponse`、503 |
| GET | `/api/v1/ops/logs/tail` | 200:`OpsLogsResultApiResponse`、206:`OpsLogsResultApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429、500:`StringApiResponse`、503 |

### src/Pim.Api/Endpoints/StatusEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/status` | 200:`SystemStatusDetailDtoApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/status/summary` | 200:`SystemStatusSummaryDtoApiResponse`、401、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/TileEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/tiles/{z}/{x}/{y}.png` | 200、400:`TileErrorDto`、500、502 |

### src/Pim.Api/Endpoints/TodayEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/today/sections` | 200:`TodaySectionRegistryDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/today/sections/{sectionId}` | 200:`TodaySectionDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |

### src/Pim.Api/Endpoints/VersionEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/version` | 200:`ApiVersionResponse`、500:`StringApiResponse` |

### src/Pim.Api/Modules/ClientShell/ClientShellModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/client/shell/latest` | 200:`ClientShellLatestResponse`、500:`StringApiResponse` |
| GET | `/api/v1/client/shell/latest` | 200:`ClientShellLatestResponse`、500:`StringApiResponse` |

### src/Pim.Api/Program.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| DELETE | `/api/{path}` | 404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/{path}` | 404:`StringApiResponse`、500:`StringApiResponse` |
| PATCH | `/api/{path}` | 404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/{path}` | 404:`StringApiResponse`、500:`StringApiResponse` |
| PUT | `/api/{path}` | 404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/health` | 200:`HealthResponse`、500:`StringApiResponse` |

### src/Pim.Api/Search/SearchEndpoints.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/search` | 200:`SearchResultPagedResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/modules/Pim.Module.Calendar/CalendarModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/calendar/ai-placeholders` | 200:`AiPlanPlaceholderViewDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/ai-placeholders` | 201:`AiPlanningPlaceholderDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/ai-placeholders/generate` | 200:`GenerateAiPlanResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/ai-placeholders/{id}/confirm` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/ai-placeholders/{id}/dismiss` | 200:`AiPlanPlaceholderViewDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/availability` | 200:`AvailabilityWindowDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/availability` | 201:`AvailabilityWindowDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/calendars` | 200:`CalendarResponseListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/calendars` | 201:`CalendarResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/calendars/{id}` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/calendars/{id}` | 200:`CalendarResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/calendars/{id}/delete-preview` | 200:`CalendarDeletePreviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/calendars/{id}/restore` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/data-center/audit/export` | 200:`AuditExportResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/batch/execute` | 200:`DataCenterBatchExecutionResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/batch/preview` | 200:`DataCenterBatchPreviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/batch/request-confirmation` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/query` | 200:`DataCenterQueryResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/restore/preview` | 200:`RestorePreviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/data-center/restore/request-confirmation` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/events` | 200:`EventResponsePagedResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/events` | 201:`EventResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/events/batch-delete` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/events/{eventId}/attachments/{attachmentId}/download` | 200、400:`StringApiResponse`、401、404、409:`StringApiResponse`、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/events/{id}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/events/{id}` | 200:`EventResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/events/{id}/restore` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/export-ics` | 200、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/habits` | 200:`HabitRoutineDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/habits` | 201:`HabitRoutineDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/habits/{id}` | 200:`DeletedHabitResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/habits/{id}` | 200:`HabitRoutineDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/habits/{id}` | 200:`HabitRoutineDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/habits/{id}/archive` | 200:`HabitRoutineDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/habits/{id}/occurrences` | 201:`HabitOccurrenceDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/import-ics` | 200:`ImportReportApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/layers` | 200:`CalendarLayerResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/outlook/calendars` | 200:`OutlookCalendarBindingResponseIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/calendars/discover` | 200:`OutlookCalendarBindingResponseIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/outlook/calendars/selection` | 200:`OutlookCalendarBindingResponseIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/check` | 200:`OutlookSettingsResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/device-code` | 200:`OutlookAuthorizationSessionResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/device-code/poll` | 200:`OutlookAuthorizationSessionResponseApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/device-code/{sessionId}/cancel` | 200:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/disconnect` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/events/writeback` | 200:`OutlookWriteResultApiResponse`、400:`StringApiResponse`、401、409:`OutlookWriteResultApiResponse`、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/outlook/local-data` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/outlook/local-data/preview` | 200:`OutlookLocalDataPreviewApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/outlook/settings` | 200:`OutlookSettingsResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/outlook/settings` | 200:`OutlookSettingsResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/sync` | 200:`OutlookSyncBatchResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/outlook/sync/batches` | 200:`OutlookSyncBatchesResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/outlook/sync/{batchId}/cancel` | 200:`StringApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/calendar/projects` | 200:`DomainProjectDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/projects` | 201:`DomainProjectDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/recycle-bin` | 200:`CalendarRecycleBinItemPagedResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/recycle-bin/{type}/{id}/restore` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/recycle-bin/{type}/{id}/restore-preview` | 200:`CalendarRestorePreviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/reminders` | 200:`ReminderResponseIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reminders` | 201:`ReminderResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/reminders/delivery-log` | 200:`ReminderDeliveryDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reminders/{id}/actions/{action}` | 200:`ReminderActionResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reminders/{id}/dismiss` | 200:`ReminderResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reminders/{id}/snooze` | 200:`ReminderResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/reports` | 200:`ReportArtifactDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reports/generate` | 201:`ReportArtifactDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reports/suggestions/{id}/request-action` | 200:`OperationConfirmationDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/reports/{id}` | 200:`ReportArtifactDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/reports/{id}/archive` | 200:`ReportArtifactDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/schedule` | 200:`ScheduleSolutionListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/task-books` | 200:`TaskBookDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/task-books` | 201:`TaskBookDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/tasks` | 200:`TaskResponsePagedResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks` | 201:`TaskResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/batch-delete` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/batch-update` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/tasks/{id}` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/tasks/{id}` | 200:`TaskResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/{id}/checklist` | 201:`TaskChecklistItemDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/tasks/{id}/checklist/{itemId}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/calendar/tasks/{id}/checklist/{itemId}` | 200:`TaskChecklistItemDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/{id}/move` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/{id}/plan` | 200:`TaskResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/{id}/restore` | 200:`CalendarOperationResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/calendar/tasks/{id}/segments` | 200:`TaskExecutionSegmentResponseIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/calendar/tasks/{id}/segments` | 201:`TaskExecutionSegmentResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/calendar/tasks/{taskId}/segments/{segmentId}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/modules/Pim.Module.Files/FilesModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| POST | `/api/v1/files/folders` | 200:`FileItemDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items` | 200:`FileListResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/files/items/upload` | 200:`FileItemDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/upload-session` | 200:`UploadSessionDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/upload-session/complete` | 200:`FileItemDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| DELETE | `/api/v1/files/items/{id}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}` | 200:`FileItemDtoApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/content` | 302、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/download` | 302、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/download-url` | 200:`OneDriveLinkDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/extracted-text` | 200:`OneDriveTextDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/{id}/move` | 200:`FileItemDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/open-link` | 200:`FileOpenLinkDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/preview-url` | 200:`OneDriveLinkDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/{id}/rename` | 200:`FileItemDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/{id}/restore` | 200:`OneDriveWriteResultDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/{id}/share` | 200:`FileShareDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/shares` | 200:`FileShareDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| DELETE | `/api/v1/files/items/{id}/shares/{permissionId}` | 200:`BooleanApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/snapshots` | 200:`FileTextSnapshotDtoIReadOnlyListApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/items/{id}/snapshots/{snapshotId}/restore` | 200:`BooleanApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/text` | 200:`OneDriveTextDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| PUT | `/api/v1/files/items/{id}/text` | 200:`BooleanApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/items/{id}/thumbnail` | 302、400:`StringApiResponse`、401、403、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/providers` | 200:`FileProviderDtoIReadOnlyListApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/files/providers/onedrive` | 200:`OneDriveBindingStartDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| DELETE | `/api/v1/files/providers/{id}` | 200:`BooleanApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| GET | `/api/v1/files/providers/{id}/binding-status` | 200:`OneDriveBindingStatusDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse` |
| POST | `/api/v1/files/providers/{id}/sync` | 200:`OneDriveSyncStartedDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse`、502:`StringApiResponse`、503:`StringApiResponse` |
| GET | `/api/v1/files/providers/{id}/sync-status` | 200:`OneDriveSyncStatusDtoApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/files/search` | 200:`FileSearchResultDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/files/shares` | 200:`FileShareDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/files/suggestions` | 200:`FileSuggestionDtoIReadOnlyListApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/files/suggestions/{id}/accept` | 200:`FileSuggestionDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/files/suggestions/{id}/dismiss` | 200:`FileSuggestionDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |

### src/modules/Pim.Module.Mcp/McpModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/mcp/activity` | 200:`McpActivityEntryIReadOnlyListApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mcp/catalog` | 200:`McpCatalogDtoApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mcp/clients` | 200:`McpClientDtoListApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mcp/clients` | 201:`McpClientCreateResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/mcp/clients/{id}` | 204、401、403、404:`StringApiResponse`、500:`StringApiResponse` |
| PUT | `/api/v1/mcp/clients/{id}` | 200:`McpClientDtoApiResponse`、400:`StringApiResponse`、401、403、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/mcp/clients/{id}/revoke` | 200:`McpClientDtoApiResponse`、401、403、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/mcp/verify` | 200:`McpVerifyResultApiResponse`、400:`StringApiResponse`、401:`StringApiResponse`、403:`StringApiResponse`、429:`StringApiResponse`、500:`StringApiResponse` |

### src/modules/Pim.Module.Mcp/Services/McpServerBootstrap.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| DELETE | `/mcp` | 200 |
| GET | `/mcp` | 200 |
| PATCH | `/mcp` | 200 |

### src/modules/Pim.Module.Mobile/MobileModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/mobile/analytics/charts` | 200:`MobileAnalyticsChartDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/goals` | 200:`MobileUsageGoalDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/analytics/goals` | 200:`MobileUsageGoalDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/mobile/analytics/goals/{goalId}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/heatmap` | 200:`MobileHeatmapBucketDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/overview` | 200:`MobileAnalyticsOverviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/sessions/{sessionId}/events` | 200:`MobileSessionEventDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/timeline-blocks` | 200:`MobileTimelineBlockPageDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/analytics/timeline-blocks/{blockId}/sessions` | 200:`MobileTimelineBlockSessionDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/apps/catalog-overrides` | 200:`MobileAppCatalogOverrideDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/mobile/apps/catalog-overrides` | 200:`MobileAppCatalogOverrideDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/mobile/apps/catalog-overrides/{packageName}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/mobile/apps/catalog-overrides/{packageName}` | 200:`MobileAppCatalogOverrideDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/apps/category-rules` | 200:`MobileAppCategoryRuleDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/apps/category-rules` | 200:`MobileAppCategoryRuleDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/mobile/apps/category-rules/{ruleId}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/mobile/apps/category-rules/{ruleId}` | 200:`MobileAppCategoryRuleDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/apps/missing-metadata` | 200:`MobileMissingAppMetadataResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices` | 200:`MobileDeviceDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/manage` | 200:`DeviceListDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/devices/merge` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/devices/merge/preview` | 200:`DeviceMergePreviewDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/devices/register` | 200:`MobileDeviceDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/mobile/devices/{deviceId}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/delete-preview` | 200:`DeviceDeletePreviewDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/detail` | 200:`DeviceDetailDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/dropped-reasons` | 200:`MobileDroppedReasonResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/export` | 200:`string`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/liveness` | 200:`MobileDeviceLivenessDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/mobile/devices/{deviceId}/liveness/events` | 200:`MobileLivenessEventPageDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/devices/{deviceId}/rename` | 200:`DeviceDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/forensics/events` | 200:`MobileForensicsIngestResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/liveness/overview` | 200:`MobileLivenessOverviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/frequent-places` | 200:`MobileFrequentPlacesResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/movement-stats` | 200:`MobileMovementStatsResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/overview` | 200:`MobileLocationAnalyticsOverviewResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/segments/{segmentId}` | 200:`MobileLocationSegmentDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/segments/{segmentId}/points` | 200:`MobileLocationSegmentPointPageDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/analytics/tracks` | 200:`MobileLocationTrackDtoIReadOnlyListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/location/history` | 200:`MobileLocationHistoryResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/location/points` | 200:`MobileLocationPointDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/location/points/batch` | 200:`MobileLocationPointsUploadResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/quality` | 200:`MobileQualityResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/summary` | 200:`MobileUsageSummaryResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/sync/gaps` | 200:`MobileGapResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/mobile/timeline` | 200:`MobileTimelineResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/mobile/usage/events` | 200:`MobileUsageIngestResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/modules/Pim.Module.PcTracker/PcTrackerModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/pc/activity-analysis` | 200:`PcActivityAnalysisResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/aggregation/app-usage` | 200:`PcAppUsageResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/aggregation/category-distribution` | 200:`PcCategoryDistributionResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/aggregation/focus-blocks` | 200:`PcFocusBlocksResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/aggregation/keystats` | 200:`PcKeystatsRangeResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/aggregation/late-night` | 200:`PcLateNightResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-knowledge/apps` | 200:`AppKnowledgeAppDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-knowledge/apps/{appId}/contexts` | 200:`AppKnowledgeContextDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-knowledge/contexts` | 200:`AppKnowledgeContextDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/pc/app-knowledge/contexts/{id}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-knowledge/suggestions/{id}/apply` | 200:`AppKnowledgeSuggestionApplyDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-knowledge/suggestions/{id}/preview` | 200:`AppKnowledgeSuggestionPreviewDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-signatures` | 200:`AppSignatureDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-signatures` | 200:`AppSignatureDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-signatures/count` | 200:`Int32ApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-signatures/export` | 200:`AppSignatureDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-signatures/import` | 200:`AppSignatureImportResultDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-signatures/lookup` | 200:`AppSignatureDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/app-signatures/lookup/{processName}` | 200:`AppSignatureDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| DELETE | `/api/v1/pc/app-signatures/{id}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/aw/timeline` | 200:`TimelineItemListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/aw/upload` | 200:`Int32ApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/aw/upload-complete` | 200:`Int32ApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/browser-tt/daily` | 200:`SiteDailyRowDtoListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/browser-tt/import` | 200:`SiteImportResultDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/browser-tt/summary` | 200:`SiteSummaryDtoApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/browser-tt/timeline` | 200:`SiteTimelineRowDtoListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/browser-tt/upload` | 200:`Int32ApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/categories` | 200:`AppCategoryRuleListApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/categories` | 200:`CategoryTreeNodeApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/app-category-rules` | 200:`AppCategoryRuleApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/categories/dictionary` | 200:`CategoryDictionaryItemDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| PUT | `/api/v1/pc/categories/reorder` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/categories/seed` | 200:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/categories/tree` | 200:`CategoryTreeNodeListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/pc/categories/{id}` | 200:`StringApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/label` | 200:`ActivityLabelingResponseApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/project-tags/recent` | 200:`StringListApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/queue` | 200:`ActivityLabelingQueueResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/recompute` | 200:`ActivityClassificationRecomputeDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/rules` | 200:`ActivityClassificationRuleDtoListApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/rules` | 200:`ActivityClassificationRuleDtoApiResponse`、400:`StringApiResponse`、401、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/rules/apply` | 200:`ActivityClassificationPreviewDtoApiResponse`、400:`StringApiResponse`、401、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/rules/preview` | 200:`ActivityClassificationPreviewDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/settings` | 200:`ActivityClassificationSettingsDtoApiResponse`、500:`StringApiResponse` |
| PUT | `/api/v1/pc/classification/settings` | 200:`ActivityClassificationSettingsDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/suggestions` | 200:`ActivityClassificationSuggestionDtoListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/suggestions/batch-accept` | 200:`BatchAcceptResultDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/classification/suggestions/v2` | 200:`ActivityClassificationSuggestionV2DtoListApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/suggestions/{id}/accept` | 200:`ActivityClassificationRuleDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/suggestions/{id}/apply` | 200:`ActivityClassificationSuggestionApplyDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/suggestions/{id}/preview` | 200:`ActivityClassificationSuggestionPreviewDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/classification/suggestions/{id}/reject` | 200:`StringApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、409:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/detail` | 200:`TypedDetailQueryResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/heatmap/grid` | 200:`HeatmapGridResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/keystats/range` | 200:`KeystatsSummaryListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/keystats/samples` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/pc/keystats/upload` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/productivity/dashboard` | 200:`ProductivityDashboardDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/productivity/goals` | 200:`ProductivityGoalDtoApiResponse`、500:`StringApiResponse` |
| PUT | `/api/v1/pc/productivity/goals` | 200:`ProductivityGoalDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/productivity/range` | 200:`DailyProductivityDtoListApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/quality` | 200:`PcQualityResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/summary` | 200:`PcSummaryResponseApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/timeline/v2` | 200:`TimelineV2ItemListApiResponse`、400:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/pc/tracker/health` | 200:`TrackerHealthEntityApiResponse`、400:`StringApiResponse`、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/tracker/health` | 200:`StringApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| GET | `/api/v1/pc/tracker/health/latest` | 200:`TrackerHealthEntityApiResponse`、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/pc/tracker/upload` | 200:`Int32ApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |

### src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs

| 方法 | 路径 | 响应（状态码:schema） |
|---|---|---|
| GET | `/api/v1/quick-notes` | 200:`QuickNoteListItemDtoPagedResultApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/quick-notes` | 201:`QuickNoteDetailDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| POST | `/api/v1/quick-notes/attachments` | 200:`QuickNoteAttachmentUploadDtoApiResponse`、400:`StringApiResponse`、401、500:`StringApiResponse` |
| DELETE | `/api/v1/quick-notes/attachments/{id}` | 200:`StringApiResponse`、401、403:`StringApiResponse`、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/quick-notes/attachments/{id}/download` | 200、302、400:`StringApiResponse`、401、403:`StringApiResponse`、404:`StringApiResponse`、500:`StringApiResponse` |
| DELETE | `/api/v1/quick-notes/{id}` | 200:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| GET | `/api/v1/quick-notes/{id}` | 200:`QuickNoteDetailDtoApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| PUT | `/api/v1/quick-notes/{id}` | 200:`QuickNoteDetailDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/quick-notes/{id}/archive` | 200:`QuickNoteDetailDtoApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/quick-notes/{id}/process` | 200:`QuickNoteDetailDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |
| POST | `/api/v1/quick-notes/{id}/restore` | 200:`QuickNoteDetailDtoApiResponse`、400:`StringApiResponse`、401、404:`StringApiResponse`、500:`StringApiResponse` |

## 约定 / Conventions（全局，不在表中重复）

1. **成功响应**：包络 `ApiResponse<T>`（`{code, message, data, timestamp}`，`Pim.Core/Common/ApiResponse.cs`）；201=Created、204=NoContent、302=Redirect、文件响应标注 content-type 无 schema。
2. **错误包络**：`ApiResponse<string>`（data=null）。来源两类：
   - handler 显式返回（如 `Results.BadRequest(ApiResponse<string>.Error(...))`）；
   - `ExceptionMiddleware`（`src/Pim.Api/Middleware/ExceptionMiddleware.cs`）统一转换：DomainException 按错误码映射（40401/4004/4006/5104/5300/5304/5305→404，40101→401，40301/40302/40303→403，42901→429，50301→503，**其余一律 400**），OneDriveGraphException→404/429/502，BadHttpRequestException→400，其余异常→500。
3. **401/403（空体）**：JwtBearer 挑战/授权失败不写响应体，故 401、角色 403 标注为无 content。
4. **500**：所有 operation 都可能因未处理异常经中间件返回 500 包络，全量标注。
5. **/api/v1/ops/**（非 JWT）：由 `OpsKeyMiddleware`/`OpsRateLimitMiddleware` 保护，401/403/429/503 的响应体是 `{code, message}` 裸 JSON（`WriteAsJsonAsync`，无 data/timestamp 字段，与 ApiResponse 形状不同），本批次标注为无 content，形状在此说明。

## 无法静态表达 / 需人工确认（保留在分母，未硬套类型）

| 端点 | 现象与原因 |
|---|---|
| `GET/DELETE/PATCH /mcp` | MCP Streamable HTTP 协议端点（`McpServerBootstrap`）：GET=SSE 事件流、POST=JSON-RPC；session-less 兜底 400 `{code:40001}` 的注解会被 `ResolveConflictingActions` 丢弃（首个 ApiDescription 属于 SDK 的 MapMcp）。响应由 MCP 协议决定，不在应用层契约表达。 |
| `GET /api/v1/calendar/events`、`GET /api/v1/calendar/tasks` | **双形态 200**：无分页/过滤参数时返回 `List<…>`（兼容旧 APK），带参数时返回 `PagedResult<…>`。单响应码只能承载一个 schema，契约标注规范形态 PagedResult（代码注释 PIM-024），List 形态在此记录。 |
| `POST /api/v1/files/providers/{id}/sync` | **双形态 200**：Hangfire 可用 → `OneDriveSyncStartedDto`（常态）；后台设施不可用 → 退回同步执行返回 `OneDriveSyncResultDto`。契约标注常态形态。 |
| `GET /api/v1/files/items/{id}/content|thumbnail`、`GET /api/v1/files/items/{id}/download`、`GET /api/v1/calendar/events/{eventId}/attachments/{attachmentId}/download`、`GET /api/v1/quick-notes/attachments/{id}/download` | 302 重定向（直链）或代理文件下载（content-type 由文件/Graph 侧决定），标注 302 + 通用二进制 content-type。 |
| `GET /api/v1/ops/db/query`、`GET /api/v1/ops/logs/tail|query` | **206 Partial Content** 是真实成功码（结果被截断时），与 200 同 schema，均标注。 |
| `OpsDbQueryResult.rows` | 行数据为 `Dictionary<string, object?>`（数据库列值类型运行时决定），schema 为 free-form 对象数组，字段类型无法静态声明。 |
| `GET /api/v1/pc/*`（readGroup） | ~~当前无鉴权~~ **已修复**：readGroup 已补 `RequireAuthorization`（PR #412），security 现为 `bearerAuth`。 |

## 错误码与代码的对应依据（抽样可复现）

- 全局映射：`src/Pim.Api/Middleware/ExceptionMiddleware.cs`（ResolveDomainStatusCode）。
- 各模块 DomainException 抛出点与 OneDriveGraphException 传播路径：见 PR 描述附录（Calendar 02xxx 全系→400；Files 5xxx 404/400 + 40303→403 + Graph 404/429/502；Mobile 04xxx/62xx→400；PcTracker 服务层无 DomainException；QuickNotes 400x→404/400、40301→403；McpClientService 40401→404、40301→403；OpsDb/OpsLogs 40002/40003→400、40401→404、40302→403、50301→503）。

## 批次 2：请求体字段级约束 / Batch 2: field-level constraints

- **required[]**：请求 DTO 树内 540 个属性中 271 个标注 required（批次 2 之前全契约仅 2 处）。来源（全部代码事实）：
  - C# 可空性（record 位置参数）：非空引用类型与非空值类型（无默认值）→ required（`NullabilityRequiredSchemaFilter`，因 Swashbuckle 6.9 的 `SupportNonNullableReferenceTypes` 对 record 属性不生效）；
  - DTO 上既有 DataAnnotations 重定向到 `[property:]` 后渲染（`[Required]`/`[MaxLength]`/`[MinLength]`/`[EmailAddress]`/`[Range]`，共 4 个文件 92 行：CalendarDtos 39、BrowserSiteDtos 44、AuthDtos 7、AdminDtos 2）；
  - 可空属性（`string?`/`Guid?`）与带默认值的参数一律不标。
- **enum（仅限服务端严格校验的取值集合，每条有代码依据）**：
  | 字段 | 取值 | 服务端校验依据 |
  |---|---|---|
  | `UpdateUserRoleRequest.role` | `admin`, `user` | AdminUserService.ChangeRoleAsync（非法值 → 40041） |
  | `RestoreQuickNoteRequest.status`、`UpdateQuickNoteRequest.status` | `inbox`, `processed`, `archived` | QuickNoteService.ValidateStatus → QuickNoteStatuses.IsValid（非法值 → 4003） |
  | `DataCenterBatchOperationRequest.action` | `archive` | DataCenterGovernanceService（其余 → 02049） |
- **格式约束**：`maxLength/minLength`（来自既有 MaxLength/MinLength）、`format: email`（EmailAddress）、`format: date-time/int32/int64/uuid/binary`（来自字段类型）。
- **如实不标（避免臆造）**：
  - `CreateShareRequest.permissionType`：代码里声明了 `SupportedPermissionTypes = ["view","edit"]` 但**从未使用**（不校验）→ 不标 enum，记录为设计备注；
  - 其余字符串字段（calendar kind、events scope、pc mode 等）服务端校验宽松或不校验 → 不标 enum。
- 运行时行为不变：以上全部是 OpenAPI 文档元数据（SwaggerGen 选项与 SchemaFilter）+ attribute target 重定向（DataAnnotations 在 Minimal API 中本就不参与自动校验）。

## 批次 3：鉴权定义 / Batch 3: security definitions

- **securitySchemes**（`components.securitySchemes`，此前为 0）：
  | scheme | 类型 | 依据 |
  |---|---|---|
  | `bearerAuth` | http bearer (JWT) | JwtBearer 认证（AuthExtensions.AddPimAuth） |
  | `opsKey` | apiKey in header `X-PIM-Ops-Key` | OpsKeyMiddleware（src/Pim.Api/Infrastructure/Ops/OpsKeyMiddleware.cs，仅拦 /api/v1/ops 前缀；未配置 key → 503，不匹配 → 401，体为 `{code,message}` 裸 JSON） |
- **per-operation security**：273/313 标注 —— 266 个 `bearerAuth`（RequireAuthorization 组/端点 + /mcp bearer guard）、7 个 `opsKey`（/api/v1/ops/*）。
- **照实不标 security 的 40 个**（现状即匿名）：
  - `/api/v1/auth/login|register|refresh`（登录入口）、`/api/v1/mcp/verify`（用 mcp_clients token 自证）；
  - `/health`、`/api/version`、`/api/v1/tiles/*`、`/api/client/shell/latest`、`/api/v1/client/shell/latest`、`/api/{path}` 兜底；
  - ~~`/api/v1/pc` 的 readGroup 全部 GET 当前无鉴权~~ **已修复**（PR #412 补 `RequireAuthorization`，契约同步为 `bearerAuth`）。
- 注意事项（写入 scheme 描述与现状说明）：持 `mcp_tool` 作用域 JWT 的调用方还会经 `McpScopedTokenMiddleware` 做工具级权限校验（403，`{code:40302,...}` 包络），这是 bearer 之上的附加层，不改变 security 要求。

## 修复记录：/api/v1/pc/categories 路由歧义（2026-10）

`GET`/`POST /api/v1/pc/categories` 自 2026-07 起因同方法同路径双注册（裸路径 vs 组根 "/"）在路由匹配阶段抛 `AmbiguousMatchException`，一律 500。处置（经裁决：树形留任）：

| 操作 | 修复前 | 修复后 |
|---|---|---|
| `GET /api/v1/pc/categories` | 歧义 500 | **扁平规则列表**（`AppCategoryRule`，MCP `get_pc_categories` 与契约原义，不变） |
| `GET /api/v1/pc/categories/` | 歧义 500 | 同上（尾斜杠同路由） |
| 冗余的树形 GET（组根 "/"） | 歧义源之一 | **删除**（`/tree` 是唯一树读入口，web 与 MCP 都走它） |
| `POST /api/v1/pc/categories` | 歧义 500 | **树形分类保存**（`CategorySaveRequest`→`CategoryTreeNode`，web 分类页语义） |
| 扁平规则保存（原 POST /categories） | 歧义源之一 | **迁移**到 `POST /api/v1/pc/app-category-rules`（`SaveCategoryRequest`→`AppCategoryRule`）；MCP `create_category` 已同步重指向（工具名/参数/返回不变） |
| `DELETE /api/v1/pc/categories/{id}` | 正常（{id:guid} 优先级胜出，树形删除生效） | 不变；注意扁平规则删除（writeGroup 版）历史上就不可达，见下方同类问题 |

**同类问题（已报告，未在本次处理）**：`DELETE /api/v1/pc/categories/{id}` 存在双注册（writeGroup `{id}` vs catWrite `{id:guid}`），路由优先级使树形删除对 guid 静默胜出、扁平规则删除不可达（非 guid 输入落扁平版 → 400）。行为与 MCP `delete_category` 的实际语义（树形删除）一致，故维持现状；如需扁平规则删除能力，应随 `/app-category-rules` 资源一并补 `DELETE`。全仓扫描（方法+归一化路径）未发现其他同优先级重复注册。
