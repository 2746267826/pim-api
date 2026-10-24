# PIM 前端工程约定（CONVENTIONS.md）

> 数据层与代码约定的事实来源。规格依据：`../frontend-rebuild-spec/`（03 数据行为、04 API 约定）。
> 违反本文件的代码不予合并；新增约定先改本文件再写代码。

## 1. 技术栈（锁定，不得擅自替换）

React 19 + TypeScript(strict) + Vite · react-router · TanStack Query v5（唯一缓存层，**无 Redux/Zustand**）· Tailwind 4 + Radix 原语（shadcn 风格自建 `components/ui`）· ECharts · FullCalendar（P1）· Leaflet（P3）· TipTap（P2）· RHF+zod · sonner。

## 2. 网络层

- **所有**请求经 `api/client.ts` 的 `apiGet/apiPost/apiPut/apiDelete/apiUpload`；URL 一律以 `/api/v1/...` 开头，经 `lib/apiBase.ts` 的 `apiUrl()` 拼接。禁止直接 `fetch(apiUrl 外的字符串)`。
- 例外（裸通道，均为规格约定）：
  - `fetchBlob()`：img/iframe 带不了认证头的下载（缩略图、导出 JSON/ICS/CSV）；
  - Graph 分片 PUT：**不带** Authorization 头（预授权 uploadUrl）；
  - `window.open`：download-url / open-link / preview-url（微软直链，不 fetch 跟随 302）；
  - 匿名端点（/api/version 等）传 `{ auth: false }`。
- **401 单飞刷新**：client.ts 内共享 Promise，禁止在业务代码里手动刷新令牌。
- **Outlook 写回**用 `{ allowStatuses: [409, 412] }`，冲突语义在 data 内（status='conflict'）。
- 刷新失败 → client 广播 `pim:unauthorized` → AuthContext 清用户 → 守卫回 /login。业务层不重复处理。

## 3. API 地址配置（v0.3 新增）

- 存储：`pim.apiBase`（协议://主机:端口，**不含** /api/v1；空 = 同源）；历史 5 条在 `pim.apiBaseHistory`。
- 壳（Capacitor/Tauri）内未配置 → `needsServerSetup()` 为真，全路由重定向 `/setup`。
- 入口：/setup（首启）、/settings/server、侧边栏底部服务器指示、登录页"更换服务器"。
- 保存后必须：`clearTokens()` + `queryClient.clear()` + 回登录页（令牌按服务器绑定）。

## 4. TanStack Query 约定

- **queryKey 工厂**：各域建 `features/<域>/queries.ts`，键形如 `['calendar','events',{start,end}]`；跨域失效时引用工厂常量，禁止手写字符串数组。
- **不乐观更新**：变更成功后按显式 key 列表 `invalidateQueries`（如 Outlook 同步后失效 13 键——集中在该域 queries.ts 的 `INVALIDATE_AFTER_SYNC` 常量）。
- **重试**：默认配置已在 providers.tsx（≤2 次、1s 间隔、4xx 不重试），不要在单个 useQuery 覆盖。
- **轮询**：`refetchInterval` 一律取 `lib/polling.ts`（标准昼夜 5min/30min、deferred、fixed、conditional）；散写数字视为 bug。
- **错误上浮**：全局 QueryCache/MutationCache onError 统一 toast（key 去重、5xx 有缓存/后台轮询时静音）。静音的查询设 `meta: { silent: true }`（如侧边栏状态点、version）。模块内已展示错误的可再加 meta.silent，避免双重提示。
- 少量定向 staleTime 允许覆盖（设备列表 60s、分类树 60s 等，跟随规格）。

## 5. 枚举与数据口径

- **数字枚举必须归一化**：`riskLevel`/`status`（确认单）、健康状态等后端输出数字——消费前一律过 `lib/enums.ts` 的 normalize 函数；TS 类型里收 `unknown`/`number | string`，不要声明成字符串枚举。
- 分页统一 `PagedResult`（**totalCount** 字段）；Outlook 同步批次端点例外（字段名 `total`），在该域 client 里适配。
- 双态端点（`GET /calendar/tasks`、`GET /calendar/events`）：带分页/筛选参数 → `PagedResult`；全缺省 → 旧版全量数组。封装函数按参数显式区分（`getTasksPaged` vs `getTasksAll`），调用方不得混淆。
- 业务日（PC/Mobile 域）：`lib/businessDay.ts`，Asia/Shanghai 04:00 切分；时间窗参数传 ISO UTC。
- 手动刷新重型聚合端点追加 `force=true`。
- 聚合缓存穿透/截断提示（206/X-Truncated 目前 Web 端不消费，忽略）。

## 6. localStorage 键名

统一 `pim.*` 前缀，新增键必须登记到 `lib/storage.ts` 的 `STORAGE_KEYS`：
accessToken / refreshToken / apiBase / apiBaseHistory / calendarLayers / fileBrowser / transferHistory / labelingCategories / quickNoteDialog / exhibition。
内嵌页例外：令牌由原生桥注入，**不落 localStorage**（03 §6）。

## 7. URL 即状态

可分享的视图状态进 searchParams（`?view=`、`?calendarId=`、`?taskBookId=`、`?view=liveness`、历史位置全部筛选、展览馆 hash+`?card=`、快速记录 `?prefill=&text=&embed=1`）。实现用 react-router `useSearchParams`；高频输入（搜索框）本地 state + 防抖后再写 URL。

## 8. UI 原语与页面

- 页面只能用 `components/ui` 原语拼装；新形态先加原语再加页面（README 词汇表 22 形式 → 原语清单见 DESIGN.md §6）。
- 颜色只经 Tailwind token 类（`bg-primary`、`text-text-3`…）或 `LIFE_CATEGORY_COLOR`/服务端下发值；**禁止裸 hex / 任意 tailwind 调色板类**（如 `bg-blue-500`）。
- 三态成对：数据模块必须有 `<Skeleton>`（loading）、`<EmptyState>`（空）、错误兜底（InlineAlert 或卡片级 ErrorBoundary）。
- 破坏性操作：`ConfirmDialog`（impact/requireText 组合）；确认中心用 `TwoStepButton`。
- 页面包一层 `PageHeader`（标题+副标题+右侧操作）；页面级错误由壳的 ErrorBoundary 兜底，卡片级在页面内自包。
- 移动端降级：表格 <640 转卡片列表；筛选栏转抽屉；编辑面板转 Drawer。触控目标 ≥44px。

## 9. 目录与命名

```
src/api        域 client + 类型（纯函数，不含 React）
src/lib        跨域工具（apiBase/polling/enums/businessDay/storage/notify）
src/app        路由、守卫、外壳、providers、nav
src/components/ui    设计系统原语（通用，无业务语义）
src/components/viz   ECharts 封装与自研图（甘特条/键盘矩阵等）
src/features/<域>    pages / components / hooks / queries（业务内聚）
src/embed/android    无壳内嵌页 + 桥客户端
```

- 文件名 kebab-case；组件 PascalCase；hooks `use-x`；域内查询文件固定 `queries.ts`。
- `import type` 强制（verbatimModuleSyntax）；路径别名一律 `@/`。
- 注释只写"代码看不出来的约束"（如端点坑位、口径来源行号），不写流水账。

## 10. 测试与验收

- vitest 单测覆盖 `lib/*` 与 `api/client.ts`（刷新单飞、HTML 识别、allowStatuses、裸对象、204）。UI 组件暂不强制。
- 每阶段交付：`tsc -b` + `vitest run` + `vite build` 三绿 + 渲染截图视觉走查（桌面 1280 / 平板 768 / 手机 375）。
- 已知坑位自检清单（实现对应页面时逐条打勾）：
  - [ ] 双态端点参数区分
  - [ ] `PagedResult.totalCount`（outlook batches 为 `total`）
  - [ ] 数字枚举归一化
  - [ ] Outlook 写回 409/412
  - [ ] 下载走 download-url + window.open（>100MB 确认）；缩略图 fetchBlob
  - [ ] 上传双通道（≤4MB multipart；>4MB Graph 会话 10MiB 分片、Content-Range、nextExpectedRanges 续传、分片 PUT 无认证头；>2GB 拒绝）
  - [ ] cursor 分页 + 栈回退（轨迹原始点 200/页）
  - [ ] 文件树逐文件夹 20 页硬上限提示
  - [ ] `/status/` 尾斜杠可达
  - [ ] accessToken 15min / refresh 7 天轮换 / 登录限流 429 + Retry-After
  - [ ] 浏览器跨域需后端 CORS 白名单；壳内走原生通道

## 11. 提交与阶段

- Conventional Commits（feat/fix/docs/chore + 作用域，如 `feat(calendar): 拖选预填日程弹窗`）。
- 六阶段：P0 骨架与标准 → P1 今日/日历/任务/确认 → P2 工作台/提醒/报告/习惯/快速记录/数据中心/审计 → P3 PC/浏览器/手机/位置/设备/状态 → P4 文件/设置域/知识库/内嵌+桥 → P5 双壳与打磨。
