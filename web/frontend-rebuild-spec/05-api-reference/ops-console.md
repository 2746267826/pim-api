# 运维只读控制台接口规格（/api/v1/ops，OpsKey 鉴权，前端不使用）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`（注意 totalCount）。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> 本域说明：本组全部端点（`/api/v1/ops*`）由 `OpsKeyMiddleware` 守卫（Program.cs:224，仅匹配 `/api/v1/ops` 前缀）：未配置 OpsKey（`PIM_OPS_KEY` 或 `Ops:Key`，逗号分隔可配多个）时整组返回 503 `{ code: 50301, message: "OpsDisabled" }`（OpsKeyMiddleware.cs:29-34）；key 缺失或不匹配返回 401 `{ code: 40101, message: "OpsKeyMissingOrInvalid" }`（OpsKeyMiddleware.cs:36-42，固定时间比较 OpsKeyValidator.cs:17-27）；校验通过后注入 `ops-reader` 角色身份（OpsKeyMiddleware.cs:44-47）。更外侧还有 `OpsRateLimitMiddleware`（Program.cs:223）：按客户端 IP 限并发 2，超出返回 429 `{ code: 42901, message: "RateLimited" }` 并附 `Retry-After: 5`（OpsRateLimitMiddleware.cs:28-35、OpsRateLimiter.cs:8、18）。每个 ops 请求（成功与失败）都写审计日志（action `ops.db.query` / `ops.logs.query`，metadata 含 ip/sqlHash/rowCount/bytes/truncated，OpsDbEndpoints.cs:94-124、OpsLogsEndpoints.cs:125-147）。**Web 前端不调用本组任何端点**，消费方为外部运维脚本。

---

## 运维总览（Ops Health）

### GET /api/v1/ops/health
- 用途：运维自检总览——OpsKey 是否启用、库表数量、日志文件数量。
- 认证：OpsKey（经 OpsKeyMiddleware；未配置 key 时在中间件层即 503，见域说明）
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：裸对象（非 ApiResponse 封装，OpsHealthEndpoints.cs:32）
  | 字段 | 类型 | 说明 |
  | opsEnabled | boolean | OpsKey 是否已配置（validator.HasKeys，OpsHealthEndpoints.cs:13-14；能到达本端点时恒为 true） |
  | tablesCount | number | 只读连接查得的 public schema 表数量（查询失败静默记 0，OpsHealthEndpoints.cs:16-23） |
  | logFiles | number | 日志目录下 .jsonl 文件数量（失败静默记 0，OpsHealthEndpoints.cs:25-30） |
- 来源：后端 `src/Pim.Api/Endpoints/OpsHealthEndpoints.cs:11`
- 备注：本端点自身不做任何鉴权注解，仅依赖 OpsKeyMiddleware；db/日志子服务不可用不影响本端点返回 200。

## 只读数据库（Ops DB）

### GET /api/v1/ops/db/tables
- 用途：列出只读连接上 public schema 的全部表。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`TableInfo[]`（OpsDbService.cs:12）
  | 字段 | 类型 | 说明 |
  | name | string | 表名（information_schema.tables，按名称升序） |
  | type | string | 表类型（BASE TABLE / VIEW 等） |
- 错误：未配置只读连接 50301 `OpsRoConnectionNotConfigured` → HTTP 503（OpsDbService.cs:212-216、ExceptionMiddleware.cs:126）；权限不足（Postgres 42501）40302 `RestrictedColumn` → HTTP 403（OpsDbService.cs:69-73）
- 来源：后端 `src/Pim.Api/Endpoints/OpsDbEndpoints.cs:19`、`src/Pim.Api/Services/OpsDbService.cs:39-74`
- 备注：查询在 `READ ONLY` 事务 + `statement_timeout=10s` 下执行（OpsDbService.cs:44-52）。

### GET /api/v1/ops/db/describe
- 用途：查看指定表的列结构（列名/类型/可空/默认值）。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | table | string | 是 | 表名，须匹配 `^[a-zA-Z_][a-zA-Z0-9_]*$`（OpsDbService.cs:24、78-79） |
- Body：无
- 响应 data：`ColumnInfo[]`（OpsDbService.cs:13，按 ordinal_position 排序）
  | 字段 | 类型 | 说明 |
  | columnName | string | 列名 |
  | dataType | string | 数据类型 |
  | isNullable | boolean | 是否可空 |
  | defaultValue | string\|null | 默认值表达式 |
- 错误：表名非法 400 code=40002 `InvalidTableName`（OpsDbService.cs:78-79）；表不存在 404 code=40401 `TableNotFound`（OpsDbService.cs:100-101）；权限不足 403 code=40302 `RestrictedColumn`（OpsDbService.cs:120-124）
- 来源：后端 `src/Pim.Api/Endpoints/OpsDbEndpoints.cs:36`、`src/Pim.Api/Services/OpsDbService.cs:76-125`
- 备注：无

### POST /api/v1/ops/db/query
- 用途：执行只读 SQL 查询（仅 SELECT/WITH，AST 校验 + 行数/字节/时长三重截断）。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：无
- Body（`OpsDbQueryRequest`，OpsDbEndpoints.cs:127）：
  | 字段 | 类型 | 必填 | 说明 |
  | sql | string | 是 | 只读 SQL；必须以 SELECT/WITH 开头、单语句（仅允许末尾分号）、禁词 DELETE/UPDATE/INSERT/DROP/ALTER/TRUNCATE/CREATE/GRANT/REVOKE/CALL/EXECUTE/VACUUM/REINDEX/CLUSTER/COMMENT/SECURITY/SHOW、禁列 password_hash/token_hash、禁系统 schema（pg_catalog/information_schema/pg_*，正则预检 + libpg_query AST 权威校验），违规抛 40002（SqlAstValidator.cs:6-47、OpsDbService.cs:129-130） |
  | params | object\|null | 否 | 命名参数字典（键去掉 @/: 前缀后绑定 Npgsql 参数，OpsDbService.cs:147-154） |
  | maxRows | number\|null | 否 | 行数上限，默认 200，钳制 1~500（OpsDbService.cs:20-21、131） |
- 响应 data：`OpsDbQueryResult`（OpsDbService.cs:14）；截断时 HTTP **206** 并附响应头 `X-Truncated: true`（OpsDbEndpoints.cs:61-66）
  | 字段 | 类型 | 说明 |
  | rows | object[] | 行数组，每行为列名 → 值的字典（null 表示 SQL NULL） |
  | truncated | boolean | 是否被截断（超 maxRows、响应超 10 秒、累计超 5MB，OpsDbService.cs:157-198） |
- 错误：SQL 校验失败 400 code=40002（错误信息为具体原因）；权限不足 403 code=40302 `RestrictedColumn`；未配置只读连接 503 code=50301
- 来源：后端 `src/Pim.Api/Endpoints/OpsDbEndpoints.cs:53`、`src/Pim.Api/Services/OpsDbService.cs:127-210`
- 备注：审计 metadata 中的 sqlHash 为 SQL 的 SHA256 前 8 位十六进制（不落原文，OpsDbEndpoints.cs:81-85）；执行于 READ ONLY 事务 + 10 秒 statement timeout。

## 日志（Ops Logs）

### GET /api/v1/ops/logs/files
- 用途：列出日志目录下的 .jsonl 日志文件（按修改时间倒序）。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：无
- Body：无
- 响应 data：`LogFileInfo[]`（OpsLogsService.cs:10、34-44）
  | 字段 | 类型 | 说明 |
  | name | string | 文件名（如 pim-api-20260925.jsonl） |
  | size | number | 文件字节数 |
  | mtime | string (ISO-8601) | 最后修改时间（UTC） |
  | rowsEstimate | number\|null | 行数估算（恒为 null，ListFiles 未赋值） |
- 错误：日志目录不存在时返回空数组而非报错（OpsLogsService.cs:36-37）
- 来源：后端 `src/Pim.Api/Endpoints/OpsLogsEndpoints.cs:18`、`src/Pim.Api/Services/OpsLogsService.cs:34-44`
- 备注：无

### GET /api/v1/ops/logs/tail
- 用途：尾部读取单个日志文件（可按级别/关键字过滤），从文件末尾向前取 N 行。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | file | string | 是 | 文件名，须匹配 `^[a-zA-Z0-9_.-]+\.jsonl$`（OpsLogsService.cs:26、48） |
  | lines | number | 否 | 行数，默认 50，必须 1~500（OpsLogsEndpoints.cs:51、OpsLogsService.cs:49） |
  | level | string | 否 | 按日志级别过滤（对行内容做级别匹配） |
  | keyword | string | 否 | 按关键字过滤行内容 |
- Body：无
- 响应 data：`OpsLogsResult`（OpsLogsService.cs:21）；截断时 HTTP **206** 并附响应头 `X-Truncated: true`（OpsLogsEndpoints.cs:52、62-64）
  | 字段 | 类型 | 说明 |
  | lines | string[] | 日志行数组（每行为原始 JSONL 字符串） |
  | truncated | boolean | 是否被截断 |
  | nextCursor | string\|null | 续读游标（tail 场景恒为 null） |
- 错误：文件名非法 400 code=40002 `InvalidFileName`；lines 越界 400 code=40003 `Limit must be 1-500`；文件不存在 404 code=40401 `LogFileNotFound`（OpsLogsService.cs:48-51）
- 来源：后端 `src/Pim.Api/Endpoints/OpsLogsEndpoints.cs:45`、`src/Pim.Api/Services/OpsLogsService.cs:46-55`
- 备注：无

### GET /api/v1/ops/logs/query
- 用途：按时间范围/级别/关键字顺序扫描日志（支持 cursor 续读，跨文件扫描）。
- 认证：OpsKey
- Web 前端使用：否（消费方为外部运维脚本）
- Path 参数：无
- Query 参数（`OpsLogsQuery` 以 `[AsParameters]` 绑定，OpsLogsService.cs:11-20）：
  | 字段 | 类型 | 必填 | 说明 |
  | file | string | 否 | 限定单文件（缺省扫描目录内全部 .jsonl，按文件名升序） |
  | limit | number | 否 | 返回行数上限，默认 50（请求中为 0 时置 50，OpsLogsEndpoints.cs:81），必须 1~500（OpsLogsService.cs:59） |
  | level | string | 否 | 按日志级别过滤 |
  | keyword | string | 否 | 按关键字过滤 |
  | from | string | 否 | 起始时间（可解析为 DateTimeOffset，非法 40002 InvalidFrom） |
  | to | string | 否 | 结束时间（非法 40002 InvalidTo） |
  | cursor | string | 否 | 续读游标（Base64("文件名:字节偏移")，非法 40002 InvalidCursor，OpsLogsService.cs:76-92） |
- Body：无
- 响应 data：`OpsLogsResult`（OpsLogsService.cs:21）；截断（单次扫描超 10 秒或累计超 5MB）时 HTTP **206** 并附响应头 `X-Truncated: true`，并返回 nextCursor 供续读（OpsLogsEndpoints.cs:87、97-98、OpsLogsService.cs:115-155）
  | 字段 | 类型 | 说明 |
  | lines | string[] | 日志行数组（原始 JSONL 字符串） |
  | truncated | boolean | 是否被截断（超时/超字节/limit 用尽仍有剩余） |
  | nextCursor | string\|null | 续读游标（无更多数据时为 null） |
- 错误：limit 越界 400 code=40003；file/cursor 非法 400 code=40002；from/to 非法 400 code=40002；文件不存在 404 code=40401 `LogFileNotFound`（OpsLogsService.cs:59-60、65-71、98）
- 来源：后端 `src/Pim.Api/Endpoints/OpsLogsEndpoints.cs:78`、`src/Pim.Api/Services/OpsLogsService.cs:57-156`
- 备注：扫描时长上限 10 秒、字节上限 5MB（OpsLogsService.cs:28-29）；cursor 与 file 同时给出且文件不一致时游标偏移重置（OpsLogsService.cs:100）。
