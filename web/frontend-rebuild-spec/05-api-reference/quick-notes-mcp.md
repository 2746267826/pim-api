# 快速记录与 MCP 域接口规格（/api/v1/quick-notes、/api/v1/mcp、/mcp）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> ⚠️ `/mcp`（无版本前缀）是 MCP Streamable HTTP 端点：POST 为 JSON-RPC，GET 为 SSE 事件流；认证为 Bearer（普通 JWT 或 MCP 签发的受限 token），缺头返回 401 `{code:40101}`；OPTIONS 允许 CORS；`/mcp/` 308 跳转 `/mcp`。

## 快速记录

### GET /api/v1/quick-notes
- 用途：分页列出当前用户的快速记录，支持按状态过滤与内容关键词搜索。
- 认证：JWT
- Web 前端使用：是（快速记录页 QuickNotesPage，React Query 列表查询）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | status | string | 否 | 状态过滤，枚举：`inbox` / `processed` / `archived`（非法值报 400 code 4003） |
  | search | string | 否 | 对 `ContentMarkdown` 做 Contains 包含匹配 |
  | page | int | 否 | 页码，默认 1，最小 1 |
  | pageSize | int | 否 | 页大小，默认 30，钳制到 1–100 |
- 响应 data：`data = PagedResult<QuickNoteListItem>`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | items | QuickNoteListItem[] | 记录列表，按 `updatedAt` 倒序 |
  | items[].id | string (uuid) | 记录 ID |
  | items[].contentPreview | string | 纯文本预览，≤140 字符（剔除 `#`/`*`/`_`/`` ` `` 与换行） |
  | items[].status | string | 枚举：`inbox` / `processed` / `archived` |
  | items[].source | string | 来源：`web-floating` / `web-page` |
  | items[].attachmentCount | number | 未删除附件数 |
  | items[].attachments | QuickNoteAttachment[] \| null | 未删除附件（按 createdAt 升序） |
  | items[].createdAt | string (ISO8601) | 创建时间 |
  | items[].updatedAt | string (ISO8601) | 更新时间 |
  | items[].archivedAt | string \| null | 归档时间 |
  | totalCount | number | 总条数 |
  | page | number | 当前页 |
  | pageSize | number | 页大小 |
  | totalPages | number | 总页数 |
  QuickNoteAttachment 子对象字段见「GET /api/v1/quick-notes/{id}」响应。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:37-47`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:36-89`（预览 272-284）；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:5-14`；前端 `src/client-web/src/api/quickNotes.ts:41-44`（参数 13-29、路径 31-39）
- 备注：前端固定传 `page=1, pageSize=50`（`src/client-web/src/pages/QuickNotesPage.tsx:117-126`），`status=all` 时不传 status 参数；分类（灵感/学业/开发/运维/生活）是前端对 contentPreview 的本地过滤，非服务端字段。

### GET /api/v1/quick-notes/{id}
- 用途：获取单条快速记录完整内容（含全部未删除附件）。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog 打开详情/编辑）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：无
- 响应 data：`QuickNoteDetail`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string (uuid) | 记录 ID |
  | contentMarkdown | string | 完整 Markdown 内容 |
  | status | string | 枚举：`inbox` / `processed` / `archived` |
  | source | string | `web-floating` / `web-page` |
  | attachments | QuickNoteAttachment[] | 未删除附件（createdAt 升序，次序键 id） |
  | attachments[].id | string (uuid) | 附件 ID |
  | attachments[].fileName | string | 文件名（存储时已取 Path.GetFileName） |
  | attachments[].contentType | string | MIME 类型，缺省 `application/octet-stream` |
  | attachments[].sizeBytes | number | 字节数 |
  | attachments[].downloadUrl | string | `/api/v1/quick-notes/attachments/{id}/download` |
  | attachments[].previewUrl | string \| null | 图片类（`image/*`）= downloadUrl，否则 null |
  | attachments[].createdAt | string (ISO8601) | 上传时间 |
  | metadataJson | string | 元数据 JSON 字符串（原样透传） |
  | createdAt | string (ISO8601) | 创建时间 |
  | updatedAt | string (ISO8601) | 更新时间 |
  | archivedAt | string \| null | 归档时间 |
- 错误：记录不存在或不属于当前用户 → 400 code 4004「快速记录不存在」（DomainException 封装为 ApiResponse）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:49-53`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:91-95, 309-345`；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:16-34`；前端 `src/client-web/src/api/quickNotes.ts:46-48`
- 备注：仅返回未软删附件（`DeletedAt == null`）。

### POST /api/v1/quick-notes
- 用途：新建快速记录，可同时绑定附件。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog 新建、QuickNoteFloatingEntry 全局悬浮入口）
- Path 参数：无
- Query 参数：无
- Body：JSON `CreateQuickNoteRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | contentMarkdown | string | 是 | Markdown 内容（服务端对 null 容错为空串） |
  | source | string | 否 | 来源，`web-floating` / `web-page`；空白时默认 `web-page` |
  | attachmentIds | string[] (uuid) | 否 | 待绑定附件 ID；会与 markdown 中出现的附件下载链接 ID 合并去重 |
- 响应 data：`QuickNoteDetail`（字段同 GET /quick-notes/{id}）；HTTP 201，响应头 `Location: /api/v1/quick-notes/{id}`。新建记录 `status` 固定为 `inbox`。
- 错误：attachmentIds 含非本人或已绑定到其他记录的附件 → 400 code 4005「附件不能绑定到这条快速记录」。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:55-64`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:97-128`（source 归一化 269-270、ID 合并 286-307、附件校验 `Services/QuickNoteAttachmentService.cs:117-146`）；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:36-39`；前端 `src/client-web/src/api/quickNotes.ts:50-52`
- 备注：前端全局悬浮入口传 `source="web-floating"`（`src/client-web/src/components/quick-notes/QuickNoteFloatingEntry.tsx:134`），页面内新建默认 `web-page`。markdown 附件引用形如 `/api/v1/quick-notes/attachments/{36位uuid}/download`，由正则提取（`Services/QuickNoteMarkdownReferences.cs:6-22`）。

### PUT /api/v1/quick-notes/{id}
- 用途：更新快速记录内容/状态/附件集合。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog 保存编辑、TipTap 编辑器附件增删后回写）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：JSON `UpdateQuickNoteRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | contentMarkdown | string | 是 | 新 Markdown 内容（整体替换；null 存为空串） |
  | status | string | 否 | `inbox` / `processed` / `archived`；传入时校验合法性，置 `archived` 时写 archivedAt，否则清空 |
  | attachmentIds | string[] (uuid) | 否 | 更新后的完整附件 ID 集合；与 markdown 引用 ID 合并 |
- 响应 data：`QuickNoteDetail`（字段同 GET /quick-notes/{id}）
- 错误：400 code 4004（不存在）；400 code 4003（status 非法）；400 code 4005（附件不可绑定）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:66-71`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:130-168`；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:41-44`；前端 `src/client-web/src/api/quickNotes.ts:54-56`
- 备注：附件为差量语义——不在最终集合中的既有附件会被软删（`QuickNoteService.cs:154-155`），集合外的 ID 直接报 4005。前端调用只传 `contentMarkdown + attachmentIds`（`src/client-web/src/components/quick-notes/QuickNoteDialog.tsx:269, 290`）。

### POST /api/v1/quick-notes/{id}/process
- 用途：将记录标记为「已处理」。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog「标记已处理」操作）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`）
- 响应 data：`QuickNoteDetail`（`status=processed`，`archivedAt=null`，`updatedAt=now`）
- 错误：400 code 4004（不存在）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:73-77`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:170-181`；前端 `src/client-web/src/api/quickNotes.ts:58-60`
- 备注：⚠️ 名字容易误解——源码中没有任何 AI 调用，仅把 `status` 置为 `processed`（`QuickNoteService.cs:173`）。前端重建时不要实现"触发 AI 处理"。

### POST /api/v1/quick-notes/{id}/archive
- 用途：归档记录。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog「归档」操作）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：无（前端发送空对象 `{}`）
- 响应 data：`QuickNoteDetail`（`status=archived`，`archivedAt=now`，`updatedAt=now`）
- 错误：400 code 4004（不存在）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:79-83`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:183-195`；前端 `src/client-web/src/api/quickNotes.ts:62-64`

### POST /api/v1/quick-notes/{id}/restore
- 用途：将（已归档/已处理的）记录恢复到指定状态。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog「恢复到收集箱」）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：JSON `RestoreQuickNoteRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | status | string | 否 | 恢复目标状态，枚举 `inbox` / `processed` / `archived`；空白时默认 `inbox`（传 `archived` 会重新置 archivedAt） |
- 响应 data：`QuickNoteDetail`
- 错误：400 code 4003（status 非法）；400 code 4004（不存在）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:85-90`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:197-220`；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:46`；前端 `src/client-web/src/api/quickNotes.ts:66-68`
- 备注：前端语义固定为"恢复回收集箱"——恒传 `status='inbox'`（`src/client-web/src/components/quick-notes/QuickNoteDialog.tsx:314`）；服务端能力比前端用到的更宽。

### DELETE /api/v1/quick-notes/{id}
- 用途：删除快速记录（软删除，级联软删其附件元数据）。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog「删除」）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 记录 ID |
- Query 参数：无
- Body：无
- 响应 data：`string`，值固定 `"已删除"`；HTTP 200（**不是** 204）。
- 错误：400 code 4004（不存在）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:92-99`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteService.cs:222-234`；前端 `src/client-web/src/api/quickNotes.ts:70-72`
- 备注：仅软删（`DeletedAt=now`）；注意与 MCP 客户端 DELETE（204 无响应体）形态不同。

## 附件

### POST /api/v1/quick-notes/attachments
- 用途：上传附件（先传后绑：上传成功拿 id，再在创建/更新记录时通过 attachmentIds 或 markdown 链接绑定）。
- 认证：JWT
- Web 前端使用：是（QuickNoteEditor/QuickNoteDialog 文件选择与粘贴上传）
- Path 参数：无
- Query 参数：无
- Body：`multipart/form-data`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | file | file (binary) | 是 | 文件字段名必须为 `file`；文件名为空报 400 code 4007 |
- 响应 data：`QuickNoteAttachmentUpload`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | id | string (uuid) | 附件 ID（后续绑定用） |
  | fileName | string | 文件名（已取 Path.GetFileName） |
  | contentType | string | MIME 类型，缺省 `application/octet-stream` |
  | sizeBytes | number | 字节数（取自上传分片长度） |
  | downloadUrl | string | `/api/v1/quick-notes/attachments/{id}/download` |
  | previewUrl | string \| null | 图片类（`image/*`）= downloadUrl，否则 null |
- 错误：非 multipart → 400 code 400「需要 multipart/form-data 请求」；缺 file 字段 → 400 code 400「缺少 file 文件字段」；multipart 解析失败 → 400 code 400「multipart/form-data 请求无效」；400 code 4007（文件名为空）/4008（大小为负）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:101-130`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteAttachmentService.cs:17-59`（对象键 `quick-notes/{userId}/{id}/{文件名}` :39）；DTO `src/modules/Pim.Module.QuickNotes/DTOs/QuickNoteDtos.cs:48-54`；前端 `src/client-web/src/api/quickNotes.ts:74-82`
- 备注：附件 v2 统一存到用户自己的 OneDrive（`QuickNotesModule.cs:24-27`），未绑定 OneDrive 时由存储层抛出明确领域错误，UI 需提示绑定状态。

### GET /api/v1/quick-notes/attachments/{id}/download
- 用途：下载附件内容（302 直链优先，回退服务器代理）。
- 认证：JWT
- Web 前端使用：是（QuickNoteEditor 内嵌图片/附件 blob 加载，quickNoteAttachmentBlobUrls）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 附件 ID |
- Query 参数：无
- Body：无
- 响应 data：两种形态（成功均**无** ApiResponse 封装）：
  1. 存储后端能给出 OneDrive 预授权直链 → HTTP **302**，`Location` 指向直链，无响应体；
  2. 拿不到直链 → 服务器代理输出文件字节流，`Content-Type` 为存储时记录的 MIME，`Content-Disposition` 带原始文件名。
- 错误：400 code 4006「附件不存在」；403 code 40301「无权访问该附件」（非本人附件）。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:132-147`（`Results.Redirect` 默认 302）；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteAttachmentService.cs:61-97`；前端 `src/client-web/src/api/quickNotes.ts:84-86`（`apiDownloadBlob` 走 fetch blob，自动跟随重定向，`src/client-web/src/api/client.ts:157-160`）
- 备注：前端编辑器通过该端点把图片附件转成 blob URL 内嵌到 TipTap 文档（`src/client-web/src/components/quick-notes/QuickNoteEditor.tsx:24, 64, 78`）。重建时注意：不要假设 200 响应——直链形态是 302。

### DELETE /api/v1/quick-notes/attachments/{id}
- 用途：删除附件（先删 OneDrive 远端对象，成功后软删本地元数据）。
- 认证：JWT
- Web 前端使用：是（QuickNoteDialog 移除附件）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 附件 ID |
- Query 参数：无
- Body：无
- 响应 data：`string`，值固定 `"已删除"`；HTTP 200。
- 错误：400 code 4006「附件不存在」；403 code 40301「无权访问该附件」。
- 来源：后端 `src/modules/Pim.Module.QuickNotes/QuickNotesModule.cs:149-156`；服务 `src/modules/Pim.Module.QuickNotes/Services/QuickNoteAttachmentService.cs:99-115`；前端 `src/client-web/src/api/quickNotes.ts:70-72`（复用 deleteQuickNote 同款 apiDelete 封装）
- 备注：远端删除成功（或远端已不存在）后才收敛本地状态（服务内注释 108-110），避免用户 OneDrive 里残留文件。

## MCP 客户端管理

### GET /api/v1/mcp/clients
- 用途：列出当前用户创建的 MCP 客户端及其在线/调用统计。
- 认证：JWT
- Web 前端使用：是（MCP 设置页 McpSettingsPage 客户端列表，10 秒轮询）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`McpClient[]`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | [] .id | string (uuid) | 客户端 ID |
  | [] .name | string | 客户端名称 |
  | [] .status | string | 枚举：`active` / `revoked` |
  | [] .tokenPrefix | string | token 展示前缀（前 12 字符，形如 `pim_mcp_` 开头） |
  | [] .permissions | object | `{ read: { <toolName>: boolean }, write: { <toolName>: boolean } }` |
  | [] .createdAt | string (ISO8601) | 创建时间 |
  | [] .revokedAt | string \| null | 吊销时间 |
  | [] .lastSeenAt | string \| null | 最近调用时间 |
  | [] .callCount | number | 累计调用次数 |
  | [] .writeCallCount | number | 累计写调用次数 |
  | [] .lastTool | string \| null | 最近调用的工具名 |
  | [] .online | boolean | `lastSeenAt` 距今 ≤5 分钟 |
  | [] .createdByUsername | string \| null | 创建者用户名（仅列表接口回填） |
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:36-44`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:64-76, 296-313`（online 判定 ：311）；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:15-28`；前端 `src/client-web/src/api/mcp.ts:14-17`；类型 `src/client-web/src/types/index.ts:1636-1664`
- 备注：仅返回 `CreatedBy == 当前用户` 的客户端，按 createdAt 倒序；不返回任何 token 明文。

### POST /api/v1/mcp/clients
- 用途：新建 MCP 客户端并生成 token（**token 仅此一次返回**）。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage「新建客户端」EditorDrawer）
- Path 参数：无
- Query 参数：无
- Body：JSON `McpCreateClientRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 是 | 客户端名称；trim 后非空、≤80 字符、同用户内唯一，否则 40001/40002/40003 |
- 响应 data：`McpClientCreateResult`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | client | McpClient | 同 GET /mcp/clients 列表项（createdByUsername 为 null） |
  | token | string | 明文 token：`pim_mcp_` + 48 位 URL-safe 小写十六进制；**只在本次响应出现，服务端只存 SHA-256 哈希** |
- 错误：400 code 40001「客户端名称不能为空」/ 40002「客户端名称不能超过 80 字符」/ 40003「客户端名称已存在」。
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:46-57`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:36-62`；token 生成 `src/modules/Pim.Module.Mcp/Services/McpTokenService.cs:9-28`；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:30-33`；前端 `src/client-web/src/api/mcp.ts:19-22`
- 备注：新客户端默认权限为 **read 全开 / write 全关**（`src/modules/Pim.Module.Mcp/Services/McpToolCatalog.cs:17-26`）。前端拿到 token 后展示一次性提示「Token 只显示这一次，请立即保存。之后只能吊销重建」并附带 mcp.json 连接配置示例（`src/client-web/src/pages/McpSettingsPage.tsx:28-35, 376-396`）：
  ```json
  {
    "mcpServers": {
      "pim": {
        "type": "http",
        "url": "https://<host>:<port>/mcp",
        "headers": { "Authorization": "Bearer <PIM_MCP_TOKEN>" }
      }
    }
  }
  ```

### PUT /api/v1/mcp/clients/{id}
- 用途：修改客户端名称和/或工具权限矩阵（部分更新，按 tool 键合并）。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage 编辑抽屉 + PermissionEditor 权限矩阵）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 客户端 ID |
- Query 参数：无
- Body：JSON `McpClientUpdateRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | name | string | 否 | 传了才改名；校验同新建（40001/40002/40003） |
  | permissions | object | 否 | `{ read?: { <toolName>: boolean }, write?: { <toolName>: boolean } }`；未知 section/tool 被丢弃，同一 section 内按 tool 键合并、不会清掉未提及的工具 |
- 响应 data：`McpClient`（字段同 GET /mcp/clients 列表项）
- 错误：400 code 40401「客户端不存在」；403 code 40301「无权操作该客户端」（非本人创建）；改名校验 40001/40002/40003。
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:59-70`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:78-128`（权限合并 102-124、白名单清洗 275-294）；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:35-37`；前端 `src/client-web/src/api/mcp.ts:24-27`
- 备注：整个 `read`/`write` section 缺省时该 section 原样保留；权限目录以 `McpToolCatalog`（101 个读工具 + 49 个写工具）为唯一事实来源。

### POST /api/v1/mcp/clients/{id}/revoke
- 用途：吊销客户端 token（立即失效）。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage「吊销」操作）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 客户端 ID |
- Query 参数：无
- Body：无
- 响应 data：`McpClient`（`status=revoked`，`revokedAt=now`）
- 错误：400 code 40401「客户端不存在」；403 code 40301「无权操作该客户端」。
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:72-82`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:130-143`；前端 `src/client-web/src/api/mcp.ts:29-32`
- 备注：幂等——已是 `revoked` 的客户端重复调用直接返回现状、不刷新 revokedAt（`McpClientService.cs:136-141`）。

### DELETE /api/v1/mcp/clients/{id}
- 用途：彻底删除客户端记录。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage「删除」）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | id | string (uuid) | 是 | 客户端 ID |
- Query 参数：无
- Body：无
- 响应 data：无响应体；HTTP **204 No Content**。
- 错误：400 code 40401「客户端不存在」；403 code 40301「无权操作该客户端」。
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:84-94`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:145-153`（物理删除 `_db.Remove`）；前端 `src/client-web/src/api/mcp.ts:34-36`
- 备注：与快速记录 DELETE（200 + `"已删除"`）不同，这里是硬删除 + 204，前端重建注意区分。

### GET /api/v1/mcp/activity
- 用途：查看本用户最近的 MCP 工具调用流水（实时监控面板）。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage 活动流水区，10 秒轮询）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`McpActivityEntry[]`（时间倒序，进程内存队列，最多 100 条，重启即清空）
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | [] .timestamp | string (ISO8601) | 调用时间 |
  | [] .clientName | string | 客户端名称（未知时 `unknown`） |
  | [] .toolName | string | 工具名 |
  | [] .statusCode | number | 执行结果码：200 成功；其余为工具异常透出的 HTTP 语义码（401/403/500 等） |
  | [] .durationMs | number | 耗时（毫秒） |
  | [] .argumentsSummary | string | 入参 JSON 序列化摘要，截断至 120 字符；无参时为空串 |
  | [] .ownerUserId | string (uuid) | 归属用户 ID（后端 DTO 有此字段，前端类型未声明） |
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:96-102`；服务 `src/modules/Pim.Module.Mcp/Services/McpToolExecutor.cs:63-67, 106-128`（上限与截断 ：27-28）；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:54-61`；前端 `src/client-web/src/api/mcp.ts:43-46`；类型 `src/client-web/src/types/index.ts:1676-1683`
- 备注：数据仅存内存（ConcurrentQueue），不是持久化日志；unknown tool 的调用也会记录（statusCode 500，`McpToolExecutor.cs:79`）。

### GET /api/v1/mcp/catalog
- 用途：获取 MCP 工具目录（权限编辑器数据源）。
- 认证：JWT
- Web 前端使用：是（McpSettingsPage + PermissionEditor 渲染权限矩阵）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`McpCatalog`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | read | McpToolInfo[] | 读工具列表，101 个 |
  | write | McpToolInfo[] | 写工具列表，49 个 |
  | read[]/write[].name | string | 工具名（如 `get_calendar_layers`、`create_quick_note`） |
  | read[]/write[].group | string | 工具分组（如 `calendar`、`quicknotes`） |
  | read[]/write[].description | string | 英文一句话描述 |
  | read[]/write[].isWrite | boolean | 是否写工具 |
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:104-106`；服务 `src/modules/Pim.Module.Mcp/Services/McpClientService.cs:270-273`、目录 `src/modules/Pim.Module.Mcp/Services/McpToolCatalog.cs:12-14, 43-245`（quicknotes 组 ：131-133, 208-212）；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:11-13`；前端 `src/client-web/src/api/mcp.ts:38-41`
- 备注：合计 150 个工具（与嵌入契约 `src/modules/Pim.Module.Mcp/Contract/mcp-tools.json` 的 150 条一致；DTO 注释写"151-tool"为陈旧描述）。目录是静态代码定义，非实时探测。

### POST /api/v1/mcp/verify
- 用途：MCP 服务器在每次工具调用前校验客户端 token 并换取受限 REST JWT（内部专用）。
- 认证：**无 JWT**——专用 MCP 客户端 token 认证：`Authorization: Bearer <pim_mcp_...>`；期望调用方是内网中的 MCP 服务器进程，Web 前端不使用。
- Web 前端使用：否（消费方：MCP 服务器 in-process 流水线 McpToolExecutor → VerifyAsync；前端 mcp.ts 无对应封装）
- Path 参数：无
- Query 参数：无
- Body：JSON `McpVerifyRequest`
  | 字段 | 类型 | 必填 | 说明 |
  | --- | --- | --- | --- |
  | tool | string | 是 | 本次要调用的工具名；缺失 → 400「tool is required」 |
  | paramsSummary | string | 否 | 入参摘要（≤500 字符），仅写调用时进审计元数据 |
- 响应 data：`McpVerifyResult`
  | 字段 | 类型 | 说明 |
  | --- | --- | --- |
  | clientId | string (uuid) | 客户端 ID |
  | clientName | string | 客户端名称 |
  | userId | string (uuid) | 归属用户 ID |
  | permissions | object | 客户端当前完整权限矩阵（read/write 两段） |
  | accessToken | string | 短效（2 分钟）用户 JWT，claims 含 `mcp_tool` + `mcp_client_id`，用于 REST 直通 |
  | isWrite | boolean | 是否写工具 |
- 错误（均以 ApiResponse 封装）：
  | 场景 | HTTP | code | 说明 |
  | --- | --- | --- | --- |
  | IP 失败次数超限（20 次 / 5 分钟滑动窗口） | 429 | 42901 | 「too many attempts」 |
  | 缺少 Bearer 头 | 401 | 40101 | 「missing bearer token」 |
  | token 无效 / 客户端已吊销 / 用户不存在或停用 | 401 | 40101 | 「invalid or revoked token」 |
  | tool 不在目录中 | 400 | 400 | 「unknown tool: <tool>」 |
  | 该工具未授权 | 403 | 40301 | 「permission denied: <tool>」 |
- 来源：后端 `src/modules/Pim.Module.Mcp/McpModule.cs:111-144`（限流器 `VerifyThrottle` :170-223，20 次/5 分钟 ：177-180）、`src/modules/Pim.Module.Mcp/Services/McpClientService.cs:160-268`（2 分钟 JWT ：192-201、统计与审计 ：205-259）、outcome 映射 `McpClientService.cs:316-328`；DTO `src/modules/Pim.Module.Mcp/DTOs/McpDtos.cs:40-51`
- 备注：仅统计 401/403 失败进限流窗口，纯参数错误（400）不计入（`McpModule.cs:133-136`）。写调用审计 action 为 `mcp.write.<tool>`。限流按 `RemoteIpAddress`（信任反向代理重写后的值），不解析原始 X-Forwarded-For 以防伪造（`McpModule.cs:147-153`）。

## MCP 协议端点

### POST /mcp（MCP Streamable HTTP 端点）
- 用途：MCP Streamable HTTP 传输端点——POST 承载 JSON-RPC 2.0 消息（initialize / tools/list / tools/call 等），供外部 MCP 客户端（Claude Code、Codex、Hermes 等）接入 PIM 全部 150 个工具。
- 认证：MCP token（Bearer）——见下方「认证与错误行为」；路径可由配置 `MCP:Path` 调整（默认 `/mcp`），总开关 `MCP:Enabled`（默认 true，关闭时整个端点族不映射）。
- Web 前端使用：否（消费方：外部 MCP 客户端，连接配置见 POST /api/v1/mcp/clients 备注；另有 stdio 形态 `dotnet Pim.Api.dll --mcp-stdio` 复用同一工具注册表）
- Path 参数 / Query 参数 / Body：无路径参数；POST Body 为 JSON-RPC 2.0 消息（`tools/list` 无参数；`tools/call` 参数为 `{ name, arguments }`）。服务端身份：`ServerInfo = { name: "pim-mcp-server", version: "2.0.0" }`，仅声明 tools 能力。
- 响应 data：POST 成功返回 JSON-RPC 结果（`tools/list` 返回嵌入契约 `Contract/mcp-tools.json` 的 150 条 name/description/inputSchema 原文；`tools/call` 返回工具执行结果文本）；GET 成功返回 `text/event-stream`（SSE）。
- 认证与错误行为（全部来自源码）：
  | 场景 | HTTP | 行为 |
  | --- | --- | --- |
  | 请求 `/mcp/`（带尾斜杠，任意方法） | 308 | 永久重定向到 `/mcp`，保留 query string |
  | 无 `Authorization: Bearer` 头（OPTIONS 除外） | 401 | JSON `{ code: 40101, message: "missing bearer token", data: null }`（注意：该匿名对象无 `timestamp` 字段） |
  | OPTIONS 预检 | — | 放行给 CORS 中间件处理，不做 bearer 拦截 |
  | 无会话 GET / 未处理的 DELETE、PATCH | 400 | JSON `{ code: 40001, message: "Session ID or valid JSON-RPC payload required for MCP endpoint" }`（兜底路由，防止落入 SPA 返回 HTML） |
  | `tools/call` 携带的 token 无效/吊销、工具未授权 | JSON-RPC error | 内部先走 `POST /api/v1/mcp/verify` 同一套校验（40101/40301 语义），以 `McpToolAuthException` 透出 |
- 会话与执行流（源码事实）：
  - 会话模式为 **Stateless**（SDK 默认，保持不变以兼容 Streamable HTTP；`McpServerFactory.cs:31-32`），SDK 为 `ModelContextProtocol.AspNetCore` 2.2.0（`src/modules/Pim.Module.Mcp/Pim.Module.Mcp.csproj:8`），`app.MapMcp(mcpPath)` 注册于 `McpServerBootstrap.cs:61`。
  - 每次工具调用的鉴权：从请求 Bearer 头提取 token → `McpClientService.VerifyAsync`（校验 `mcp_clients.token_hash` + `status=active` + 该工具权限）→ 签发 2 分钟、claims 带 `mcp_tool`/`mcp_client_id` 的受限 JWT → 通过进程内管道（同进程同中间件链，含 JWT 认证与 `McpScopedTokenMiddleware`）转发到 REST 接口（`src/modules/Pim.Module.Mcp/Services/McpToolExecutor.cs:280-299`、`Services/McpInProcessClient.cs:8-14`）。
  - 受限 JWT 的 REST 直通受 `McpScopedTokenMiddleware` 约束：写工具只能调用映射好的精确端点，读工具只能 GET /api/* 加白名单的读语义 POST 端点；越权返回 403 `{code:40302}`（`src/Pim.Api/Middleware/McpScopedTokenMiddleware.cs:23-53`、`Services/McpReadEndpointPolicy.cs:14-20`）。
  - 工具执行异常时记录活动流水（见 GET /api/v1/mcp/activity），HTTP 直通超时上限 `MCP:DispatchTimeout` 默认 60 秒（`src/modules/Pim.Module.Mcp/McpOptions.cs:18`）。
- 来源：后端 `src/modules/Pim.Module.Mcp/Services/McpServerBootstrap.cs:25-72`（bearer 守卫 37-58、308 :40-45、MapMcp :61、400 兜底 64-66）、`src/modules/Pim.Module.Mcp/Services/McpServerFactory.cs:19-42, 57-81`、`src/modules/Pim.Module.Mcp/McpOptions.cs:12-18`、`src/Pim.Api/Program.cs:224-225, 417-419`、配置样例 `src/Pim.Api/appsettings.json:2-5`；前端无调用方（`src/client-web/src/api/mcp.ts` 全文件仅覆盖 /api/v1/mcp 管理接口）
- 备注：文档头中"普通 JWT 或 MCP 签发的受限 token"是守卫层面的描述——bearer 守卫只检查头格式；**实际工具调用要求 `pim_mcp_` 前缀的 MCP 客户端 token**（`VerifyAsync` 按存储的 token 哈希匹配，普通用户 JWT 不会命中，`McpClientService.cs:167-175`）。`/mcp` 的 GET（SSE 事件流）由 SDK 处理，仓库源码未见自定义 Last-Event-ID/恢复逻辑（源码中未定位，勿虚构）。执行统计、权限矩阵均在 verify 时原子更新，`lastSeenAt`/`callCount` 由此回写。
