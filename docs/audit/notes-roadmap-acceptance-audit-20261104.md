# 笔记路线图与验收审计（2026-11-04）

## 结论

截至 2026-11-04，本轮可证实 API 材料定向测试 26/26、Web 当前 UX 测试 37/37 与 build 通过、Android 精确 head JVM 2256/2256。Chrome 已进入 React 页面，但综合流程首轮被陈旧 runner 断言拦截，修订 runner 后待重跑；Android connected UI、完整跨端往返、生产/provider/设备验收仍未验证。M0–M5 与所列 issues 均未整体完成；局部实现或测试通过不构成整体完成或 issue 关闭依据。

## 交付状态更新（2026-11-04）

- API：材料定向测试 26/26；Test Swagger HTTP 200；官方 Web contract update/check 通过；Notes+Contract 回归 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout。108 skipped 不计 PASS。
- Web：本地 head `d30076bc`；ASR/Derived 两个 UX 测试文件 37/37 PASS，`npm run build` PASS。构建 warnings 为 `__dirname`、>500KB chunks、`INEFFECTIVE_DYNAMIC_IMPORT`。Chrome 已到 React 页面并看到正文、录音保全提示、“转写录音”；综合流程首轮因 runner 陈旧断言未通过，截图是失败现场。runner 正修订，流程仍未验收；不再称缺 Chromium 或未进入页面。
- Android Draft PR #70 / 精确 head `3f6b1d4`：Android Studio JDK、已安装 SDK、Gradle 8.14 环境下完整 `:app:testDebugUnitTest --no-daemon --stacktrace` 重跑，193 XML / 2256 tests、0 failures / 0 errors / 0 skipped；`:app:compileDebugKotlin` PASS、契约生成物一致。此前汇总曾报告 14 项失败，但当时缺 XML；本次精确 head 重跑未复现，旧失败原因未知，不能判定为既有问题或本次回归。ADB 可读取设备列表但没有连接设备，connected UI 仍 NOT-VERIFIED；阻断原因是没有专用设备，不是缺 SDK/JDK。
- Android JVM 本地重跑的环境、命令、XML 路径、计数和差异说明见 [回执](../../scratch/notes-user-flow-verification-20261104/android-jvm-rerun-receipt-20261104.md)；该记录不是 CI 或 connected-device 证据。
- Android 初始化自动刷新云端列表；用户选定笔记后才下载、校验并安装副本。相同 `noteId` 本地已有副本时当前拒绝覆盖，因此只是自动发现 + 用户选定下载，不是自动双向同步或合并。共同 cloud-material contract（`inputJson`、`manifestJson`、`files` 与生成 DTO）是在线交换边界，不要求本地 DB/editor 相同。
- schema-v1 只对 Markdown/照片/手写基础材料有共同理解。离线 ZIP 不通用：Android 完整包可含录音，Web 导入拒绝 `recordings/`，Web export 不含录音，converter/录音互导未完成。包差异不妨碍在线同步；包导入只用于支持范围内的离线交换、备份恢复或旧格式兼容。
- ASR 凭据仅管理员通过「设置 → AI 与 ASR → ASR 配置」管理；普通用户不显示凭据配置，阅读写作不依赖 ASR 配置。
- Android PR70 freshness CI 已有失败日志：API master `622382f4` 为 419 operations/734 schemas，Android snapshot 为 422/738。依赖是 API 新契约先进入 master；再官方 fetch-contract 刷新 Android snapshot/provenance/generated 并重跑 CI。不得手改或绕过 freshness。
- M0–M5、#516、#539、#551–554、Android #59–63 未整体完成；issues 不关闭。以下原审计结论是本轮早期状态快照，若与本更新冲突，以本节及三份当前状态文档为准。

本审计只核对文档、源码/提交记录、已有测试回执和 2026-11-04 的远端只读快照，不修改产品代码、CI 或 issue。结论是：M0–M5 均未整体完成；API #516、#539、#551–554 与 Android #59–63 在快照中仍为 OPEN。OPEN 不等于完全没有实现，但也不等于全部验收完成。生产、真实 provider、物理设备/OEM 和 Web 与 Android 之间完整往返均未验证。

本文件替换“Kant 正进行诊断”“更广回归 pending”等过期叙述。PR #561 的远端仍是初始 head `faa6ab30`；本地新增提交未推送。可核对的固定事实是：

- `faa6ab30` 是 PR #561 的初始远端 head；本地 `78c4a17a` 及后续未提交改动不能自动写成该 PR 的当前 head。
- 有界 Notes+Contract 回归已有完成 TRX：438 total / 330 passed / 108 skipped / 0 failed / 0 timeout。108 skipped 不是 PASS。
- 独立风险审查曾指出 Web 正文块未知扩展可能丢失、clone 未知结果恢复缺 `baseVersionId` 比对。后续 `f5037e08` 加入材料保全实现与覆盖；当前已有 52/52 原生笔记聚焦测试和 build 通过的汇总证据，但本台账未逐项映射这两种旧风险到具体测试，不能声称两项均已独立验收。浏览器用户流程仍 NOT-VERIFIED。
- Android `d840094` 的自动上传 5/5 和 compile 早于云端 DTO/恢复接入；后续 WIP 未取得组合验证。

## 用户可理解排期

| 顺序 | 用户目标 | 本轮可执行内容 | 完成门槛 |
| --- | --- | --- | --- |
| P0 | 找到笔记→阅读/续写→保存并看版本/冲突→若有录音再转写→整理 | Web 当前 UX 测试/build 通过，Chrome 已入页面但综合 runner 断言过时、修订后待重跑；Android 自动发现后由用户选定下载，同 ID 冲突拒绝覆盖 | 同一账号发现；选择后完整还原；正文可读写；原件不丢；冲突有清楚且安全的处理；状态明确 |
| P1 | 同步稳定、失败后知道怎么办 | 逐条接收 #539/#551–554 与 Android #59–63；先核对已发布实现，再补真实缺口 | 每项原始 AC 有精确源码、测试/现场证据；设备或生产结论单独标记 |
| P2 | 录音能可靠转写和整理 | 按 #516 依赖完成 Phase 1 工程验收，再推进 Phase 2 多模态/批处理、Phase 3 Windows 节点、Phase 4 用户闭环、Phase 5 历史与恢复 | 不以 mock、synthetic PG、provider 代码存在代替真实能力/质量/成本验收 |

## M0–M5 审计

| 阶段 | 当前可证实状态 | 主要剩余工作 | 状态 |
| --- | --- | --- | --- |
| M0 风险与数据边界 | owner、固定版本、consent、部分 SAFE/材料原件保护已有工程基础 | 补原件/删除/未知结果全链验证，确认真实录音与设备后台风险 | 未整体完成；外部验收 NOT-VERIFIED |
| M1 本地笔记 | Android/Windows 已有本机笔记、编辑、录音或手写等基础能力 | 以用户路径接收新建、重开、autosave、崩溃/离线恢复；补设备压力场景 | 未整体完成；目标设备 NOT-VERIFIED |
| M2 云端与多端处理 | 固定版本、分段任务、持久任务、receipt 和部分已发布 issue 修复存在 | 将发现/读取/保存/转写/整理/失败恢复串成用户流程；逐条复核 #539/#551–554 | 未整体完成；PR/最新源码组合 NOT-VERIFIED |
| M3 混合材料 | Web 有材料包/读取基础，Android/Windows 有各自材料能力；API 有材料保全与克隆方向 | 完成云端同笔记发现→材料还原→续写→冲突保全；核对路径/hash/大小/预算；离线包单列 | 未整体完成；真实跨端往返 NOT-VERIFIED |
| M4 智能组织 | 多端保留任务建议、派生处理和结果选择等工程资产 | 核对来源回链、授权、幂等、人工稿保护与用户可见状态 | 未整体完成；真实模型质量/授权效果 NOT-VERIFIED |
| M5 全面迁移 | Android/Windows 已有多个业务域和咨询/历史入口 | 按 MIG 和业务域逐项验收，不以导航入口或已有页面代替迁移 | 未整体完成；生产迁移 NOT-VERIFIED |

## 同步与离线格式边界

在线笔记交换通过共同 cloud-material API contract（`inputJson`、`manifestJson`、`files` 与 generated DTO）；客户端适配各自 DB/editor，故本地格式不同不等于云端不能同步。当前实现是列表自动刷新、用户选定后下载/校验/安装；Android 同 `noteId` 已存在时拒绝覆盖，自动冲突合并未交付。schema-v1 仅有 Markdown、照片、手写基础材料共同理解；离线 ZIP 不完全通用，Android 完整包可含录音，Web import 不接受 `recordings/`，Web export 不含音频。converter 与录音互导未交付。因此不能声称 Web↔Android 完整无损往返已完成。

## Issue 审计与下一步

| Issue | 2026-11-04 远端状态 | 可证实内容 / 边界 | 下一步 |
| --- | --- | --- | --- |
| API #516 | OPEN | 五阶段、AC1–19、双通道/资源退避与 MiMo Batch 规划保留；MiMo Batch 已有工程实现切片，但 Phase 2 用户闭环未完成，真实音频 Batch 能力、质量、费用、region/TTL 与硬件闭环均未验证；代码/mock/契约不等于用户可用 | 按 Phase 1→2/3→4→5 的顺序验收；先闭合 Phase 2 用户闭环，真实能力保持 NOT-VERIFIED |
| API #539 | OPEN | 历史发布包含同步耗时/pending/错误码相关修复；固定 issue 快照仍 OPEN，需逐条对 AC 复核 | 核对长请求上限、pending 收敛、错误码和客户端表现 |
| API #551 | OPEN | 历史发布包含缺口窗口/重复上传修复方向；issue 描述的真实空洞收敛未获本轮现场复核 | 用受控数据验证窗口收敛、重采和重复事件上传 |
| API #552 | OPEN | 历史发布包含 ASR/网关超时相关修复；8 分 28 秒真实音频与部署超时仍 NOT-VERIFIED | 在隔离环境验证长音频及网关/模型超时区分 |
| API #553 | OPEN | 输出 token 上限设置/共享来源修复有历史发布记录，具体实时与 Batch 生效未复核 | 核对设置来源、实际 wire 请求与超限切片 |
| API #554 | OPEN | 失败原因区分有历史修复方向，调用方取消/断开/上游超时/HTTP 错误仍需现场证据 | 验证持久错误分类、诊断字段和前端建议 |
| Android #59 | OPEN | rejected usage 的修复已随历史 Android 发布继承的说法存在，但 18,331 条样本清理/重传结果未本轮复核 | 核对旧数据升级后是否可重传/清理且不丢数据 |
| Android #60 | OPEN | 定位诊断重复入库修复有历史发布背景，重复样本与修复后去重未复验 | 用同一 fix 重复事件验证只入一条且诊断保留 |
| Android #61 | OPEN | 诊断包完整度修复有历史发布背景，超限日志、sync-history、standbyBucket 未复验 | 核对超限保留、历史字段和实际诊断包内容 |
| Android #62 | OPEN | 崩溃堆栈修复有历史发布背景，真实崩溃捕获与无堆栈场景仍 NOT-VERIFIED | 用受控崩溃验证异常类型和 stack trace 可导出 |
| Android #63 | OPEN | 采样档/高速判定修复有历史发布背景，抖动和长时间高采样未复验 | 验证档位滞回、冷却和负载/电量预算 |

issue 关闭条件：原问题可复现、修复代码处于可交付分支/版本、测试或现场证据覆盖原始 AC、没有把上传/CI/合成数据冒充真实设备或生产验收。缺少任一项就保持 OPEN 或写明残余 NOT-VERIFIED。

## 资料边界

- 远端 issue 快照：`analysis/product-audit-20261104/remote/pim-*-issue-*.json`（本审计复制前的原始材料）。
- 回归：`scratch/notes-user-flow-verification-20261104/api-regression/`；其中 438/330/108/0/0 来自已验证 TRX，最终仍以原始 TRX 为准。
- 前端风险审查：`scratch/notes-user-flow-verification-20261104/continuation-review.md`。
- PR 草稿：`scratch/notes-user-flow-verification-20261104/api-pr-body.md`。
- 本次未访问生产数据库、真实 token、付费 provider、物理设备或 OEM；也未修改 workflow 或远端状态。
