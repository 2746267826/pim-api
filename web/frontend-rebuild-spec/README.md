# PIM Web 前端重建规格（Frontend Rebuild Spec）

> **目的**：本目录是一份独立规格文档，目标是让开发者**不参考本仓库源码**，即可重新实现一个与现有 PIM Web 客户端功能对等的前端。
>
> **内容边界（严格）**：只包含三类信息——
> 1. 板块（页面）与模块清单，模块标注**形式类型**（如：热力图、甘特图、数据表格、主从面板）与一句话功能说明；
> 2. 每个模块调用的后端接口，以及前端的数据处理行为（轮询、分页、上传分片、缓存失效、URL 状态等）；
> 3. 后端开放的全部接口的字段级规格（附录 `05-api-reference/`）。
>
> **明确不包含**：任何视觉风格、颜色、字体、间距、尺寸、图标样式、动效、具体布局实现。模块"形式"只到类型级（"热力图"），不描述它长什么样（"绿色格子"）。

## 文档目录

| 文件 | 内容 |
|---|---|
| [01-global-shell.md](01-global-shell.md) | 应用形态、全局框架（侧边导航/认证/全局浮层/错误空加载态）、完整路由表 |
| [02-pages-and-modules.md](02-pages-and-modules.md) | **核心**：全部路由页面的模块清单（模块 × 形式 × 调用接口 × 前端处理） |
| [03-data-behaviors.md](03-data-behaviors.md) | 全局数据行为：轮询策略、分页模式、上传/下载通道、缓存与失效、URL 与本地持久化状态、Android 桥 |
| [04-api-conventions.md](04-api-conventions.md) | API 通用约定：基地址、响应封装、认证与角色、分页、错误码、multipart/二进制端点索引、非 REST 面 |
| [05-api-reference/](05-api-reference/) | 全部接口的字段级规格（按域分 8 个文件） |

### 05-api-reference 域文件索引

| 文件 | 覆盖路由前缀 |
|---|---|
| [auth-admin-ai.md](05-api-reference/auth-admin-ai.md) | `/api/v1/auth`、`/api/v1/admin`、`/api/v1/ai` |
| [calendar.md](05-api-reference/calendar.md) | `/api/v1/calendar`（含 Outlook 同步、数据中心、回收站、任务） |
| [files.md](05-api-reference/files.md) | `/api/v1/files`（OneDrive） |
| [mobile.md](05-api-reference/mobile.md) | `/api/v1/mobile` |
| [pc-tracker.md](05-api-reference/pc-tracker.md) | `/api/v1/pc`（含 browser-tt、app-knowledge、app-signatures） |
| [quick-notes-mcp.md](05-api-reference/quick-notes-mcp.md) | `/api/v1/quick-notes`、`/api/v1/mcp`、`/mcp`（Streamable HTTP） |
| [operations-status.md](05-api-reference/operations-status.md) | `/api/v1/operations`、`/api/v1/data-reliability`、`/api/v1/status`、`/api/v1/daemon`、`/api/v1/endpoints`、`/api/v1/today`、`/api/v1/search`、`/api/v1/tiles`、`/api/version`、`/api/client/shell/latest`、`/api/v1/stats`、`/health*`、`/metrics` |
| [ops-console.md](05-api-reference/ops-console.md) | `/api/v1/ops`（运维只读控制台，Web 前端不使用） |

## 阅读约定与词汇表

### 模块形式词汇表（02 文件中"形式"列使用的类型词）

数据展示类：**列表**（纵向条目行）、**卡片列表**（卡片堆叠）、**数据表格**（多列、可分页/排序）、**主从面板**（左列表+右详情）、**统计卡片/指标条**（少量关键数字）、**树**（层级展开）、**瀑布流卡片墙**、**时间线**（按时间轴排布的条目）、**甘特式时间条**（横向时间条形图）、**热力图**（矩阵强度网格）、**日历网格**（月/周日历）、**图表**（折线/柱状/环形/漏斗/仪表盘等 ECharts 类）、**地图轨迹**（地图上的点/线/覆盖物）、**侧板/抽屉**（从边缘滑出的面板）、**弹窗/对话框**、**向导/分步流程**、**表单**、**标签页**、**筛选栏**（输入框+下拉+开关组合）、**chips 过滤条**（可切换的短标签组）、**命令面板**、**拖拽上传区**。

交互结构类：**多选批操作**（复选+批量工具条）、**两步确认**（先"武装"再执行）、**删除影响预览**（先返回将受影响对象再确认）、**乐观本地状态**（无）、**内联编辑**、**拖放调度**（拖拽对象到时间槽）、**URL 即状态**（过滤器序列化进查询参数）、**本地持久化记忆**（localStorage）。

### 数据处理词汇表（各表"前端处理"列使用的缩略语）

- **标准轮询**：白天（06:00–23:59）每 5 分钟、夜间每 30 分钟后台刷新（`lib/autoRefresh.ts`）。
- **延迟轮询**：标准轮询的变体——用户滚动结束后的 1 秒内强制立即刷新，之后回落到标准间隔。
- **固定轮询 Ns**：不随昼夜变化的固定间隔。
- **条件轮询**：仅在特定状态/视图下轮询（如同步进行中）。
- **服务端分页/过滤/排序**：参数下发后端；**客户端过滤**：全量拉取后前端处理。
- **失效重取**：变更操作成功后按 queryKey 使缓存失效并重新拉取（本项目**不使用乐观更新**）。
- **cursor 分页**：游标式翻页（见移动位置轨迹点）。

### 接口路径约定

- 文中接口一律写完整路径，含 `/api/v1` 前缀。
- "响应 data"均指统一封装 `{ code, message, data, timestamp }` 中的 `data` 字段。
- 分页封装 `PagedResult<T>` 的实际序列化字段为 `{ items[], totalCount, page, pageSize, totalPages }`（注意是 `totalCount`；个别端点另有包装，见对应域说明）。
- "前端是否使用"以后端端点为单位标注；后端存在但 Web 前端未用的端点**仍然全部列出**（可能被 Android 客户端、Windows 守护进程或 MCP 客户端消费）。

## 现状备注（重建时可据此取舍）

- `pages/PcClassificationPage.tsx`（PC 分类规则页）存在于代码中但**未注册路由**（已被应用知识库取代），重建可跳过。
- 前端 `api/*.ts` 中存在一批**已定义但无 UI 调用**的包装函数，清单见 [03-data-behaviors.md](03-data-behaviors.md) §11；对应端点在附录中标"否"。
- 本前端无 WebSocket/SignalR；实时性全部由轮询实现。唯一的流式接口是后端 `/mcp` 的 SSE（Web UI 不消费，供 MCP 客户端使用）。
- `/embed/android/*` 两个内嵌页运行于 Android 原生壳内，通过 `window.pimAndroid` postMessage 桥获取令牌与原生采集状态，见 [03-data-behaviors.md](03-data-behaviors.md) §6。

## 基线

- 本规格整理自仓库提交 `fbf1a86e`（2026-09 时的 master）。API 若演进，以 `04-api-conventions.md` 与 `05-api-reference/` 的更新为准。
