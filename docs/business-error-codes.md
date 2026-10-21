# 业务错误码表（Business error codes）

> 本文是 `contract/openapi.json` 的**补充契约**：OpenAPI 只能表达 HTTP 状态码，表达不了
> `ApiResponse.code` 的业务语义。客户端（Web / Android / Windows / 外部开发者）按这里的码做分支，
> 前端展示 `message`、按「客户端建议动作」处理。
>
> - 生成方式：`python3 scripts/ci/extract-business-error-codes.py`。采集范围覆盖
>   `DomainException(...)`、`ApiResponse<...>.Error(...)`、`WriteDomainLikeErrorAsync(context, status, code, ...)`
>   与名字带 `Code` 的 `const int`（含 `$"..."` 插值、变量消息、`类名.常量` 写法）；消息来自第二个实参或配对的
>   `const string ...Message`，取不到时如实写「消息为变量或表达式，见源码」。
> - 校验方式：`python3 scripts/ci/audit-contract-gaps.py` 会要求「契约文本里出现的码」都在本表内；
>   `python3 scripts/ci/extract-business-error-codes.py --check` 判断本表是否落后于源码（并做反向完整性核对：
>   源码里出现、抽取器没归因的码会让它失败，而不是被静默漏掉）。
> - 约定：
>   1. `code = 0` 表示成功，其余均为业务失败；HTTP 状态**优先取调用点显式状态**（`Results.Conflict/NotFound`、
>      `Results.Json(..., statusCode:)`、`Response.StatusCode`、helper 的状态实参），否则用
>      `ExceptionMiddleware.ResolveDomainStatusCode` 的映射（默认 400）。同一个码在不同路径上状态不同时，
>      HTTP 列会写成 `400 / 409` 并给出按状态处理的建议；
>   2. 客户端**不要**按 message 字符串判断错误类型，只按 code；**HTTP 状态也不要用来推断业务码**；
>   3. 表中「码」列保留源码写法（如 `02057`），客户端收到的是整数 `2057`，前导零只是源码可读性；
>   4. 新增业务码必须同时出现在契约相关端点的错误响应说明里，并在 PR 正文声明影响面。
>

<!-- BEGIN GENERATED: 由 scripts/ci/extract-business-error-codes.py 生成，请勿手工编辑 -->

| 码 | HTTP | 模块 | 含义（源码消息样例，最多 3 条） | 客户端建议动作 |
| --- | --- | --- | --- | --- |
| 01001 | 500 | ExceptionMiddleware | 内部服务器错误 | 服务端未预期错误：可重试一次，仍失败则上报 |
| 01002 / 1002 | 400 | Calendar, EndpointStatusService, Files, OperationsEndpoints, QuickNotes | 未登录；Login required | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 01003 | 409 | AdminEndpoints, AuthEndpoints | 用户名已存在；用户名已存在或邮箱已存在 | 状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作 |
| 01004 | 409 | AdminEndpoints, AuthEndpoints | 邮箱已存在 | 状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作 |
| 02001 | 400 | Calendar | 日程不存在；系列主事件不存在；Event does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02002 | 400 | Calendar | 日历不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02003 | 400 | Calendar | 日历不存在；任务本不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02004 | 400 | Calendar | 任务不存在；日程不存在；Task does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02005 | 400 | Calendar | Outlook is not connected.；Microsoft Client ID is not configured.；One or more calendar bindings do not belong to the current user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02006 | 400 | Calendar | Confirmation does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02007 | 400 | Calendar | Operation has not been confirmed. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02008 | 400 | Calendar | 已有同步任务正在运行（running），请稍后再试。；Default calendar was not found. Create a calendar before syncing Outlook. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02009 | 400 / 409 | Calendar | RecurrenceId 格式无效；RecurrenceId 与查询参数不一致；Outlook 连接需要重新授权。 | 按具体状态处理：400 展示 message；多数是请求形状/参数问题，检查客户端组装；409 状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作 |
| 02010 | 400 | Calendar | 结束时间必须晚于开始时间 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02011 | 400 | Calendar | 预计时长至少为 1 分钟 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02013 | 400 | Calendar | 描述中不允许包含可执行的 HTML 标签（script、iframe、object、embed）；描述中不允许包含事件处理属性（on*） | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02020 | 400 | Calendar | 恢复存在冲突 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02021 | 400 | Calendar | 不支持的回收站类型 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02022 | 400 | Calendar | 仅日程和任务支持恢复为副本。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02023 | 400 | Calendar | 请先恢复所属本。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02024 | 400 | Calendar | Segment end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02025 | 400 | Calendar | Task execution segment does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02026 | 400 | Calendar | {fieldName} must be 1-40 characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02027 | 400 | Calendar | Layer end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02028 | 400 | Calendar | Project does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02029 | 400 | Calendar | Habit occurrence end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02030 | 400 | Calendar | Habit does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02031 | 400 | Calendar | Availability end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02032 | 400 | Calendar | AI placeholder end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02033 | 400 | Calendar | AI placeholder does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02034 | 400 | Calendar | {fieldName} must be 1-{maxLength} characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02035 | 400 | Calendar | Value must be 1-40 characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02036 | 400 | Calendar | Outlook token service is not configured. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02037 | 400 | Calendar | Outlook client id is required before polling device code.；Checklist item does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02038 | 400 | Calendar | Event is not linked to an Outlook event. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02039 | 400 | Calendar | Sync conflict does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02040 | 400 | Calendar | Unsupported conflict action. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02041 | 400 | Calendar | Confirmation belongs to a different user.；Reminder does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02042 | 400 | Calendar | Confirmation does not match this stop-sync operation.；{fieldName} must be 1-{maxLength} characters. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02043 | 400 | Calendar | Strict confirmation metadata is required for stop-sync execution.；RelatedObjectId must be a valid GUID.；Report suggestion does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02044 | 400 | Calendar | ScheduledAt is required.；Report does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02045 | 400 | Calendar | Unsupported report kind. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02046 | 400 | Calendar | Batch confirmation does not exist.；项目报告必须指定 projectId。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02047 | 400 | Calendar | Batch operation requires at least one object.；项目不存在。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02048 | 400 | Calendar | Batch operation requires valid object references.；项目下没有任何任务，无法推导项目周期。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02049 | 400 | Calendar | Unsupported data center batch action '{request.Action}'. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02050 | 400 | Calendar | Unsupported data center archive object type '{obj.ObjectType}'. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02051 | 400 | Calendar | Batch confirmation belongs to a different user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02052 | 400 | Calendar | Batch confirmation must be strictly confirmed before execution. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02053 | 400 | Calendar | Batch operation payload is empty. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02054 | 400 | Calendar | Batch operation payload is invalid. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02055 | 400 | Calendar | Batch confirmation is missing strict confirmation metadata. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02056 | 400 | AuditVersionService | Audit version does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02057 | 400 | Calendar | 任务状态 '{status}' 无效；允许值：{string.Join(", ", TaskStatuses.All)} | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02058 | 400 | Calendar | 该任务已有正在进行的计时，请先暂停或结束 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02059 | 400 | Calendar | 正在计时的执行段不能删除，请先暂停或结束；该任务没有正在进行的计时 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02060 | 400 | Calendar | 任务状态为 {task.Status}，不能开始计时 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3001 | 400 | OperationConfirmationService, OperationsEndpoints | Confirmation record does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3002 | 400 | OperationConfirmationService | Only confirmed operations can be executed. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3003 | 400 | OperationConfirmationService | Confirmation record is not pending. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3004 | 400 | OperationConfirmationService | Confirmation record has expired. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3005 | 400 | OperationConfirmationService, OperationsEndpoints | Confirmation record is not assigned to the current user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3009 | 400 | OperationConfirmationService | Strict confirmation is required for this operation. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3010 | 400 | DaemonHeartbeatService, OperationConfirmationService | StatusJson 必须是有效 JSON；Second-level confirmation is required for this operation. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 04000 | 400 | Mobile | 别名长度需 1-50 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 04001 | 400 | Mobile | 至少需要选择一台源设备；源设备不能包含目标设备 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 04002 | 400 | Mobile | 设备正在同步，禁止删除 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4003 | 400 | QuickNotes | 快速记录状态无效 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 04004 / 4004 | 404 | Mobile, QuickNotes | 设备不存在；部分设备不存在或不属于当前用户；快速记录不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 4005 | 400 | QuickNotes | 附件不能绑定到这条快速记录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4006 | 404 | Files, QuickNotes | 附件超过可读取大小上限；附件不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 4007 | 400 | QuickNotes | 附件文件名不能为空 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4008 | 400 | QuickNotes | 附件大小不能为负数 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5104 | 404 | ExceptionMiddleware, Files | 文件不存在；文件来源不存在；快照不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 5300 | 404 | Files | 文件不存在；需要权限 ID；分享权限只支持 view（可看）或 edit（可编辑） | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 5301 | 400 | Files | 目标路径必须包含文件或文件夹名称；目标路径必须包含文件夹名称 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5304 | 404 | Files | 目标文件夹不存在（未同步或已删除） | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 5305 | 404 | Files | 文件建议不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 5306 | 400 | Files | 需要 multipart 表单数据 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5307 | 400 | Files | 需要文件来源 ID | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5308 | 400 | Files | 需要上传路径；名称不能为空；名称过长（最多 255 个字符） | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5309 | 400 | Files | 需要上传文件；文件名不能为空且不能包含 / | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5320 | 400 | Files | 尚未绑定网盘账号，附件功能不可用；文件来源绑定不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5321 | 400 | Files | 文件来源尚未完成绑定；文件来源未绑定或绑定不完整；文件来源绑定状态已变化，请刷新后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5322 | 400 | ExceptionMiddleware, Files | 网盘授权已失效或该文件权限不足，请到设置页重新连接该账号或检查文件权限；网盘授权已失效，请到设置页重新连接该账号；文件来源授权已失效，请重新绑定 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5323 | 400 | Files | 该文件来源（{provider.Provider}）不受支持；不支持的文件来源类型：{providerKey}；该文件来源（{provider.Provider}）不受支持，请重新选择 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5324 | 400 | ExceptionMiddleware, Files | （消息为变量或表达式，见源码）；需要文件来源类型；需要 Azure 应用注册的 Client ID | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5325 | 400 | ExceptionMiddleware | （消息为变量或表达式，见源码） | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5331 | 400 | ExceptionMiddleware, Files | 文件超过处理上限，请在网盘中操作；附件超过 {FileContentService.MaxSaveBytes / 1024 / 1024}MB，请使用网盘客户端；内容超过 {MaxSaveBytes / 1024 / 1024}MB，请在网盘中编辑 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5332 | 400 | Files | 该文件不支持缩略图；文件夹没有可抽取的文本；文件夹没有可下载内容 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5333 | 400 | Files | 网盘暂未返回下载直链，请稍后重试；网盘暂未返回预览地址，请稍后重试；网盘暂未返回网页地址，请稍后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5334 | 400 | ExceptionMiddleware, Files | 该文件来源（{ex.ProviderKey}）不支持 {ex.Capability}；该文件来源（{session.Account.ProviderKey}）不支持分享 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5335 | 400 | Files | 该绑定正在同步中，请稍后再试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5336 | 400 | Files | 暂不支持读取该 Google 类型（{file.MimeType}）的内容；暂不支持从 {extension} 文件抽取文本；文件结构异常，无法抽取文本 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5337 | 400 | Files | 不能移动网盘根目录；不能把文件夹移动到自身；不能把文件夹移动到自己的子目录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5338 | 400 | Files | 新名称不能为空且不能包含 /；不能重命名网盘根目录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5339 | 400 | Files | 该文件不在回收站中 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5340 | 400 | Files | 文件已在网盘端删除，无法恢复 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5341 | 400 | Files | 读取过于频繁，每分钟最多 {_limit} 次，请稍后再试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5342 | 400 | Files | 存在多个网盘账号，请指定 providerId 后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5343 | 400 | Files | 尚未绑定网盘账号 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5344 | 400 | Files | Google Drive 的链接分享不支持有效期，请选择「不过期」；OneDrive 账号仍可选 7/30 天 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5345 | 400 | ExceptionMiddleware | 网盘空间或配额不足，请清理后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5390 | 502 | ExceptionMiddleware | 网盘服务配置异常，请联系管理员 | 上游（云盘/微软）失败：稍后重试，可展示为三方服务异常 |
| 5391 | 503 | Files | 同步任务入队失败，请稍后重试 | 服务不可用/功能被关闭：不要重试，展示配置提示 |
| 6200 | 400 | Mobile | Mobile endpoints require an authenticated user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 6201 | 400 | Mobile | （消息为变量或表达式，见源码） | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 6202 | 400 | Mobile | Mobile location accuracy is not usable. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 6203 | 400 | Mobile | A location batch must not contain more than {MaxBatchPoints} points. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40000 | 400 | ExceptionMiddleware | 请求参数格式无效: {ex.Message} | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40001 | 400 | Mcp | 客户端名称不能为空 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40002 | 400 | Mcp, OpsDbService, OpsLogsService | InvalidTableName；（消息为变量或表达式，见源码）；InvalidFileName | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40003 | 400 | Mcp, OpsLogsService | Limit must be 1-500；客户端名称已存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40004 | 400 | AuthEndpoints, UserCredentialsValidator | Password must be at least 8 characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40007 | 400 | UserCredentialsValidator | Password must not exceed 100 characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40030 | 403 | AuthEndpoints | 账号已停用，请联系管理员 | 无权限：不要重试；提示用户或联系管理员 |
| 40040 | 404 | AdminEndpoints | 用户不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 40041 | 404 / 409 | AdminEndpoints | 无效的角色，仅支持 admin / user | 按具体状态处理：404 目标不存在或已被删除：刷新列表，不要重试同一请求；409 状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作 |
| 40042 | 404 | AdminEndpoints | 至少需要保留一名管理员；至少需要保留一名有效管理员 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 40043 | 404 / 409 | AdminEndpoints | 操作失败 | 按具体状态处理：404 目标不存在或已被删除：刷新列表，不要重试同一请求；409 状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作 |
| 40044 | 400 | DataReliabilityEndpoints | 未知的尺子编号 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40063 | 401 | AuthEndpoints, UserPasswordService | 当前密码不正确 | 刷新令牌后重试一次；刷新失败则回登录页 |
| 40064 | 400 | AuthEndpoints, UserPasswordService | 新密码不能与当前密码相同 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40065 | 400 / 401 | AuthEndpoints, UserPasswordService | 会话标识无效，请重新登录后再试 | 按具体状态处理：400 展示 message；多数是请求形状/参数问题，检查客户端组装；401 刷新令牌后重试一次；刷新失败则回登录页 |
| 40101 | 401 | Mcp | missing bearer token；（消息为变量或表达式，见源码） | 刷新令牌后重试一次；刷新失败则回登录页 |
| 40301 | 403 | Mcp, QuickNotes | （消息为变量或表达式，见源码）；无权操作该客户端；无权访问该附件 | 无权限：不要重试；提示用户或联系管理员 |
| 40302 | 403 | McpScopedTokenMiddleware, OpsDbService | mcp scope denied: {tool} cannot call {method} {path}；RestrictedColumn | 无权限：不要重试；提示用户或联系管理员 |
| 40303 | 403 | Files | 敏感路径受保护，不允许该操作；敏感路径受保护，不允许分享 | 无权限：不要重试；提示用户或联系管理员 |
| 40304 | 403 | AuthEndpoints, PublicRegistrationPolicy | 公开注册已关闭，请联系管理员创建账号 | 无权限：不要重试；提示用户或联系管理员 |
| 40401 | 404 | Mcp, OpsDbService, OpsLogsService | TableNotFound；LogFileNotFound；客户端不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 42901 | 429 | Mcp | too many attempts | 限流：按 Retry-After 退避后重试 |
| 50301 | 503 | OpsDbService | OpsRoConnectionNotConfigured | 服务不可用/功能被关闭：不要重试，展示配置提示 |

<!-- END GENERATED -->
