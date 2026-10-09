# pim-api

PIM 的后端服务：数据接入、确定性处理、结论输出。**所有结论由服务端代码计算，可复现**；AI 只在建议与叙事场景出场。

## 本仓范围

| 路径 | 内容 |
| --- | --- |
| `src/Pim.Api` | HTTP 宿主（Minimal API），端点集中在 `src/Pim.Api/Endpoints/` |
| `src/Pim.Core` | 领域模型与核心逻辑 |
| `src/Pim.Infrastructure` | 基础设施：数据库、鉴权、文件、外部集成 |
| `src/modules/` | 业务模块 ×7：Calendar / Files / Mcp / Mobile / PcTracker / QuickNotes / Stats |
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

> **镜像与前端**：本仓镜像只含 API。需要 PIM API 同源伺服 SPA 时，构建前把前端（`pim-web` 仓）的产物解包到 `src/Pim.Api/wwwroot/`；或由部署层的反向代理伺服前端静态文件。

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
