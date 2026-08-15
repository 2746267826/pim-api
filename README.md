# PIM — 个人信息中枢

PIM（Personal Information Manager）是一个自托管的个人信息中枢：自动记录电脑使用与手机活动，理解你的时间都花在了哪里，并把数据变成看得懂的结论。

[简介](#简介) · [功能特性](#功能特性) · [架构总览](#架构总览) · [设计逻辑](#设计逻辑) · [快速开始](#快速开始) · [部署指南](#部署指南) · [客户端](#客户端) · [配置参考](#配置参考) · [常见问题](#常见问题) · [开发指南](#开发指南) · [许可证](#许可证)

---

## 简介

PIM 回答一个问题：**我的时间去哪了？**

它围绕一条闭环工作：**输入 → 处理 → 输出 → 我用**。

- **输入**：Windows 客户端后台采集前台应用、窗口、浏览器页面与键鼠活跃度；Android 客户端采集定位轨迹、运动状态与应用使用情况。日历、任务、文件、笔记由你在 Web 端维护。
- **处理**：服务端对原始记录做应用归一、活动分类、轨迹聚合、停留识别、统计计算。所有结论由代码确定性计算，可复现。
- **输出**：今日面板一屏总览（如「今天编码 3.5 小时，占 44%」），报表与文件检索随时可用，AI 只在建议与叙事场景出场。
- **我用**：数据回到你手里——回顾一天、调整习惯、规划日程。

技术栈：**.NET 8** 服务端 + **React / TypeScript** Web 前端 + **PostgreSQL** 存储；**Android（Kotlin）**与 **Windows** 双客户端。所有组件可自托管，数据完全归你自己。

## 功能特性

### PC 活动追踪

Windows 客户端在后台采集电脑使用数据，服务端将其整理为可读的活动记录。

| 能力 | 说明 |
|---|---|
| 活动采集 | 前台应用进程、窗口标题、浏览器当前页面、键盘 / 鼠标活跃度 |
| 应用归一 | 内置 170+ 条应用签名表，把「一个软件多个进程」归并为同一个应用；签名可随时补充 |
| 键盘 / 鼠标统计 | 按键次数、点击（左右中 / 侧键）、鼠标距离、滚动量、峰值速度、Top 按键 |
| 热力图 | 按小时的活动密度热力分布 |
| 活动分类 | 三层递进：进程归一 → 应用映射分类 → 情境规则覆盖（窗口标题 / URL 关键词） |
| 分类维护 | 新应用积累到阈值后进入「待打标」队列，点选即分类；自定义分类自动纳入选项；时间线上可直接纠错并沉淀为规则 |
| 时间线 | 平滑合并后的活动时间线，每段带分类、置信度与判定依据 |
| 专注会话 | 连续活跃的工作会话（起止、主导应用、切换次数） |
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

- **日历**：本地事件 + Microsoft 日历双向同步（Graph API，冲突检测与回写）、ICS 导入导出、重复事件。
- **任务**：任务书（清单项、执行段拆分）、任务执行记录。
- **提醒**：提醒规则与投递记录。
- **习惯**：习惯例程与打卡记录。
- **排程工作台**：可用时段、排程引擎、AI 规划占位、排程反馈。
- **确认事项**：需人工确认的事项管理，配套事实确认策略。

### 文件库

- 文件管理、版本历史、回收站。
- 全文 / 语义混合搜索（Qdrant 向量库 + 本地哈希嵌入，384 维，无需外部嵌入模型）。
- 文档解析（Apache Tika）、Nextcloud 网盘对接、OnlyOffice 在线编辑。
- 可选 AI：文件摘要、标签建议、组织建议（AI 关闭时文件库照常工作）。
- 敏感路径保护：`/Secrets/*`、`/Passwords/*` 等目录内容默认不进入 AI 处理。

### 快速笔记

轻量速记，随手记随手找，支持附件。

### 应用知识库

为常用应用建立知识条目（用途、技巧），结合 AI 提供使用建议；知识条目也可由 AI 根据使用情况生成建议。

### AI 能力（可选开关）

AI 层通过 LiteLLM 网关接入任意 OpenAI 兼容模型：

- **建议与叙事场景**：文件摘要 / 问答 / 组织建议等。
- **不碰核心数据**：所有统计由服务端代码计算，AI 输出不参与存储、计算与判定。
- **可审计**：每次调用的 prompt / response 完整落库。
- **可关闭**：`AI_ENABLED=false` 即可整体关闭，核心功能不受影响；调用带超时与重试上限。

### 今日面板

每日一屏：专注会话、分类时间分布、移动概况、提醒等**处理过的结论**，而非原始记录流水账。Android 客户端内嵌同一套今日视图。

### 系统能力

| 能力 | 说明 |
|---|---|
| 认证 | JWT 登录 + 刷新令牌；私钥文件持久化，容器重建登录态不失效 |
| 审计时间线 | 关键操作全程留痕 |
| 回收站 | 删除可恢复 |
| 数据中台 | 数据治理、批量预览与导出 |
| 健康检查 | `/health` 端点 + Web 状态页 |
| 同步管理 | 多端同步：Android 批量上传（队列 + 确认回执 + 心跳）、Windows 事件上报 |
| 备份 | Kopia 仓库，加密备份 |
| 端点管理 | Windows / Android 客户端只缓存与上传，复杂事实变更统一回 Web 确认 |

## 架构总览

```
┌──────────────────┐        ┌──────────────────┐
│  Windows 客户端    │        │  Android 客户端    │
│  采集 + 事件上报    │        │  定位 + 使用 + 同步 │
└────────┬─────────┘        └────────┬─────────┘
         └───────────┬───────────────┘
                     ▼
      ┌──────────────────────────────┐
      │        PIM 服务端 (.NET 8)     │
      │  Pim.Api ─ 模块化后端          │
      │  ├ Pim.Module.PcTracker       │
      │  ├ Pim.Module.Mobile          │
      │  ├ Pim.Module.Calendar        │
      │  ├ Pim.Module.Files           │
      │  ├ Pim.Module.QuickNotes      │
      │  └ Pim.Module.Stats           │
      │  （Web 前端由服务端托管）        │
      └──────────────┬───────────────┘
                     ▼
   PostgreSQL ─ MinIO ─ Qdrant ─ Tika ─ LiteLLM（可选）─ Nextcloud / OnlyOffice（可选）
```

- **服务端是唯一事实来源**：业务规则、聚合计算、分类判定全部在服务端完成；客户端只是传感器。
- **模块化**：后端按领域拆模块，模块间边界稳定，可并行演进。模块开发规范见 [docs/module-development-guide.md](docs/module-development-guide.md)。
- **客户端形态**：Windows 守护程序（托盘 + 内嵌 Web 外壳）；Android 原生应用（采集 + 状态 + 内嵌今日视图）。

## 设计逻辑

1. **服务端为中心，客户端是传感器。** 服务端拥有全部业务状态与规则；Web 是主要交互端；Windows / Android 客户端只负责采集与上报。任何一端损坏都不影响数据完整性。
2. **模块化并行开发。** 后端按领域拆模块，接口稳定后各模块独立演进。
3. **定位设计三原则。**
   - *手动 = 自动*：同一套采集引擎，手动触发只是「立即执行一次」，不存在两套代码。
   - *全力定位*：所有场景恒定高精度，省电靠采样间隔而不是降精度；20m 质量门，宁缺毋滥。
   - *不依赖 GMS*：自研传感器运动检测（加速度计 + 步数 + 重大运动），在无 GMS 设备上同样可靠。
4. **分类描述事实，不评判人。** 分类回答「这段时间在做什么」（编程、视频、文档……），把好与坏的判断留给你自己。
5. **交互式收敛，不写死映射。** 分类靠使用中互动维护：打标队列、自定义分类、时间线纠错沉淀为规则，而不是静态配置表。
6. **展示结论而非记录。** 面板上的每个数字都是处理过的结论；聚合在服务端完成、固定格式、可复现，不依赖 AI 现算。
7. **AI 有清晰边界。** AI 只做建议与叙事，存储、计算、判定全部由代码完成——代码写可靠系统，AI 只做接口。
8. **数据完整性优先。** 审计、回收站、备份、密钥持久化、健康检查，都为「数据不能丢」服务。

## 快速开始

前置：Docker + Docker Compose，一台可访问的 PostgreSQL 16 与 MinIO 实例（或用开发全家桶一并启动）。

```bash
git clone https://github.com/2746267826/pim-platform.git
cd pim-platform
cp .env.prod.example .env.prod
# 编辑 .env.prod：填入数据库连接串、MinIO 凭据与 Kopia 密码
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
```

验证健康状态：

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml exec pim curl -s http://localhost:5000/health
```

然后浏览器打开服务端地址（默认仅绑定本机回环，端口由 `.env.prod` 中的 `PIM_HTTP_PORT` 决定），注册账号并登录。之后安装 [Windows 客户端](#windows-客户端)与 [Android 客户端](#android-客户端)，数据就开始流动了。

> 更完整的生产部署说明见下文；只想要一个本地环境一把梭，用「Docker 全家桶」开发部署即可。

## 部署指南

### 生产部署（Docker，推荐）

镜像：`ghcr.io/2746267826/pim-platform-server:latest`（公开镜像，可匿名拉取）。

单容器形态：HTTP（容器内 5000）+ SSH（容器内 22，用于受限远程管理）。编排文件 `docker-compose.prod.yml` 包含：

- **数据卷** `pim_data`：应用数据与备份仓库（Kopia）。
- **密钥卷**（只读挂载）：`/data/keys` 存放 JWT 私钥与数据保护密钥，容器重建不丢登录态。
- **健康检查**：`GET /health`。
- **日志**：JSON 日志轮转（10m × 3），保留策略可配。

外部依赖（生产环境通常接现有实例）：

| 依赖 | 用途 | 必选 |
|---|---|---|
| PostgreSQL 16 | 主存储 | 是 |
| MinIO | 对象存储（文件） | 是 |
| Qdrant | 向量库（文件语义搜索） | 推荐 |
| Apache Tika | 文档内容解析 | 推荐 |
| LiteLLM | AI 网关 | 否（关闭 AI 可不接） |
| Nextcloud | 网盘对接 | 否 |
| OnlyOffice | 在线编辑 | 否 |

部署步骤：

1. 复制模板：`cp .env.prod.example .env.prod`，逐项填入（见[配置参考](#配置参考)）。
2. 生成容器 SSH 公钥（base64 单行）：

   ```bash
   printf 'ssh-ed25519 AAAA...\n' | base64 -w0
   ```

3. 启动并检查：

   ```bash
   docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
   docker compose --env-file .env.prod -f docker-compose.prod.yml exec pim curl -s http://localhost:5000/health
   ```

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
- **WebSocket**：`Upgrade` / `Connection` 头必须透传（OnlyOffice 在线编辑依赖）。
- **上传体积**：`client_max_body_size 500M`。
- **路径转发**：`/` 与 `/api/` 转发到 API；地图瓦片经 `/tiles` 中转。

### 备份与恢复

Kopia 备份仓库位于数据卷内（`Kopia__RepositoryPath`），加密密码来自 `KOPIA_PASSWORD`。备份与恢复操作见 [docs/operations/backup-restore.md](docs/operations/backup-restore.md)。

## 客户端

### Windows 客户端

Windows 守护程序（WPF），后台运行于托盘：

- 采集前台应用、窗口标题、浏览器页面与键鼠活跃度，批量上报服务端，离线队列重试。
- 内嵌 Web 外壳，登录后可直接使用完整 Web 界面。
- 构建：`build-daemon.ps1`（仓库根目录）发布自包含程序与安装包；更多脚本见 `scripts/`。

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
| `TIKA_BASE_URL` | Tika 服务地址 | 否 |
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
服务端 PostgreSQL（结构化数据）与 MinIO（文件），备份进 Kopia 仓库。全部自托管，无云端依赖。

**AI 必须开吗？**
不必须。`AI_ENABLED=false`（默认）时文件库、分类、统计全部照常工作，只有 AI 摘要 / 建议类功能不可用。

**分类怎么维护？**
无需配置：新应用用多了会进入「待打标」队列，点选即可；时间线上发现错误直接纠错，自动沉淀为规则。内置分类树可在分类页调整。

**客户端离线会丢数据吗？**
不会。Windows 客户端离线队列重试；Android 客户端本地缓存，网络恢复后批量同步。数据质量报告会如实反映采集缺口。

**忘记密码怎么办？**
当前无自助找回，请联系服务端管理员处理（回收站与审计不涉及认证数据）。

**支持多端同时使用吗？**
支持。Windows / Android 客户端按设备上报，Web 统一查看；数据按账号隔离。

## 开发指南

- 后端：`dotnet test Pim.sln`；前端：`npm --prefix src/client-web run build`；Android：`./gradlew :app:testDebugUnitTest`。
- 模块化开发规范、API 契约与 Web 模块结构：见 [docs/module-development-guide.md](docs/module-development-guide.md)。
- 验收文档：见 [docs/operations](docs/operations)。
- 贡献：所有改动走分支 + Pull Request，提交信息与 PR 描述双语（英文 + 简体中文）。

## 许可证

本项目暂未指定开源许可证。如有使用或再分发需求，请联系仓库所有者。
