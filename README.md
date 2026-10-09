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
