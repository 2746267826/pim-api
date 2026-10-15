# 02 板块与模块清单（Pages & Modules）

> 每页一张模块表：**模块**（功能名）× **形式**（类型级）× **调用接口**（完整路径）× **前端处理**（数据行为）。
> 轮询/分页等缩略语见 [README 词汇表](README.md)。可从页面打开的弹窗/抽屉一并列为本页模块。

---

## /today 今日

职责：服务器驱动的分区仪表盘。页面本身不写死分区，而是拉取"分区注册表"，按 `kind` 分发渲染；分区归入三个区域——行动格、数据条、可折叠的"运维与状态"手风琴区。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 分区注册表加载器 | 注册表驱动的分区宿主 | GET /api/v1/today/sections?date= | 延迟轮询；客户端只渲染已知 kind；午夜自动翻转日期 |
| 单分区加载器 | 按需数据获取 | GET /api/v1/today/sections/{sectionId}?date= | 仅 `kind` 前缀为 `pc.`/`operations.` 的分区轮询 |
| 今日日程 | 列表（日程+已排任务合并、按时间排序、优先级标记条） | 来自分区数据 | 点击打开日程/任务编辑弹窗 |
| 今日任务 | 列表（按截止排序） | 来自分区数据 | 点击打开任务编辑弹窗 |
| 今日 PC 概览 | 统计卡片 + 概要列表 | 来自 `pc.` 分区 | — |
| PC 数据质量 | 状态列表 | 来自 `pc.` 分区 | — |
| 系统健康 | 状态列表 | 来自 `operations.` 分区 | — |
| 分类建议 | 列表（待处理建议） | 来自 `pc.` 分区 | — |
| 运维与状态区（8 卡） | 统一"分区壳"列表卡（标题/副标题/徽标+前三行+空提示）：待确认、微软同步、提醒队列、报告、设备端点、空闲窗口、习惯、AI 占位 | 来自 `operations.` 分区 | 手风琴可折叠 |
| 展览馆内嵌 | 两张图表卡：近 7 日趋势折线图 + 习惯日历热力图 | 复用展览馆数据钩子 | 见 /exhibition |

另含：注册表错误面板、加载空态、任务编辑弹窗、日程编辑弹窗（见 /tasks、/calendar 的弹窗定义）。

## /calendar 日历

职责：日历主视图，时间轴（单日时间格）与月网格两种视图；任务排期的拖放目标。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 日期导航 | 按钮组（前一天/今天/后一天）+ 可见范围标题 | — | 纯 UI 状态 |
| 视图切换 | 分段控件（时间轴 / 月视图） | — | 持久化到 `?view=` URL |
| 图层过滤条 | chips 切换组：日程 / 任务时间段 / 习惯 / 可用时间 / 智能占位 + 仅 Outlook 开关 | — | 显隐状态存 Context（含侧边栏日历本显隐联动） |
| 日历网格 | 完整日历网格（时间轴=单日时间格；月视图=月网格）；事件卡带徽标位：已取消、闲忙、地点、来源日历本、描述摘要、重复、重要、提醒 | GET /api/v1/calendar/events?start&end&page=1&pageSize=100；GET /api/v1/calendar/calendars?kind=calendar；GET /api/v1/calendar/layers?start&end&layers=…&outlookOnly | 延迟轮询；events 有 pageSize 上限并在截断时提示；拖选空白时段→预填日程编辑弹窗；从收件箱侧板拖入任务到时间槽→POST 排期 |
| 拖放排期 | 拖放调度 | POST /api/v1/calendar/tasks/{id}/plan | Body `{plannedStart, plannedEnd, estimatedDuration}`；失败显示告警条 |
| 收件箱侧板 | 右侧停靠任务卡列表（全局模块，见 01 §5） | GET /api/v1/calendar/tasks（无参数） | 客户端过滤（收件箱或未排期）；标准轮询；"一键重排"按钮 |
| 任务编辑弹窗 | 表单弹窗 | 见 /tasks 弹窗 | — |
| 日程编辑弹窗 | 表单弹窗 | POST /api/v1/calendar/events；PUT/DELETE /api/v1/calendar/events/{id}?scope&recurrenceId&originalEventId | 重复事件按 scope（单次/本次及以后/全部）编辑删除；Outlook 镜像事件走回写接口（409/412 驱动冲突 UI）；只读日历禁用表单并显示横幅 |

## /workbench 工作台

职责：排程工作台/运营驾驶舱，聚合关键状态与 AI 排程建议。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 页头控制 | 分段控件 ×2（密度：标准/紧凑/专注；视图：排程/执行/反馈） | — | 纯 UI 状态 |
| 统计卡行 | 4–5 张指标卡：日历图层、待确认、AI 排程建议、Outlook 同步状态/令牌健康、最近同步批 | 见下列各行 | — |
| 日历图层概览 | 5 个计数瓦片 + "打开日历"链接 | GET /api/v1/calendar/layers?start&end&layers=… | — |
| AI 智能排程建议 | 建议卡片网格：来源徽标（AI 规则/规则引擎）、时段、理由，每卡采纳/忽略按钮；地平线选择器（3/7/14 天）+ 一键生成按钮（toast 反馈） | POST /api/v1/calendar/ai-placeholders/generate（Body horizonDays）；GET /api/v1/calendar/ai-placeholders?status=Suggested；POST /api/v1/calendar/ai-placeholders/{id}/confirm；POST …/{id}/dismiss | 采纳会创建操作确认单 |
| 待确认操作卡 | 列表卡（前三条 + 链接到确认中心） | GET /api/v1/operations/confirmations/pending | — |
| Outlook 同步状态卡 | 状态卡：最近同步、提供方、令牌健康、错误、配置链接 | GET /api/v1/calendar/outlook/settings；GET /api/v1/calendar/outlook/sync/batches?page&pageSize | — |
| PC 记录概览卡 | 3 个统计瓦片 | GET /api/v1/pc/summary?date= | — |
| 待办任务列表 | 独立滚动容器内的任务行列表：完成切换圆点、优先级/任务本徽标、截止日；点击开编辑弹窗；"添加任务"按钮 | GET /api/v1/calendar/tasks?…&page&pageSize=50；PUT /api/v1/calendar/tasks/{id}（完成切换） | — |
| 端点与状态链接面板 | 4 个 URL 信息瓦片 | — | 静态链接 |

## /tasks 任务

职责：任务管理主列表，支持过滤、任务本层级、多选批操作与执行时间段编辑。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 快捷筛选条 | chips 单选组：全部/收集箱/今日截止/今日已安排/高优先/已完成 | — | 映射为查询参数（inbox、due 范围、priority、status） |
| 筛选栏 | 搜索输入 + 任务本下拉 | — | 搜索直接进服务端参数；任务本同步到 `?taskBookId=` URL |
| 多选工具条 | 计数徽标 + 全选当前结果/取消全选 + 删除选中（确认对话框） | POST /api/v1/calendar/tasks/batch-delete | 先确认（受影响数+样例）再执行 |
| 任务层级树 | 左侧树（项目/任务本分组，可选中） | GET /api/v1/calendar/task-books | — |
| 任务卡列表 | 任务行列表：复选多选、优先级标记条+徽标组（优先级/状态/截止/已排期/收件箱）、内联"标记完成"开关、时间段按钮、行点击开编辑弹窗 | GET /api/v1/calendar/tasks?inbox&search&calendarId&status&priority&plannedFrom&plannedTo&dueFrom&dueTo&page&pageSize=100；PUT /api/v1/calendar/tasks/{id}（完成切换/内联编辑） | 服务端过滤+分页 |
| 分段编辑面板 | 选中任务的多次执行时间段编辑面板 | GET/POST /api/v1/calendar/tasks/{id}/segments | 删除时间段的端点（DELETE …/segments/{segmentId}）前端未调用 |
| 任务编辑弹窗 | 表单弹窗（属性+清单） | POST /api/v1/calendar/tasks；PUT/DELETE /api/v1/calendar/tasks/{id}；POST /api/v1/calendar/tasks/{id}/checklist；PUT/DELETE /api/v1/calendar/tasks/{id}/checklist/{itemId} | — |
| 空状态 | 统一空态组件 | — | — |

## /confirmations 确认中心

职责：高风险/待确认操作的处置中心（主从布局）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 待确认列表 | 左侧列表：摘要、风险徽标、来源/操作、过期时间；自动选中第一条 | GET /api/v1/operations/confirmations/pending | 延迟轮询 |
| 确认详情面板 | 右侧面板：键值详情网格（风险/来源/操作类型/状态/对象/关联/审计批/AI 建议/外部回写效果/恢复路径）、变更字段 chips、允许操作 chips、预览说明 | GET /api/v1/operations/confirmations/{id} | — |
| 前后差异对比 | 字段级 before/after 对比表 | 详情数据内嵌 | — |
| 确认/拒绝操作 | 两步武装确认按钮（第一次点击变为待执行态，再次点击执行）；按风险等级分别走标准/二级/严格确认端点；严格确认另有专门面板 | POST /api/v1/operations/confirmations/{id}/confirm；/confirm-second-level；/confirm-strict；/reject | 失败显示错误横幅；成功后失效列表缓存 |

## /data-center 数据中心

职责：跨对象类型的数据治理（查询、批量影响预览、恢复、审计导出）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 审计导出 | 页头按钮（JSON 下载） | GET /api/v1/calendar/data-center/audit/export?start&end | 响应保存为本地 Blob |
| 筛选栏 | 桌面筛选栏 / 移动端筛选抽屉：全局搜索、对象类型下拉（日程/任务/任务时间段/习惯/提醒/报告/同步批次/同步冲突/审计版本）、来源下拉、待处理复选、仅 Outlook 复选 | — | — |
| 治理对象表格 | 桌面数据表格 / 移动卡片列表：标题摘要、对象类型、来源、状态、开始时间；行选择；总数 | POST /api/v1/calendar/data-center/query | 服务端查询 |
| 对象详情面板 | 键值网格 + 审计时间线链接 + 回收站/版本恢复按钮 + 恢复预览结果横幅 | POST /api/v1/calendar/data-center/restore/preview（Body: auditVersionId, reason） | — |
| 批量影响预览面板 | 批量操作影响预览面板 | POST /api/v1/calendar/data-center/batch/preview | — |

## /reminders 提醒

职责：提醒队列、规则与发送历史。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 标签页 | 分段控件：待提醒 / 规则 / 发送历史 | — | — |
| 筛选栏 | 3 个下拉：时间范围（今天/本周/逾期/全部）、通道（桌面/邮件/Web/Android）、状态（待发/已延后/已忽略/已发送/全部） | — | — |
| 提醒卡列表 | 提醒卡：标题正文、风险徽标、4 瓦片信息格（触发原因/通道/勿扰窗口/升级策略）、计划时间+状态、关联对象审计链接、投递与响应历史条、操作按钮（打开详情/稍后提醒/忽略） | GET /api/v1/calendar/reminders；POST /api/v1/calendar/reminders/{id}/snooze?scheduledAt=；POST …/{id}/dismiss；POST …/{id}/actions/{action} | — |
| 发送历史列表 | 投递日志卡列表（通道/状态/时间戳/载荷/响应时间）；用户响应历史 chips | GET /api/v1/calendar/reminders/delivery-log | — |

## /reports 报告

职责：报告生成与浏览。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 报告类型标签页 | 标签组：日报/周报/月报/项目报告 + 生成报告按钮 + 筛选栏（日期输入+状态：草稿/已发布/归档） | GET /api/v1/calendar/reports；POST /api/v1/calendar/reports/generate | — |
| 统计卡行 | 3 张指标卡：报告数、建议数、待跟进确认 | — | — |
| 报告内容面板 | 报告头（标题/风险/状态/生成时间）+ 文本正文 | — | — |
| 指标面板 | 键值行（最多 8 条） | — | — |
| 后续确认面板 | 建议卡片 + "请求确认"操作 | POST /api/v1/calendar/reports/suggestions/{id}/request-action | 创建操作确认单 |
| 展览馆内嵌 | 漏斗图 + 仪表盘图两张图表卡 | 复用展览馆数据钩子 | — |

## /habits 习惯

职责：习惯规则中心（创建/查看；完成历史与日历投射为占位）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 标签页 | 分段控件：执行中 / 规划 / 归档 | — | — |
| 习惯规则编辑表单 | 表单（标题、频率、时间段→生成日历图层规则） | POST /api/v1/calendar/habits | — |
| 筛选下拉 | 频率（每日/每周/每月）、来源（手动/模板/AI） | — | — |
| 习惯规则卡列表 | 只读规则卡：标题、频率、状态 | GET /api/v1/calendar/habits | — |
| 占位面板 ×2 | 纯描述文本（完成历史 / 投射到日历） | — | — |

## /exhibition 展览馆

职责：图表组件陈列馆（内部画廊）：21 种生产图表组件的卡片网格，支持真实/模拟数据切换、对比、评分、导出。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 筛选栏 | 数据源下拉（模拟/真实）、模块下拉（手机使用/位置轨迹/PC 活动/日程习惯/设备健康）、排序下拉（评分升降）、搜索输入（`/` 热键聚焦） | — | 状态同步进 URL hash |
| 状态 chips 行 + 对比条 | 选中计数 chips；≥2 个选中出现对比条 → 对比弹窗（最多 3 张并排 + 导出） | — | — |
| 图表卡片网格 | 分页网格（9/页，j/k 翻页热键）；每卡：头（标题/数据类型/图表类型/描述）、单卡数据源覆盖下拉、复制深链按钮、选中复选、标签 chips、图表体（懒加载真实数据、模拟数据回退、卡片级错误边界）、1–5 星评分、选中开关 | 复用各域查询端点：GET /api/v1/mobile/analytics/charts、/heatmap、GET /api/v1/mobile/location/analytics/tracks、/frequent-places、GET /api/v1/pc/summary、GET /api/v1/pc/aggregation/app-usage、GET /api/v1/calendar/tasks、GET /api/v1/calendar/habits 等 | 客户端聚合分箱；部分序列（习惯连续记录等）由标题确定性模拟；展览馆查询静音错误 toast；评分/选中/视图存 localStorage |
| 分页条 + 空态 | 分页栏；空筛选状态（含重置）；JSON 导出（选中项下载）；"如何体验"说明面板；`?card=` 深链滚动定位 | — | — |
| 图表类型覆盖 | 环形、折线、面积、堆叠柱、热力矩阵、GPS 散点地图、六边形密度、气泡、直方图、横向条形图、键盘矩阵、日历热力图、进度环、仪表盘、状态条、漏斗 | — | — |

## /quick-notes 快速记录

职责：闪念笔记瀑布流看板与编辑。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 状态筛选 + 搜索 | chips 单选（全部/收集箱/已处理/已归档）+ 搜索输入 | GET /api/v1/quick-notes?status&search&page&pageSize | 搜索逐键直查服务端（无防抖）；服务端分页 |
| 分类筛选条 | pills 单选（全部/灵感/学业/开发/运维/生活） | — | 分类从笔记内容前缀自动提取 |
| 闪念瀑布流 | 瀑布流卡片墙（多列卡片流）：分类 chip、时间戳、内容预览、附件胶囊（名称+大小）、状态徽标；点击开编辑弹窗 | — | — |
| 页面 FAB | 圆形按钮 → 菜单（写闪念/建任务/排日程） | — | — |
| 闪念编辑弹窗 | 可拖拽弹窗：分类下拉、Markdown 编辑器 + 预览、归档/已处理复选、附件上传行（选择文件+胶囊列表）、保存/删除 | GET /api/v1/quick-notes/{id}；POST /api/v1/quick-notes；PUT /api/v1/quick-notes/{id}（contentMarkdown+attachmentIds）；POST /api/v1/quick-notes/{id}/process（仅标记为已处理）；POST …/{id}/archive；POST …/{id}/restore；DELETE …/{id}；POST /api/v1/quick-notes/attachments（multipart） | 编辑器支持粘贴/上传图片；预览时附件下载链接改写为带认证的 blob URL |
| Shell 分享预填 | URL 参数预填 | — | 支持 `?prefill=`/`?text=`/`?embed=1` |

## /files 文件

职责：OneDrive 三栏文件浏览器（树 / 列表 / 预览）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 未绑定引导 | 空绑定卡 + 自动弹出的设备码绑定向导弹窗（输入 Client ID → 显示设备码+外部验证链接 → 轮询直到连接） | GET /api/v1/files/providers；POST /api/v1/files/providers/onedrive；GET /api/v1/files/providers/{id}/binding-status | 绑定状态固定 5 秒轮询；断开 DELETE /api/v1/files/providers/{id} |
| 文件夹树 | 左栏懒加载树：展开/收起不切目录；双击/单击进入；每文件夹错误重试；截断提示 | GET /api/v1/files/items?path=…&page&pageSize | 逐文件夹顺序加载分页，硬上限 20 页/文件夹并明示截断；树状态横幅 |
| 中栏工具行 | 移动端目录抽屉按钮、同步状态横幅+手动触发、上传按钮、新建文件夹、传输任务切换（活动点）、我的分享按钮 | POST /api/v1/files/providers/{id}/sync；GET /api/v1/files/providers/{id}/sync-status | 同步状态**条件轮询**：仅 syncing 时 2 秒，否则停止 |
| 文件列表 | 面包屑导航；搜索输入+范围切换（本文件夹/全盘）；列表/网格（缩略图+灯箱）切换；可排序列；服务端分页；复选多选+批操作条（删除/移动/下载）；行菜单（打开/下载/改名/移动/删除/分享/在 OneDrive 打开） | GET /api/v1/files/items?path&page&pageSize=100&q&sort=name\|modified\|size&order&type；GET /api/v1/files/search?q&mode=keyword&page&pageSize | 服务端排序/过滤/分页；搜索 250ms 防抖；浏览器记忆（路径/排序/视图/搜索范围）存 localStorage |
| 拖放/粘贴上传 | 整个中栏为拖放上传区；文档级粘贴上传 | 见"传输队列面板" | — |
| 传输队列面板 | 上传队列面板：逐文件进度/失败/重试；串行队列（一次一个）；历史持久化 | ≤4MB：POST /api/v1/files/items/upload（multipart providerId+path+file）；>4MB：POST /api/v1/files/items/upload-session → 浏览器直 PUT Graph uploadUrl（10MiB 分片、Content-Range、202/201、nextExpectedRanges 续传）→ POST /api/v1/files/items/upload-session/complete | 客户端硬限：>2GB 直接拒绝；>4MB 必须走会话；分片 PUT 不带认证头（Graph 预授权 URL） |
| 预览面板 | 右栏文件预览：图片 / 可编辑文本 / Office 文档；移动端全屏覆盖+返回 | GET /api/v1/files/items/{id}/preview-url（JSON {url} 打开微软域）；GET/PUT /api/v1/files/items/{id}/text；GET /api/v1/files/items/{id}/content、/thumbnail（带认证 fetch→blob，因 img 标签带不了头；缩略图懒加载 IntersectionObserver） | 未选中显示占位 |
| 名称弹窗 | 新建文件夹/改名表单弹窗 | POST /api/v1/files/folders（Body: path）；POST /api/v1/files/items/{id}/rename | — |
| 移动弹窗 | 目标文件夹选择弹窗（支持批量） | POST /api/v1/files/items/{id}/move | — |
| 删除弹窗 | 批量删除确认（含"可在 OneDrive 回收站恢复"提示） | DELETE /api/v1/files/items/{id} | — |
| 下载流程 | 下载确认（>100MB 提示）+ 直接打开 | GET /api/v1/files/items/{id}/download-url（JSON {url}）→ window.open | 刻意不用 fetch 跟随 302（避免双重传输） |
| 分享弹窗 / 我的分享弹窗 | 创建分享（权限 view\|edit + 有效期天数）；管理已有分享（撤销/显示链接） | POST /api/v1/files/items/{id}/share；GET /api/v1/files/items/{id}/shares；DELETE /api/v1/files/items/{id}/shares/{permissionId}；GET /api/v1/files/shares | — |
| 打开于 OneDrive | 外部跳转 | GET /api/v1/files/items/{id}/open-link?mode=view → window.open | — |
| 移动树抽屉 | 侧滑抽屉（镜像文件夹树） | — | — |

## /pc-tracker 电脑记录

职责：PC 活动分析仪表盘。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 日期维度条 | 日期选择器 + 维度分段控件（时/日/月/年） | — | 驱动热力图维度 |
| 今日复盘摘要 | 统计瓦片 + 待处理建议数 + 专注/深夜/分类分布概要统计 | GET /api/v1/pc/summary?date= | 延迟轮询 |
| 分类时间线 | 甘特式小时分类时间条（自定义图表）；"查看详情"→ 当日逐事件时间线弹窗 | GET /api/v1/pc/summary?date= | — |
| 上下文确认面板 | 待处理上下文分类建议列表（预览/拒绝按钮） | GET /api/v1/pc/classification/suggestions?date=（延迟轮询）；POST /api/v1/pc/classification/suggestions/{id}/reject；POST …/{id}/preview；POST …/{id}/apply | 预览走"先预览后应用" |
| 生产力面板 | 专注仪表盘 + 深夜统计 + 指标瓦片 | GET /api/v1/pc/productivity/dashboard?date= | — |
| 时间块热力图 | 时间块活动强度热力矩阵；选中块驱动联动过滤 | GET /api/v1/pc/activity-analysis?date=&blockMinutes=60 | 选中状态为页面级状态 |
| 维度活动热力图 | 时/日/月/年活动热力矩阵 | GET /api/v1/pc/heatmap/grid?start&end&dimension=hour\|day\|month\|year | — |
| 每日活动面板 | 分类排行列表 + 应用时长排行（点击即过滤）+ 应用使用图表 | GET /api/v1/pc/aggregation/category-distribution；GET /api/v1/pc/aggregation/app-usage?…&limit=8 | — |
| 键盘热力图 | 108 键键盘矩阵热力 + 鼠标键矩阵 + 快捷键统计 | GET /api/v1/pc/summary?date= | — |
| 标注队列 | 可展开标注卡：未分类应用/域名/手机应用 + 建议分类快捷按钮 + 自定义分类输入 + 应用范围（全部/关键词） | GET /api/v1/pc/classification/queue?limit&mode=queue；POST /api/v1/pc/classification/label | 成功后移除条目；自定义分类记忆 localStorage；今日页复用（limit=1） |
| 展览馆内嵌 | 应用环形图 + 键盘热力图两卡 | 复用展览馆钩子 | — |
| 分类纠正弹窗 | 分类选择器 + 预览后应用（服务端预览结果+差异展示） | POST /api/v1/pc/classification/suggestions/{id}/preview；POST …/{id}/apply | — |

## /pc-tracker/browser 浏览器使用

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 页头控制 | 单日/范围模式切换；单日：今天/前后按钮+日期输入；范围：近 7 天/近 30 天/自定义快捷 + 起止日期输入；导入历史数据按钮 | — | — |
| 摘要卡条 | 指标卡条（汇总 API 数据） | GET /api/v1/pc/browser-tt/summary?date= 或 ?from&to | 按模式切换参数 |
| 单日时间线 | 各域名 0–24 小时时段分布图 | GET /api/v1/pc/browser-tt/timeline?date= | 仅单日模式 |
| 趋势图 | 日趋势折线/柱状图 | GET /api/v1/pc/browser-tt/daily?from&to | 仅范围模式 |
| 站点排行 | 域名排行列表（各域名专注时长） | summary 数据 | — |
| 导入弹窗 | 历史数据导入弹窗（粘贴/文件内容，模式 overwrite\|add，可选 deviceId） | POST /api/v1/pc/browser-tt/import | — |

## /mobile-records 手机记录

职责：手机使用分析（子视图：使用记录 / 设备存活）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 子视图标签 | 标签条（使用记录 / 设备存活） | — | `?view=liveness` URL 状态 |
| 筛选栏 | 范围快捷（今天/7 天/30 天/自定义）+ 自定义日期、设备下拉、分类下拉、包名输入、含系统噪声开关、刷新按钮（强制刷新+进行中指示） | — | 手动刷新追加 `force=true` 穿透聚合缓存 |
| 概览统计条 | 指标条 | GET /api/v1/mobile/analytics/overview?… | 延迟轮询 |
| 使用热力图 | 热力矩阵（粒度切换）；选中单元 → 侧边详情面板 | GET /api/v1/mobile/analytics/heatmap?…&granularity | 延迟轮询 |
| 图表网格 | 服务端定义的响应式图表组（分类/应用图表）；点击系列→设置过滤器 | GET /api/v1/mobile/analytics/charts?… | 延迟轮询 |
| 时间线条带 | 分页时间条（ECharts）+ 可展开块→会话列表→会话事件（三级下钻）；页大小选择 | GET /api/v1/mobile/analytics/timeline-blocks?page&pageSize=20；GET …/{blockId}/sessions；GET /api/v1/mobile/analytics/sessions/{sessionId}/events | 手动数字分页；子级按展开条件加载 |
| 异常面板 | 异常/建议/质量列表面板；旁边放共享标注队列 | GET /api/v1/mobile/quality?date&deviceId；标注队列同 /pc-tracker（targetType=mobile_app） | — |
| 展览馆内嵌 | 分类环形图 + 日热力矩阵两卡 | 复用展览馆钩子 | — |
| 设备存活视图 | 日期快捷（今天/7 天/30 天）+ 刷新；分组设备卡（在线组/静默组，含心跳覆盖） | GET /api/v1/mobile/liveness/overview?rangeStartUtc&rangeEndUtc；GET /api/v1/mobile/devices | 仅该视图激活时加载 |

## /location-history 历史位置

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 筛选栏 | 范围快捷 + 自定义日期、设备下拉、最大精度输入、含已拒绝点开关、刷新 | — | 全部过滤条件序列化进 URL 参数 |
| 指标条 | 位置点计数/距离等指标条 + 移动统计 4 瓦片 | GET /api/v1/mobile/location/analytics/overview；GET /api/v1/mobile/location/analytics/movement-stats | 延迟轮询 |
| 轨迹地图 | 地图轨迹（懒加载）：轨迹折线、停留点标记、精度圆、重新定位控件 | GET /api/v1/mobile/location/analytics/tracks | 延迟轮询；地图瓦片走后端代理 /api/v1/tiles/{z}/{x}/{y}.png |
| 段选择器 | 段选择/列表（与地图联动） | （数据来自 tracks 响应内嵌的 segments） | 独立的分段详情端点 GET /api/v1/mobile/location/analytics/segments/{segmentId} 前端未调用 |
| 停留/移动时间线 | 停留与移动段时间线 | — | — |
| 原始点表格 | 分页原始点表格（前一页/后一页 cursor 翻页；每点可选择）；停留点列表 | GET /api/v1/mobile/location/analytics/segments/{segmentId}/points?cursor&pageSize=200&force | **cursor 分页**（cursor 栈支持回退）——全站唯一游标分页 |
| 常去地点 | 地点列表 | GET /api/v1/mobile/location/analytics/frequent-places | 客户端按坐标舍入去重 home+places |
| 展览馆内嵌 | GPS 轨迹地图 + 位置气泡图两卡 | 复用展览馆钩子 | — |

## /devices 设备管理

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 排序切换 | 分段控件：按活跃 / 按数据量 | GET /api/v1/mobile/devices/manage?sortBy= | — |
| 批量引导条 | ≥2 个选中出现合并按钮 | — | — |
| 设备卡列表 | 设备卡：名称、在线/离线、品牌/型号/系统/应用、可复制设备 ID、注册/最近活跃、数据计数（会话/事件/位置/范围/存储）、同步/质量/存储压力徽标；操作：合并复选、内联重命名、删除（预览计数+确认）、导出（JSON 下载）、详情链接 | POST /api/v1/mobile/devices/{id}/rename；GET /api/v1/mobile/devices/{id}/delete-preview；DELETE /api/v1/mobile/devices/{id}；GET /api/v1/mobile/devices/{id}/export（带认证 fetch→Blob 下载） | — |
| 合并确认弹窗 | 单选存留设备列表（活跃/保留/移除徽标+各设备记录数）+ 服务端合并预览（并入总量）+ 确认/取消 | POST /api/v1/mobile/devices/merge/preview；POST /api/v1/mobile/devices/merge | 预览在选择合法时才启用 |

## /devices/:deviceId 设备详情

只读详情页：设备头 + 规格列表、同步历史列表、健康时间线（7 天，原始 JSON 展示）、存储明细（预格式化 JSON）。

调用接口：GET /api/v1/mobile/devices/{id}/detail。

## /status 状态

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 刷新按钮 | 手动重取全部状态查询 | — | — |
| 总体状态卡 | 大状态标签 + 消息 + 状态胶囊 + 检查时间 | GET /api/v1/status | 延迟轮询 |
| PC 数据质量面板 | 状态胶囊+问题+下一步 | GET /api/v1/pc/quality | — |
| 采集器健康卡 | 连接胶囊（浏览器插件/站点通道）+ 上传计数 + 心跳年龄 | GET /api/v1/pc/tracker/health/latest | 轮询；失败静默置空 |
| 移动诊断面板 | 移动数据质量诊断列表 | GET /api/v1/mobile/quality | — |
| 连接设备与工作站网格 | Windows 工作站卡（版本/心跳/原生追踪状态/上传队列/计划离线）+ 移动设备卡（品牌/型号/Android/应用版本/最近活跃/质量徽标） | GET /api/v1/daemon/heartbeats + GET /api/v1/mobile/devices | 两源合并为统一设备模型（PC=非 android 的守护心跳；移动=设备行）；心跳接口错误吞掉为空数组 |
| 需要关注面板 | 后续步骤 bullet 列表告警面板 | — | — |
| 组件卡网格 | 每组件状态卡：名称/类型/状态胶囊/消息/检查时间/详情键值网格 | GET /api/v1/status | — |
| 每设备质量 | 独立小查询组 | GET /api/v1/mobile/quality?deviceId=… | useQueries 按设备并发；staleTime 30s、轮询 60s |
| 侧边栏状态点 | 同源小查询 | GET /api/v1/status/summary | 轮询（见 01 §3） |

## /settings 设置枢纽

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 功能入口卡列表 | 8 张链接卡 + 条件性"用户管理"卡（仅管理员）：账号安全/数据可信度/管理日程数据/回收站/PC 记录详细数据/同步设置/AI 设置/MCP 连接 | — | 角色来自 GET /api/v1/auth/me；「账号安全」不设 adminOnly，普通用户也可进入 |
| 板块入口卡 | 展览馆/状态信息/设备管理/应用知识库链接卡 | — | — |
| 关于卡 | 版本信息 + 复制按钮 | GET /api/version（页脚同源） | 匿名接口 |

## /settings/data-reliability 数据可信度

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 重新体检按钮 | 触发体检 + 过期结果警告横幅 | GET /api/v1/data-reliability/inspection；POST /api/v1/data-reliability/inspection/refresh | 完成后 toast |
| 13 规则列表 | 规则行：严重度三档（对应数据字段 redCount/yellowCount/greenCount）、阈值、违规计数；按区块分组 | inspection 数据 | 只读（不自动修复） |
| 规则详情弹窗 | 判据/阈值/为什么这么定/当前情况/违规样例列表/关联 issue | GET /api/v1/data-reliability/rules/{code}/violations?limit=2000 | 面板显示 10 行；全量导出 CSV Blob |

## /settings/sync 同步设置

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| Microsoft 设置面板 | Client ID 表单（UUID 校验）+ 检查连接按钮 + 结果显示 + 可折叠注册步骤指南（编号列表） | GET/PUT /api/v1/calendar/outlook/settings；POST /api/v1/calendar/outlook/check | 检查结果写回查询缓存 |
| 设备码授权面板 | 获取代码/取消/刷新按钮、设备码显示+复制、倒计时、外部打开验证链接、连接/错误状态 | POST /api/v1/calendar/outlook/device-code；POST /api/v1/calendar/outlook/device-code/poll；POST /api/v1/calendar/outlook/device-code/{sessionId}/cancel | **3 秒客户端轮询**直到 connected/failed/canceled；按 expiresAt 倒计时 |
| 日历选择面板 | 发现日历按钮；分组日历复选树（组全选；只读/远端缺失标签）；保存选择 | POST /api/v1/calendar/outlook/calendars/discover；GET /api/v1/calendar/outlook/calendars；PUT /api/v1/calendar/outlook/calendars/selection | — |
| 同步操作面板 | 立即同步/深度同步/强制获取全部日程按钮；可折叠日期范围行（起止输入+按范围同步） | POST /api/v1/calendar/outlook/sync | 同步/断开后失效 13 个相关查询键 |
| 同步历史面板 | 分页批次卡列表：状态、提供方、计数徽标（读/建/改/冲突/错）、镜像删除警告块、每日历失败行+重试、取消运行中批次 | GET /api/v1/calendar/outlook/sync/batches?page&pageSize=20；POST /api/v1/calendar/outlook/sync（retryOfBatchId）；POST /api/v1/calendar/outlook/sync/{id}/cancel | 延迟轮询 |
| 本地数据管理面板 | 预览计数（绑定/日历/事件）+ 两步删除确认 | GET /api/v1/calendar/outlook/local-data/preview；DELETE /api/v1/calendar/outlook/local-data | — |
| 断开连接面板 | 断开按钮 | POST /api/v1/calendar/outlook/disconnect | — |

## /settings/ai AI 设置

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| AI 状态面板 | 提供方状态面板（含测试/健康检查按钮） | GET /api/v1/ai/status；POST /api/v1/ai/test；POST /api/v1/ai/health-check | 轮询 |
| 用量概览 | 用量统计瓦片 | GET /api/v1/ai/usage/summary | 轮询 |
| 请求日志区 | 可折叠区：分页数据表格（行选择）+ 详情面板（键值/JSON 分区） | GET /api/v1/ai/requests?module&purpose&model&status&page&pageSize；GET /api/v1/ai/requests/{id} | 服务端过滤+分页；轮询 |

## /settings/mcp MCP 设置

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 客户端列表 | 数据表格：名称、token 前缀、状态徽标（在线/离线/已吊销）、最近活跃相对时间、调用计数（写次数）、最近工具；行操作：编辑权限/吊销/删除（原生 confirm） | GET /api/v1/mcp/clients；POST /api/v1/mcp/clients；PUT /api/v1/mcp/clients/{id}；POST /api/v1/mcp/clients/{id}/revoke；DELETE /api/v1/mcp/clients/{id} | **固定 10 秒轮询** |
| 实时调用流水表 | 数据表格：时间、客户端、工具名、状态码胶囊、时长、参数（截断）；刷新按钮 | GET /api/v1/mcp/activity | 固定 10 秒轮询 |
| 权限管理 | 读写工具权限复选矩阵；保存/取消 | GET /api/v1/mcp/catalog（工具目录）；PUT /api/v1/mcp/clients/{id} | — |
| 新建客户端抽屉 | 编辑抽屉：名称表单 → 一次性 token 显示（复制 token + 复制 mcp.json 配置示例）+ 一次性可见警告 | POST /api/v1/mcp/clients | token 仅创建时返回一次 |

## /settings/calendar-data 日程数据管理

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 头工具条 | 返回链接；导入目标日历下拉；导入 ICS（文件选择）；导出选中/导出全部（ICS 下载）；删除选中 | POST /api/v1/calendar/import-ics（multipart file+calendarId）；GET /api/v1/calendar/export-ics（Blob 下载 pim-events.ics）；POST /api/v1/calendar/events/batch-delete | — |
| 筛选栏 | 标题搜索、日历下拉、日期范围（全部/7 天/30 天/本月/自定义两输入）、总数 | — | — |
| 事件数据表格 | 全选/行复选、标题、日历色点+名称、起止、重复规则标签、详情操作 | GET /api/v1/calendar/events?search&calendarId&start&end&page&pageSize | 服务端分页 |
| 数字分页条 | 带省略号的页码分页 | — | — |
| 详情弹窗 | 只读事件字段（标题/日历/起止/地点/描述/重复/状态 + Outlook 导入提示） | — | — |
| 确认对话框 | 批量删除确认 | — | — |

## /settings/recycle-bin 回收站

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 筛选栏 | 类型下拉（全部/日程/任务/日历/任务本）+ 搜索输入 + 总数 | — | — |
| 回收站数据表格 | 类型、标题+时间、原属日历本、删除时间、恢复操作 | GET /api/v1/calendar/recycle-bin?type&search&page&pageSize=50 | 服务端分页 |
| 恢复预览弹窗 | 检查中状态、冲突/无冲突横幅、恢复样例列表、冲突明细列表、错误+重试、操作（取消/恢复为副本/恢复——副本恢复仅日程与任务） | POST /api/v1/calendar/recycle-bin/{type}/{id}/restore-preview；POST …/restore（Body: restoreAsCopy） | — |

## /settings/pc-data PC 明细查询

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 说明卡 | 2 张说明性指标卡 | — | — |
| 筛选抽屉 | 移动端快捷筛选抽屉 | — | — |
| 14 字段筛选格 | 起止日期、维度（时/日/月/年）、视图（interpreted/raw）、事件类型下拉、设备/应用/分类/按键/域名/标题/URL 文本输入、排序下拉 | GET /api/v1/pc/detail?<组合参数> | 客户端拼查询串 |
| 导出按钮 | 导出表格 CSV（带 BOM）+ 导出原始 JSON | — | 浏览器内由同一响应生成 Blob |
| 明细数据表格 | 宽表：类型/起止/设备/来源/明细/额外/时长；分页 | — | 空态文案由数据质量驱动 |

## /settings/users 用户管理

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 用户列表 | 卡片行：显示名、"我"徽标、角色徽标、停用徽标、用户名/邮箱/注册时间 | GET /api/v1/admin/users | — |
| 行操作 | 设为管理员/移除管理员、停用/启用（均原生 confirm） | POST /api/v1/admin/users/{id}/role；POST /api/v1/admin/users/{id}/status | 最后一个管理员受保护（40042） |
| 新建用户 | 页头右上「新建用户」按钮 → 居中弹窗（用户名 / 邮箱 / 显示名（可留空，默认与用户名相同）/ 初始密码（至少 8 位）/ 角色：普通用户·管理员，默认普通用户） | POST /api/v1/admin/users | 客户端预校验（用户名非空、邮箱含 @、初始密码 ≥8）不发请求；400/409 在弹窗内显示服务端 message 且弹窗不关闭、已填内容保留；提交中禁用按钮防重复提交；成功后关闭弹窗、刷新列表并提示「用户已创建」 |
| 重置密码 | 每行操作区「重置密码」按钮 → 居中弹窗（后果说明逐字：「重置后该用户在所有设备都需要重新登录。」） | POST /api/v1/admin/users/{id}/password | 新密码 ≥8 本地校验不发请求；成功后关闭弹窗并提示「密码已重置」；失败在弹窗内显示服务端 message；对自己重置的文案与其他用户一致（行为由服务端决定：全部会话失效） |
| 错误横幅 | 操作失败内联横幅 | — | — |

## /app-knowledge-base 应用知识库

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 域标签页 | 链接标签组（应用 / 分类，路由跳转） | — | — |
| 搜索 + 添加切换 | 搜索输入 + "添加应用"展开按钮 | — | — |
| 添加应用内联表单 | 网格表单：进程名、显示名、分类路径、效率下拉（高效率/中性/分散精力）、emoji 图标、描述 | POST /api/v1/pc/app-signatures | — |
| 应用数据表格 | 行选择表格：图标、显示名、进程名、分类路径、效率徽标、上下文计数（+待处理）、来源徽标（内置/学习/自定义）、删除（内置禁止） | GET /api/v1/pc/app-knowledge/apps?search=；DELETE /api/v1/pc/app-signatures/{id} | — |
| 上下文侧板 | 小屏全屏覆盖：选中应用头 + 统计 chips；上下文模式列表+删除 | GET /api/v1/pc/app-knowledge/apps/{appId}/contexts；DELETE /api/v1/pc/app-knowledge/contexts/{id} | — |

## /app-knowledge-base/categories 分类树

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 域标签页 | 同上 | — | — |
| 分类树 | 递归树：展开/折叠、图标、色点、名称、效率徽标、内置标签；行选择 | GET /api/v1/pc/categories/tree | staleTime 60s |
| 树工具条 | 初始化默认（种子）+ 添加根分类 | POST /api/v1/pc/categories/seed | — |
| 编辑面板 | 桌面内联 / 移动侧抽屉：名称、emoji、色板选择、效率分段按钮、保存、+添加子分类、删除 | POST /api/v1/pc/categories（创建+更新同端点）；DELETE /api/v1/pc/categories/{id} | — |

## /audit/:objectType/:objectId 审计时间线

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 页头 | 导出审计按钮（JSON 下载）；移动端筛选抽屉（来源下拉） | GET /api/v1/operations/audit/export?start&end | — |
| 版本记录列表 | 左侧版本行列表：来源+变更字段标签、操作者/确认、时间戳；可选中 | GET /api/v1/operations/audit/{objectType}/{objectId} | — |
| 版本详情面板 | 右侧键值网格（对象类型/ID、来源、确认、操作者、创建时间）+ 恢复预览按钮+结果横幅 + 前后差异对比表 | POST /api/v1/operations/audit/{auditVersionId}/restore-preview | — |

## /endpoint-shell 端点外壳（调试页）

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 心跳工具 | 发送心跳按钮；手动心跳表单（设备 ID 输入+平台选择 Windows/Android）+ 在线专属拦截计数 chip | POST /api/v1/endpoints/{deviceId}/heartbeat | — |
| 端点状态列表 | 可选端点卡：deviceId、平台、最近心跳、上传状态胶囊、采集缓存/在线处理瓦片 | GET /api/v1/endpoints | 延迟轮询 |
| 采集质量面板 | 设备、问题数、检查时间、状态胶囊 | GET /api/v1/endpoints/{deviceId}/collection-quality | 轮询 |
| 通知动作面板 | 执行低风险 / 打开高风险详情按钮 + 结果读数 | POST /api/v1/endpoints/{deviceId}/notification-actions | — |

## /embed/android/today（Android 今日内嵌）

无外壳内嵌页（Android 壳内 WebView），与原生通过 postMessage 桥通信（见 03 §6）。

| 模块 | 形式 | 调用接口 | 前端处理 |
|---|---|---|---|
| 采集状态卡 | 原生采集状态（持续采集、触发原因、下个位置） | — | 原生桥 native.state.request |
| 生成时间条 | 信息条 + 过期徽标 | — | 原生状态 30 秒轮询；日期每 45 秒重键 |
| 错误/加载态 | 错误横幅+重试；加载态 | — | — |
| 位置指标条 + 轨迹地图 | 指标条 + 地图轨迹（有点时） | GET /api/v1/mobile/location/analytics/overview；GET /api/v1/mobile/location/analytics/tracks | — |
| 使用洞察/摘要条 | 洞察条；回退使用摘要条 | GET /api/v1/mobile/analytics/overview；GET /api/v1/mobile/summary?date= | — |
| 应用排行列表 | 应用使用排行（占比条） | summary 数据 | — |
| 页面回报 | — | — | 通过桥向原生上报页面数据状态（page.report，30 秒重试） |

## /embed/android/tracks（Android 轨迹内嵌）

历史位置页（/location-history）的 embedded 紧凑变体：无页面外壳，模块子集相同。

## 未路由遗留页（重建可跳过）

`pages/PcClassificationPage.tsx`（PC 分类规则表+重算设置+规则编辑器）存在但未注册路由，已被应用知识库取代。其调用的规则端点（GET/POST /api/v1/pc/classification/rules、/preview、/apply、GET/PUT /pc/classification/settings）在附录中仍完整记录并标"否"。
