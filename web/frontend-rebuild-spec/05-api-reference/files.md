# 文件域接口规格（/api/v1/files，OneDrive）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域路由组整体要求 JWT。

## 域级说明（阅读前必看）

- 路由组 `/api/v1/files` 整体 `RequireAuthorization()`（后端 `src/modules/Pim.Module.Files/FilesModule.cs:86`），本域全部端点要求 JWT；本域没有匿名、Admin、OpsKey 端点。
- 本域 v2 只有 OneDrive 一种提供程序（`provider = "onedrive"`）；Nextcloud 已退役，相关端点见"提供程序绑定"组内标注"遗留/未注册"的条目。
- **分页字段名的实际取值**：`PagedResult<T>` 在后端序列化为 `{ items, page, pageSize, totalCount, totalPages }`（`src/Pim.Core/Common/PagedResult.cs:3-9`，camelCase），上文统一文档头中的 `total` 在本域实际字段名为 `totalCount`。且 `GET /files/items` 的 `PagedResult` 不是 `data` 本身，而是包在 `data.result` 内（`FileListResponse`，`src/modules/Pim.Module.Files/DTOs/FileDtos.cs:149`）；`GET /files/search` 不用 `PagedResult`，而是平铺 `{ items, chunks, totalCount, totalPages }`。
- JSON 序列化为 camelCase（`JsonSerializerDefaults.Web`，`src/Pim.Api/Middleware/ExceptionMiddleware.cs:17`；minimal API 响应同默认）。错误经统一异常中间件返回 `ApiResponse{code, message}` 非 0 code。
- 下文 `FileItemDto` 结构在"目录与条目"组的 `GET /files/items` 处一次性完整展开，其余端点返回 `FileItemDto` 时引用该节。

## 提供程序绑定

### GET /api/v1/files/providers
- 用途：列出当前用户的文件提供程序绑定（v2 恒为 0 或 1 条 OneDrive 绑定）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`FileProviderDto[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 绑定记录 ID |
  | provider | string | 提供程序类型，v2 恒为 `onedrive` |
  | baseUrl | string | Nextcloud 时代遗留字段；OneDrive 绑定流程不写入（保持默认值） |
  | internalBaseUrl | string\|null | 同上遗留字段 |
  | username | string | 同上遗留字段 |
  | status | string | 绑定状态：`pending` / `connected` / `expired` / `denied`（OneDriveBindingService.cs:74,114,145,200） |
  | lastSyncAt | string\|null | 最近同步完成时间 |
  | lastError | string\|null | 最近同步错误信息 |
  | createdAt | string | 创建时间 |
  | updatedAt | string | 更新时间 |
  | clientId | string\|null | Azure 应用注册 Client ID |
  | driveId | string\|null | OneDrive drive ID（连接成功后写入） |
  | accountId | string\|null | 微软账户 ID |
  | accountName | string\|null | 微软账户显示名 |
  | syncStatus | string | 同步状态：`idle` / `syncing` / `error`（默认 `idle`） |
  | syncedItemCount | integer | 已同步条目数（默认 0） |
  | deltaResetAt | string\|null | delta 游标重置时间 |
  | tokenExpiresAt | string\|null | 访问令牌过期时间 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:88-91`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:5-23`；前端 `src/client-web/src/api/files.ts:50,110-112`

### POST /api/v1/files/providers/onedrive
- 用途：发起 OneDrive 设备码授权（OAuth Device Code Flow）起点，创建或复用绑定记录。
- 认证：JWT
- Web 前端使用：是（OneDrive 绑定对话框 OneDriveBindDialog）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | clientId | string | 是 | Azure 应用注册的 Client ID；缺失/空白报 5324 |
- 响应 data：`OneDriveBindingStartDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | providerId | string(uuid) | 绑定记录 ID（后续轮询用） |
  | userCode | string | 设备码流程的用户码（展示给用户输入） |
  | verificationUri | string | 微软验证页地址 |
  | expiresIn | integer | 设备码有效期（秒） |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:93,239-245`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:5-11`；服务 `src/modules/Pim.Module.Files/Services/OneDriveBindingService.cs:51-98`；前端 `src/client-web/src/api/files.ts:52,209-211`
- 备注：每用户仅允许一条 onedrive 绑定；重复发起会复用同一条记录并作废旧凭据/游标（OneDriveBindingService.cs:79-84）。并发双击撞唯一索引时返回 5326。

### GET /api/v1/files/providers/{id}/binding-status
- 用途：查询设备码授权进度；在 pending 期间服务端代为轮询 Graph，完成时写入凭据。
- 认证：JWT
- Web 前端使用：是（OneDrive 绑定对话框 OneDriveBindDialog，5 秒轮询直到 connected）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | providerId |
- Query 参数 / Body：无
- 响应 data：`OneDriveBindingStatusDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | status | string | `pending` / `connected` / `expired` / `denied` |
  | driveId | string\|null | connected 后才有值 |
  | accountId | string\|null | connected 后才有值 |
  | accountName | string\|null | connected 后才有值 |
  | userCode | string\|null | pending 期间返回 |
  | verificationUri | string\|null | pending 期间返回 |
  | deviceCodeExpiresAt | string\|null | 设备码过期时间 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:94,247-253`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:13-25`；服务 `src/modules/Pim.Module.Files/Services/OneDriveBindingService.cs:100-209`；前端 `src/client-web/src/api/files.ts:53,213-215`；轮询间隔 `src/client-web/src/components/files/OneDriveBindDialog.tsx:13`（`POLL_INTERVAL_MS = 5000`）
- 备注：绑定不存在/非本人返回 5320。Graph 返回 `slow_down` 时服务端内部把轮询节奏提为 7 秒（OneDriveBindingService.cs:182-188），但该提示未进入 DTO——前端类型里的 `pollIntervalSeconds?`（`src/client-web/src/types/index.ts:1407`）后端从不返回，属前后端差异。`authorization_pending` 时维持 pending 原样返回。

### DELETE /api/v1/files/providers/{id}
- 用途：断开（解绑）OneDrive 提供程序。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | providerId |
- Query 参数 / Body：无
- 响应 data：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | （布尔） | boolean | 成功恒为 `true` |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:95,255-265`；服务 `src/modules/Pim.Module.Files/Services/OneDriveBindingService.cs:211-226`；前端 `src/client-web/src/api/files.ts:61,217-219`
- 备注：级联删除该绑定的全部本地文件元数据并使令牌缓存失效（FilesModule.cs:262-264）；不会删除 OneDrive 云端内容。非 OneDrive 来源返回 5323。

### POST /api/v1/files/providers/{id}/test
- 用途：（遗留声明）测试提供程序连通性。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:62,118-120` 声明 `testFileProvider`，无任何组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | providerId |
- Query 参数：无
- Body：前端传 `{}`（空对象）
- 响应 data：（按遗留 DTO `FileProviderTestDto`，后端无处理器产出）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | success | boolean | 是否连通 |
  | status | string | 状态描述 |
  | errorMessage | string\|null | 失败信息 |
- 来源：后端 unknown（源码中未定位路由注册；仅有路径常量 `src/modules/Pim.Module.Files/FilesModule.cs:627` 与 DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:31`）；前端 `src/client-web/src/api/files.ts:62,118-120`
- 备注：v1 Nextcloud 时代端点；文件模块 v2（OneDrive Graph 直链版）重写时移除，前端 API 封装为遗留残留。

### POST /api/v1/files/providers/{id}/sync
- 用途：触发一次手动 OneDrive 同步（REQ-25：后台化，立即返回"已开始"）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | providerId |
- Query 参数：无
- Body：无（前端传 `{}`）
- 响应 data：三种形态之一
  - 正常（Hangfire 可用）：`OneDriveSyncStartedDto`
    | 字段 | 类型 | 说明 |
    | --- | --- | --- |
    | started | boolean | 恒为 `true` |
    | message | string | 提示文案（"已开始同步，可继续浏览；完成后会显示结果"） |
  - 后台设施不可用（未启用 Hangfire 时同步执行兜底）：`OneDriveSyncResultDto`
    | 字段 | 类型 | 说明 |
    | --- | --- | --- |
    | pagesProcessed | integer | 处理的 delta 页数 |
    | itemsApplied | integer | 应用条目数 |
    | itemsDeleted | integer | 删除条目数 |
    | fullRecrawl | boolean | 是否全量重扫 |
  - 入队失败：HTTP 503，`code=5391`，data 为 null
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:97,270-320`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:27-31`、`src/modules/Pim.Module.Files/DTOs/FileDtos.cs:136`；前端 `src/client-web/src/api/files.ts:63,228-230`（另有遗留封装 `getOneDriveSyncResult`/`syncFileProvider`，`files.ts:122-124,221-223`，无组件调用）
- 备注：provider 不存在返回 5104；非 onedrive 来源返回 5334（已退役）。进度与结果经 `GET /providers/{id}/sync-status` 轮询（AC-25.1/AC-25.3）。另有每 20 分钟的 Hangfire 周期任务（FilesModule.cs:175-178）。

### GET /api/v1/files/providers/{id}/sync-status
- 用途：查询手动/周期同步的状态与进度（文件页顶部横幅数据源）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | providerId |
- Query 参数 / Body：无
- 响应 data：`OneDriveSyncStatusDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | syncStatus | string | `idle` / `syncing` / `error`（进程重启会把残留 `syncing` 复位为 `error`，FilesModule.cs:144-154） |
  | lastError | string\|null | 最近错误（如"进程重启中断了上次同步，将自动重试"） |
  | lastSyncAt | string\|null | 最近同步完成时间 |
  | syncedItemCount | integer | 已同步条目数 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:98,323-347`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:139-143`；前端 `src/client-web/src/api/files.ts:103,233-235`

### POST /api/v1/files/providers/nextcloud（清单外发现）
- 用途：（遗留声明）绑定 Nextcloud 提供程序。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:51,114-116` 声明 `bindNextcloudProvider`，无任何组件调用）
- Body（按前端遗留类型 `BindNextcloudProviderRequest`，`src/client-web/src/types/index.ts:1438-1443`）：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | baseUrl | string | 是 | Nextcloud 地址 |
  | internalBaseUrl | string\|null | 否 | 内网地址 |
  | username | string | 是 | 用户名 |
  | appPassword | string | 是 | 应用密码 |
- 响应 data：（后端无处理器；前端类型标注为 `FileProvider`，结构见 `GET /files/providers` 的 `FileProviderDto`）
- 来源：后端 unknown（源码中未定位路由注册；仅路径常量 `src/modules/Pim.Module.Files/FilesModule.cs:625`）；前端 `src/client-web/src/api/files.ts:51,114-116`
- 备注：v1 遗留端点，Nextcloud 随 P4 退役。

## 目录与条目

### GET /api/v1/files/items
- 用途：列出指定目录的直属子项（服务端分页/过滤/排序；文件页目录浏览、目录树、日程附件选择器的数据源）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage、目录树 loadFolderTree、上传传输面板 TransferPanel、日程附件选择器 EventAttachmentFields）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | path | string | 否 | 目录路径；空白规范化为 `/`（FileOperationService.cs:298-309）；前端缺省传 `/`（files.ts:65） |
  | page | integer | 否 | 页码，缺省 1，<1 归 1（FileOperationService.cs:49） |
  | pageSize | integer | 否 | 每页条数，缺省 100（`DefaultPageSize`，FileOperationService.cs:28），钳制 1–100（:31,:50） |
  | q | string | 否 | 当前文件夹内名称过滤（大小写不敏感，服务端执行，FileOperationService.cs:63-69） |
  | sort | string | 否 | `name` / `modified` / `size`；缺省或非法值回落 `name`（FileOperationService.cs:150-158） |
  | order | string | 否 | `asc` / `desc`；缺省 `asc`（FileOperationService.cs:147） |
  | type | string | 否 | `folder` / `file`；其他值（含缺省）不过滤（FileOperationService.cs:161-168） |
- Body：无
- 响应 data：`{ result: PagedResult<FileItemDto> }`（`FileListResponse`，FileDtos.cs:149；`PagedResult` 字段为 items/page/pageSize/totalCount/totalPages）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | result.items[] | FileItemDto[] | 当前页条目，结构见下 |
  | result.page | integer | 当前页码 |
  | result.pageSize | integer | 每页条数 |
  | result.totalCount | integer | 总条数 |
  | result.totalPages | integer | 总页数 |
  `result.items[]`（`FileItemDto`，FileDtos.cs:33-54；映射 FileItemMapper.cs:12-34）：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | result.items[].id | string(uuid) | 条目 ID |
  | result.items[].providerId | string(uuid) | 所属绑定 ID |
  | result.items[].externalFileId | string | OneDrive Graph 条目 ID |
  | result.items[].parentExternalFileId | string\|null | 父条目 Graph ID |
  | result.items[].path | string | 规范化路径（`/` 开头） |
  | result.items[].name | string | 名称 |
  | result.items[].itemType | string | `folder` / `file` |
  | result.items[].mimeType | string\|null | MIME 类型 |
  | result.items[].size | integer\|null | 字节数 |
  | result.items[].etag | string\|null | ETag |
  | result.items[].contentHash | string\|null | 内容哈希 |
  | result.items[].currentVersionId | string\|null | 当前版本 ID |
  | result.items[].permissions | string\|null | 权限标记 |
  | result.items[].isDeleted | boolean | 是否软删（列表恒为 false） |
  | result.items[].deletedAt | string\|null | 删除时间 |
  | result.items[].lastSeenAt | string\|null | 最近一次同步看到该条目的时间 |
  | result.items[].createdAt | string | 创建时间 |
  | result.items[].modifiedAt | string | 修改时间 |
  | result.items[].syncedAt | string | 最近同步时间 |
  | result.items[].indexStatus | string | 最近一次索引任务状态，无任务时 `not_indexed`（FileItemMapper.cs:36-42） |
  | result.items[].ai | object\|null | v2 恒为 `null`（FileItemMapper.cs:34） |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:99,349-366`；服务 `src/modules/Pim.Module.Files/Services/FileOperationService.cs:43-86`；前端 `src/client-web/src/api/files.ts:64-73,126-128`；调用方 `src/client-web/src/pages/FilesPage.tsx:130-143`、`src/client-web/src/components/files/loadFolderTree.ts`、`src/client-web/src/components/files/upload/TransferPanel.tsx`、`src/client-web/src/components/calendar/EventAttachmentFields.tsx:26`（固定 `path=/` 作日程附件选择）
- 备注：排序恒"文件夹在前 + Id 兜底"保证全序翻页（FileOperationService.cs:142-159）；路径前缀判定大小写敏感（与 OneDrive 事实源一致，FileOperationService.cs:106-112 注释）。

### GET /api/v1/files/items/{id}
- 用途：取单个文件条目的元数据。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:74,130-132` 声明 `getFileItem`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`，见上）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:100,368-372`；服务 `src/modules/Pim.Module.Files/Services/FileOperationService.cs:170-174,229-242`；前端 `src/client-web/src/api/files.ts:74,130-132`
- 备注：不存在/非本人/已软删返回 5300。

## 上传

### POST /api/v1/files/items/upload
- 用途：小文件 multipart 直传（服务器经 Graph 写入 OneDrive 并收敛本地元数据）。
- 认证：JWT
- Web 前端使用：是（上传引擎 uploadEngine、上传传输面板 TransferPanel；仅 ≤4MB 使用）
- Path 参数 / Query 参数：无
- Body（multipart/form-data）：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | providerId | string(uuid) | 是 | 绑定 ID；必须是已连接的 OneDrive（否则 5104/5321/5334） |
  | path | string | 是 | 完整目标路径（含文件名）；服务器拆分为目录 + 文件名（FilesModule.cs:401-408） |
  | file | binary | 是 | 文件内容；≤4MB（超限 5331） |
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:101,374-414`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:318-382`（4MB 上限 `OneDriveContentService.cs:36`）；前端 `src/client-web/src/api/files.ts:75,134-141`；调用方 `src/client-web/src/components/files/upload/uploadEngine.ts`、`src/client-web/src/components/files/upload/TransferPanel.tsx`
- 备注：4MB 阈值来自 Graph 单请求 PUT 限制（`SIMPLE_UPLOAD_LIMIT = 4 * 1024 * 1024`，`src/client-web/src/components/files/upload/uploadChunkPlan.ts:24`）；边读边计数防超大请求吃内存（OneDriveWriteService.cs:330-343）。

### POST /api/v1/files/items/upload-session
- 用途：创建 OneDrive 上传会话（REQ-14：服务器只向 Graph 要一个预授权 uploadUrl，不接收字节）。
- 认证：JWT
- Web 前端使用：是（上传分片计划 uploadChunkPlan、上传引擎 uploadEngine、上传传输面板 TransferPanel；>4MB 使用，2GB 上限）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | path | string | 是 | 目标目录路径（须已同步到本地元数据，否则 5304） |
  | fileName | string | 是 | 文件名（OneDriveNameValidator 校验） |
- 响应 data：`UploadSessionDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | uploadUrl | string | Graph 预授权上传 URL（分片 PUT **不得携带 Authorization**，FileDtos.cs:125） |
  | expirationDateTime | string\|null | 会话过期时间 |
  | path | string | 回显请求的目录路径 |
  | fileName | string | 回显请求的文件名 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:128,534-545`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:122-130`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:388-400`；前端 `src/client-web/src/api/files.ts:105,263-265`
- 备注：前端 `MAX_UPLOAD_BYTES = 2 * 1024 * 1024 * 1024`（2GB 上限，uploadChunkPlan.ts:67）；推荐分片 10MiB（`UPLOAD_CHUNK_SIZE`，uploadChunkPlan.ts:21），单块硬上限 60MiB（:18），非末块须 320KiB 对齐（:15,76-77）。

### POST /api/v1/files/items/upload-session/complete
- 用途：分片上传完成后登记元数据（内容已在微软侧，服务器只回读并收敛本地元数据）。
- 认证：JWT
- Web 前端使用：是（上传传输面板 TransferPanel）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | path | string | 是 | 目标目录路径（与创建会话时一致） |
  | fileName | string | 是 | 文件名 |
  | uploadedItemId | string | 否 | 上传完成响应里的真实条目 id；带上有助于重名场景正确登记 |
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:129,547-557`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:133`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:406-473`；前端 `src/client-web/src/api/files.ts:107,268-274`；调用方 `src/client-web/src/components/files/upload/TransferPanel.tsx`
- 备注（前端直传 Graph 的行为约定）：浏览器拿到 `uploadUrl` 后直接分片 PUT 字节到微软域——分片 10MiB，逐片带 `Content-Range` 头；HTTP `202` = 还需继续（读响应 `nextExpectedRanges` 对齐下一分片），`201` = 上传完成（响应体即最终条目，含真实 id/name，需带回 `uploadedItemId`）；`416` 等错误按 `nextExpectedRanges` 断点续传（`src/client-web/src/components/files/upload/uploadEngine.ts:64-76,152-163`）。重名时 Graph 自动改名"名称 (1).ext"，服务器优先按 `uploadedItemId` 回读真实条目（OneDriveWriteService.cs:418-431）。

## 下载与预览

### GET /api/v1/files/items/{id}/download
- 用途：稳定下载直链：302 重定向到 Graph 预授权下载 URL。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:76,143-145` 声明 `downloadFileBlob`，无组件调用；前端实际下载走 `download-url` 端点）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：无 JSON 响应体——`302` 重定向（`Location` 为 Graph 预授权 URL）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:102,439-444`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:78-86`；前端 `src/client-web/src/api/files.ts:76,143-145`
- 备注：统一过"登录 → 归属 → 敏感路径"三道闸；文件夹返回 5332；Graph 未返回直链返回 5333。前端改用 `download-url`（REQ-20/AC-20.1：避免 fetch 跟随 302 把整个文件体拉进内存）。

### GET /api/v1/files/items/{id}/download-url
- 用途：以 JSON 形态返回下载直链（页面 `window.open`，由浏览器直接从微软域下载）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage、OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`OneDriveLinkDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | url | string | Graph 预授权下载 URL（微软域名） |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:106,450-454`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:33`；前端 `src/client-web/src/api/files.ts:57,295-297`；调用方 `src/client-web/src/pages/FilesPage.tsx:414-416`、`src/client-web/src/components/files/OneDrivePreviewPane.tsx`
- 备注：>100MB 下载前先弹确认框（`DOWNLOAD_CONFIRM_THRESHOLD_BYTES = 100 * 1024 * 1024`，`src/client-web/src/components/files/fileActions.ts:10`；FilesPage.tsx:425）；错误口径同 `download`（5332/5333/40303）。

### GET /api/v1/files/items/{id}/content
- 用途：内容直链：302 重定向到 Graph 预授权内容 URL。
- 认证：JWT
- Web 前端使用：是（图片网格 ImageGrid，作为图片加载地址）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：无 JSON 响应体——`302` 重定向（`Location` 为 Graph 预授权 URL）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:110,188-192`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:78-86`；前端 `src/client-web/src/api/files.ts:54,277-279`；调用方 `src/client-web/src/components/files/ImageGrid.tsx:46,131`
- 备注：前端用带 `Authorization` 的 `fetch` 跟随 302 取图片内容（跨域重定向后浏览器自动丢弃凭据头，Graph 预授权 URL 本身免鉴权）；文件夹 5332；敏感路径 40303。

### GET /api/v1/files/items/{id}/thumbnail
- 用途：缩略图直链：302 重定向到 Graph 缩略图 URL。
- 认证：JWT
- Web 前端使用：是（文件缩略图 FileThumbnail）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | size | string | 否 | 缩略图尺寸，`medium` / `large`（前端类型 `src/client-web/src/components/files/FileThumbnail.tsx:9`）；缺省由服务端补 `medium`（FilesModule.cs:199） |
- Body：无
- 响应 data：无 JSON 响应体——`302` 重定向（`Location` 为 Graph 缩略图 URL，`src/modules/Pim.Module.Files/Providers/OneDriveGraphClient.cs:214-219`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:111,194-199`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:88-95`；前端 `src/client-web/src/api/files.ts:55,281-283`；调用方 `src/client-web/src/components/files/FileThumbnail.tsx:71-78`
- 备注：`<img src>` 无法携带 `Authorization` 头，因此前端用带鉴权 `fetch` → `blob` → `URL.createObjectURL` 渲染，用完即 revoke（FileThumbnail.tsx:22-23,71-78）；该文件不支持缩略图返回 5332；敏感路径 40303。

### GET /api/v1/files/items/{id}/preview-url
- 用途：返回微软域预览页地址（JSON，不重定向）。
- 认证：JWT
- Web 前端使用：是（OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`OneDriveLinkDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | url | string | Graph preview 预览地址（指向微软域名） |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:112,201-205`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:33`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:97-105`；前端 `src/client-web/src/api/files.ts:56,285-287`
- 备注：与 `download-url` 同一模式——服务器只给链接、不搬字节（FilesModule.cs:103-105 注释）；文件夹 5332；无预览地址 5333；敏感路径 40303。

## 搜索

### GET /api/v1/files/search
- 用途：全盘元数据搜索（只搜 name/path/mimeType 的 PostgreSQL ILIKE，v2 不做内容/向量检索）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage，全局搜索模式；输入 250ms 防抖）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | q | string | 否 | 关键词；空白时直接返回空结果（FileSearchService.cs:61-65） |
  | mode | string | 否 | `keyword` / `semantic` / `hybrid`（前端类型 `src/client-web/src/types/index.ts:1568`）；**v2 接受但忽略**，恒为元数据关键词搜索；前端恒传 `keyword`（FilesPage.tsx:145） |
  | page | integer | 否 | 页码，缺省 1（FilesModule.cs:504） |
  | pageSize | integer | 否 | 每页条数，缺省 20（`ResultLimit`，FileSearchService.cs:25），钳制 1–100（:28,:59）；前端传 100（`FILE_PAGE_SIZE`，`src/client-web/src/components/files/fileBrowserState.ts:13`） |
- Body：无
- 响应 data：`FileSearchResultDto`（FileDtos.cs:112-116）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | items[] | FileItemDto[] | 命中条目，元素结构同 `GET /files/items` 的 `result.items[]` |
  | chunks[] | FileChunkSearchHitDto[] | 内容块命中；**v2 恒为空数组**（FileSearchService.cs:99） |
  | totalCount | integer | 命中总数（不含敏感路径项） |
  | totalPages | integer | 总页数 |
  `FileChunkSearchHitDto`（FileDtos.cs:117，v2 实际不产出）：`chunkId` / `fileItemId` / `versionId` / `text` / `score`（decimal）。
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:120,495-504`；服务 `src/modules/Pim.Module.Files/Services/FileSearchService.cs:52-102`；前端 `src/client-web/src/api/files.ts:86-91,187-189`；调用方 `src/client-web/src/pages/FilesPage.tsx:126,144-145`
- 备注：敏感路径文件在 SQL 侧排除，且 `totalCount` 也不含敏感项（防数量泄漏，FileSearchService.cs:43-51,126-140）；排序恒"文件夹在前 → 名称 → Id"。

## 整理操作

### POST /api/v1/files/folders
- 用途：在当前目录新建文件夹（REQ-15）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage、上传传输面板 TransferPanel）
- Path 参数 / Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | path | string | 是 | 完整目标路径（含新文件夹名），服务器拆分为父目录 + 名称（OneDriveNameValidator.SplitTargetPath） |
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:126,523-532`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:119`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:484-531`；前端 `src/client-web/src/api/files.ts:97,238-240`
- 备注：重名时 Graph 以 `conflictBehavior=rename` 自动改名（如"报告 1"），响应以服务端回读的**真实名称**为准，不是输入名（OneDriveWriteService.cs:479-500）；父目录不存在 5304。

### POST /api/v1/files/items/{id}/move
- 用途：移动条目到目标文件夹（经 Graph PATCH + 本地元数据收敛，含子孙路径改写）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage，经移动对话框 MoveDialog）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | destinationPath | string | 是 | 目标文件夹路径（须已同步到本地元数据） |
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:107,456-466`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:118`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:54-90`；前端 `src/client-web/src/api/files.ts:77,147-149`
- 备注：移动根目录 / 移入自身 / 移入自己的子目录均返回 5337（Graph 调用前拦截，防子孙路径错乱）；目标文件夹不存在 5304。

### POST /api/v1/files/items/{id}/rename
- 用途：重命名条目（经 Graph PATCH + 本地元数据收敛，目录改名会改写子孙路径）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 是 | 新名称；非空且不能包含 `/`（否则 5338） |
- 响应 data：`FileItemDto`（字段同 `GET /files/items` 的 `result.items[]`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:108,468-478`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:144`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:92-120`；前端 `src/client-web/src/api/files.ts:78,151-153`
- 备注：根目录（`path = "/"`）不可重命名，返回 5338。

### DELETE /api/v1/files/items/{id}
- 用途：删除条目（Graph DELETE 移入 OneDrive 自身回收站 + 本地软删）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | （字符串） | string | 提示文案："已删除（已移入 OneDrive 回收站；如需还原请在 OneDrive 网页版操作）" |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:109,480-490`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:122-143`；前端 `src/client-web/src/api/files.ts:78,155-157`
- 备注：目录删除会在本地级联软删全部子孙（远端随父项一起进回收站）；根目录不可删（5337）。个人版 OneDrive 无回收站 API，因此响应文案如实说明需到 OneDrive 网页版还原（复审 I-7）。

## 分享

### POST /api/v1/files/items/{id}/share
- 用途：为条目生成 OneDrive 分享链接（REQ-21）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage，经分享对话框 ShareDialog）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | permissionType | string | 是 | `view` / `edit`；空或 null 回落 `view`，其他值报 5300（OneDriveShareService.cs:180-186） |
  | expiresInDays | integer | 否 | `null`/`0` = 不过期；仅接受 `7` 或 `30`（天），其他值报 5300（OneDriveShareService.cs:189-196） |
- 响应 data：`FileShareDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | itemId | string(uuid) | 条目 ID |
  | itemName | string | 条目名称 |
  | path | string | 条目路径 |
  | permissionType | string | 权限档（Graph 实际返回值） |
  | permissionId | string\|null | Graph 权限 ID（撤销时用） |
  | webUrl | string | 分享链接 |
  | expiresAt | string\|null | 过期时间 |
  | createdAt | string | 创建时间 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:131,559-565`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:120`、`src/modules/Pim.Module.Files/Services/OneDriveShareService.cs:12-20`；前端 `src/client-web/src/api/files.ts:98,243-245`
- 备注：敏感路径禁止分享（40303，AC-21.4）；webUrl 只在响应里返回，不写日志/审计（OneDriveShareService.cs:25-31）。

### GET /api/v1/files/items/{id}/shares
- 用途：列出某条目当前的全部分享权限（预览面板就地撤销用）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage，经分享对话框 ShareDialog）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`FileShareDto[]`（元素结构同 `POST /files/items/{id}/share`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:132,567-571`；服务 `src/modules/Pim.Module.Files/Services/OneDriveShareService.cs:106-123`；前端 `src/client-web/src/api/files.ts:99,248-250`
- 备注：实时查询 Graph 权限列表，非本地缓存。

### DELETE /api/v1/files/items/{id}/shares/{permissionId}
- 用途：撤销某个分享权限（撤销后链接失效，AC-21.1）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage，经分享对话框 ShareDialog）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
  | permissionId | string | 是 | Graph 权限 ID（前端经 `encodeURIComponent` 传递，files.ts:100-101） |
- Query 参数 / Body：无
- 响应 data：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | （布尔） | boolean | 成功恒为 `true` |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:133,573-581`；服务 `src/modules/Pim.Module.Files/Services/OneDriveShareService.cs:83-103`；前端 `src/client-web/src/api/files.ts:100-101,253-255`
- 备注：permissionId 空白返回 5300；敏感路径条目不可操作（40303）。

### GET /api/v1/files/shares
- 用途："我的分享"列表（按最近修改的文件逐个查询分享权限聚合）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | limit | integer | 否 | 候选文件数上限，缺省 50，钳制 1–200（OneDriveShareService.cs:145,583-588） |
- Body：无
- 响应 data：`FileShareDto[]`（元素结构同 `POST /files/items/{id}/share`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:134,583-588`；服务 `src/modules/Pim.Module.Files/Services/OneDriveShareService.cs:132-178`；前端 `src/client-web/src/api/files.ts:102,258-260`
- 备注：个人版没有"列出我的全部分享"API，实际是对最近同步的 ≤limit 个文件逐个查 Graph 权限（OneDriveShareService.cs:132-146 注释），**不是全量保证**；敏感路径条目跳过，单个条目查询失败不影响整表。

## 文本与快照

### GET /api/v1/files/items/{id}/text
- 用途：读取小文本文件内容（编辑器载入）。
- 认证：JWT
- Web 前端使用：是（OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`OneDriveTextDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | content | string | UTF-8 文本全文 |
  | mimeType | string\|null | MIME 类型 |
  | size | integer | 字节数 |
  | truncated | boolean | 恒为 `false`（读取上限与保存一致） |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:113,207-211`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:35-39`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:172-186`；前端 `src/client-web/src/api/files.ts:58,299-301`
- 备注：仅文本类型可读——MIME 以 `text/` 开头或含 `json`/`xml`/`yaml`，或扩展名为 `.txt`/`.md`/`.markdown`/`.json`/`.csv`/`.log`/`.yml`/`.yaml`/`.xml`（OneDriveContentService.cs:276-294），否则 5332；读取上限 4MB（`MaxSaveBytes`，OneDriveContentService.cs:36,179-180 注释）；文件夹 5332；敏感路径 40303。

### PUT /api/v1/files/items/{id}/text
- 用途：保存小文本文件内容（先快照当前内容再经 Graph 覆写）。
- 认证：JWT
- Web 前端使用：是（OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：无
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | content | string | 是 | 新文本全文（null 直接抛参数异常） |
- 响应 data：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | （布尔） | boolean | 成功恒为 `true` |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:114,213-221`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:41`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:188-214`；前端 `src/client-web/src/api/files.ts:58,303-305`
- 备注：内容 >4MB 返回 5331；保存前先把当前内容存为快照（`reason = "pre-edit"`），每文件只保留最近 10 份快照（`KeepSnapshotsPerItem`，OneDriveContentService.cs:39,202-210）。

### GET /api/v1/files/items/{id}/snapshots
- 用途：列出该文件的文本编辑快照（恢复入口）。
- 认证：JWT
- Web 前端使用：是（OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`FileTextSnapshotDto[]`（按 createdAt 倒序）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 快照 ID |
  | path | string | 快照时的条目路径 |
  | name | string | 快照时的名称 |
  | content | string | 内容**预览**：超过 64 字符截断（OneDriveContentService.cs:226） |
  | byteSize | integer | 快照字节数 |
  | reason | string | 快照原因：`pre-edit`（编辑前）/ `pre-restore`（恢复前） |
  | createdAt | string | 创建时间 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:115,223-227`；DTO `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:16-23`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:216-229`；前端 `src/client-web/src/api/files.ts:59,307-309`
- 备注：列表中的 `content` 是截断预览；恢复动作使用数据库中的完整内容（OneDriveContentService.cs:243）。敏感路径同样拦截（40303）。

### POST /api/v1/files/items/{id}/snapshots/{snapshotId}/restore
- 用途：把文件内容恢复到指定快照。
- 认证：JWT
- Web 前端使用：是（OneDrive 预览面板 OneDrivePreviewPane）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
  | snapshotId | string(uuid) | 是 | 快照 ID |
- Query 参数 / Body：无
- 响应 data：
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | （布尔） | boolean | 成功恒为 `true` |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:116,229-237`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:231-248`；前端 `src/client-web/src/api/files.ts:60,311-313`
- 备注：恢复前先把**当前**内容存为快照（`reason = "pre-restore"`，可再撤销回来）；快照不存在返回 5104；恢复后同样执行 10 份快照裁剪。

### GET /api/v1/files/items/{id}/extracted-text
- 用途：文本抽取读取（docx/pptx/PDF 等经 Tika 抽取；面向 MCP `read_file_text` 与 agent 的高频读取，带限流与审计）。
- 认证：JWT
- Web 前端使用：否（前端无调用方，服务 MCP 工具 read_file_text）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | maxBytes | integer | 否 | 返回文本字节上限；缺省 64KB（`DefaultMaxBytes = 65536`，OneDriveTextExtractor.cs:25），上限 1MB（`HardMaxBytes = 1048576`，:26）；≤0 视为缺省（OneDriveContentService.cs:114-116） |
- Body：无
- 响应 data：`OneDriveTextDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | content | string | 抽取出的文本（按 maxBytes 截断） |
  | mimeType | string\|null | 条目 MIME 类型 |
  | size | integer | 抽取源字节数 |
  | truncated | boolean | 是否被 maxBytes 截断 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:118,600-608`；服务 `src/modules/Pim.Module.Files/Services/OneDriveContentService.cs:112-133`、`src/modules/Pim.Module.Files/Services/OneDriveTextExtractor.cs:25-26`；前端无（`src/client-web/src/api/files.ts` 中无对应封装）
- 备注：文件夹返回 5332；源文件超过处理上限返回 5331；敏感路径 40303；有瞬态限流（`OneDriveTransientRateLimiter`）。

### POST /api/v1/files/items/{id}/restore
- 用途：本地恢复软删条目（仅"本地软删但远端仍在"的短暂窗口可用）。
- 认证：JWT
- Web 前端使用：否（前端无调用方）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：`OneDriveWriteResultDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | itemId | string(uuid) | 条目 ID |
  | path | string | 恢复后路径 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:119,610-618`；DTO `src/modules/Pim.Module.Files/DTOs/OneDriveDtos.cs:43-47`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:196-236`；前端无（`src/client-web/src/api/files.ts` 中无对应封装）
- 备注：OneDrive 个人版无回收站 API，恢复前先经 Graph 校验远端仍在——不在回收站返回 5339，远端已删返回 5340；目录恢复会级联恢复"同一次删除且远端仍存在"的子孙（OneDriveWriteService.cs:214-235）。

## 其他

### GET /api/v1/files/items/{id}/open-link
- 用途：获取 OneDrive 网页版地址（在新标签打开云端页面）。
- 认证：JWT
- Web 前端使用：是（文件页 FilesPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 条目 ID |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | mode | string | 否 | 前端会传 `view` / `edit` / `nextcloud`（`FileOpenLinkMode`，types/index.ts:1599；files.ts:95）；**后端处理器不读取该参数，直接忽略** |
- Body：无
- 响应 data：`FileOpenLinkDto`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | url | string | OneDrive 网页版地址（Graph webUrl） |
  | mode | string | 恒为 `"onedrive-web"` |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:124,590-597`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:145`；服务 `src/modules/Pim.Module.Files/Services/OneDriveWriteService.cs:534-543`；前端 `src/client-web/src/api/files.ts:95,203-205`
- 备注（前后端差异）：前端发送 `mode` 查询参数，后端处理器签名（FilesModule.cs:590-593）没有对应 `[FromQuery]` 入参，`mode` 被忽略；响应 `mode` 恒为 `onedrive-web`。webUrl 是内容出口，敏感路径同样拦截（40303，OneDriveWriteService.cs:536-539）；无 webUrl 返回 5333。

### GET /api/v1/files/items/{id}/versions
- 用途：（遗留声明）列出条目历史版本。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:81,167-169` 声明 `getFileVersions`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | 条目 ID |
- Query 参数 / Body：无
- 响应 data：（按遗留 DTO `FileVersionDto`，后端无处理器产出）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 版本 ID |
  | fileItemId | string(uuid) | 条目 ID |
  | externalVersionId | string | Graph 版本 ID |
  | etag | string\|null | ETag |
  | size | integer\|null | 字节数 |
  | modifiedAt | string | 修改时间 |
  | source | string | 版本来源 |
  | isCurrent | boolean | 是否当前版本 |
  | syncedAt | string | 同步时间 |
- 来源：后端 unknown（源码中未定位路由注册；DTO 见 `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:56-65`）；前端 `src/client-web/src/api/files.ts:81,167-169`
- 备注：个人版 OneDrive 版本 API 不确定是 v2 移除版本端点的原因（编辑改用文本快照机制，见"文本与快照"组）。

### GET /api/v1/files/items/{id}/versions/{versionId}/download
- 用途：（遗留声明）下载历史版本内容。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:82,171-173` 声明 `downloadFileVersionBlob`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | 条目 ID |
  | versionId | string | 是 | 版本 ID |
- Query 参数 / Body：无
- 响应 data：前端按 Blob 处理（`apiDownloadBlob`）；后端无处理器，形态 unknown（源码中未定位）
- 来源：后端 unknown（源码中未定位路由注册）；前端 `src/client-web/src/api/files.ts:82,171-173`
- 备注：v1 遗留端点。

### POST /api/v1/files/items/{id}/index
- 用途：（遗留声明）手动触发条目 AI 索引。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:85,183-185` 声明 `indexFile`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | 条目 ID |
- Query 参数：无
- Body：前端传 `{}`（空对象）
- 响应 data：（按遗留 DTO `FileIndexJobDto`，后端无处理器产出）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 索引任务 ID |
  | fileItemId | string(uuid) | 条目 ID |
  | versionId | string\|null | 版本 ID |
  | status | string | 任务状态 |
  | stage | string | 处理阶段 |
  | attemptCount | integer | 尝试次数 |
  | lastError | string\|null | 最近错误 |
- 来源：后端 unknown（源码中未定位路由注册；DTO 见 `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:147`）；前端 `src/client-web/src/api/files.ts:85,183-185`
- 备注：v2 只保留 `indexStatus` 元数据字段（FileItemDto），无手动索引入口。

### GET /api/v1/files/trash
- 用途：（遗留声明）列出本地回收站。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:79,159-161` 声明 `getFileTrash`，无组件调用）
- Path 参数 / Query 参数 / Body：无
- 响应 data：（按前端遗留类型 `FileTrashItem`，后端无处理器产出）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | trashId | string | 回收站条目 ID |
  | originalLocation | string | 原路径 |
  | name | string | 名称 |
  | itemType | string | `folder` / `file` |
  | size | integer\|null | 字节数 |
  | deletedAt | string | 删除时间 |
- 来源：后端 unknown（源码中未定位路由注册）；前端 `src/client-web/src/api/files.ts:79,159-161`；类型 `src/client-web/src/types/index.ts:1624-1631`
- 备注：v1 本地回收站语义已退役；v2 删除直接进 OneDrive 回收站（见 `DELETE /files/items/{id}` 备注）。

### POST /api/v1/files/trash/{providerId}/restore
- 用途：（遗留声明）从本地回收站恢复。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:80,163-165` 声明 `restoreFileTrash`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | providerId | string | 是 | 提供程序 ID |
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | trashId | string | 是 | 回收站条目 ID（前端必拼，files.ts:80） |
- Body：前端传 `{}`（空对象）
- 响应 data：（按前端声明为 string；后端无处理器，形态 unknown（源码中未定位））
- 来源：后端 unknown（源码中未定位路由注册）；前端 `src/client-web/src/api/files.ts:80,163-165`
- 备注：v1 遗留端点；v2 的对应能力是 `POST /files/items/{id}/restore`（仅短暂窗口可用，见该节备注）。

### GET /api/v1/files/suggestions
- 用途：列出当前用户的文件整理建议（AI 产生的移动/归类建议）。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:92,191-193` 声明 `getFileSuggestions`，无组件调用）
- Path 参数 / Query 参数 / Body：无
- 响应 data：`FileSuggestionDto[]`（按 updatedAt 倒序）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string(uuid) | 建议 ID |
  | fileItemId | string(uuid) | 关联条目 ID |
  | suggestionType | string | 建议类型 |
  | title | string | 标题 |
  | reason | string | 理由说明 |
  | confidence | number(decimal) | 置信度（0–1） |
  | payloadJson | string | 载荷 JSON 字符串 |
  | status | string | 状态：`dismissed` / `accepted` 等 |
  | aiRequestLogId | string\|null | 产生该建议的 AI 请求日志 ID |
  | createdAt | string | 创建时间 |
  | updatedAt | string | 更新时间 |
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:121,506-509`；DTO `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:80-91`；服务 `src/modules/Pim.Module.Files/Services/FileOperationService.cs:190-203`；前端 `src/client-web/src/api/files.ts:92,191-193`

### POST /api/v1/files/suggestions/{id}/dismiss
- 用途：忽略（驳回）一条文件建议。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:93,195-197` 声明 `dismissFileSuggestion`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 建议 ID |
- Query 参数 / Body：无
- 响应 data：`FileSuggestionDto`（结构同 `GET /files/suggestions`；`status` 置为 `dismissed`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:122,511-515`；服务 `src/modules/Pim.Module.Files/Services/FileOperationService.cs:205-215`；前端 `src/client-web/src/api/files.ts:93,195-197`
- 备注：建议不存在返回 5305；写审计 `files.suggestion_dismiss`。

### POST /api/v1/files/suggestions/{id}/accept
- 用途：接受一条文件建议。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:94,199-201` 声明 `acceptFileSuggestion`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string(uuid) | 是 | 建议 ID |
- Query 参数 / Body：无
- 响应 data：`FileSuggestionDto`（结构同 `GET /files/suggestions`；`status` 置为 `accepted`）
- 来源：后端 `src/modules/Pim.Module.Files/FilesModule.cs:123,517-521`；服务 `src/modules/Pim.Module.Files/Services/FileOperationService.cs:217-227`；前端 `src/client-web/src/api/files.ts:94,199-201`
- 备注：建议不存在返回 5305；写审计 `files.suggestion_accept`。

### POST /api/v1/files/items/{id}/versions/{versionId}/restore（清单外发现）
- 用途：（遗留声明）恢复到历史版本。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:84,179-181` 声明 `restoreFileVersion`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | 条目 ID |
  | versionId | string | 是 | 版本 ID |
- Query 参数 / Body：前端传 `{}`（空对象）；无其他参数
- 响应 data：（按前端声明为 string；后端无处理器，形态 unknown（源码中未定位））
- 来源：后端 unknown（源码中未定位路由注册；仅路径辅助常量 `src/modules/Pim.Module.Files/FilesModule.cs:631`）
- 备注：v1 遗留端点；v2 对应能力为文本快照恢复。

### POST /api/v1/files/items/{id}/versions/{versionId}/restore-preview（清单外发现）
- 用途：（遗留声明）预览版本恢复将产生的变化。**后端 v2 未注册该路由，调用会 404**。
- 认证：JWT
- Web 前端使用：否（仅 `src/client-web/src/api/files.ts:83,175-177` 声明 `restoreFileVersionPreview`，无组件调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string | 是 | 条目 ID |
  | versionId | string | 是 | 版本 ID |
- Query 参数 / Body：前端传 `{}`（空对象）；无其他参数
- 响应 data：（按遗留 DTO `VersionRestorePreviewDto`，后端无处理器产出）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | fileItemId | string(uuid) | 条目 ID |
  | versionId | string(uuid) | 目标版本 ID |
  | currentVersionLabel | string | 当前版本标签 |
  | restoreVersionLabel | string | 待恢复版本标签 |
  | requiresConfirmation | boolean | 是否需要确认 |
  | summary | string | 变化摘要 |
- 来源：后端 unknown（源码中未定位路由注册；DTO 见 `src/modules/Pim.Module.Files/DTOs/FileDtos.cs:146`）；前端 `src/client-web/src/api/files.ts:83,175-177`
- 备注：v1 遗留端点，随版本端点一并退役。

---

## 自查记录

- 端点节计数：44 个 `###` 节（任务清单 41 项全覆盖 + 清单外补充 3 项：`POST /files/providers/nextcloud`、`POST /files/items/{id}/versions/{versionId}/restore`、`POST /files/items/{id}/versions/{versionId}/restore-preview`）。
- 清单中 6 个端点（`POST /files/providers/{id}/test`、`GET /files/trash`、`POST /files/trash/{providerId}/restore`、`GET /files/items/{id}/versions`、`GET /files/items/{id}/versions/{versionId}/download`、`POST /files/items/{id}/index`）经全仓 grep 确认后端无路由注册（v2 移除），文档按"遗留声明、未注册、前端未调用"如实标注，未编造响应行号。
- 分页封装实际字段为 `totalCount`（非统一文档头示例中的 `total`），已在"域级说明"中标注差异。
