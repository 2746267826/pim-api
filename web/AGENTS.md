# Web Subdirectory Rules

本文件只补充 `web/**` 的差异；其余规范（分支/PR/worktree、评审、安全、生产隔离等）全部继承仓库根 `AGENTS.md`。冲突时以根规范为准，不要在本文件重复整套流程。

## 路径

- `web/client-web/`：前端源码；未特别说明时，命令均在此目录执行。
- `contract/openapi.json`（仓库根）：唯一 OpenAPI 权威；`web/` 不保存副本。
- `web/client-web/src/api/generated.ts`：生成物，禁止手工编辑。

## 契约

- 更新：在仓库根执行 `bash web/scripts/ci/update-contract.sh`（从根契约重生成）。
- 只读校验：`bash web/scripts/ci/check-contract.sh`；离线自检：`bash web/scripts/ci/test-contract.sh`（前置 `npm ci`）。
- 不追远端分支或 release，不再有跨仓刷新 PR。
- 脚本先规范化为 LF 再生成，保证 Windows 本地与 CI 逐字节一致。

## 构建与验证

- 依赖：`npm ci`
- 常规：`npm run lint`、`npm run test`、`npm run build`
- UI 可见改动另跑 `scripts/verify-pitfalls.mjs`、`qa-screenshots.mjs`、`qa-three-states.mjs`。
- 只重跑受影响的检查；根 CI 执行完整必需检查，不为新一轮 review 重复整套。
- `web-version.json` 由 `web/scripts/ci/write-web-version.sh` 生成；改逻辑要同步 `test-write-web-version.sh`。

## 产物与 CI

- `web/` 不维护 `.github/workflows`，也不单独发布或触发 API 发版；打包与发布由根流水线负责。
- 不提交 `node_modules/`、`dist/`、`shell-dist/`、`client-web/android/app/build/`、`client-web/src-tauri/target/`。