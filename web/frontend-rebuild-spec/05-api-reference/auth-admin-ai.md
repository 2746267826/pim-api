# 认证、用户管理与 AI 域接口规格（/api/v1/auth、/api/v1/admin、/api/v1/ai）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域说明：auth 组中 `/login`、`/refresh` 为匿名；`/register` 默认关闭（仅 `users` 表为空时可用，否则 403 code=40304）；`/me`、`/password` 要求 JWT；admin 组整体要求 Admin（AdminEndpoints.cs:15-16）；ai 组整体要求 Admin（AiEndpoints.cs:12-13）。序列化约定：ASP.NET Core 默认 camelCase；`AiRequestStatus` 枚举标注 `[JsonConverter(typeof(JsonStringEnumConverter))]`（AiEnums.cs:12），序列化为 PascalCase 字符串（`Succeeded`/`Failed`/`Blocked`/`TimedOut`/`FailedValidation`）。

---

## 认证（Auth）

### POST /api/v1/auth/register
- 用途：注册新用户并直接返回令牌（注册即登录）。**默认关闭**：仅当 `users` 表为空（全新部署引导，首个用户为 `admin`）或后端开启 `Auth:AllowPublicRegistration` 时可用；库非空且未开开关时返回 403 code=40304（`公开注册已关闭，请联系管理员创建账号`），且不写任何行。
- 认证：匿名
- Web 前端使用：**否**（前端已移除全部注册入口，账号由管理员创建；见 REQ-11）
- Path 参数：无
- Query 参数：无
- Body（`RegisterRequest`，AuthDtos.cs:5-10）：
  | 字段 | 类型 | 必填 | 说明 |
  | username | string | 是 | ≤50 字符；为空返回 400 code=40001 |
  | email | string | 是 | ≤255 字符且须通过 EmailAddressAttribute 校验，否则 400 code=40002/40003；入库前转小写（AuthEndpoints.cs:27） |
  | password | string | 是 | 8~100 字符，否则 400 code=40004/40007 |
  | displayName | string | 否 | ≤100 字符（超长 400 code=40008）；缺省时取 username（AuthEndpoints.cs:28-30） |
- 响应 data：HTTP 201，`AuthResponse`（AuthDtos.cs:21-26）
  | 字段 | 类型 | 说明 |
  | accessToken | string | JWT 访问令牌 |
  | refreshToken | string | 刷新令牌（SHA256 哈希后落库，有效期 7 天，AuthEndpoints.cs:88-93） |
  | expiresAt | string (ISO-8601) | 访问令牌过期时间 = 签发时刻 + 15 分钟 |
  | user.id | string (UUID) | 用户 ID |
  | user.username | string | 用户名 |
  | user.displayName | string | 显示名 |
  | user.role | string | `admin` \| `user`；`users` 表为空时首个注册用户为 `admin`（AuthEndpoints.cs、AdminBootstrap.DetermineRegistrationRoleAsync）；表非空时需管理员创建（或后端开启注册开关）；并发首注册竞争中已有更早管理员则降级为 `user` |
- 错误：403 code=40304 `公开注册已关闭，请联系管理员创建账号`（关闭判定**先于**参数校验：库非空且未开开关时，非法载荷同样返回 403 而不是 400）；400（40001~40008 参数校验）；409 code=01003 `用户名已存在`；409 code=01004 `邮箱已存在`；并发唯一索引冲突 409 code=01003 `用户名已存在或邮箱已存在`
- 来源：后端 `src/Pim.Api/Endpoints/AuthEndpoints.cs:19`；DTO `src/Pim.Api/DTOs/AuthDtos.cs:5`；前端 `src/client-web/src/auth/AuthContext.tsx:61`（请求 `src/client-web/src/api/client.ts:141`）
- 备注：前端类型 `AuthResponse`（types/index.ts:8-14）与后端一致。

### POST /api/v1/auth/login
- 用途：用户名或邮箱 + 密码登录，换取令牌。
- 认证：匿名；IP 限流——同一 IP 近 15 分钟内失败次数 ≥5 时直接返回 429 并附响应头 `Retry-After: 900`（AuthEndpoints.cs:117-126）
- Web 前端使用：是（登录页，经 AuthContext.login）
- Path 参数：无
- Query 参数：无
- Body（`LoginRequest`，AuthDtos.cs:12-15）：
  | 字段 | 类型 | 必填 | 说明 |
  | username | string | 是 | 用户名或邮箱均可（`u.Username == request.Username || u.Email == request.Username`，AuthEndpoints.cs:129-130） |
  | password | string | 是 | 密码明文（bcrypt 校验） |
- 响应 data：HTTP 200，`AuthResponse`（同 register：accessToken/refreshToken/expiresAt/user，expiresAt = 签发 + 15 分钟，refreshToken 7 天）
- 错误：429（限流，带 `Retry-After: 900`，空 body）；403 code=40030 `账号已停用，请联系管理员`（用户存在且密码正确但 isActive=false，AuthEndpoints.cs:137-141）；401 空 body（用户不存在或密码错误，AuthEndpoints.cs:153）
- 来源：后端 `src/Pim.Api/Endpoints/AuthEndpoints.cs:104`；前端 `src/client-web/src/auth/AuthContext.tsx:47`
- 备注：登录成功/失败均写入 `login_attempts` 表用于限流统计（AuthEndpoints.cs:145-150、156-161）。

### POST /api/v1/auth/refresh
- 用途：用 refreshToken 换取新令牌对（轮换式：旧 refreshToken 立即吊销）。
- 认证：匿名
- Web 前端使用：是（api/client.ts 在任意请求收到首个 401 时自动调用，非用户直接触发）
- Path 参数：无
- Query 参数：无
- Body（`RefreshRequest`，AuthDtos.cs:17-19）：
  | 字段 | 类型 | 必填 | 说明 |
  | refreshToken | string | 是 | 未吊销且未过期的刷新令牌（按 SHA256 哈希匹配库记录，AuthEndpoints.cs:198-203） |
- 响应 data：HTTP 200，`AuthResponse`（同 register；新 refreshToken 有效期 7 天）
- 错误：401 空 body——token 查不到/已吊销/已过期（AuthEndpoints.cs:205-206），或对应用户不存在/已停用（AuthEndpoints.cs:212）
- 来源：后端 `src/Pim.Api/Endpoints/AuthEndpoints.cs:192`；前端 `src/client-web/src/api/client.ts:77`（调用点 client.ts:114-118、401 触发点 client.ts:248-262）
- 备注（前端刷新流程）：旧 token 在校验通过后立即置 `RevokedAt`（AuthEndpoints.cs:209），实现单次有效轮换。前端在 `apiFetchResponse` 中对任意请求的 401 调用 `refreshAccessToken()`，成功后用新 accessToken 重放原请求；失败则清除令牌并广播登出（client.ts:248-262）。并发保护：模块级单一共享 `refreshPromise`，并发 401 只发起一次 refresh，其余等待同一 Promise（client.ts:78、129-134）；刷新结果仅在 `generation` 与 refreshToken 未变化时生效（client.ts:122-124），避免过期响应覆盖新状态。Android 内嵌模式（/embed/android/）改走 bridge.refreshToken，不走该 HTTP 端点（client.ts:81-108）。

### GET /api/v1/auth/me
- 用途：获取当前登录用户信息（页面刷新后恢复用户名与角色）。
- 认证：JWT（`.RequireAuthorization()`，AuthEndpoints.cs:249）
- Web 前端使用：是（AuthContext 初始化恢复会话）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`UserInfo`（AuthDtos.cs:28-33）
  | 字段 | 类型 | 说明 |
  | id | string (UUID) | 用户 ID |
  | username | string | 用户名 |
  | displayName | string | 显示名 |
  | role | string | `admin` \| `user` |
- 错误：401（无有效 JWT / 用户不存在 / 已停用，AuthEndpoints.cs:241-245）
- 来源：后端 `src/Pim.Api/Endpoints/AuthEndpoints.cs:236`；前端 `src/client-web/src/auth/AuthContext.tsx:37`
- 备注：前端显示名优先取 displayName，为空回退 username（AuthContext.tsx:40）。

### POST /api/v1/auth/password
- 用途：登录用户修改**自己**的密码。改密码后**只踢其它设备**，当前设备保持登录（请求体携带的 `refreshToken` 即「当前设备」）。
- 认证：JWT（`.RequireAuthorization()`）
- Web 前端使用：是（设置中心 → 账号安全，AccountSecurityPage）
- Path 参数：无
- Query 参数：无
- Body（`ChangePasswordRequest`）：
  | 字段 | 类型 | 必填 | 说明 |
  | currentPassword | string | 是 | 当前密码，错误返回 400 code=40063 `当前密码不正确` |
  | newPassword | string | 是 | 8~100 字符，否则 400 code=40004 / 40007 |
  | refreshToken | string | 是 | **当前设备**的 refresh token 明文（前端在**提交时**从令牌存储现取，不得缓存）。缺失/为空/不属于本人/已撤销/已过期 → 400 code=40065 `会话标识无效，请重新登录后再试`，且**不撤销任何会话** |
- 响应 data：HTTP 200，string（提示文案）
- 错误：401（无登录态，空 body）；400 code=40063 `当前密码不正确`；400 code=40064 `新密码不能与当前密码相同`；400 code=40065 `会话标识无效，请重新登录后再试`；400 code=40004 / 40007（新密码长度）
- 会话语义：该用户未撤销的 refresh token 中，除请求携带的那条外全部置为已撤销
- 备注：判定顺序为「新密码长度 → refreshToken → 当前密码 → 新旧是否相同」；access token 不做即时吊销（最长 15 分钟自然过期），这是已知边界。

## 用户管理（Admin）

### GET /api/v1/admin/users
- 用途：管理员列出全部用户（按创建时间升序）。
- 认证：Admin（admin 组整体 `Roles = "admin"`，AdminEndpoints.cs:15-16）
- Web 前端使用：是（管理用户页 /settings/users，AdminUsersPage）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`AdminUserDto[]`（AdminUserService.cs:9-16）
  | 字段 | 类型 | 说明 |
  | id | string (UUID) | 用户 ID |
  | username | string | 用户名 |
  | email | string | 邮箱（已小写） |
  | displayName | string\|null | 显示名 |
  | role | string | `admin` \| `user` |
  | isActive | boolean | 是否启用 |
  | createdAt | string (ISO-8601) | 创建时间 |
- 来源：后端 `src/Pim.Api/Endpoints/AdminEndpoints.cs:18`、`src/Pim.Infrastructure/Auth/AdminUserService.cs:47-52`；前端 `src/client-web/src/api/admin.ts:19`（类型 admin.ts:4-12）
- 备注：排序 `CreatedAt, Id`（AdminUserService.cs:49-50）；前端 `AdminUser` 接口与后端一一对应。

### POST /api/v1/admin/users
- 用途：管理员创建用户（公开注册收敛后的唯一建号入口）。
- 认证：Admin
- Web 前端使用：是（用户管理页右上「新建用户」居中弹窗）
- Path 参数：无
- Query 参数：无
- Body（`CreateUserRequest`）：
  | 字段 | 类型 | 必填 | 说明 |
  | username | string | 是 | ≤50 字符（40001/40005） |
  | email | string | 是 | ≤255 且格式合法（40002/40003/40006）；入库前转小写 |
  | displayName | string\|null | 否 | ≤100 字符（40008）；留空回落为 username |
  | password | string | 是 | 8~100 字符（40004/40007） |
  | role | string\|null | 否 | 只接受 `admin` / `user`，其他值 400 code=40041；**缺省或为空按 `user`**（不默认创建管理员） |
- 响应 data：HTTP 201，`AdminUserDto`（字段同 GET /admin/users 列表项）
- 错误：401（无登录态）；403（非 admin）；400（40001~40008 参数校验、40041 无效角色）；409 code=01003 `用户名已存在`；409 code=01004 `邮箱已存在`；500
- 备注：新用户 `is_active = true`、`deleted_at` 为空；建号与审计同一次提交。

### POST /api/v1/admin/users/{id}/role
- 用途：修改目标用户角色（admin ↔ user）。
- 认证：Admin
- Web 前端使用：是（管理用户页角色切换）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 目标用户 ID（路由约束 `{id:guid}`） |
- Query 参数：无
- Body（`UpdateUserRoleRequest`，AdminDtos.cs:5-7）：
  | 字段 | 类型 | 必填 | 说明 |
  | role | string | 是 | 仅 `admin` \| `user`（其他值 400 code=40041） |
- 响应 data：HTTP 200，`AdminUserDto`（字段同 GET /admin/users 列表项）
- 错误：404 code=40040 `用户不存在`；400 code=40041 `无效的角色，仅支持 admin / user`；400 code=40042 `至少需要保留一名管理员`（目标为有效管理员且降级后系统无其他有效管理员，AdminUserService.cs:69-75）；400 code=40043 `操作失败`（兜底）
- 来源：后端 `src/Pim.Api/Endpoints/AdminEndpoints.cs:21`、`src/Pim.Infrastructure/Auth/AdminUserService.cs:54-86`；前端 `src/client-web/src/api/admin.ts:24`
- 备注：目标角色与现值相同直接成功返回（AdminUserService.cs:63-64）；停用状态下的管理员降级不受 LastAdminProtected 拦截（AdminUserService.cs:67-68 注释）；角色变更写审计 `admin.user.change_role`（AdminUserService.cs:82-83）。

### POST /api/v1/admin/users/{id}/status
- 用途：启用/停用目标用户账号。
- 认证：Admin
- Web 前端使用：是（管理用户页启停开关）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 目标用户 ID（路由约束 `{id:guid}`） |
- Query 参数：无
- Body（`UpdateUserStatusRequest`，AdminDtos.cs:9-11）：
  | 字段 | 类型 | 必填 | 说明 |
  | isActive | boolean | 是 | true=启用，false=停用 |
- 响应 data：HTTP 200，`AdminUserDto`（字段同 GET /admin/users 列表项）
- 错误：404 code=40040 `用户不存在`；400 code=40042 `至少需要保留一名有效管理员`（停用最后一名有效管理员，AdminUserService.cs:100-106）；400 code=40043 `操作失败`（兜底）
- 来源：后端 `src/Pim.Api/Endpoints/AdminEndpoints.cs:43`、`src/Pim.Infrastructure/Auth/AdminUserService.cs:88-116`；前端 `src/client-web/src/api/admin.ts:33`
- 备注：状态未变化直接成功返回（AdminUserService.cs:94-95）；停用后该用户登录被拒（403 40030）、/auth/me 返回 401；启停写审计 `admin.user.enable`/`admin.user.disable`（AdminUserService.cs:112-113）。

### POST /api/v1/admin/users/{id}/password
- 用途：管理员重置任意用户密码。重置**没有「当前设备」概念**：该用户全部未撤销会话失效（所有设备需重新登录）。
- 认证：Admin
- Web 前端使用：是（用户管理页每行「重置密码」居中弹窗）
- Path 参数：id（UUID，目标用户；不存在 → 404 code=40040 `用户不存在`）
- Query 参数：无
- Body（`ResetPasswordRequest`）：
  | 字段 | 类型 | 必填 | 说明 |
  | newPassword | string | 是 | 8~100 字符，否则 400 code=40004 / 40007（校验失败不落库） |
- 响应 data：HTTP 200，`AdminUserDto`（被重置用户的当前信息）
- 错误：401；403（非 admin）；404 code=40040；400 code=40004/40007；500
- 备注：对自己执行重置同样会让自己的全部会话失效（与其他用户行为一致，无特殊分支）。

## AI（Admin）

### GET /api/v1/ai/status
- 用途：查看 AI 网关配置与健康概览。
- 认证：Admin（ai 组整体 `Roles = "admin"`，AiEndpoints.cs:12-13）
- Web 前端使用：是（AI 设置页 /settings/ai，AiSettingsPage 状态卡）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`AiStatusDto`（AiDtos.cs:56-63，映射 AiUsageService.cs:12-31）
  | 字段 | 类型 | 说明 |
  | enabled | boolean | AI 功能总开关（AiOptions.Enabled） |
  | provider | string | 提供方（当前为 litellm） |
  | baseUrl | string | 网关基地址 |
  | defaultModel | string | 默认模型 |
  | lastHealthCheckAt | string\|null (ISO-8601) | 最近健康检查时间（ai_provider_settings 表 litellm 行） |
  | lastError | string\|null | 最近健康检查错误 |
  | recentSuccessfulCallAt | string\|null (ISO-8601) | 最近一次 status=succeeded 的请求开始时间 |
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:15`；前端 `src/client-web/src/api/ai.ts:32`（路径常量 ai.ts:5-12）、调用方 `src/client-web/src/pages/AiSettingsPage.tsx:27`
- 备注：前端类型 `AiStatus`（types/index.ts:1291-1300）与后端一致。

### POST /api/v1/ai/test
- 用途：向 AI 网关发送固定测试消息，验证连通性与响应格式。
- 认证：Admin
- Web 前端使用：是（AI 设置页 AiStatusPanel"测试"按钮）
- Path 参数：无
- Query 参数：无
- Body：无（请求参数由后端固定：Module=system、Purpose=ai.test、消息 "Reply with the word ok."、MaxOutputTokens=32、MaxAttempts=1，AiEndpoints.cs:20-31）
- 响应 data：`AiResult`（AiDtos.cs:30-48）
  | 字段 | 类型 | 说明 |
  | status | string | `Succeeded` \| `Failed` \| `Blocked` \| `TimedOut` \| `FailedValidation`（AiRequestStatus 枚举，AiEnums.cs:13-20） |
  | responseText | string\|null | 模型原始文本响应 |
  | parsedOutputJson | string\|null | 结构化输出 JSON 字符串 |
  | schemaValidationErrors | string[] | Schema 校验错误列表（固定为空数组，非 null） |
  | usage.promptTokens | number\|null | 输入 token 数 |
  | usage.completionTokens | number\|null | 输出 token 数 |
  | usage.totalTokens | number\|null | 总 token 数 |
  | usage.estimatedCost | number\|null | 估算成本 |
  | usage.currency | string\|null | 币种 |
  | logId | string\|null (UUID) | 对应 AI 请求日志 ID |
  | userFacingError | string\|null | 面向用户的错误文案（如校验失败时"AI 响应不符合要求的格式，未生成建议。"） |
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:18`；前端 `src/client-web/src/api/ai.ts:37`、调用方 `src/client-web/src/components/ai/AiStatusPanel.tsx:44`
- 备注：无论 AI 调用成功与否均返回 HTTP 200 + code=0，调用结果体现在 data.status / userFacingError 中（网关层不抛异常路径）。

### GET /api/v1/ai/requests
- 用途：分页查询 AI 请求日志列表（按开始时间倒序）。
- 认证：Admin
- Web 前端使用：是（AI 设置页调用记录列表）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | from | string (ISO-8601) | 否 | 起始时间（StartedAt ≥ from） |
  | to | string (ISO-8601) | 否 | 结束时间（StartedAt ≤ to） |
  | module | string | 否 | 模块精确匹配 |
  | purpose | string | 否 | 用途精确匹配 |
  | sourceObjectType | string | 否 | 来源对象类型精确匹配 |
  | sourceObjectId | string | 否 | 来源对象 ID 精确匹配 |
  | model | string | 否 | 模型精确匹配 |
  | status | string | 否 | 状态过滤，接受 `succeeded` \| `failed` \| `blocked` \| `timedout` \| `timed_out` \| `failedvalidation` \| `failed_validation`（大小写不敏感，AiEndpoints.cs:85-95）；非法值 400 code=400 `AI 请求状态无效。` |
  | userId | string (UUID) | 否 | 按发起用户过滤 |
  | page | number | 否 | 页码，默认 1（<1 按 1 处理，AiUsageService.cs:39） |
  | pageSize | number | 否 | 每页条数，默认 50，钳制 1~200（AiUsageService.cs:40） |
- Body：无
- 响应 data：`PagedResult<AiRequestLogListItemDto>`（PagedResult.cs:3-9；列表项 AiDtos.cs:78-90）
  | 字段 | 类型 | 说明 |
  | items | object[] | 日志列表项数组 |
  | items[].id | string (UUID) | 日志 ID |
  | items[].startedAt | string (ISO-8601) | 开始时间 |
  | items[].module | string | 模块 |
  | items[].purpose | string | 用途 |
  | items[].model | string | 模型 |
  | items[].status | string | `Succeeded` \| `Failed` \| `Blocked` \| `TimedOut` \| `FailedValidation`（存储值 succeeded/failed/blocked/timed_out/failed_validation 映射回枚举，AiUsageService.cs:232-239） |
  | items[].totalTokens | number\|null | 总 token |
  | items[].estimatedCost | number\|null | 估算成本 |
  | items[].durationMs | number\|null | 耗时毫秒 |
  | items[].sourceObjectType | string | 来源对象类型 |
  | items[].sourceObjectId | string | 来源对象 ID |
  | items[].errorSummary | string\|null | 错误摘要（存 ErrorMessage） |
  | totalCount | number | 总条数 |
  | page | number | 当前页 |
  | pageSize | number | 每页条数 |
  | totalPages | number | 总页数（total=0 时为 0，AiUsageService.cs:65） |
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:35`、`src/Pim.Infrastructure/Ai/AiUsageService.cs:33-66`；前端 `src/client-web/src/api/ai.ts:47`、调用方 `src/client-web/src/pages/AiSettingsPage.tsx:39`
- 备注（前后端差异）：前端 `AiRequestFilters` 仅发送 module/purpose/model/status/page/pageSize（ai.ts:14-21），且 status 取值为后端枚举 PascalCase 字符串 'Succeeded' 等（types/index.ts:1289）；Query 侧后端接受的是小写/下划线形式（timedout/timed_out 等），二者大小写不敏感匹配可互通；from/to/sourceObjectType/sourceObjectId/userId 后端支持但前端暂未暴露。

### GET /api/v1/ai/requests/{id}
- 用途：查询单条 AI 请求日志全量详情。
- 认证：Admin
- Web 前端使用：是（AI 设置页日志详情抽屉）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (UUID) | 是 | 日志 ID（路由约束 `{id:guid}`） |
- Query 参数：无
- Body：无
- 响应 data：`AiRequestLogDetailDto`（AiDtos.cs:92-121；继承列表项全部字段外新增：）
  | 字段 | 类型 | 说明 |
  | id | string (UUID) | 日志 ID |
  | userId | string\|null (UUID) | 发起用户 ID |
  | module | string | 模块 |
  | purpose | string | 用途 |
  | sourceObjectType | string | 来源对象类型 |
  | sourceObjectId | string | 来源对象 ID |
  | provider | string | 提供方 |
  | model | string | 模型 |
  | liteLlmRequestId | string\|null | LiteLLM 请求 ID |
  | correlationId | string | 关联 ID |
  | status | string | 同列表项 status 枚举 |
  | attemptNumber | number | 当前尝试次数 |
  | maxAttempts | number | 最大尝试次数 |
  | startedAt | string (ISO-8601) | 开始时间 |
  | finishedAt | string\|null (ISO-8601) | 结束时间 |
  | durationMs | number\|null | 耗时毫秒 |
  | requestMessagesJson | string | 请求消息 JSON |
  | requestPayloadJson | string | 请求负载 JSON |
  | responseRawJson | string | 原始响应 JSON |
  | responseText | string\|null | 响应文本 |
  | parsedOutputJson | string\|null | 结构化输出 JSON |
  | schemaName | string\|null | Schema 名称 |
  | schemaVersion | string\|null | Schema 版本 |
  | schemaJsonSnapshot | string\|null | Schema 快照 JSON |
  | schemaValidationErrorsJson | string | 校验错误 JSON 数组字符串 |
  | usage.promptTokens | number\|null | 输入 token |
  | usage.completionTokens | number\|null | 输出 token |
  | usage.totalTokens | number\|null | 总 token |
  | usage.estimatedCost | number\|null | 估算成本 |
  | usage.currency | string\|null | 币种 |
  | errorCode | string\|null | 错误码 |
  | errorMessage | string\|null | 错误消息 |
  | metadataJson | string | 元数据 JSON |
- 错误：404 code=404 `AI 请求日志不存在。`（AiEndpoints.cs:62-64）
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:59`、`src/Pim.Infrastructure/Ai/AiUsageService.cs:68-106`；前端 `src/client-web/src/api/ai.ts:54`、调用方 `src/client-web/src/pages/AiSettingsPage.tsx:51`
- 备注：前端类型 `AiRequestLogDetail`（types/index.ts:1316-1353）与后端一致。

### GET /api/v1/ai/usage/summary
- 用途：按时间范围汇总 AI 用量（总览 + 按模块/用途/模型/状态四维分组）。
- 认证：Admin
- Web 前端使用：是（AI 设置页用量统计卡）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | from | string (ISO-8601) | 否 | 起始时间（StartedAt ≥ from） |
  | to | string (ISO-8601) | 否 | 结束时间（StartedAt ≤ to） |
- Body：无
- 响应 data：`AiUsageSummaryDto`（AiDtos.cs:133-144，聚合 AiUsageService.cs:108-138）
  | 字段 | 类型 | 说明 |
  | requestCount | number | 请求总数 |
  | successCount | number | 成功数（仅 status=="succeeded" 计为成功，AiUsageService.cs:218-220） |
  | failureCount | number | 失败数（其余全部状态） |
  | promptTokens | number | 输入 token 合计（null 按 0） |
  | completionTokens | number | 输出 token 合计 |
  | totalTokens | number | 总 token 合计 |
  | estimatedCost | number | 成本合计 |
  | byModule | object[] | 按模块分组 |
  | byPurpose | object[] | 按用途分组 |
  | byModel | object[] | 按模型分组 |
  | byStatus | object[] | 按存储状态字符串分组（groupKey 为 succeeded/failed/blocked/timed_out/failed_validation） |
  | byModule[].groupKey 等 | string | 分组键（四组同构） |
  | byModule[].requestCount 等 | number | 该组请求数 |
  | byModule[].successCount 等 | number | 该组成功数 |
  | byModule[].failureCount 等 | number | 该组失败数 |
  | byModule[].promptTokens 等 | number | 该组输入 token |
  | byModule[].completionTokens 等 | number | 该组输出 token |
  | byModule[].totalTokens 等 | number | 该组总 token |
  | byModule[].estimatedCost 等 | number | 该组成本 |
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:67`；前端 `src/client-web/src/api/ai.ts:59`、调用方 `src/client-web/src/pages/AiSettingsPage.tsx:33`
- 备注（前后端差异）：后端支持 from/to 过滤，前端 `getAiUsageSummary()` 未传任何参数（ai.ts:59-62），始终全量汇总；分组按组内条数降序（AiUsageService.cs:206）。byStatus 的 groupKey 是存储字符串（小写下划线），而 items[].status 是 PascalCase 枚举字符串。

### POST /api/v1/ai/health-check
- 用途：立即触发一次 AI 提供方健康检查并返回最新状态。
- 认证：Admin
- Web 前端使用：是（AI 设置页 AiStatusPanel"健康检查"按钮）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`AiStatusDto`（字段同 GET /api/v1/ai/status）
- 来源：后端 `src/Pim.Api/Endpoints/AiEndpoints.cs:70`（先 `health.CheckAsync` 再读 GetStatusAsync，AiEndpoints.cs:72-73）；前端 `src/client-web/src/api/ai.ts:42`、调用方 `src/client-web/src/components/ai/AiStatusPanel.tsx:39`
- 备注：与 GET /ai/status 响应同构，区别在于本端点会实际拨测网关并刷新 lastHealthCheckAt/lastError。
