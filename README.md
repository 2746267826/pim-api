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
| `contract/` | **对外契约出口**：`openapi.json`，客户端仓据此生成类型 |
| `sql/`、`deploy/`、`examples/` | 运维脚本、Grafana 看板、MCP 调用示例 |
| `docs/mcp.md` | MCP 协议规范（本仓唯一保留的文档） |
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

> **镜像与前端**：`pim-api-server` 镜像**内嵌前端**（`/app/wwwroot`，API 同源伺服 SPA）。
> 前端产物来自 `pim-web` 推的产物镜像 `ghcr.io/2746267826/pim-web-dist`（tag：`sha-<7 位短 SHA>` + `latest`），
> 构建期多阶段 `COPY --from` 取用；**默认取最新并在构建开始时冻结成 `sha-<7>`**，
> 也可以在 dispatch 时用 `web_dist_ref` 指定确切版本（回滚/复现）。
> 旧的手工路径（把 dist 解包到 `src/Pim.Api/wwwroot/` 再 build）**已废弃**，不要再使用。
> 详见 [前端随镜像发布](#前端随镜像发布)。

## 前端随镜像发布

一次 API 发布 = 后端 + **当时最新的前端**，装在同一个镜像里；发布侧三处可以机械互验"前端是哪一版"。

| 位置 | 内容 |
| --- | --- |
| 接口 | `GET /api/version` 的 `webVersion` / `webSha` / `webBuiltAt` 三个字段（读镜像内 `wwwroot/web-version.json`，缺失时返回 `"unknown"`） |
| 镜像内文件 | `/app/wwwroot/web-version.json`（由 pim-web 的产物镜像带进来，字段：`version` / `gitSha` / `builtAt`） |
| release 台账 | `pim-web-version.json`（releases 资产；前端三字段**原样转录**自烘焙好的镜像，另含产物镜像 tag/digest 与本次 API 版本） |

镜像 label（`docker inspect` 直接可读）：`org.pim.api-version`、`org.pim.web-version`、`org.pim.web-sha`。

**`workflow_dispatch` 输入**（两个契约输入都声明在 `ci.yml` 的 `workflow_dispatch.inputs`；
其中 `web_dist_ref` 还必须同步声明在 `build-docker.yml` 的 `workflow_call.inputs`，否则传不到镜像构建。
`rebuild_image_only` 只被 `ci.yml` 的组件判定消费，在 reusable workflow 里声明它只会变成一个没有行为的死参数）：

| 输入 | 作用 |
| --- | --- |
| `web_dist_ref` | 要内嵌的前端产物镜像引用（`sha-<7>`）。留空 = 取 `latest` 并在构建开始时冻结成不可变引用 |
| `rebuild_image_only` | 只重建镜像：只重烘 `pim-api-server`，API/MCP 资产从上一版 release 搬运。**后端确有变化时自动转全量构建**，绝不把旧资产搬进新版本的 release |

前端产物包**必须是 public**：本 workflow 的 registry 登录发生在镜像构建之后、且只在发版时执行，
构建阶段只能匿名拉取（这是契约，不接受在 pim-api 侧新增 registry 凭证）。

**防重复发版**：本次内嵌的前端版本与上一版 release 台账一致、且后端源码无变化时，不发新 release、
也不推新镜像 tag；判定依据（本次前端版本 / 上一版台账版本 / 后端 diff 是否为空）会写进 job summary。
上一版 release 或台账缺失时按"需要发版"处理，并写明"无上一版台账，按需发版处理"。

**契约刷新流程**：`/api/version` 属于跨仓契约。改了 `ApiVersionResponse` 必须在**同一个 PR** 里
重新导出 `contract/openapi.json`（CI 会硬校验），下游 `pim-web` 仓随后用
`scripts/ci/fetch-contract.sh` 刷新 `contract/openapi.json`、`provenance.json` 与
`client-web/src/api/generated.ts`；在其刷新之前，pim-web 的契约新鲜度门禁会变红，这是预期行为。

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

## 贡献约定

所有改动走分支 + Pull Request；提交信息与 PR 描述双语（英文 + 简体中文）。详见 [`AGENTS.md`](AGENTS.md)。
