# 笔记路线图与验收审计（2026-11-04）

## 结论

本审计只核对文档、源码/提交记录、已有测试回执和 2026-11-04 的远端只读快照，不修改产品代码、CI 或 issue。结论是：M0–M5 均未整体完成；API #516、#539、#551–554 与 Android #59–63 在快照中仍为 OPEN。OPEN 不等于完全没有实现，但也不等于全部验收完成。生产、真实 provider、物理设备/OEM 和 Web 与 Android 之间完整往返均未验证。

本文件替换“Kant 正进行诊断”“更广回归 pending”“PR #561 当前 head 为 `faa6ab30`”等过期叙述。可核对的固定事实是：

- `faa6ab30` 是 PR #561 的初始远端 head；本地 `78c4a17a` 及后续未提交改动不能自动写成该 PR 的当前 head。
- 有界 Notes+Contract 回归已有完成 TRX：438 total / 330 passed / 108 skipped / 0 failed / 0 timeout。108 skipped 不是 PASS。
- 独立风险审查指出 Web 正文块未知扩展可能丢失、clone 未知结果恢复缺 `baseVersionId` 比对；两项均未取得修复后测试。
- Android `d840094` 的自动上传 5/5 和 compile 早于云端 DTO/恢复接入；后续 WIP 未取得组合验证。

## 用户可理解排期

| 顺序 | 用户目标 | 本轮可执行内容 | 完成门槛 |
| --- | --- | --- | --- |
| P0 | 从笔记列表打开一篇已有笔记，安全续写并保存 | 修复/测试 Web 未知材料往返风险；跑 Web 回归与桌面、390px 窄屏流程；Android 接入云端发现、下载和材料还原 | 同一账号可见同一笔记；正文可读写；原件不丢；冲突不覆盖；保存/同步状态明确 |
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

## Issue 审计与下一步

| Issue | 2026-11-04 远端状态 | 可证实内容 / 边界 | 下一步 |
| --- | --- | --- | --- |
| API #516 | OPEN | 五阶段、AC1–19、双通道/资源退避与 MiMo Batch 规划保留；已有 provider/mock/契约等工程切片，不代表真实音频 Batch、成本、region/TTL 或硬件闭环 | 按 Phase 1→2/3→4→5 的顺序验收；真实能力保持 NOT-VERIFIED |
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
