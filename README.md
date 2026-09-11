# PIM — 个人信息中枢

PIM（Personal Information Manager）是一个自托管的个人信息中枢：自动记录电脑使用与手机活动，理解你的时间都花在了哪里，并把数据变成看得懂的结论。

[简介](#简介) · [功能特性](#功能特性) · [架构总览](#架构总览) · [设计逻辑](#设计逻辑) · [快速开始](#快速开始) · [部署指南](#部署指南) · [客户端](#客户端) · [MCP 协议接入](#mcp-协议接入) · [可观测性与运维](#可观测性与运维) · [配置参考](#配置参考) · [常见问题](#常见问题) · [TODO / Roadmap](#todo--roadmap) · [开发指南](#开发指南) · [许可证](#许可证)

---

## 简介

PIM 回答一个问题：**我的时间去哪了？**

它围绕一条闭环工作：**输入 → 处理 → 输出 → 我用**。

- **输入**：Windows 客户端后台采集前台应用、窗口、浏览器页面与键鼠活跃度；浏览器扩展（Chrome / Edge）实时同步活动标签页详情；Android 客户端采集定位轨迹、运动状态与应用使用情况。日历、任务、文件、笔记由你在 Web 端维护。
- **处理**：服务端对原始记录做应用归一、活动分类、轨迹聚合、停留识别、统计计算。所有结论由代码确定性计算，可复现。
- **输出**：今日面板一屏总览（如「今天编码 3.5 小时，占 44%」），报表与文件检索随时可用，AI 只在建议与叙事场景出场。
- **我用**：数据回到你手里——回顾一天、调整习惯、规划日程。

技术栈：**.NET 8** 服务端 + **React / TypeScript** Web 前端 + **PostgreSQL** 存储；**Android（Kotlin）**、**Windows（WPF）** 与 **浏览器扩展（WebExtension）** 多端客户端。所有组件可自托管，数据完全归你自己。

## 功能特性

### PC 活动追踪

Windows 客户端在后台采集电脑使用数据，服务端将其整理为可读的活动记录。

| 能力 | 说明 |
|---|---|
| 活动采集 | 前台应用进程、窗口标题、浏览器当前页面、键盘 / 鼠标活跃度 |
| 应用归一 | 内置 210+ 条主流桌面应用与开发工具签名表，优先匹配用户自定义规则，其次自动应用精确/通配符签名匹配；支持应用签名 JSON 导入导出 |
| 键盘 / 鼠标统计 | 按键次数、点击（左右中 / 侧键）、鼠标距离、滚动量、峰值速度、Top 按键 |
| 热力图 | 按小时的活动密度热力分布 |
| 活动分类 2.0 | 支持多级树形分类层级（如「技术学习 → 语言与框架」），严格采用中性化生产力属性（专注 / 休闲 / 中性，不作价值评判） |
| 分类建议与反馈静默 | 提供智能打标建议与批量采纳；内置反馈静默降频机制（用户连续忽略或拒绝 3 次则自动进入静默期，避免干扰） |
| 域名启发式与知识库 | 支持基于域名的分类推断；支持可选的隐私保护应用知识库检索（默认关闭，0 外部请求） |
| 时间线 | 平滑合并后的活动时间线，每段带分类、置信度与判定依据；展示友好的中文应用名称 |
| 专注会话与目标 | 连续活跃的工作会话（起止、主导应用、切换次数）；支持设定每日专注工时目标（默认 5.0 小时）并追踪达成进度 |
| 派生指标 | 活跃输入时长、空闲时长、应用切换频率、最专注应用、键击比 |
| 数据质量报告 | 自动评估采集健康度：事件完整性、数据桶缺失、键鼠采样缺口、守护进程心跳新鲜度 |

### 手机定位与使用

Android 客户端常驻采集定位与手机使用数据，服务端负责轨迹理解。

| 能力 | 说明 |
|---|---|
| 定位采集 | 恒定高精度（HIGH_ACCURACY）定位流；20m 精度质量门，等 GPS 收敛才收点；信号差时收最优 fix 并明确标记低质量，绝不静默 |
| 运动检测 | 自研传感器方案（不依赖 GMS 活动识别）：加速度计波动分三档（静止 / 晃动 / 运动），叠加步数增量与重大运动传感器，双防抖去抖 |
| 采样策略 | 按运动状态动态调整采样间隔：静止低频、运动高频，省电靠间隔而非降精度 |
| 统一引擎 | 手动触发与自动采集共用同一套采集引擎，手动只是「立即执行一次」 |
| 轨迹与停留 | 定位轨迹、停留段识别（速度阈值判定）、轨迹聚合与低精度区域标注 |
| 移动统计 | 停留时长、移动里程等行程指标 |
| 使用统计 | 应用使用时长 / 次数 / 汇总，支持自定义使用目标 |
| 应用分类 | 独立规则引擎：包名精确 / 前缀、关键词规则 + 目录覆盖，可交互维护 |

### 日历 / 任务 / 提醒 / 习惯

- **日历**：本地事件 + Microsoft 日历双向同步（Graph API，冲突检测与回写）、ICS 导入导出、双向重复事件展开（支持 Daily/Weekly/Monthly/Yearly 与 Count/Until 规则）、日程冲突检测。
- **任务**：任务书（清单项、执行段拆分）、看板与任务执行记录。
- **提醒**：提醒规则与投递记录。
- **习惯**：习惯例程与打卡记录、连续完成统计环。
- **排程工作台**：时区一致性空闲时段检索、智能排程引擎、AI 规划草案流式占位生成、排程反馈闭环。
- **数据质量巡检**：全量数据完整性巡检、时区一致性核验、孤儿记录安全清理。

### 文件库

- 文件管理、版本历史、回收站。
- 全文 / 语义混合搜索（Qdrant 向量库 + 本地哈希嵌入，384 维，无需外部嵌入模型）。
- 文档解析（Apache Tika）、Nextcloud 网盘对接、OnlyOffice 在线编辑。
- 可选 AI：文件摘要、标签建议、组织建议（AI 关闭时文件库照常工作）。
- 敏感路径保护：`/Secrets/*`、`/Passwords/*` 等目录内容默认不进入 AI 处理。

### 快速笔记

轻量速记，随手记随手找，支持附件与双向关联。

### 应用知识库

为常用应用建立知识条目（用途、技巧），结合 AI 提供使用建议；知识条目也可由 AI 根据使用情况生成建议。

### AI 能力（可选开关）

AI 层通过 LiteLLM 网关接入任意 OpenAI 兼容模型：

- **建议与叙事场景**：文件摘要 / 问答 / 组织建议、智能排程草案等。
- **不碰核心数据**：统计、分类、判定全部由服务端代码计算；AI 只产出建议性内容，不参与核心事实的生成。
- **可审计**：每次调用的 prompt / response 完整落库。
- **可关闭**：`AI_ENABLED=false` 即可整体关闭，核心功能不受影响；调用带超时与重试上限。

### 今日面板

每日一屏：专注会话、分类时间分布、移动概况、提醒等**处理过的结论**，而非原始记录流水账。Android 客户端内嵌同一套今日视图。

### 系统能力

| 能力 | 说明 |
|---|---|
| 管理员引导与多用户 | 首位注册用户自动成为 Admin 管理员；首次注册平滑迁移历史孤儿数据；强数据隔离与禁用拦截 |
| 认证 | JWT 登录 + 刷新令牌；私钥文件持久化，容器重建登录态不失效 |
| 可观测性 | 暴露 `/metrics`（Prometheus）、预置告警规则、Grafana 仪表盘与 Loki 日志管道 |
| MCP 协议服务 | 内置 Streamable HTTP `/mcp` 端点，支持 101 只读 + 50 写入工具及客户端 Scoped Token |
| 审计时间线 | 关键操作全程留痕 |
| 回收站 | 逻辑删除安全可恢复 |
| 数据完整性 | 数据治理、批量预览、孤儿清理与导出 |
| 健康检查 | `/health`（基础探针）、`/health/live`（存活探针）、`/health/ready`（就绪探针）矩阵 |
| 同步管理 | 多端同步：Android 批量上传（队列 + 确认回执 + 心跳）、Windows 事件上报 |
| 备份 | Kopia 仓库，加密备份 |
| 端点管理 | Windows / Android 客户端只缓存与上传，复杂事实变更统一回 Web 确认 |

## 架构总览

```
┌──────────────────┐        ┌──────────────────┐
│  Windows 客户端   │        │  Android 客户端   │
│  采集 + 事件上报   │        │  定位 + 使用 + 同步 │
└────────┬─────────┘        └────────┬─────────┘
         │                           │
         │   ┌──────────────────┐    │
         ├───┤ 浏览器扩展 (Web)   │    │
         │   │ (pim-watcher-web)│    │
         │   └──────────────────┘    │
         └─────────────┬─────────────┘
                       ▼
        ┌────────────────────────────┐        ┌──────────────────┐
        │        PIM 服务端 (.NET 8)  │ ◄──────┤ Claude / Cursor  │
        │  Pim.Api ─ 模块化后端      │        │ (MCP Client)     │
        │  ├ Pim.Module.PcTracker    │        └──────────────────┘
        │  ├ Pim.Module.Mobile       │
        │  ├ Pim.Module.Calendar     │        ┌──────────────────┐
        │  ├ Pim.Module.Files        │ ◄──────┤ Prometheus/Loki  │
        │  ├ Pim.Module.QuickNotes   │        │ (Observability)  │
        │  ├ Pim.Module.Mcp (/mcp)   │        └──────────────────┘
        │  └ （模块按领域扩展）        │
        │  （Web 前端由服务端托管）    │
        └──────────────┬─────────────┘
                       ▼
     PostgreSQL ─ MinIO ─ Tika ─ Qdrant（可选）─ LiteLLM（可选）─ Nextcloud / OnlyOffice（可选）
```

- **服务端是唯一事实来源**：业务规则、聚合计算、分类判定全部在服务端完成；客户端只是传感器。
- **模块化**：后端按领域拆模块，模块间边界稳定，可并行演进。模块开发规范见 [docs/module-development-guide.md](docs/module-development-guide.md)。
- **客户端形态**：Windows 守护程序（托盘 + 内嵌 Web 外壳）；Android 原生应用（采集 + 状态 + 内嵌今日视图）；浏览器扩展（活动标签页监听）。

## 设计逻辑

1. **服务端为中心，客户端是传感器。** 服务端拥有全部业务状态与规则；Web 是主要交互端；Windows / Android / 扩展客户端只负责采集与上报。任何一端损坏都不影响数据完整性。
2. **模块化并行开发。** 后端按领域拆模块，接口稳定后各模块独立演进。
3. **定位设计三原则。**
   - *手动 = 自动*：同一套采集引擎，手动触发只是「立即执行一次」，不存在两套代码。
   - *全力定位*：所有场景恒定高精度，省电靠采样间隔而不是降精度；20m 质量门，宁缺毋滥。
   - *不依赖 GMS 活动识别*：自研传感器运动检测（加速度计 + 步数 + 重大运动），在 GMS 活动识别不可用的设备上（如部分国行机型）同样可靠。
4. **分类描述事实，不评判人。** 分类回答「这段时间在做什么」（编程、视频、文档……），严格采用中性化词汇（专注 / 休闲 / 中性），把好与坏的判断留给你自己。
5. **交互式收敛，不写死映射。** 分类靠使用中互动维护：打标建议、多级分类、时间线纠错沉淀为规则、智能静默闭环，而不是静态配置文件。
6. **展示结论而非记录。** 面板上的每个数字都是处理过的结论；聚合在服务端完成、固定格式、可复现，不依赖 AI 现算。
7. **AI 有清晰边界。** AI 只做建议与叙事，核心数据的存储、计算、判定全部由代码完成——代码写可靠系统，AI 只做接口。
8. **数据完整性优先。** 审计、回收站、孤儿巡检、备份、密钥持久化、健康探针，都为「数据不能丢」服务。

## 快速开始

前置：Docker + Docker Compose，可访问的 PostgreSQL 16、MinIO 与 Tika 实例（或按开发全家桶一并启动）。

```bash
git clone https://github.com/2746267826/pim-platform.git
cd pim-platform
cp .env.prod.example .env.prod
# 编辑 .env.prod：填入数据库连接串、MinIO 凭据与 Kopia 密码
# 预置密钥：mkdir -p /data/keys/data-protection && openssl genrsa -out /data/keys/jwt_private.pem 2048
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
```

验证服务已启动并进入健康状态：

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml ps
```

然后浏览器打开服务端地址（默认绑定宿主机，端口由 `.env.prod` 中的 `PIM_HTTP_PORT` 决定），首位注册用户将自动获得系统管理员（Admin）权限并接管初始数据。之后安装客户端即可开启自动记录。

## 部署指南

### 生产部署（Docker，推荐）

镜像：`ghcr.io/2746267826/pim-platform-server:latest`（公开镜像，可匿名拉取）。

单容器形态：HTTP（容器内 5000）+ SSH（容器内 22，用于远程管理）。编排文件 `docker-compose.prod.yml` 包含：

- **数据卷** `pim_data`：应用数据与备份仓库（Kopia）。
- **密钥卷**（只读挂载）：`/data/keys` 存放 JWT 私钥与数据保护密钥，容器重建不丢登录态；部署前需预置。
- **健康检查**：`GET /health`、`GET /health/live`、`GET /health/ready`。
- **日志**：JSON 日志轮转（10m × 3），支持配置 `LOKI_URL` 推送到统一日志系统。

外部依赖（生产环境通常接现有实例）：

| 依赖 | 用途 | 必选 |
|---|---|---|
| PostgreSQL 16 | 主存储 | 是 |
| MinIO | 对象存储（文件） | 是（未配置时服务可启动，但文件模块不可用） |
| Apache Tika | 文档内容解析 | 是（未配置时文档解析不可用，文件索引报错） |
| Qdrant | 向量库（文件语义搜索） | 推荐（未配置时语义搜索不可用，其余正常） |
| LiteLLM | AI 网关 | 否（关闭 AI 可不接） |
| Nextcloud | 网盘对接 | 否 |
| OnlyOffice | 在线编辑 | 否 |
| Prometheus / Grafana | 运维监控与告警 | 否（推荐，开箱即用） |
| Grafana Loki | 集中日志收集 | 否（可选） |

部署步骤：

1. 复制模板：`cp .env.prod.example .env.prod`，逐项填入（见[配置参考](#配置参考)）。
2. 预置密钥目录与 JWT 私钥（容器只读挂载，缺失将导致启动失败）：

   ```bash
   sudo mkdir -p /data/keys/data-protection
   sudo openssl genrsa -out /data/keys/jwt_private.pem 2048
   # 确保容器内运行用户对以上路径可读
   ```

   > **已知限制**：当前生产编排将 `/data/keys` 挂载为只读。依赖数据保护密钥写入的功能（如 Outlook 日历同步、文件提供商绑定）在此挂载下无法保存新密钥；如需使用这些功能，请将宿主机目录调整为可写挂载。

3. 生成容器 SSH 公钥（base64 单行，`AAAA...` 替换为你的公钥内容，可多行）：

   ```bash
   printf 'ssh-ed25519 AAAA...\n' | base64 -w0
   ```

4. 启动并检查：

   ```bash
   docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
   docker compose --env-file .env.prod -f docker-compose.prod.yml ps
   ```

### 管理员引导与多用户体系

- **首用户管理员引导（Admin Bootstrap）**：
  新系统部署后，首个成功注册的用户自动获得 `Admin` 管理员权限。无需预置复杂的初始化环境变量或手工运行脚本。
- **历史数据自动平滑迁移**：
  首次管理员注册激活时，后台自动扫描无所有者的遗留历史记录（包括早期活动桶、日历、笔记及文件元数据），自动关联平滑迁移到该管理员账户下，零数据丢失。
- **多租户与数据隔离**：
  系统底层所有查询通过 EF Core 全局过滤强制绑定当前上下文的 `UserId`，保障跨用户数据物理逻辑双重隔离。
- **用户状态生命周期控制**：
  管理员可通过用户管理界面/API 检索用户清单、启用或冻结用户。被冻结的用户现有 JWT 与 Refresh Token 立即吊销失效，阻断任何进一步操作。

### 开发部署（Docker 全家桶）

仓库根目录 `docker-compose.yml` 一键启动全部依赖：PostgreSQL、MinIO、Tika、LiteLLM、Qdrant、Nextcloud、OnlyOffice、Redis + API 容器（本地构建镜像）。

```bash
cp .env.example .env   # 修改其中的占位密码
docker compose up -d
```

前端本地开发（`src/client-web`，Vite + React + TypeScript）：

```bash
npm --prefix src/client-web install
npm --prefix src/client-web run dev
```

开发服务器会将 API 请求代理到本地 API。构建产物输出到 `src/Pim.Api/wwwroot`，由服务端托管。

### 反向代理

生产环境建议前置 nginx（仓库 `nginx.conf` 可作参考），要点：

- **SSL**：证书 `fullchain.pem` / `privkey.pem`。
- **WebSocket / SSE**：`Upgrade` / `Connection` 必须透传（OnlyOffice 在线编辑及 MCP Streamable HTTP 流式传输依赖）。
- **上传体积**：`client_max_body_size 500M`。
- **路径转发**：`/` 与 `/api/` 转发到 API，`/mcp` 保持长连接；地图瓦片另需补充 `/tiles` 反代 OpenStreetMap 瓦片服务。

### 备份与恢复

Kopia 备份仓库位于数据卷内（`Kopia__RepositoryPath`），加密密码来自 `KOPIA_PASSWORD`。备份与恢复操作见 [docs/operations/backup-restore.md](docs/operations/backup-restore.md)。

## 客户端

### Windows 客户端

Windows 守护程序（WPF），后台运行于托盘：

- 采集前台应用、窗口标题、浏览器当前页面与键鼠活跃度，批量上报服务端，离线队列重试。
- 本地常驻轻量 HTTP 服务（`http://localhost:15601`），接收浏览器扩展上报的实时标签页心跳。
- 内嵌 Web 外壳，登录后可直接使用完整 Web 界面。
- 构建：`build-daemon.ps1`（仓库根目录）发布自包含程序与安装包；更多脚本见 `scripts/`。

### 浏览器扩展（PIM Browser Watcher）

Chrome / Edge 浏览器扩展（`pim-watcher-web`），基于 ActivityWatch 深度定制：

- **标签页感知**：实时监听前台活动标签页的 URL、网页标题、音频播放状态（是否静音）及隐身窗口状态。
- **本地守护集成**：通过本地回环端口（`http://localhost:15601/browser/heartbeat`）每秒发送心跳包，由 Windows 客户端合并后统一上报，实现精准网页时间追踪。
- **隐私保护策略**：
  - 仅采集顶层 URL 与标题，绝不读取页面内 DOM 结构、表单密码或私密输入；
  - 隐身模式窗口自动打标脱敏；
  - 支持配置黑名单/白名单域名排除。
- **构建与安装**：
  ```bash
  cd pim-watcher-web
  npm install
  npm run build
  ```
  构建完成后产物位于 `pim-watcher-web/dist`：
  1. 在 Chrome 或 Edge 地址栏打开 `chrome://extensions`；
  2. 打开右上角「开发者模式」；
  3. 点击「加载已解压的扩展程序」，选择 `pim-watcher-web/dist` 目录即可载入。

### Android 客户端

Kotlin 工程（`src/client-android`，Gradle 构建），产出 APK 安装到手机：

- 定位采集（高精度 + 质量门）、自研运动检测、应用使用统计。
- 本地缓存 + WorkManager 批量同步，断网不丢数据。
- 内置状态页与内嵌今日视图。

构建：

```bash
cd src/client-android
./gradlew :app:assembleDebug
```

## MCP 协议接入

PIM 原生集成了 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 服务端（位于 `Pim.Module.Mcp`），使外部 AI 智能体（如 Claude Desktop、Cursor、VS Code）能够直接安全地调用 PIM 的个人中枢能力。

详细设计与工具清单请参阅 [docs/mcp.md](docs/mcp.md)。

### 协议特性

- **双协议支持**：支持进程内 stdio 模式，以及通过 `POST /mcp` 进行的现代 **Streamable HTTP** 协议交互（支持多客户端长连接并发）。
- **工具全景（151 个工具）**：
  - **101 个只读工具**：日历日程、待办清单、习惯例程、提醒、快速笔记、文件检索、PC 活动与移动端概览。
  - **50 个写入工具**：支持日程增删改查、任务分解、习惯打卡、笔记创建、文件管理、分类标记等完整控制流。
- **客户端 Scoped Token 与权限矩阵**：
  - 在 Web 界面「MCP 管理」中可随时创建专属客户端 Token；
  - 细粒度工具级授权开关：可按客户端针对性开放或禁用特定的只读/写入工具集合；
  - 随时吊销令牌，一键阻断未授权接入。

### 客户端接入配置示例

#### 1. Claude Desktop 配置 (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "pim": {
      "command": "pim-mcp",
      "args": ["--url", "http://localhost:5000", "--token", "mcp_tok_xxxxxxxxxxxx"]
    }
  }
}
```

#### 2. Streamable HTTP 接入（Cursor / 远程 Agent）

```json
{
  "name": "PIM-Remote",
  "transport": "http",
  "url": "http://your-pim-host:5000/mcp",
  "headers": {
    "X-MCP-Token": "mcp_tok_xxxxxxxxxxxx"
  }
}
```

## 可观测性与运维

PIM 提供企业级的生产可观测性基础设施，详细运维指南见 [docs/operations/observability.md](docs/operations/observability.md)。

### Prometheus 指标暴露

服务通过 `GET /metrics` 端点提供标准 Prometheus 格式指标，涵盖：
- **HTTP 服务指标**：请求总数、QPS、状态码分布、请求延迟耗时直方图。
- **AI 网关监控**：调用次数、响应延迟、令牌消耗量、异常与重试比率。
- **采集健康度**：`pim_daemon_heartbeat_freshness_seconds`（守护进程心跳新鲜度，用于秒级发现客户端断联）。
- **后台作业队列**：`pim_hangfire_jobs`（Hangfire 正在处理、排队中、成功与失败任务数）。
- **运行时指标**：.NET GC、线程池、内存与进程资源。

> **鉴权机制**：`/metrics` 端点受保护，可通过请求头 `X-PIM-Ops-Key: <PIM_OPS_KEY>`、Bearer 运维令牌或 Admin 身份 JWT 进行安全抓取。

### 健康检查探针矩阵

| 端点 | 探针类型 | 说明 |
|---|---|---|
| `GET /health` | 基础健康 | 检查进程存活状态与基础配置 |
| `GET /health/live` | Liveness | Kubernetes / Docker 存活探针，轻量即时返回 |
| `GET /health/ready` | Readiness | 就绪探针，核验 PostgreSQL 连接、MinIO 及核心依赖就绪态 |

### 告警规则与仪表盘

仓库预置了完整的监控配置资产：
- **Prometheus 告警规则**：`deploy/prometheus/alerts.yml`，预定义 `PimApiDown`、`PimDatabaseDown`、`PimDaemonHeartbeatStale`、`PimHangfireBacklog`、`PimAiErrorRateHigh`、`PimHttp5xxSpike` 等核心告警规则。
- **Grafana 监控看板**：`deploy/grafana/dashboards/pim-overview.json`，一键导入即刻呈现 API 性能、后台任务健康与客户端活跃全景。
- **Loki 日志管道**：配置 `LOKI_URL`（如 `http://loki:3100`）环境变量后，Serilog 自动启用 Loki 接收器进行结构化集中日志推送。

## 配置参考

### 生产环境变量（.env.prod）

| 变量 | 说明 | 必选 |
|---|---|---|
| `PIM_IMAGE_TAG` | 镜像标签（默认 `latest`） | 否 |
| `PIM_HTTP_PORT` | 宿主机 HTTP 端口（默认仅绑定回环地址） | 否 |
| `PIM_SSH_PORT` | 宿主机 SSH 端口（默认仅绑定回环地址） | 否 |
| `PG_CONNECTION` | PostgreSQL 连接串（映射 `ConnectionStrings__DefaultConnection`） | 是 |
| `MINIO_ENDPOINT` / `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` | MinIO 对象存储 | 是 |
| `KOPIA_PASSWORD` | Kopia 备份仓库加密密码 | 是 |
| `PIM_SSH_AUTHORIZED_KEYS` | 容器 SSH 公钥（base64 单行） | 是 |
| `TIKA_BASE_URL` | Tika 服务地址（未配置时文档解析不可用） | 是 |
| `PIM_OPS_KEY` | 运维监控与 `/metrics` 抓取鉴权密钥（支持逗号分隔多个） | 否 |
| `LOKI_URL` | Grafana Loki 日志收集端点（如 `http://loki:3100`） | 否 |
| `AppLookup__Enabled` | PC Tracker 在线元数据检索开关（严格默认 `false`，保护隐私） | 否 |
| `DailyProductiveHoursGoal` | 每日基准专注工时目标（默认 `5.0` 小时） | 否 |
| `DisableHangfire` | 是否禁用 Hangfire 后台调度服务（默认 `false`） | 否 |
| `AI_ENABLED` | AI 开关（默认 `false`） | 否 |
| `AI_BASE_URL` / `AI_API_KEY` | LiteLLM 网关地址与虚拟密钥 | 启用 AI 时 |
| `AI_DEFAULT_MODEL` | 默认模型名（网关侧 `pim-default`） | 否 |
| `NEXTCLOUD_PUBLIC_BASE_URL` / `NEXTCLOUD_INTERNAL_BASE_URL` | Nextcloud 对接 | 否 |
| `ONLYOFFICE_PUBLIC_URL` / `ONLYOFFICE_JWT_SECRET` | OnlyOffice 在线编辑 | 否 |
| `QDRANT_BASE_URL` | Qdrant 向量库 | 否 |
| `PIM_LOG_RETAINED_FILES` | 日志保留份数（默认 2） | 否 |
| `TZ` | 时区（默认 Asia/Shanghai，compose 预设） | 否 |

### 容器内预设（compose 已配好，一般无需改动）

| 变量 | 值 | 说明 |
|---|---|---|
| `Jwt__PrivateKeyPath` | `/data/keys/jwt_private.pem` | JWT 私钥 |
| `DataProtection__KeysPath` | `/data/keys/data-protection` | 数据保护密钥 |
| `Kopia__RepositoryPath` | `/data/kopia-repo` | 备份仓库 |
| `Qdrant__Collection` | `pim_file_chunks` | 向量集合 |
| `Files__AiDisabledPathPatterns__0/1` | `/Secrets/*`、`/Passwords/*` | 敏感路径不进 AI |
| `Ai__TimeoutSeconds` / `Ai__MaxAttemptsPerRequest` | 30 / 2 | AI 超时与重试上限 |
| `Ai__SaveFullPrompts` / `Ai__SaveFullResponses` | true | AI 调用审计留痕 |
| `Embedding__Provider` / `Embedding__Dimensions` | hashing / 384 | 本地哈希嵌入 |

### 开发环境变量（.env）

开发全家桶的密码类变量见 `.env.example`，复制后把 `change_me_*` 占位符替换为强密码。LiteLLM 建议为 PIM 创建独立虚拟密钥，主密钥仅本地临时调试使用。

## 常见问题

**数据存在哪里？**
服务端 PostgreSQL（结构化数据）与 MinIO（文件），备份进 Kopia 仓库。核心数据无云端依赖（可选的外部服务对接除外）。

**AI 必须开吗？**
不必开。`AI_ENABLED=false`（默认）时文件库、分类、统计全部正常工作，只有 AI 摘要 / 建议类功能不可用。

**分类怎么维护？**
无需配置：新应用用多了会进入「待打标」队列，点选即可；时间线上发现错误直接纠错，自动沉淀为规则；连续忽略或拒绝的建议自动进入静默降频。内置分类树可在分类页调整。

**客户端离线会丢数据吗？**
不会。Windows 客户端离线队列重试；Android 客户端本地缓存，网络恢复后批量同步。数据质量报告会如实反映采集缺口。

**忘记密码怎么办？**
当前无自助找回，请联系服务端管理员处理（回收站与审计不涉及认证数据）。

**支持多端同时使用吗？**
支持。Windows / 浏览器扩展 / Android 客户端按设备上报，Web 统一查看；数据按账号隔离。

## TODO / Roadmap

- [x] 多用户与管理员引导机制 — 首位注册用户自动赋予 Admin 权限，历史孤儿数据平滑接管，多租户隔离与账户状态实时控制。
- [x] 系统可观测性与运维闭环 — 遵循 [docs/operations/observability.md](docs/operations/observability.md)，提供 Prometheus `/metrics` 指标暴露（`PIM_OPS_KEY` 鉴权）、`/health` 探针矩阵、预置告警规则（`alerts.yml`）、Grafana 看板与 Loki 日志管道。
- [x] 日历日程冲突检测与 AI 规划辅助 — 支持复杂双向循环日程（Daily/Weekly/Monthly/Yearly 与 Count/Until）、时区安全空闲槽位发掘与 AI 规划草案流式生成。
- [x] 数据质量检查与孤儿清理 — 时区一致性检查、文件/日历/活动数据完整性巡检与安全清理。
- [x] PcTracker 分类 2.0 — 内置 210+ 应用签名知识库、树形多级分类体系、严格中性化用词（专注 / 休闲 / 中性）、反馈静默降频闭环、每日专注工时目标与签名批量导入导出。
- [x] MCP 协议写入与 Streamable HTTP 支持 — Phase 3 已交付：151 个工具（101 读 + 50 写）+ Streamable HTTP 多客户端并发 + 客户端专属 Token + 工具级细粒度权限控制 + WebUI MCP 管理页。详见 [docs/mcp.md](docs/mcp.md)。

## 开发指南

- 后端：`dotnet test Pim.sln`；前端：`npm --prefix src/client-web run build`；Android：`./gradlew :app:testDebugUnitTest`。
- 模块化开发规范、API 契约与 Web 模块结构：见 [docs/module-development-guide.md](docs/module-development-guide.md)。
- 验收文档：见 [docs/operations](docs/operations)。
- 贡献：所有改动走分支 + Pull Request，提交信息与 PR 描述双语（英文 + 简体中文）。

## 许可证

本项目暂未指定开源许可证。如有使用或再分发需求，请联系仓库所有者。
