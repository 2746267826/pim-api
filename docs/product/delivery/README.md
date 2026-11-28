# PIM 全量交付台账：结构化事实源

本目录的 JSON 是 PIM 全量交付状态的唯一当前事实源。`docs/product/pim-delivery-ledger.md`
是由 `scripts/ci/render-delivery-ledger.py` 生成的当前视图，不得手工编辑。
`docs/product/pim-delivery-ledger-20261125.md` 等旧文本只保留历史追溯；若旧文本、旧表格与
结构化 JSON 冲突，一律以结构化 JSON 为准，不能把旧文本当作当前事实或第二事实源。

## 范围与验收条件冻结

- 交付包 A–R、Z 共 **160** 项，加 Release/版本与发布安全补充项 **S01**，合计 **161** 项。
- 交付包：`PIM功能确认-最终交付-20261122.zip`，SHA-256
  `b488a01e6f317a2735f387a918787204791fb32a38ad9ce064a07166f4b8a864`。
- item ID 集固定为每个前缀的 `01..count`，加 `S01`。前缀计数固定为：A 10、B 16、C 22、
  D 8、E 3、F 11、G 13、H 11、I 4、J 9、K 4、L 14、M 6、N 3、O 6、P 5、Q 8、R 5、
  S 1、Z 2。validator 从实际 items 重算前缀计数、状态计数和总数，不接受只改声明值或
  用另一个合法 ID 替换成员。
- 验收条件 ID 与文本另有一层冻结权威：`acceptance_scope_version = 2`、
  `acceptance_criteria_total = 731`，完整 ID + text 映射的 SHA-256 为
  `e3402e18b210f89ffaed6e0528c7d43984f570d32fc2d88c71d38c4cae0256b1`。
  validator 对“item ID → 排序后的 `[AC ID, text]` 列表”做规范化 JSON 哈希，逐 item 检查
  完整集合、重复和遗漏。仅保持总数不变、删除一个 AC 再补另一个合法 ID、替换 ID，或保留
  ID 但改写验收文本，都不能通过；新增、删除、替换 AC 或修改文本必须显式升级版本并更新
  权威哈希，同时同步全部派生计数和生成视图，不能只改 `acceptance_criteria_total`。

## 文件

| 文件 | 内容 |
| --- | --- |
| `ledger.json` | 入口：范围、冻结 AC 范围、快照、批次、161 个 item、渲染目标 |
| `prs.json` | PR 注册表：仓库、编号、状态、head、merge_commit、状态 freshness、观测时点 |
| `ci-runs.json` | CI run 注册表：PR、head、结论、关键 job、freshness、`head_is_current` |
| `real-flows.json` | 真实 API/浏览器/设备/桌面/发布流程；每条流程显式声明 `items` 与 `ac_ids` |
| `evidence.json` | E0–E6 证据，含 claim、source、source_commit、observed_at、freshness、limits |
| `decisions.json` | 范围例外、阻塞裁决与迁移记录；每条记录显式声明绑定的 `ac_ids` |

所有数组字段都必须是 JSON 数组，成员必须是非空字符串或对象，并按字段语义拒绝重复。
item 的 `prs`、`evidence`、`decisions`，AC 的 `evidence`，以及 flow/evidence 的
`items`、`ac_ids`、`platforms`、`artifacts` 都做显式类型和唯一性检查。

## 状态规则

| 状态 | 含义 |
| --- | --- |
| `PASS` | 当前 head、全部冻结 AC、全部适用平台、正常与异常分支均有 current 真实证据 |
| `FAIL` | 当前产品仍有确认缺口，或实现未合并 |
| `NOT-VERIFIED` | 实现可能存在，但当前 head 没有完整真实验收 |
| `BLOCKED` | 缺产品裁决或外部前提；必须写 `blocked_reason` 与 `unblock_conditions` |
| `APPROVED-EXCEPTION` | 交付包原决议明确跳过或替代；必须引用 `approved` 决策 |

不使用 `PARTIAL`。部分实现写进 `implementation`，不发明新状态。`implementation` 与
`legacy_status` 保留旧台账原文，并带 `implementation_basis`；它们不是当前 head 的实现证明。

`legacy_source.file` 必须固定为历史快照
`docs/product/pim-delivery-ledger-20261125.md`；`line` 必须命中对应 item 行，且该行旧状态
必须等于 `legacy_status`。`section` 必须是该行向上最近的 Markdown 章节标题；若带
`audit_line`/`audit_section`，也同样校验审计行与章节标题。`stale` item 的 `status` 必须等于
`legacy_status`，不能静默改写历史。这里只证明仓库内历史快照的行定位，不证明交付包外部
artifact 的真伪。历史文件中的旧文本不是当前事实源。

单项还可以在某个验收条件上记录 `blocked`（原因、解除条件、决策 ID），用于“条目没有整体
BLOCKED、但某条 AC 缺产品裁决”的情况，例如 `R05-AC05` 的永久墓碑容量策略。AC 级阻塞
不得与 `verified: true` 同时存在，且 blocked decision 必须是 `pending`、双向引用 item，并把
decision evidence 列入 item.evidence；该 decision 的 `ac_ids` 必须恰为当前 AC。一个 pending
decision 最多绑定一个 AC，不能把 `R05-AC05` 的裁决复用到 `R05-AC06`。整体 `BLOCKED` item
同样必须引用至少一个 pending decision。

### 升级为 PASS 的条件

1. `freshness` 为 `current`，且 `implementation_basis` 不是 legacy 快照；
2. item 的完整 AC ID/text 集与冻结权威映射一致，全部 `acceptance_criteria[].verified` 为真；
3. 每条 AC 至少引用一条 current、`result=passed` 的 E4/E5/E6 真实流程证据；E1/E2/E3、
   `failed`、`blocked`、`passed_legacy`、`not-run` 或 stale 证据都不能代替 AC proof；
4. 该流程与 evidence 显式声明同一个 AC，且 flow/evidence 的 `items` 与 `ac_ids` 完全一致；
   平台覆盖只从这些 AC 关联的 passed flow 的 `platforms` 字段推导，不能被未声明 AC 的
   item-level flow 单独满足；
5. 每个 `required` 与 `read_only` 平台都被至少一条上述 AC 关联 passed flow 覆盖；
6. 同一 item 不得同时存在 current `failed` 或 `blocked` 真实流程。PASS item 若仍保留
   stale `failed`/`blocked` 历史流程，该流程必须通过 `superseded_by` 指向覆盖其全部 AC 的
   current passed 流程；没有明确替代关系的历史失败不能与 PASS 共存，不能用“保留失败同时
   标 PASS”绕过；
7. item 引用的每个实现 PR 必须是 `state_freshness=current` 的 `merged` PR，`head` 与
   `merge_commit` 都不能为空且必须符合 SHA 格式；pim-api 的 head/merge_commit 还必须能在
   本地 Git 解析，二者至少满足相等、head 是 merge_commit 祖先、tree 相同或 merge 提交声明
   该 PR 编号之一，merge_commit 还必须属于 `master` 且不晚于 `source_snapshot.commit`。
   merge 发生在快照之后时必须先刷新快照再 PASS。Android/Windows 没有本地仓库对象，head
   只做结构化校验；外部 GitHub 的 head↔merge 关系不能在离线 DB 校验中被声称已证明；
8. 关联的 current CI 证据不得是 stale/旧 head；current run 必须关联
   `state_freshness=current` 的 PR，repository 必须是 `2746267826/<repo>`，workflow 必须为
   登记值 `CI`，jobs 只能来自该仓库固定集合且必须包含 `CI Gate`。PASS 中 current CI 还必须
   `head_is_current=true`、head 与对应 PR head 一致、run 结论为 `success`，且 CI 证据关联的
   item 必须引用该 PR；
9. 任意 AC 为 BLOCKED、pending，或 AC 引用了 stale/不合格证据时，不得标 PASS。

PASS 至少要有 `required` 或 `read_only` 平台；把全部适用平台改成 `coordination` 会清空平台
覆盖集合，不能据此升级为 PASS。合法 supersede 只放行 stale `failed`/`blocked` 历史流程：
其 `superseded_by` 必须指向覆盖该流程全部 AC 的 current `passed` 流程；其他 stale 证据、
未声明替代关系的失败，以及覆盖不足或目标不合格的替代关系仍会失败。

**PR-less / item-only 例外**：若一个 item 在结构化源中确实没有关联实现 PR（`prs: []`），
只有在其他条件全部满足，并且 item.decisions 恰好引用一个 approved、`type=item-only-pass`
的决定时才可升级为 PASS。该决定必须只绑定该 item、`ac_ids` 为空、记录 `approved_by`，
且其 E0 evidence 已列入 item.evidence；这是一条显式批准事实，不是“没有 PR 就自动通过”
的捷径。清空 `prs` 后直接标 PASS 仍会失败。只要 item 关联了 PR，所有 PR 都必须满足上述
merged/current/merge_commit 规则；若当前资料没有定义某项的 PR，不要为了通过门禁伪造 PR
或验收。

CI 绿、代码合并、`CLEAN`、模拟器、jsdom、组件测试、旧 head 或旧 run 都不等于 PASS。

## 证据等级 E0–E6

| 等级 | 用途 | 不能证明 |
| --- | --- | --- |
| `E0` | 决议、范围批准、迁移记录 | 任何实现或运行行为；decision evidence 保持 stale，不能冒充 current 执行证据 |
| `E1` | 源码/文档只读检查 | 运行行为、端到端流程 |
| `E2` | 单元/组件/集成测试 | 真实环境、真实设备、用户流程 |
| `E3` | CI run（记录 run id 与 head） | 当前 head、合并、真实验收 |
| `E4` | 真实 API/PostgreSQL/浏览器/客户端链路 | 未覆盖的设备与生产环境 |
| `E5` | 真实 Android 实机/OEM、Windows 交互桌面 | 生产部署 |
| `E6` | 正式发布、生产部署或线上只读验证 | 未执行的分支 |

每条证据必须有 `source`、`observed_at`、`freshness`；current 证据还必须有可解析的
`source_commit`，且等于 `source_snapshot.commit`。真实流程还必须有 `environment` 与
非空 `artifacts`（`passed`、`passed_legacy`、`failed`、`blocked` 均适用），未执行项必须写
非空 `limits`。`passed_legacy` 必须是 `stale`；`not-run` 必须是 `not-verified`。

`real-flows.json` 与 `evidence.json` 是 1:1 关系：每个 `EV-RF-*` 证据只能被一个流程引用，
流程与证据的 `items`、`ac_ids`、`source_commit`、`freshness` 必须一致；流程覆盖的每个 AC
都必须属于 `items` 中的条目，item 必须反向引用该流程证据。不能用一个未声明 AC 的通用流程
证明多个 item/AC，也不能让 evidence 额外声明 item 后逃避反向引用。

## Freshness 与 current 时间

- `current`：对应当前 `source_snapshot` 的 head/版本，可在本轮复核。
- `stale`：来自旧台账或旧 head，只能用于定位缺口；`passed_legacy` 只能使用此值。
- `not-verified`：明确未验证的流程占位；`not-run` 必须使用此值。
- `unknown`：来源不明，必须补证。

`current` 的 `observed_at` 必须是带时区的 ISO-8601 日期时间，且严格晚于
`source_snapshot.commit_date`、不得晚于 `source_snapshot.observed_at`。当前 PR/CI 记录也只
能在该时间窗内声称 current。`source_snapshot.commit` 必须能在本地 Git 解析，并严格等于
声明的 `master` 当前 tip：优先使用 `refs/remotes/origin/master`，否则使用
`refs/heads/master`。只把旧 snapshot 作为新 master 的祖先不再可通过；`current` 证据、
流程、PR 与 CI 的 `observed_at` 以及 snapshot 的 `commit_date`/`observed_at` 还必须是过去
时间（UTC wall clock，允许 5 分钟时钟偏差），未来时间直接失败。

`source_snapshot.branch` 只允许 `master`，缺少 `origin/master` 或
`refs/heads/master` 引用时提交归属校验 fail-closed，不允许静默降级为 warning 后继续
current/PASS。旧日期、非法日期、不可解析 commit、master tip 不一致或 source head mismatch
都必须失败。测试日期固定在 2026-11-28，不把本地时区字面值当 UTC；所有比较都使用带时区的
`datetime`。

`current` 的 `FAIL`/`NOT-VERIFIED` 仍必须引用至少一条与该 item 关联的 current E4–E6
真实流程证据（可以是 `failed`/`blocked`）；这证明本轮确实运行过，不代表 PASS。

迁移时全部 item 初始为 `stale`，并保留 `legacy_status` 与 `legacy_source`。

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

B1–B7 覆盖全部 161 项，每项恰属一个批次。前缀/成员映射是固定契约：

| 批次 | 固定前缀 |
| --- | --- |
| B1 | A、B、C |
| B2 | D、E、F |
| B3 | G、H、I |
| B4 | J、K |
| B5 | L、M、N |
| B6 | O、P、Q |
| B7 | R、S、Z |

每批的 `prefixes` 必须与成员实际前缀一致，成员集合必须恰好是该批固定前缀下的全部 item；
不能仅移动一个成员而保留合法前缀和计数。`close_criteria` 当前只要求非空、无重复的描述性
条件，具体文案不是冻结契约；真正关闭门禁由状态重算、AC/证据规则和依赖闭包执行。

关闭条件：

1. 批次内全部 item 为 current `PASS` 或有效 `APPROVED-EXCEPTION`，且没有
   `FAIL`/`NOT-VERIFIED`/`BLOCKED`；
2. 每个 item 的 `required`/`read_only` 平台都有 current 真实证据；`APPROVED-EXCEPTION`
   也必须由 current `passed` 真实流程覆盖这些平台，不能靠“例外”标签跳过批次平台门禁；
3. `BLOCKED` 项解除前批次不得关闭；`APPROVED-EXCEPTION` 保持原决议；
4. 批次依赖必须覆盖所有成员（包括 `APPROVED-EXCEPTION`），不能只检查 PASS 项的一跳：
   依赖目标必须是 `PASS` 或 `APPROVED-EXCEPTION`；跨批次依赖的目标批次必须已 `closed`，
   并递归检查整条传递依赖；全局依赖环也必须拒绝；
5. 没有任何未说明的 `stale` 依赖或未满足的外部前提。

validator 从实际 item 状态重算批次：有 `FAIL`/`NOT-VERIFIED` 必须 `open`；没有前两者但仍有
`BLOCKED` 必须 `blocked`；全部为 current `PASS` 或有效 `APPROVED-EXCEPTION` 只是状态前提，
仍需通过上述平台与依赖门禁才能 `closed`。

## 安全边界

- 不访问生产数据库 `pim_prod`；需要真实数据时只用镜像的 `pim_test`。
- 本目录只记录仓库、PR 编号、run id、SHA 与流程描述；不写入 token、密钥、生产日志、
  生产数据导出或 GraphEventId/ChangeKey 等敏感字段。
- `source_snapshot.repo` 固定为 `2746267826/pim-api`，`scope.source_package` 固定为
  `PIM功能确认-最终交付-20261122.zip`，`scope.source_package_sha256` 固定为
  `b488a01e6f317a2735f387a918787204791fb32a38ad9ce064a07166f4b8a864`。这些值是冻结契约，
  证明元数据与已登记交付包一致，不证明外部 zip 仍存在或其字节在运行时被重新哈希。
- 证据里的 URL 只指向公开 GitHub PR/run 或仓库内文件。
- 校验与渲染脚本只做本地文件与本地 Git 读取，不访问网络。
- `render-delivery-ledger.py` 只写 `docs/product/pim-delivery-ledger.md`，不接受任意输出
  路径；`--stdout` 只用于校验/测试读取生成内容，不落盘。`rendered_view.path` 偏离固定路径
  会被渲染器拒绝。
- validator 是结构、关系、元数据门禁：它能证明引用可追溯、字段齐全、状态闭包自洽，
  **不能证明外部 artifact 的真伪、真实设备用户流程或生产部署真的发生**。这些仍需人工/环境
  核验，并在证据的 `environment`、`artifacts`、`limits` 中如实记录。Android/Windows 的
  PR head↔merge 关系、GitHub API 声称的状态和 CI run 内容也不能靠本仓库离线对象独立复验。

## 工作流与验证边界

```bash
# 1. 修改 docs/product/delivery/*.json
# 2. 重新生成当前视图
python3 scripts/ci/render-delivery-ledger.py
# 3. 校验（同时检查视图是否为生成产物且恰好 161 项）
python3 scripts/ci/validate-delivery-ledger.py
# 4. 聚焦测试
python3 scripts/ci/test-validate-delivery-ledger.py

# 本地聚焦门禁：重渲染 → 校验 → 聚焦测试
bash scripts/ci/check-delivery-ledger.sh
```

`scripts/ci/check-delivery-ledger.sh` 是本地聚焦门禁，**不是**现有 CI Gate 的一部分；当前
`.github/workflows/*` 没有执行它。通过该脚本只能说明本地结构/关系测试通过，不能写成云端 CI
通过，也不能替代真实用户验收。修改 JSON 后必须先渲染再校验；校验器会拒绝未同步的生成视图。
渲染器固定写入当前视图路径；`--stdout` 仅用于无副作用地读取生成结果。聚焦测试中的真实台账
smoke 在临时副本上运行：建立独立 Git fixture，并把 pim-api merged PR 的 head/merge_commit
与 `source_snapshot` 校准到 fixture commit，保留全部真实 item/evidence/flow 计数，不依赖
调用者的 `origin/master`、PR refs 或悬空对象。该 fixture 只证明台账结构可在干净 clone 中
复验，不证明外部 GitHub 的 head↔merge 关系。直接对实际台账运行 validator 时，pim-api
merged PR 的 head/merge_commit 仍需在当前 Git object DB 可解析；缺少 PR refs 的 clone 会
fail-closed，而不是降级成“看起来通过”。

`scripts/ci/generate-pim-delivery-ledger.sh` 是兼容旧调用方式的 shim：它不再解析旧源表，
而是忽略旧源表与旧输出参数并转发到 `render-delivery-ledger.py`，只写固定视图。它不是
第二事实源，也不能生成第二份可编辑台账或写到调用者指定路径。

渲染视图的证据分类合计包含 `audit-note`、CI、真实流程和决议四类；证据等级表必须同时显示
current、stale、not-verified、unknown，等级总条数不能被读成 current 通过证据。

## 历史快照

- `docs/product/pim-delivery-ledger-20261125.md`：2026-11-25 的逐项文本快照，保留作历史追溯。
- `docs/product/pim-delivery-ledger.md`：当前生成视图，由本目录渲染。

两份文本都不是当前事实源；出现冲突时以结构化 JSON 为准。当前结构化事实必须持续保持
真实状态，不能为了门禁通过而伪造真实用户流程或把旧证据升级为 current。
