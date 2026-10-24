# PC 活动追踪域接口规格（/api/v1/pc）
> 基地址 `/api/v1`；响应封装 `ApiResponse<T>` = `{ code, message, data, timestamp }`（`code=0` 成功）；下文"响应 data"均指 `data` 字段内容；列表分页封装 `PagedResult<T>` = `{ items[], totalCount, page, pageSize, totalPages }`。
> 认证图例：JWT = `Authorization: Bearer <accessToken>`；匿名 = 无需认证；Admin = JWT 且 role=admin；OpsKey = 请求头 `X-PIM-Ops-Key`。
> ⚠️ 本域特殊：写操作组（POST/PUT/DELETE）要求 JWT，但**读操作组（GET）当前未挂 RequireAuthorization，即匿名可读**（PcTrackerModule.cs 路由分组处，写明行号）。重建时建议按 JWT 处理，但现状如此。
>
> 补充（源码核实）：上述"读匿名"仅指 `readGroup = MapGroup("/api/v1/pc")`（src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:60，未挂授权）；写操作组 `writeGroup`（PcTrackerModule.cs:61-62，挂 RequireAuthorization）。但以下子分组**读写都挂了 RequireAuthorization**：app-knowledge（PcTrackerModule.cs:746-747）、app-signatures（PcTrackerModule.cs:890-891）、categories 树分组（PcTrackerModule.cs:1121-1122）、productivity 读分组（PcTrackerModule.cs:1201）。各端点"认证"行按实际注册标注。
> 序列化约定：ASP.NET Core 默认 camelCase；个别字段带 `[JsonPropertyName]` 别名（在对应节注明）。`force=true` 时跳过聚合结果缓存（IAggregateResultCache）直接重算。

---

## 汇总与明细

### GET /api/v1/pc/summary
- 用途：按业务日返回 PC 活动总览（键鼠统计、小时热力图、应用排行、时间线、工作会话、派生指标、分类占比）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage、工作台 WorkbenchPage、展览数据钩子 useExhibitionData——ExhibitionPage 等使用）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日 `yyyy-MM-dd`，缺省今天；业务日按 Asia/Shanghai 04:00 起算（PcTrackerService.cs:12） |
  | force | boolean | 否 | 默认 false；true 跳过聚合缓存重算 |
- 响应 data：`PcSummaryResponse`
  | 字段 | 类型 | 说明 |
  | keystats | KeystatsSummary \| null | 当日键鼠统计；无数据时为 null（从日汇总或最新采样兜底构建，PcTrackerService.cs:397-398） |
  | keystats.date | string | 统计日期 |
  | keystats.keyPresses | number | 按键总数 |
  | keystats.totalClicks | number | 点击总数 |
  | keystats.leftClicks | number | 左键点击数 |
  | keystats.rightClicks | number | 右键点击数 |
  | keystats.middleClicks | number | 中键点击数 |
  | keystats.sideBackClicks | number | 侧键后退点击数 |
  | keystats.sideForwardClicks | number | 侧键前进点击数 |
  | keystats.mouseDistance | number | 鼠标移动距离 |
  | keystats.scrollDistance | number | 滚轮距离 |
  | keystats.peakKps | number | 峰值 KPS（每秒按键） |
  | keystats.peakCps | number | 峰值 CPS（每秒点击） |
  | keystats.keyPressCounts | Record<string, number> | 按键名→次数字典 |
  | keystats.topKeys | KeyCountItem[] | 按键 Top 榜 |
  | keystats.topKeys[].keyName | string | 按键名 |
  | keystats.topKeys[].count | number | 次数 |
  | keystats.topKeys[].share | number | 占比 |
  | heatmap | HeatmapBucket[] | 小时级热力桶（AW window + tracker window 事件合并去重，PcTrackerService.cs:377-378） |
  | heatmap[].start | string | 桶起始时间 ISO-8601 |
  | heatmap[].end | string | 桶结束时间 ISO-8601 |
  | heatmap[].hour | number | 本地小时 |
  | heatmap[].activeMinutes | number | 活跃分钟数 |
  | heatmap[].totalEvents | number | 事件数 |
  | heatmap[].intensityScore | number | 强度分 |
  | appRanking | AppRankingItem[] | 应用键鼠排行 |
  | appRanking[].appName | string | 进程名 |
  | appRanking[].displayName | string | 归一化显示名 |
  | appRanking[].keyPresses | number | 按键数 |
  | appRanking[].totalClicks | number | 点击数 |
  | appRanking[].scrollDistance | number | 滚轮距离 |
  | appRanking[].share | number | 占比 |
  | timeline | TimelineItem[] | 归一化平滑后的活动时间线（PcTrackerService.cs:380-393） |
  | timeline[].start | string | 块开始时间 |
  | timeline[].end | string | 块结束时间 |
  | timeline[].durationMinutes | number | 时长（分钟） |
  | timeline[].appName | string | 进程名 |
  | timeline[].windowTitle | string \| null | 窗口标题 |
  | timeline[].categoryName | string | 分类名 |
  | timeline[].categoryColor | string | 分类颜色 |
  | timeline[].projectTag | string \| null | 项目标签 |
  | timeline[].classificationConfidence | number | 分类置信度 |
  | timeline[].classificationSource | string | 分类来源 |
  | timeline[].classificationExplanation | string | 分类解释 |
  | sessions | WorkSessionItem[] | 工作会话 |
  | sessions[].start | string | 会话开始 |
  | sessions[].end | string | 会话结束 |
  | sessions[].durationMinutes | number | 会话时长（分钟） |
  | sessions[].mainApp | string | 主应用 |
  | sessions[].appSwitchCount | number | 应用切换次数 |
  | metrics | DerivedMetrics \| null | 派生指标 |
  | metrics.totalRecordedDuration | string | 记录总时长（格式化字符串） |
  | metrics.activeInputDuration | string | 有效输入时长 |
  | metrics.idleDuration | string | 闲置时长 |
  | metrics.sessionCount | number | 会话数 |
  | metrics.activeAppCount | number | 活跃应用数 |
  | metrics.totalKeyPresses | number | 按键总数 |
  | metrics.totalClicks | number | 点击总数 |
  | metrics.appSwitchCount | number | 切换次数 |
  | metrics.switchFrequency | number | 切换频率 |
  | metrics.mostFocusedApp | string | 最专注应用 |
  | metrics.keyClickRatio | number | 键/点击比 |
  | categories | CategorySummary[] | 分类占比 |
  | categories[].categoryName | string | 分类名 |
  | categories[].color | string | 颜色 |
  | categories[].share | number | 占比 |
  | categories[].keyPresses | number | 按键数 |
  | categories[].totalClicks | number | 点击数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:241-256`（服务 `Services/PcTrackerService.cs:338-401`）；DTO `DTOs/PcTrackerDtos.cs:48-137`；前端 `src/client-web/src/api/pcTracker.ts:17-19`
- 备注：前端类型定义见 `src/client-web/src/types/index.ts:848-939`，与后端一致。

### GET /api/v1/pc/detail
- 用途：跨来源（AW 事件 / 原生 tracker 事件 / 键鼠采样）统一明细查询，前端在此响应基础上浏览器内生成 CSV/JSON 导出。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 明细查询面板 PcDetailQueryPanel，宿主 PcDetailQueryPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | dateFrom | string | 否 | 起始业务日 `yyyy-MM-dd`，缺省今天（PcTrackerService.cs:815-819） |
  | dateTo | string | 否 | 结束业务日，缺省同 dateFrom；start 晚于 end 报 400（PcTrackerService.cs:824-825） |
  | dimension | string | 否 | `hour\|day\|month\|year`（前端下拉）；**服务端当前未使用该参数**（GetDetailQueryRange 只看 dateFrom/dateTo），见备注 |
  | deviceId | string | 否 | 设备 ID 精确匹配（忽略大小写，PcTrackerService.cs:918-921） |
  | appName | string | 否 | 对 appName/displayName 包含匹配（PcTrackerService.cs:923-928） |
  | categoryName | string | 否 | 分类名精确匹配（忽略大小写，PcTrackerService.cs:930-933） |
  | keyName | string | 否 | 仅 input-minute 记录，keyCounts 键名包含匹配（PcTrackerService.cs:935-941） |
  | eventType | string | 否 | 记录类型精确匹配 recordType；`web` 会触发原始视图（PcTrackerService.cs:913-915、573-574） |
  | sortBy | string | 否 | 前端提供 `keyPresses\|totalClicks\|date`，**本端点服务端未使用**（排序只按 Start，见备注） |
  | sortDir | string | 否 | `asc\|desc`（默认 desc），按 Start 字符串序排序（PcTrackerService.cs:977-981） |
  | domain | string | 否 | 域名包含匹配（PcTrackerService.cs:943-946） |
  | title | string | 否 | 标题包含匹配（PcTrackerService.cs:948-951） |
  | url | string | 否 | URL 包含匹配（PcTrackerService.cs:953-956） |
  | view | string | 否 | `raw` 原始视图 / 其他为解释视图；eventType=web 也强制原始视图（PcTrackerService.cs:573-579） |
  | page | number | 否 | 默认 1 |
  | pageSize | number | 否 | 默认 20，钳制 1-200（PcTrackerService.cs:554） |
- 响应 data：`TypedDetailQueryResponse`（本域自定义分页结构，非 PagedResult）
  | 字段 | 类型 | 说明 |
  | items | PcDetailRecord[] | 当前页记录 |
  | page | number | 页码 |
  | pageSize | number | 每页条数 |
  | totalCount | number | 总条数 |
  | totalPages | number | 总页数 |
  | items[].recordType | string | 记录类型：`window` \| `afk` \| `web` \| `web-page` \| `input-minute` \| `app-input` \| `key-input` 等（前后端按字符串处理） |
  | items[].start | string | 记录开始时间 ISO-8601（UTC） |
  | items[].end | string \| null | 记录结束时间 |
  | items[].durationSeconds | number \| null | 时长（秒） |
  | items[].deviceId | string | 设备 ID |
  | items[].appName | string \| null | 进程名 |
  | items[].displayName | string \| null | 归一化显示名 |
  | items[].categoryName | string \| null | 分类名 |
  | items[].title | string \| null | 标题 |
  | items[].keyPresses | number \| null | 按键数 |
  | items[].totalClicks | number \| null | 点击数 |
  | items[].mouseDistance | number \| null | 鼠标距离 |
  | items[].scrollDistance | number \| null | 滚轮距离 |
  | items[].keyCounts | Record<string, number> \| null | 按键计数（input-minute） |
  | items[].raw | object \| null | 原始 JSON（解析失败为 null） |
  | items[].url | string \| null | 网页地址 |
  | items[].domain | string \| null | 域名 |
  | items[].path | string \| null | 路径 |
  | items[].isLocalFile | boolean | 是否本地文件（缺省 false） |
  | items[].browserAppName | string \| null | 宿主浏览器进程名 |
  | items[].browserWindowTitle | string \| null | 宿主浏览器窗口标题 |
  | items[].audible | boolean \| null | 是否有声 |
  | items[].incognito | boolean \| null | 是否无痕 |
  | items[].tabCount | number \| null | 标签页数 |
  | items[].absorbedShortEventsCount | number | 吸并短事件数（缺省 0） |
  | items[].absorbedDurationSeconds | number | 吸并时长秒（缺省 0） |
  | items[].sourceWebEventIds | number[] \| null | 来源 AW web 事件 ID |
  | items[].sourceWindowEventIds | number[] \| null | 来源 AW window 事件 ID |
  | items[].categoryColor | string \| null | 分类颜色 |
  | items[].projectTag | string \| null | 项目标签 |
  | items[].classificationConfidence | number \| null | 分类置信度 |
  | items[].classificationSource | string \| null | 分类来源 |
  | items[].classificationExplanation | string \| null | 分类解释 |
  | items[].bucketType | string \| null | 桶类型 |
  | items[].recordKey | string \| null | 记录键 |
  | items[].recordKeyVersion | string \| null | 记录键版本 |
  | items[].recordKeyStability | string \| null | 记录键稳定性 |
  | items[].sourceBucketIds | string[] \| null | 来源桶 ID |
  | items[].sourceType | string \| null | 来源类型（如 `aw`/`fallback`） |
  | items[].interpretationVersion | string \| null | 解释版本（如 `raw-aw-v1`） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:313-338`（服务 `Services/PcTrackerService.cs:551-610`）；DTO `DTOs/PcTrackerDtos.cs:148-165、175-224`；前端 `src/client-web/src/api/pcTracker.ts:35-41`、类型 `src/client-web/src/types/index.ts:1072-1152`
- 备注：① 前端导出为浏览器内生成：CSV `PcDetailQueryPanel.tsx:44-56`（BOM + detailCsvColumns），JSON `PcDetailQueryPanel.tsx:327-330`；② `sortBy`/`dimension` 参数服务端实际未参与明细查询（PcTrackerService.cs:973-981 只按 Start 排序），`sortBy` 的 keyPresses/totalClicks/date 分支只存在于旧的键盘统计明细路径（PcTrackerService.cs:799-813）；③ eventType 下拉取值 web-page/web/window/afk/input-minute/app-input/key-input（PcDetailQueryPanel.tsx:261-271）。

### GET /api/v1/pc/quality
- 用途：PC 数据质量体检（总状态、组件状态、问题清单、下一步建议）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 明细查询面板 PcDetailQueryPanel、状态页 StatusPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日 `yyyy-MM-dd` |
  | dateFrom | string | 否 | 起始日 |
  | dateTo | string | 否 | 结束日 |
  | force | boolean | 否 | 默认 false；true 跳过聚合缓存 |
- 响应 data：`PcQualityResponse`
  | 字段 | 类型 | 说明 |
  | overallStatus | PimHealthStatus | 总状态，枚举 `Unknown=0 \| Healthy=1 \| Warning=2 \| Critical=3`（src/Pim.Core/Operations/OperationEnums.cs:5-11） |
  | label | string | 展示标签 |
  | message | string | 说明信息 |
  | checkedAt | string | 检查时间 ISO-8601 |
  | components | PcQualityComponentDto[] | 组件状态列表 |
  | components[].key | string | 组件键 |
  | components[].name | string | 组件名 |
  | components[].status | PimHealthStatus | 组件状态（同上枚举） |
  | components[].message | string | 组件消息 |
  | components[].details | Record<string, string> | 附加明细键值 |
  | issues | PcQualityIssueDto[] | 问题列表 |
  | issues[].code | string | 问题码 |
  | issues[].severity | PimHealthStatus | 严重度（同上枚举） |
  | issues[].componentKey | string | 所属组件键 |
  | issues[].message | string | 问题消息 |
  | issues[].nextStep | string \| null | 下一步建议 |
  | nextSteps | string[] | 全局下一步建议 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:340-368`；DTO `DTOs/PcQualityDtos.cs:5-26`；前端 `src/client-web/src/api/pcTracker.ts:162-170`
- 备注：前端对 status 做数值/字符串双兼容归一化（pcTracker.ts:43-94），序列化可能为数字或枚举名。

### GET /api/v1/pc/heatmap/grid
- 用途：键盘热力图网格（按小时单行或按周 7 列网格）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | start | string | 否 | 起始日，缺省今天-30 天 |
  | end | string | 否 | 结束日，缺省今天 |
  | dimension | string | 否 | `hour\|day\|month\|year`，默认 `day`（PcTrackerModule.cs:731）；`hour` 取 start 当日 24 桶单行，其余按天网格（PcTrackerService.cs:682-749） |
  | force | boolean | 否 | 默认 false；true 跳过聚合缓存 |
- 响应 data：`HeatmapGridResponse`
  | 字段 | 类型 | 说明 |
  | grid | HeatmapBucket[][] | 网格（二维数组；day 维度每行 7 天，bucket.hour=DayOfWeek 序号；hour 维度单行 24 桶） |
  | grid[][].start | string | 桶起始 ISO-8601 |
  | grid[][].end | string | 桶结束 ISO-8601 |
  | grid[][].hour | number | 本地小时（hour 维度）或星期序号（day 维度） |
  | grid[][].activeMinutes | number | 恒 0（占位） |
  | grid[][].totalEvents | number | hour 维度为事件数，day 维度恒 0 |
  | grid[][].intensityScore | number | hour 维度按比例分摊的按键数，day 维度=当日按键数 |
  | dimension | string | 回显维度 |
  | maxKeyCount | number | 区间内最大按键数（无数据为 1） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:725-743`（服务 `Services/PcTrackerService.cs:675-750`）；DTO `DTOs/PcTrackerDtos.cs:233-237`；前端 `src/client-web/src/api/pcTracker.ts:29-33`
- 备注：hour 维度合并 AW 与 tracker window 事件并跨来源去重（#303，PcTrackerService.cs:688-710）。

### GET /api/v1/pc/activity-analysis
- 用途：按时间块（默认 60 分钟）分析当日活动强度、待分类数量与上下文切换。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日，缺省今天 |
  | blockMinutes | number | 否 | 块大小（分钟），默认 60 |
  | force | boolean | 否 | 默认 false；true 跳过聚合缓存 |
- 响应 data：`PcActivityAnalysisResponse`
  | 字段 | 类型 | 说明 |
  | date | string | 业务日 |
  | blockMinutes | number | 实际块大小 |
  | blocks | PcActivityAnalysisBlockDto[] | 时间块列表 |
  | blocks[].start | string | 块开始 ISO-8601 |
  | blocks[].end | string | 块结束 ISO-8601 |
  | blocks[].intensityScore | number | 强度分 |
  | blocks[].activeDurationSeconds | number | 活跃时长秒 |
  | blocks[].pendingClassificationCount | number | 待分类记录数 |
  | blocks[].contextSwitchCount | number | 上下文切换次数 |
  | blocks[].categoryChangeCount | number | 分类变化次数 |
  | blocks[].categories | PcActivityAnalysisCategoryDto[] | 块内分类分布 |
  | blocks[].categories[].categoryName | string | 分类名 |
  | blocks[].categories[].color | string | 颜色 |
  | blocks[].categories[].durationSeconds | number | 时长秒 |
  | blocks[].apps | PcActivityAnalysisAppDto[] | 块内应用分布 |
  | blocks[].apps[].appName | string | 应用名 |
  | blocks[].apps[].durationSeconds | number | 时长秒 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:495-522`；DTO `DTOs/ActivityClassificationDtos.cs:155-178`；前端 `src/client-web/src/api/pcTracker.ts:188-190、282-286`

---

## 活动分析聚合

> 本组统一查询：`date` 单日与 `start`&`end` 范围二选一；`timezone` 默认 Asia/Shanghai（DTOs/PcAggregationDtos.cs:3-4）；均支持 `force` 跳过聚合缓存；参数非法或格式错误返回 400。

### GET /api/v1/pc/aggregation/focus-blocks
- 用途：专注块列表（连续高专注时段及主要应用）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（今日页 PC 概览区块 TodayPcOverview、PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日模式 |
  | start | string | 否 | 范围模式起始 |
  | end | string | 否 | 范围模式结束 |
  | timezone | string | 否 | IANA 时区，缺省 Asia/Shanghai |
  | force | boolean | 否 | 默认 false |
- 响应 data：`PcFocusBlocksResponse`
  | 字段 | 类型 | 说明 |
  | items | PcFocusBlockItem[] | 专注块列表 |
  | items[].startUtc | string | 开始（UTC 偏移 ISO-8601） |
  | items[].endUtc | string | 结束（UTC 偏移 ISO-8601） |
  | items[].startLocal | string | 本地开始时间字符串 |
  | items[].endLocal | string | 本地结束时间字符串 |
  | items[].durationMinutes | number | 时长（分钟） |
  | items[].mainApp | string | 主应用 |
  | items[].topApps | PcAggregationAppMinutes[] | 应用时长 Top |
  | items[].topApps[].name | string | 应用名 |
  | items[].topApps[].minutes | number | 分钟数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:970-1005`；DTO `DTOs/PcAggregationDtos.cs:7-14`；前端 `src/client-web/src/api/pcTracker.ts:447-450、462-464`

### GET /api/v1/pc/aggregation/app-usage
- 用途：应用时长排行与总时长。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage、应用条形图组件 PcAppGradientBar、展览数据钩子 useExhibitionData）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日模式 |
  | start | string | 否 | 范围模式起始 |
  | end | string | 否 | 范围模式结束 |
  | timezone | string | 否 | IANA 时区，缺省 Asia/Shanghai |
  | limit | number | 否 | 返回条数上限 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`PcAppUsageResponse`
  | 字段 | 类型 | 说明 |
  | items | PcAppUsageItem[] | 应用时长条目 |
  | items[].appName | string | 进程名 |
  | items[].displayName | string \| null | 显示名 |
  | items[].totalMinutes | number | 总时长（分钟） |
  | items[].percentage | number | 占比 |
  | totalMinutes | number | 全部应用总分钟数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1007-1044`；DTO `DTOs/PcAggregationDtos.cs:17-19`；前端 `src/client-web/src/api/pcTracker.ts:451-452、466-468`

### GET /api/v1/pc/aggregation/late-night
- 用途：深夜使用统计（按业务日）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日模式 |
  | start | string | 否 | 范围模式起始 |
  | end | string | 否 | 范围模式结束 |
  | timezone | string | 否 | IANA 时区，缺省 Asia/Shanghai |
  | force | boolean | 否 | 默认 false |
- 响应 data：`PcLateNightResponse`
  | 字段 | 类型 | 说明 |
  | items | PcLateNightDayItem[] | 按业务日条目 |
  | items[].date | string | 业务日 |
  | items[].minutes | number | 深夜使用分钟数 |
  | items[].hadActivity | boolean | 是否有活动 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1046-1081`；DTO `DTOs/PcAggregationDtos.cs:22-24`；前端 `src/client-web/src/api/pcTracker.ts:454-455、470-472`

### GET /api/v1/pc/aggregation/category-distribution
- 用途：分类时长分布。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（今日页 PC 概览区块 TodayPcOverview、PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日模式 |
  | start | string | 否 | 范围模式起始 |
  | end | string | 否 | 范围模式结束 |
  | timezone | string | 否 | IANA 时区，缺省 Asia/Shanghai |
  | force | boolean | 否 | 默认 false |
- 响应 data：`PcCategoryDistributionResponse`
  | 字段 | 类型 | 说明 |
  | items | PcCategoryDistributionItem[] | 分类分布条目 |
  | items[].categoryName | string | 分类名 |
  | items[].color | string | 颜色 |
  | items[].minutes | number | 分钟数 |
  | items[].percentage | number | 占比 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1083-1118`；DTO `DTOs/PcAggregationDtos.cs:27-29`；前端 `src/client-web/src/api/pcTracker.ts:457-458、474-476`

---

## 分类与标注

### GET /api/v1/pc/categories
- 用途：旧版应用→分类规则（AppCategoryRule 平铺列表，按优先级倒序）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端封装 getPcCategories 保留但无页面调用）
- Query 参数：无
- 响应 data：`AppCategoryRule[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 规则 ID |
  | appPattern | string | 应用匹配模式 |
  | categoryName | string | 分类名 |
  | color | string | 颜色 |
  | priority | number | 优先级 |
  | isBuiltin | boolean | 是否内置 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:370-376`；DTO `DTOs/PcTrackerDtos.cs:139-146`；前端 `src/client-web/src/api/pcTracker.ts:172-174`
- 备注：与下方 `GET /pc/categories/`（树分组，JWT）路径仅差一个尾斜杠、响应结构完全不同，重建时应合并为一套。

### GET /api/v1/pc/categories/
- 用途：分类树（与 `/pc/categories/tree` 同一实现，返回 CategoryTreeNode 树）。
- 认证：JWT（catRead 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1124-1130）
- Web 前端使用：否（前端一律请求 `/pc/categories/tree`）
- Query 参数：无
- 响应 data：`CategoryTreeNode[]`（字段同 GET /pc/categories/tree）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1124-1130`；DTO `DTOs/Phase2Dtos.cs:3-14`；前端 `src/client-web/src/api/pcTracker.ts:319-321`（仅 tree 版）
- 备注：清单外发现：与 `/tree` 重复注册（同一 handler `svc.GetTreeAsync`）。

### GET /api/v1/pc/categories/tree
- 用途：分类树（父子结构）。
- 认证：JWT（catRead 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1132-1138）
- Web 前端使用：是（分类树页 CategoryTreePage、PC 追踪总览页 PcTrackerPage）
- Query 参数：无
- 响应 data：`CategoryTreeNode[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 分类 ID |
  | parentId | string \| null | 父分类 ID |
  | name | string | 分类名 |
  | color | string | 颜色（默认 `#64748b`） |
  | icon | string \| null | 图标（emoji） |
  | productivity | string | 生产力属性，取值 `productive \| neutral \| distracting`（种子示例 PcCategoryService.cs:148-152） |
  | sortOrder | number | 排序号 |
  | isBuiltin | boolean | 是否内置 |
  | children | CategoryTreeNode[] | 子分类（递归） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1132-1138`；DTO `DTOs/Phase2Dtos.cs:3-14`；前端 `src/client-web/src/api/pcTracker.ts:297-321`

### GET /api/v1/pc/categories/dictionary
- 用途：分类字典（标注场景用的平铺 id/名称/颜色/图标列表）。
- 认证：JWT（catRead 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1140-1146）
- Web 前端使用：是（标注队列组件 LabelingQueue、首次标注向导 FirstLabelingWizard）
- Query 参数：无
- 响应 data：`CategoryDictionaryItemDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 分类 ID |
  | name | string | 分类名 |
  | color | string | 颜色 |
  | icon | string \| null | 图标 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1140-1146`；DTO `DTOs/ActivityLabelingDtos.cs:13`；前端 `src/client-web/src/api/classificationLabeling.ts:39-41`

### POST /api/v1/pc/categories
- 用途：旧版应用→分类规则保存（按 appPattern upsert）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、378-387）
- Web 前端使用：是（分类树页 CategoryTreePage 的 saveCategory 也请求同一路径 `/pc/categories`，见备注）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | appPattern | string | 是 | 应用匹配模式（upsert 键，PcTrackerService.cs:634-645） |
  | categoryName | string | 是 | 分类名 |
  | color | string | 是 | 颜色 |
  | priority | number | 是 | 优先级 |
- 响应 data：`AppCategoryRule`（字段同 GET /pc/categories 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:378-387`（服务 `Services/PcTrackerService.cs:634-662`）；DTO `DTOs/PcTrackerDtos.cs:226-231`；前端 `src/client-web/src/api/pcTracker.ts:288-290`（savePcCategory，无页面调用）、`323-325`（saveCategory）
- 备注：同一路径存在两套注册（本节 legacy SaveCategoryRequest 与下方树版 CategorySaveRequest 仅差尾斜杠）；前端 saveCategory 实际请求不带尾斜杠的 `/pc/categories`，落到的具体端点由路由解析，重建时需合并两套语义。成功后按前缀清空 `/api/v1/pc/` 聚合缓存（PcTrackerModule.cs:385）。

### POST /api/v1/pc/categories/
- 用途：分类树节点保存（新建或更新）。
- 认证：JWT（catWrite 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1148-1157）
- Web 前端使用：是（分类树页 CategoryTreePage，实际请求路径 `/pc/categories`，见上节备注）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 否 | 传入则更新，缺省新建 |
  | parentId | string \| null | 否 | 父分类 ID |
  | name | string | 是 | 分类名 |
  | color | string | 否 | 颜色，默认 `#64748b` |
  | icon | string \| null | 否 | 图标 |
  | productivity | string | 否 | `productive \| neutral \| distracting`，默认 neutral |
  | sortOrder | number | 否 | 排序号 |
- 响应 data：`CategoryTreeNode`（字段同 GET /pc/categories/tree 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1148-1157`；DTO `DTOs/Phase2Dtos.cs:16-25`；前端 `src/client-web/src/api/pcTracker.ts:309-325`

### DELETE /api/v1/pc/categories/{id}
- 用途：删除分类（两套注册并存：legacy 应用规则删除与树节点删除，路径相同）。
- 认证：JWT（writeGroup PcTrackerModule.cs:61-62、389-400；catWrite PcTrackerModule.cs:1121-1122、1159-1177）
- Web 前端使用：是（分类树页 CategoryTreePage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 分类/规则 ID（树版带 `:guid` 路由约束） |
- Body：无
- 响应 data：`string`（成功 `"已删除"`；legacy 版删除失败或内置项 404 `"不存在或为内置项"`，树版 400 `"内置项不可删除或不存在"`；树版另有 InvalidOperationException 时 409）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:389-400（legacy）、1159-1177（树版）`；前端 `src/client-web/src/api/pcTracker.ts:292-294（deletePcCategory）、327-329（deleteCategory）`
- 备注：树版删除时会级联处理子节点冲突（409），见 `Services/PcCategoryService.cs`。

### PUT /api/v1/pc/categories/reorder
- 用途：批量更新分类树排序与父子关系。
- 认证：JWT（catWrite 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1179-1188）
- Web 前端使用：否（前端未调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | items | ReorderItem[] | 是 | 排序条目列表 |
  | items[].id | string (uuid) | 是 | 分类 ID |
  | items[].parentId | string \| null | 否 | 新父分类 ID |
  | items[].sortOrder | number | 是 | 新排序号 |
- 响应 data：`string`（`"排序已更新"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1179-1188`；DTO `DTOs/Phase2Dtos.cs:27-37`；前端无封装
- 备注：上报方仅 Web 管理（当前前端未接入）。

### POST /api/v1/pc/categories/seed
- 用途：初始化内置分类种子数据。
- 认证：JWT（catWrite 分组挂 RequireAuthorization，PcTrackerModule.cs:1121-1122、1190-1198）
- Web 前端使用：是（分类树页 CategoryTreePage）
- Body：无
- 响应 data：`string`（`"种子数据已初始化"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1190-1198`；前端 `src/client-web/src/api/pcTracker.ts:331-333`

### GET /api/v1/pc/classification/rules
- 用途：活动分类规则列表。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 分类页 PcClassificationPage）
- Query 参数：无
- 响应 data：`ActivityClassificationRuleDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 规则 ID |
  | ruleName | string | 规则名 |
  | scope | string | 作用域（如 `activity`） |
  | categoryName | string \| null | 目标分类名 |
  | categoryId | string \| null | 目标分类 ID |
  | projectTag | string \| null | 项目标签 |
  | color | string | 颜色 |
  | priority | number | 优先级 |
  | source | string | 规则来源 |
  | status | string | 状态（如 `active`，ActivitySuggestionService.cs:306） |
  | conditionsJson | string | 条件 JSON（规则条件模型：field/op/value） |
  | confidence | number | 置信度 |
  | explanation | string \| null | 解释 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:402-408`；DTO `DTOs/ActivityClassificationDtos.cs:42-55`；前端 `src/client-web/src/api/pcTracker.ts:176-177、192-194`

### POST /api/v1/pc/classification/rules
- 用途：保存活动分类规则。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、524-544）
- Web 前端使用：否（仅路径常量 pcClassificationApiPaths.rules；规则创建当前经由建议 accept/apply 链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | ruleName | string | 是 | 规则名（服务端按 target 截断 48 字符+短哈希保证幂等，ActivityLabelingService.cs:726+） |
  | scope | string | 是 | 作用域 |
  | categoryName | string \| null | 否 | 目标分类名 |
  | projectTag | string \| null | 否 | 项目标签 |
  | color | string | 是 | 颜色 |
  | priority | number | 是 | 优先级 |
  | conditionsJson | string | 是 | 条件 JSON |
  | confidence | number | 是 | 置信度 |
  | explanation | string \| null | 否 | 解释 |
- 响应 data：`ActivityClassificationRuleDto`（字段同 GET /pc/classification/rules 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:524-544`；DTO `DTOs/ActivityClassificationDtos.cs:57-66`；前端 `src/client-web/src/api/pcTracker.ts:177`
- 备注：参数错误 400；规则冲突 409。成功后清空 `/api/v1/pc/` 聚合缓存。

### POST /api/v1/pc/classification/rules/preview
- 用途：预演规则在指定范围内会影响哪些记录（不落库）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、546-560）
- Web 前端使用：否（前端封装 previewActivityClassificationRule 存在但无页面调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | rule | SaveActivityClassificationRuleRequest | 是 | 规则（字段同 POST /pc/classification/rules） |
  | range | ActivityClassificationApplyRangeRequest | 是 | 应用范围 |
  | range.mode | string | 是 | `today` \| `range`（前端枚举 appKnowledge.ts:94） |
  | range.dateFrom | string \| null | 否 | mode=range 时起始日 |
  | range.dateTo | string \| null | 否 | mode=range 时结束日 |
- 响应 data：`ActivityClassificationPreviewDto`
  | 字段 | 类型 | 说明 |
  | affectedRecordCount | number | 影响记录数 |
  | affectedDurationSeconds | number | 影响时长秒 |
  | currentCategoryCounts | Record<string, number> | 现分类→条数 |
  | newCategoryCounts | Record<string, number> | 新分类→条数 |
  | samples | PcDetailRecord[] | 影响样例记录（字段结构同 GET /pc/detail items） |
  | requiresConfirmation | boolean | 是否需二次确认 |
  | summary | string | 摘要文案 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:546-560`；DTO `DTOs/ActivityClassificationDtos.cs:104-120`；前端 `src/client-web/src/api/pcTracker.ts:247-255`
- 备注：参数错误 400。

### POST /api/v1/pc/classification/rules/apply
- 用途：应用规则并重算指定范围内记录的分类（落库）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、562-582）
- Web 前端使用：否（前端封装 applyActivityClassificationRule 存在但无页面调用）
- Body：同 POST /pc/classification/rules/preview（`{ rule, range }`，DTOs/ActivityClassificationDtos.cs:122-124）
- 响应 data：`ActivityClassificationPreviewDto`（字段同上）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:562-582`；前端 `src/client-web/src/api/pcTracker.ts:257-265`
- 备注：400/409；成功后清空聚合缓存。

### GET /api/v1/pc/classification/suggestions
- 用途：v1 分类建议（按指定日待分类明细聚类生成）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日，缺省今天 |
- 响应 data：`ActivityClassificationSuggestionDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 建议 ID |
  | clusterKey | string | 聚类键 |
  | sampleCount | number | 样本数 |
  | totalDurationSeconds | number | 总时长秒 |
  | sampleRecordsJson | string | 样本记录 JSON |
  | sanitizedContextJson | string | 脱敏上下文 JSON |
  | currentCategory | string \| null | 当前分类 |
  | suggestedCategory | string \| null | 建议分类 |
  | suggestedProjectTag | string \| null | 建议项目标签 |
  | suggestedRulesJson | string \| null | 建议规则 JSON |
  | userFeedback | string \| null | 用户反馈 |
  | llmResponseJson | string \| null | LLM 响应 JSON |
  | status | string | 状态：`pending` \| `accepted` \| `rejected`（ActivitySuggestionService.cs:11、265、401） |
  | appDisplayName | string \| null | 应用显示名 |
  | appIcon | string \| null | 应用图标 |
  | recognitionSource | string \| null | 识别来源 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:418-449`；DTO `DTOs/ActivityClassificationDtos.cs:68-84`；前端 `src/client-web/src/api/pcTracker.ts:180、196-200`
- 备注：服务端先取当日明细（page=1, pageSize=500），筛出 `classificationSource=fallback` 或 `classificationConfidence<0.5` 的记录再聚类（PcTrackerModule.cs:1320-1324 NeedsClassificationSuggestion）。

### GET /api/v1/pc/classification/suggestions/v2
- 用途：v2 分类建议（基于应用签名/域名知识库/启发式的聚类建议）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端封装 getSuggestionsV2 无页面调用）
- Query 参数：无
- 响应 data：`ActivityClassificationSuggestionV2Dto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 建议 ID |
  | clusterKey | string | 聚类键 |
  | processName | string \| null | 进程名 |
  | domain | string \| null | 域名 |
  | appDisplayName | string \| null | 应用显示名 |
  | appIcon | string \| null | 应用图标 |
  | currentCategory | string \| null | 当前分类 |
  | recommendedCategoryName | string \| null | 推荐分类名 |
  | recommendedCategoryId | string \| null | 推荐分类 ID |
  | recommendedProductivity | string \| null | 推荐生产力属性 |
  | confidence | number | 置信度 |
  | recognitionSource | string | 识别来源：`heuristic` \| `domain-knowledge` \| 签名 Source 值（ActivitySuggestionService.cs:168、178、198） |
  | isOnlineLookup | boolean | 是否来自在线查询（source=online，ActivitySuggestionService.cs:179） |
  | totalDurationSeconds | number | 总时长秒 |
  | sampleCount | number | 样本数 |
  | status | string | `pending` \| `accepted` \| `rejected` |
  | createdAt | string | 创建时间 ISO-8601 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:410-416`；DTO `DTOs/ActivityClassificationDtos.cs:258-275`；前端 `src/client-web/src/api/pcTracker.ts:479-481`

### POST /api/v1/pc/classification/suggestions/batch-accept
- 用途：批量接受 v2 建议（可批量建规则）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、674-683）
- Web 前端使用：否（前端封装 batchAcceptSuggestions 无页面调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | items | BatchAcceptItem[] | 是 | 待接受建议列表 |
  | items[].suggestionId | string (uuid) | 是 | 建议 ID |
  | items[].categoryId | string \| null | 否 | 指定分类 ID |
  | items[].categoryName | string \| null | 否 | 指定分类名 |
  | items[].createRule | boolean | 否 | 是否创建规则，默认 true |
- 响应 data：`BatchAcceptResultDto`
  | 字段 | 类型 | 说明 |
  | acceptedCount | number | 接受数 |
  | rulesCreatedCount | number | 创建规则数 |
  | failuresCount | number | 失败数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:674-683`；DTO `DTOs/ActivityClassificationDtos.cs:277-289`；前端 `src/client-web/src/api/pcTracker.ts:483-485`

### POST /api/v1/pc/classification/suggestions/{id}/preview
- 用途：预演"按建议给定的分类/标签重算"的影响面（并生成规则草稿）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、597-621）
- Web 前端使用：否（前端封装 previewActivityClassificationSuggestion 无页面调用；有页面调用的是 app-knowledge 同构预览，见下）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 建议 ID |
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | categoryName | string \| null | 否 | 目标分类名 |
  | projectTag | string \| null | 否 | 项目标签 |
  | range | ActivityClassificationApplyRangeRequest | 是 | `{ mode, dateFrom?, dateTo? }` |
- 响应 data：`ActivityClassificationSuggestionPreviewDto`
  | 字段 | 类型 | 说明 |
  | rule | SaveActivityClassificationRuleRequest | 由建议生成的规则草稿 |
  | preview | ActivityClassificationPreviewDto | 影响面预演（字段同 rules/preview） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:597-621`；DTO `DTOs/ActivityClassificationDtos.cs:126-138`；前端 `src/client-web/src/api/pcTracker.ts:181、227-235`
- 备注：404 建议不存在；400 参数错误；409 冲突。

### POST /api/v1/pc/classification/suggestions/{id}/apply
- 用途：按建议实际应用分类并重算（生成审计记录）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、623-649）
- Web 前端使用：否（前端封装 applyActivityClassificationSuggestion 无页面调用；有页面调用的是 app-knowledge apply）
- Path 参数：同 preview（`id`）
- Body：同 preview（`{ categoryName?, projectTag?, range }`）
- 响应 data：`ActivityClassificationSuggestionApplyDto`
  | 字段 | 类型 | 说明 |
  | rule | ActivityClassificationRuleDto | 落库规则 |
  | preview | ActivityClassificationPreviewDto | 实际影响面 |
  | auditId | string (uuid) | 审计 ID |
  | suggestionStatus | string | 建议最新状态 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:623-649`；DTO `DTOs/ActivityClassificationDtos.cs:131-144`；前端 `src/client-web/src/api/pcTracker.ts:182、237-245`
- 备注：404/400/409 同 preview；成功后清空聚合缓存。

### POST /api/v1/pc/classification/suggestions/{id}/accept
- 用途：直接接受建议（按请求体给定内容创建规则）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、651-672）
- Web 前端使用：否（前端封装 acceptActivityClassificationSuggestion 无页面调用）
- Path 参数：同 preview（`id`）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | ruleName | string | 是 | 规则名 |
  | scope | string | 是 | 作用域 |
  | categoryName | string \| null | 否 | 分类名 |
  | projectTag | string \| null | 否 | 项目标签 |
  | color | string | 是 | 颜色 |
  | priority | number | 是 | 优先级 |
  | conditionsJson | string | 是 | 条件 JSON |
  | confidence | number | 是 | 置信度 |
  | explanation | string \| null | 否 | 解释 |
  （前端封装只传 ruleName/scope/categoryName/conditionsJson 四字段，pcTracker.ts:207-225）
- 响应 data：`ActivityClassificationRuleDto`（字段同 GET /pc/classification/rules 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:651-672`；DTO `DTOs/ActivityClassificationDtos.cs:86-95`；前端 `src/client-web/src/api/pcTracker.ts:207-225`
- 备注：404 不存在；409 冲突。

### POST /api/v1/pc/classification/suggestions/{id}/reject
- 用途：拒绝建议。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、685-705）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 建议 ID |
- Body：无（前端发送空对象 `{}`，pcTracker.ts:203）
- 响应 data：`string`（`"已拒绝"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:685-705`；前端 `src/client-web/src/api/pcTracker.ts:202-205`
- 备注：404 不存在；409 冲突。

### GET /api/v1/pc/classification/queue
- 用途：待标注队列（按未覆盖的应用/域名聚合）或向导候选。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（标注队列组件 LabelingQueue、首次标注向导 FirstLabelingWizard、今日页分类建议区块 TodayClassificationSuggestionsSection）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | limit | number | 是 | 返回条数（前端默认 20/50/1 视场景） |
  | mode | string | 否 | `queue`（默认）\| `wizard`；wizard 返回 Top N 高频应用候选（含已分类，附 currentCategory），跳过"排除已覆盖"步骤（ActivityLabelingService.cs:254-283） |
- 响应 data：`ActivityLabelingQueueResponse`
  | 字段 | 类型 | 说明 |
  | items | ActivityLabelingQueueItem[] | 队列条目 |
  | items[].targetType | string | `app` \| `domain` \| `mobile_app` |
  | items[].target | string | 目标（进程名或域名） |
  | items[].displayName | string | 显示名 |
  | items[].minutes | number | 累计分钟数 |
  | items[].sampleTitles | string[] | 样本标题 |
  | items[].currentCategory | string \| null | 当前分类（wizard 模式） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:467-476`；DTO `DTOs/ActivityLabelingDtos.cs:3-6`；前端 `src/client-web/src/api/classificationLabeling.ts:35-37`
- 备注：limit 服务端未做上限钳制（直接透传 BuildQueueAsync）；userId 取自当前登录用户（ICurrentUserService，匿名时可能为空）。

### POST /api/v1/pc/classification/label
- 用途：提交标注（把应用/域名/移动应用归入分类，按需创建规则）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、478-493）
- Web 前端使用：是（标注队列组件 LabelingQueue、首次标注向导 FirstLabelingWizard）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | targetType | string | 是 | `app` \| `domain` \| `mobile_app`（服务端小写归一化，ActivityLabelingService.cs:702-703） |
  | target | string | 是 | 目标（进程名或域名），空报 400 |
  | categoryId | string (uuid) \| null | 否 | 指定分类 ID |
  | categoryName | string \| null | 否 | 指定分类名 |
  | scope | string | 是 | `all` \| `keyword`（缺省归一化为 all，ActivityLabelingService.cs:713-716） |
  | keyword | string \| null | 否 | scope=keyword 时必填（缺失 400）；mobile_app 不支持 keyword（ActivityLabelingService.cs:37） |
- 响应 data：`ActivityLabelingResponse`
  | 字段 | 类型 | 说明 |
  | ok | boolean | 是否成功 |
  | categoryId | string \| null | 命中/创建的分类 ID |
  | categoryName | string \| null | 分类名 |
  | created | string | 创建说明（分类是否新建等） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:478-493`；DTO `DTOs/ActivityLabelingDtos.cs:8-11`；前端 `src/client-web/src/api/classificationLabeling.ts:43-45`
- 备注：app+all→LabelAppAsync，app+keyword→LabelAppKeywordAsync，domain→LabelDomainAsync，mobile_app→LabelMobileAppAsync（ActivityLabelingService.cs:44-49）；参数错误 400。

### GET /api/v1/pc/classification/settings
- 用途：读取分类全局设置（推荐最短分类时长）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（PC 分类页 PcClassificationPage）
- Query 参数：无
- 响应 data：`ActivityClassificationSettingsDto`
  | 字段 | 类型 | 说明 |
  | recommendedMinimumClassificationDurationMinutes | number | 推荐最短分类时长（分钟），影响时间线平滑与建议聚类 |
  | supportedRecommendedMinimumDurations | number[] | 可选档位（如 `[1,3,5,10,15]`） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:459-465`；DTO `DTOs/ActivityClassificationDtos.cs:97-99`；前端 `src/client-web/src/api/pcTracker.ts:267-269`

### PUT /api/v1/pc/classification/settings
- 用途：更新推荐最短分类时长。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、584-595）
- Web 前端使用：是（PC 分类页 PcClassificationPage）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | recommendedMinimumClassificationDurationMinutes | number | 是 | 目标分钟数 |
- 响应 data：`ActivityClassificationSettingsDto`（字段同上）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:584-595`；DTO `DTOs/ActivityClassificationDtos.cs:101-102`；前端 `src/client-web/src/api/pcTracker.ts:271-276`
- 备注：成功后清空聚合缓存。

### GET /api/v1/pc/classification/project-tags/recent
- 用途：最近使用的项目标签（建议/标注输入联想）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端封装 getRecentActivityProjectTags 无页面调用）
- Query 参数：无
- 响应 data：`string[]`（项目标签列表）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:451-457`；前端 `src/client-web/src/api/pcTracker.ts:185、278-280`

### POST /api/v1/pc/classification/recompute
- 用途：按范围重算全部分类快照（不带新规则）。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、707-723）
- Web 前端使用：否（前端仅有路径常量 pcClassificationApiPaths.recompute，无调用方）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | range | ActivityClassificationApplyRangeRequest | 是 | `{ mode, dateFrom?, dateTo? }` |
- 响应 data：`ActivityClassificationRecomputeDto`
  | 字段 | 类型 | 说明 |
  | recomputedRecordCount | number | 重算记录数 |
  | recomputedDurationSeconds | number | 重算时长秒 |
  | auditId | string (uuid) | 审计 ID |
  | summary | string | 摘要 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:707-723`；DTO `DTOs/ActivityClassificationDtos.cs:146-153`；前端 `src/client-web/src/api/pcTracker.ts:183`
- 备注：参数错误 400；成功后清空聚合缓存。另有后台快照任务每 30 分钟运行（PcTrackerModule.cs:1299-1302）。

---

## 应用知识库

> 本分组读写在源码中均挂 RequireAuthorization（PcTrackerModule.cs:746-747），即全部端点要求 JWT。

### GET /api/v1/pc/app-knowledge/apps
- 用途：知识库应用列表（含上下文数量与近期影响时长）。
- 认证：JWT（appKnowledgeRead 分组，PcTrackerModule.cs:746、749-756）
- Web 前端使用：是（应用知识库页 AppKnowledgeBasePage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | search | string | 否 | 按进程名/显示名搜索 |
- 响应 data：`AppKnowledgeAppDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 应用签名 ID |
  | processName | string | 进程名 |
  | displayName | string | 显示名 |
  | categoryPath | string \| null | 分类路径 |
  | productivity | string \| null | 生产力属性 |
  | description | string \| null | 描述 |
  | source | string | 来源（如 builtin/online/user） |
  | confidence | number | 置信度 |
  | icon | string \| null | 图标 |
  | lastSeenAt | string \| null | 最近出现时间 |
  | createdAt | string | 创建时间 |
  | contextCount | number | 关联上下文数 |
  | pendingContextCount | number | 待确认上下文数 |
  | recentAffectedDurationSeconds | number | 近期影响时长秒 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:749-756`；DTO `DTOs/ActivityClassificationDtos.cs:202-216`；前端 `src/client-web/src/api/appKnowledge.ts:67-77`

### GET /api/v1/pc/app-knowledge/apps/{appId}/contexts
- 用途：读取某应用的上下文知识（默认分类/标题/域名等模式规则）。
- 认证：JWT（appKnowledgeRead 分组，PcTrackerModule.cs:746、758-765）
- Web 前端使用：是（应用知识库页 AppKnowledgeBasePage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | appId | string (uuid) | 是 | 应用签名 ID |
- 响应 data：`AppKnowledgeContextDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 上下文 ID |
  | appId | string \| null | 所属应用 ID |
  | processName | string | 进程名 |
  | patternType | string | 模式类型：`app-default` \| `domain` \| `title` \| `url-path` \| `source-family`（前端枚举 appKnowledge.ts:4） |
  | patternValue | string | 模式值 |
  | targetCategoryName | string \| null | 目标分类名 |
  | projectTag | string \| null | 项目标签 |
  | scopeSummary | string | 作用范围摘要 |
  | source | string | 来源 |
  | confidence | number | 置信度 |
  | enabled | boolean | 是否启用 |
  | affectedRecordCount | number | 影响记录数 |
  | affectedDurationSeconds | number | 影响时长秒 |
  | lastMatchedAt | string \| null | 最近匹配时间 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:758-765`；DTO `DTOs/ActivityClassificationDtos.cs:218-232`；前端 `src/client-web/src/api/appKnowledge.ts:69、79-81`

### POST /api/v1/pc/app-knowledge/contexts
- 用途：手工保存上下文知识。
- 认证：JWT（appKnowledgeWrite 分组，PcTrackerModule.cs:747、767-781）
- Web 前端使用：否（前端封装 saveAppKnowledgeContext 无页面调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | appId | string (uuid) \| null | 否 | 关联应用 ID |
  | processName | string | 是 | 进程名 |
  | patternType | string | 是 | 同上枚举 |
  | patternValue | string | 是 | 模式值 |
  | targetCategoryName | string \| null | 否 | 目标分类名 |
  | projectTag | string \| null | 否 | 项目标签 |
  | confidence | number \| null | 否 | 置信度 |
  | enabled | boolean \| null | 否 | 是否启用 |
- 响应 data：`AppKnowledgeContextDto`（字段同上）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:767-781`；DTO `DTOs/ActivityClassificationDtos.cs:234-242`；前端 `src/client-web/src/api/appKnowledge.ts:70、83-85`
- 备注：参数错误 400。

### DELETE /api/v1/pc/app-knowledge/contexts/{id}
- 用途：删除上下文知识。
- 认证：JWT（appKnowledgeWrite 分组，PcTrackerModule.cs:747、783-792）
- Web 前端使用：是（应用知识库页 AppKnowledgeBasePage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 上下文 ID |
- Body：无
- 响应 data：`string`（成功 `"已删除。"`；不存在 404 `"未找到上下文知识。"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:783-792`；前端 `src/client-web/src/api/appKnowledge.ts:87-89`

### POST /api/v1/pc/app-knowledge/suggestions/{id}/preview
- 用途：对分类建议预演并生成推荐上下文知识（含备选）。
- 认证：JWT（appKnowledgeWrite 分组，PcTrackerModule.cs:747、794-824）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage 的上下文确认流程 ContextConfirmationPanel）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 分类建议 ID |
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | categoryName | string \| null | 否 | 目标分类名 |
  | projectTag | string \| null | 否 | 项目标签 |
  | range | ActivityClassificationApplyRangeRequest | 是 | `{ mode: 'today'\|'range', dateFrom?, dateTo? }` |
- 响应 data：`AppKnowledgeSuggestionPreviewDto`
  | 字段 | 类型 | 说明 |
  | suggestionId | string (uuid) | 建议 ID |
  | recommendedContext | AppKnowledgeContextDto | 推荐写入的上下文（字段同 GET contexts 条目） |
  | alternatives | AppKnowledgeContextDto[] | 备选上下文 |
  | preview | ActivityClassificationPreviewDto | 分类影响面预演 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:794-824`；DTO `DTOs/ActivityClassificationDtos.cs:244-248`；前端 `src/client-web/src/api/appKnowledge.ts:71、91-98`
- 备注：404/400/409 语义同 classification suggestions preview。

### POST /api/v1/pc/app-knowledge/suggestions/{id}/apply
- 用途：按建议应用分类并把推荐上下文写入应用知识库（一次完成重算+知识沉淀）。
- 认证：JWT（appKnowledgeWrite 分组，PcTrackerModule.cs:747、826-888）
- Web 前端使用：是（PC 追踪总览页 PcTrackerPage）
- Path 参数：同 preview（`id`）
- Body：同 preview（`{ categoryName?, projectTag?, range }`）
- 响应 data：`AppKnowledgeSuggestionApplyDto`
  | 字段 | 类型 | 说明 |
  | suggestionId | string (uuid) | 建议 ID |
  | savedContext | AppKnowledgeContextDto | 已保存上下文 |
  | preview | ActivityClassificationPreviewDto | 实际影响面 |
  | auditId | string (uuid) | 审计 ID |
  | suggestionStatus | string | 建议最新状态 |
  | message | string | 结果消息（含写入摘要与重算条数） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:826-888`；DTO `DTOs/ActivityClassificationDtos.cs:250-256`；前端 `src/client-web/src/api/appKnowledge.ts:72、100-107`
- 备注：成功后清空聚合缓存（PcTrackerModule.cs:865）。

---

## 应用签名

> 本分组读写在源码中均挂 RequireAuthorization（PcTrackerModule.cs:890-891），即全部端点要求 JWT。

### GET /api/v1/pc/app-signatures/
- 用途：应用签名列表（可搜索）。
- 认证：JWT（kbRead 分组，PcTrackerModule.cs:890、893-900）
- Web 前端使用：否（前端封装 getAppSignatures 无页面调用）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | search | string | 否 | 关键字过滤 |
- 响应 data：`AppSignatureDto[]`
  | 字段 | 类型 | 说明 |
  | id | string (uuid) | 签名 ID |
  | processName | string | 进程名 |
  | displayName | string | 显示名 |
  | categoryPath | string \| null | 分类路径 |
  | productivity | string \| null | 生产力属性 |
  | description | string \| null | 描述 |
  | source | string | 来源 |
  | confidence | number | 置信度 |
  | icon | string \| null | 图标 |
  | lastSeenAt | string \| null | 最近出现时间 |
  | createdAt | string | 创建时间 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:893-900`；DTO `DTOs/ActivityClassificationDtos.cs:182-193`；前端 `src/client-web/src/api/appSignatures.ts:28-33`

### GET /api/v1/pc/app-signatures/count
- 用途：签名总数。
- 认证：JWT（kbRead 分组，PcTrackerModule.cs:890、930-936）
- Web 前端使用：否（前端封装 getAppSignatureCount 无页面调用）
- Query 参数：无
- 响应 data：`number`
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:930-936`；前端 `src/client-web/src/api/appSignatures.ts:35-37`

### GET /api/v1/pc/app-signatures/lookup/{processName}
- 用途：按进程名精确查找本地签名。
- 认证：JWT（kbRead 分组，PcTrackerModule.cs:890、938-947）
- Web 前端使用：否（前端封装 lookupAppSignature（appSignatures.ts）无页面调用）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | processName | string | 是 | 进程名（URL 编码） |
- 响应 data：`AppSignatureDto`（字段同上）；未找到 404 `"未找到"`
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:938-947`；前端 `src/client-web/src/api/appSignatures.ts:39-41`

### POST /api/v1/pc/app-signatures/lookup
- 用途：按进程名在线查询签名信息（本地未命中时回源）。
- 认证：JWT（kbWrite 分组，PcTrackerModule.cs:891、919-928）
- Web 前端使用：否（前端封装 lookupAppSignature（pcTracker.ts POST 版）无页面调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | processName | string | 是 | 进程名 |
- 响应 data：`AppSignatureDto`（字段同上）；未找到 404 `"未找到应用签名"`
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:919-928`；DTO `DTOs/Phase2Dtos.cs:49`；前端 `src/client-web/src/api/pcTracker.ts:495-497`

### POST /api/v1/pc/app-signatures/
- 用途：新增/更新应用签名。
- 认证：JWT（kbWrite 分组，PcTrackerModule.cs:891、949-956）
- Web 前端使用：是（应用知识库页 AppKnowledgeBasePage）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | processName | string | 是 | 进程名 |
  | displayName | string | 是 | 显示名 |
  | categoryPath | string \| null | 否 | 分类路径 |
  | productivity | string \| null | 否 | 生产力属性 |
  | description | string \| null | 否 | 描述 |
- 响应 data：`AppSignatureDto`（字段同上）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:949-956`；DTO `DTOs/ActivityClassificationDtos.cs:195-200`；前端 `src/client-web/src/api/appSignatures.ts:43-45`
- 备注：前端请求路径 `/pc/app-signatures/`（带尾斜杠，appSignatures.ts:44）；前端 SaveAppSignatureRequest 类型另含 icon/confidence 可选字段（appSignatures.ts:24-25），后端 DTO 无此二字段（多余字段被忽略）。

### DELETE /api/v1/pc/app-signatures/{id}
- 用途：删除应用签名。
- 认证：JWT（kbWrite 分组，PcTrackerModule.cs:891、958-967）
- Web 前端使用：是（应用知识库页 AppKnowledgeBasePage）
- Path 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | id | string (uuid) | 是 | 签名 ID |
- Body：无
- 响应 data：`string`（成功 `"已删除"`；内置项或不存在 400 `"内置项不可删除或不存在"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:958-967`；前端 `src/client-web/src/api/appSignatures.ts:47-49`

### GET /api/v1/pc/app-signatures/export
- 用途：导出全部应用签名（JSON）。
- 认证：JWT（kbRead 分组，PcTrackerModule.cs:890、902-908）
- Web 前端使用：否（前端封装 exportAppSignatures 无页面调用）
- Query 参数：无
- 响应 data：`AppSignatureDto[]`（字段同 GET /pc/app-signatures/ 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:902-908`；前端 `src/client-web/src/api/pcTracker.ts:487-489`

### POST /api/v1/pc/app-signatures/import
- 用途：批量导入应用签名（按进程名 upsert）。
- 认证：JWT（kbWrite 分组，PcTrackerModule.cs:891、910-917）
- Web 前端使用：否（前端封装 importAppSignatures 无页面调用）
- Body：`SaveAppSignatureRequest[]`（数组；字段同 POST /pc/app-signatures/）
- 响应 data：`object`
  | 字段 | 类型 | 说明 |
  | imported | number | 新导入条数 |
  | updated | number | 更新条数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:910-917`；前端 `src/client-web/src/api/pcTracker.ts:491-493`

---

## 键盘统计与上报（守护进程）

> 本组 POST 全部位于 writeGroup（PcTrackerModule.cs:61-62），要求 JWT；上报方为 Windows 守护进程（及 AW/浏览器插件链路），Web 前端不调用。

### POST /api/v1/pc/keystats/upload
- 用途：守护进程上传当日键鼠日汇总（upsert）。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID |
  | date | string | 是 | 统计日 |
  | keyPresses | number | 是 | 按键总数 |
  | keyPressCounts | Record<string, number> \| null | 否 | 按键名→次数 |
  | leftClicks | number | 是 | 左键次数 |
  | rightClicks | number | 是 | 右键次数 |
  | middleClicks | number | 是 | 中键次数 |
  | sideBackClicks | number | 是 | 侧键后退次数 |
  | sideForwardClicks | number | 是 | 侧键前进次数 |
  | mouseDistance | number | 是 | 鼠标距离 |
  | scrollDistance | number | 是 | 滚轮距离 |
  | peakKps | number | 是 | 峰值 KPS |
  | peakCps | number | 是 | 峰值 CPS |
  | appStats | Record<string, AppStatEntry> \| null | 否 | 按应用统计字典 |
  | appStats.*.appName | string | 是 | 进程名 |
  | appStats.*.displayName | string | 是 | 显示名 |
  | appStats.*.keyPresses | number | 是 | 按键数 |
  | appStats.*.leftClicks | number | 是 | 左键 |
  | appStats.*.rightClicks | number | 是 | 右键 |
  | appStats.*.middleClicks | number | 是 | 中键 |
  | appStats.*.sideBackClicks | number | 是 | 侧后退 |
  | appStats.*.sideForwardClicks | number | 是 | 侧前进 |
  | appStats.*.scrollDistance | number | 是 | 滚轮距离 |
- 响应 data：`string`（`"已接收"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:64-71`；DTO `DTOs/PcTrackerDtos.cs:5-32`；前端无封装
- 备注：序列化为 camelCase（keyPressCounts/appStats 等）。

### POST /api/v1/pc/keystats/samples
- 用途：守护进程上传键鼠采样点（用于 input-minute 明细与增量计算）。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | pimDeviceId | string | 是 | 设备 ID |
  | sampledAt | string | 是 | 采样时间 |
  | date | string | 是 | 统计日 |
  | keyPresses | number | 是 | 累计按键 |
  | keyPressCounts | Record<string, number> \| null | 否 | 累计按键字典 |
  | leftClicks | number | 是 | 左键 |
  | rightClicks | number | 是 | 右键 |
  | middleClicks | number | 是 | 中键 |
  | sideBackClicks | number | 是 | 侧后退 |
  | sideForwardClicks | number | 是 | 侧前进 |
  | mouseDistance | number | 是 | 鼠标距离 |
  | scrollDistance | number | 是 | 滚轮距离 |
  | peakKPS | number | 是 | 峰值 KPS（JSON 别名 `peakKPS`，DTOs/PcTrackerDtos.cs:286-289） |
  | peakCPS | number | 是 | 峰值 CPS（JSON 别名 `peakCPS`） |
  | formattedMouseDistance | string \| null | 否 | 格式化鼠标距离（JSON 别名 `formattedMouseDistance`） |
  | formattedScrollDistance | string \| null | 否 | 格式化滚轮距离（JSON 别名 `formattedScrollDistance`） |
  | appStats | Record<string, AppStatEntry> \| null | 否 | 按应用统计（结构同 keystats/upload） |
- 响应 data：`string`（`"已接收"`）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:73-80`；DTO `DTOs/PcTrackerDtos.cs:273-295`；前端无封装

### POST /api/v1/pc/aw/upload
- 用途：守护进程上传 ActivityWatch 简化事件批。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID |
  | events | AwEventEntry[] | 是 | 事件数组 |
  | events[].timestamp | string | 是 | 事件时间 |
  | events[].duration | number | 是 | 时长（秒） |
  | events[].eventType | string | 是 | `window` \| `afk` \| `web` 等 |
  | events[].appName | string \| null | 否 | 进程名 |
  | events[].windowTitle | string \| null | 否 | 窗口标题 |
  | events[].afkStatus | string \| null | 否 | AFK 状态 |
- 响应 data：`number`（接收条数）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:82-89`；DTO `DTOs/PcTrackerDtos.cs:34-46`；前端无封装

### POST /api/v1/pc/aw/upload-complete
- 用途：守护进程整包同步 AW bucket 原始事件（含 bucket 元数据）。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | pimDeviceId | string | 是 | 设备 ID |
  | awInfo | AwInfoDto \| null | 否 | AW 实例信息 |
  | awInfo.hostname | string \| null | 否 | 主机名 |
  | awInfo.version | string \| null | 否 | AW 版本 |
  | awInfo.testing | boolean | 是 | 是否测试实例 |
  | awInfo.device_id | string \| null | 否 | 设备 ID（JSON 别名 `device_id`） |
  | bucket | AwBucketDto | 是 | bucket 元数据 |
  | bucket.id | string | 是 | bucket ID |
  | bucket.name | string \| null | 否 | 名称 |
  | bucket.type | string | 是 | 类型 |
  | bucket.client | string | 是 | 客户端 |
  | bucket.hostname | string | 是 | 主机名 |
  | bucket.created | string \| null | 否 | 创建时间 |
  | bucket.last_updated | string \| null | 否 | 更新时间（JSON 别名 `last_updated`） |
  | bucket.data | Record<string, object> \| null | 否 | 附加数据 |
  | events | CompleteAwEventEntry[] | 是 | 事件数组 |
  | events[].sourceEventId | number | 是 | 源事件 ID |
  | events[].timestamp | string | 是 | 时间 |
  | events[].duration | number | 是 | 时长秒 |
  | events[].data | Record<string, object> \| null | 否 | 原始数据 |
- 响应 data：`number`（接收条数）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:91-98`；DTO `DTOs/PcTrackerDtos.cs:239-271`；前端无封装

### POST /api/v1/pc/tracker/upload
- 用途：原生 Windows 守护进程上传活动事件批。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID |
  | events | TrackerEventDto[] | 是 | 事件数组 |
  | events[].timestamp | string | 是 | 时间 |
  | events[].duration | number | 是 | 时长秒 |
  | events[].eventType | string | 是 | `window` \| `idle` 等 |
  | events[].exePath | string \| null | 否 | 可执行路径 |
  | events[].appName | string \| null | 否 | 进程名 |
  | events[].displayName | string \| null | 否 | 显示名 |
  | events[].windowTitle | string \| null | 否 | 窗口标题 |
  | events[].commandLine | string \| null | 否 | 命令行 |
  | events[].isIdle | boolean | 是 | 是否闲置 |
  | events[].isMediaActive | boolean | 是 | 是否媒体播放中 |
  | events[].url | string \| null | 否 | 网页地址 |
  | events[].domain | string \| null | 否 | 域名 |
  | events[].pagePath | string \| null | 否 | 页面路径 |
  | events[].audible | boolean \| null | 否 | 是否有声 |
  | events[].incognito | boolean \| null | 否 | 是否无痕 |
  | events[].tabCount | number \| null | 否 | 标签页数 |
  | events[].pageVisitCount | number | 是 | 页面访问次数 |
  | events[].pageVisitDuration | number | 是 | 页面访问时长 |
  | events[].rawJson | object \| null | 否 | 原始 JSON |
  | events[].date | string | 是 | 所属日期 |
  | events[].browser | string \| null | 否 | 浏览器标识 |
  | events[].instanceId | string \| null | 否 | 浏览器实例 ID |
- 响应 data：`number`（接收条数）；参数错误 400
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:100-114`；DTO `DTOs/TrackerDtos.cs:5-33`；前端无封装

### POST /api/v1/pc/tracker/health
- 用途：守护进程心跳上报（采集器运行状态、浏览器/站点通道健康）。
- 认证：JWT（writeGroup）
- Web 前端使用：否（Windows 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID |
  | status | string | 是 | 状态（如 running） |
  | uptimeSeconds | number | 是 | 运行秒数 |
  | hookActive | boolean | 是 | 钩子是否活跃 |
  | pollCount | number | 是 | 轮询计数 |
  | sessionsCreated | number | 是 | 会话创建数 |
  | eventsUploaded | number | 是 | 已上传事件数 |
  | uploadFailures | number | 是 | 上传失败数 |
  | lastError | string \| null | 否 | 最近错误 |
  | browserConnected | boolean | 是 | 浏览器通道是否连接 |
  | browserHeartbeatAgeSeconds | number \| null | 否 | 浏览器心跳年龄秒 |
  | siteConnected | boolean | 否 | 站点通道是否连接（默认 false） |
  | siteLastEventAgeSeconds | number \| null | 否 | 站点最近事件年龄秒 |
  | siteEventsUploaded | number | 否 | 站点事件上传数（默认 0） |
  | siteLastError | string \| null | 否 | 站点通道最近错误 |
- 响应 data：`string`（`"ok"`）；参数错误 400
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:116-130`；DTO `DTOs/TrackerDtos.cs:35-51`；前端无封装

### GET /api/v1/pc/tracker/health/latest
- 用途：读取最近一条守护进程心跳（状态页采集健康卡片）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（状态页 StatusPage）
- Query 参数：无
- 响应 data：`TrackerHealthEntity`
  | 字段 | 类型 | 说明 |
  | id | number | 主键 |
  | deviceId | string | 设备 ID |
  | status | string | 状态（默认 running） |
  | uptimeSeconds | number | 运行秒数 |
  | hookActive | boolean | 钩子活跃 |
  | pollCount | number | 轮询计数 |
  | sessionsCreated | number | 会话数 |
  | eventsUploaded | number | 事件上传数 |
  | uploadFailures | number | 上传失败数 |
  | lastError | string \| null | 最近错误 |
  | browserConnected | boolean | 浏览器通道连接 |
  | browserHeartbeatAgeSeconds | number \| null | 浏览器心跳年龄 |
  | siteConnected | boolean | 站点通道连接 |
  | siteLastEventAgeSeconds | number \| null | 站点最近事件年龄 |
  | siteEventsUploaded | number | 站点事件数 |
  | siteLastError | string \| null | 站点通道错误 |
  | reportedAt | string | 上报时间 |
  | createdAt / updatedAt | string | 创建/更新时间 |
  无记录时 404 `"not found"`
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:132-140`；实体 `Entities/TrackerHealthEntity.cs:7-28`；前端 `src/client-web/src/api/pcTracker.ts:513-534`
- 备注：前端 TrackerHealth 类型省略 id/createdAt/updatedAt（pcTracker.ts:513-530）。

### GET /api/v1/pc/tracker/health
- 用途：按设备读取最近一条守护进程心跳。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（状态页只用 latest 版本）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID；缺失 400 `"deviceId required"` |
- 响应 data：`TrackerHealthEntity`（字段同 latest）；无记录 404
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:142-153`；前端无封装
- 备注：上报方为 Windows 守护进程链路的状态查询通道。

---

## 浏览器站点（browser-tt）

> 上传/导入位于 writeGroup（JWT）；查询位于 readGroup（匿名）。

### POST /api/v1/pc/browser-tt/upload
- 用途：站点级停留数据上传（Time Tracker fork / 浏览器插件经守护进程通道）。
- 认证：JWT（writeGroup）
- Web 前端使用：否（time-tracker-4-browser 插件 → 守护进程上报链路）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | deviceId | string | 是 | 设备 ID（[Required]） |
  | events | SiteEventUploadDto[] | 否 | 事件数组（默认空列表） |
  | events[].kind | string | 是 | 事件种类（focus/run/media 等） |
  | events[].host | string | 是 | 站点域名 |
  | events[].startMs | number \| null | 否 | 开始毫秒时间戳 |
  | events[].endMs | number \| null | 否 | 结束毫秒时间戳 |
  | events[].durationMs | number \| null | 否 | 时长毫秒 |
  | events[].date | string \| null | 否 | 归属日 |
- 响应 data：`number`（接收条数）；参数错误 400
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:156-170`；DTO `DTOs/BrowserSiteDtos.cs:8-22`；前端无封装
- 备注：字段名与守护进程 SiteEventDto 的 JSON 契约一致（BrowserSiteDtos.cs:7）。

### POST /api/v1/pc/browser-tt/import
- 用途：历史站点数据自助导入（tt4b 备份 markdown 或记录页导出 JSON）。
- 认证：JWT（writeGroup）
- Web 前端使用：是（浏览器站点页 PcBrowserSitePage 的导入对话框 ImportDialog）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | content | string | 是 | 文件全文（[Required]） |
  | mode | string | 否 | `overwrite`（默认，按 date+host 覆盖）\| `add`（累加） |
  | deviceId | string \| null | 否 | 导入归属设备，缺省 `__imported__` |
- 响应 data：`SiteImportResultDto`
  | 字段 | 类型 | 说明 |
  | rows | number | 导入行数 |
  | dates | number | 覆盖日期数 |
  | hosts | number | 覆盖站点数 |
  | skipped | number | 跳过行数 |
  | format | string | 识别的格式 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:172-186`；DTO `DTOs/BrowserSiteDtos.cs:78-95`；前端 `src/client-web/src/api/pcBrowserSite.ts:74、89-91`
- 备注：参数错误 400。

### GET /api/v1/pc/browser-tt/summary
- 用途：站点使用汇总（总专注/访问/运行/媒体时长与 Top 站点）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（浏览器站点页 PcBrowserSitePage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日模式；与 from/to 二选一，date 优先（PcTrackerModule.cs:231） |
  | from | string | 否 | 范围起始 |
  | to | string | 否 | 范围结束 |
- 响应 data：`SiteSummaryDto`
  | 字段 | 类型 | 说明 |
  | from | string | 实际起始 |
  | to | string | 实际结束 |
  | totalFocusMs | number | 总专注毫秒 |
  | totalVisits | number | 总访问次数 |
  | totalRunMs | number | 总运行毫秒 |
  | totalMediaMs | number | 总媒体播放毫秒 |
  | siteCount | number | 站点数 |
  | topHosts | SiteTopHostDto[] | Top 站点 |
  | topHosts[].host | string | 域名 |
  | topHosts[].alias | string \| null | 别名 |
  | topHosts[].focusMs | number | 专注毫秒 |
  | topHosts[].visitCount | number | 访问次数 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:222-239`；DTO `DTOs/BrowserSiteDtos.cs:49-67`；前端 `src/client-web/src/api/pcBrowserSite.ts:71、77-79`

### GET /api/v1/pc/browser-tt/daily
- 用途：站点按日明细行。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（浏览器站点页 PcBrowserSitePage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 单日 |
  | from | string | 否 | 范围起始 |
  | to | string | 否 | 范围结束 |
- 响应 data：`SiteDailyRowDto[]`
  | 字段 | 类型 | 说明 |
  | date | string | 日期 |
  | host | string | 域名 |
  | focusMs | number | 专注毫秒 |
  | visitCount | number | 访问次数 |
  | runMs | number | 运行毫秒 |
  | mediaMs | number | 媒体毫秒 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:188-204`；DTO `DTOs/BrowserSiteDtos.cs:25-40`；前端 `src/client-web/src/api/pcBrowserSite.ts:72、81-83`
- 备注：参数错误 400。

### GET /api/v1/pc/browser-tt/timeline
- 用途：单日站点时间线块。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：是（浏览器站点页 PcBrowserSitePage）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 是 | 日期 |
- 响应 data：`SiteTimelineRowDto[]`
  | 字段 | 类型 | 说明 |
  | host | string | 域名 |
  | startMs | number | 开始毫秒时间戳 |
  | durationMs | number | 时长毫秒 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:206-220`；DTO `DTOs/BrowserSiteDtos.cs:42-47`；前端 `src/client-web/src/api/pcBrowserSite.ts:73、85-87`
- 备注：参数错误 400。

---

## 生产力

### GET /api/v1/pc/productivity/dashboard
- 用途：当日生产力仪表盘（得分、三类时长、目标达成、周趋势）。
- 认证：JWT（prodRead 分组挂 RequireAuthorization，PcTrackerModule.cs:1201、1203-1218）
- Web 前端使用：是（PC 追踪总览页内生产力面板组件 ProductivityDashboard）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日，缺省今天 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`ProductivityDashboardDto`
  | 字段 | 类型 | 说明 |
  | todayScore | number | 今日得分 |
  | productiveHours | number | 生产力时长（小时） |
  | distractingHours | number | 分心时长（小时） |
  | neutralHours | number | 中性时长（小时） |
  | targetHours | number | 目标时长（小时） |
  | goalMet | boolean | 是否达标 |
  | weeklyTrend | DailyProductivityDto[] | 周趋势 |
  | weeklyTrend[].date | string | 日期 |
  | weeklyTrend[].productiveMinutes | number | 生产力分钟 |
  | weeklyTrend[].neutralMinutes | number | 中性分钟 |
  | weeklyTrend[].distractingMinutes | number | 分心分钟 |
  | weeklyTrend[].totalMinutes | number | 总分钟 |
  | weeklyTrend[].productiveRatio | number | 生产力占比 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1203-1218`；DTO `DTOs/Phase2Dtos.cs:39-65`；前端 `src/client-web/src/api/pcTracker.ts:355-357`

### GET /api/v1/pc/productivity/range
- 用途：区间逐日生产力统计。
- 认证：JWT（prodRead 分组挂 RequireAuthorization，PcTrackerModule.cs:1201、1220-1237）
- Web 前端使用：否（前端封装 getProductivityRange 无页面调用）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | start | string | 否 | 起始日，缺省今天-7 天 |
  | end | string | 否 | 结束日，缺省今天 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`DailyProductivityDto[]`（字段同 dashboard.weeklyTrend 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1220-1237`；前端 `src/client-web/src/api/pcTracker.ts:359-361`

### GET /api/v1/pc/productivity/goals
- 用途：读取生产力目标（每日生产力小时数）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60、1240-1246）
- Web 前端使用：否（前端封装 getProductivityGoals 无页面调用）
- Query 参数：无
- 响应 data：`ProductivityGoalDto`
  | 字段 | 类型 | 说明 |
  | dailyProductiveHours | number | 每日生产力目标小时（默认 5.0） |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1240-1246`；DTO `DTOs/Phase2Dtos.cs:51-54`；前端 `src/client-web/src/api/pcTracker.ts:499-501`

### PUT /api/v1/pc/productivity/goals
- 用途：更新生产力目标。
- 认证：JWT（writeGroup，PcTrackerModule.cs:61-62、1248-1257）
- Web 前端使用：否（前端封装 updateProductivityGoals 无页面调用）
- Body：
  | 字段 | 类型 | 必填 | 说明 |
  | dailyProductiveHours | number | 是 | 每日生产力目标小时 |
- 响应 data：`ProductivityGoalDto`（字段同上）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1248-1257`；前端 `src/client-web/src/api/pcTracker.ts:503-505`
- 备注：成功后清空聚合缓存。

### GET /api/v1/pc/aw/timeline
- 用途：v1 活动时间线（AW+tracker 合并平滑）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端封装 getPcTimeline 无页面调用；Web 已改用 timeline/v2）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日，缺省今天 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`TimelineItem[]`（字段同 GET /pc/summary 的 timeline 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:258-273`（服务 `Services/PcTrackerService.cs:403-426`）；前端 `src/client-web/src/api/pcTracker.ts:21-23`
- 备注：上报方为守护进程链路写入的数据；Web 端时间线展示走 GET /pc/timeline/v2。

### GET /api/v1/pc/aw/heatmap
- 用途：v1 小时级活动热力（单层桶列表）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端封装 getPcHeatmap 无页面调用；Web 已改用 heatmap/grid）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | start | string | 否 | 起始日，缺省今天-7 天 |
  | end | string | 否 | 结束日，缺省今天 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`HeatmapBucket[]`（字段同 GET /pc/summary 的 heatmap 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:275-292`；前端 `src/client-web/src/api/pcTracker.ts:25-27`
- 备注：上报方为守护进程链路写入的数据。

### GET /api/v1/pc/keystats/range
- 用途：区间逐日键鼠统计（热力图/趋势数据源）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端无封装无调用；数据消费方为键盘热力图等面板的替代实现）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | start | string | 否 | 起始日，缺省今天-7 天 |
  | end | string | 否 | 结束日，缺省今天 |
  | force | boolean | 否 | 默认 false |
- 响应 data：`KeystatsSummary[]`（字段同 GET /pc/summary 的 keystats 条目）
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:294-311`；前端无封装
- 备注：上报方为 Windows 守护进程的 keystats 上传链路。

### GET /api/v1/pc/timeline/v2
- 用途：v2 业务日时间线（带生产力属性与置信度）。
- 认证：匿名（readGroup 未挂授权，PcTrackerModule.cs:60）
- Web 前端使用：否（前端存在两个封装 getTimelineV2/getPcTimelineV2 但当前均无页面调用）
- Query 参数：
  | 字段 | 类型 | 必填 | 说明 |
  | date | string | 否 | 业务日，缺省按服务端业务日解析（PcProductivityService.ResolveBusinessDay） |
  | force | boolean | 否 | 默认 false |
- 响应 data：`TimelineV2Item[]`
  | 字段 | 类型 | 说明 |
  | start | string | 块开始（带 +08:00 偏移的 ISO-8601，#236） |
  | end | string | 块结束（同上） |
  | appName | string | 进程名 |
  | appDisplayName | string \| null | 显示名 |
  | windowTitle | string \| null | 窗口标题 |
  | categoryName | string | 分类名 |
  | categoryColor | string \| null | 分类颜色 |
  | productivity | string | `productive \| neutral \| distracting`（默认 neutral） |
  | confidence | number | 置信度 |
  | durationMinutes | number | 时长分钟 |
- 来源：后端 `src/modules/Pim.Module.PcTracker/PcTrackerModule.cs:1259-1276`；DTO `DTOs/Phase2Dtos.cs:67-83`；前端 `src/client-web/src/api/pcTracker.ts:366-381、507-510`
- 备注：业务日固定 Asia/Shanghai 04:00 起算，服务端决定、不接受 timezone 参数；start/end 为带 +08:00 偏移的 ISO-8601，块之间互不重叠（pcTracker.ts:363-365 注释，#235/#236/#237）。

---

## 清单外补充说明

- 全模块共 71 处路由注册（grep 计数），无清单之外的额外路径；仅以下重复注册需注意：
  - `GET /pc/categories`（legacy，匿名，返回 AppCategoryRule 平铺列表）与 `GET /pc/categories/`（树分组，JWT，返回 CategoryTreeNode 树），同一路径仅尾斜杠之差、响应结构不同（PcTrackerModule.cs:370 vs 1124）。
  - `POST /pc/categories`（legacy SaveCategoryRequest）与 `POST /pc/categories/`（树版 CategorySaveRequest）（PcTrackerModule.cs:378 vs 1148）。
  - `DELETE /pc/categories/{id}` 双注册（PcTrackerModule.cs:389 writeGroup 与 1159 catWrite，后者带 `:guid` 约束与 409 分支）。
- `GET /pc/app-signatures/` 前端封装请求带尾斜杠（appSignatures.ts:32：`${basePath}/${params}`）。
