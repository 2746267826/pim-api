# pim-api

PIM 的后端服务：数据接入、确定性处理、结论输出。**所有结论由服务端代码计算，可复现**；AI 只在建议与叙事场景出场。

## 本仓范围

| 路径 | 内容 |
| --- | --- |
| `src/Pim.Api` | HTTP 宿主（Minimal API），端点集中在 `src/Pim.Api/Endpoints/` |
| `src/Pim.Core` | 领域模型与核心逻辑 |
| `src/Pim.Infrastructure` | 基础设施：数据库、鉴权、文件、外部集成 |
| `src/modules/` | 业务模块 ×6：Calendar / Files / Mcp / Mobile / PcTracker / QuickNotes |
| `tests/Pim.UnitTests` | 单元测试 |
| `contract/` | **唯一契约出口**：`openapi.json`，同仓 Web 与外部 Android/Windows 据此生成类型 |
| `web/client-web/` | 原 pim-web 前端，内部应用结构保持不变 |
| `sql/`、`deploy/`、`examples/` | 运维脚本、Grafana 看板、MCP 调用示例 |
| `docs/mcp.md` | MCP 协议规范 |
| `docs/product/native-notes-roadmap.md` | 原生客户端与笔记的持续路线图、完整需求编号及分批交付边界 |
| `docker-compose*.yml`、`nginx.conf`、`litellm-config.yaml` | 部署编排 |

## 部署要点

```bash
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d
docker compose --env-file .env.prod -f docker-compose.prod.yml ps
```

> **密钥目录权限与预置要求**：生产编排只按两条具体路径挂载 —— `/data/keys/jwt_private.pem` 按只读方式挂载，应用运行期间无法改写该私钥文件；`/data/keys/data-protection` 按可写方式挂载，DataProtection 需要在其中写入主密钥（框架默认寿命 90 天）。两条宿主机路径都必须在**首次启动前**预置，两种缺失的后果不同：
>
> - **私钥文件缺失**：健康检查会持续失败（`start_period` 过后进入 `unhealthy`），容器日志中出现该文件的完整路径（不出现私钥内容）。注意 Docker 会把缺失的绑定源自动建成同名**目录**，且该路径同时存在于宿主机与命名卷 `pim_data`（Docker 里的实际卷名是 `<compose 项目名>_pim_data`）里（卷内路径 `keys/jwt_private.pem`）。恢复步骤：停止并移除容器 → 删除宿主机上的同名目录 → 删除卷内被挡住的同路径 → 放回 PEM 文件 → `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --force-recreate`。只删宿主机目录、不处理卷内路径、不重建容器，服务起不来。另一种形态：卷内若留着上一次成功挂载产生的 0 字节同名文件，容器会在**创建阶段**就失败（报 `not a directory`），按上面的完整步骤同样能恢复，**不要** `down -v` 清掉整个数据卷。
> - **数据保护密钥目录缺失**：容器仍会在约 20 秒内通过健康检查，并在该目录就地生成一把新的 `key-*.xml`。用旧密钥环保护的密文因此解不开（依赖 `ISecretProtector` 的令牌/绑定链路会失败），而健康检查仍是绿的 —— 故障不会自己暴露出来。请连同已有的 `key-*.xml` 一起预置该目录。

> **镜像与前端**：`pim-api-server` 内嵌前端（`/app/wwwroot`，API 同源伺服 SPA）。
> Dockerfile 直接构建本仓 `web/client-web`，前后端来自同一个提交，不再拉取旧 `pim-web-dist` 镜像或跨仓触发发版。
> 不要手工把 dist 解包到 `src/Pim.Api/wwwroot/`。

## 前端随镜像发布

一次发布包含同一提交中的 API 与 Web。Web 单独修改也会构建、测试并重新发布内嵌镜像；API/MCP 未变化时，保留现有的上一版资产复用逻辑。

| 位置 | 内容 |
| --- | --- |
| 接口 | `GET /api/version` 的 `webVersion` / `webSha` / `webBuiltAt`（读取镜像内版本文件） |
| 镜像内文件 | `/app/wwwroot/web-version.json`，字段 `version` / `gitSha` / `builtAt` |
| Release 资产 | `pim-web-version.json`，原样转录镜像内三字段，另含 `apiVersion` |
| 镜像标签 | `org.pim.api-version` / `org.pim.web-version` / `org.pim.web-sha` |

版本与 SHA 由同一次构建注入。Web 的 SHA 现在指本仓提交，不再指旧 pim-web 仓。
Web 构建、契约类型检查、镜像内容检查、启动健康检查和实际 HTTP SPA 响应检查都必须通过。
删除了 `web_dist_ref`、`rebuild_image_only` 两个跨仓输入；重跑完整构建可使用 `force_all=true`。

**同仓开发**：前端源码位置与内部结构不变，见 `web/README.md`。
`contract/openapi.json` 是唯一契约；API 形状变更后，在同一 PR 导出契约并运行
`bash web/scripts/ci/update-contract.sh` 更新 Web 类型。Android/Windows 仍通过独立仓接收通知。

**迁移与回滚**：原始 Web 提交 `8bef21245a28da85fb16846d6d8c3c5a18730449` 及其历史未改写，
通过无 squash 的 subtree 导入到 `web/`。旧 pim-web 仓、镜像和历史保持不变，暂不归档或删除。
需要撤回时通过 PR revert 迁移的整组合并变更，不强推 master；生产镜像回滚需另行明确操作，代码回滚不会自动恢复数据库或部署。

## 日志级别与降噪

日志最低级别**可配置**，不再硬编码。优先级：

1. 环境变量 `PIM_LOG_LEVEL`（`Trace`/`Debug`/`Information`/`Warning`/`Error`/`Fatal`）
2. 配置 `Logging:LogLevel:Default`（`appsettings*.json`，或环境变量 `Logging__LogLevel__Default`）
3. 默认 `Information`

类别级覆盖同样来自配置 `Logging:LogLevel:<类别>`（例如
`Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command`）。默认已把两个 EF 内务类别
（`Microsoft.EntityFrameworkCore.ChangeTracking`、`Microsoft.EntityFrameworkCore.Database.Command`）
抬到 `Warning`，压掉实体跟踪与逐条 SQL 流水；请求日志、启动/迁移日志以及**所有 Warning 及以上**
照旧输出 —— 降噪只压内务，不吞真错误。写错的级别名不会静默忽略：启动时会记一条 Warning 说明它被跳过。

`--mcp-stdio` 模式与 HTTP 模式共用同一套级别配置，只是控制台输出改到 stderr（stdout 只走 MCP 协议）。

启动日志里会有一条 `日志级别生效：MinimumLevel=… （来源：…）；类别覆盖：…`，用来核对实际生效值。

## 开发

```bash
dotnet test Pim.sln                # 单元测试
dotnet run --project src/Pim.Api   # 本地启动
```

## CI

`.github/workflows/`：`ci.yml`（总门禁）、`build-api.yml`、`build-docker.yml`、`build-mcp.yml`。

## 相关仓库

| 仓库 | 角色 |
| --- | --- |
| [pim-web](https://github.com/2746267826/pim-web) | Web 前端 + 桌面/安卓双壳 + legacy 原版前端 |
| [pim-android](https://github.com/2746267826/pim-android) | 安卓采集端 |
| [pim-windows](https://github.com/2746267826/pim-windows) | Windows 守护 + 浏览器扩展 |
| [pim-docs](https://github.com/2746267826/pim-docs) | 文档与归档（private） |

后端开发流程、模块开发规范、运维只读 API 等文档在 `pim-docs` 仓。

## 原生客户端与笔记

[完整路线图](docs/product/native-notes-roadmap.md)是这项跨端功能的需求依据；
[首版范围与交互](docs/product/native-notes-first-release.md)和
[视觉基础与组件复用规则](docs/product/native-notes-visual-foundation.md)细化当前切片。
安卓先行不取消 Windows 记录、Web、多端编辑、任务提取或既有业务原生化；
主题基础落地不等于录音、手写或云端闭环已经交付。

## 贡献约定

所有改动走分支 + Pull Request；提交信息与 PR 描述双语（英文 + 简体中文）。详见 [`AGENTS.md`](AGENTS.md)。
