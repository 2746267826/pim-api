# 业务错误码表（Business error codes）

> 本文是 `contract/openapi.json` 的**补充契约**：OpenAPI 只能表达 HTTP 状态码，表达不了
> `ApiResponse.code` 的业务语义。客户端（Web / Android / Windows / 外部开发者）按这里的码做分支，
> 前端展示 `message`、按「客户端建议动作」处理。
>
> - 生成方式：`python3 scripts/ci/extract-business-error-codes.py`（从 `pim-api` 源码的
>   `DomainException(<码>, "<消息>")` / `ApiResponse<...>.Error(<码>, "<消息>")` 自动采集）。
> - 校验方式：`python3 scripts/ci/audit-contract-gaps.py` 会要求「契约文本里出现的码」都在本表内；
>   `python3 scripts/ci/extract-business-error-codes.py --check` 判断本表是否落后于源码。
> - 约定：
>   1. `code = 0` 表示成功，其余均为业务失败；HTTP 状态由 `ExceptionMiddleware.ResolveDomainStatusCode` 决定；
>   2. 客户端**不要**按 message 字符串判断错误类型，只按 code；
>   3. 新增业务码必须同时出现在契约相关端点的错误响应说明里，并在 PR 正文声明影响面。
>

<!-- BEGIN GENERATED: 由 scripts/ci/extract-business-error-codes.py 生成，请勿手工编辑 -->

| 码 | HTTP | 模块 | 含义（源码消息样例，最多 3 条） | 客户端建议动作 |
| --- | --- | --- | --- | --- |
| 1001 | 400 | ExceptionMiddleware | 内部服务器错误 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 1002 | 400 | Calendar, EndpointStatusService, Files, OperationsEndpoints, QuickNotes | 未登录；Login required | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 1003 | 400 | AdminEndpoints, AuthEndpoints | 用户名已存在；用户名已存在或邮箱已存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 1004 | 400 | AdminEndpoints, AuthEndpoints | 邮箱已存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2001 | 400 | Calendar | 日程不存在；系列主事件不存在；Event does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2002 | 400 | Calendar | 日历不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2003 | 400 | Calendar | 日历不存在；任务本不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2004 | 400 | Calendar | 任务不存在；日程不存在；Task does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2005 | 400 | Calendar | Outlook is not connected.；Microsoft Client ID is not configured.；One or more calendar bindings do not belong to the current user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2006 | 400 | Calendar | Confirmation does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2007 | 400 | Calendar | Operation has not been confirmed. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2008 | 400 | Calendar | 已有同步任务正在运行（running），请稍后再试。；Default calendar was not found. Create a calendar before syncing Outlook. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2009 | 400 | Calendar | RecurrenceId 格式无效；RecurrenceId 与查询参数不一致；Outlook 连接需要重新授权。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2010 | 400 | Calendar | 结束时间必须晚于开始时间 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2011 | 400 | Calendar | 预计时长至少为 1 分钟 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2013 | 400 | Calendar | 描述中不允许包含可执行的 HTML 标签（script、iframe、object、embed）；描述中不允许包含事件处理属性（on*） | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2020 | 400 | Calendar | 恢复存在冲突 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2021 | 400 | Calendar | 不支持的回收站类型 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2022 | 400 | Calendar | 仅日程和任务支持恢复为副本。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2023 | 400 | Calendar | 请先恢复所属本。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2024 | 400 | Calendar | Segment end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2025 | 400 | Calendar | Task execution segment does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2027 | 400 | Calendar | Layer end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2028 | 400 | Calendar | Project does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2029 | 400 | Calendar | Habit occurrence end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2030 | 400 | Calendar | Habit does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2031 | 400 | Calendar | Availability end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2032 | 400 | Calendar | AI placeholder end must be after start | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2033 | 400 | Calendar | AI placeholder does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2035 | 400 | Calendar | Value must be 1-40 characters | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2036 | 400 | Calendar | Outlook token service is not configured. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2037 | 400 | Calendar | Outlook client id is required before polling device code.；Checklist item does not exist | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2038 | 400 | Calendar | Event is not linked to an Outlook event. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2039 | 400 | Calendar | Sync conflict does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2040 | 400 | Calendar | Unsupported conflict action. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2041 | 400 | Calendar | Confirmation belongs to a different user.；Reminder does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2042 | 400 | Calendar | Confirmation does not match this stop-sync operation. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2043 | 400 | Calendar | Strict confirmation metadata is required for stop-sync execution.；RelatedObjectId must be a valid GUID.；Report suggestion does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2044 | 400 | Calendar | ScheduledAt is required.；Report does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2045 | 400 | Calendar | Unsupported report kind. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2046 | 400 | Calendar | Batch confirmation does not exist.；项目报告必须指定 projectId。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2047 | 400 | Calendar | Batch operation requires at least one object.；项目不存在。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2048 | 400 | Calendar | Batch operation requires valid object references.；项目下没有任何任务，无法推导项目周期。 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2051 | 400 | Calendar | Batch confirmation belongs to a different user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2052 | 400 | Calendar | Batch confirmation must be strictly confirmed before execution. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2053 | 400 | Calendar | Batch operation payload is empty. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2054 | 400 | Calendar | Batch operation payload is invalid. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2055 | 400 | Calendar | Batch confirmation is missing strict confirmation metadata. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2056 | 400 | AuditVersionService | Audit version does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 02057 | 400 | Calendar | 任务状态 '{status}' 无效；允许值：{string.Join( | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2058 | 400 | Calendar | 该任务已有正在进行的计时，请先暂停或结束 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 2059 | 400 | Calendar | 正在计时的执行段不能删除，请先暂停或结束；该任务没有正在进行的计时 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3001 | 400 | OperationConfirmationService, OperationsEndpoints | Confirmation record does not exist. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3002 | 400 | OperationConfirmationService | Only confirmed operations can be executed. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3003 | 400 | OperationConfirmationService | Confirmation record is not pending. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3004 | 400 | OperationConfirmationService | Confirmation record has expired. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3005 | 400 | OperationConfirmationService, OperationsEndpoints | Confirmation record is not assigned to the current user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3009 | 400 | OperationConfirmationService | Strict confirmation is required for this operation. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 3010 | 400 | DaemonHeartbeatService, OperationConfirmationService | StatusJson 必须是有效 JSON；Second-level confirmation is required for this operation. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4000 | 400 | Mobile | 别名长度需 1-50 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4001 | 400 | Mobile | 至少需要选择一台源设备；源设备不能包含目标设备 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4002 | 400 | Mobile | 设备正在同步，禁止删除 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4003 | 400 | QuickNotes | 快速记录状态无效 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4004 | 404 | Mobile, QuickNotes | 设备不存在；部分设备不存在或不属于当前用户；快速记录不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 4005 | 400 | QuickNotes | 附件不能绑定到这条快速记录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4006 | 404 | Files, QuickNotes | 附件超过可读取大小上限；附件不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 4007 | 400 | QuickNotes | 附件文件名不能为空 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 4008 | 400 | QuickNotes | 附件大小不能为负数 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5104 | 404 | Files | 文件来源不存在；快照不存在；文件不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
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
| 5322 | 400 | Files | 网盘授权已失效，请到设置页重新连接该账号；文件来源授权已失效，请重新绑定 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5324 | 400 | Files | 需要文件来源类型；需要 Azure 应用注册的 Client ID | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5331 | 400 | Files | 文件超过文本处理上限，请在网盘中操作 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5332 | 400 | Files | 该文件不支持缩略图；文件夹没有可抽取的文本；文件夹没有可下载内容 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5333 | 400 | Files | 网盘暂未返回下载直链，请稍后重试；网盘暂未返回预览地址，请稍后重试；网盘暂未返回网页地址，请稍后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5335 | 400 | Files | 该绑定正在同步中，请稍后再试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5336 | 400 | Files | 文件结构异常，无法抽取文本；文件解压后过大，无法抽取文本；压缩包条目过多，无法抽取文本 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5337 | 400 | Files | 不能移动网盘根目录；不能把文件夹移动到自身；不能把文件夹移动到自己的子目录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5338 | 400 | Files | 新名称不能为空且不能包含 /；不能重命名网盘根目录 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5339 | 400 | Files | 该文件不在回收站中 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5340 | 400 | Files | 文件已在网盘端删除，无法恢复 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5342 | 400 | Files | 存在多个网盘账号，请指定 providerId 后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5343 | 400 | Files | 尚未绑定网盘账号 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5344 | 400 | Files | Google Drive 的链接分享不支持有效期，请选择「不过期」；OneDrive 账号仍可选 7/30 天 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 5391 | 400 | Files | 同步任务入队失败，请稍后重试 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 6200 | 400 | Mobile | Mobile endpoints require an authenticated user. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 6202 | 400 | Mobile | Mobile location accuracy is not usable. | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40001 | 400 | Mcp | 客户端名称不能为空 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40002 | 400 | Mcp, OpsDbService, OpsLogsService | InvalidTableName；InvalidFileName；InvalidFrom | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40003 | 400 | Mcp, OpsLogsService | Limit must be 1-500；客户端名称已存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40030 | 400 | AuthEndpoints | 账号已停用，请联系管理员 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40040 | 400 | AdminEndpoints | 用户不存在 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40041 | 400 | AdminEndpoints | 无效的角色，仅支持 admin / user | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40042 | 400 | AdminEndpoints | 至少需要保留一名管理员；至少需要保留一名有效管理员 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40043 | 400 | AdminEndpoints | 操作失败 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40044 | 400 | DataReliabilityEndpoints | 未知的尺子编号 | 展示 message；多数是请求形状/参数问题，检查客户端组装 |
| 40101 | 401 | Mcp | missing bearer token | 刷新令牌后重试一次；刷新失败则回登录页 |
| 40301 | 403 | Mcp, QuickNotes | 无权操作该客户端；无权访问该附件 | 无权限：不要重试；提示用户或联系管理员 |
| 40302 | 403 | OpsDbService | RestrictedColumn | 无权限：不要重试；提示用户或联系管理员 |
| 40303 | 403 | Files | 敏感路径受保护，不允许该操作；敏感路径受保护，不允许分享 | 无权限：不要重试；提示用户或联系管理员 |
| 40401 | 404 | Mcp, OpsDbService, OpsLogsService | TableNotFound；LogFileNotFound；客户端不存在 | 目标不存在或已被删除：刷新列表，不要重试同一请求 |
| 42901 | 429 | Mcp | too many attempts | 限流：按 Retry-After 退避后重试 |
| 50301 | 503 | OpsDbService | OpsRoConnectionNotConfigured | 服务不可用/功能被关闭：不要重试，展示配置提示 |

<!-- END GENERATED -->
