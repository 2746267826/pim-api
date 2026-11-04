## 当前审计指针（2026-11-04）

本路线图保留 45 个稳定 ID、32 capabilities、32 gaps、13 MIG、AC1–19、M0–M5 和 #516 五阶段追踪范围；不代表整体完成。逐阶段用户可理解排期及 #516/#539/#551–554/Android #59–63 状态见[笔记整顿方案](PIM笔记整顿与推进方案.md)和[审计台账](docs/audit/notes-roadmap-acceptance-audit-20261104.md)。下方 v0.53 执行层是 2026-11-02 历史快照，不应当作当前发布/PR 状态。

### 当前交付快照（2026-11-04）

- API：材料定向测试 26/26；Test Swagger HTTP 200；官方 contract update/check 通过；Notes+Contract 有界回归 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout，108 skipped 不计 PASS。
- Web：提交链 `78c4a17a` → `d229185b` → `f5037e08` → `e6f5ea82` → `d0b796f1`；四个 native-notes 聚焦测试文件 52/52，`npm run build` PASS，lint 仅既有 warnings；材料保全已加测。Playwright 因 Chromium executable 缺失而 FAIL，未进入页面、无截图，浏览器验收 NOT-VERIFIED。
- Android：Draft PR #70 / `3f6b1d4`；Kotlin compile PASS、契约生成物一致；2256 JVM 中 14 项失败（首个为既有 `NoteEditHistoryTest.oversizedDocumentDegradesHistoryWhileTheNoteStillSaves`）；connected UI NOT-VERIFIED。正常跨端发现、完整恢复、续写与冲突尚未整体验收。
- ASR：管理员入口为 Web「设置 → AI 与 ASR → ASR 配置」；普通用户不显示凭据配置，阅读写作不依赖配置。
- M0–M5、#516、#539、#551–554、Android #59–63 均未整体完成，issues 保持开放。生产、真实 provider、硬件、设备/OEM 与端到端跨端往返仍 NOT-VERIFIED。

<!-- current-pointer-pr44-pr67-repaired-20261102 -->
## 历史执行检查点（2026-11-02；当前状态见上方 2026-11-04 快照）

- **Android 当前产品**：正式版本 `android-v26.11.1161` 已于 `2026-11-02T11:02:40Z` 发布，继承 PR #66 的1158。PR #67 合并提交 `56169e788c30bcec18a42753906c83f8e8fce146`；master CI `41432001403` 七个 jobs 全部成功，tag 指向 merge，480 B版本账本 SHA-256 `88cabc01b60b24b54ba3cf496c976896c9011d35df2c65df8574b340e72d5b34` 与 Release digest匹配。核验回执：`scratch/android-pr67-passive-location-20261102/delivery/verified-release-receipt.json`。这不等同于真实设备/OEM验收。
- **Android #67 验证**：最终 head `fe9bf9ea4f88af58e752a00714cd5cad8101abc7` 的 PR CI `41429913121` 与 CI Gate 全部成功，正常 matched-head 合并并已发布1161。最终源码 App 2239/2239、focused 2/2、Node editor 9/9 与 build 通过，父独立核验全部190份XML与5份日志哈希；回执 `scratch/android-pr67-passive-location-20261102/parent-verified-receipt.json`。首轮 CI `41424187890` 单项失败原始证据保留；生产仍使用 `Dispatchers.Default`。
- **API 路线图文档**：docs PR #559 已合并；PR CI `41419400564` 的 release job 为 `SKIPPED`。master CI `41420427742` 的 release job 为 `SUCCESS`，执行结果为 no-components，未产生新产品 Release；当前 API 产品 Release 仍为 `api-v26.11.1060`。canonical 文档在 API master v0.53；不改写已合并文档历史。
- **TASK / ASR 派生链**：TASK 已由父集成提交 `d5fa`，focused tests `21/21` 通过，仅代表该 focused 范围，不代表完整套件或端到端验收。ASR 结果证据围栏审查包 `a5da9200` 已集成为 `750ab8f1`，校验日志与 durable job 身份、传输结果及重复字段；组合测试仍待执行。Derived unknown 状态风险继续开发，公开整篇 DELETE 尚未开放。
- **Windows**：最新已核产品版本仍为 `windows-v26.11.1121`。服务器电脑/浏览器历史查询已进入 [PR #44](https://github.com/2746267826/pim-windows/pull/44)，head `b9890adaaf7b316ade2be80c9f10068baefa3d03` 的 CI `41428353346` 与 CI Gate 全成功（PR release 按规则 skipped）。同源本地 Core 389/389、WPF App 117/117、官方契约 freshness/generated 均通过，父已独立核对 TRX 和日志哈希；回执 `scratch/full-roadmap-20261101/windows-history-b9890ada-validation-20261102/parent-verified-receipt.json`。合并暂等活动页详情防串写补丁及新源码验证，不把旧检查继承到补丁。MIG-07 分类/应用上下文与 INK-01 已存笔迹重开编辑继续在独立树开发，尚未交付。
- **NOTE-03 / MIG-04 下一批**：旧快速记录可逆本机副本迁移已进入 Android [PR #68](https://github.com/2746267826/pim-android/pull/68)，源 `d39577042f543112f0aab26fc8c68c025551f5f7`、首轮 CI `41433264762` 已失败，非仍在运行。两项契约检查和 shell 构建成功；App 因可空 JSON 类型、Compose 实验 API 标记及函数引用三处编译问题失败，Dewey 正连同撤回映射后的删除结果确认一起修复。新增迁移回归尚未验证，不把测试源码存在记为执行通过。此包仅创建本机副本并保留服务器原记录和附件引用，不包含尚在开发的 M3 schema-v1 互操作。
- **Windows 下一批组合**：MIG-07 分类/应用上下文与 INK-01 笔迹重开、闭窗围栏、擦除分裂后的稳定身份已集成为 `wt/windows-notes-next-20261102` 的 `4b3c772a`。官方生成脚本已运行，生成物差异与校验由子代理核对；还须接入 PR #44 最终修复并针对组合源码完整验证，目前无新 PR 或 Release。
- **总体边界**：M0–M5、45 个稳定 ID、32 capabilities/gaps、13 MIG、AC1–19 与五阶段范围均保留，尚未整体完成。产品 CI/Release 不能替代生产部署、真实 provider、设备/OEM、GPU/VRAM 验收。

以下检查点均为写入时快照；当前状态以本段与 canonical v0.53 为准。

<!-- historical-pointer-v053-pr559-android66-20261102 -->
## 历史检查点（2026-11-02，v0.53 已进 master；Android #66 已合并待发布）

唯一完整路线图为 [pim-api master 的 v0.53 当前执行层](https://github.com/2746267826/pim-api/blob/master/docs/product/native-notes-roadmap.md#v053-current)，docs PR #559 已正常 matched-head 合并：head `356145a0849254902b491a8590f55650a1b5d7ea` → master `622382f4708bc22c2a233a8e6babc5fbd629c274`，时间 `2026-11-02T09:24:46Z`。PR CI `41419400564` 成功，CI Gate 与 release job 成功，平台 build/notify 按 docs-only 路径 skipped；未产生新产品 Release，最新已核产品版本仍为 `api-v26.11.1060`。回执：`scratch/full-roadmap-20261101/roadmap-v053-pr559-delivery-20261102/verified-docs-receipt.json`。v0.53 保留45稳定ID、32 capabilities/gaps、13 MIG、AC1–19、M0–M5与五阶段，不代表这些范围整体完成。

- **API/Web**：产品 Release 源仍为 `7922f6e2` / `api-v26.11.1060`；后续 master 前进到 docs merge `622382f4`，docs-only 未产生新产品 Release，也不代表生产部署或真实 provider 验收。
- **Windows**：`windows-v26.11.1121` 仍为最新已核产品版本。MIG-07 在独立 worktree `wt/windows-mig07-knowledge-20261102`、branch `codex-win/windows-mig07-knowledge-20261102`，基线 `b0d0b28e14e58f96d45fd23b2b2d585f21fc791c`；正在开发原生分类树与应用上下文管理 UI、契约生成/API client 接入及账户/服务器/视图代数围栏，尚未提交或验证完成。
- **Android**：PR #66 head `7ffe554a88ab86663383ae57b7eefe1ef5ab8a4d` 已正常 matched-head 合并为 `d29a364073a095657f384eeabec4f3ce34676904`，时间 `2026-11-02T09:24:45Z`；PR CI `41417477292` 所有必需检查与 CI Gate 成功。master CI/正式 Release 尚待核验，当前产品 Release 仍为 `android-v26.11.1154`；未做真实设备验收声明。
- **并行风险修复**：ASR 与 Derived status-only/unknown 状态风险已派 Sagan 修复；Euler 并行处理 TASK 状态/日志风险。均为进行中，不能记为已修复或验收完成。
- **验收边界**：#516、M0–M5、AC1–19 与真实 provider/模型、生产部署、设备/OEM、GPU/VRAM 验收仍按各自证据标记；CI、文档合并或 Release 均不能替代外部验收。

以下 v0.53 / Windows 1121 检查点及更早内容保留写入时状态；当前事实以本段与 canonical master v0.53 为准。

<!-- historical-pointer-v053-windows1121-android66-20261102 -->
## 历史检查点（2026-11-02，API 1060 / Windows 1121 已核）
<!-- current-pointer-api-1060-verified-20261102 -->
## 历史检查点（2026-11-02，API 1060 Release 核验时；Windows 1118 后由 1121 替代）

API #558 源提交 `6f6d1c11` 已正常合并为 `7922f6e2ce6e8884dd3251c0e3af933912167d14`。PR CI `41401604874` 为 6488 PASS / 100 skipped / 0 failed / 6588 total，耗时 25m23s；master CI `41404982876` 为 6488 PASS / 100 skipped / 0 failed / 6588 total，耗时 26m40s，原始日志 SHA-256 `38fa8ea4494669c0c851973d0dd33a05c500af5bf71e8f62205f0c988081dfd9`，路径 `scratch/full-roadmap-20261101/api-pr558-delivery-20261102/master-api-job-41404982876.log`。正式 [API Release `api-v26.11.1060`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1060) 于 `2026-11-02T07:44:52Z` 发布，tag 指向上述 merge。120 B `pim-web-version.json` 的 version/apiVersion 为 `26.11.1060`、gitSha 为 `7922f6e`、builtAt 为 `2026-11-02T07:14:47Z`、SHA-256 为 `b8e15f75b3802ad549c5a228fa1a69060665d55ad50816a6d5904c46577f95f1`，与 Release 元数据匹配；回执：`scratch/full-roadmap-20261101/release-api-v26.11.1060-20261102/verified-receipt.json`。Release 不代表已部署到生产环境；生产、真实 provider 和设备验收仍为 `NOT-VERIFIED`。

该检查点写入时 Windows `windows-v26.11.1118` 与 Android `android-v26.11.1154` 均已发布；Windows 现行版本见上方 `windows-v26.11.1121`。Release 不代表 #516 或 M0–M5 完成。#516 仍 OPEN，AC1–19、真实 provider、设备/OEM、GPU 清退和生产验收未整体完成。

- **当前集成**：Android `5d106e3` 已集成 Dewey 的 `7922` 契约刷新和测试小包；Windows `774ce1d8` 资源 typed-wire 正在完整测试；SAFE `f7916150` 读取保护与 TASK 日志组合测试正在运行。三项均无新 PR 或 Release，暂不作为已交付。
- **#516 评论**：既有评论 `6555130064` 已于 `2026-11-02T07:52:22Z` 更新并逐字回读匹配，原评论全文保留；issue 仍 OPEN，正文、勾选项和标签未改。回执：`scratch/full-roadmap-20261101/resource-feedback-fix-20261102/516-comment-publish-receipt.json` 与 `516-comment-published-readback.json`。

下方 v0.52 检查点中的 1058、“master pending”和“评论未发送”均保留为历史记录，不再是当前状态。
<!-- current-pointer-v052-pr558-20261102 -->
## 历史检查点（2026-11-02，v0.52 / PR #558 合并时）

当前路线图 v0.52 已随 [PR #558](https://github.com/2746267826/pim-api/pull/558) 正常合并：锁定 head `6f6d1c1181e6b45f02ead5014777c592f0adbaf0` → merge `7922f6e2ce6e8884dd3251c0e3af933912167d14`，时间 `2026-11-02T07:14:13Z`。exact-head CI [41401604874](https://github.com/2746267826/pim-api/actions/runs/41401604874) 的适用检查与 CI Gate 均成功（Gate `07:11:03Z`）；API job 为 6488 passed / 100 skipped / 0 failed / 6588 total（25m23s），原始 job log SHA-256 `A43150532A27581CA1997BC62FC1FE8AF468624A3E457903BF90B2CE1997727E`；MCP、contract-notify、PR release jobs 按规则 skipped。post-merge master CI [41404982876](https://github.com/2746267826/pim-api/actions/runs/41404982876) 于 `07:14:15Z` 开始，当前进行中；暂不推断新 API Release。合并回执为 `scratch/full-roadmap-20261101/api-pr558-delivery-20261102/pr558-merged.json`，原始 API job log 在同目录。

- **API/Web**：[Release `api-v26.11.1058`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1058) 已发布，产品源为 `d4a95882`；PR #558 合并后的 master CI [41404982876](https://github.com/2746267826/pim-api/actions/runs/41404982876) 正在运行，不推断新 API Release。完整 Notes 真实 PG 在锁定源码 `6f6d1c11` 上执行为 402 passed / 0 failed / 2 skipped / 404 total；两个 skip 是需 opt-in 的 URL 专项 PG 用例，不计作通过。日志与 TRX 位于 `scratch/full-roadmap-20261101/resource-feedback-fix-20261102/full-notes-realpg/`。
- **Android/Windows**：Android `android-v26.11.1154` 已正式发布。Windows PR #42 exact source `c0c0e4237ebc795a647ab6c0e651f6cdcae3202e` 的 PR CI/Gate 成功并正常合并为 `526b6755bec78ac4a545dfad69ab531572bab642`；master CI [41403230994](https://github.com/2746267826/pim-windows/actions/runs/41403230994) 对该 merge 的全部 jobs 成功，包括 `07:04:00Z` release job，但 Windows Release/tag/ledger 尚待独立核实，暂不宣称已发布。该 source 的 App 114/114、Core 366/366 均 0 failed / 0 skipped，官方 contract 检查通过；receipt：`scratch/full-roadmap-20261101/windows-integrated-c0c0e423-20261102/verified-receipt.json`。
- **范围与验收**：M0–M5、45 个稳定 ID、AC1–19 与 #516 五阶段范围不变；#516 及相关 issues 仍 OPEN。生产部署、真实付费 provider/模型质量、Windows 资源遥测、物理设备/OEM 与端到端验收仍为 NOT-VERIFIED；CI、合成 PG 与 GitHub Release 不替代这些验收。
- **进度评论**：#516 既有评论 `6555130064` 的更新仅保存在本地草稿 `scratch/full-roadmap-20261101/resource-feedback-fix-20261102/pim-api-516-progress-comment-draft-20261102.md`，未发送。

<!-- current-pointer-resumed-20261102 -->
## 历史检查点（2026-11-02，修复批已推送）

- API/Web 首批 [PR #556](https://github.com/2746267826/pim-api/pull/556) 的首轮完整 CI 为 6440 PASS、42 FAIL、99 skipped，总计 6581，原失败证据保留。Notes 回归 9/9、EF fixture 相关测试 58 PASS / 6 条件跳过 / 0 FAIL；两类根因修复已正常推送到 `6d97c934585968b88ef85862cf8bab91e15b45c1`。新完整 [CI 41365251283](https://github.com/2746267826/pim-api/actions/runs/41365251283) 正在运行，Web 与真实契约已通过，API 完整测试及 Docker 验证仍在执行；尚未合并或发布。
- 首批修复在独立集成树 `wt/notes-pr556-ci-fixes-20261102` 收敛；下一批 Web 手写会话/dirty/冻结和实际解压预算已修，完整 537 项通过；SAFE ZIP 真实 Swagger 已导出并官方生成，材料 HTTP/store/shape 56/56 通过。原始包主动下载、安全删除和跨端多分片恢复继续开发；新开发契约尚不能用于正式客户端 freshness。
- Android 稳定性版本 `android-v26.11.1150`、Windows 首批 `windows-v26.11.1114` 的发布事实不变。Android 第二树 `3d693b8` 完整 App 2188/2188、重点 80/80 通过，完整 XML 先行归档；派生作用域后续补丁正在审查。Windows 继续保全多分片录音与混合块顺序。
- 已恢复六个既有 `group/group` 子代理。用户已确认工作区独享，保留并行修改继续推进；父代理负责审查、集成与交付。M0–M5 和十项 issue 的整体验收仍未完成。

下方检查点均保留为历史。

<!-- current-pointer-integrating-20261101 -->
## 历史检查点（2026-11-01 23:50）

- API/Web 首批 [PR #556](https://github.com/2746267826/pim-api/pull/556) 已推送固定提交 `81c30bb4` 并转为待审核；父 API 189/189、Web 517/517、构建通过。最终 CI 中 Web 与真实契约校验已通过，API 全套测试及镜像 smoke 仍在运行，尚未合并。
- Android 稳定性修复已正式发布 `android-v26.11.1150`，PR64、主线 CI、标签与版本台账均核验；#59–#63 的已有评论均已更新。M1/M2/M5 第二批继续验证，新增接口的测试替身编译阻断已修，Dewey 持唯一 Gradle slot。
- Windows 原生笔记首批已正式发布 `windows-v26.11.1114`，PR41、主线 CI、标签和版本台账均核验；父完整 App 105/105，通过后的材料恢复导入在独立第二树继续。
- Web 手写和 Android 笔记包导入导出两项新提交已保留在第二集成树；继续修笔迹未保存状态和 ZIP 容量边界。资源门反馈、SAFE01 导出/删除、跨端混合材料恢复仍属开发范围，M0–M5 未整体完成。

## 历史检查点（2026-11-01，继续推进）

唯一总路线图的 [v0.49 当前执行层](https://github.com/2746267826/pim-api/blob/codex-win/notes-full-features-20261101/docs/product/native-notes-roadmap.md#v049-current) 已随开发 [PR #556](https://github.com/2746267826/pim-api/pull/556) 推送，尚未合并；最新集成事实同时维护在本地 `wt/notes-full-features-api-20261101/docs/product/native-notes-roadmap.md`。master 仍为下方 v0.48。保留全部 45 个稳定 ID 与旧历史。

- API #539/#551：master CI 和 CI Gate 全绿，已发布 `api-v26.11.1052`，tag/source `def642099` 与实际 Web 版本台账一致；未操作生产部署。#552–554、材料 receipt 和任务建议在 PR #556 继续，官方 EF 迁移已修，父聚合 157/158 通过；唯一失败为撤整篇 DELETE 后的旧测试，正在更新。最终真实契约已重新导出并官方生成，Web 510 项为此前集成回执。
- Android 稳定性 [PR #64](https://github.com/2746267826/pim-android/pull/64) exact head `cefe6ff` 的全部必需检查与 CI Gate 已通过，于 `2026-11-01T14:26:59Z` 正常合并为 `2e7867d7`，主线构建/Release 待核。M1 history/导入恢复与 M5 原生动作已入第二树，App 实际编译遇到 M5 错误，Dewey 持唯一构建 slot 修复。
- Windows 原生笔记、混合材料云访问、本机历史、日历/任务工作台已集成：Core 332/332、实际 WPF/App 101/101 全通过，开发 [PR #41](https://github.com/2746267826/pim-windows/pull/41) 原 head 的 CI Gate 已绿，planner 旧 UI 跨账号隔离补丁完成后再交付。binary 云下载导入和完整原生迁移仍在开发。
- 六个现有 `group/group` 子代理继续开发；父代理审查、集成与交付。下一步先消除实际测试失败，再完成安全/receipt 小包、正式契约刷新、PR/CI/Release 和 issue 回填。未把 M0–M5 或外部验收写成已完成。

下方状态为保留历史。

<!-- current-pointer-resume-20261101 -->
## 历史入口与开发状态（2026-11-01，续推）

唯一完整路线图已更新到 [API master 的 v0.48 当前执行层](https://github.com/2746267826/pim-api/blob/master/docs/product/native-notes-roadmap.md#v048-current)。[PR #555](https://github.com/2746267826/pim-api/pull/555) 已通过适用 PR 检查与 CI Gate，于 `2026-11-01T13:35:30Z` 正常合并为 `def642099aeb5d8057b2a5bc620b05b6f943d0ec`；[master CI 41312005940](https://github.com/2746267826/pim-api/actions/runs/41312005940) 仍在运行，暂不宣称新产品发布。

- **已合并源码**：API #539/#551 同步与覆盖收敛修复、M0-M5 逐项状态和开发波次。测试与 PR 证据见 #555；上线部署和用户环境验收分别保留。
- **正在开发/集成**：API #552-554 持久 ASR 与 Web、材料消费回执、任务建议确认；Android #59-63 稳定性、M1 编辑/交换/恢复和 M5 原生业务；Windows 笔记录音、混合材料云端读写与原生业务。
- **Issue 事实**：#525、#533 已由用户关闭；Android #61/#62 是独立后续。#516、#539、#551-554 与 Android #59-63 不因源码合并或局部测试提前标记整体完成。
- **执行方式**：六个现有 `group/group` 子代理承担开发，父代理负责分派、审查、集成和正常 PR/CI/Release 交付。真实 provider、付费请求、生产部署与物理设备验收没有新增证据。

下方入口与版本信息作为历史快照保留；当前状态以本节及 canonical 路线图为准。

<!-- current-pointer-cover-20261101 -->
## 历史入口与交付状态（2026-11-01）

本轮三仓产品发布及 v0.47 路线图文档收尾已完成；这不代表 #516 全部功能或真实外部验收完成。唯一完整路线图见[pim-api master 的 v0.47 当前状态](https://github.com/2746267826/pim-api/blob/master/docs/product/native-notes-roadmap.md#v047-current)。

- **API/Web**：[PR #549](https://github.com/2746267826/pim-api/pull/549)、[Release `api-v26.11.1047`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1047)。产品实现 merge/tag source 是 `84ef802e`；后续 docs-only merge 为 [PR #550](https://github.com/2746267826/pim-api/pull/550) head `8d32c44e` → master merge `88c11c6b58a496540c8a35ade4d38c5e490913b1`。docs-only master CI [41285420578](https://github.com/2746267826/pim-api/actions/runs/41285420578) 成功；API/Web 产品 Release 仍指向 `84ef802e`，没有新产品 Release。
- **Windows**：[PR #40](https://github.com/2746267826/pim-windows/pull/40) / [Release `windows-v26.11.1111`](https://github.com/2746267826/pim-windows/releases/tag/windows-v26.11.1111)。**Android**：[PR #58](https://github.com/2746267826/pim-android/pull/58) / [Release `android-v26.11.1147`](https://github.com/2746267826/pim-android/releases/tag/android-v26.11.1147)。
- **管理员配置**：登录 Web 工作台 →「设置」→「AI 与 ASR」→「ASR 配置」(`/settings/ai?tab=asr`) →「添加ASR配置」→ 选择「MiMo 多模态 · v1」，配置 API 根地址、模型标识和 API Key；批量推理另配置 MiMo Batch API 根地址并保存。
- **发起任务**：进入「收集」→「笔记处理」→ 选「材料版本」→「启动录音转写」，明确选择执行位置/方式；Batch 提交前单独确认整批范围、现金计费与 24 小时窗口。Windows 本地入口为托盘「PIM 守护程序」→「打开状态中心」→「本地模型」→「ASR 后台处理」。
- **下一步与边界**：在授权的受控环境继续真实 provider/音频/Batch质量与计费、设备/OEM、安装态和 production 验收；#516/#533/#539 的外部验收仍未完成，不能由 CI 或 GitHub Release 替代。

下方 v0.42 与更早内容是保留的历史快照；不是当前状态，也不复制 canonical backlog。
# PIM 原生客户端与笔记路线图

唯一完整正文与后续状态以 [原生笔记路线图 v0.42](pim-api/docs/product/native-notes-roadmap.md#v042-current) 为准；本文件只作本地入口，不复制 backlog、测试台账或历史失败。

v0.42：截至 2026-10-31，API canonical 位于 `48dfe65cc3a8b16fe8f38d7fddb853ee1d6b4db8`。Docs [PR #545](https://github.com/2746267826/pim-api/pull/545) 已从 head `6dde16fce4e460cfb9e8238ab6c05b6e1e874973` 普通 merge 为 merge commit `48dfe65cc3a8b16fe8f38d7fddb853ee1d6b4db8`（2026-10-31T07:14:16Z）；exact PR CI [41179635126](https://github.com/2746267826/pim-api/actions/runs/41179635126) success、`CI Gate` SUCCESS，master push CI [41179807473](https://github.com/2746267826/pim-api/actions/runs/41179807473) success、`CI Gate` SUCCESS，product builds/notify 按规则 skipped，release job success（无新产品 Release）。v0.42 为 #516 追加 MiMo Batch 云端异步 provider 需求与排期（本轮范围仅 `/v1/chat/completions`），仍 `NOT IMPLEMENTED`，音频 Batch 端到端未实证，产品 runtime 全 `NOT-VERIFIED`。

v0.41 历史：截至 2026-10-31，API canonical 位于 `1e0090f524fee38458f5b201ca487a9ca7795023` / v0.41。Docs [PR #544](https://github.com/2746267826/pim-api/pull/544) 已从 head `81245fb08a03cc8e253ab29e8f671e8fca43af08` 普通 merge 为 merge commit `1e0090f524fee38458f5b201ca487a9ca7795023`（2026-10-31T05:50:32Z）；exact PR CI [41173971210](https://github.com/2746267826/pim-api/actions/runs/41173971210) 的 `CI Gate`/适用 docs 检查成功，平台 build/release 按规则 skipped；master CI [41174179911](https://github.com/2746267826/pim-api/actions/runs/41174179911) 整体 success、`CI Gate` SUCCESS，release job SUCCESS，执行 no-components 分支，未创建新 Release。

v0.41 记录用户已确认的两项 #516 产品决策：本地 ASR 节点不可用时继续排队，仅用户显式手动切云端，禁止自动付费 fallback；Windows 节点仅处理当前登录账号任务，禁止跨账号或共享节点。`#516` 仍为 `PLANNED / NOT IMPLEMENTED`，阶段 1 最小负向验收仍 `NOT-VERIFIED`；模型/运行时/硬件/阈值仍是工程前置。当前波次保持先 #539 processing/退避与 Android `not-before` 成套闭环，再进入 #516 阶段 1。

Android 1142 已交付，#533 的 core 稳定 spool 快照/空间守护已随该交付收口；实机/OEM 导出复验与 issue 总验收仍 OPEN。#539 semantic-preserving 修复已随 API PR #542 / Release `api-v26.10.1031` 交付；新 processing/退避协议和 Android consumer/not-before 仍未实现。GitHub Release 不是 production、物理设备、OEM/课堂或完整 MVP 验收；残余边界、稳定性波次和 #516 排期以 v0.42 当前覆盖及其保留历史为准。API #505/#533/#539 OPEN；#525 保持用户关闭为 CLOSED/COMPLETED；#516 五阶段产品未实现。
<!-- current-pointer-v052-master-attempt1-20261102 -->
## 历史检查点（2026-11-02，PR #557 master attempt 1 已完成）

PR #557 exact head `f7da2fe48e546052fccc05c4a44f530962bbd77d` 与 master merge `d4a958822635518c7d147061eaf8c5c07057b2ad` 的 Git tree 均为 `d0ddc8095a528c86451afea14c51e2baf7dfaa83`。PR CI [41371199869](https://github.com/2746267826/pim-api/actions/runs/41371199869) 成功；master CI [41387760422](https://github.com/2746267826/pim-api/actions/runs/41387760422) attempt 1 已完成 FAILURE：API 6484 PASS / 99 skipped / 0 failed / 6583 total，29m10s；唯一失败为 Docker `/health` smoke，CI Gate FAIL、release job SKIPPED。API log `scratch/full-roadmap-20261101/pr557-master-41387760422-20261102/api-job-attempt1.log`，SHA-256 `B73A99F3920F868457B6084740AEEB0206848855A084461BBE575AAD9279A18E`。attempt 2 已排队，父只重跑失败 jobs、不改门禁。最新实际 API Release 仍为 [`api-v26.11.1056`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1056)；`26.11.1058` 仅为构建标签。

- Resource API/Web 受控切片：API source `d618acc6f752f1f6a2f5205c8299c97ff421cb9c` 真 PG 87/87 PASS；实际 Swagger source `a6b4cca326321ebbacf63ab7acf5d942291b262a` 并完成官方 Web types update/check。Web source `34c67e8abcf130650787ee4e45ddac26ad115006`，聚焦 12/12、完整 66 files / 551 tests、build 通过；交付文档 commit `361ac78724190a50b9444dc8d3e180e7b64faf4e`。服务端与 Web 状态面板不包含 Windows 原生资源决策上报，不代表真实硬件/GPU/VRAM 测量或 #516 验收完成。
- Android source `40e972b`：App 2206、Core 74、focused 91 均 0 fail / 0 skip；priority/discard UI 小修仍待接，保留 source-specific 计数，不推断为最终交付。
- #516 保持 OPEN；M0–M5、45 IDs、AC1–19 与五阶段范围不变。真实 provider、生产、Windows 资源遥测及目标硬件/设备验收均未由上述受控测试证明。

下方既有 v0.52/v0.51 检查点保留为历史快照。

<!-- current-pointer-v052-20261102 -->
## 历史检查点（2026-11-02，PR #557 master首次验证失败）

PR #557 exact head `f7da2fe48e546052fccc05c4a44f530962bbd77d` 与 master merge `d4a958822635518c7d147061eaf8c5c07057b2ad` 的 Git tree 均为 `d0ddc8095a528c86451afea14c51e2baf7dfaa83`。PR CI `41371199869` 通过；master CI [41387760422](https://github.com/2746267826/pim-api/actions/runs/41387760422) attempt 1 已完成 FAILURE：API 6484 PASS / 99 skipped / 0 failed / 6583 total（29m10s），唯一失败为 Docker `/health` smoke，导致 CI Gate FAIL、release job SKIPPED。API raw log：`scratch/full-roadmap-20261101/pr557-master-41387760422-20261102/api-job-attempt1.log`，SHA-256 `B73A99F3920F868457B6084740AEEB0206848855A084461BBE575AAD9279A18E`。父计划只重跑 failed jobs，不改门禁；最新实际 API Release 仍为 [api-v26.11.1056](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1056)，1058 只是构建标签。

- Resource API/Web 受控切片：API source `d618acc6f752f1f6a2f5205c8299c97ff421cb9c` 真 PG 87/87 PASS；实际 Swagger source `a6b4cca326321ebbacf63ab7acf5d942291b262a` 并完成官方 Web types update/check。Web source `34c67e8abcf130650787ee4e45ddac26ad115006`，聚焦 12/12、完整 66 files / 551 tests、build 通过；文档追加 commit `361ac78724190a50b9444dc8d3e180e7b64faf4e`。这些不代表 Windows 已上报资源、真实硬件测量或 #516 完成。
- Android source `40e972b`：完整 App 2206、Core 74、0 fail/0 skip；focused 91/0 fail。priority/discard UI 小修仍待接，不把这些 source-specific 结果合并成已交付全绿。
- #516 保持 OPEN。服务端/Web 资源门持久反馈与状态展示不覆盖 Windows 原生资源决策上报，也没有真实 GPU/VRAM/设备验收；外部验收仍 `NOT-VERIFIED`。

下方 v0.51 与旧检查点保留为历史快照，不回写旧事实。

<!-- current-pointer-v052-resource-20261102 -->
## 历史检查点（2026-11-02，Resource PR启动前）

唯一当前路线图为资源交付树 v0.52：`wt/notes-resource-feedback-20261102/docs/product/native-notes-roadmap.md#v052-current`；架构状态见同树 `docs/architecture/asr-dual-channel-plan.md`。canonical双文档提交 `6f6d1c1181e6b45f02ead5014777c592f0adbaf0`，基于已合并 API 源 `d4a958822635518c7d147061eaf8c5c07057b2ad` 追加；Resource代码 follow-up `3a61a2deb0580b6f37d7f407d1ed15dcbcf6b8ef`。父负责审核、push、PR与CI，本树尚未开PR。

- **API/Web**：Release [`api-v26.11.1058`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1058) 已发布，tag/source `d4a95882`；master run `41387760422` 的首次 Docker `/health` 失败保留为历史，failed-jobs-only attempt 2 的 Gate/release通过，API计数复用attempt 1 的 `6484 PASS / 99 skipped / 0 FAIL / 6583 total`。
- **Android**：PR #65 已合并为 `c5614fc80af045b16790fc45ca119b072b504482`；master CI `41396446174` 适用jobs/Gate/release成功。Release [`android-v26.11.1154`](https://github.com/2746267826/pim-android/releases/tag/android-v26.11.1154) 于 `2026-11-02T06:05:57Z` 发布；version ledger 480 B，SHA-256 `936a39fd6e93360e3b40242e755d0b724449cd16b19b088726eff9d2ef620362`。
- **Resource反馈**：commit `3a61a2de` 保留关系型原子更新并支持InMemory HTTP ACK。LocalRoute HTTP 1/1；隔离真实PG资源门/reset 4/4。证据与hash见 `scratch/full-roadmap-20261101/resource-feedback-fix-20261102/`。完整Notes真实PG套件仍待执行；Web/API follow-up 尚无PR/CI，不宣称整个Resource或M0–M5完成。
- **Windows/外部验收**：source `6cf4c141` Core 366 PASS / 0 skipped，WPF仍在验证。production部署、真实provider/模型质量、硬件/GPU、物理设备/OEM及跨端端到端验收仍 `NOT-VERIFIED`。
- **Issue**：父已更新十条既有进度评论并回读MATCH（`2026-11-02T06:13:37Z–06:14:17Z`），全部保持OPEN；#516既有评论 `6555130064` 仅准备追加进度草稿，未发送。

## 历史检查点（2026-11-02，Resource PR启动前副本）

唯一当前路线图为资源交付树 v0.52：`wt/notes-resource-feedback-20261102/docs/product/native-notes-roadmap.md#v052-current`；架构状态见同树 `docs/architecture/asr-dual-channel-plan.md`。canonical双文档提交 `6f6d1c1181e6b45f02ead5014777c592f0adbaf0`，基于已合并 API 源 `d4a958822635518c7d147061eaf8c5c07057b2ad` 追加；Resource代码 follow-up `3a61a2deb0580b6f37d7f407d1ed15dcbcf6b8ef`。父负责审核、push、PR与CI，本树尚未开PR。

- **API/Web**：Release [`api-v26.11.1058`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1058) 已发布，tag/source `d4a95882`；master run `41387760422` 的首次 Docker `/health` 失败保留为历史，failed-jobs-only attempt 2 的 Gate/release通过，API计数复用attempt 1 的 `6484 PASS / 99 skipped / 0 FAIL / 6583 total`。
- **Android**：PR #65 已合并为 `c5614fc80af045b16790fc45ca119b072b504482`；master CI `41396446174` 适用jobs/Gate/release成功。Release [`android-v26.11.1154`](https://github.com/2746267826/pim-android/releases/tag/android-v26.11.1154) 于 `2026-11-02T06:05:57Z` 发布；version ledger 480 B，SHA-256 `936a39fd6e93360e3b40242e755d0b724449cd16b19b088726eff9d2ef620362`。
- **Resource反馈**：commit `3a61a2de` 保留关系型原子更新并支持InMemory HTTP ACK。LocalRoute HTTP 1/1；隔离真实PG资源门/reset 4/4。证据与hash见 `scratch/full-roadmap-20261101/resource-feedback-fix-20261102/`。完整Notes真实PG套件仍待执行；Web/API follow-up 尚无PR/CI，不宣称整个Resource或M0–M5完成。
- **Windows/外部验收**：source `6cf4c141` Core 366 PASS / 0 skipped，WPF仍在验证。production部署、真实provider/模型质量、硬件/GPU、物理设备/OEM及跨端端到端验收仍 `NOT-VERIFIED`。
- **Issue**：父已更新十条既有进度评论并回读MATCH（`2026-11-02T06:13:37Z–06:14:17Z`），全部保持OPEN；#516既有评论 `6555130064` 仅准备追加进度草稿，未发送。

## 历史检查点（2026-11-02，API #557 已发布 v26.11.1058）

PR #557 exact head `f7da2fe48e546052fccc05c4a44f530962bbd77d` 已于 2026-11-02T04:18:53Z 普通 matched-head merge 为 `d4a958822635518c7d147061eaf8c5c07057b2ad`；PR CI 41371199869 Gate SUCCESS。master CI [41387760422](https://github.com/2746267826/pim-api/actions/runs/41387760422) attempt 1 的 API job 为 6484 PASS / 99 skipped / 0 FAIL / 6583 total（29m10s），但唯一 Docker `/health` smoke 失败，CI Gate 首次失败；failed-jobs-only attempt 2 后续成功，Gate 与 release job SUCCESS，API 测试 job 复用 attempt 1。正式 [API Release `api-v26.11.1058`](https://github.com/2746267826/pim-api/releases/tag/api-v26.11.1058) 于 2026-11-02T05:09:47Z 发布，tag/source 解析到 `d4a958822635518c7d147061eaf8c5c07057b2ad`。120 B ledger SHA-256 `5b5b1d7199a63c89bb84b0663755cdea4e9c79970492cefb6102596a746e9e13`，回执 `scratch/full-roadmap-20261101/release-api-v26.11.1058-20261102/verified-receipt.json`。该 Release 不等于生产部署、真实 provider/模型或物理设备验收。

- v0.52 当前覆盖与完整 M0–M5 / 45 IDs / 32 capabilities / 32 gaps / 13 MIG / AC1–19 / #516 五阶段范围见 `wt/notes-resource-feedback-20261102/docs/product/native-notes-roadmap.md#v052-current`；架构状态见同树 `docs/architecture/asr-dual-channel-plan.md`。资源反馈 API 87/87 真实合成 PG、Swagger official check、Web 66 files / 551 tests/build 等边界维持原记录；resource Web 尚待独立 PR/CI，Android/Windows 新合同组合验证与外部验收仍 pending。
- PR #556/#557 的历史状态及先前 1056 发布记账保留在下方；此入口只描述最新 1058 发布，不重写历史。

<!-- current-pointer-v051-20261102 -->
