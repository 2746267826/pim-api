# 03 全局数据行为（Data Behaviors）

> 描述前端的数据层行为约定。重建时这些行为共同决定了页面"活不活"，与模块清单同等重要。

## 1. 数据层栈

- **服务端状态**：全部由 TanStack Query 管理缓存（无 Redux/Zustand）；变更成功后按显式 queryKey 列表失效重取，**不使用乐观更新**（唯一例外：文件页的本地"同步已发起"横幅状态）。
- **查询重试**：最多 2 次，间隔 1 秒；4xx、用户中止、"登录已过期"不重试；后台轮询不进后台刷新（`refetchIntervalInBackground: false`）；结构共享开启。
- **错误上浮**：查询错误统一 toast，但 5xx 在"后台轮询中"或"已有缓存数据"时静音；展览馆查询（key 前缀 `exh`）完全静音；变更错误按 key 去重后 toast。

## 2. 轮询策略（本项目无 WebSocket/SignalR/SSE 消费，实时性全部靠轮询）

| 策略 | 间隔 | 适用 |
|---|---|---|
| 标准轮询 | 白天（06:00–23:59）5 分钟 / 夜间 30 分钟 | 大多数运营仪表盘 |
| 延迟轮询 | 用户滚动结束后的 1 秒内强制立即刷新，随后回落到标准间隔 | 同上（作为 refetchInterval 函数） |
| 固定轮询 10s | MCP 客户端列表、MCP 调用流水 | /settings/mcp |
| 条件轮询 2s | 文件同步状态 | 仅 syncStatus === 'syncing' 时 |
| 固定轮询 5s | OneDrive 绑定状态 | 绑定向导弹窗打开期间 |
| 固定轮询 3s | Outlook 设备码授权轮询 | 授权进行中（客户端 setTimeout 链） |
| 轮询 60s | 状态页每设备移动质量 | /status |
| 轮询 30s | Android 内嵌原生采集状态 | /embed/android/*（另有日期每 45 秒重键） |

## 3. 分页与排序模式

| 模式 | 约定 | 使用处 |
|---|---|---|
| offset 分页（`page`/`pageSize`） | 服务端分页封装 `PagedResult` | 文件列表（100/页）、任务列表（100/页）、日程数据管理、回收站（50/页）、快速记录、AI 请求日志、Outlook 同步批次（20/页）、工作台待办（50/页） |
| cursor 分页（`cursor`/`pageSize`） | 游标翻页 + cursor 栈回退 | 移动位置轨迹段原始点（200/页）——全站唯一 |
| 混合（旧版全量 vs 分页） | 同一端点带不带分页参数行为不同 | GET /api/v1/calendar/tasks：带参数→分页；不带→全量旧列表（日历页/收件箱侧板仍用全量+客户端过滤） |
| 范围查询 + 容量上限 | `start&end&page=1&pageSize=100`，截断时提示 | 日历事件 |
| 逐文件夹分页拼接 + 硬上限 | 每文件夹最多顺序拉 20 页并明示截断 | 文件树懒加载 |
| 数字页码条（带省略号） | UI 分页控件 | 日程数据管理 |

## 4. 文件上传（双通道引擎）

| 条件 | 通道 | 细节 |
|---|---|---|
| ≤ 4MB | 直传代理：POST /api/v1/files/items/upload（multipart：providerId、path、file） | 字节经 PIM 服务器中继 |
| > 4MB（≤ 2GB） | Graph 会话直传：POST /api/v1/files/items/upload-session（Body: path, fileName）→ 返回微软域预授权 uploadUrl → 浏览器直接 PUT 字节（10MiB 分片、`Content-Range: bytes s-e/total`、202/201 语义、按 `nextExpectedRanges` 续传、每分片最多重试 2 次、分片 PUT **不带** Authorization 头）→ POST /api/v1/files/items/upload-session/complete 登记元数据 | 字节不经过 PIM 服务器 |
| > 2GB | 客户端直接拒绝 | — |

队列行为：串行（一次一个文件）；传输历史持久化 localStorage；逐文件进度/失败/重试。

其他上传通道（均为单次 multipart）：快速记录附件（POST /api/v1/quick-notes/attachments）、ICS 导入（POST /api/v1/calendar/import-ics）。

## 5. 文件下载与预览通道

| 通道 | 行为 | 使用处 |
|---|---|---|
| 直链 JSON + 新窗口 | GET …/download-url 返回 `{url}`（微软域）→ `window.open`；>100MB 先弹确认（带大小） | OneDrive 文件下载 |
| 带认证 fetch → Blob | img/iframe 带不了认证头时用 fetch 拉取为 Blob（缩略图、图片网格、设备导出 JSON、审计/违规导出、ICS 导出、PC 明细 CSV/JSON） | 各处下载/预览 |
| 302 重定向 | 服务端直接 302 到微软直链（`download`/`content`/`thumbnail` 端点）——前端刻意**不**用 fetch 跟随 302（避免双重传输），仅 window.open 或已改用上述通道 | OneDrive 内容 |
| 客户端生成 | 从查询响应在浏览器内生成 CSV（带 BOM）/JSON Blob | PC 明细查询导出、数据可信度违规导出 |

## 6. Android 原生桥（/embed/android/* 专用）

产品 Android 壳只通过 `window.pimAndroid` postMessage 桥交换非敏感状态。内嵌页的三条业务 GET
由原生 OkHttp 代发，access/refresh token 不进入页面、DOM、storage 或桥消息。消息类型：

| 消息 | 方向 | 用途 |
|---|---|---|
| `token.request` / `token.refresh` | 页 → 原生 | 兼容类型，仅固定回复 `auth_unavailable`；不读取或返回令牌 |
| `native.state.request` | 页 → 原生 | 拉取原生采集状态（持续采集/触发原因/下个位置/电池优化），30 秒轮询 |
| `page.report` | 页 → 原生 | 向壳上报页面数据状态（成功/失败/空），30 秒重试 |

## 7. 缓存失效要点

- 变更成功后按显式 queryKey 列表失效（例如 Outlook 同步/断开操作后失效 13 个相关键）。
- 少量定向 staleTime 覆盖：移动设备列表 60s、PC 分类树 60s、展览馆位置数据 60s、每设备移动质量 30s；其余用默认。
- `setQueryData` 直接写缓存的场景：Outlook 连接检查结果。

## 8. URL 即状态

| 页面 | URL 参数 |
|---|---|
| 日历 | `?view=timeline\|month`、`?calendarId=` |
| 任务 | `?taskBookId=` |
| 手机记录 | `?view=liveness` |
| 历史位置 | 全部筛选条件序列化 |
| 展览馆 | hash 参数（模块/搜索/排序/页/选中）+ `?card=` 深链 |
| 快速记录 | `?prefill=`/`?text=`/`?embed=1` |

## 9. localStorage 持久化键（行为清单）

| 键（语义名） | 内容 |
|---|---|
| 认证令牌 | accessToken / refreshToken（内嵌页不用，见 §6） |
| 文件浏览器记忆 | 路径、排序、视图、搜索范围 |
| 传输队列历史 | 上传历史条目 |
| 标注自定义分类 | 标注队列的常用自定义分类 |
| 闪念弹窗位置 | 快捷记录弹窗坐标 |
| 日历图层显隐 | 图层开关状态（Context 持久化） |
| 展览馆 | 评分、选中、视图、数据源 |

## 10. 跨切面交互结构（重建需实现）

- **两步武装确认**：确认中心（标准/二级/严格三档端点）。
- **删除影响预览**：日历本删除、任务/事件批删、设备删除、回收站恢复、恢复预览——先调 preview 端点拿到受影响对象再确认。
- **拖放调度**：收件箱任务拖到日历时间槽（自动估算时长）。
- **先预览后应用**：分类建议纠正、版本恢复。
- **内联编辑**：日历本重命名（双击）、设备重命名。
- **多选批操作**：任务、日程数据管理、文件、设备合并。

## 11. 前端已定义但无 UI 调用的 API 包装（重建可省略）

`moveTask`、`batchUpdateTasks`、`createProject`/`getProjects`、`getReport`、数据中心 batch `request-confirmation`/`execute` 与 restore `request-confirmation`、`getPcTimeline`、`getPcHeatmap`、`getTimelineV2`/`getPcTimelineV2`、`getProductivityGoals`/`updateProductivityGoals`/`getProductivityRange`、PC 分类 v2（suggestions/v2、batch-accept）、应用签名 export/import/lookup/list/count、`savePcCategory`（部分）、recompute 面板、`getMobileTimeline`、`getMobileLocationHistory`、应用目录覆盖/分类规则/使用目标 CRUD、文件 trash/versions/index/suggestions/download-blob、`getEvents` 单查变体、规则 preview/apply 对。

> 对应端点在 05-api-reference 中仍全部列出并标"Web 前端使用：否"。
