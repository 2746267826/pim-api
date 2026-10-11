# contract/openapi.json

本文件是 **`pim-api` 仓契约的只读快照**，仅供本仓开发时对照，**不要手工编辑**。

- 来源：`2746267826/pim-api` 的 `contract/openapi.json`（该仓 CI 从 Test 实例导出并硬校验与代码同步）
- 快照取自：`pim-api@master`（刷新时解析到的不可变来源 commit：`940fe42face873e48c7c36af030d8ca838791471`）
- 文件 sha256：`4dd7eafb4938818e13766e7177e1d05f45a4090643f9fe6dcd8d108c4f82af53`（938271 字节）

机器可读的来源记录在同目录 `provenance.json`（由刷新脚本自动写出）。上面这几行是**给人看的摘要**，
权威值以 `provenance.json` 与文件字节为准 —— 它们由脚本维护，不要手工改。

## 为什么要放进本仓

本仓需要调用后端接口，但**不要凭猜测调用**。契约里有什么，才能调什么；契约里没有的，
就不要写进代码 —— 那是「需要新接口」的信号，按 `AGENTS.md` 的「接口契约与门禁」一节
把需求交给用户，由后端在 `pim-api` 实现并更新契约。

## 怎么刷新

后端契约更新后（用户告知，或 CI 报 `契约快照已陈旧`）：

```bash
scripts/ci/fetch-contract.sh
```

脚本会解析上游 ref → 不可变 40 位 commit SHA，按该 SHA 取字节，校验它确实是一份 OpenAPI 3.x 文档，
然后**一次性写出三个产物**：

| 产物 | 内容 |
|---|---|
| `contract/openapi.json` | 上游契约原始字节 |
| `contract/provenance.json` | 确定性来源记录：`repo` / `ref` / `resolvedRef` / `resolvedRefSource` / `sourcePath` / `sourceUrl` / `bytes` / `sha256`（不含时间戳） |
| `client-web/src/api/generated.ts` | 由仓内锁定的 `openapi-typescript` 生成，banner 只含快照内容摘要（不含可移动的 ref/SHA） |

任一步失败（取不到源、404、空体、HTML、非 OpenAPI3、生成失败）→ **三个产物一个字节都不写**并退出非 0。
刷新后**请一并提交这三个文件**，并在 PR 里说明刷新原因。

## 新鲜度门禁（CI 实际行为）

CI 的 `Verify client contract is up to date` 步骤（在 `build-web` 里，已被 `CI Gate` 消费）执行**只读**校验：

```bash
scripts/ci/check-contract-freshness.sh
```

它做三件事，任一不一致即非 0：

1. 用 `git ls-remote` 把 `PIM_CONTRACT_REF`（默认 `master`）解析成**一个**不可变 40 位 SHA，按该 SHA 取上游字节，
   与仓内 `contract/openapi.json` **逐字节比较**；
2. 用仓内锁定的 `client-web/node_modules/.bin/openapi-typescript` 从**仓内快照**重新生成到临时目录，
   与 `client-web/src/api/generated.ts` **逐字节比较**；
3. 校验 `contract/provenance.json` 存在、字段形状合法，且与本仓快照一致。

**语义约定：**

- 上游契约一变 → 本仓 CI 变红，直到有人刷新并提交。**这是预期行为，不是故障**；不要用跳过/放宽门禁来解决。
- 上游出现**非契约提交**（改了别的文件）时，契约字节没变 → **仍然绿**。`provenance.resolvedRef` 允许滞后于上游 HEAD，
  它记录的是「上次刷新的来源」，不参与「是否等于今天 HEAD」的判定。
- 取不到权威源（网络失败 / 404 / 空体 / HTML / 非 OpenAPI3）→ **一律 fail-closed**，绝不当作「跳过」或「当成新的」。
- 校验脚本**不写仓**；写仓的只有 `fetch-contract.sh`。

触发面：`push`（含 master）、`pull_request`、`workflow_dispatch` **都执行**该校验，没有 `if:` 跳过、
没有 `continue-on-error`。

## 注意

- 手写模型对不上契约时，编译/构建不会报错，问题只会在运行时出现。改接口相关代码前，先对照本文件。
- `client-web/src/api/generated.ts` 当前**没有被任何产品代码 import**；因此 `tsc` / `vite build` / `vitest` 全绿
  不能证明「手写模型符合契约」，它们只能证明「没有破坏既有回归」。契约符合性由上面的新鲜度校验与人工比对负责。
