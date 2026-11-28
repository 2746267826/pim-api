# PIM 全量交付台账：结构化事实源

本目录是 PIM 全量交付状态的唯一结构化事实源。`docs/product/pim-delivery-ledger.md` 是由
`scripts/ci/render-delivery-ledger.py` 生成的当前视图，不得手工编辑。

## 范围

- 交付包 A–R、Z 共 **160** 项，加 Release/版本与发布安全补充项 **S01**，合计 **161** 项。
- 交付包：`PIM功能确认-最终交付-20261122.zip`，SHA-256
  `b488a01e6f317a2735f387a918787204791fb32a38ad9ce064a07166f4b8a864`。
- 前缀计数固定为：A 10、B 16、C 22、D 8、E 3、F 11、G 13、H 11、I 4、J 9、K 4、
  L 14、M 6、N 3、O 6、P 5、Q 8、R 5、S 1、Z 2。校验器按此硬校验，增删范围必须显式改
  `scope.counts_by_prefix`、`scope.counts_by_status` 与 `scope.total` 并同步 README。
- 精确 ID 集固定为每个前缀的 `01..count`，加 `S01`；不允许用另一个合法 ID 替换范围成员。
  校验器从 `items` 重算前缀计数与状态计数，并拒绝声明值与实际值不一致。

## 文件

| 文件 | 内容 |
| --- | --- |
| `ledger.json` | 入口：范围、快照、批次、161 个 item、渲染目标 |
| `prs.json` | PR 注册表：仓库、编号、状态、head、merge、状态 freshness、观测时点 |
| `ci-runs.json` | CI run 注册表：PR、head、结论、关键 job、freshness |
| `real-flows.json` | 真实 API/浏览器/设备/桌面/发布流程记录；每条流程必须引用唯一对应证据 |
| `evidence.json` | E0–E6 证据，含 claim、source、freshness、limits |
| `decisions.json` | 范围例外、阻塞裁决与迁移记录 |

## 状态规则

| 状态 | 含义 |
| --- | --- |
| `PASS` | 当前 head、全部 AC、全部适用平台、正常与异常分支均有 current 真实证据 |
| `FAIL` | 当前产品仍有确认缺口，或实现未合并 |
| `NOT-VERIFIED` | 实现可能存在，但当前 head 没有完整真实验收 |
| `BLOCKED` | 缺产品裁决或外部前提；必须写 `blocked_reason` 与 `unblock_conditions` |
| `APPROVED-EXCEPTION` | 交付包原决议明确跳过或替代；必须引用 `approved` 决策 |

不使用 `PARTIAL`。部分实现写进 `implementation`，不发明新状态。`implementation` 与
`legacy_status` 保留旧台账原文，并带 `implementation_basis`；它们不是当前 head 的实现证明。

单项还可以在某个验收条件上记录 `blocked`（原因、解除条件、决策 ID），用于“条目没有整体
BLOCKED、但某条 AC 缺产品裁决”的情况，例如 `R05-AC05` 的永久墓碑容量策略。AC 级阻塞
不得与 `verified: true` 同时存在。

### 升级为 PASS 的条件

1. `freshness` 为 `current`，且 `implementation_basis` 不是 legacy 快照；
2. 全部 `acceptance_criteria[].verified` 为真，且每条 AC 都至少引用一条 current E4/E5/E6
   真实流程证据；
3. 该真实流程必须 `result=passed`、`freshness=current`，并在 `real-flows.json` 中反向引用
   本 item；平台覆盖从该流程的 `platforms` 字段推导，不使用 evidence 中未声明的
   `platforms`/`flows` 字段；
4. 每个 `required` 与 `read_only` 平台都被至少一条上述流程覆盖；
5. 依赖的 PR 为 `state_freshness: current`，且不得把 stale CI 当作通过证据；
6. 任意 AC 为 BLOCKED、pending，或 AC 引用了 stale/不合格证据时，不得标 PASS。

CI 绿、代码合并、`CLEAN`、模拟器、jsdom、组件测试、旧 head 或旧 run 都不等于 PASS。

## 证据等级 E0–E6

| 等级 | 用途 | 不能证明 |
| --- | --- | --- |
| `E0` | 决议、范围批准、迁移记录 | 任何实现或运行行为 |
| `E1` | 源码/文档只读检查 | 运行行为、端到端流程 |
| `E2` | 单元/组件/集成测试 | 真实环境、真实设备、用户流程 |
| `E3` | CI run（记录 run id 与 head） | 当前 head、合并、真实验收 |
| `E4` | 真实 API/PostgreSQL/浏览器/客户端链路 | 未覆盖的设备与生产环境 |
| `E5` | 真实 Android 实机/OEM、Windows 交互桌面 | 生产部署 |
| `E6` | 正式发布、生产部署或线上只读验证 | 未执行的分支 |

每条证据必须有 `source`、`observed_at`、`freshness`；真实流程还必须有 `environment` 与
`artifacts`，未执行项必须写 `limits`。

`real-flows.json` 与 `evidence.json` 是 1:1 关系：每个 `EV-RF-*` 证据只能被一个流程引用，
流程的 `items` 必须与证据 `items` 完全一致，且 item 必须反向引用该证据。`passed` 流程必须
current；历史通过只能是 `passed_legacy` + `stale`；未执行只能是 `not-run` + `not-verified`。

## Freshness

- `current`：对应当前 `source_snapshot` 的 head/版本，可在本轮复核。
- `stale`：来自旧台账或旧 head，只能用于定位缺口。
- `not-verified`：明确未验证的流程占位。
- `unknown`：来源不明，必须补证。

迁移时全部 item 初始为 `stale`，并保留 `legacy_status` 与 `legacy_source`。`stale` item 的
`status` 必须等于 `legacy_status`，禁止静默改写历史。

PR 注册表每行必须带 `observed_at`。当前状态由 `git-history` 或 `github-api` 验证；无法重新
观测的旧记录必须显式标为 `stale`，不得保留旧 head/SHA 却声称 current。`source_snapshot` 的
`commit` 必须能在本地 Git 解析、属于声明的 `master` 分支，并与 `commit_date` 一致。

## 平台角色

| 角色 | 含义 |
| --- | --- |
| `required` | 必须在该平台完成当前验收 |
| `read_only` | 只消费/查看，不产出写操作 |
| `coordination` | 只做契约或跨仓协调 |
| `not_applicable` | 明确不在该项范围 |

平台固定为 `api`、`mcp`、`web`、`android`、`windows`、`release`。原台账的
“Windows（Web 仅查看/回放）”表示为 Windows `required` + Web `read_only`。

## 批次

批次 B1–B7 覆盖全部 161 项，每项恰属一个批次。关闭条件：

1. 批次内全部 item 的 `required` 平台都有 current 真实证据；
2. 没有未说明的 `stale` 依赖；
3. `BLOCKED` 项解除前批次不得关闭，`APPROVED-EXCEPTION` 保持原决议；
4. 仍有 `FAIL`/`NOT-VERIFIED` 时批次保持 `open`。

校验器从实际 item 状态重算批次：有 `FAIL`/`NOT-VERIFIED` 必须是 `open`；没有前两者但仍有
`BLOCKED` 必须是 `blocked`；只有全部为 current `PASS` 或有效 `APPROVED-EXCEPTION` 才能
`closed`。批次 items 必须恰好覆盖全部 161 项一次，`prefixes` 必须与成员一致。

## 安全边界

- 不访问生产数据库 `pim_prod`；需要真实数据时只用镜像的 `pim_test`。
- 本目录只记录仓库、PR 编号、run id、SHA 与流程描述；不写入 token、密钥、生产日志、
  生产数据导出或 GraphEventId/ChangeKey 等敏感字段。
- 证据里的 URL 只指向公开 GitHub PR/run 或仓库内文件。
- 校验与渲染脚本只做本地文件与本地 Git 读取，不访问网络。

## 工作流

```bash
# 1. 修改 docs/product/delivery/*.json
# 2. 校验
python3 scripts/ci/validate-delivery-ledger.py
# 3. 重新生成当前视图
python3 scripts/ci/render-delivery-ledger.py
# 4. 再校验（校验器会检查视图是否为生成产物且恰好 161 项）
python3 scripts/ci/validate-delivery-ledger.py
# 5. 负例测试
python3 scripts/ci/test-validate-delivery-ledger.py
```

`scripts/ci/generate-pim-delivery-ledger.sh` 是旧 awk 生成器，只保留历史用途，不再产出
当前台账；当前视图统一由 `render-delivery-ledger.py` 生成。

`implementation_basis` 必须以 `legacy-snapshot-` 或 `current-` 开头。渲染视图会把 legacy
标为“旧台账快照（非 current）”，并把 AC 级 BLOCKED 的原因、解除条件与 decision 单独列出。

## 历史快照

- `docs/product/pim-delivery-ledger-20261125.md`：2026-11-25 的逐项文本快照，保留作历史追溯。
- `docs/product/pim-delivery-ledger.md`：当前生成视图，由本目录渲染。

两份文本都不再是事实源；出现冲突时以 `stale` 标注与 `legacy_source` 指向的历史原文为准，
新增事实只写结构化源。
