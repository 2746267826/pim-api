# pim-web

个人信息管理（PIM）系统的 Web 前端：一套 **React 19 SPA**，同时服务桌面浏览器、Windows 桌面壳（Tauri 2）与 Android 壳（Capacitor 8）。由 [`frontend-rebuild-spec/`](frontend-rebuild-spec/README.md) 规格文档独立重建而成——规格只描述模块、接口与数据行为，不含视觉实现。

| | |
|---|---|
| 后端 | PIM API（`/api/v1`，独立仓库），本仓库只含前端 |
| 业务日口径 | Asia/Shanghai **04:00 起算**（贯穿 PC/手机/聚合全域） |
| 部署形态 | SPA 构建产物由 PIM API **同源伺服**；桌面/移动壳按「远程加载」架构打开服务器地址 |
| 状态 | P0–P5 六阶段全部交付；146 单测 + 四套可重复验收脚本 |

## 功能总览

- **工作**：今日（分区驾驶舱 + 24h 今日脉搏）、日历（时间轴/周/月/列表四视图 + 收件箱拖入排期 + 凌晨空档三种策略）、工作台、任务（快捷筛选/优先级分布/两步删除）、确认中心（三档破坏性确认）
- **洞察**：电脑记录（时间线/热力/生产力/键鼠矩阵/标注队列）、浏览器使用、手机记录（使用分析/设备存活）、历史位置（Leaflet 轨迹 + cursor 分页原始点）
- **收集**：快速记录（TipTap）、文件（OneDrive 三栏 + >4MB Graph 会话分片直传）
- **治理**：数据中心、提醒、报告、习惯（创建/编辑/归档/删除）
- **系统**：状态页、设置（服务器/Microsoft 绑定/AI/用户管理/MCP/数据可信度 13 尺子/回收站/PC 明细查询）、应用知识库、审计时间线、设备端点
- **嵌入**：`/embed/android/today|tracks` 内嵌页（三条业务 GET 由采集端 App 原生代发；令牌不进入页面）

## 技术栈

React 19 · TypeScript · Vite 8 · Tailwind 4 · TanStack Query/Table/Virtual · FullCalendar 6 · ECharts 6 · Leaflet 1.9 · TipTap 2 · motion · Radix UI（自封装设计系统原语）

## 仓库结构

```
client-web/              前端主体（本文件大部分内容的所在地，见其 README）
  src/app/               路由/守卫/外壳/全局编辑器/导航
  src/api/               域 client + 类型（apiFetch：401 单飞刷新、429 退避、409/412 白名单）
  src/lib/               apiBase / polling / enums / businessDay / storage / notify
  src/components/        ui 原语 + viz 图表（GitHub 热力图/键鼠矩阵/甘特条）+ motion 原语
  src/features/<域>/     pages / components / queries（按域分包）
  src/embed/android/     内嵌页 + 非敏感状态桥适配（原生代发业务 GET）
  docs/                  DESIGN（视觉规范）/ CONVENTIONS（工程约定）/ qa（截图与清单）
  scripts/               自验与 QA 脚本（见下）
  shell-dist/            Windows 壳引导页（服务器地址记忆 + 跳转）
  android/               Capacitor 8 工程（PimBridge 参考插件）
  src-tauri/             Tauri 2 工程（NSIS 安装包 ~1.8MiB，远程加载壳）
docs/backend-issues.md   提给后端的问题清单与修复记录
frontend-rebuild-spec/   重建规格（模块×接口×数据行为 + 字段级 API 参考）
```

## 快速开始（开发）

要求：Node ≥ 20（开发验证于 24）；后端可用（本地或测试服务器）。

```bash
git clone https://github.com/2746267826/pim-web.git
cd pim-web/client-web
npm install
cp .env.example .env      # 填 PIM_API_TARGET（后端地址）；QA 脚本另需 PIM_TEST_USER/PIM_TEST_PASS
npm run dev               # http://127.0.0.1:3000（/api 代理到 PIM_API_TARGET）
```

- 端口固定 **3000**（5173 落在 Windows 保留端口区间会 EACCES），`PIM_DEV_PORT` 可覆盖；显式绑定 127.0.0.1。
- 服务器地址也可在应用内「设置 → 服务器」填写（存 `pim.apiBase`；留空 = 同源）。**跨域直连需后端 CORS 白名单放行该来源**（精确匹配，含端口）。
- 业务日 04:00 起算为服务端口径，前端不做本地时区换算。

## 构建与分发

```bash
npm run build            # tsc + vite build → client-web/dist/（由 PIM API 同源伺服）
```

### 产物镜像与自动触发（`pim-web-dist`）

master 上 lint + 单测 + 契约新鲜度门禁**全部通过之后**，CI 会把 `client-web/dist` 全量打成
一个**只含静态文件**的镜像推到 GHCR（包必须 public）：

```
ghcr.io/2746267826/pim-web-dist:sha-<7 位短 SHA>   # 不可变，与本次 master 提交一一对应
ghcr.io/2746267826/pim-web-dist:latest             # 滚动
```

- 产物里带一份 **`web-version.json`**（字段：`version` / `gitSha` / `builtAt`(UTC)），版本号与 sha
  与本次构建注入构建过程的 `VITE_APP_VERSION` / `VITE_GIT_SHA` **同源**，不另算一份。
- 推包成功后，CI 用 GitHub App（`pim-release-trigger`）现签的临时 token 触发 `pim-api` 的
  `workflow_dispatch`，带上本次前端的确切引用（`sha-<7>`）与「只重建镜像」意图 ——
  于是"前端合并"会自动产出一版内嵌该前端的新 API 镜像；触发失败会让本仓 CI 变红（打印 HTTP 状态与响应体）。
- PR 上**不推包**；master 上"该跑却没跑"（例如推包被跳过）会被 `Release Gate` 判红，没有静默旁路。
- 本仓仍然**不单独发 release**：产物通过产物镜像进入 `pim-api` 的镜像与 release 台账。

```bash
# 本地复核产物镜像（包是 public，未登录也能拉）
docker pull ghcr.io/2746267826/pim-web-dist:latest
CID=$(docker create ghcr.io/2746267826/pim-web-dist:latest /reader)   # scratch 镜像没有 shell
docker cp "$CID:/web-version.json" -   # 版本号 / git sha / 构建时间(UTC)
docker cp "$CID:/index.html" -
docker rm "$CID"
```

### Android（Capacitor 8）

```bash
npm run build && npx cap sync android
cd android && JAVA_HOME="<JDK21 路径>" ./gradlew assembleDebug
# 产物：android/app/build/outputs/apk/debug/app-debug.apk
```

- 需要 JDK 21（Android Studio 自带 JBR 即可）与 Android SDK；详见 [client-web/README.md](client-web/README.md) 的本机验证记录。
- `PimBridgePlugin`（Java）仍是待 PW-07b/c 整改的参考壳，保留旧 `token.request/refresh` 流程，不能作为产品安全实现的验收证据。真实采集端 App 按规格 03 §6 原生代发三条业务 GET，桥只交换 `native.state.request` / `page.report` 等非敏感状态。
- 令牌来源默认值经构建期注入（`build.gradle` resValue，取自 `.env`），不写入源码。

### Windows（Tauri 2）

```bash
npm i -D @tauri-apps/cli   # 已在 devDependencies
npx tauri build            # 需 Rust（stable-msvc）+ MSVC Build Tools
# 产物：src-tauri/target/release/bundle/nsis/PIM_<版本>_x64-setup.exe（约 1.8MiB）
```

- 严格远程加载架构：安装包只含一个引导页（记住服务器地址 → 倒计时自动进入），React 应用不进安装包，更新只发服务端。
- 开发调试：`npx tauri dev`（加载 Vite dev server）。

## 测试与验收

```bash
npm run test             # vitest 全量单测：client-web/src + audit-tests
                         # （网络层/业务日/域接口 + 契约一致性、CI 门禁自检、宿主会话等审计用例）
```

`client-web/scripts/` 下的可重复验收脚本（地址与账号读 `.env`）：

| 脚本 | 用途 |
| --- | --- |
| `verify-backend-20260930.mjs` | PC 域后端契约回归（12 项） |
| `verify-pitfalls.mjs` | API 约定坑位自检（双态端点/totalCount/尾斜杠/302/令牌轮换等） |
| `qa-screenshots.mjs` | 全页截图验收：35 路由 × 桌面/手机 → `docs/qa/`（含对照清单页） |
| `qa-three-states.mjs` | 三态复查：loading/error/empty 确定性制造（API 拦截） |

## 工程要点（细节见 [client-web/docs/CONVENTIONS.md](client-web/docs/CONVENTIONS.md)）

- **网络层**：统一 `apiFetch`——ApiResponse 解包、401 单飞刷新、429 按 Retry-After 退避重试一次、409/412 白名单、HTML 响应识别（SPA fallback 误配检测）。
- **路由**：全量 `React.lazy` 分包 + 统一 `LazyOutlet` 骨架兜底；侧边栏 `prefetch="intent"` 消除冷点击冻结。
- **动效**：时长/缓动 tokens 唯一来源；只动 `transform/opacity`；`prefers-reduced-motion` 三重退路（全局 CSS + MotionConfig + 显式短路）。
- **图表**：ECharts 按需注册 + 统一动画/tooltip 主题；自研 GitHub 风格热力图、键鼠矩阵等组件复用于多页。

## 相关文档

- [重建规格](frontend-rebuild-spec/README.md)（模块 × 接口 × 数据行为 + 字段级 API 参考）
- [client-web/README.md](client-web/README.md)（开发细节：本机构建验证记录、脚本说明、目录）
- [docs/DESIGN.md](client-web/docs/DESIGN.md) / [docs/CONVENTIONS.md](client-web/docs/CONVENTIONS.md)
- [docs/backend-issues.md](docs/backend-issues.md)（后端问题清单与修复回归记录）

## 本仓结构

| 路径 | 内容 |
| --- | --- |
| `client-web/` | 当前前端（features 分域），含双壳 `src-tauri/`（Windows）与 `android/`（Capacitor） |
| `frontend-rebuild-spec/` | 重建规格（模块 × 接口 × 数据行为）——前端约定的**唯一来源** |
| `legacy/` | 原版前端与其测试的快照（保持原仓库相对路径）——**逐页替换，替换一页删一页** |
| `docs/` | 前端设计与工程约定、后端问题清单 |

## 接口契约

接口类型由 `pim-api` 的 `contract/openapi.json` 生成。契约变更后重新生成，字段不符会在 `tsc` 阶段直接报错，无需等联调。

## 相关仓库

| 仓库 | 角色 |
| --- | --- |
| [pim-api](https://github.com/2746267826/pim-api) | 后端 + MCP + 部署 + 契约出口 |
| [pim-android](https://github.com/2746267826/pim-android) | 安卓采集端 |
| [pim-windows](https://github.com/2746267826/pim-windows) | Windows 守护 + 浏览器扩展 |
| [pim-docs](https://github.com/2746267826/pim-docs) | 文档与归档（private） |

## 贡献约定

所有改动走分支 + Pull Request；提交信息与 PR 描述双语（英文 + 简体中文）。详见 [`AGENTS.md`](AGENTS.md)。
