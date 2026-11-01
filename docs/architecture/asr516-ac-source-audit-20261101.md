# #516 AC1–19 / 五阶段源码审计（2026-11-01）

基线：API/Web 集成源码 `7eeb238c`（本独立补丁从其后含 contract export 的 `aa76c8af` worktree snapshot 派生）。这是源代码覆盖审计，不修改 canonical 路线图/方案、不改变验收状态。Issue #516 保持 OPEN；AC1–19 的最终结果仍是 `NOT-VERIFIED`。存在实现或受控测试不等于真实模型、硬件、provider、设备或 production 验收。

标记：`源码+受控证据` = 对应代码/合成或隔离测试存在；`部分源码` = 有基础但规格闭环/页面/生命周期不全；`代码缺口` = 明确验收能力未见实现；`外部验收` = 本地源码不能证明验收。每行只列主要差距；所列 NOT-VERIFIED 是验收结论，不否认已存在的源码。

## AC1–19

| AC | 当前源覆盖（主要位置） | 仍未满足/需保留的验收边界 | 审计判断 |
| --- | --- | --- | --- |
| 1 | 本地/云端显式路由、冻结 attempt（`NoteAsrService.cs`、`NoteAsrRoutingService.cs`、`note-asr-start-panel.tsx`） | 两种配置组合与真实 Windows 本地执行的最终闭环未独立验收 | 部分源码；NOT-VERIFIED |
| 2 | per-account 默认路由与任务覆盖；云端不自动 fallback（同上） | 双配置默认本地、配置变化负向和真实客户端闭环待验 | 部分源码；NOT-VERIFIED |
| 3 | 已验证模型/启用节点/heartbeat 才可新 claim；owner 隔离、lease/fence（`NoteAsrRoutingService.cs`；`NoteAsrRoutingConcurrencyTests.cs`） | 安装/卸载中等 Windows 模型生命周期及真实离线设备验收 | 源码+受控证据；外部验收 NOT-VERIFIED |
| 4 | 保存资源快照 JSON，Windows 有受控资源/claim 基础 | API 不判 CPU/内存/显存/电源阈值，也没有资源拒绝原因的持久状态闭环 | 代码缺口（Phase 4/Windows owner）；NOT-VERIFIED |
| 5 | 有重试/停止/处理控制基础 | 没有完整的连续失败上限、自动退避与手动绕过计数但仍执行资源门的闭环 | 代码缺口（Phase 4/Windows owner）；NOT-VERIFIED |
| 6 | Windows 有运行控制/资源观察基础 | 真实目标硬件任务前后显存基线与资源释放未证明 | 部分源码；外部验收 NOT-VERIFIED |
| 7 | Windows 生命周期/恢复基础 | 崩溃重启后的真实模型进程、内存/显存清退证据未证明 | 部分源码；外部验收 NOT-VERIFIED |
| 8 | Web 可明确选择 cloud route、冻结 consent、显示目标/错误（`note-asr-start-panel.tsx`） | “笔记界面”触发路径及真实 Test API 页面闭环仍需最终集成验收 | 部分源码；NOT-VERIFIED |
| 9 | MiMo preset、密钥由 profile secret 管理；同步与 Batch 共用配置 | 仍需对实际序列化/日志/产物做完整无密钥验证；不使用真实 key | 源码+合成测试基础；NOT-VERIFIED |
| 10 | 同步/切片与 Batch 源码存在 | Issue 中 8–10 分钟 benchmark 不是本仓证据；无授权真实样本/provider 完整转写验收 | 外部验收；NOT-VERIFIED |
| 11 | content 空时读取 `reasoning_content`（`MimoMultimodalResponseParser.cs`） | provider 实际行为与真实样本仍未验 | 源码+合成测试；外部验收 NOT-VERIFIED |
| 12 | canonical `usage.prompt_tokens_details.audio_tokens` 缺失/0 fail closed，并有 diagnostics UI | 真实中转链路行为仍未验 | 源码+合成测试；外部验收 NOT-VERIFIED |
| 13 | 截断/确定性失败可自适应分片、有序拼接（`NoteAsrAdaptiveTranscription.cs`、`NoteAsrJob.cs`） | 真实长音频完整率、provider 失败可恢复质量仍未验 | 源码+合成测试；外部验收 NOT-VERIFIED |
| 14 | attempt/任务记 route、model/preset 并在详情展示（`NoteAsrAttemptDetailDto`、`note-asr-reprocess-panel.tsx`） | 节点身份/实际执行位置的完整可见性和最终 UI 路径待集成复核 | 部分源码；NOT-VERIFIED |
| 15 | 时间戳解析、模型估算分段；不识别格式时纯文本回退（`MimoTranscriptParser.cs`、`MimoMultimodalResponseParser.cs`） | provider 输出质量/页面最终展示仍待外部验收 | 源码+合成测试；NOT-VERIFIED |
| 16 | 追加式 reprocess 支持本地/专用云端/多模态，需明确原因与云 consent（`NoteAsrReprocessService.cs`、Web reprocess panel） | 三路线的完成态真实 UI/受控服务闭环仍待统一验收 | 源码+受控测试；NOT-VERIFIED |
| 17 | parent-child task/history 追加保存，不覆盖原任务（同上） | 历史分页/选择及并发负向的最终 UI 验收 | 源码+受控测试；NOT-VERIFIED |
| 18 | 固定 source version/hash，重跑写新任务 | 应以原音频/hash 与源任务元数据回读作最终集成验收 | 源码+受控测试；NOT-VERIFIED |
| 19 | 空/重复/截断 diagnostics flags 和红色 Web 提示（`MimoMultimodalResponseParser.cs`、`asr-diagnostics.ts`、`asr-result-insights.tsx`） | 页面接入的所有结果路径及真实 provider 输出仍需验证 | 源码+合成测试；NOT-VERIFIED |

## 五阶段

| 阶段 | 最新源码覆盖 | 主要剩余差距 / 责任边界 | 判断 |
| --- | --- | --- | --- |
| 1 路由、节点、claim/lease | 路由配置、owner-bound node、heartbeat、claim、lease/fence/ACK 与迁移已集成；父记录 aggregate `157/158 PASS, 0 skip`（唯一失败是撤 DELETE 路由后旧测试，已交 Sagan） | 本补丁修正重复 claim 在 heartbeat 过期后无法回读同一活动 lease；完整设备/部署负向矩阵仍需验 | 工程主体有证据；阶段验收未整体 PASS |
| 2 云端多模态/Batch | MiMo preset/parser/usage 防护、adaptive slicing、持久 Batch 状态机与 Web 配置/consumer 存在 | 真实 provider/音频质量、Batch audio/TTL/region/费用均不由 mock 或源码证明 | 源码已实现多项；外部项 NOT-VERIFIED |
| 3 Windows 模型/runtime | Windows 模型管理 UI、登记/transport 基础已交付 | 实际模型安装/卸载/校验及目标硬件验收由 Windows owner（Euler）推进 | 部分源码；生命周期未验 |
| 4 pull、资源门、执行、退避/清退 | pull/claim、Windows runner/control 与资源观察基础存在 | CPU/内存/GPU/电源阈值、退避上限及真实执行后资源回落/崩溃清退未闭环；不在本补丁另行改 Windows owner 范围 | 部分源码；NOT-VERIFIED |
| 5 重跑、异常 UI、全 AC | immutable reprocess/history、诊断 flags/UI 与 task consumers 存在 | 全任务路径消费、最终 UI/refresh 与三平台设备/质量验收由 Dewey/Einstein 等现 owner 负责；本补丁不碰共享 refresh 或页面 | 部分源码；NOT-VERIFIED |

## 独立后端补洞

明确规格：同一 owner、node、idempotency key 的重复 claim 必须幂等；已有活动 lease 时应返回相同 lease/fencing token。旧代码在检查 `NodeClaimable` 后才查 replay，故 heartbeat 刚过期且第一次响应丢失时，重试错误返回 `unavailable`，尽管租约仍有效。`NoteAsrRoutingService.ClaimAsync` 现先按 owner/node/key 查 replay，只对新 claim 检查节点当前可领取性；不改变过期 lease、错误 node/owner 或新 key 的规则。

新增隔离 PostgreSQL 回归覆盖：heartbeat 过期后同 key 仍返回原 lease/token/fence；过期节点的新 key 返回 `unavailable`；另一 node 用同 key 不获得 lease；数据库仍只有一条 lease/receipt 且 fence 不增加。该修复不改变 OpenAPI/DTO。
