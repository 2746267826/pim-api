# PIM 原生客户端与笔记路线图

版本：v0.42 新增 #516 MiMo Batch 云端异步 provider 需求与排期。更新日期：2026-10-31。v0.41 记录两项 #516 产品决策确认；v0.41 及更早正文保持原样。

本文位于 `pim-api/docs/product/native-notes-roadmap.md`，作为跨仓持续维护的唯一总路线图。Android、Windows 与 Web 按批次引用同一份需求，不复制另一套台账；首版范围和视觉规范分别通过文内链接细化。更新状态时标明平台、证据和仍未交付部分，不以聊天记录代替仓内记录。

总跟踪：[pim-api#505](https://github.com/2746267826/pim-api/issues/505)。主题切片 [pim-android#31](https://github.com/2746267826/pim-android/issues/31) 已合并；录音切片：[pim-android#33](https://github.com/2746267826/pim-android/issues/33)；混合文档与基础手写：[pim-android#35](https://github.com/2746267826/pim-android/issues/35)；完整材料版本：[pim-android#37](https://github.com/2746267826/pim-android/issues/37)。总跟踪不能随主题切片或安卓首版完成而提前关闭。

<a id="v042-current"></a>

### v0.42 当前状态：新增 MiMo Batch 云端异步 provider 需求与排期（2026-10-31）

用户要求为 #516 再增加一种 provider：“MiMo 批量推理”。自 v0.42 起确认把 MiMo Batch 作为云端 `cloud-multimodal` 下的独立 provider 与异步 execution mode 选项；不新增对外 route 枚举，旧三路由规划是历史计划语义，不表示其已进入 OpenAPI enum 或已实现。本节只追加需求与排期，不实现 provider，不产生真实付费请求，#516 仍为 `PLANNED / NOT IMPLEMENTED`。

- 排期增量：Phase 1 契约纳入 execution mode、provider handle 与按 item 对账；Phase 2 增加独立 MiMo Batch 垂直切片，依赖 Phase 1 与官方音频 Batch 能力确认，合成 HTTP/JSONL 确定性先行，真实付费调用另有授权；Phase 3/4 无新增依赖；Phase 5 覆盖 Batch history、partial、cancel 与 errors。五阶段与 AC1–19 原编号不变。
- 策略继承：v0.41 两项确认继续有效——local 不可用仅用户手动切 cloud，禁止自动付费 fallback；Windows 仅当前账号。批次选择是用户/管理员的显式云批量授权，管理员默认不追溯既有 attempt，也不因 local offline 自动切 Batch；不改默认成本授权。
- 关键待核：Batch 音频端到端证据、per-file `expire_at` 与 batch `expires_at` 的实际语义、Batch 状态全集/大小写、region 与 base URL/凭据组合、单行与文件行数上限、音频 URL 有效期。未知状态保留 raw，不得当成功或触发重交；未知项只阻塞依赖该能力的 runtime 验收，不全局阻塞 Phase 1。
- 全部产品 runtime 仍 `NOT-VERIFIED`；完整设计、冲突记录与排期见[双通道方案 v0.42 当前覆盖](../architecture/asr-dual-channel-plan.md#v042-latest-status)。原 v0.41 及更早正文、单条 v0.42 history row 与历史保护保持原样。

<a id="v041-current"></a>

### v0.41 当前状态：两项 #516 产品决策已确认（2026-10-31）

用户已明确回复“确认”，以下两项自本轮起为 `CONFIRMED PRODUCT DECISION`，取代 v0.40 及更早快照中的 pending 状态；旧快照仍按历史保留。

1. **本地 ASR 节点不可用：继续排队，仅用户手动切云端。** 不得因节点离线、超时、默认配置或 queued 任务漂移而自动转为付费云调用。手动切换必须沿用已有的显式用户授权、不可变 route 与 attempt 快照。
2. **Windows 节点只处理当前登录账号的任务。** 禁止跨账号处理、共享节点或扩大音频所有者范围。

已确认决策只解除上述两项产品决定，不解除工程前置：目标模型/revision、运行时、硬件能力、资源阈值、退避参数与兼容矩阵仍需在实现前选定并受控验证。当前优先级不变：先完成 #539 processing/退避契约与 Android `not-before` 消费者成套闭环，再进入 #516 阶段 1；独立稳定性工作可并行。

`#516` 仍为 `PLANNED / NOT IMPLEMENTED`。五阶段真实依赖、AC1–19、工程前置和非目标不变；本次不实现协议或产品，也不把路径/租约契约、Windows 本地 ASR 或 MVP 写成已完成。

阶段 1 最小负向验收仍为 `NOT-VERIFIED`：本地节点不可用时不得产生自动云调用；错误 owner 或跨账号 claim 必须被拒绝；route 配置变化不得使排队任务静默转云。确认产品决策不等于实现或测试通过，后续须以受控负向证据验收。

<a id="v040-current"></a>

### v0.40 当前状态：Web/Android/API 发布收口、稳定性补修与 #516 排期

状态：**WEB/ANDROID/API GITHUB RELEASE VERIFIED / NOT PRODUCTION-VERIFIED；#516 PLANNED / NOT IMPLEMENTED**。更新日期：2026-10-31。本节只覆盖当前交付状态和排期，不改写下方 v0.39 及更早历史，也不把计划、受控测试或 GitHub Release 写成产品实现、物理设备或 production PASS。

| 对象 | 当前实际 | 证据边界 |
| --- | --- | --- |
| Web ASR 入口 | Release `api-v26.10.1023` 已核验，配置入口与权限隔离已交付 | GitHub Release 与冻结 preview/合成 API QA 不等于 production、live-backend E2E 或完整 WEB-01 |
| Android 与 [#533](https://github.com/2746267826/pim-api/issues/533) | [PR #56](https://github.com/2746267826/pim-android/pull/56) 已普通 merge 为 `998015d5b466c2d621c01beb3760c286a351037f`；[android-v26.10.1142](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1142) 已发布；fullApp `2034 / 0 fail`、focused `84 PASS` 为受控测试结果；#533 仍 `OPEN` | 物理 OPPO/OEM、真实导出事故复验与 production 仍 `NOT-VERIFIED`；GitHub Release 不等于设备验收 |
| [#539](https://github.com/2746267826/pim-api/issues/539) 与 [PR #542](https://github.com/2746267826/pim-api/pull/542) | #539 仍 `OPEN`；PR #542 已由 head `933a892b` 普通 merge 为 `f0ecf4501a81b6e8c105ca14cc1def844408d762`（2026-10-30T17:19:56Z）；exact PR CI `41090756654` 整体 SUCCESS/Gate/API/Web/Docker | 新的 processing/退避协议尚在补齐契约、HTTP 证据与兼容，未完成交付；Android 旧 `202` 客户端测试已本地 commit `2d64024c41940560c53ff608623fc168d4bd2b73`（clean/no push），core `5` + app `8` 真 HTTP/Room focused 与父解析 PASS；新 consumer/not-before 未实现 |
| API Release | [api-v26.10.1031](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.1031) 于 `2026-10-30T17:55:13Z` published，非 draft/prerelease，target/tag 为 `f0ecf450`；exact master CI `41097882594` 全部适用 job/Gate/release SUCCESS；4 assets 已上传：server `136175696` B（`a14b6ea3…95d39`）、API `17897085` B（`244758f6…6ae8`）、MCP `31365` B（`86c7f128…a0`）、web version `120` B（`27380d9c9a6cd73cc04f93489de35f6305ab768f8b33f626def55a33dec6d504`） | 资产 SHA 为 GitHub metadata 校验，不是本地大包 byte hash；GitHub Release 不等于 production 部署 |
| [#525](https://github.com/2746267826/pim-api/issues/525) | `CLOSED / COMPLETED`，保持用户关闭状态 | 用户 reported 的 4731 入库与不再 500 是生产观测，不是本代理独立 production PASS；不重开、不改写旧 OPEN 历史 |
| [#516](https://github.com/2746267826/pim-api/issues/516) | `OPEN`；五个阶段与 AC1–19 全部仍是计划；两项用户 decision 未答 | #516 产品、Windows 本地 ASR 与完整 MVP 仍未完成交付；本轮只做文档状态与排期，不塞入整套本地 ASR |

当前执行按优先级/稳定性波次组织，不作日历承诺：

1. Web 入口与同步/导出稳定性先收口；Web Release `1023` 已交付，production 另证。
2. 第一稳定性波次中 #533 的 core 稳定 spool 快照/空间守护已随 `1142` 交付；仅实机/OEM 导出复验与 issue 总验收 `OPEN`。#539 semantic-preserving 修复已随 PR #542 普通 merge，master CI 与设备边界另证；两者无跨仓前置。
3. 第二稳定性波次推进 #539 显式 processing/退避契约与 Android 消费者成套闭环；该协议增强仍未交付。
4. 随后进入 #516 阶段 1：路由、节点与 claim/lease 契约骨架。

波次只表示默认优先级，不是全串行硬门。真实依赖以[#516 五阶段与 AC 映射](../architecture/asr-dual-channel-plan.md#9-ac119-阶段映射)为准：阶段 1 完成后，阶段 2 云端多模态与阶段 3 Windows 模型管理可并行；阶段 4 依赖阶段 1、3；阶段 5 依赖阶段 2、4 及路由/租约可审计。

待用户决定仍为两项，未改成已批准：本地不可用时是否自动切云端（付费），以及 Windows 节点是否可处理其他账号音频。安全默认候选仍是继续排队仅手动切云端、节点仅当前账号；未答复前不预设通过。OPPO/production 证据缺口只保持 `NOT-VERIFIED`，不作为无限期阻塞所有独立笔记规划的硬门。

v0.40 只追加当前状态覆盖；v0.39 及更早正文、45 个稳定 requirement ID、32 项 capability、32 行差距、13 个 MIG 和 186 条保护事实全部原样保留。

<a id="v039-current"></a>

### v0.39 当前状态：Web ASR 入口 GitHub 发布、Issue 收口与双通道排期

状态：**WEB ASR 入口 GITHUB RELEASE VERIFIED / NOT PRODUCTION-VERIFIED；#516 产品未实现**。更新日期：2026-10-30。本节覆盖 Web 入口交付、当前 issue 状态和下一批排期；GitHub Release 不是 production 部署，也不表示 #516 对应源码、Windows 实现或完整 MVP 已完成。下方 v0.38 及更早正文是历史记录，原样保留，不因本节重写；尤其旧文中的 #525 OPEN 只属于当时快照。

v0.39 完成 #516 的设计与依赖排期，不授权把整套 Windows 本地 ASR 实现塞入本轮；详细路由、lease、资源清退、云端多模态、重跑和 [AC1–19 映射](../architecture/asr-dual-channel-plan.md#9-ac119-阶段映射)见[ASR 双通道增量方案](../architecture/asr-dual-channel-plan.md)。

**Issue 与在建入口快照**

| 对象 | 当前状态 | 证据边界 |
| --- | --- | --- |
| API [#525](https://github.com/2746267826/pim-api/issues/525) | `CLOSED / COMPLETED`，关闭于 `2026-10-30T13:11:56Z` | 用户评论 `6523656382` 报告 server 997 + App 1140 大批积压已正常入库 4731、不再 500、重传风暴未再出现。这是用户 reported 生产观测，不是独立 production PASS；不重开，不把旧 v0.38 及更早的 OPEN 历史改写成当前状态 |
| Android [#533](https://github.com/2746267826/pim-api/issues/533) | `OPEN`；body 已收窄为 App 1140 状态中心导出转圈/闪退，仍无 logcat；当前 fullApp `2027 / 0 fail`，作者正补修并复测 | 只读复审发现分页并发 cleanup 可能漏行、空间预算存在退步，故 `2027/0` 不能作为新修复最终 PASS。旧 `snapshotFiles` 源码引用未反映 1140 有界日志；分页/流式最终结论与物理设备证据仍未完成 |
| API [#539](https://github.com/2746267826/pim-api/issues/539) | `OPEN`；body 于 `2026-10-30T13:11:30Z` 增加残余队列/重试/时延报告；性能与 ID 保持此前 `5/5 green`，正在补修 | 正常 in-flight 协议尚未修改，用户线上尾批仍未验证。复审又发现 `day_overflow` 派生 flag 残留、较早 late 事件漏读及同设备并发边界，不能做最终 PASS。隔离合成 PG 已复现 5006 rows 回扫，但不等于现网唯一根因；不写已修复、不关闭 |
| Web ASR 配置入口 | `DELIVERED / GITHUB RELEASE VERIFIED / NOT PRODUCTION-VERIFIED`；[PR #540](https://github.com/2746267826/pim-api/pull/540) 已于 `2026-10-30T14:29:51Z` 普通 merge 为 `0e6d9f4adcf283138d68405d9ca65b04b7c51793` | exact CI `41069510853` Gate/Web/Docker SUCCESS；master CI `41072022034` SUCCESS/Gate/release，Release `api-v26.10.1023` 于 `14:38:55Z` 发布、4 assets 尺寸/SHA 已核，`pim-web-version` 为 26.10.1023/git`0e6d9f4`。管理员 ASR 配置入口与普通用户权限隔离经冻结 preview/合成 API QA 6/6 验证，未改变权限模型；不是 live-backend E2E 或 production |
| API [#516](https://github.com/2746267826/pim-api/issues/516) | `OPEN`，`feature`；issue 正文 `updatedAt=2026-10-30T13:36:30Z`，本轮鲜读时 0 comments；body SHA-256 `720e789e88af5241f7f3975666e569f9cb29b0873ba76be1b6fefaefe3a20ef9` | v0.39 完成设计与排期，#516 产品仍未实现；评论数仅代表本轮鲜读观察，后续评论不使本轮正文失真。Issue body 中“目前没有转写”是过时背景，当前已有专用/分段 ASR，不能照抄抹去已交付事实 |
| 总跟踪 [#505](https://github.com/2746267826/pim-api/issues/505) | `OPEN`；本轮写入前鲜读的保护基线 body SHA-256 `391b2dca476ba0fee3d4e3e03dcc5b56a74cdc0a35972030160297bb84a7186f`，17 checkbox 为 11 checked / 6 unchecked | 该 SHA 是写前保护基线而非“当前 body”自引用；仅保存元数据摘要，不复制正文/私有日志。后续插入覆盖层前必须重新鲜读。写入后校验应移除新版 marker 区块，并将剩余 body 与该保护基线逐字节比较，而不是要求原 body 在插入后仍整体连续出现 |

当前执行按优先级/稳定性波次组织，不是一条全串行链，也不作日历承诺：

1. Web ASR 配置入口已完成 GitHub Release 交付收口；production 仍另证。
2. 第一稳定性波次：#533 core 安全修/受控设备 QA 与 #539 semantic fix 并行；两者无跨仓前置，可独立推进和验收。
3. 第二稳定性波次：#539 processing/退避契约与 Android 消费者成套闭环；该协议增强尚未实现。
4. 随后进入 #516 阶段 1 路由、节点与 claim/lease 契约骨架。

波次表示默认优先级，不等于前一波所有项目都必须完成才能启动后一波；某条独立工作满足自身前置后即可推进。真实依赖只按本节和 #516 五阶段表中列出的前置判定。物理 OPPO 或 production 未验收时保留 `NOT-VERIFIED` 边界，但不把它写成无限期阻塞所有笔记规划或独立工作的硬门。

**#516 的实际增量**

#516 要求服务端配置本地与云端两组供体、云端区分专用 ASR 与多模态；Windows 提供模型板块、下载/安装/卸载和资源展示；服务端保留任务权威，Windows 通过心跳/轮询主动领取，服务端不反向连接客户端；领取前检查资源并按原因退避，连续失败停止自动、手动仍复查；成功/失败/取消/退出/崩溃重启均卸载模型并释放显存/内存；任务详情显示实际通道、云端类型、节点、耗时和失败原因。另要求独立 MiMo 多模态预设、音频未送达防护、截断切片拼接、模型估算时间戳、完成态跨通道重跑、旧结果可回溯、原音频/元数据不变及空文本/循环/截断异常提示。

该增量的前置并非“从零做转写”。现有可复用基础包括 `IAiAsrPreset`/MiMo 专用 ASR、固定材料持久分段任务与样本覆盖、Hangfire 检查点、owner/destinations/固定目标与同意、Android/Web ASR 消费者基础，以及 AI-04 的不可变候选/head/CAS/重生成保护。server 仍是编排权威，本地模型只由已登记节点执行；服务端本机加载模型、实时流式、多客户端负载均衡/任务分片、macOS/Linux、训练微调、多模态摘要/问答/说话人分析及多模态本地部署均为非目标。

**五阶段最小排期**

阶段编号用于表达本表列出的真实前置，不是具体日历承诺；每阶段按自身退出条件独立验收，后续阶段证据不能替代尚未满足的本阶段前置或验收缺口。

| 阶段 | 交付范围 | 关键依赖 | 最小验收/退出条件 | 当前状态 |
| --- | --- | --- | --- | --- |
| 1. 路由与 claim/lease 契约骨架 | local/cloud-asr/cloud-multimodal 路由、配置/尝试快照、节点 capability heartbeat、claim/lease/fencing/idempotency；不执行模型 | 当前 #505 保护基线；#520/#528/#532 已有固定输入/授权/唯一契约；两项用户 decision 以显式参数处理 | 隔离 Test API 证明单/双配置路由快照、重复/过期 claim、错误 owner、旧回执与配置变化负向；不发送真实音频 | `PLANNED / NOT IMPLEMENTED` |
| 2. 云端多模态垂直切片 | 独立 MiMo 多模态预设、无密钥预设、模板/结果解析、`reasoning_content` 回退、`audio_tokens` 防护、`finish_reason=length` 切片有序拼接 | 阶段 1 attempt/路由快照；Web 入口用于配置 UI 验收；真实模型质量需另获付费授权 | 合成 SDK/HTTP 与隔离 API 覆盖 AC9–13、15、19 的确定性部分；8–10 分钟完整转写未获授权前 `NOT-VERIFIED`，Issue benchmark 不当本仓 PASS | `PLANNED / NOT IMPLEMENTED` |
| 3. Windows 模型板块与节点登记 | 模型/资源需求/本机资源、下载安装卸载、摘要校验、节点心跳与已装模型登记；本阶段不领取任务 | 阶段 1 capability 契约；首个模型/推理运行时/目标硬件/阈值选定；Windows 原生宿主与唯一契约生成 | 安装、重启恢复、卸载、损坏摘要、权限不足、无卡/显存不足均可见；仅校验完成模型可登记，未安装/离线/卸载中不可执行 | `PLANNED / NOT IMPLEMENTED` |
| 4. 拉取、资源退避、本地执行与清退 | Windows 主动 claim、资源门槛、结构化退避、自动上限、手动触发；本机推理子进程；成功/失败/取消/退出/崩溃后清理 | 阶段 1、3；两项用户 decision；目标硬件；owner/节点范围 | 受控 Windows + Test API 覆盖 AC3–7；资源原因可见；显存/内存回任务前基线且重启无残留；无真实硬件证据不写 PASS | `PLANNED / NOT IMPLEMENTED` |
| 5. 重跑、异常与全链验收 | 笔记页强制云端、完成态跨通道重跑、不可变历史、状态/错误展示、异常标红、AC1–19 全矩阵 | 阶段 2、4；Web ASR 配置入口；阶段 1 路由/lease；AI-04 版本保护模式 | 三通道重跑、旧结果回读、原音频/元数据不变、异常 UI；AC1–19 逐项真实证据，缺付费/硬件证据保持 `NOT-VERIFIED` | `PLANNED / NOT IMPLEMENTED` |

#516 阶段内部依赖保持：阶段 1 完成后，阶段 2 云端多模态与阶段 3 Windows 模型管理可并行推进；阶段 4 依赖阶段 3 的真实模型生命周期；阶段 5 依赖阶段 2、阶段 4 以及路由/租约可审计。Cloud 多模态的 8–10 分钟与真实 CER/质量、本地 AC6/7 的显存清理均不能以合成 CI 或文档代替。物理 OPPO 或 production 未验收只把相应结果保持为 `NOT-VERIFIED`，不构成所有独立笔记工作的全局硬阻塞。

**待用户决定与阻塞**

1. `PENDING USER DECISION`：本地不可用时是否自动切云端（付费）。安全默认候选是继续排队，仅用户手动切云端；未答复前不得实现自动付费兜底或写成已批准。
2. `PENDING USER DECISION`：Windows 节点是否可处理其他账号音频。安全默认候选是仅当前登录账号；未答复前不得扩大为共享节点。

两项决定不阻止 Web ASR 入口、#533/#539 或本提案的契约骨架等其它已授权工作；只阻塞阶段 4 最终领取规则和 AC1–4、8 的最终产品口径。另有工程前置：首个 Windows 本地模型/推理运行时/目标硬件/资源阈值必须选定，不能用猜测模型或阈值进入阶段 3/4。

**状态与历史保护**

- #516 全部阶段与 AC1–19 仍是 `PLANNED / NOT IMPLEMENTED / NOT-VERIFIED`；不写未来 PR、tag、CI run、merge SHA、发布日期或 production 结论。
- 现有专用/分段 ASR 与 API/Web/Android 既有交付保持有效；v0.39 只增加当前状态覆盖，不把旧背景当作当前事实，也不把用户 reported production 观测标为独立 production PASS。
- 保留 v0.38 及更早整段 Git 内容、45 个稳定 requirement ID、32 个 capability ID、32 行差距、13 个 MIG、111 条早期历史事实和 v0.38 后的历史，总计至少 186 条保护事实；不重定原需求文案，不在既有清单之外新增 MIG/TASK 编号或替代编号。
- v0.39 已完成文档设计与排期交付；排期不等于产品实现，不预写后续 PR、merge、CI、Release 或 production 结论。

<a id="v038-current"></a>

### v0.38 当前状态：移动同步半开窗口与诊断导出完成受控修复，物理与完整系统验收仍开放

状态：**DELIVERED / GITHUB RELEASE VERIFIED / NOT PRODUCTION-VERIFIED**。Android PR #55 与 API tests PR #537 已普通 merge；master CI、`api-v26.10.1019` 和 `android-v26.10.1140` 的 tag/target/assets 已只读核验。本节记录源码、JVM、隔离 API、原生 Android 模拟器组件和 QA 证据；GitHub Release 不是 production 部署、物理手机、旧 APK 升级或完整 MVP 验收。下方 v0.37 及更早正文、45 个稳定需求编号、32 个 capability ID、32 行差距、13 个 MIG、111 条历史事实和 17 行 checkbox 原样保留。详细语义与残余边界见[移动同步与诊断导出说明](../architecture/mobile-sync-followup.md)。

| 层级 | 当前可核事实 | 不能据此宣布 |
| --- | --- | --- |
| API tests / 窗口边界 | [PR #537](https://github.com/2746267826/pim-api/pull/537) exact head [`83b6e3191ecbab14c16fbf35d7b064eea2d3a3cf`](https://github.com/2746267826/pim-api/commit/83b6e3191ecbab14c16fbf35d7b064eea2d3a3cf) 已普通 MERGED 为 [`9631437d42fb63ff66af8e3a3dbcf6fe894ded6e`](https://github.com/2746267826/pim-api/commit/9631437d42fb63ff66af8e3a3dbcf6fe894ded6e)（2026-10-30T11:56:46Z）；exact [CI 41048454669](https://github.com/2746267826/pim-api/actions/runs/41048454669) 全部 required/Gate SUCCESS，MCP、PR notify、PR release 合法 SKIPPED。新增 half-open HTTP 3/3 是 WebFactory + InMemory 测试：旧闭式 `event at end` 400、`end+1ms` 200、counts/rows 回读、同批次无重复；relation/regression focused 为 77 PASS / 8 SKIP；隔离 PostgreSQL 8/8 是复用 #529 既有 relation/regression 在新 cluster 的独立结果，不是 half-open HTTP 在 PG 上运行，也不拼成真实外部后端 E2E。77/8、HTTP3、PG8 分账，不重复加总。master [CI 41051709488](https://github.com/2746267826/pim-api/actions/runs/41051709488) @`9631437d` completed/SUCCESS，Gate 与 release job SUCCESS，MCP/contract notify 按规则 SKIP | exact PR CI/master CI 不等于 production 或旧 APK 升级；不得把三组测试拼成完整外部后端 E2E |
| API Release | [api-v26.10.1019](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.1019) published 2026-10-30T12:26:48Z；tag 为 commit object 并精确解析到 `9631437d`。4 个 uploaded assets：server `136174629` bytes SHA-256 `45fef565...536e9c4`；API `17894197` bytes SHA-256 `e25821bf...c468c341a6`；MCP `31365` bytes SHA-256 `86c7f128...e0059a04ca0`；Web version JSON `120` bytes SHA-256 `dfd621e0...404d2ad8` | GitHub Release 资产不是 production 部署、服务健康或真实后端 E2E 证据 |
| Android source / PR | 产品修复 commit [`dd90be250b72979e31c4f1d1a25df970b4afd7bf`](https://github.com/2746267826/pim-android/commit/dd90be250b72979e31c4f1d1a25df970b4afd7bf) 为 11-path；最终 test-only/debug-entrypoint commit [`5c4d898555b526d75988ef70c54963874d50f9c3`](https://github.com/2746267826/pim-android/commit/5c4d898555b526d75988ef70c54963874d50f9c3) parent 为 `dd90`。[PR #55](https://github.com/2746267826/pim-android/pull/55) 已 READY 并普通 MERGED 为 [`2968eea4fbe701ff9214f8797c43820c77e3de62`](https://github.com/2746267826/pim-android/commit/2968eea4fbe701ff9214f8797c43820c77e3de62)（2026-10-30T12:19:41Z）；exact [CI 41052159717](https://github.com/2746267826/pim-android/actions/runs/41052159717) required/Gate SUCCESS，PR release SKIPPED；master [CI 41054503798](https://github.com/2746267826/pim-android/actions/runs/41054503798) @`2968eea4` completed/SUCCESS，Gate 与 release job SUCCESS | exact PR CI 与 merge 不等于 production、物理手机、旧 APK 升级、完整 WorkManager/Status 系统旅程或完整 MVP |
| Android Release | [android-v26.10.1140](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1140) published 2026-10-30T12:31:56Z；Release API target 为 `master`，不可变 tag 精确解析到 `2968eea4`，与已验证 master head 相同。3 个 uploaded assets：APK `14490824` bytes SHA-256 `c4e7f82a...57cf6829`；shell `2550430` bytes SHA-256 `ccbb0623...cb46863c`；version.json `480` bytes SHA-256 `ab675ab0...d6adf475` | Release asset 未安装到物理旧版上执行升级，也不证明 OEM、长课堂、production 或完整 MVP |
| Android JVM / component QA | full App `2023/2023`、Core `69/69`，均 0 failures/errors/skips。window targeted `7/7`、sync suite `193/193`、export focused `133/133`；focused 与 full 有重叠，不重复求和。最终 6 个**原生 Android 模拟器组件实测** case 全部 PASS：4 个 sync `9.496s`（Room v3→v4、DAO、Coordinator、真实 bound client + 合成 MockWebServer 模拟器回环 HTTP、cancel/owner/重放）与 2 个 export `1.583s`（真实 Repository 写有效 isolated ZIP、脱敏、partial、FileProvider chooser cancel、IO failure）。App APK `7B1DA6...`、Test APK `08C56...`、cert `b18...`；首轮 harness APK `74BE/706` 的 sync `0/4` EntryPoint `ClassCastException` 原样保留 | 模拟器组件 PASS 不是物理手机、WebFactory API、完整 WorkManager/Status 按钮、旧 APK 物理升级、OEM 或原手机 flash 修复 |
| 诊断导出边界 | `mobile-*.jsonl` 日志路径使用 bounded/streaming；`coreEntries` 的 dropped-location、forensics、24h locations 仍走既有 DAO 全量 `List` + `joinToString`。partial manifest/UI 有受控证据；不截断 AC 9.3 数据 | 日志 bounds 不等于诊断整包任意大小内存有界；极大 DB 表、全量 core section 与原始物理事故仍未证 |
| 源码 lineage | `qa/lineage-correction.json` 为 append-only：未单独 snapshot 精确 runtime HEAD；第二次 debug build 19:57:35 晚于 `dd90` 19:52:41，最终 test commit 20:00:15。源码 blob/hash、APK hash、raw instrumentation logs 与最终 commit 同源匹配；READY2/3 原文保留 | 不得虚称“精确 runtime HEAD 已运行”；同源匹配不等于旧 APK 实际升级 |

**v0.38 未验边界**

- API master CI 与 `api-v26.10.1019`、Android master CI 与 `android-v26.10.1140` 已核验；两者只是代码/CI/GitHub Release 交付，不是 production 部署或真实系统验收。
- 旧 APK/旧数据跨版本升级、旧批次 ID 连续性仍 `NOT-VERIFIED`；真实 Room 3→4 组件 case 不等同于旧 APK 实际升级。
- 完整 `TodayStatusWorkManager` 系统编排、真实 Status 导出按钮、二次 auth/退避/不重发的完整系统旅程仍未验。
- 极大 DB `coreEntries` 分页/无损导出、原物理手机 flash/crash 根因（无 stack/logcat）仍未证。
- OPPO/OEM、真实课堂、强杀掉电、production 和完整 MVP 未完成。
- 原生 Android 模拟器已 nameguard 关闭：5556 无残留、无 5554、adb 空、未 wipe/uninstall；测试 `finally` 恢复原测试会话/prefs，且只使用 synthetic/no private credentials。不能表述为测试从未写 prefs。PG 55439 先前已 guard stop。导出 ZIP 在 test `finally` 清理，raw log 保留但不以伪造 ZIP artifact 替代。

**下一批优先顺序（计划，未执行；不新增 ID/backlog）**

1. `P0-1` 隔离验证旧 APK→当前 APK 升级，使用合成旧队列覆盖 batch ID 连续性、owner、ack/retry、完整 Worker/Status 读回；不操作 production pending。
2. `P0-2` 为 core DB 诊断 section 增加无损分页/流式，并验证真实 Status 导出按钮、取消与 IO failure；不得以截断 AC 9.3 数据掩盖内存风险。
3. `P1` 再验证物理 OPPO/OEM、100–180 分钟真实课堂与逐图 receipt，随后按既有路线推进 Windows/TASK/MIG；任何付费模型调用须另行授权。

<a id="v037-current"></a>

### v0.37 当前状态：Android 切片 PR/CI/merge/Release 已交付，四 cohort 在真实 Test API + 原生 Android 模拟器分轮 PASS

状态：**API/Web 的既有交付不变；Android 切片已正常交付。[PR #54](https://github.com/2746267826/pim-android/pull/54) exact head [`a8507365ba35b4cc1c8af0efa67a1bd5e8b7af2e`](https://github.com/2746267826/pim-android/commit/a8507365ba35b4cc1c8af0efa67a1bd5e8b7af2e) 已普通 MERGED 为 [`3844ef62409765be0d5d229d8d3955cc31fcb261`](https://github.com/2746267826/pim-android/commit/3844ef62409765be0d5d229d8d3955cc31fcb261)，merged 2026-10-30T10:21:27Z；main tree 保持 `201a4f701734ad93b2da3f2d10cab0fe2fa9cc67`。exact [PR CI 41038364205](https://github.com/2746267826/pim-android/actions/runs/41038364205) 所有 required/CI Gate SUCCESS，PR 事件 release 按规则 SKIPPED；[master CI 41040144538](https://github.com/2746267826/pim-android/actions/runs/41040144538) @merge 整体 SUCCESS（Gate 10:32:53Z、release 10:33:03Z）。Release [android-v26.10.1137](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1137) published 10:33:00Z、non-draft/non-prerelease；target 字段为 master，但 tag 实际解析 merge `3844ef62`。assets：APK 14,477,900 bytes SHA-256 `297b029a70f34a2355e8fa06002656057cdf06eaf504f82a7e8111ae1f4ec89e`；shell 2,550,430 bytes SHA-256 `fa3d2a4c3b2ca0882722ec1bbb05d56cb9f6226e6857867189a1e2b81e126ebd`；version.json 480 bytes SHA-256 `8c1df66c443ebc228c3f77387bf70d8b6b8905317cb5c2c57faf9022b9150ecf`。四 cohort 仍为真实 Test API + owned Android AVD 模拟器（5556/API36.1）分轮 PASS：normal9、conflict6、unknown6、narrow9；均为 App `D197CE5B...890E`、debug 兼容重签，narrow 匹配 dirty `FACCE` blob而构建早于 a850 commit。四条链 Test APK/source 不同，不能拼成同一 Test APK 的 4/4。GitHub Release 不是 production、Release APK 物理设备验证或完整视觉验收；OPPO/OEM/100–180 课堂/真实模型质量/公开逐图 receipt/完整 MVP 仍 `NOT-VERIFIED`。旧全部 FAIL、签名安装与 runner evidence pull 历史故障保留并与本次成功交付分账**。

| 层级 | 当前可核事实 | 不能据此宣布 |
| --- | --- | --- |
| Android fix chain | `d37db9cd` 单确认只建一 task；`54f6ace2` 按固定材料隔离 refresh/regen 并保留 legacy 缺 `kinds` 原 TaskId；`080f5c2` 分离 viewed/pending/confirmed head，head 为 null 时仍可由用户合法选择，并补 manual-save guard；`af24e2f` 阻止 A 稿使用 B confirmed head 及旧账号 callback 在联网前继续；`afab38ee` 持久化 detail 回执并发布当前 task，修复 server Succeeded/UI Running；`f8fc4c0d` 增加两行 E2E 真实 UI refresh；`6afb6438` 2 处 test 修正（4 additions / 4 deletions）；`6dc70a0d` 最后 E2E 14 additions / 4 deletions 增加 coverage 异步 UI 等待与 narrow destination 滚动顺序；`a8507365` 将 narrow dirty `FACCE` blob 正常 test commit | test-only commit 不改产品 tree/App；6dc 构建与 a850 commit 分账；提交链不是同一 Test APK 4/4、merge 或 Release |
| 当前 source / PR / Release | head `a8507365` → merge `3844ef62`（10:21:27Z），main tree `201a4f...`；exact PR CI 41038364205 全 required/Gate SUCCESS、PR release skipped；master CI 41040144538 SUCCESS；Release `android-v26.10.1137` target merge `3844ef62`，3 assets SHA/尺寸已核 | PR CI、master CI 或 GitHub Release 不替代 production、物理设备、OEM/课堂或完整视觉验收 |
| 本地回归 | 最新 full App `2008/2008`、Repository targeted `11/11`、Core `69/69`，均 fail 0 / error 0 / skip 0；Core 源未变并沿 fresh 结果，统计不求和。历史 `2006/2004/1999/1997/1985+33 FAIL` 继续保留 | JVM 全绿不替代真实设备；不同 focused/full 计数不得相加 |
| 原生模拟器前段 | `af24e2f` 在 owned Android AVD（5556/API36.1）+ 真实 Test API 的 normal cohort 已完成一次 confirm 一 task、`CompleteRecord` + `IndependentAnalysis` 双产物、Photo/Ink actual bytes、普通 USER head `2ac85107-e738-43bd-a41f-91698e0b356b`、provider ASR3 + vision1 + text2 = 6；未重复 ASR，Disabled 断言后无额外 provider 调用 | 这不是 normal 整体 PASS，也不是 OPPO/物理真机验证；不得把前段通过写成 regen 或真机通过 |
| normalphone afab PASS9 | `afab38ee` + test source `59A36F...` + Test APK `D3B5A646...E2D`。run `164238` instrumentation OK 1 test、recover case-result PASS；initial `23d68e06...`/regen `41247e61...` 各一次 TaskId；USER head `6fafd9ed...`，regen version `8e902c5f...` nonhead parent USER head；ASR3 + vision2 + text4 = 9 全 success、无重 ASR；Photo120B`f40dea...`/Ink956B`79d099...`。该 PASS 对应 App `D197CE5B...890E` | normal 单链 PASS；不等于同一 Test APK 4/4、完整设备矩阵或真实课堂 PASS |
| f8 conflict-phone PASS | `f8fc4c0` + test source `7AA5DA...` + same App `D197CE5B...890E`，兼容 test APK `B76E37DA37322EAAED771080605F8E21D59E6DDE1D7381057C9AC8C70DB74EF0`；raw `F286...` 仅 META-INF 兼容重签、91 payload 相同。instrument OK 1 test 39.628s；second writer head `a4a02ee1...`，local conflict `62ebb80b...` 为 hasConflict/non-head；双方完整文本与本机 draft 保留；ASR3+vision1+text2=6，冲突额外 model 0 | conflict PASS 已真实闭合；初始 runner Windows 反斜杠 pull 失败保留，同 case 恢复 PASS，不是产品失败。不得与 normal 的旧 test source `59A` 拼成同一 test APK 的四项 PASS |
| 采集失败与签名 lineage | 原外层 FAIL 为 runner evidence pull 单文件 “Not a directory”，保留；recover 证据确认 PASS，无模型重跑。raw afab `E260.../9FB...` 与 owned 5556 cert 不兼容安装 FAIL 保留；同编译 APK 仅兼容重签 App `D197CE5B...890E`、Test `D3B5A646...E2D`，旧 source `59A36F...`、product tree `201a4f...`；f8 新 E2E source `7AA5DA...` | f8 只含 E2E 两行 refresh，产品 tree/App 不变；不得把 f8 test source 写成产品 rebuild，不得说所有轮次 PASS |
| 独立证据 lineage | 独立核验的 ZIP 非签名 payload 与安装 APK 同 SHA：App 478/478、Test 91/91；device manifest 与 run actual source/product tree 闭合，无新 source | 独立 lineage 确认不等于 unknown/narrow 或完整 MVP 通过 |
| 前序 head 选择 P1 | `54f6ace2` 的 testfix-select 在 owned Android AVD 原生模拟器实测中暴露 API 单 Model `isHead=false` / `head=null`，但 UI 将同 candidate 标为“当前选用”且 Disabled；manual-save 409 是该状态结果。后续 `080f5c2`/`af24e2f` 已补显式选择与 stale-source/binding guard | 旧 FAIL 历史不改写；新 source 仍需重新执行真实选择、保存与 USER-head 断言 |
| 当前验收状态 | Android 切片的 PR merge、master CI、Release 与四 cohort 分轮 PASS 均已完成；四 cohort matrix 独立复核，App/main tree 同源，Test APK/source 分账 | 仍不能写同一 Test APK 4/4、OPPO/OEM/课堂/完整视觉、production 或完整 MVP PASS |
| unknown final PASS | `6dc70a0d` + test source `45E3A6A8...` + Test APK `712788C98973FC981F816C1F921EB90F7AC8B6C5D9309BBC4B2DEEE2F0433B20`，provider 6 PASS。`VerificationPending`、`remote=null` 与 fault 本 case 各 consume 1；显式同 ID 核对模型 0，完整 coverage 文案通过 | unknown 旧 FAIL 保留；本链 PASS 不等于完整矩阵，临时 QA proxy 不是产品补丁 |
| narrow final PASS | 运行 HEAD `6dc70a0d` + dirty `FACCE...`，Test APK `6CF705BFCB8421A256F6C95EC1C80615E7D45D248F2111D12D6729709972D2AB`，45.87s PASS、provider9。USER head `55ff0f71-d2ae-4fc6-9a9f-4cc3a7e07e5c` 保留；regen `8deec9ce-f062-8f5a-bcd3-50320b4c5e1f` 为 non-head 且 parent 为 USER head；dirty 全文保留 | 最终 `a8507365` commit 匹配 dirty `FACCE` blob但构建早于 commit；本链 PASS 不等于同一 Test APK 4/4 或完整视觉验收 |
| 测试环境清理 | `device negative-round-fast/device-cleanup-receipt.json`：5556 nameguard 后恢复 1080x2424/density420/font1/rotation0，knownreverse 清除，emulator 正常退出、adb list 空、无 5554、wipe/uninstall。R4 `runtime-p1-5026e455-20261030-r4-6fe3e03fbc95/cleanup-receipt.sanitized.json` SHA-256 `88C393F31F72CD4402B83D7F4A7D30923B241DC40F31BC9A6F35CBED51F84888`：7576 无 listener/health 不可达，PID 104740/144540/150828 退出，临时 host `1AC09083` hashguard 删除，API `5026` tracked 无 diff，证据保留 | cleanup 已全部完成；环境正常清理不等于功能、production 或 Release 验收 |
| issues | Android [#37](https://github.com/2746267826/pim-android/issues/37) CLOSED(completed)；[#33](https://github.com/2746267826/pim-android/issues/33)/[#35](https://github.com/2746267826/pim-android/issues/35) OPEN(partial)。API [#505](https://github.com/2746267826/pim-api/issues/505)/[#525](https://github.com/2746267826/pim-api/issues/525)/[#533](https://github.com/2746267826/pim-api/issues/533) OPEN；#505 protected body base `306f380b658da6960f67c819de2826c5abbf03151904eecf4e3d8468f00aafca`，17 checkbox 仍 11/6；#525/#533 body 未改，#533 仅报告且无本轮修复 | issue 状态不是功能验收；#37 不含 OPPO/完整 MVP；不写 issue |
| 范围边界 | production、完整 MVP、OEM、OPPO、100/180 分钟真实课堂、长时后台、跨端完整流程、真实模型质量与公开逐图 receipt 均 `NOT-VERIFIED` | 本候选、JVM、旧设备前段或 Draft CI 都不能关闭总跟踪或完成完整 MVP |

**v0.37 本轮交付结果**

1. Android PR #54、exact CI、普通 merge、master CI 与 Release 已交付，所有准确 head/merge/tag/assets 见上表与状态段；不再保留本轮 Gate/merge/Release 待办措辞。
2. 四 cohort 保持真实 Test API + 原生 Android 模拟器的分轮 PASS 证据，不写成同一 Test APK 4/4、物理真机或完整视觉 PASS。
3. `fullApp 2008/2008` 与 `core 69/69` 分账不求和；历史失败、重签兼容、runner pull、narrow built-before-commit 与 FACCE blob 匹配均保留。
4. 后续仅按下方既有门槛独立验收，不新增本轮开发 scope。

**下一批（按现有 P0/P1/P2 保留优先级，不启动新开发）**

1. 独立跟踪并复现 [#525](https://github.com/2746267826/pim-api/issues/525)/[#533](https://github.com/2746267826/pim-api/issues/533) 的受控升级与上传故障链、验收门槛和回执。`#533` 当前只有 issue 报告/静态推断，不得写成已证根因，也不做生产处置或关闭 issue。
2. 之后对真实设备、OEM、OPPO、100–180 分钟课堂、长时后台与真实模型质量做独立验收；未经明确授权不启动付费模型调用。GitHub Release 不是真实升级或 production 证据。
3. 公开逐图/逐 block receipt，以及其余未实现的 Windows/TASK/MIG 范围继续按既有路线图排队；不得删除、缩短或把完整 MVP 降级为已交付。
4. 以上只列剩余门槛，不重排 canonical 已有优先级；`#505/#525/#533` 均保持 OPEN，直到各自真实证据满足。

<a id="v036-current"></a>

### v0.36 历史状态：d37/54f6 中间 head 的 CI 与 normal 前段证据（由 PR #535 / merge `6ace1366` 交付）

状态：**API/Web 的 v0.35 交付不变；Android derived functional 基础 head `2ae81d8a` 与中间 head `d37db9cd3967aac31e40e8a3081bddb3204e4045` 均已有 exact required CI；[Draft PR #54](https://github.com/2746267826/pim-android/pull/54) 仍为 Draft。`d37/testfix1` 真实 phone normal 仅前段 PASS：一次确认一 task、双 artifact、Photo/Ink 原字节与 ASR3/vision1/text2=6 通过；随后人工保存 USER head 超时 FAIL，原因待分类，normal/regen 整体不 PASS，negative/narrow 未验。第二 fix commit `54f6ace22d7895e1d8a471d53fbe6f7ad4e83a6a`（parent `d37db9cd`）已正常提交推送，按固定材料隔离 refresh/regen 并保持 legacy 缺 `kinds` pending 复用 ID；6 paths SHA6/6，targeted28/App1999/Core69 fresh JVM全绿。该 head 的 CI 与 new-device 仍 PENDING，QA 已从 exact head 开始 build；故不得写设备闭环 PASS、最终验收、已合并或已交付**。

| 层级 | 本轮可核事实 | 不能据此宣布 |
| --- | --- | --- |
| Android P1 commits / PR | 第一中间 commit `d37db9cd` 完成一确认一 task；第二 fix commit `54f6ace22d7895e1d8a471d53fbe6f7ad4e83a6a`（parent `d37db9cd`，7 files / 418 insertions / 43 deletions）按固定材料隔离 refresh/regen，并保持 legacy 缺 `kinds` pending 复用 ID。远端分支 head 为 `54f6ace2`，PR #54 仍 Draft | commits/push 不是新 CI 或 device 验收；`d37` 的 normal 人工保存失败历史保留，不能写已 merge/Release/Android 已交付 |
| d37 exact-head CI | [CI 41017246721](https://github.com/2746267826/pim-android/actions/runs/41017246721) completed/success，head `d37db9cd`：required/CI Gate SUCCESS；resolved-version、Contract freshness、codegen、Android shell build、Android build 均 success，PR 事件 `release` 按规则 SKIPPED | CI 只证明该中间 head 的构建/契约/单测门禁；不能覆盖 A/B P1 或真实 normal 后半失败 |
| 54f6ace exact-head CI | `54f6ace22d7895e1d8a471d53fbe6f7ad4e83a6a` 的 CI 当前 PENDING | 旧 d37 CI 不能替代新 head；新 head CI 回执前不得升级状态 |
| Android 旧 base CI | [CI 41004036926](https://github.com/2746267826/pim-android/actions/runs/41004036926) 历史 success，head `2ae81d8a`；`release` 在 PR 事件按规则 skipped | 仅作 parent 历史，不是 `d37db9cd` 或最终 source 的验收 |
| P1 source READY / 冻结 | `scratch/android-duplicate-start-20261030/READY-freeze.json` 状态 `READY_PRODUCT_SOURCE_FROZEN`，冻结 source parent 为 `2ae81d8a`；11 个 product/test paths 已逐文件独立核对 SHA-256：11/11 match、0 mismatch；对应修复正常提交为 `d37db9cd`。含 7 main + 4 test paths，排除 generated contract、workflow、AGENTS、生产数据等非交付输入 | 冻结不是 CI/APK/device 验收；不得用旧 base CI 代替 `d37db9cd` exact CI |
| P1 行为修复 | 一次 confirm 只调用一个 `artifacts.start(...)`；task 持久关联 `CompleteRecord` 与 `IndependentAnalysis` 两 kind；VM、regen 与 UI 使用 task 的 `supports(kind)` 判断；费用同意文案明确每次确认同时请求两种产物、可能产生费用。source evidence 在 `NotesViewModel.kt`、`NoteArtifactTaskModels.kt`、`NoteArtifactEntrySheet.kt` 等已提交路径中 | 该行为已有 source/JVM、d37 CI 与真实 normal 前段证据；但 A/B scope 和 normal 后半失败未关闭，不能写真实设备整体 PASS |
| P1 A/B / legacy identity source | 独立 review 报告 `scratch/note-derived-implementation/android-p1-independent-review-d37db9cd.md`。A/B fixed-material scope 与 legacy pending identity 已提交 `54f6ace2`：`scratch/android-material-scope-20261030/READY-freeze.json` 6 paths、SHA 6/6 match；显式 `materialVersionId` pin refresh/publish/tasks/drafts/candidates，A 重生成留在 A；缺 `kinds` 按两 server artifact 解码，pending unknown 复用同一 TaskId | **COMMITTED / NEW CI+DEVICE PENDING**；真实 normal/人工保存与新 exact-head device 未验，当前不得写已修复/最终验收 |
| P1 本地回归 | `54f6ace2` 对应 targeted `28/28`、fresh App `1,999/1,999`、fresh Core `69/69`，均 fail 0 / error 0 / skip 0，JBR 21 `BUILD SUCCESSFUL`。此前 `26/26 + 1,997 + 69` 与 App `1,990` checkpoint 保留为历史 | targeted、App、Core 是不同证据集，**不求和**；本地全绿不替代 54f6ace2 新 CI、APK 或 device |
| 历史真实设备失败 | 原 owned phone/R2 normal FAIL：一次 confirm 新增两个 TaskId `41bf2402...`、`bb65ba87...`，同一 note/material/ASR 且都引用同一双 artifact；该 R2 FAIL 历史保留 | 旧失败不能由后续前段 PASS 删除；它不是最终通过证据 |
| d37/testfix1 真实 normal | Run `scratch/note-derived-implementation/android-qa/runs/20261030-150514-normal-phone`：前段 PASS 已见一次 confirm 一 task、task 双 artifact、Photo/Ink 原字节、provider ASR3 + vision1 + text2 = 6；随后人工保存 `FullRecord` USER head 断言超时 FAIL，`versions=[e11de5eb-0d40-ac50-a4b9-9d2f78f84a51]` 无 USER version。诊断见 `scratch/note-derived-implementation/android-qa/human-save-diagnosis-20261030.md` | normal/regen 整体不 PASS；失败原因待分类，不能写 regen 通过；negative/narrow 未验，也不能以“前段 PASS”宣布 Android 已交付 |
| 产品根因边界 | `NotesViewModel.kt:154` 对每个选中 kind 循环调用 `artifacts.start(...)`；但 backend 每个 task 已同时产生两种 artifact，因此 UI 选择不应 fan-out 为多个 server task。Provider summary：ASR calls 3 / ASR succeeded 3 / vision 0 / text 0，尚未重复视觉或文本调用 | 现有 2ae head 存在重复创建 P1；vision/text 为 0 不等于不会重复收费，完整双产物及 Android 验收仍未通过 |
| P1 新 CI / QA 状态 | d37 exact CI 41017246721 SUCCESS 且 testfix1 normal 前段 PASS 后人工保存失败；新 source `54f6ace2` 已提交，其 CI PENDING。QA 已从该 exact head build，normal/人工保存复验尚未完成 | 前段 PASS、d37 CI 或 candidate JVM 都不是最终验收；新 device 尚未有，不得写 device PASS 或 Android 已交付 |
| issues 收尾事实 | [Android #37](https://github.com/2746267826/pim-android/issues/37) 已普通 CLOSED（2026-10-30T06:52:09Z，reason completed），本机材料版本 slice 验收满足，[评论](https://github.com/2746267826/pim-android/issues/37#issuecomment-6516822157)明确 OPPO 与完整 MVP 不含；[#33](https://github.com/2746267826/pim-android/issues/33) 已追加 partial 进度并保持 OPEN（[评论](https://github.com/2746267826/pim-android/issues/33#issuecomment-6516831970)）；[#35](https://github.com/2746267826/pim-android/issues/35) 已追加 partial 进度并保持 OPEN（[评论](https://github.com/2746267826/pim-android/issues/35#issuecomment-6516835098)） | #37 close 不等于完整 MVP/OEM；#33/#35 的设备 AC 仍未完成 |
| API issues | [API #505](https://github.com/2746267826/pim-api/issues/505)、[#525](https://github.com/2746267826/pim-api/issues/525)、[#533](https://github.com/2746267826/pim-api/issues/533) 均保持 OPEN，body 未改。#533 仅有 issue 报告，本轮未独立复验且无本轮修复 | 不因 issue 状态、Draft PR 或本地测试关闭总跟踪；#533 不得写成已修复 |
| 证据入口 | P1 初版冻结 `scratch/android-duplicate-start-20261030/READY-freeze.json`；A/B/legacy candidate 冻结 `scratch/android-material-scope-20261030/READY-freeze.json`；本地日志 `targeted-material-scope-round3-jbr21.log`、`full-app-core-round2-jbr21.log`、`full-core-rerun-jbr21.log`；d37 CI 回执 `scratch/note-derived-implementation/android-pr54-run-41017246721-final.json`；normal 失败归档 `runs/20261030-150514-normal-phone/fail-phone-normal-human-regenerate-1793343991460.tgz` SHA-256 `ED5214...3CC2C1`；issues 回执 `scratch/note-derived-implementation/issues-followup-20261030/remote-actions-receipt-20261030.json` | 冻结/JVM/CI/issue 回执及 normal 前段都不是 device PASS；真实视觉/文本质量、课堂、OEM 与 production 仍 `NOT-VERIFIED` |
| production / MVP | #505、#525、#533 仍 OPEN；真实 upgrade 303、OEM、OPPO、100/180 分钟课堂、长时后台、跨端完整流程、课堂质量、公开逐图 receipts、full MVP 与 production 全部仍 `NOT-VERIFIED` | Draft PR、旧 base CI、P1 commit/本地全量绿或一次设备失败修复都不能关闭问题或完成完整 MVP；#533 无本轮修复 |

**v0.36 下一步（只登记真实可执行顺序）**

1. 等待 `54f6ace2` exact-head PR CI；未回执前不升级状态。`d37` 的 CI/前段 normal 只作历史诊断。
2. 新 source build 后先跑 normal phone，既确认一 task/双 artifact、A 重生成不创建 B task，也查清并验证人工保存 USER head 超时。
4. normal 完整通过后再跑 negative/narrow；再推进真实课堂/OEM/OPPO/100–180 分钟/长时后台与升级验收；公开逐图 receipts、真实视觉/文本质量仍 `NOT-VERIFIED`。

<a id="v035-current"></a>

### v0.35 历史状态：API/Web 已合并交付，Android 当时仅本地实现（截至 2026-10-30）

状态：**历史记录（v0.35 时点）**。当时 API/Web 已完成普通 PR、required CI 与 GitHub Release；Android 原生派生稿功能仅在本机工作树实现，尚未提交、创建 functional PR、通过 instrumentation test APK 编译或执行真实设备 E2E，不能记为已验收、已合并或已发布。本节的旧源码、失败与未执行事实原样保留；后续 Android feature commit、Draft PR、CI 和真实设备失败见 v0.36，不覆盖当时状态。本轮只更新进度与证据边界，不缩小完整 MVP/OEM/课堂/生产范围。

| 层级 | 本轮可核事实 | 不能据此宣布 |
| --- | --- | --- |
| API merge / CI / contract | [PR #532](https://github.com/2746267826/pim-api/pull/532) 普通 MERGED：exact head `5026e455`，merge `3e3829aa959f34cd602d193a82462116b80d0737`，2026-10-30T02:53:34Z。exact PR [CI 40907900018](https://github.com/2746267826/pim-api/actions/runs/40907900018) SUCCESS：6193 pass / 15 skip / 0 fail（6208 total），其中 Web `349/349`。master [CI 40994288275](https://github.com/2746267826/pim-api/actions/runs/40994288275) completed/success，Gate/Release/Notify 均 success；`build-mcp` 合法 skipped，不计 pass。canonical contract SHA-256 `e38d1991b372174a3a4bac10df0a35eee90de92d268a5f1d35762d76da07f0f9`（1,119,472 bytes，329 paths，659 schemas）；CI 真实生成 artifact 已修复旧本机导出缺 `/mcp` 的问题，所有历史失败保留 | 不表示 production 已升级；专项/focused 计数不与最终 CI 6193 相加；`build-mcp` skipped 不算 pass |
| GitHub Release | [api-v26.10.1011](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.1011) published 2026-10-30T03:22:29Z，non-draft/non-prerelease，target `3e3829a`。真实 evidence 为仓外 scratch 的 `.codex-tmp/note-derived-implementation/final-planning/api-master-run.json` 与 `api-releases.json` | **GitHub Release ≠ production 部署**；未读取或变更生产来补证 |
| 消费者契约同步 | Android officialbot [PR #52](https://github.com/2746267826/pim-android/pull/52) 普通 merge `4a05a68bc991a3fa92bd269d88cb7a7955cc07dd`，master [CI 40995211845](https://github.com/2746267826/pim-android/actions/runs/40995211845)/Gate/release SUCCESS，[android-v26.10.1125](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1125) 于 03:13:03Z 发布。Windows bot [PR #38](https://github.com/2746267826/pim-windows/pull/38) 普通 merge `69defe7ab4826466a54ab086c29def5f1eba7fad`，master [CI 40995706728](https://github.com/2746267826/pim-windows/actions/runs/40995706728)/Gate/release SUCCESS，[windows-v26.10.1104](https://github.com/2746267826/pim-windows/releases/tag/windows-v26.10.1104) 于 03:13:45Z 发布 | 两端只是契约/生成物同步，不是原生派生稿功能交付，也不证明 Android functional App 已发布 |
| Web 真实受控验收 | 最终 frontend `index.html` SHA-256 `c915835a...`；真实隔离 Test API + loopback SDK 下 phone 390×844 core PASS、narrow 320×720 core PASS；negative PASS：CAS 双留、丢失响应后 503→200 且同版本模型 call 不增、dirty 刷新/切候选保留。原图 Photo/Ink bytes match；ASR 3 + vision 2 + text 4 = 9 calls；普通账号 admin calls 0。late-success receipt P1 单独 12/12 real-SDK cases PASS。public evidence：`.codex-tmp/note-derived-implementation/qa-runtime/public-evidence/final-C915-20261029`，后端 final receipts/coordination.md 可读 | 合成 provider 不代表真实模型质量；narrow 过渡截图不等于完整视觉验收；focused 12/12 不与完整 CI 6193 相加 |
| Android 原生功能（未提交） | 工作树 `wt/note-derived-android`，HEAD `967ad5cb`，基线来自 officialbot；typed 14 operations，Client/Repo/VM/导航/EntrySheet/UI/人工修订与再生等已接。app Debug APK 已构建，SHA-256 `2375cb77...`，20,476,254 bytes。focused 30/30 + core 69/69 PASS；fullapp 1985 有 33 FAIL，集中在旧 ASRStore 写入 checkpoints，根因未证实，正在以新独立基线对照和尝试最小修复 | 无 functional PR、无全量绿、instrumentation test APK 编译仍未通过、真实 Android 设备 E2E 未执行；不得声称已验收/Release；owned AVD 与 Test API 仅是准备状态 |
| Android 消费者 Release | `android-v26.10.1125` 的 merge 来自 officialbot 契约同步 `4a05a68b` | 不能把它当成本地 derived functional APK 的发布 |
| production / MVP | #505、#525 仍 OPEN；真实 upgrade 303、OEM、OPPO、100/180 分钟课堂、长时后台、跨端完整流程、课堂质量、公开逐图 receipts、full MVP 与 production 全部仍 `NOT-VERIFIED` | 阶段交付、GitHub Release、契约 bot 或本地 APK 都不关闭问题或完成完整 MVP |

**下一步顺序（不改变完整范围）**

1. 先完成 fullapp 33 项失败的独立基线对照与最小修复回归，并让 Android instrumentation test APK 真实编译通过。
2. 冻结新 App/test APK 与 commit，使用 owned AVD/Test API 执行真实设备 E2E；设备未通过前不创建“已完成”声明。
3. 从最新 official master 建立独立 functional Android PR，跑完整 required CI；PR 和 master 证据分别记录。
4. 再做真实课堂/OEM/OPPO/100–180 分钟/长时后台与升级验收，不以模拟器、合成音频或 loopback provider 代替。
5. 公开逐图/逐 block receipts 与图片质量仍为 P2；完整最终 CI、Android functional 交付和生产层级继续分开升级状态。

### v0.33 历史状态与交付边界（截至 2026-10-29 21:07）

普通账号固定版本 ASR 的前三步已阶段交付：管理员共享目标授权/destinations、按唯一契约再生成 Android、固定版本逐段任务/结果 UI。Android ASR [PR #51](https://github.com/2746267826/pim-android/pull/51) 已普通 MERGED：exact head `8b1d091`，merge `93d934379b9a0e10318a9369e4d9d80a0c47e5e2`，2026-10-29T12:53:42Z；merge tree `6294c2ab6e95112f5a1461e66084a6864a45b82e` 与测试 source `8b1d` tree 相同，无 admin、head match、分支保留。准确 [CI 40874958748](https://github.com/2746267826/pim-android/actions/runs/40874958748) native/shell/resolve/freshness/codegen/Gate 全 SUCCESS/CLEAN；准确 master [CI 40877083407](https://github.com/2746267826/pim-android/actions/runs/40877083407) 全部 required/Gate/release SUCCESS。Current Android Release 为 [android-v26.10.1123](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1123)：2026-10-29T13:05:35Z 发布，non-draft/non-prerelease，tag/source `93d93437`，3 assets（Android APK 14,273,356B、shell 2,550,426B、version 480B），version JSON 含 SHA。

| 范围 | 当前事实 | 边界 |
| --- | --- | --- |
| 普通账号 ASR 阶段 | API #528 已合并；Android #51 已合并 `93d93437`；master CI 40877083407 SUCCESS；Release 1123 指向 `93d93437`；QA FINAL `33/33 PASS / 0 FAIL`；App source `1eaa3c5fe09d13460288eee8e0d6b3723f297cbc`/sha `001546`、2024 JVM 同 product lineage 保留 | Release 1123 不等于完整 MVP、production 或完整 UI |
| 设备与组件证据 | phone12+narrow12+tablet9，bucket 按 R6/R7/R8 lineage；Source 组件 3 为同源 R5 另账，不并入 33 | P2 `fullBody` NOT-VERIFIED；不得称 36 PASS 或完整 UI 全过 |
| 清理与安全 | `qa-cleanup-receipt-20261029.json` SHA-256 `75e6676794875d5edfef5ee41eca7e96c1c762307d9429fef13f10bc4ddcc335`；10 archive hashes+3 profile manifests、no JWT/auth prefs；Host74716停、9098不可达、59b912 temp source移除；adb空，未碰5554/PG | 不代表 production 或真实设备升级验收 |
| 发布与真实运行 | v0.33 时点 API Release `api-v26.10.997`、Android Release `android-v26.10.1123`，P0 `android-v26.10.1116` 转为历史；真实 upgrade E2E、#525 历史303 pending、production、OEM、100/180分钟课堂、M2 跨端及全 MVP 仍未验 | GitHub Release ≠ production；不能据此关闭 #525/#505；当前 API/Web Release仍见 v0.35，Android functional状态见 v0.36 |

Source 组件 3 为同源 R5 lineage，另账且不称 R8 重跑。P2 UI 余项：窄屏 Legacy dialog 的 owner/server/count/range 与取消确认按钮实见，但说明正文尾部在大字号被截于“拒绝”、浅色过渡帧不足；`fullBody` 仍 `NOT-VERIFIED`，完整 UI 不能记全过。

### v0.34 历史切片实现与受控验收记录（已于 v0.35 完成 API/Web 交付）

状态：**历史记录**。v0.34 当时是 API/Web 已实现、受控验收中；其后续最终 CI、真实 API + Web 复验、merge 与 GitHub Release 已由 v0.35 完成，Android functional 端仍未完成。设计与逐项验收矩阵见[设计与验收](../architecture/native-note-derived-artifacts.md)。

| 范围 | 本轮事实 | 仍待完成 |
| --- | --- | --- |
| API 固定材料与双产物 | 固定版本 `document.md` 原文/hash；全部成功 ASR 完整计划与 slice offset；照片/手写图原字节 SHA；`record-synthesis`/`analysis-synthesis` 独立调用、不可变 version、分析读取记录正文与来源；生成不移动人工 head | 最终整库 CI、真实 API + Web 闭环、完整负向集复跑与 merge |
| 当前 SDK 批次限制 | PNG/JPEG 单批最多 4 张、单图 4 MiB、单批 8 MiB；最多 32 图/8 批、200k 输入/单证据 50k；WebP/超限显式 blocked；不改上传 32/40 MiB 能力 | 公开 coverage 仍只有聚合 count，逐图/逐 block 用户 audit 未完成；provider 实际字节覆盖仅内部受控验证 |
| 修订、重放与恢复 | 人工完整 payload 幂等；CAS 冲突保存双方全文；`Mutation.IsHead` 区分 crash 候选；locator 可修复；no-op 不变 Revision；stop 保留 success/unknown 且不自动重发；retry 限显式范围并要求 ack | 最终 CI 与真实 API + Web 竞态闭环 |
| 契约与证据 | v2 分支契约由真实 Test Swagger 导出：HTTP 200，`jq -S` 后 SHA-256 `b745bcd4bc3fd7d09e8971d59cdcf2e40de2d9454a8e48dda6ff01ffd1b250b5`；官方 Web 生成/check exit 0，生成物 SHA-256 `11acd5eb5b2d577522658679ee9bd70bed3ce3c518a592a70f300026a4891be6`；API 统一 build 0 errors/64 warnings，专项三轮为 `50/57`、`24/25`、`1/1`，不求和 | 最终统一 CI、最终契约快照、Android 官方快照与 merge |
| Web 与 Android | Web 最终 `349/349`（46 files，23:10:22，25.27s）、note-derived `36/36`、storage 独立 `20/20` + TS/lint、lint/build exit 0；双视口合成 QA 各 8 状态但非真实 API。真实 Test API 首次手机 core 在旧 WebIndex `f6bd...` 上探路 PASS：ordinary/admin0、ASR3+vision2+text4=9、确原图 bytes、无重 ASR、手工后再生成保 head。Android 本机人工稿 `10/10 PASS`；纯结果 UI 在办，网络等待 API 官方 merge 契约 | 最终 dist `c915835a...` 上的 phone+narrow 真实 Test API 复验；Android 网络、设备与完整结果 UI；当前不得写成已对 Android 开放 |

[#505](https://github.com/2746267826/pim-api/issues/505) 与 [#525](https://github.com/2746267826/pim-api/issues/525) 仍 OPEN；GitHub Release 不等于 production。真实 upgrade、历史 303 pending、OEM、100/180 分钟真实课堂、跨平台 M2、课堂质量、full MVP 和生产仍为 `NOT-VERIFIED`。

### 历史证据与旧修订（v0.32及更早）

- v0.32 的旧 current-at 指针 `dae87c32afeb1c35cb27252dc7e3bb3321c0980f` 与 CI `40840177716` 仅作历史；旧 frozen `852bce4`/app`5da8`/test`6a84` 为 Tests run 6 / Failures 3。
- 历史失败逐条保留：`18ce5` case 3/2、`5da86` case 3/3、`b97/8bff6` case 3/3、`577/e0816` case 4/2、`R5test220a6` case 5/1 fail、R7 narrow 3/6 selector fail。
- 既有失败、受控夹具与生产外证据继续保留在原处，不用一次绿灯覆盖历史失败。
- v0.34 开工前的历史口径原文继续保留，不得误读为当前状态：`独立分析及全部材料的完整多步骤处理未实现`；`全材料实际参与 AI 未实现`；`Windows/Web多端同篇访问及处理入口仍未接通`；`跨端编辑、比较/解决UI与真实并发端到端仍未做`；`普通历史字节回收、模型同意/第三方保留、记录清理、备份删除、全服务器配额、定时清理及生产磁盘验收仍有缺口`。
- API #528 历史合并 `a3b35dc`，CI 40825191098（6122/15/0），Release `api-v26.10.995`；P0 API #529 合并 `c5ff06fe`，CI 40838578113（6150/15/0），Release `api-v26.10.997`。
- Android bot #49 merge `e629febe`、Release `android-v26.10.1112`；P0 Android #50 merge `235f1203`、Release `android-v26.10.1116` 为历史；Windows bot #37 merge `94384c9e`，均不代表完整笔记 MVP。

本次直接入口是第7节第1-5步编码依赖链及第6步发布验收；[全材料双产物与修订保护](#v035-current) 已通过 API PR #532 与该 PR 内同仓 Web 完成普通 merge、required CI 和 GitHub Release，行为与逐项验收状态见[设计与验收](../architecture/native-note-derived-artifacts.md)。Android derived functional 后续已固定 feature commit `2ae81d8a`、创建 Draft PR #54，并让 exact-head required CI 41004036926 通过；但 owned phone 的正常 E2E 因一次确认创建两个 task 而 FAIL，修复未完成。因此不能写成已修复、已 merge、已验收或已 Release，也不能把 PR CI 当设备通过。该切片不新增编号、不复制第二套 backlog。共享 ASR 模型由管理员预配置并 enable，普通账号只 consume 安全 targets，不是个人 BYOK；消费者契约 bot 合并不等于 Android 功能交付，也不证明 Windows/Web 跨端已验。

### 已采纳的核查结论与当前批次（2026-10-29 校对）

用户要求按完整MVP断点推进并维护进度，已采纳“契约对齐 → 正式安卓入口/账号绑定上传 → 全材料双产物/修订保护”的优先级。通用文字流式和旧调用迁移不再作为全部客户端工作的统一前置；实时ASR仍非必需，不删除任何既定范围。

- [Android #44](https://github.com/2746267826/pim-android/pull/44)与[Windows #35](https://github.com/2746267826/pim-windows/pull/35)已按准确head全绿门禁合并，均只改契约/生成物，统一到API `5da39cc`的315路径；不表示上传或新原生业务已实现。
- [Android #48](https://github.com/2746267826/pim-android/pull/48)从 `78b5d915`独立实现正式笔记导航/本机搜索及固定版本、账号/服务器绑定后台上传；准确head `29432b5` 的[PR CI](https://github.com/2746267826/pim-android/actions/runs/40807190584)成功，随后以 `2debf569` 合并，[master CI](https://github.com/2746267826/pim-android/actions/runs/40811075538)也成功。仅存储材料，不自动发起收费模型任务；原录音/照片/笔迹及已合并编辑恢复继续复用。
- [API #528](https://github.com/2746267826/pim-api/pull/528)已普通 **MERGED** 到 `a3b35dc`：AI 模型/运行时是管理员管理的全局共享配置，普通账号只能处理自己的固定材料/任务，通过安全目录选择管理员已配置并启用的共享转写目标；空目录先由管理员配置，不提供普通账号 BYOK 自助。owner 隔离约束材料/任务/调用账号，不表示模型配置有 owner。实际目标/预设/地址/语言与明确同意被绑定，配置变化、停用、非 owner 任务和越权在任务路径拒绝；PR CI 40825191098 SUCCESS（测试 6122 通过/15 跳过），contract SHA256 为 `eff87d962f5a935148c7f74ef1d73793d534719c4493397fd8a8247567d644c0`。master [CI 40829847795](https://github.com/2746267826/pim-api/actions/runs/40829847795) 整体 SUCCESS，Gate、contract notify 与 release job 成功；[api-v26.10.995](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.995) 已发布并指向 `a3b35dc`。**GitHub Release ≠ production**，生产升级/健康仍 `NOT-VERIFIED`。
- Android ASR [PR #51](https://github.com/2746267826/pim-android/pull/51) exact head `8b1d091`/R8 sha `67f0` 已普通 MERGED `93d934379b9a0e10318a9369e4d9d80a0c47e5e2`（2026-10-29T12:53:42Z，tree `6294c2ab...` 与 source `8b1d` tree 相同，无 admin/head match/分支保留）；[CI 40874958748](https://github.com/2746267826/pim-android/actions/runs/40874958748) native/shell/resolve/freshness/codegen/Gate 全 SUCCESS/CLEAN。QA FINAL `33/33 PASS / 0 FAIL`：phone12+narrow12+tablet9；narrow ASR6 `OK6 @20:39:58`、Legacy3 `OK3 @20:41:09`、tablet ASR6 `OK6 114.559s @20:44:47`。Source 组件 3 为同源 R5 lineage，不冒称 36 PASS。旧 `18ce5` case 3/2、`5da86` case 3/3、`b97/8bff6` case 3/3、`577/e0816` case 4/2、`R5test220a6` case 5/1 fail及 R7 narrow 3/6 selector fail 继续保留。
- 契约 bot 已普通 merge：Android [#49](https://github.com/2746267826/pim-android/pull/49) head `824ab9` 的 [CI 40832353212](https://github.com/2746267826/pim-android/actions/runs/40832353212) 与 Windows [#37](https://github.com/2746267826/pim-windows/pull/37) head `42f141` 的 [CI 40832349406](https://github.com/2746267826/pim-windows/actions/runs/40832349406) 各 required CI 全 SUCCESS，diff 仅 contract/generated、来源复核无 finding。Android merge `e629febe90f2e6c79dd560c429ac2b6c17698d00`（08:00:40Z），master CI [40839118734](https://github.com/2746267826/pim-android/actions/runs/40839118734) SUCCESS，Release [android-v26.10.1112](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1112) 准确指向该 merge；Windows merge `94384c9ecc632d0a0b4c20162f484e6228ff2ac7`（08:00:34Z），master CI [40839109286](https://github.com/2746267826/pim-windows/actions/runs/40839109286) SUCCESS，Release [windows-v26.10.1102](https://github.com/2746267826/pim-windows/releases/tag/windows-v26.10.1102) 准确指向该 merge。两者均不代表 ASR 产品或完整 MVP 完成。
- [API #525](https://github.com/2746267826/pim-api/issues/525)仍 **OPEN**。API P0 PR [#529](https://github.com/2746267826/pim-api/pull/529) head `2287ecc8` 的准确 [CI 40838578113](https://github.com/2746267826/pim-api/actions/runs/40838578113) 最终 SUCCESS、PR CLEAN，已普通 MERGED 到 `c5ff06fe413c10a04956f21825314d0dc5770e93`；本机 P0 Mobile focused 68 项、middleware+OpenAPI shape 46 项、真实 PostgreSQL 8 项均 PASS，Swagger 规范化后与 contract `eff87d96` 逐字节 match。master [CI 40842715856](https://github.com/2746267826/pim-api/actions/runs/40842715856) SUCCESS，Release [api-v26.10.997](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.997) target `c5ff06fe`、非draft/prerelease、4 assets。PG marker 实例在 8/8 真库测试后按现有 marker 正常停止：`Running=false`、PID `88540` 不存在、postmaster.pid 不存在，数据保留。Android P0 PR [#50](https://github.com/2746267826/pim-android/pull/50) source head `8c55dc1576b7a8a2e64ef9ca5db1ed7766a09812` 的 [run 40845315327](https://github.com/2746267826/pim-android/actions/runs/40845315327) required CI 全 SUCCESS，3 项 P1 修复已由静态 review 关闭；该 PR 已普通 MERGED 到 `235f1203e3dc403665758cf4d25d11fd0dbc523d`（2026-10-29T09:08:31Z）。组件检查与真实升级仍 `NOT-VERIFIED`，不能据此关闭 #525。旧 head `089391ba` 的绿 CI 仅作历史；local focused app290/QueuedUpload4/coreNetwork41 PASS（计数可能重叠，不求和），真实 5k stress Windows JBR21 C2 native crash 历史保留 `NOT-VERIFIED`，501 max1 单测 plain PASS。历史 303 pending 和生产数据未改，#525 不因 API/Android merge 或 Release 关闭。
- 全部材料双产物/人工修订保护已完成 API/Web merge/release 与最终 CI；Android derived functional 的 normal、conflict、unknown、narrow 四条链路已分轮次真实 PASS，最终 Android test head 为 `a8507365`，匹配 narrow dirty `FACCE` blob但构建早于 commit。四链均为 App D197/main tree201a4f，但 Test APK/source 不同，不拼成同一 Test APK 的 4/4。最新 App2008/Repo11/Core69 全绿；a850 exact Gate、merge/master CI/Release 仍未完成，不能写完整矩阵或交付。v0.35/v0.36 失败历史保留。全局撤销、长文性能、真实长时后台/OEM课堂、OPPO与真实升级确认继续保留。45稳定编号和32差距行继续逐项保留。

### 本批安卓入口与存储上传的验证边界

#48包含“今日/笔记/更多/设置”、可重用本机库/编辑器、标题/正文搜索、宽窗口侧栏。定位/轨迹/日程/状态保留在“更多”。固定材料经明确同意后进入持久队列；每次首发/401重试都校验原owner和服务器，任务/WorkData不存令牌。分块/完整回执未知时核对同一版本，暂停、原账号明确恢复、有限分批和退避均保留原稿；冲突不自动覆盖。

验证使用本任务专用模拟器和真实隔离Test API，普通合成账号及照片/笔迹/PCM；4,802,915字节混合版本、上传A同时编辑B、丢失完成回执后离开页面继续、换账号暂停/原账号恢复、重建后重新同意分别有设备断言。它不是生产、真实课堂、付费ASR或OPPO证据。首次回环网络条件导致排队超时、首轮CI的Windows换行摘要差异均保留；分别修正字面回环约束及经唯一脚本重新生成，未扩大120秒预算、改工作流或弱化断言。窄屏截图发现新建按钮遮挡状态后改为独立操作区并重跑。

最终head `29432b5` 的原生/独立壳、契约freshness/codegen与CI Gate全部SUCCESS；本地应用1890+core60 JVM零失败/零跳过，debug/test/release构建通过。该JVM结果来自产品源码相同的`9408df1`，之后只加强三个androidTest文件；最终head的必需CI另已执行通过。设备矩阵逐组日志/摘要校验为80通过、0失败、0跳过；本轮真实Test API保存54分块/9完整版本。CI不运行模拟器，不将二者混为一项证据。

| 专用模拟器窗口 | 原编辑/录音/恢复 | 实际API上传 | 导航 | 合计/结果 |
| --- | --- | --- | --- | --- |
| 1080×2424、density420、字号1.0 | 30 | 4 | 4 | 38 PASS |
| 640×1280、density320、字号1.5 | 13 | 4 | 4 | 21 PASS |
| 2048×1536、density320、字号1.5 | 13 | 4 | 4 | 21 PASS |

保留`9408df1`原手机26/27失败及后续单项失败：原预览/返回用例未核对真实模式、控件可用及导航状态，最终测试增加这些断言并滚动定位Lazy条目，保留原稿/笔迹/尺寸/重开校验。一次与测试包重建重叠的启动ANR独立保存；不声称排尽所有环境/OEM时序故障。普通服务器仍要求CONNECTED；Doze/OEM、实际进程杀死/设备重启、真实无公网LAN及长期传输未验收，不以受控503代替。当前最多256个保留上传意图，归档/清理产品入口未交付，服务器配额/过期以实际回执为准。

### 本批长录音服务端切片

从已合并主干独立接入[固定材料持久分段ASR](../architecture/native-note-asr-tasks.md)：全部录音按样本拆分/覆盖、录音内及全篇时偏、Hangfire调度、检查点重开/调用回执核对、逐片结果/停止和明确失败片恢复。管理员Web从已上传版本选择并确认目标，成功片不重跑。已通过[#520](https://github.com/2746267826/pim-api/pull/520)合并；准确head `dc8577ee` 的完整CI为6118通过/15跳过/0失败，合并后主干CI/契约通知/GitHub Release成功；不能将其当成Android上传或真实课堂质量验收。

基础之后，普通账号 ASR 授权/destinations 已随 API #528 合并且 PR/master CI 成功；Android ASR PR #51 已普通 MERGED `93d93437`，exact head CI 40874958748 全 SUCCESS/CLEAN，master CI 40877083407 SUCCESS，Release 1123 指向同一 commit，QA FINAL `33/33 PASS / 0 FAIL`；Source 组件 3 按同源 R5 lineage 另记。v0.35 的完整记录/独立分析/修订保护已随 API PR #532 合并、完成 40907900018/40994288275 与 Release `api-v26.10.1011`，并由最终 Web `c915835a...` 真实受控 API 复验。Android functional 后续已到 Draft PR #54/CI 41004036926，但真实 phone E2E 因一次确认创建两 task 暴露 P1，修复与设备重验仍待完成。流式/部分结果、旧AI调用迁移、后台进程实际崩溃及OPPO验收继续保留。100/180分钟合成材料与真实课堂/设备分别记账，45稳定编号和32差距行不变。

<a id="四类进度总览2026-10-28-校对"></a>

### 四类进度总览（2026-10-29 校对）

本表与下面32项差距台账共用事实来源。已合并基础、仍需编码的缺口、仅待目标设备验收分别列明；“进行中”不能解读成只剩真机测试。完整MVP仍未完成，测试证据按实际修订和环境记录。

**已完成**

| 范围 | 实际完成及证据 | 不能据此宣布 |
| --- | --- | --- |
| 产品范围与技术方向 | 完整目标/六条规则/45稳定编号持续保存；本轮确认SDK直连、最小AI三项与原生组件分工 | AI调用、正式页面或全部平台已经实现 |
| 服务器材料收包 | #509随#510合并：鉴权、完整清单、分块/摘要、固定版本/冲突保全、配额/过期/删除与唯一契约；合并后完整CI通过（API6036通过/15跳过） | Android已上传、逐段ASR或模型已参与 |
| 文件系统内置存储 | #510已合并：无需云盘的服务器来源，文件/旧附件可用，普通长文件分块、常用文件操作及共享字节层；旧引用保留 | 手机本机盘、普通历史字节物理回收或生产恢复验收已完成 |
| Android薄主题基础 | #32已合并；PIM蓝色、主题/基础组件引用已落到已有页面 | 正式笔记导航和整套UI已完成，或已升级/接入所选组件 |
| 最小AI文字闭环 | [PR #514](https://github.com/2746267826/pim-api/pull/514)已合并，合并前完整CI为API6061通过/15跳过/0失败，主干发布/通知也成功；已实现类型/默认模型设置→SDK非流式文本→实际消息/正文/回复与配置快照→基础统计；权限、凭据加密、幂等/取消/超时/未知恢复有测试。复用现有SDK，不加独立网关；[实现及验证](../architecture/ai-text-model-settings.md)区分本机SDK HTTP与合成界面证据，合并/完整CI见本切片PR | 真实模型质量、视觉/ASR、流式/中断部分回复、旧业务迁移或笔记处理已完成；旧链路仍经LiteLLM |
| 多图视觉基础 | [PR #515](https://github.com/2746267826/pim-api/pull/515)已合并，主干CI/发布通过；实现正文+1至4张PNG/JPEG实际SDK输入、目标确认、原图存入默认文件来源、顺序/摘要/所有者保护及回复/统计；[实现与证据](../architecture/ai-vision-calls.md)区分本机HTTP、合成网页和授权网关小样 | 真实笔记/手写质量、全部材料版本处理、流式或完整MVP已完成 |
| ASR预设与单片调用 | [PR #517](https://github.com/2746267826/pim-api/pull/517)已合并；准确head完整CI为6102通过/15跳过/0失败。策略/注册目录仅MiMo、独立凭据、单音频/语言、实际记录/统计、原音频/指纹/所有者读取；auto/zh/en三次授权真实合成短句通过；[实现及限制](../architecture/ai-asr-presets.md)保留验证边界 | 已支持整堂课、全部段持久处理、流式、原生上传或完整MVP |
| 普通账号ASR授权与目标发现 | [PR #528](https://github.com/2746267826/pim-api/pull/528)普通MERGED到`a3b35dc`，head`1b3b0edb`；PR CI [40825191098](https://github.com/2746267826/pim-api/actions/runs/40825191098) SUCCESS（6122通过/15跳过/0失败）；master [CI 40829847795](https://github.com/2746267826/pim-api/actions/runs/40829847795) 整体SUCCESS，Gate/contract notify/release job成功，历史[api-v26.10.995](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.995) target`a3b35dc`。模型/运行时为管理员管理的全局共享配置；普通账号只处理自己的固定材料/任务，从安全目录选择管理员已启用目标，空目录先由管理员配置，不提供普通账号BYOK。任务绑定owner/固定版本/模型/预设/地址/语言/同意，停用、非owner任务和配置变化被拒绝；contract SHA256 `eff87d96` | GitHub Release不等于production；生产、Android设备矩阵或完整结果UI未完成；模型配置owner/BYOK自助不存在 |
| Android本机笔记基础 | [PR #39](https://github.com/2746267826/pim-android/pull/39)与祖先#34/#36/#38/#42已合并；当前head[CI Gate](https://github.com/2746267826/pim-android/actions/runs/40514532620)通过。录音/通知/波形、Markdown与照片/向量手写、固定材料/重试/导出及本机列表/记录页已有实现；设备证据为原修订1866 JVM、23 connected及两组大字号各4项 | 富文本#40、OPPO/长时验收、正式产品入口、上传或完整MVP已完成 |
| 客户端唯一契约对齐 | Android #44与Windows #35均已全绿合并，按既有唯一脚本统一到API `5da39cc`的315路径；本批Android实际接入四个材料operation并生成所需类型 | 仅同步契约不能证明上传或ASR界面可用 |
| Android离线富文本及输入恢复 | [#40](https://github.com/2746267826/pim-android/pull/40)合并到 `50e6ac8d23ef`；离线Tiptap/ProseMirror、原生格式栏和精确光标；串行提交/物理视图交接/显式原文核对已接通，核对状态可重建。当前1870 JVM、30/13/13设备矩阵及[必需CI](https://github.com/2746267826/pim-android/actions/runs/40628144209)通过；原修订44f1cd3的26/27失败独立保留 | 完整全局撤销、所有长文/大量块性能、OPPO或完整MVP均已验收 |
| Android正式入口与绑定存储上传 | #48准确head `29432b5` 的 PR CI 与合并后 master CI 均成功，合并到 `2debf569`；本机库/搜索、四主导航/宽窗口侧栏、固定版本/明确同意/进度、原账号和服务器绑定、分块续作/暂停/明确恢复及未知回执核对。1950本地JVM、三窗口80项设备记录保留 | 双产物/修订/跨端冲突网络 UI、真实进程重启/OEM/课堂或完整MVP已完成；v0.35 Android derived 本地快照保留历史，v0.36 已推进到 Draft PR #54 |
| Android客户端GitHub Release | v0.33 时点 Release[android-v26.10.1123](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1123)→`93d93437`；P0 Release`android-v26.10.1116`、bot #49 Release`android-v26.10.1112`→`e629febe`与历史#48 Release`android-v26.10.1110`→`2debf569`保留；当前 bot Release见 v0.35，Draft #54不是Release | API生产部署、完整笔记云流程、ASR产品UI或完整MVP验收已完成 |

**进行中（仍需编码和回归）**

| 范围 | 当前进度 | 未完成与下一证据 |
| --- | --- | --- |
| 安卓产品界面补全 | #48实现正式导航/本机库与搜索、材料/上传状态和同意；v0.37 最终 test head `a8507365`，UI/修订/再生与 task detail 刷新已接，App2008/Repo11/Core69 全绿且计数不求和，normal/conflict/unknown/narrow 四链已分轮次 PASS | v0.35/v0.36 失败历史保留；a850 exact Gate、merge/master CI/Release 未完成，不得写成同一 Test APK 4/4、完整设备矩阵 PASS 或已对 Android 开放 |
| 编辑器体验余项 | 本轮#41的首次结束回归、真实渲染终止/核对继续、24叶块滚动及独立延迟交接已验收；已合并#40 | 全局撤销、极长文/大量块性能、所有OEM输入仍未验收；64叶块+手动挂起提交探索曾无法稳定空闲，失败保留，不改标为性能通过 |
| AI及跨端业务衔接 | 文本/视觉/单片ASR、服务端持久分段任务及普通账号API授权已合并；#48接入安卓存储上传队列；Android ASR PR#51已MERGED`93d93437`，head`8b1d091`/sha`67f0`，CI40874958748/master40877083407全SUCCESS，Release1123；QA FINAL33/33PASS0FAIL；Source组件3为同源R5另记。API/Web 双产物/修订保护已随 PR#532 merge、CI40907900018/40994288275与Release`api-v26.10.1011`交付，最终 Web `c915835a...` 真实受控复验 PASS；Android derived Draft PR #54/CI 41004036926 已建立 | Android duplicate-task P1、新 head 完整 CI、重验设备 E2E、P2逐图receipt、真实质量及完整跨端访问仍需处理 |
| 现有业务并行阻塞 | [API #525](https://github.com/2746267826/pim-api/issues/525) 仍 OPEN：手机 40 小时批次 `block_id` 超长、服务端 pending 空回放、客户端无退避重传，约 64GB；issue 记录 303 条历史 pending。API PR#529已普通MERGED到`c5ff06fe`，PR CI 40838578113（6150/15/0）、master 40842715856、Release`api-v26.10.997`均SUCCESS；local Mobile68/middleware+shape46/真PG8 PASS，Swagger匹配`eff87d96`。Android PR#50 source`8c55dc15`/run40845315327 required CI全SUCCESS，3项P1源码修复静态review关闭，并已普通MERGED到`235f1203`；组件检查与真实upgrade仍NOT-VERIFIED。旧`089391ba`绿CI仅历史；local focused app290/QueuedUpload4/coreNetwork41 PASS（可能重叠，不求和），5k stress Windows JBR21 C2 native crash历史NOT-VERIFIED，501 max1单测plain PASS | API/Android merge均未关闭#525；组件测试/合并不等于真实升级；PG已停、生产未验证。v0.35 derived 交付不关闭任何后续验收 |

**仅待目标设备的验收项**：OPPO Find X9 Pro/Pad 4 Pro的真实声学、笔/掌触、100/180分钟及续段、锁屏/切应用、省电/温度与异常资源场景。这些设备证据仍缺，但不覆盖上面需要编码的缺口，也不阻止已验证基础合入主干。正式发布仍需两部分全部满足。

**马上要做（MVP主线，按序）**

| 顺序 | 工作 | 完成标准 |
| --- | --- | --- |
| 1 | 普通账号 ASR 权限、固定版本范围与实际模型目标确认 | API #528 已合并（历史 Release `api-v26.10.995`）；P0 API #529 PR/master CI 与后续 API PR#532/Release `api-v26.10.1011` 成功，production 未验证。待 Android functional 客户端正式消费证据；仅 owner/获授权账号可处理自己的固定版本；模型/预设/地址/语言/同意可追溯；越权、停用、目标变化拒绝；上传不自动收费 |
| 2 | 按 API 唯一契约再生成 Android | 走仓库既有导出/生成流程，不手写类型；当前PR#51已普通合并bot contract，Android bot #49 merge `e629febe`、Windows bot #37 merge `94384c9e`；最终整合bot官方generated commit，不依赖本机raw下载；记录API/client/bot准确提交 |
| 3 | Android 固定版本逐段任务与结果 UI | 当前PR#51已MERGED`93d93437`，exact head`8b1d091`/R8 sha`67f0`，CI 40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL；Source组件3为同源R5 lineage另记。P2 UI limitation与master CI/release回执仍需保留 |
| 4 | 全部材料完整记录与独立分析 | API/Web 已 merge/release；最终 CI、完整 Web 真实受控 phone/narrow core 与负向路径 PASS；Android 本地 App/Core 与 Draft PR CI 已建立，但真实正常 E2E 因重复 task P1 FAIL，未验收 |
| 5 | 人工修订、补录与重生成保护 | API/Web 已 merge/release；CAS 双留、late-success、dirty 刷新/切候选等真实受控负向 PASS；Android 修订/再生代码已进 Draft PR，但 P1 修复、新 APK、真实 E2E 与跨端并发使用验收未完成 |
| 6 | 完整 UI、目标设备与签名发布验收 | 组件/安全/完整云流程及 OPPO/真实课堂门槛通过；后台崩溃、Doze/OEM/LAN、100/180分钟与输入/资源证据单独保留 |

目标设备验证与上述编码并行进行，但各步骤没有自己的准确证据时不能互相销项。API #525 是现有手机同步业务的独立高优先级缺陷，不占上表 1-6 的完成度，也不由笔记上传批次修复。

**排序靠后（保留范围，非默默删除）**

| 类别 | 后续内容 | 边界 |
| --- | --- | --- |
| 后续平台必做 | Windows主动录音/混合笔记；Web新笔记访问编辑与更完整跨端体验 | 平台批次分别验收，不能以只读代替Windows记录；底层版本/双方材料保全仍是当前MVP要求 |
| 智能扩展 | 任务建议经用户修改/确认后进入现有任务模块 | 不自动创建任务，保持M4与幂等要求 |
| 全业务迁移 | MIG-01至MIG-13：今日/工作台，日历/任务，确认/提醒/习惯/报告/数据中心，快速记录/文件，采集查询，手机/位置，知识库，设备，账号安全，各类设置，诊断/回收站，入口兼容，客户端生命周期 | 全部13项仍在第5节，不以笔记可用代替迁移完成 |
| 后续完善与运维 | 容量管理、普通历史字节物理回收/定时扫描、更多部署与备份恢复工具 | 当前限制须明示；影响本期数据保全或使用的缺口应在发布前前移解决，不能把SAFE-01整体后移 |
| 可选/范围未定 | 实时转写、笔记速览、HTML/图片等更多导出 | 非近期必需、不阻塞MVP；速览/导出仍须确认具体范围，不擅自升格为首版承诺 |
| 非当前建设目标 | 大型独立AI网关平台、Dify/Agent框架/RAG等 | 有实际需要再评估，不因开源库存在就建平台；不把候选当成未来必做功能 |

## 1. 最终产品方向

PIM 从“Web 业务界面加采集器/壳”逐步发展为“原生客户端为主要体验，Web 保留，采集能力内置”的多端产品。笔记是这次演进的首个完整业务闭环，不需要等现有功能全部原生化后才交付。

用户在手机、电脑和 Web 上查看、编辑同一套笔记。笔记以普通自上而下的 Markdown 文档为正文，可插入照片、可再次编辑的手写卡片，以及多个录音；手写卡片通过右下角拖拽柄自由调整宽高，不用滑块，不是整页无限画布，基础手写随首个安卓可用版本交付。安卓手机是主要录音设备，正常锁屏、切换应用时仍应持续录音。记录过程以离线、本地保存为主，编辑区是主页面，录音状态、时长与实时波形放在应用内左下角的紧凑悬浮条，不用占据大块正文空间；应用不在前台时通过系统通知展示状态。实时转写不是必须能力，保留为不紧急的后续可选项。手动结束本次记录后，整体上传本次笔记及录音到服务器，逐段正式转写，再结合全部录音转写、手动笔记及图片生成一份完整记录和一份独立分析；未来还能生成经用户确认后加入任务模块的任务建议。

“分阶段”只改变交付顺序，不自动缩小最终目标。手写、Windows 记录、Web、多端编辑、任务提取、现有业务原生化都必须保留在路线图中，不能因为首版不做就从计划里消失。实时转写以“后续可选、非必需”状态保留，不视为首版或整个主流程的完成条件。用户接受初期仅安卓记录，但明确要求未来支持电脑记录，不能把电脑端只读访问当作已经兑现这一目标。

## 2. 已确认与待确认

### 用户明确提出的目标

- 手机、电脑、Web 都能查看和编辑笔记。
- Markdown 正文，优先复用成熟编辑器，不从零造编辑器。
- 课堂/会议开始时新建条目并开始录音；记录时编辑区占主要空间，录音采用应用内左下角紧凑悬浮条，不再常驻大卡片。
- 可以边录音边手动记笔记，拍照直接插入笔记。
- 可编辑的嵌入式手写卡片随首个安卓可用版本一起交付；首批安卓目标设备包含 OPPO Find X9 Pro 手机和 OPPO Pad 4 Pro 平板，具体笔输入需实测；Windows 等后续输入适配仍保留。
- 用户已确认：可拉伸的是嵌在正文内的手写卡片，正文仍是普通自上而下的 Markdown 文档，不是整页无限画布。
- 用户已确认尺寸交互：拖动手写卡片右下角的拖拽柄，直接决定宽高，不用高度滑块或预设尺寸档位。
- 一篇笔记容纳一个或多个录音，手动开始和结束。
- 典型课堂约 100 分钟；单段录音上限 3 小时，满时保存当前段并自动接续同篇下一段，同篇多段累计不另设固定业务时长上限。用户已接受自动续段及暂停不计入录音时长的建议；具体控件和接续连续性仍需验证。
- 安卓是主要录音设备，必须考虑锁屏和切换应用。
- 用户已澄清：实时转写准确率体验较差，不是必须能力，也不紧急；只作后续可选项，不阻塞可靠录音和云端正式转写的交付。
- 应用不在前台时，通过系统通知展示录音当前状态。
- 录音时显示实时波形，帮助用户直观确认是否有音频输入；波形应来自实际采集信号，不是装饰动画。
- 记录时不要求实时联网，结束后整体上传笔记与录音；服务器调用 ASR 正式转写，每段录音可单独查看其文字。
- 分析时使用本次全部录音的正式转写和手动笔记；手写部分以图片形式交给 AI，照片也应作为笔记材料纳入，具体图片处理能力需验证。
- 输出一份完整记录及一份独立分析（包含总结）；原始材料和逐段转写仍可查看，笔记处理后仍能编辑。
- 用户已接受提交与修改六条规则：本地保存后后台提交，处理期间可继续编辑，结束后可同篇补录，完整记录/分析可人工修订且不被重生成覆盖，失败按步骤重试，多端冲突保留双方完整材料；具体规则见下文。
- 初期可以仅安卓端记录，未来必须增加电脑端记录，而非永远只允许电脑查看。
- 后续提取任务建议，用户可修改、确认后加入现有任务板块。
- 逐步将现有 WebUI 的业务能力迁入原生客户端，并将采集/守护能力集成在客户端产品内。
- 可以分批交付，但完整路线图需要持续保留与维护。
- 延续 PIM 蓝色基调，取消记录页常驻“PIM 笔记”品牌栏；整次结束操作在应用内右上角悬浮。所有颜色和基础组件统一定义、通过引用复用，不在各业务页面复制样式或散写色号。

### 已接受的架构方向

- 演进现有 Android、Windows 客户端，复用现有后端、认证与业务契约，不同时重写整个后端。
- 一个产品入口不等于一个进程；后台采集、录音、上传不依赖业务窗口一直打开。
- Android 使用适当的原生前台录音服务承载用户主动开始的录音；同步任务不承担持续占用麦克风的职责。
- 原生客户端、录音、文件存储、系统通知和后台任务保留；正文采用随客户端离线打包、由 PIM 独立维护的成熟开源编辑核心，以 Tiptap/ProseMirror 为实施方向。不是回到远程 WebUI 壳，不移植 OPPO 混淆业务 H5；具体职责和验证边界见下文“原生宿主与离线正文核心决议”。
- 录音先可靠保存到本地；网络、上传、AI 处理故障不能阻塞正在进行的录音。
- 本次记录的录音、文字和图片先在记录设备本地保存，结束后再提交；不把记录中实时跨端协同作为第一版前提。
- 原始笔记、笔迹和音频，与转写、总结、任务建议分开保存。重新处理不得静默覆盖用户内容。
- 多端保存校验所基于的版本，冲突时保留服务端版本和本地完整草稿，不静默后写覆盖；首版不默认引入实时协同或 AI 自动合并。
- 不逐页照抄 Web 的布局，而是迁移业务能力，采用符合平台的交互。
- 用户已同意先形成面向整个 PIM 的薄视觉基础和笔记关键页样板，再立即进入真实原生功能；不先建设完整组件库，不等全功能完成再换皮。蓝色基调、紧凑页首及主题/基础组件复用已确认，其他字体与布局参数继续细化。

### 尚未形成决议

- 已知手机为 OPPO Find X9 Pro，安卓平板为 OPPO Pad 4 Pro；具体 Android/ColorOS 版本、手写笔型号及输入行为待真机验证时记录。是否需要 iOS/iPadOS 客户端仍未确定。
- 笔记组织方式、正文工具栏与源码/阅读入口、手写卡片的插入/编辑手势、最小尺寸和缩小时的笔迹边界处理。成熟离线正文核心的方向、右下角自由拖宽高及放大只增加空白而不拉伸笔迹已经确认，不再作为开放选项。
- 单段暂停/停止、补录与明确更新整理的详细控件和提示。整次结束的右上角悬浮位置、结束后可同篇补录、手动触发更新，以及自动续段/暂停计时均已确认，不再重复询问这些规则。
- 完整记录与分析的具体结构，以及人工修订/新生成稿的比较和选用界面；是否需要笔迹/正文与音频时间对齐。完整记录和分析允许人工修改且不被重生成覆盖已确认。
- 后续若实施可选本地实时转写，再确认目标机型、可接受延迟/效果、模型预下载，以及不支持/过热/失败时的体验；不作为近期前置决策。
- 后台通知的状态字段、锁屏隐私与权限告知、是否增加快捷操作；波形位置与刷新策略需在交互设计中细化。
- 音频/附件保留和物理垃圾回收策略、导出范围、模型与供应商选择。服务器内置盘已获用户接受，存储位置与默认规则见下文。
- 冲突副本的呈现、差异比较、用户解决冲突的具体交互，以及历史保留策略；固定提交版本、保存时版本校验和保留双方完整材料已确认，API/数据结构仍待设计。
- Windows/Web 各能力的交付批次和平台输入适配；基础手写随首个安卓可用版本交付已经确认，不再作为可默默延期的可选项。
- 演进现有 Android Kotlin/Compose 和 Windows .NET/WPF 宿主，正文走已确认的离线开源编辑核心方向；具体上游版本、扩展组合、桥接和手写嵌入方式需工程验证，不代表要更换整个客户端或 Web 框架。
- 完整原生迁移的功能顺序、各端差异，以及每阶段的具体排期；薄视觉基础与真实功能相互验证的方法已获同意。蓝色和统一主题引用不再待定，具体字体、主题交付范围、布局与平台组件细节仍需收敛。

### 本轮明确的端到端流程

1. 上课/会议开始，在安卓端新建条目并开始录音，本地建立记录。
2. 编辑器是主工作区，可输入普通 Markdown、拍照插入图片，并嵌入可编辑手写卡片，用右下角拖拽柄调整宽高。应用内左下角的紧凑悬浮录音条显示状态、时长与真实音频波形，不要求实时识别文字。
3. 过程中允许一篇笔记多段录音，记录不依赖实时网络。退到后台时通过系统通知展示状态；锁屏、切应用以及打开相机时须验证录音与本地内容的可靠性。
4. 用户手动结束本次记录，先确保本地保存，再上传笔记、图片/笔迹材料和所有录音。离线时显示待上传，不把无网络误报成已完成云处理。
5. 服务器在本次输入材料齐备后调用 ASR，保留每段录音对应的正式转写。某段失败应标明缺口并可重试，不将只处理一部分的结果伪装成完整记录。
6. 全部录音的正式转写与手动 Markdown、照片、手写渲染图片共同参与 AI 整理；不能只分析最后一段或当前选中段。
7. 客户端能查看原始笔记、逐段录音和正式转写，以及生成的完整记录、独立分析。后续编辑不被 AI 结果覆盖。

以下说明该流程的内容层次，其中原材料/结果分离及后文六条产品规则已确认，但不是已批准的 API 或数据表设计：

| 内容层 | 保存/呈现内容 | 边界 |
| --- | --- | --- |
| 原始材料 | Markdown、照片原图、可编辑笔迹、每段原始录音 | 是用户记录，不用 AI 生成内容替换；笔迹不能仅存一张图 |
| 临时实时文字（后续可选） | 若未来启用，保存设备本地识别结果 | 非主流程必需；与服务器正式转写区分，不能替代录音上传 |
| 正式转写 | 每段录音独立的 ASR 结果 | 单段可查，全篇分析覆盖全部段；是否带说话人/时间戳另行确认 |
| 完整记录 | 根据全部材料整理的可读记录 | 不是只给几句话的摘要，也不是冒充未经改写的逐字稿；具体详略待定 |
| 独立分析 | 总结、归纳等分析结果，未来可关联任务建议 | 与完整记录区分，不混淆原始陈述和 AI 推断；格式待讨论 |

将“停止当前录音”和“结束本次记录并提交处理”区分：停止某段后仍能写笔记或录下一段，结束整次记录才提交完整材料。结束后允许同篇补录并明确触发更新已确认，具体控件仍需设计。

整体上传是产品层面的“一次提交完整记录”，不强制把所有文件压成一个巨大请求。实现时可分文件/分片重试并核对完整性，避免一段大音频失败就重传全部照片和录音；这些传输细节不应增加用户操作负担。

提交时固定一份输入版本，生成结果标明基于哪个版本。处理期间或事后修改原笔记，旧结果保留并提示输入已变化，需要时明确重新分析。该规则已获用户接受；离线记录减少了录制期间的协同问题，但不能自动消除事后覆盖、重复提交和处理时材料变化的问题。

后续多端编辑采用版本校验：基于旧版本保存时提示冲突、保留两份，禁止静默后写覆盖；首版不默认引入实时协作或自动合并。这一产品规则已获用户接受，具体数据及交互实现尚未完成。

### 已确认的提交、处理与事后修改六条规则

用户本轮明确接受上一轮六条规则，展开见 [PIM笔记首版范围与交互草案](native-notes-first-release.md) 第 5 节。这是产品行为决议，不等于全部技术细节或尚未讨论的范围已经批准：

- 结束后先完成当前录音段和本地材料保存，再后台提交固定版本；离线待上传，可离开页面。本地保存、上传、转写、完整记录和分析分别展示状态，服务端核对材料齐全后正式处理。
- 处理期间可继续编辑，不锁住原笔记；修改形成同篇新材料版本，不改变排队或处理中的旧版。结果标明输入版本，来源变化时提示，由用户明确触发更新，不每改一个字就自动调用 AI。
- 结束后允许同篇补录，新段不覆盖旧段。更新整理覆盖当前版本的全部材料，未变音频且仍有效的转写可复用，不能只分析新增段。
- 完整记录和分析可手动编辑，区分生成原稿、用户修订和再次生成的结果；新输出不覆盖人工修订，结果修改不自动反写原笔记。
- 失败按具体步骤重试并保留可用结果，缺材料或只完成一类产物不标全篇完成；重复提交不重复创建任务，重试不串入其他版本，旧任务晚返回不能冒充新版结果。
- 多端保存校验版本；冲突保留本地完整草稿与服务端版本，包含正文、块顺序/尺寸、笔迹及可读取附件关联，不只救出纯文本；首版不默认自动合并或让 AI 决定保留哪份。

相关需求仍由 AI-04、REC-04、SYNC-01/02 覆盖，不因细化行为重新编号，也不宣称服务端已有材料快照、幂等任务或冲突副本能力。音频保留、删除、上传网络策略及模型供应商等边界仍需单独收敛。

### 已接受的视觉与功能推进方法

用户已同意先形成面向整个 PIM 的薄视觉基础，以笔记记录页和结果页验证，再在真实原生功能中持续完善，而不是先做全套组件库或先写完功能再整体换皮。随后确认延续 PIM 蓝色、取消常驻品牌栏、右上角悬浮结束，以及颜色和基础组件统一定义、多处引用；不表示所有外观参数已冻结或原生组件已完成。

- 起步只明确设计原则、语义色/字体层级/间距/圆角/边框、状态表达和手机/平板布局规则；按已确认的蓝色和操作位置收敛关键页，不预先为全量迁移设计所有页面。
- 通用控件优先复用平台成熟组件，统一主题和必要约束；首批业务组件只覆盖紧凑录音条、手写卡片框架、材料插入、提交/失败状态和来源版本提示，编辑器与笔迹仍需真实组件验证。
- 颜色、常用字体/间距/圆角/触控目标等集中在主题资源中定义；页面通过语义引用，按钮、输入区、材料边界、状态提示等复用共同基础定义与必要变体。禁止业务页面散写色号或复制整套控件样式，调整一个资源应同步影响所有引用处；平台资源映射见 [PIM视觉基础与笔记样板](native-notes-visual-foundation.md) 第 3.1 节。
- Android/Windows/Web 共享语义及产品识别，不要求共用一套 UI 代码或像素级相同；触控、手写、鼠标/键盘和原生导航分别适配。
- 后台录音、恢复及编辑器风险验证不等待视觉全部定稿。薄视觉基础应用到真实安卓记录切片后再细化，新增共性出现时再提炼组件，不先引入通用设计系统平台或全站换肤工程。
- 视觉阶段退出依据是关键页方向、必要主题参数、少量可组合组件及状态明确，中文长文/键盘/缩放/手机平板布局可读可操作；不是组件数量达到某个数字。预览和模拟数据不能作为真实录音或同步已验收的证据。
- 用户已要求视觉“差不多就行”，统一后继续实施。当前不再扩展静态样板或大型组件库；先把薄主题落到已有安卓页面，再进入真实录音和混合编辑风险验证。

前期代码检视确认 Android 已有 `PimTheme`、`PimSection`，主入口 `PimRootScreen` 使用 `PimTheme`；Windows 已有 `Styles/Theme.xaml`。本轮已获取 Android/API 最新 `origin/master`，在既有安卓主题内实施薄基础，具体基线见交付台账；Windows 未改动。这不是已经完成跨端设计系统的证据，后续切片仍须重新核验最新基线。

已建立 [PIM视觉基础与笔记样板](native-notes-visual-foundation.md)，记录视觉参数、首批组件边界与原生实施退出条件；独立视觉样板 `pim-notes-visual-foundation.html` 不替换此前流程草图。“清晰文档”提供干净文档和宽屏材料侧栏对照，最近样板偏好为“安静纸面”的阅读布局；共同延续已确认的蓝色，不再要求重新选风格。两者都已取消常驻品牌行，结束在右上角悬浮，保留左下角录音和右下角拖拽，并引用同一主题和基础控件定义。手机/平板、深浅外观和十类状态均为浏览器合成样板，不证明原生笔记 UI、真实录音或云处理已实现；当前原生主题切片见交付台账，其他字体和布局细节在真实功能中完善。

### 目标设备与录音时长边界

用户已提供 OPPO Find X9 Pro 和 OPPO Pad 4 Pro，典型课堂约 100 分钟。两台设备纳入 M0/M1 的首批安卓实测目标；平板基础记录和手写不能因旧版泛称“平板后续适配”而被推迟至 M3。它们尚未经过本项目的录音或手写验收，具体系统版本和笔输入在实测时记录，不凭型号宣称兼容。

询问型号主要为确定真机可靠性和输入验证对象，而非按型号写死布局：需验证锁屏/切应用后的采集、实际系统的后台和省电限制、通知权限及可见性，长录音的电量/温度/写盘，以及录音中拍照；平板还需验证手写与掌触、正文滚动、尺寸拖拽及横竖屏/分屏的冲突。布局应按实际可用空间自适应。

| 层级 | 当前边界 | 状态与说明 |
| --- | --- | --- |
| 一段用户可见录音 | 上限 3 小时，满时保存并自动接续同篇下一段 | 用户已接受；暂停不计入录音时长，不是 Android 或 ASR 的通用硬限制；接续需实测 |
| 一篇笔记的全部录音 | 多段累计，不另设固定业务时长上限 | 可超过 3 小时；不宣称存储、设备或服务资源物理无限 |
| 内部文件、上传或 ASR 处理片段 | 可因可靠保存、传输和供应商限制进一步分片 | 仍映射回用户原来的录音段，不能把处理分片误当成新的用户录音或漏掉材料 |

用户本轮已接受以下录音行为：

- 正在录音时达到 3 小时，保存当前段并自动接续同篇的下一段，避免课堂中突然停止等待用户操作；整次记录仍由用户手动结束。接续是否有间隙及其影响必须实测，不承诺零丢帧。
- 若提供暂停功能，3 小时按实际采集时长计算，不计暂停；恢复后继续累计当前段，不因暂停经过的墙钟时间触发新段。

时长/段号的具体展示仍需设计。自动接续失败时必须显示中断、保留已经保存的音频，不继续用正常计时或波形掩盖故障。单段的本地可靠保存不应等到 3 小时结束才发生；内部落盘与恢复边界在 M0 验证，不把单段产品上限当作唯一文件保存周期。

无固定笔记总时长上限不等于忽略资源约束。实现时需明确本地空间、上传大小/用量、服务端存储与处理额度，提前提示可预见的不足；异常时保留已保存材料并如实标明中断或未处理部分，不静默截断、不以部分结果冒充全篇完成。具体资源配额仍待确定。

### 实时波形与后台通知

用户确认的目标是可直观判断录音状态；以下实现及交互细节是建议，仍需真机验证：

- 应用内左下角悬浮录音条显示当前段的状态、已录时长和小型实时波形，保持紧凑，不夺走主编辑区；不依赖 ASR 或网络即可工作。用户已确定该位置和紧凑方向，具体尺寸和展开操作仍需原生验证。
- 波形使用实际采集音频的幅度/能量数据，复用录音采集链路，不另开第二路麦克风。避免用随机动画、仅计时或仅判断服务存在来假装采集正常。
- 波形只证明存在采集信号，不保证声音可懂、文件已经落盘或整段音频完整。录音状态还需结合采集异常和实际写入情况；录音与保存失败必须如实显示，不能仍呈现正常录音。
- 安静时波形变平是正常情况，不应直接误报为录音中断。暂停（若支持）、已停止和异常中断分别显示，不继续播放看似正常的波形。
- Android 使用与录音前台服务配套的持续状态通知；建议开始录音即建立，不等界面进入后台才依赖界面代码补发，返回前台后也保持状态一致。
- 后台通知建议优先显示录音状态、时长和返回记录入口；普通更新不反复弹出横幅、发声或振动，不要求悬浮窗权限。左下角悬浮条是应用内控件，不是盖在其他应用上的系统悬浮窗；实时波形主要放在应用内，不承诺通知栏在各厂商设备上高频播放动态波形。
- 系统通知权限、通知渠道及锁屏设置会影响可见性，不能承诺所有设备始终弹出或显示在锁屏；开始前应说明相关限制。建议锁屏不展示笔记正文或转写等敏感内容。
- 波形渲染与通知刷新不决定麦克风及文件写入是否继续；界面隐藏后停止无意义的高频绘制，避免为了反馈增加后台耗电。

### 截图借鉴边界

用户提供 14 张实际使用截图作为产品参考。可观察到的相关交互包括：笔记列表/笔记库、新建入口、编辑区配录音控制条、独立转写面板、拍照插入入口、结束确认、处理后的长文与目录。

- 借鉴工作层级：记录时编辑优先，录音与转写独立；结束后进入整理、回看，而不是录音波形一直占据主页面。
- 借鉴原始材料与生成结果可访问的关系，不依据截图推断参考应用是否离线、后台可靠性如何、或采用了何种数据/模型架构。
- 信息速览图、跨笔记“灵光一刻”/金句卡、AI 问答、分享导出、文件夹/标签体系、扫描文档、其他内容导入、说话人识别等，先保留为参考候选，不自动扩张为本轮确认需求。
- 截图内容不自动成为 PIM 规格；用户已经接受单段三小时、到点自动续段和暂停不计时，作为 PIM 当前录音规则，而非笔记累计上限。品牌仍为 PIM；参考包中的字体和动画已于 2026-10-26 经用户确认可用，按统一主题选用，不等于照搬品牌或新增参考产品的全部功能。原生控件与接续连续性仍需验证。

### 原生宿主与离线正文核心决议

2026-10-26 用户接受参考资料利用建议，并明确确认资料中的动画和字体可用。以下作为已确定的方向，不再反复讨论“是否允许本地 Web 编辑核心”；工程验证未通过时记录具体失败及替代方案，不悄悄缩减首版或改回另一套架构。

| 层次 | 已确定的职责 | 不承担的职责 |
| --- | --- | --- |
| 原生宿主 | 现有客户端导航、认证、相机/照片、系统权限、主题、键盘和平台输入适配 | 不把整个产品换成远程网页，不要求每个正文控件都从零原生实现 |
| 原生录音与后台层 | 麦克风、持续通知、实际信号波形、本地落盘、恢复、材料固定及后台任务 | 不依赖编辑器窗口、渲染进程或 JavaScript 定时器持续存活，不因编辑核替换而重写已有可靠链路 |
| 离线正文核心 | 从上游开源组件构建自己的 Tiptap/ProseMirror 编辑模块，负责正文输入、选择、撤销和材料节点交互；随客户端发布，断网可用 | 不直接剪出 OPPO 的混淆 H5，不加载远程业务页/CDN，不接入厂商账号、私有桥接或云服务 |
| 文档与材料层 | 保留版本化混合文档、稳定块身份/顺序/尺寸、Markdown 内容、向量笔迹、原照片和全部录音；编辑核心通过受控接口读写 | 不把 HTML 截图或某个引擎的不可迁移私有引用作为唯一原稿，不把派生手写图代替向量 |
| 云端与跨端 | 沿用 PIM 后端及唯一 OpenAPI；逐步共享文档语义、兼容夹具及可复用正文模块 | 不从对方包还原或依赖其 ASR/模型/云接口，不把同一引擎等同于 Windows/Web 已交付 |

PIM Web 已使用 React 和 Tiptap 依赖，可复用经核对的上游能力；不因参考产品使用某个框架就改成 Vue，也不在本轮凭空锁定版本号。上游准确版本、许可证/声明和实际采用的扩展随实现记录，不能把整包许可或第三方收费扩展视作已经自动获得。

正文与原生层之间只开放版本化、校验过的消息及按材料 ID 授权的文件访问；凭据不进入正文脚本，任意网页/跳转不能获得同一桥接权限，模型生成的 HTML/脚本不直接执行。页面显示“已保存”必须对应原生持久化确认；页面重建不能停止录音，也不能把未落盘内容伪装成已恢复。

更换编辑核心之前，必须在真实应用内验证中文输入法/选区/撤销、长文与键盘、照片和可编辑手写、正文滚动与卡片拖拽、渲染重建和保存恢复、旧笔记迁移及 Markdown/未知扩展往返。验证沿用现有录音与材料层，失败时保留旧原稿和附件；未知的新格式可只读或明确不支持，禁止静默降级后覆盖。具体用例见 [首版范围](native-notes-first-release.md) 第 3 节。这些是待执行的工程验证，不是本轮已经通过的测试。

字体和动画按用户确认进入可用素材范围，选用规则、出处与哈希登记要求见 [视觉规范](native-notes-visual-foundation.md) 第 3.2 节；不再写成“授权待确认”。该确认不扩张为 OPPO Java/smali、混淆 H5、图标、原生库、模型或云服务全部可复制。此前决议阶段只更新规则；现已恢复开发，但本轮仍未复制素材进产品或公开 Git，也未执行包内程序。

### 分析结果与速览图参考

用户进一步提供了分析结果长截图，以及其中“笔记速览图”的 HTML 导出。前期已逐段查看长图，并读取 HTML 静态内容及内嵌 `content-spec` JSON，没有执行附件脚本。私人原材料与详细拆解不纳入产品仓库；下文只保留产品理解和参考边界。

由此得到的产品理解如下，作为参考方向而非已冻结的完整验收规格：

- 分析不只是短摘要：应能围绕主题重整长讨论，提炼整体主线；保留关键数据、方法对比、争议、适用边界与下一步行动，按内容选用标题、段落、列表、表格和引用。
- 完整记录重在忠实、详细整理；分析重在结构、主线和问题提炼。参考样例将两者混排，PIM 需分清来源及结果，但不强制做成重复的两篇长文或固定数量的页面。
- 对话中已经提出的质疑、结论、待办，与 AI 新推断要区分。引用不能编造，任务负责人/日期/优先级等缺失时不能补成貌似已确认的会议决定。
- 速览图是完整内容的浓缩阅读视图：结论标题、关键指标、主题卡片、比较矩阵、风险/边界和行动列表。HTML 导出有结构化内容与排版，并非只是一张位图；是否做速览及 HTML/图片等导出仍需确认，不自动进入首版。
- 建议结构化内容与应用排版分离；长文、表格和速览共用同一批事实和输入版本，不让模型分别编造互不一致的数字。已接受的离线正文核心不承担录音生命周期，不是退回远程 WebUI 录音壳；不能直接执行模型生成的任意脚本。
- 专业结论不因出现在参考产品结果里就被认定正确。未获得原始录音、转写和笔记，不能验证该样例的保真性；可见的数字/措辞不一致提示我们需要事实一致性和来源核查，而不是只模仿好看的排版。

该样例使分析输出有了具体参照，不再要求用户抽象重复定义。普通课堂、一般会议仍需适应各自材料，不固定套用科研风险栏目，不强行凑指标、对比或待办。后续以页面草图和具体样例确认首版范围。

### 交互草图与首版范围建议

已建立 [PIM笔记首版范围与交互草案](native-notes-first-release.md)，配合可点击草图讨论记录页、后台通知、整次结束、离线待上传、逐段处理/失败重试，以及原笔记/录音文字/完整记录/分析的切换。用户已从早期位置草图收敛到应用内左下角紧凑悬浮录音条，并要求手写卡片采用右下角直接拖拽调整宽高；草图不再把顶部段落卡片或大录音面板作为当前候选。

- 建议首个对外可用版本是 M1 与 M2 的安卓纵向闭环：离线记录、正文、照片、多段可靠录音、真实波形与通知，以及结束后的全部材料上传、逐段 ASR、完整记录和独立分析。不把单独的本地录音里程碑冒充完整产品，也不等待全量原生迁移。
- 用户明确基础可编辑手写卡片与首个安卓可用版本一起交付。因此 M1 纳入基础笔迹编辑、保存和右下角尺寸拖拽，M2 纳入对应手写图片的云端分析输入，不再等 M3 才做基础手写。M0 仍需验证真实组件及混合文档往返；OPPO Pad 4 Pro 纳入首批安卓记录/手写验证，Windows 等其他平台输入、多端混合编辑和约定导出可在后续阶段扩展。
- 草图中的照片、笔迹、波形、时长、网络和处理结果全部模拟，没有真实麦克风、相机、上传、ASR 或模型调用。交互检查不能作为 REC-02/06/07 等原生能力完成的证据。
- 文本框加材料示意没有验证真正的块式编辑、任意位置插入、自由尺寸手势、笔输入或 Markdown 往返。版本提示、明确重处理和冲突保全的产品规则现已获接受，但草图仍不表示真实快照、版本校验或冲突恢复已经完成。
- 速览、导出、任务写入和可选实时转写未因出现在草图或参考应用中被自动纳入首版。草图场景枚举也不是 API/后台状态机规格。

基础手写首版、录音/尺寸交互、设备/时长及六条提交修改规则均已明确。蓝色、无品牌栏、右上悬浮结束和资源复用不再反复征询；已有真实本地预览仍不等于完整首版。恢复后直接在真实客户端接入离线正文并补云端闭环，不再以静态样板或大型组件库作为前置。

### 当前交付台账

首批实施从实际获取的 Android `origin/master`（`d9b339cea95c`）和 API `origin/master`（`eb68d6e35424`）开始，不把旧本地副本当作最新基线。

用户授权合并后，文档 PR #506 和 Android PR #32 已于 2026-10-25 合并。录音切片重新获取最新基线：Android `9941ce411740`、API `50d48bc86273`。只把实际通过并合并的范围标为交付，不把内核合并等同于首版完成。

以下基线段落是各切片建立时的历史记录；本轮已合并状态以上方“本轮状态与执行入口”为准。

混合文档切片再次获取 `origin/master`，两仓 master 仍为上述提交。由于录音 [PR #34](https://github.com/2746267826/pim-android/pull/34) 和跟踪文档 [PR #507](https://github.com/2746267826/pim-api/pull/507) 尚未合并，本批分别基于它们的 head `cf09bbc26119` / `be98a2b26cad` 建立依赖分支，不把未合并代码冒充 master，也不自动合并前一批。PR 基线应明确标示依赖。

材料版本切片再次获取 `origin/master`，基线未变。混合笔记 [PR #36](https://github.com/2746267826/pim-android/pull/36) (`ec59b091ea1e`) 和文档 [PR #508](https://github.com/2746267826/pim-api/pull/508) (`76c4f98d7d9d`) 仍开放且 CI Gate 通过；本批显式建立在它们之上。没有将旧的两份 PR 合并授权扩展为所有未来功能自动合并。

本轮决议更新再次获取远端 master：API 仍为 `50d48bc86273`，Android 仍为 `9941ce411740`；继续在未合并的文档 [PR #509](https://github.com/2746267826/pim-api/pull/509) 上维护同一份规范，保留 #507 → #508 → #509 的依赖。记录页纠偏 [PR #39](https://github.com/2746267826/pim-android/pull/39) 已通过 CI，尚未合并；用户认为新版“看起来还可以”，随后要求暂停开发和研究参考包，不将该反馈扩大为完整产品验收或自动合并授权。

恢复 MVP 开发时再次获取两仓 master，仍为上述基线。[Android PR #40](https://github.com/2746267826/pim-android/pull/40) 基于未合并的 #39 `ba5f639`，现推进到 `44f1cd3`：离线正文、获焦叶块保活、精确光标回归与统一原生滚动；旧失败证据仍保留。[API PR #509](https://github.com/2746267826/pim-api/pull/509) 本轮由文档跟踪扩展为固定材料收包、唯一契约与生成类型的产品改动，须运行完整产品门禁，不能再按 docs-only 看待。双方仍显式继承旧栈，没有自动合并或发布。

| 切片 | 对应需求 | 当前状态 | 不应误报为 |
| --- | --- | --- | --- |
| 总路线图、首版范围与视觉约束入库 | 全部稳定需求编号 | 已交付文档：[PR #506](https://github.com/2746267826/pim-api/pull/506) 已合并，docs-only CI Gate 通过 | 笔记功能已经交付 |
| Android 薄主题与基础组件实际复用 | NATIVE-01、NOTE-05 的视觉前置部分 | 已交付视觉基础：[PR #32](https://github.com/2746267826/pim-android/pull/32) 已合并，1,803 项单测/8 项模拟器测试及必需 CI 通过 | 记录页、后台录音、手写或跨端统一已完成 |
| 可靠录音与本地恢复验证 | REC-01/02/03/06/07、NOTE-05 | [切片 #33](https://github.com/2746267826/pim-android/issues/33)、[PR #34](https://github.com/2746267826/pim-android/pull/34) 已随#39保留历史合并；历史证据：`cf09bbc` 的 1,820 单测、12 隔离模拟器测试及 CI Gate 通过；100/180 分钟 OPPO 真机仍未验收 | 以旧修订证据证明新改动；把 M0 验证页当成缺手写的成品首版 |
| 混合文档和基础手写验证 | NOTE-01/02/04/05、INK-01/02/03/04 | [切片 #35](https://github.com/2746267826/pim-android/issues/35)、PR #36 已随#39保留历史合并；历史证据：`ec59b09` 的 1,843 单测、16 隔离模拟器测试及 CI Gate 通过，含真实 Android 相机/录音管线和笔迹拖拽 | 已完成 OPPO 手写/掌触验收、已提供云处理或完整首版 |
| 本机完整材料版本 | REC-04、SYNC-01/02、AI-04、SAFE-01 的提交前部分 | [切片 #37](https://github.com/2746267826/pim-android/issues/37)、[PR #38](https://github.com/2746267826/pim-android/pull/38) 已随#39保留历史合并；历史证据：`c7cf58d` 的 1,864 app 单测（新增材料 21）、19 隔离模拟器测试、debug/release 构建及 [CI Gate](https://github.com/2746267826/pim-android/actions/runs/40261428087) 通过；固定全部材料/录音、本机后台准备/重试和含音频包导出 | 已上传、服务端接受输入版本、ASR/AI 已开始，或已实现跨端冲突保护 |
| 记录页体验纠偏 | NOTE-01/02/04/05、INK-04 的本地体验 | [PR #39](https://github.com/2746267826/pim-android/pull/39) 已随#39保留历史合并；历史证据：`7f66e66` 的 1,866 JVM/23 connected、debug/release 通过，`ba5f639` 仅改测试查找，320dp/1024dp、1.5 倍字号各 4 项通过；[CI Gate](https://github.com/2746267826/pim-android/actions/runs/40273765181) 成功。正文优先、紧凑浮条、照片/向量手写及本机结束反馈已在真实 Android 页面验证 | 已完成所见即所得编辑核、OPPO 笔/掌触/长时验收、云端闭环或完整首版 |
| 离线正文架构与参考素材 | NOTE-02、INK-01/03/04、NATIVE-01/02、WEB-01 | [#40](https://github.com/2746267826/pim-android/pull/40)已合并，head `749c39c`；Tiptap 3.31.3/ProseMirror自行离线构建，原生材料/schema-v1保留。串行提交、物理实例pending、退场确认与后继初始化屏障、显式原文核对/重建；1870 JVM、30/13/13设备矩阵及[CI](https://github.com/2746267826/pim-android/actions/runs/40628144209)通过 | 全局撤销、长文性能、所有故障/OEM、参考字体/动画或完整MVP已完成 |
| 服务器固定材料收包 | REC-04、SYNC-01/02、AI-04、SAFE-01 的上传前置 | API PR #509 已随 #510 合并；实现鉴权、完整清单、1 MiB 分块续传/摘要、WAV/图片类型核对、固定版本与冲突保全、配额/过期/删除；19 项隔离测试通过，含实际 HTTP 和大于 4 MB 的合成 WAV。新增唯一契约，既有 284 条路径保持不变；完整门禁与证据见 PR。部署/限制见[收包实现说明](../architecture/native-notes-material-upload.md) | Android 已经能上传课堂、ASR/模型已开始、全局分布式存储或完整跨端冲突 UI 已完成 |
| 文件系统内置存储与共享字节层 | REC-04、SYNC-01、SAFE-01 的存储前置及 MIG-04 文件系统 | [PR #510](https://github.com/2746267826/pim-api/pull/510) 已合并；在 API #509 `f2c93960` 之上独立实现 `local` provider，无云盘可直接存文件/旧附件，保留既有云盘默认与引用；普通长文件复用会话分块，笔记新分块共用底层但保留独立版本语义。379 项文件/速记/笔记/HTTP 回归通过；桌面/手机合成接口浏览器验证通过（含上传/预览关闭/失效来源显式切换），完整 CI 证据见本批 PR。默认路径、额度与缺口见[内置存储说明](../architecture/builtin-local-files.md) | 手机本机盘、对外分享、普通历史字节物理垃圾回收、Android 已上传课堂或完整 MVP 已完成 |
| 最小 SDK 文字调用闭环 | AI-05、SAFE-01、MIG-10 的通用设置/记录前置部分 | 本批独立实现管理员模型/默认类型设置、非流式 SDK 调用、实际消息及请求/响应正文、配置版本与统计；取消/超时/未知状态、单次幂等与会话保护有实际 SDK/本机 HTTP 测试，Web 桌面/手机合成接口各 9 状态验证。唯一契约由真实 Swagger 导出；当前 head 完整 CI 与合并见本切片 PR，[实现及缺口](../architecture/ai-text-model-settings.md)持续保留 | 已验收真实供应商、视觉/ASR/流式/旧业务迁移，或笔记全材料处理已完成 |

这里的“进行中”不表示已合并或发布；PR 与测试记录是交付依据。Windows、Web、任务提取及既有业务迁移继续受第 3-5 节约束，不因近期台账较短而消失。

材料版本本批还在专用模拟器关闭 Wi-Fi/移动数据后，完成真实短时录音、旧输入 A / 新稿 B 隔离和系统文件选择器导出；逐文件 SHA-256、相对路径与 WAV 样本数核对通过。冷启动补排使用合成孤立请求，模拟“请求已落盘、任务尚未排入”的中断窗口，不冒充真实掉电或厂商杀进程。模拟器禁用宿主音频，不能证明真实声学质量或 OPPO 100/180 分钟录音可靠性；初轮夹具写入/启动等待问题修正后才完成手工验证，未改产品逻辑掩盖工具失败。

用户随后提出并接受默认内置盘：无需绑定任何云盘，作为文件/附件保底来源。实现为服务器持久盘的正式 provider；显式默认或记住的云盘失效时必须由用户重连或切换，不静默换保存位置。普通文件和笔记共用账号隔离字节层，但版本材料不暴露为可随意删除的普通文件；旧引用和旧笔记分块兼容保留。普通回收站/历史字节物理垃圾回收、容量界面与后台扫描仍未完成。

#510时先验证离线正文和旧稿兼容，再落地服务端收包/内置存储；该历史批次没有Android上传或真实课堂证据。其后SDK选型、文字/视觉/单片与持久分段ASR已分别随#514/#515/#517/#520合并，契约通过#44/#35同步，本批#48接正式入口与账号绑定存储上传。v0.35 已由 API PR#532 合并普通账号全材料双产物/修订保护，同仓 Web 最终 CI 349/349，真实 Test API + loopback SDK 在 `c915835a...` 上完成 phone/narrow core 与负向复验；Android functional 网络/设备仍未完成。schema-v1保留仍不等于旧稿/跨端迁移全部验收。

`a0e4528` 本轮另在真实页面手工查看富文本/向量卡片、键盘/原生格式栏/录音浮条及结束材料反馈。合成笔记录音中，正文获焦但未实质编辑，收起键盘后首次点击结束曾被输入确认边界拒绝，第二次成功固定完整材料；没有跳过校验或据此宣称无故障。另在 **320dp/1.5 倍字号的 9 项回归中，8 项通过、1 项格式栏查找失败**，不能认作该修订的窄屏验收通过。首次结束仍需可靠复现和定位；窄屏问题后续已复现并针对性修复，不能反过来将早期失败归为单纯测试时序。连同渲染器故障核对继续入口，集中跟踪于 [Android 修复 #41](https://github.com/2746267826/pim-android/issues/41)。默认尺寸测试和 CI 成功不覆盖这些失败。全部设备证据来自专用 Android 16 模拟器、禁用宿主音频，不替代 OPPO 真机/真实声学/长时或云端验收。

手工查看 `a0e4528` 的 1024dp/1.5 倍字号横屏页面又发现：键盘弹出后，格式栏可见但正文光标没有被带入缩小后的原生可见区域，早期 9/9 自动测试没有检查这个条件。`d1d0707` 通过受限桥传递光标几何范围，使用原生滚动容器带入可见区域，新增混合笔记录音中测量真实光标坐标的回归。修复工作树的平板 10/10 已通过且源码摘要与提交一致；随后手机整组的照片故障测试在 2.488 秒内点击尚未出现的确认框而失败，`ab08020` 仅为测试补上有界异步等待，不自动重试或绕过输入确认。该修订默认手机 27/27、JVM 1,870、构建与 CI 通过，但窄屏 10 项中仍有格式栏和光标 2 项失败；不能以修测试宣布手工首次结束缺陷已解决。

窄屏继续复现并保存实际坐标/失败截图后，确认键盘/格式栏缩小布局时，活跃 WebView 叶块会被列表回收，且光标滚动早于父容器完成新视口测量。`441b67a` 固定获焦叶块的存活，并在实测视口和下一布局帧后跟随光标，不靠无限等待或重试掩盖问题。该修订默认手机 27/27、窄屏 10/10、JVM/构建与 CI 通过，但平板 10 项中仍有光标 1 项失败，未作为完整验收交付。

核对平板失败时，还发现旧光标测试依赖原生子视图顺序、在输入法真正获焦前设置临时选区。`8f85b74` 加强精确叶块/段落/插入位置断言，针对性平板曾通过，但完整平板回归仍有 1 项光标失败。`cf3059f` 增加布局帧后几何刷新，又暴露界面滚动反馈导致无法稳定的问题。后续工作树尝试及失败日志独立保留，没有挑一次通过来覆盖失败。

`44f1cd3` 改由原生列表按实际光标距离统一滚动，关闭整块 WebView 的竞争滚动，位置读取不再触发重组。提交前窄屏 6 项针对性回归通过；该提交完整手机回归 26/27，剩余失败发生在精确光标和补充输入成功之后的首次结束确认，仍属于 #41。原三尺寸串行脚本因此中止，不能写成该提交的窄屏/平板整组通过；单测/构建与必需 CI 分别记账。首次结束、渲染故障核对继续与离屏提交缺口继续开放，完整 MVP 未完成。

本轮2026-10-28的`749c39c`将整个原生提交/操作/恢复串行化，旧物理视图的最后确认由视图自身持有，后继必须等待；旧实例不能清掉新实例pending，重绑定不能在确认途中禁用DOM或切换epoch。失败后显示“待核对正文”和保留源码，显式确认跨Activity重建保存，不要求任意编辑才能继续。真实渲染终止搭配受控桥接延迟、实际24叶块页面滚动、独立实际视图延迟交接分别验收；真实中文IME来自既有组合输入用例。原间歇观察在本轮修复前单项和6项回归曾通过，故不声称已排尽原故障所有可能原因；旧失败未洗白。首轮正常重建误提示已修正；64叶块与手动阻塞提交组合未稳定空闲，仍保留为失败探索，不用分层通过替代一般性能验收。

第一次统一矩阵fe487b0的1870 JVM/手机30/窄屏13通过，但平板13项中1项精确光标失败：光标底部652超出可见底部592，纠正稍后才发生。该失败及坐标/截图单列保留；随后改为实际placement后合并滚动请求，不把位置写成Compose状态，不改断言或等待。当前提交的完整矩阵另验，旧绿灯不替代。第二轮4c94eb5整组仍在该断言失败；复核测试先读取原生位置、再触发语义布局，边界来自不同时刻。最终修订仅将原生/Compose边界置于同一runOnIdle屏障后读取，保留可见范围/精确段落/后续输入/首次结束及原时限；生产源码未再修改。修订前平板rich6通过，最终整组结果仍单独校验，两次旧失败均不洗白。

### MVP 执行与差距台账

用户已要求“尽快做 MVP，尽可能不漏功能，跳过或不完善必须如实记录并后续完善”。**MVP 仍指安卓 M1+M2 真实闭环**，不将剩余必需项改名为“未来增强”来宣布完成。以下对应全部 32 个能力编号；13 个既有业务迁移编号继续逐项保留在第 5 节，本轮没有迁移它们，也没有关闭总跟踪。

“本地实现”指已合并的Android本机能力及其实际证据；服务端收包/存储的合并状态另列。不代表已正式发布笔记、OPPO 真机通过或云端处理完成。每次更新必须同时改当前事实、缺口和补齐归属；没有证据的部分记为未验证，不用新增测试代码代替已执行测试。

| 编号 | 当前事实与明确缺口 | 补齐/复查归属 |
| --- | --- | --- |
| NOTE-01 | Android #48实现正式笔记导航、本机列表/新建/打开和标题/Markdown搜索，复用既有编辑/保存；结果完整UI、Windows/Web新笔记访问和更多组织能力仍未做 | M2 云端与各平台批次；本机搜索不等于云端全文或多端完成 |
| NOTE-02 | 离线成熟核、原稿保全、串行输入与物理视图交接、显式源码核对已随#40合并并通过当前矩阵。**逐叶块撤销不跨材料/模式/重建；超长叶块内部滚动，整篇编辑/大量块性能未验收**；64叶块+挂起提交探索失败单列，不承诺恢复未收到字符 | M2继续统一撤销/长文和更多故障性能验收，保留源码及材料边界，不另做孤岛demo |
| NOTE-03 | 旧快速记录模块及其数据保留；未迁移或自动合并到新笔记，关系尚未落地 | M2 确定标识/访问兼容，回归旧内容；不得删旧数据充当迁移 |
| NOTE-04 | 原生照片导入/录音中相机/取消保全已实现；#48固定版本实际上传原图/笔迹图及全部材料，摘要核对不随新稿变化。v0.35 API/Web 已用固定 `document.md`、照片/手写原字节与成功 ASR 完整计划参与派生并完成真实受控 phone/narrow core；Android functional 网络/设备 E2E 及公开逐图 receipt 仍未完成 | M2 全材料真实闭环、逐图 audit 与课堂验收；受控 provider 或截图不等于真实模型质量 |
| NOTE-05 | PIM蓝色、正文优先、右上结束、左下真实录音浮条和精确光标保留；#40的首次结束/渲染核对与重建已通过。#48补正式导航/本机库/材料上传及失败状态；转写/结果/修订/冲突完整页面仍未做。Material3/Animation/Adaptive与按需Coil/Lottie版本对齐/接入未完成，参考字体/动画未采用 | 继续完整业务UI、长文/大量块性能及OPPO验收；当前宽度分支不算Adaptive库接入 |
| INK-01 | 原生可编辑向量卡片及保存/重开已有实现，不以截图替代 | 本轮成熟核共存回归；M2 用对应版派生图 |
| INK-02 | 触控及模拟器已覆盖；**OPPO Pad 4 Pro 真笔/掌触、Find X9 Pro 系统输入未验证** | M0/M1 真机验收，仍为安卓首版缺口，不后移至其他平台扩展 |
| INK-03 | 向量、身份/尺寸和 PNG 可导出；Windows/Web 预览/往返及包导入未做 | M2/M3 对同一兼容夹具分别验收，不以 Android 通过代替 |
| INK-04 | 右下角自由拖宽高、增空白不拉伸已有实现；v0.35 API/Web 已读取固定版本手写衍生图原字节并进入 loopback SDK，最终真实受控复验确认原图 bytes match；公开契约仍只有聚合 count | M2 真实多模态质量、公开逐图 receipt 与 Android functional 网络/设备接入 |
| REC-01 | 多会话/多段、3 小时自动续段/暂停不计已有逻辑；**真实 100/180 分钟及接续间隙未验收** | M0/M1 OPPO 长时和资源边界，不能以加速单测替代 |
| REC-02 | 原生前台服务独立于正文视图；模拟器切应用/相机/重建已有短时证据，厂商省电/锁屏未验收 | M0/M1 两台 OPPO 真机与后台限制告知 |
| REC-03 | 本机原文件/清单恢复、故障提示已有实现；真实来电/音频抢占、耗电/温度、极低空间场景不完整 | M0/M1 故障矩阵；已落盘材料保全，不承诺强制停止后持续采集 |
| REC-04 | 本地回放、音频固定/流式导出及服务端独立分块收包已实现；#48补账号/服务器绑定持久队列、暂停/明确恢复与未知回执核对，普通账号真实Test API接收4,802,915字节混合版本；未借用4MB小附件。**实际课堂长时上传/进程重启/OEM网络仍未验收** | M2目标环境后台/网络与课堂验收；保留原失败证据，不以受控503代替真实断网 |
| REC-05 | Windows 主动记录尚未实现，目标保留 | 后续 Windows 记录批次，不以只读结果页销项 |
| REC-06 | 原生持续通知/状态入口和关闭权限提示已有实现；OPPO 锁屏隐私/可见性待测 | M0/M1 真机，与实际服务状态一起验收 |
| REC-07 | 波形来自实际 PCM 输入；无宿主音频的模拟器不能证明真实声学效果 | M0/M1 OPPO 声学/静音/异常输入验收，不使用素材动画代替 |
| AI-01 | 实时转写未做；用户明确非必需、不紧急 | 后续可选，未排期，不阻塞 MVP |
| AI-02 | MiMo预设/单片及#520全部录音的固定版本持久分段、样本偏移/覆盖、回执核对、停止/明确失败片恢复已合并；管理员Web逐片可查。普通账号API授权/destinations已随#528合并且PR CI成功；Android ASR PR#51已MERGED`93d93437`，exact head`8b1d091`/sha`67f0`，CI 40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL；Source组件3按同源R5另记。**普通账号/Android逐片入口、真实后台重启和课堂识别质量未验收**；真实运行仍待独立验收 | M2客户端处理接入与真实运行/长课堂验收；不把Source组件R5 lineage、旧JVM、合成100/180分钟或旧APK当最终课堂/生产证据 |
| AI-03 | #520已有ASR进度、逐片结果/失败及恢复。v0.35 API/Web 已实现独立记录/分析、逐步覆盖与失败/停止/未知入口，最终 API CI/Release 与真实受控 Web PASS；Android functional E2E 仍 PENDING | M2真实闭环与逐步骤产品验收；不以本机测试替代 |
| AI-04 | 本机/服务端固定输入、同篇版本和#520任务输入隔离已实现。v0.35 已合并不可变候选、人工 CAS 双全文、`hasConflict`/`isHead`、生成不移动 head、幂等/恢复，并通过最终 API/Web CI 与受控负向路径；Android functional 设备链 PENDING | M2端到端版本/修订保护与 Android 真实并发闭环 |
| AI-05 | 本批实现多类型配置/默认值、SDK非流式文本直连、实际消息/请求响应正文留存和基础统计；凭据加密且仅窄范围遮盖已知密钥。本批另接通PNG/JPEG多图、目的地确认、原图留存/指纹与所有者读取，并以授权本机模型做合成小样；本批ASR通过可扩展预设接通MiMo单片；长音频服务端基础已由#520补齐；流式/中断部分结果、真实笔记质量、旧调用迁移及计价/清理未做；4 MiB响应上限、结果写库失败不能恢复未持久回复等限制见[说明](../architecture/ai-text-model-settings.md) | M2 服务端适配与真实模型小样验证；按顺序补对应能力和旧调用方回归后清理LiteLLM，不先造全套AI平台 |
| AI-06 | 本机版本能固定全部正文/照片/向量派生图/音频；v0.35 API/Web 已实际消费固定正文、全部成功转写、照片/手写图，视觉分批 4张/4MiB/8MiB、最多32图8批、200k输入/50k单证据，WebP/超限显式 blocked，并通过最终 CI/受控复验 | 公开逐图/逐 block receipt、Android functional 网络与课堂质量 |
| AI-07 | v0.35 API/Web 已合并 `record-synthesis` 与 `analysis-synthesis` 独立调用/不可变版本，分析实际读取记录正文与来源，最终 CI 与 Release 成功；Android functional 全链未验 | M2 Android 产品入口与真实课堂质量；不以短摘要或合成 provider 代替 |
| AI-08 | 已有主题化 prompt、来源区分与双产物；本机/合成回执可证明输入和结构路径，真实生成质量与事实保真尚未验收 | M2 经同意样例核对细节、事实/推断和来源，不伪造引用 |
| AI-09 | 速览/HTML 或图片导出未实现，仍为未确认候选 | 详细记录/分析完成后再确认范围；不默默升格或取消 |
| TASK-01 | 任务建议到任务板块未做 | M4 用户确认与幂等创建，现有任务功能保留 |
| SYNC-01 | 离线与本机固定材料、服务端JWT隔离已实现；#48接账号/服务器绑定存储队列，明确同意才提交、A上传与新稿B分离、旧账号明确恢复。v0.35 API/Web 已合并持久任务/步骤、stop 保留 success/unknown、显式 retry+ack，真实受控 Web 完成 dirty 刷新/切候选保留；Android functional 与 Windows 跨端使用仍未完成 | M2跨端真实闭环；登录、本机材料或受控 QA 不自动创建上传 |
| SYNC-02 | 本机旧输入不随新稿变化；服务端比较基础版本、保留双方完整材料及显式选择已实现；#48仅固定已知基础版本并显示冲突、不自动提升。v0.35 已合并 JSON/CAS 双全文与 `IsHead`，真实受控 CAS 双留、dirty 刷新/切候选 PASS；Android functional 与真实并发使用仍未完成 | M2各平台完整材料比较与选择，不以单元测试或受控路线销项 |
| SAFE-01 | 服务端上传隔离/配额/过期/删除意图、内置文件来源和AI密钥加密/内容受控记录已实现；#48补原材料范围/EXIF明确同意、页面重建重确认、加密owner与令牌快照、首发/401重试身份核验及无令牌任务。v0.35 已合并 owner/固定输入/目标/同意与停用/跨 owner/precheck 目标漂移拒绝，最终 API/Web 受控路径 PASS | 模型同意/第三方保留、自动 secret/log 扫描、记录清理、备份删除、全服务器配额、生产磁盘与 Android functional E2E 仍有缺口 |
| NATIVE-01 | 复用现有 Android 主应用、录音/存储/通知；未迁移全部既有业务 | 第 5 节 MIG-01 至 MIG-13 按主线 B 盘点，不因笔记结项销项 |
| NATIVE-02 | 本轮未改 Windows，既有宿主/采集/认证保留 | 主线 B 及 Windows 记录批次 |
| WEB-01 | v0.35 同仓 Web 已随 PR#532 merge/release：“笔记处理”页面、正式生成类型、双产物/修订/冲突/草稿保护；最终全量 `349/349`、最终 API CI 40907900018/40994288275 与 Release 成功。最终 dist `c915835a...` 在真实 Test API + loopback SDK 下 phone/narrow core 与负向路径 PASS，late-success 12/12 real-SDK cases PASS | 真实模型质量、完整视觉/课堂验收、公开逐图 receipt 与 Windows/Android functional 跨端仍需独立证据；受控 QA 不等于 production |

当前优先顺序以第7节为准：#48准确head验收及合并、API #528普通账号授权与PR CI已完成 → Android PR#51 设备/merge → API/Web 全材料双产物/修订保护 PR#532 merge/release/最终 CI/受控 Web 已完成 → Android 33 项 fullapp 回归 + instrumentation test APK 编译 → 冻结 APK/设备 E2E → functional Android PR 完整 CI → 真实课堂/OEM/长时与公开逐图 receipt 独立验收。依赖对齐、后台实际恢复与目标设备验证随批推进；通用流式/部分结果及旧调用迁移并行保留。#40输入恢复、#520持久ASR与#528授权基础不重复开发；Windows/Web、任务与全业务迁移不删除。

## 3. 完整能力清单

下表是需求覆盖索引，不是实现规格。“待排期”不代表取消；新增、合并、取消均需保留原编号及原因。视觉前置已合并，笔记业务闭环仍未完成；逐切片状态见交付台账。

| 编号 | 能力与最终预期 | 来源/状态 | 建议阶段 |
| --- | --- | --- | --- |
| NOTE-01 | 笔记列表、打开、新建、修改、保存；手机/电脑/Web 可用 | 用户目标；组织方式待定 | M1 本地起步，M2 多端 |
| NOTE-02 | Markdown 正文和成熟组件；确定通用 Markdown 与扩展内容的兼容方式 | 已确认离线开源正文核心方向；版本/格式适配及迁移待验证 | M0 验证，M1-M2 |
| NOTE-03 | 现有快速记录与新笔记的关系、历史内容兼容，避免数据被遗弃 | 集成必需；合并或独立模块待定 | M0 定边界，M2 落地 |
| NOTE-04 | 录音过程中拍照并直接插入笔记，本地保存，提交后作为 AI 输入材料 | 用户新增明确目标；图片处理方式待验证 | M1 记录，M2 云处理 |
| NOTE-05 | 编辑器是记录主页面；录音状态/时长/小型真实波形采用应用内左下角紧凑悬浮条；取消常驻品牌栏，整次结束右上角悬浮，不挤占主要编辑空间 | 用户明确位置与紧凑方向；实时文字非必需 | M0 真机验证，M1 |
| INK-01 | 嵌入式手写卡片可编辑，保存笔迹而不只是截图 | 用户明确与首个安卓可用版本一起交付 | M0 验证，M1 基础交付 |
| INK-02 | 安卓手机/平板首版基础手写，含 OPPO Pad 4 Pro 验证；Windows 等其他平台后续输入适配 | 基础手写首版与安卓目标设备已明确；具体笔输入待测 | M0 真机验证，M1 安卓手机/平板，M3 其他扩展 |
| INK-03 | 多端一致预览与可迁移的笔迹数据；导出时不只留下失效私有引用 | 兼容性建议；具体导出范围待定 | M0 验证，M1 数据基础，M3 扩展 |
| INK-04 | 普通 Markdown 内嵌手写卡片，拖动右下角直接调整宽高，保留笔迹与布局，分析时渲染为图 | 用户已确认形态、拖拽入口和首版归属；不使用滑块/预设档位 | M0 验证，M1 编辑/拖拽，M2 图片输入 |
| REC-01 | 一篇笔记多个录音，手动开始/结束整次记录；单段上限 3 小时，满时保存并自动续段，暂停不计时，同篇累计不另设固定业务时长上限 | 用户已接受时长、自动续段及暂停计时；具体控件/连续性需验证，资源限制需透明 | M0 验证 100/180 分钟及跨段边界，M1 |
| REC-02 | 安卓锁屏和切换应用持续录音，可感知录音状态并主动停止 | 用户硬要求 | M0 真机验证，M1 |
| REC-03 | 本地保存、空间不足/权限变化/来电与其他音频占用处理、异常恢复与中断提示 | 可靠性必需；具体行为待定 | M0 验证，M1-M2 |
| REC-04 | 音频回放；上传队列、失败重试与避免重复；长录音不走现有小附件假设 | 闭环必需；上传/存储方案待定 | M1 本地回放，M2 完整链路 |
| REC-05 | 电脑端也能开始和完成记录，不止查看安卓上传的内容 | 用户明确后续目标；安卓可先行 | M0 保留接口边界，后续批次待排期 |
| REC-06 | 后台录音时通过系统通知展示当前状态，权限/锁屏可见性如实告知，与实际录音状态一致 | 用户新增明确目标；通知具体交互待定 | M0 真机验证，M1 |
| REC-07 | 应用内显示基于实际采集信号的实时波形，辅助判断输入；不以假动画代替，不混淆输入和成功保存 | 用户新增明确目标；呈现/异常细节为建议 | M0 验证，M1 |
| AI-01 | 可选本地实时转写；临时文字与正式结果区分，识别故障不影响录音 | 用户明确非必需、不紧急；保留可选项，不是交付门槛 | 后续可选，未排期；不阻塞 M0-M3 |
| AI-02 | 结束后由服务器调用 ASR；各段录音独立正式转写、单独查看，覆盖长录音 | 用户明确目标 | M2 |
| AI-03 | 独立分析与总结、可查看正式转写，显示处理状态和失败，支持明确重试 | 用户目标及可靠性要求 | M2 |
| AI-04 | 原笔记与派生结果独立；固定处理输入版本，提交后可编辑/补录，完整记录和分析可人工修订，重生成不覆盖用户稿 | 用户已接受六条规则；控件与实现细节待设计 | M1 数据边界，M2 处理闭环 |
| AI-05 | 完善 AI 配置：语音/文本能力、凭据、时限、用量与隐私；不假定一个模型包办一切 | 用户提出完善 AI；具体范围待定 | M0 定边界，M2 |
| AI-06 | 全部录音转写与手动 Markdown、照片、手写图片共同参与分析；不丢材料、不只用当前段 | 用户明确多材料目标；手写随首版；图片/长内容能力待验证 | M0 验证，M2 含手写的完整链路 |
| AI-07 | 根据全部材料产出完整整理记录，与独立分析和原始逐段转写分开 | 用户明确双产物目标；格式待定 | M2 |
| AI-08 | 参考样例的主题化分析：主线、事实/数据、对比、引用、问题边界与行动；区分原陈述和 AI 归纳，按内容选结构 | 用户提供具体参考；模板与详细验收待确认 | M0 定内容形态，M2 按确认范围落地 |
| AI-09 | 基于同版事实的速览视图，按内容使用指标/卡片/比较/行动等呈现；可考虑 HTML 或图片导出 | 参考候选；不默认承诺所有模板及导出格式 | 未排期，首版是否包含待确认 |
| TASK-01 | 提取任务建议；查看、修改、确认/放弃后写入现有任务模块，重复确认不重复创建 | 用户后续目标 | M4 |
| SYNC-01 | 记录时本地保存，结束后整体提交；多端访问同一笔记及附件，明确各阶段状态 | 用户明确记录策略及多端目标；传输细节待定 | M1 本地，M2 云端及多端 |
| SYNC-02 | 固定提交输入版本，多端保存校验版本，冲突保全双方正文/笔迹/布局/可读取附件，不静默覆盖，不默认自动合并 | 用户已接受版本校验与冲突保全；界面/数据结构待设计 | M0 细化，M2 按平台批次交付 |
| SAFE-01 | 用户隔离、录音及云处理告知、敏感内容日志策略、删除与保留边界 | 产品安全必需；策略待定 | M0 定边界，各阶段验证 |
| NATIVE-01 | Android 原生业务扩展，并与现有采集、权限、通知和后台生命周期共存 | 用户方向；已有原生基础 | 迁移主线持续推进 |
| NATIVE-02 | Windows 原生业务扩展，并保留现有托盘、采集、认证及本地存储能力 | 用户方向；已有原生基础 | M0 盘点，M2 起持续推进 |
| WEB-01 | Web 保留为正式访问方式，逐步支持新笔记、录音结果和手写内容 | 用户目标；Web 不承担安卓可靠后台录音保证 | M2 起，M3 扩展 |

说明：完整记录/分析的具体格式、说话人区分、逐字稿校订、点击转写定位音频、全文搜索、手写单独 OCR 和录音时间对齐，都有讨论价值，但目前不能标为已确认需求。手写渲染成图用于 AI 输入已经明确，不等于要求先把手写全部 OCR 成正文。后续确认新增需求后分配编号，不用它们替换已明确提出的能力。

## 4. 两条并行主线与建议里程碑

主线 A 是“笔记、录音、AI、任务”的新业务闭环；主线 B 是现有业务原生化。它们共享认证、数据访问、同步等基础，但不要求全部迁移完成才能使用笔记，也不因笔记首版交付而宣布原生化结束。

以下是根据本轮使用流程调整的依赖顺序建议，不是工期承诺。M1 是本地能力里程碑，不应被称为包含云端分析的完整产品。当前首版建议是安卓 M1+M2 纵向闭环，用户已明确基础手写必须随该版本交付；其他尚未确定的发布边界仍逐项确认，不把这一决议扩大为所有规格已批准。

| 阶段 | 主线 A：新业务交付 | 主线 B：原生客户端演进 | 阶段退出依据 |
| --- | --- | --- | --- |
| M0 产品定界与风险验证 | 以 OPPO Find X9 Pro/Pad 4 Pro 验证后台录音、通知、真实波形、拍照及恢复，覆盖约 100 分钟课堂、180 分钟单段边界和多段累计超过 180 分钟；手写/Markdown 往返、图片 AI 输入和长内容验证；定义数据和隐私边界 | 盘点现有原生、WebView 和独立壳的责任，确定唯一主应用及迁移台账 | 真实设备上的录音连续性、通知可见性/状态一致性、实际信号波形和保存恢复证据；明确未解决风险，不只提供界面截图 |
| M1 安卓本地可用 | 编辑优先的 Markdown、拍照插图、基础可编辑手写与右下角自由尺寸拖拽、同篇多段录音、单段满 3 小时自动续段和暂停不计时、后台录音、持续通知、左下角紧凑悬浮录音及真实波形、本地回放/恢复；不要求实时转写 | 在现有原生主应用中形成真正可用的笔记入口，不另做一次性孤岛 demo | 断网完成记录；手写能再次编辑，调整宽高与正文共存，保存重开不丢笔迹；锁屏/切应用/拍照与自动续段的实际连续性有证据，暂停计时正确；通知/波形如实反映状态；异常时明确中断并保留材料 |
| M2 安卓云端闭环与多端访问 | 结束后完整提交/重试，服务器逐段 ASR，全部转写、正文、照片和手写渲染图片生成完整记录与独立分析，处理后编辑和版本保护；逐步接入 Windows/Web 访问编辑 | Windows 原生业务入口开始落地；按已确认优先级迁移首批现有核心业务，不阻塞安卓闭环 | 安卓完成真实课堂/会议闭环；全部录音均有可查结果；照片、手动正文及对应版本的手写图片实际参与处理；失败不损伤原材料；多端与冲突覆盖按实际逐项验收 |
| M3 混合笔记多端扩展 | 扩展 Windows 等其他平台输入、多端混合编辑/预览与约定导出；安卓手机/平板基础手写及其 AI 图片输入已前移到 M1/M2，不在本阶段重复等待 | 推进其他平台的原生阅读和编辑体验，持续优化平板体验；不将首批 OPPO Pad 4 Pro 基础记录延期至此 | 目标设备跨端往返不丢笔迹、块位置和尺寸；约定导出可用；各端实际编辑与仅预览状态分开验收 |
| M4 智能整理扩展 | 任务建议到现有任务模块的确认闭环，完善分析体验；实时转写仅作为后续可选候选，按需求再验证和排期 | 继续补齐任务等关联原生能力和统一体验 | 用户可拒绝/修改建议；重复提交无重复任务；AI 故障不影响原始记录；可选实时转写不计入必需完成条件 |
| M5 全面迁移与收尾 | 补齐明确延期的笔记能力，持续优化性能、存储和恢复体验 | 对照第 5 节逐项原生化；确认电脑记录能力的实际交付，不以只读访问代替；旧入口仅在替代能力验收后退场 | 所有能力有明确去向，未完成项仍公开保留；不以“主要页面做完”代替完整迁移 |

混合文档与笔迹的风险验证必须在 M0 做，基础手写已按用户要求前移到 M1/M2，不能仍按旧版 M3 安排而漏掉首版。实时转写仍是非必需、未排期的后续可选项，近期不为其引入本地模型安装和性能门槛；波形与后台通知属于 M0/M1。主线 B 的先后顺序尚未确认，电脑记录能力可以按确认后的批次提前，不因列入收尾核对就被固定到最后。M5 也不是此前完全不做现有业务迁移的意思。

任何有价值的独立技术验证都应复用真实客户端基础，产出可复用结论与组件；浏览器录音演示不能证明安卓锁屏录音可靠。

## 5. 现有业务原生化覆盖台账

来源：当前 Web 路由与导航、Android 和 Windows 已有实现。路由存在不等于功能完整，本表不替代逐项功能验收。每项最终补充 Android/Windows/Web 状态、issue/PR、验收证据和差异说明。

初始统一状态：已纳入迁移范围；原生覆盖细节与批次待盘点。已存在的原生部分应复用，不重复建设；Web 继续保留。

MVP 恢复开发本轮没有新增 MIG-01 至 MIG-13 的业务迁移覆盖，也没有删除它们或停止既有采集；全部继续归属主线 B，逐平台盘点、实现和验收。不以新笔记本地编辑可用宣布原生化完成。

| 编号 | 功能范围 | 迁移需保留或明确处理的内容 |
| --- | --- | --- |
| MIG-01 | 今日、工作台 | 聚合展示、业务操作、详情跳转，不仅是展示壳 |
| MIG-02 | 日历、任务、日历样式、日历本/任务本 | 既有查看与修改能力、分类及业务规则 |
| MIG-03 | 确认中心、提醒、习惯、报告、数据中心 | 现有业务动作与状态，不因低频而消失 |
| MIG-04 | 快速记录、文件 | 与新笔记的关系、已有内容兼容、附件访问和操作 |
| MIG-05 | 电脑记录、浏览器使用、电脑明细查询 | 采集不停摆，查询/分类能力和平台展示差异明确 |
| MIG-06 | 手机记录、历史位置 | 采集、地图/轨迹展示、权限和后台状态共存 |
| MIG-07 | 应用知识库、分类树 | 既有知识维护及分类操作 |
| MIG-08 | 设备列表、设备详情 | 设备身份、状态和管理动作 |
| MIG-09 | 登录、初始化、服务器与账号安全、用户管理 | 权限隔离、凭据保护和配置；管理员能力是否暂留 Web 要显式记录 |
| MIG-10 | Microsoft/Outlook、Google Drive、AI、MCP 设置 | 现有第三方集成及配置能力；音频处理按需扩展 |
| MIG-11 | 系统状态、数据可靠性、日历数据管理、回收站、审计详情 | 诊断、数据修复/恢复和审计入口 |
| MIG-12 | 端点/壳入口、嵌入页、旧路由别名、展览馆占位 | 分清兼容入口与未完成功能；不机械搬运壳页面，也不未经决定删除产品能力 |
| MIG-13 | 客户端安装更新、托盘/通知、采集启停、队列与同步、设置 | 保留现有客户端能力，确保业务窗口关闭与后台工作生命周期分离 |

完整原生化不是每项必须在所有端完全相同。确有平台不适用或先留 Web 的项目，必须在台账中注明、由用户确认；不能直接从总范围中删掉。此台账还需用实际页面动作和客户端能力补充核验，不能声称已经达到逐按钮覆盖。

## 6. 已知技术边界，避免建立在错误假设上

- Android 已有 Kotlin/Compose、Room、本地队列、认证、采集与同步基础，部分业务仍由 WebView 展示；录音前台服务和恢复已随 #39 的保留历史合并进入主线，尚需真机验证，不能由既有定位服务或正文 WebView 代替。
- Windows 已有 .NET/WPF、托盘、原生采集和 SQLite 基础；业务内容部分仍由 WebView 承载，不是从零创建桌面程序。
- 现有快速记录编辑器保存 HTML，不能当作已经完成 Markdown 存储与往返兼容。
- 已合并 #40 的离线编辑默认使用 Tiptap/ProseMirror 编辑 Markdown 叶块，Markwon 源码编辑/阅读预览继续作显式回退；尚非统一整篇编辑核，全局撤销、故障确认和长文性能缺口见 NOTE-02。旧混合原稿兼容必须连同向量、块身份/尺寸、照片和全部录音验证，不删除旧数据后重新录入，也不靠纯文本导出证明无损。
- 现有通用附件有 4 MB 限制并使用缓冲读写，不能直接当成长录音上传和播放管线。
- 新控制台已复用 Microsoft.Extensions.AI/官方SDK直连文本，旧调用方与LiteLLM部署保留，不从零写协议或解析器。当前 PIM 实际调用仍主要是文本消息；视觉/语音转写需要独立接入，不能将图片/音频路径硬塞给文本模型。新路径限制见实现说明，笔记长任务/全材料覆盖仍在业务切片验收。
- 离线实时转写需要设备本地具备模型及推理能力；模型可预先下载，但不能在每次录音时依赖在线识别。目标手机的中文识别效果、延迟、内存、温度和耗电都需实测，失败时保住原始录音并明确提示。
- AI 分析照片/手写图片需要真实的图片输入能力，不能只将图片路径传给文本模型，也不能默认 OCR 后不会丢失公式或图表。手写给 AI 的图片是派生材料，可编辑笔迹仍需保存。
- 长记录覆盖全部材料不等于强行塞进一次模型请求；若需分段处理，必须保留材料覆盖关系与缺失提示，避免后面的录音或图片被静默截断。
- 新模块目录不会自动完成构建、部署和 API 接入；数据契约仍以 `pim-api/contract/openapi.json` 为唯一权威。
- 原生实现也不能保证被强制停止、重启或系统/厂商杀进程后仍持续录音。必须明示中断、保护已保存音频，而不是用“支持后台”掩盖这些边界。
- 后台状态通知依赖系统通知权限、渠道与锁屏可见性；不需要用悬浮窗模拟，也不能以通知计时或波形动画作为音频成功保存的唯一证据。

## 7. 接下来按什么顺序落实

#48 的固定版本上传、#520 的持久分段 ASR 和 #528 的普通账号授权/destinations 已是可复用基础，v0.35 不重写切分、检查点、Hangfire 调度、回执核对或失败片恢复。API/Web 已由 PR#532 从最新主干独立完成并合并；Android derived functional 已从中间 `2ae81d8a`/P1 修复链推进到 a850 普通 merge、master CI 与 Release `android-v26.10.1137`，四 cohort 在真实 Test API + owned AVD 分轮 PASS。编码/契约、模拟器功能与物理设备/产品验收仍并行记账，不能用 CI 或 GitHub Release 代替真实课堂。全材料双产物/修订保护的具体行为、证据和门槛见后文 v0.37、v0.36、v0.35 历史与[设计与验收](../architecture/native-note-derived-artifacts.md)。

生产当前 `NOT-VERIFIED` 只属于部署/发布证据，不作为普通账号授权、唯一契约再生成和 ASR 编码的前置；完成生产核实也不能反向证明编码、课堂或完整 MVP 已验收。

| 顺序 | 交付与稳定需求 | 依赖/复用 | 编码与契约验收 | 目标设备/产品验收（独立） |
| --- | --- | --- | --- | --- |
| 已交付基础 | NOTE-01/04/05、REC-04、SYNC-01/02、SAFE-01 的存储上传；AI-02/03/04 的服务端固定版本分片任务 | #48 固定版本上传；#520 任务/检查点/逐片结果/恢复；#40 编辑恢复；唯一契约与现有 SDK | #48 的 `29432b5` PR CI 和合并后 master CI 成功；#520 准确 head 门禁与逐片恢复回归保留 | 仅证明受控模拟器、隔离 Test API 与合成材料；不是真实课堂、OEM、生产或完整结果 UI |
| 1 普通账号授权与目标确认（已交付） | AI-02/03/04/05、SAFE-01：普通账号只能处理自己的固定材料/任务，通过管理员维护的安全目录选择已启用共享转写目标；空目录先由管理员配置，不提供BYOK自助 | API #528 已普通合并到 `a3b35dc`（Release`api-v26.10.995`为历史），复用#520固定版本/模型revision/预设版本/地址快照与显式同意；owner只隔离材料/任务/调用账号，不隔离模型配置；P0 API #529与current Release`api-v26.10.997`成功；Android PR#51已消费共享目标 | PR CI 40825191098、master 40829847795、P0 PR/master CI与current Release均成功；目标目录不含凭据；无权限、非owner任务、停用、配置变更、目标不一致均拒绝或停止；授权绑定固定版本、模型/预设/地址、语言和同意；上传不自动收费 | Android显示实际目标、材料范围、共享配置来源和后果；匿名401、跨owner任务与disabled-account负向测试不能由设备测试代替；GitHub Release不等于production，管理员CRUD/runtimeWeb仍admin-only |
| 2 按唯一契约再生成 Android（已交付） | 跨端接入前置：只消费 API `contract/openapi.json` 中的任务接口与类型 | `a3b35dc` 的唯一契约（SHA256 `eff87d96`）；Android bot #49 merge `e629febe`、Windows bot #37 merge `94384c9e`，required CI全SUCCESS；Android PR#51已包含bot契约与official generated commit | 当前PR#51的freshness/codegen SUCCESS；记录 API/client/bot准确提交 | 本步骤不做设备功能验收；生成物门禁失败不得用设备通过抵销 |
| 3 固定版本逐段任务与结果 UI（已交付；P2 UI余项） | AI-02/03/04、NOTE-05、SYNC-01、SAFE-01：选择自己已上传版本，确认目标，创建/查看/停止/恢复逐段任务并查看每段结果 | 步骤1的服务端授权；步骤2的生成类型；#48 固定版本与上传状态；#520 的 `asr-tasks`、`sources`、单任务、`segments`、`stop`、`retry` 与持久回执 | 当前PR#51已MERGED`93d93437`，head`8b1d091`/R8 sha`67f0`，CI 40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL；Source组件3按同源R5另记。P2 UI limitation与master CI/release回执另记 | 隔离Test API与目标设备验证；Source组件3不得拼成当前test package 36 PASS；P2 UI limitation、真实课堂/生产质量另列 |
| 4 全材料完整记录与独立分析 | AI-03/04/06/07/08：全部录音转写、正文、照片及对应版本手写图进入同一次完整记录/独立分析 | 步骤3的明确成功或缺口；#520 不重写；复用文字/视觉 SDK、固定材料版本、实际调用记录与统计；派生稿与原始材料分离 | 服务端证明输入覆盖全部所需段和材料；任一必需段缺失/失败时不得发布“完整”，必须显示缺口；完整记录与独立分析分开存储、来源/模型/版本可追溯；禁止静默截断或单摘要冒充双产物；契约同步生成 Android/Web 类型 | 用真实课堂/会议全部材料验证完整性、事实保真和失败恢复；合成 100/180 分钟只证明覆盖算法，不证明识别/整理质量 |
| 5 人工修订与重生成保护 | AI-04 及六条提交/修改规则：派生稿可人工修订，重新生成/补录/来源变化不覆盖人工稿 | 步骤4的派生版本与来源；固定输入版本；服务端版本/冲突能力；人工稿与模型稿独立身份 | 自动稿、人工修订稿、新生成稿分别保存；重生成创建新版本并保留旧稿；补录/材料版本变化显示来源差异和选择；并发、幂等重放、失败恢复均不覆盖较新人工稿；冲突时保留双方完整材料 | Android/目标设备验证编辑、比较、选择和恢复路径；真实课堂材料至少走一遍“处理→修订→补录→重生成→选择”，不能只用单元夹具 |
| 6 完整 UI、正式发布验收 | 安卓 M1+M2 的导航/库、处理/结果/修订/冲突 UI、目标设备与签名发布门槛 | 步骤1-5；#40/#48 现有导航/编辑/上传；Material3/Animation/Adaptive 及 BOM 兼容清单，Coil/Lottie 按需 | 手机/平板/大字号/IME、首次结束、故障恢复、完整云流程和签名构建回归通过；契约、测试与产物对应准确 head | OPPO Find X9 Pro/Pad 4 Pro 的 100/180 分钟/续段、锁屏/切应用、声学、笔/掌触、资源、断网/OEM 和完整课堂流程通过；任一关键缺口未关闭不正式发版 |
| 并行保留：调用与可靠性 | AI-02/03/04/05/06/07/08、SAFE-01：流式/部分结果、旧调用迁移、实际 Hangfire 崩溃/重启 | 复用现有 Hangfire/SDK 与 #520 持久检查点，不另建任务平台 | 流式/部分输出按独立能力验收；旧调用方回归后才清理 LiteLLM；真实进程崩溃、恢复和部署演练单独留证 | 不作为所有客户端编码的统一前置；不能用合成 API/CI 页替代后台进程和真实课堂 |
| 并行高优先级：现有手机同步缺陷 | API 稳定编号之外的现有业务阻塞：[API #525](https://github.com/2746267826/pim-api/issues/525)，40 小时批次 `block_id` 超长、pending 空回放、客户端无退避重传，约 64GB，issue 记录 303 条历史 pending | 独立于笔记路线；API #529 已push`2287ecc8`并普通MERGED到`c5ff06fe`（2026-10-29T08:31:12Z），PR CI [40838578113](https://github.com/2746267826/pim-api/actions/runs/40838578113)为6150 pass/15 skip/0 fail（total6165），master CI [40842715856](https://github.com/2746267826/pim-api/actions/runs/40842715856) SUCCESS，Release[api-v26.10.997](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.997) target`c5ff06fe`、4 assets。Android P0 PR[#50](https://github.com/2746267826/pim-android/pull/50) source`8c55dc15`/run40845315327 required CI全SUCCESS，3项P1修复静态review关闭，已普通MERGED到`235f1203`（2026-10-29T09:08:31Z） | P0仍需组件检查与真实升级end-to-end独立回归；#525保持OPEN | 不因API/Android merge或Release宣称#525解决；**#48 Release、#526/#528/#529及P0 #50合并均未关闭此问题；生产未改** |
| 保留范围 | Windows 记录/混合笔记、Web 新笔记访问编辑、TASK-01 任务建议确认、MIG-01 至 MIG-13 现有业务原生化 | 各平台既有宿主/认证/采集与唯一契约；后续按独立批次从最新 master 开始 | Windows/Web/TASK/MIG 不得因 Android 笔记推进而从台账消失；每批仍写稳定需求、契约与回归证据 | 各平台分别验收；不支持平台/暂留 Web 须显式记录，不以 Android、只读页或文档合并代替 |

<a id="v032-下一切片全材料双产物与修订保护"></a>
<a id="v034-note-derived-artifacts"></a>
<a id="v035-note-derived-artifacts"></a>
<a id="v036-note-derived-artifacts"></a>

### v0.37 当前切片结论：Android 切片已普通 merge、master CI 与 Release 交付，四 cohort 分轮 PASS

本轮全部 Android functional 运行均为 owned Android AVD 模拟器（5556/API36.1）配合真实 Test API 的受控实测，不是 OPPO/物理真机。Android fix chain 已推进到 test head `a8507365` 并普通 merge 为 `3844ef62`：`d37db9cd` 单确认一 task，`54f6ace2` 隔离固定材料并兼容 legacy `kinds`，`080f5c2` 分离 viewed/pending/confirmed head，`af24e2f` 增加跨材料/旧账号 manual-save guard，`afab38ee` 将刷新 detail 回执持久化并发布当前 task，`f8fc4c0d` 增加两行 E2E 真实 UI refresh，`6afb6438` 以 2 处 test 修正补 fixture 和窄屏步骤，`6dc70a0d` 以 14 additions / 4 deletions 增加 coverage 异步 UI 等待和 narrow destination 滚动顺序，`a8507365` 正常提交匹配 narrow dirty `FACCE` blob而构建早于 commit。最新 JVM 为 App2008、Repository targeted11、Core69，均零 fail/error/skip；Core 源未变并沿 fresh 结果，统计不求和。exact PR CI 41038364205、master CI 41040144538 与 Release 1137 均已成功。

`af24e2f` 的真实 normal 已完成一 task、双 artifact、Photo/Ink actual bytes、普通 USER head `2ac85107...` 和 ASR3+vision1+text2=6，未重复 ASR；服务端 task `Succeeded` 后 UI 仍持旧 `Queued/Running`，真实 regeneration 按钮 Disabled，因此该轮 FAIL。`afab38ee` 的后续真实 normal/regen 已通过：initial/regen 各一次 TaskId，USER head 与 non-head 子版本关系正确，provider ASR3+vision2+text4=9 全部成功且无重复 ASR。f8 conflict-phone 也已真实 PASS：second writer head `a4a02ee1...`、local conflict `62ebb80b...` 为 hasConflict/non-head，双方完整文本与本机 draft 保留，provider ASR3+vision1+text2=6 且冲突额外 model 0。

unknown 已在 `6dc70a0d`/`45E3A6A8...`/Test `712788...` 上 PASS6：`VerificationPending`、`remote=null` 与 fault 本 case 各 consume 1，显式同 ID 核对模型 0，完整 coverage 文案通过。narrow 在 6dc + dirty `FACCE...`/Test `6CF705...` 上 45.87s PASS9：USER head `55ff0f71...` 保留，regen `8deec9ce...` 为 non-head 且 parent USER head，dirty 全文保留。两者旧 FAIL 均保留且与产品失败分账。normal `59A`、conflict `7AA`、unknown `45E3`、narrow dirty `FACCE`/最终 a850 是不同 Test APK/source lineage；即使 App/product tree 同为 D197/201a4f，也不合并成同一 Test APK 的 4/4。真实手动保存 409 的旧分类保留在 v0.36 历史，不由本版回溯改写。

### v0.36 历史切片结论：d37 exact CI 已过但 normal 后半失败；第二 fix commit 54f6ace2 已提交

Android functional 基础 head `2ae81d8a` 的 [CI 41004036926](https://github.com/2746267826/pim-android/actions/runs/41004036926) 为 success；P1 中间 head `d37db9cd` 的 [CI 41017246721](https://github.com/2746267826/pim-android/actions/runs/41017246721) 也已完成 required/CI Gate SUCCESS，PR 事件 `release` 按规则 SKIPPED。[Draft PR #54](https://github.com/2746267826/pim-android/pull/54) 仍为 Draft。CI 不等于 merge、Release、真实 normal 或最终 device 验收。

P1 中间 commit 为 `d37db9cd`；A/B/legacy pending identity 修复已正常提交推送为 `54f6ace22d7895e1d8a471d53fbe6f7ad4e83a6a`（parent `d37`，7 files）。最新 fresh JBR 21 为 targeted 28/28、App 1,999/1,999、Core 69/69，均 fail 0 / error 0 / skip 0，日志均 `BUILD SUCCESSFUL`。此前 `26/26 + 1,997 + 69` 与 App 1,990 checkpoint 保留为历史；targeted、App、Core 不求和，也不替代 `54f6ace2` exact-head CI 或 device 结果。

owned phone/R2 的真实正常 E2E 已执行并 FAIL：一次 `note-artifact-entry-confirm` 确认新增两个 server TaskId `41bf2402-b10e-48b3-88ce-4e2a26b0e519` 与 `bb65ba87-00f4-436c-a578-504d3799e1fc`。两 task 的 note/material version/ASR task 均相同，并都引用同一对 `CompleteRecord`/`IndependentAnalysis` artifact；本地也有相隔 342ms 的 FullRecord/Analysis 两个 intent。`NotesViewModel.kt:154` 按 kind 循环 start，而 backend 每 task 已生成两种 artifact，构成产品 P1。

该失败时的 provider summary 为 ASR 3 / success 3、vision 0、text 0；后续 `d37/testfix1` normal 前段 PASS 为 ASR3 + vision1 + text2 = 6。产品 P1 source 已提交 `d37db9cd`：一次 confirm 只调用一个 start，task 持久关联两 kind，VM/regen/UI 按 `supports(kind)`，费用同意明确每 task 双产物。11 个 product/test paths SHA-256 11/11 match；targeted `26/26`、fresh App `1,997/1,997`、fresh Core `69/69`，均 0 fail/error/skip，计数不求和。

但 `d37/testfix1` 真实 normal 仅前段 PASS：一次 task/双 artifact、Photo/Ink 原字节与 6 次 provider 调用后，人工保存 `FullRecord` USER head 断言超时 FAIL，原因待分类，normal/regen 整体不 PASS，negative/narrow 未验。独立 review 的 A/B fixed-material scope 与 legacy pending identity 修复现已提交为 `54f6ace2`（6 paths SHA 6/6，targeted28/App1999/Core69），该 head 的 CI/device 复验仍 PENDING。**因此当前仍是 IN PROGRESS / NOT-VERIFIED，禁止写成 device PASS、最终验收或 Android 已交付**。issues 已实际收尾为 Android #37 CLOSED(completed)、#33/#35 OPEN(partial)、API #505/#525/#533 OPEN。

**v0.36 下一步**：取得 `54f6ace2` exact-head CI → 新 build 后跑 normal phone，既确认一 task/双 artifact、A 重生成不创建 B task，也查清并验证人工保存 USER head 超时 → normal 完整通过后再跑 negative/narrow → 真实课堂/OEM/OPPO/100–180 分钟/长时/升级。生产、完整 MVP、#505/#525/#533 均保持 OPEN/NOT-VERIFIED。

### v0.35 历史切片结论：全材料双产物与修订保护已交付 API/Web

PR [#532](https://github.com/2746267826/pim-api/pull/532) 从 commit `5026e455` 普通 MERGED 为 `3e3829aa959f34cd602d193a82462116b80d0737`；exact PR CI 40907900018 以 6193 pass / 15 skip / 0 fail 完成，同 PR Web `349/349`；merge 后 master CI 40994288275、Gate、Release、Notify 均 success，`build-mcp` 合法 skipped，不计 pass。Release `api-v26.10.1011` 指向该 merge。canonical contract 为 `e38d1991...f0f9`，CI 真实生成 artifact 修复了旧本机导出的 `/mcp` 缺失。Android officialbot PR #52 与 Windows bot PR #38 只同步契约/生成物，分别发布 `android-v26.10.1125`、`windows-v26.10.1104`，都不是 derived 原生功能交付。

同仓 Web 最终 `index.html` 为 `c915835a...`，真实隔离 Test API + loopback SDK 下 phone 390×844、narrow 320×720 的 core PASS；CAS 双留、丢失响应 503→200 同版本 call 不增、dirty 刷新/切候选、late-success 12/12 单独 PASS。Photo/Ink 原字节匹配，ASR 3 + vision 2 + text 4 = 9，普通账号 admin 0。合成 provider 不证明真实质量，narrow 过渡截图不证明完整视觉。公开逐图/逐 block receipts 仍为 P2。

Android functional 工作树 `wt/note-derived-android` HEAD `967ad5cb` 已接 typed 14 operations 及 Client/Repo/VM/导航/EntrySheet/UI/人工修订/再生，Debug APK `2375cb77...` / 20,476,254 bytes；focused 30/30 + core 69/69 PASS，但 fullapp 1985 有 33 FAIL（旧 ASRStore checkpoints 写入，原因未证实），instrumentation test APK 尚未编译，真实设备 E2E 尚未执行。该工作树未提交、无 functional PR，不能记为已验收或 Release。以上均为 v0.35 当时事实，后续 Draft PR/CI/设备 P1 见 v0.36。

**v0.35 当时下一步（历史）**：先回归 33 项失败并修正/验证 test APK 编译 → 冻结新 App/test APK 与 commit → owned AVD/Test API 真实设备 E2E → 从最新 official master 创建 functional Android PR 并跑完整 required CI → 真实课堂/OEM/OPPO/100–180 分钟/长时/升级 → 公开逐图 receipts 与质量验收。生产、完整 MVP、#505/#525 均保持 OPEN/NOT-VERIFIED。

### v0.34 历史切片：全材料双产物与修订保护（已由 v0.35 完成 API/Web 交付）

该切片的目标不是再做一个摘要 demo，而是把已固定的材料版本推进到 `record-synthesis`（完整记录）与 `analysis-synthesis`（独立分析）两份正式派生稿，并让自动稿、人工稿和新生成稿拥有独立身份。它映射 AI-03/04/05/06/07/08、NOTE-04/05、SYNC-01/02 与 SAFE-01，不新增需求编号，不替代 32 行差距表。ASR 已产出的逐段转写与回执作为有录音时的材料覆盖证据和下游输入复用；无录音纯文字/照片笔记允许 ASR 任务关联为空。两类派生稿、人工修订分别拥有独立 artifact/version/state；不得把整篇产物塞进 ASR 片段状态，也不得因片段全成功就直接宣称全材料双产物完成。

本轮历史记录形成[领域行为、来源覆盖、版本关系与验收矩阵](../architecture/native-note-derived-artifacts.md)。当时 API/Web 源码和 v2 分支 Test 契约已实现：HTTP 200 Swagger 经 `jq -S` 后 SHA-256 为 `b745bcd4bc3fd7d09e8971d59cdcf2e40de2d9454a8e48dda6ff01ffd1b250b5`，官方 Web 生成/check exit 0；API 统一 build 0 errors/64 warnings，专项轮次 `50/57`、`24/25`、`1/1` 分账不求和；Web 最终全量 `349/349`（46 files，23:10:22，25.27s）/聚焦 `36/36`/storage `20/20`+TS/lint/lint/build exit 0。真实 Test API 首次 phone core 在旧 WebIndex `f6bd...` 探路 PASS（ordinary/admin0、ASR3+vision2+text4=9、确原图 bytes、无重 ASR、手工后再生成保 head），最终 dist `c915835a...` 的 phone+narrow 复验当时 PENDING。Android 当时先做本机人工稿 `10/10 PASS`，网络接入等待上游官方合并契约。以上 PENDING 已由 v0.35 API/Web 证据补齐或转成 Android functional 待办，不重写当时历史。

**退出定义**

- **`record-synthesis`**：有录音时复用已核对成功的逐段 ASR 转写/回执；无录音纯文字/照片笔记不伪造 ASR，允许任务关联为空。核对同一固定输入版本下的全部录音、Markdown、照片及已上传且版本匹配的手写图；Android 已有渲染/上传前置，仍需下游 SDK 实际消费与逐图覆盖证据。任一必需段缺失、失败或未知时只能显示缺口，不能发布“完整”。
- **`analysis-synthesis`**：分析只引用已核对的完整记录/材料来源，独立存储、独立版本、可追溯到模型/预设/地址/输入版本；不能覆盖 `record-synthesis` 或作为其替代。
- **SDK 图片批次**：当前视觉 SDK 当前只接受 PNG/JPEG，单次最多 4 张、单张最多 4 MiB、合计最多 8 MiB；超过时用可审计多批覆盖，不能只发前 N 张。WebP、超大单图、超总量或正文超限明确阻断，不静默截正文或转码。上传既有照片 32 MiB、手写派 40 MiB 和 WebP 能力不改。
- **候选/head**：首次生成、再次生成和恢复只追加候选，不移动 head。人工 CAS 冲突候选可保存但不改现 head；分支契约已含 `hasConflict`/`isHead`，最终以合并契约为准。
- **人工修订与重放保护**：自动稿、人工修订稿、新生成稿分别保存；补录、材料版本变化、重生成、并发和幂等重放都创建新版本或显式冲突，不覆盖较新人工稿；比较与选择路径可恢复。
- **失败与恢复**：ASR 成功片不重跑；停止保留已成功结果；未知回执先核对再决定；失败/缺口可按明确范围恢复；令牌不入 WorkData，owner/server/target/version 绑定不漂移。

**并行写集与顺序**

| 工作流 | 优先级 | 可并行写集（以现有目录/文件为界） | 关键依赖 | 独立验收门槛 |
| --- | --- | --- | --- | --- |
| API 契约与领域模型 | 历史 | `src/Pim.Core/Notes/`、`src/Pim.Api/Endpoints/NoteDerivedEndpoints.cs`、`contract/openapi.json`、对应 unit/endpoint tests | #528 的 owner/destinations 与固定目标授权；#520 任务/分片身份及已产转写/回执 | 已通过 PR#532 普通 merge；final contract `e38d1991...f0f9`；CI 40907900018/40994288275 与 Release 1011 成功 |
| API 编排与持久化 | 历史 | `src/Pim.Infrastructure/Notes/NoteDerivedPlan.cs`、`NoteDerivedCoverage.cs`、`NoteDerivedStore.cs`、`NoteDerivedService.cs`、`NoteDerivedJob.cs` 及 tests | 上项契约；#520 Hangfire/检查点；#514 文字 SDK；#515 视觉 SDK；#48 不可变材料版本；#528 授权快照 | 已随 PR#532 合并；最终 CI 覆盖固定材料、双产物、CAS、停止/未知/恢复、批次和输入上限；公开逐图 receipt 仍 P2 |
| Web 处理与修订界面 | 历史 | `web/client-web/src/features/ai/note-derived/`、生成类型与对应 tests | 真实 Test Swagger 经唯一脚本生成 Web 类型并接入 | 已随 PR#532 合并；最终全量 `349/349`；`c915835a...` 真实受控 phone/narrow core 与负向 PASS |
| Android 结果与修订界面 | P1 | `NoteAsrModels.kt`、`NoteAsrClient.kt`、`NoteAsrRepository.kt`、`NoteAsrTaskStore.kt`、`NoteAsrScreen.kt` 及邻近 navigation/test 文件 | 先实现本机人工稿；网络接入前取得上游官方契约快照。既有Android PR#51已MERGED`93d93437`，head `8b1d091`/R8 sha `67f0`，CI 40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL；产品 App source `1eaa3c5`/sha `001546`；API #528 post-merge contract receipt | 不手改生成物；master CI/release与P2 UI limitation仍需保留；固定版本任务/逐段/缺口/两产物/修订路径可操作 |
| QA 与真实运行证据 | P1/P2 | 独立 QA host/driver、`NativeNoteAsrTest.kt`、隔离 Test API/合成材料；不写生产 | 冻结 APK/test artifact；固定路径 phone 6+3/narrow/tablet；API 真实契约；owned AVD 身份复核 | QA FINAL `33/33 PASS / 0 FAIL`：phone12+narrow12+tablet9，跨R6/R7/R8；narrow ASR6 `OK6 @20:39:58`、Legacy3 `OK3 @20:41:09`、tablet ASR6 `OK6 114.559s @20:44:47`。Source组件3为同源R5 lineage另记，不拼成36 PASS。P2 UI limitation：窄Legacy dialog四字段/取消确认实见，但说明尾部大字号截于“拒绝”、浅过渡帧不足，fullBody NOT-VERIFIED；R7 selector fail与旧frozen失败保留。清理回执`qa-cleanup-receipt-20261029.json` SHA-256`75e66767...4ddcc335`：10 archive hash+3 profile manifests/no JWT/auth prefs；Host74716 stopped、9098不可达、59b912临时source移除；owned5556 nameguard后emu kill、adb空、未碰5554/PG |
| 文档与交接 | P1 | 本路线图、关联 current-state、验收记录；不与产品源码混写 | 各批准确 head 与回执 | 45 稳定 ID、32 差距行、历史失败和 OPEN 项不丢失；只更新事实/依赖，不复制 backlog |

API 领域/编排与同仓 Web 已由 PR#532 完成最终 CI、受控真实复验和普通 merge/release；canonical contract 为 `e38d1991...f0f9`。Android 基础 `2ae81d8a`、中间 `d37db9cd` 和新 source `54f6ace2` 已有 typed 14 operations、Draft PR #54 与两段 source；d37/testfix1 normal 前段后人工保存 USER head 超时，整体不 PASS。A/B/legacy 修复已提交 `54f6ace2`（6 paths SHA6/6、targeted28/App1999/Core69），但该 head CI/device 未完成，具体行为与验收项见[设计与验收矩阵](../architecture/native-note-derived-artifacts.md)。产品主 Gradle 构建、API 全量测试和设备执行按统一构建槽串行，不能并发覆盖同一产物。QA 的组件/设备验证只证明受控契约与界面路径，不等于真实部署升级后的端到端验证；真实升级、生产数据、OEM/课堂和长时后台继续 `NOT-VERIFIED`。

**门槛与优先级**

| 优先级 | 必须结束的事项 | 不得替代 |
| --- | --- | --- |
| P0 现有手机同步 | #525 保持 OPEN；API PR #529 已普通MERGED@`c5ff06fe`，PR CI 40838578113（6150/15/0）、master CI 40842715856与Release`api-v26.10.997`成功；P0 Mobile focused68/middleware+shape46/真PG8 PASS，Swagger匹配`eff87d96`。PG已按marker正常停止并保留数据。Android PR #50 source`8c55dc15`/run40845315327 required CI全SUCCESS，3项P1修复静态review关闭，并已普通MERGED到`235f1203`@2026-10-29T09:08:31Z；组件/真实upgrade仍NOT-VERIFIED | 不能用API/Android merge、Release、旧Android绿、285 JVM或组件测试关闭#525 |
| P0 发布事实 | 当前 API Release 为 [api-v26.10.1011](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.1011)，published 2026-10-30T03:22:29Z、non-draft/non-prerelease、target`3e3829aa`。Android functional 当前 Release 为 [android-v26.10.1137](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1137)，published 10:33:00Z、target字段master但tag解析merge`3844ef62`；bot Release[android-v26.10.1125](https://github.com/2746267826/pim-android/releases/tag/android-v26.10.1125)指向契约同步merge`4a05a68b`，保留历史。Windows bot 当前 Release [windows-v26.10.1104](https://github.com/2746267826/pim-windows/releases/tag/windows-v26.10.1104) 指向契约同步 merge`69defe7a`。历史Release`api-v26.10.997/995`、`android-v26.10.1123/1116/1112`继续保留。**GitHub Release ≠ production**，当前生产升级/健康仍`NOT-VERIFIED` | 不能用GitHub Release、bot merge、job绿、QA组件验证或旧镜像观测代替生产升级回执 |
| P1 Android ASR | PR#51已MERGED`93d934379b9a0e10318a9369e4d9d80a0c47e5e2`，exact head`8b1d091`/R8 sha`67f0`的source helper仅改test 3处Lazy先滚动，CI 40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL，merge tree与source tree一致。Source组件3按同源R5另记 | 不能用QA包、Source组件R5 lineage、旧987绿或旧JVM宣称master CI/release、完整UI或真实课堂完成 |
| P1 本切片（API/Web 与 Android 已交付） | PR#532/API/Web交付保持不变；Android PR#54 a850→merge`3844ef62`，exact CI41038364205、masterCI41040144538、Release`android-v26.10.1137`与3 assets均成功；App2008/Repo11/Core69全绿，normal9/conflict6/unknown6/narrow9四链分轮PASS | 不能把四链或GitHub Release写成同一TestAPK4/4、物理真机、真实课堂质量、production/fullMVP或公开逐图receipt |
| P2 独立设备证据 | 100/180 分钟真实课堂、长时后台/进程重启、Doze/锁屏/切应用、OPPO Find X9 Pro/Pad 4 Pro 笔/掌触、OEM 网络与升级后真实设备 | 不能用模拟器、合成 PCM、CI 或设备矩阵中一个窗口代替全部目标证据 |

#520 的现有实现入口为[固定版本收包](../../src/Pim.Infrastructure/Notes/NoteMaterialUploadStore.cs)、[持久任务](../../src/Pim.Infrastructure/Notes/NoteAsrTaskStore.cs)和[后台执行](../../src/Pim.Infrastructure/Notes/NoteAsrJob.cs)；#528在 `a3b35dc` 的 contract `eff87d96` 中增加普通账号 `GET /api/v1/native-notes/asr-tasks/destinations`。既有任务接口为 `GET/POST /api/v1/native-notes/asr-tasks`、`GET .../sources`、`GET .../{taskId}`、`GET .../{taskId}/segments`、`POST .../{taskId}/stop` 与 `POST .../{taskId}/retry`。客户端只可使用按当前契约再生成的类型，不另造路径或状态机。任务资源限额、未知回执和实际进程崩溃边界见[说明](../architecture/native-note-asr-tasks.md)。

每个切片在动工前写清：对应稳定需求、源码基线/依赖、唯一契约变化、材料/任务/产物版本关系、成功/失败/恢复路径、目标界面、验证方式与退出标准。切片完成后同时更新本表状态、32 项差距表和证据；任务声明不得只写“接 AI”或“优化 UI”。基础组件已有能力、假服务测试、受控真实模型、真实设备和已正式发布分别记账。

Windows 记录、Web 编辑、TASK-01 和 MIG-01 至 MIG-13 继续保留原阶段与独立验收，不因此次调整被删除，也不要求全部原生迁移后才能交付安卓笔记。既有六条提交/修改规则、3小时单段自动续段、首版手写与非必需实时转写均保持，不重复询问已确认产品决议。

## 8. 防偏航的轻量维护规则

- 保留“目标能力清单 + 迁移台账 + 决议记录”这一份权威索引。讨论稿入库后，在开发入口链接它；不要依赖 AI 或人的聊天记忆。
- 近期事项才拆成可验收 issue，关联需求编号和里程碑；远期事项保留在总表，不急于创建几十个空任务。
- 状态使用：待确认、已规划、进行中、已交付、延期、明确取消。延期写明原因和重新检查的阶段；取消须得到用户确认并保留记录。
- 每个交付 PR 写明推进的需求编号、实际覆盖端、验证方式和仍未完成部分；相关状态与能力变化同步更新。没有真机证据不能标“安卓后台录音已验收”。
- 阶段结束时对照完整清单核一次遗漏，而不只核本阶段 issue 是否关闭；Android 已完成不代表 Windows/Web 已完成，能预览笔迹不代表能编辑。
- 已有 issues 不因新计划自动失效。后续确认依赖、重叠和延期关系，不擅自关闭或丢弃原有工作。

## 9. 决议与变更记录

| 日期 | 内容 | 状态/影响 |
| --- | --- | --- |
| 2026-10-25 | 用户明确安卓手机为主要录音设备，需考虑锁屏与切应用 | 纳入 REC-02，不以 Web 录音 demo 替代验收 |
| 2026-10-25 | 用户认可原生客户端为主要体验、Web 保留、后台能力集成的方向 | 作为架构方向，不等同于所有技术细节已定 |
| 2026-10-25 | 用户接受分批交付，但要求保留完整路线图、防止偏航与遗漏 | 建立本文；阶段划分仍为建议 |
| 2026-10-25 | 创建 v0.1 本地讨论稿，下一步先确认真实使用流程 | 尚未入库，无实现完成声明，无已批准工期 |
| 2026-10-25 | 用户明确记录时本地优先，结束后整体上传笔记与录音；服务器 ASR，每段可查，分析用全部段 | 更新 REC-04、SYNC-01、AI-02、AI-06；事后冲突规则仍未决定 |
| 2026-10-25 | 用户明确编辑区为主、录音卡片实时文字、拍照插入、手写作为图片参与分析，输出完整记录与分析 | 新增 NOTE-04/05、INK-04、AI-06/07；块类型与产物格式待确认 |
| 2026-10-25 | v0.2 根据当时描述建议实时转写提前验证 | 当时将 AI-01 调整为 M0 验证、M1 建议目标；已被下方 v0.3 用户明确优先级覆盖，不是当前安排 |
| 2026-10-25 | 用户接受初期只安卓记录，但未来必须支持电脑记录 | 新增 REC-05；电脑只读不能算记录能力完成 |
| 2026-10-25 | v0.2 整理本轮流程与截图参考，区分明确需求、建议方案与截图候选 | 仍为本地未入库讨论稿；未实现产品、未创建 issue/PR |
| 2026-10-25 | 用户确认可拉伸的是正文内手写卡片，正文是普通自上而下的 Markdown | INK-04 形态确认；不做整页无限画布，不再作为待确认问题重复询问 |
| 2026-10-25 | 用户明确实时转写非必需、不紧急，原因是类似软件准确率体验差 | AI-01 调整为后续可选、未排期，不阻塞 M0-M3；取代 v0.2 的近期建议安排 |
| 2026-10-25 | 用户新增后台实时状态通知与录音实时波形，用于确认当前状态 | 新增 REC-06/07，纳入 M0 验证、M1 交付；区分实际信号、录音状态与保存成功 |
| 2026-10-25 | v0.3 更新编辑形态、优先级和录音反馈要求 | 仍为本地未入库讨论稿；未实现产品、未创建 issue/PR |
| 2026-10-25 | 用户提供完整分析长图及速览 HTML，要求参考理解 | v0.4 记录主题化整理、数据/对比/引用、问题与行动等参考方向；AI-08 详细规格待定，AI-09 速览及导出保持候选 |
| 2026-10-25 | 对照查看长图和 HTML 内容并建立本地参考拆解 | 未验证原录音保真性，不自动采纳科学结论，不将所有参考功能升级为首版承诺；仍未实现产品、未创建 issue/PR |
| 2026-10-25 | 用户要求继续，建立交互草图与首版范围草案 | v0.5 当时建议首版交付安卓 M1+M2 闭环，手写归属当时未定；已被下方 v0.6 用户明确一起交付的决议覆盖 |
| 2026-10-25 | 草图覆盖录音控制、后台通知、离线提交、部分转写失败、结果切换与来源变化提示 | 全部为模拟，只提供交互讨论依据；不能算原生后台录音、真实波形、云处理或冲突保护已经交付 |
| 2026-10-25 | 用户明确手写卡片不用滑块，采用右下角拖拽柄直接决定宽高 | INK-04 交互确认；取消早期高度滑块方案，画布/笔迹语义和原生手势仍需验证 |
| 2026-10-25 | 用户确定录音在底部但必须缩小，参考截图采用左下角悬浮 | NOTE-05 更新为应用内紧凑悬浮条；不等同于系统跨应用悬浮窗，后台仍使用持续状态通知 |
| 2026-10-25 | 用户明确“手写卡片一起交付” | v0.6 将基础手写编辑/保存/尺寸拖拽前移至 M1，手写图片参与 AI 前移至 M2；M3 保留多端输入和混合编辑扩展；不是功能完成声明 |
| 2026-10-25 | 用户提供 OPPO Find X9 Pro 和 OPPO Pad 4 Pro，典型课堂约 100 分钟 | v0.7 纳入首批安卓录音/手写验证；不再将平板平台写为未知或将其基础记录默认推迟至 M3；未完成真机验收 |
| 2026-10-25 | 用户愿意参考单段 3 小时上限，并提出同篇多段累计不封顶 | REC-01 记录单段产品上限方向、同篇不另设固定业务时长上限与资源约束；内部文件/ASR 可再分片，不等同于用户录音段 |
| 2026-10-25 | v0.7 提出到点保存并自动续段、暂停不计入采集时长及内部可靠落盘建议 | 当时续段与暂停计时待确认，已被下方 v0.8 用户接受覆盖；接续连续性与内部落盘仍需实测，不承诺零丢帧或物理无限 |
| 2026-10-25 | 用户接受上一轮录音建议并要求继续 | v0.8 将单段 3 小时到点自动保存续段、暂停不计入录音时长记为已确认，更新 REC-01/M1；不是录音功能已经交付 |
| 2026-10-25 | v0.8 细化提交、处理、补录、人工修订与多端冲突规则，并提出近期开发切片 | 当时六条规则作为新建议，已被下方 v0.9 用户接受覆盖；切片顺序/技术细节仍为建议，后续实施须从最新 origin/master 建立基线 |
| 2026-10-25 | 用户明确接受上一轮六条提交/修改/冲突规则 | v0.9 更新 AI-04、SYNC-02 及首版验收，不再把补录、结果编辑或冲突副本记为未决产品选择；尚未实现相关能力 |
| 2026-10-25 | v0.9 用户询问先定全局视觉/基础组件还是直接做功能 | 当时提出薄视觉基础与真实功能相互验证的方法，现已被下方用户同意覆盖；具体风格及原生实现并未因此定稿或完成 |
| 2026-10-25 | 用户同意薄视觉基础、关键页样板后立即接真实功能的顺序 | v0.10 将推进方法记为已接受；不扩大为全量组件库或全应用重设计，不让原生可靠性验证等待所有视觉定稿 |
| 2026-10-25 | 建立视觉规范提案和“清晰文档/安静纸面”关键页样板 | 手机/平板、两类页面和状态均为浏览器示意，方向尚待选择；未替换旧流程草图，未交付原生组件或真实录音/云处理 |
| 2026-10-25 | 用户明确延续 PIM 蓝色，取消常驻“PIM 笔记”品牌栏，结束操作右上角悬浮 | v0.11 更新 NOTE-05 和关键页样板；蓝色与这两项页首规则不再待确认，其他排版参数未全部冻结 |
| 2026-10-25 | 用户要求颜色和基础组件一处定义、多处引用 | 纳入原生迁移实施约束；样板集中主题资源并复用基础控件，后续按现有 Android/Windows/Web 主题入口落地，不宣称各客户端已完成改造 |
| 2026-10-25 | 用户确认视觉足够，统一后继续，不再反复打磨 | v0.12 将路线图及配套规格纳入权威仓库，启动 Android 薄主题与已有页面复用切片；真实录音、手写与云端闭环仍待后续证据 |
| 2026-10-25 | 用户授权合并并继续开发 | v0.13 合并文档 #506 和 Android 主题 #32，从合并后最新 master 开始 #33 原生录音内核；验证入口仅 debug，首版手写不延期，OPPO 长时证据仍单独保留 |
| 2026-10-26 | 用户要求继续推进 | v0.14 以 #35 推进混合文档/基础手写本地闭环，显式依赖尚未合并的 #34/#507；保留向量原稿、窄相机授权及导出范围，OPPO 真机、材料提交与云端处理未验收；不自动关闭总 epic |
| 2026-10-26 | 用户要求继续并提示可用安卓模拟器 | v0.15 以 #37 推进完整材料版本的本机前置能力，显式依赖 #36/#508；版本包含全部录音，原文和新补录不替换旧输入，仍未上传。模拟器证据与 OPPO 真机、长附件服务端及云端处理分别记录，不降低首版范围 |
| 2026-10-26 | 用户不满意早期成品 UI，继续后认为记录页纠偏“看起来还可以” | PR #39 提供真实原生本地预览，尚未合并；不以测试数量或本机预览替代完整 M1+M2 验收 |
| 2026-10-26 | 用户暂停功能开发，提供 OPPO AI 笔记 17.1.17 资料；随后接受利用建议 | v0.16 确定原生宿主/录音/存储/通知/后台任务不变，正文采用自己构建的离线成熟开源核心，以 Tiptap/ProseMirror 为方向；不移植混淆业务代码。当时仅更新文档，验证与替换留待恢复开发 |
| 2026-10-26 | 用户明确“其中的动画和字体都是可用的，已经确认过了” | 这两类按用户确认可用登记，不再标为许可待确认；保持 PIM 蓝色及统一资源引用。其他专有代码、图标、库、模型与云服务不因此纳入复用，首版及全部稳定需求范围不变 |
| 2026-10-26 | 用户恢复开发，要求最快形成 MVP，尽量不漏功能，跳过/不完善必须如实记录并后续完善 | v0.17 增加覆盖全部 32 能力编号的 MVP 差距表，保留 13 项既有业务迁移；PR #40 接入离线成熟正文但不重做录音/向量原稿，逐叶块撤销、参考素材未用及真机/长时/云端缺口明确保留。完整 MVP 仍需上传、逐段 ASR、完整记录和独立分析，不缩水为本地预览 |
| 2026-10-26 | 用户要求继续；继续选择清晰文档方向 | v0.18 在同一 API PR #509 实现服务器完整收包、独立分块/续传、用户隔离、版本冲突和容量/清理边界，导出唯一契约并生成 Web 类型；不改变 45 个稳定编号。Android #40 当前完整设备回归仍有首次结束失败，客户端上传/ASR/双产物未接通，全部如实保留 |
| 2026-10-26 | 用户提出并接受把服务器本地盘作为文件系统默认保底来源 | v0.19 新增正式 `local` provider；无云盘能存文件/旧附件，保留旧默认和引用，失效来源明确切换；笔记新分块共用底层但版本语义隔离。垃圾回收、容量运维和完整笔记闭环缺口保留，45 个稳定编号不变 |
| 2026-10-26 | 用户要求合并适合的 PR、完善可指导开发的路线图，并先以开源组件做完整 AI 调用基础 | v0.20 核对后将服务端 #507～#510 保留历史一次合并；安卓预览栈仍保留。审计已有 LiteLLM/MEAI，补组件比较、AI 基础验收、正式 UI/入口及发布门槛；研究不是实现，全部45稳定编号和32差距行保留 |
| 2026-10-26 | 用户担心 LiteLLM 过重，明确先记录模型类型设置、实际提示词/回复调用记录与统计，再讨论轻量方案 | v0.21 将 LiteLLM 从默认建议改为待讨论候选；撤回不保留实际内容的规划，记录统计细项未定；无运行配置或代码变更，完整笔记与45稳定编号保留 |
| 2026-10-27 | 用户接受服务端SDK与安卓视觉/动画组件方案，要求完整同步并校对四类进度 | v0.22 记录SDK直连、官方组件分工与实际未接入状态；新增已完成/进行中/马上要做/靠后快照，核对API合并、Android26/27失败和#42生成物PR，保留45编号、32差距行及完整MVP |
| 2026-10-27 | 用户要求按计划继续、完整登记缺项并适时合并，避免PR叠PR | v0.23 从最新master独立实现首条SDK文字闭环、管理页面/实际内容记录/统计与真实契约，验证后独立合并再从主干继续；实际SDK本机HTTP、合成Web、真实模型分别记账。当时视觉/ASR、流式/部分回复、旧业务迁移和完整笔记MVP缺口保留，45编号及32差距行不变 |
| 2026-10-27 | 用户要求继续，并授权指定本机网关/模型做真实验证 | v0.24 从合并后master独立推进多图视觉，真实SDK输入、默认文件来源留存、所有者读取和目标确认接通；4次真实合成小样包含文本、双图/换序与截断，未使用真实私密材料。ASR/流式/旧调用迁移、真实笔记和设备验收继续保留；全部45编号和32差距行不变 |
| 2026-10-27 | 用户要求继续并强调ASR厂商差异，当前只提供MiMo预设且容易扩展 | v0.25 使用受信任策略接口/注册目录与数据驱动页面，MiMo通过成熟SDK原生音频内容接入，保留独立凭据/预设版本/目标确认；单音频闭环不冒充整堂课，长录音持久步骤/全材料与全部45编号、32差距行保持 |
| 2026-10-28 | 用户在新对话要求延续原计划继续推进 | v0.28 从已合并master独立接通固定材料的持久分段ASR、时偏/覆盖、逐片查看与明确恢复；准确head验证和合并见切片PR。保留真实课堂/后台进程演练、流式/旧调用迁移、Android上传、完整双产物和全部45编号/32差距行 |
| 2026-10-28 | 用户采纳完整进度核查结论并要求按新顺序推进、维护路线图防漏项 | v0.29纠正AI-02/03/04/05状态；#520及契约#44/#35已合并，正式安卓入口/绑定上传开发中；通用流式/旧调用迁移并行保留，45稳定编号与全部验收缺口不变 |
| 2026-10-29 | Android 正式笔记入口与绑定上传交付，用户要求记录进度并保留验证边界 | v0.30 记录 #48 准确 head、CI、设备证据、合并及后续缺口；同时保留 #40 合并到 `50e6ac8d23ef` 与原修订44f1cd3的26/27失败独立保留。仍区分模拟器/隔离 Test API 与生产、真实课堂和完整 MVP |
| 2026-10-29 | 用户要求按 API/Android/Windows 当前 master 做本日进度校对，更新下一批入口并保留全部台账 | v0.31 分层记录已实现/已合并/已验证/客户端 GitHub Release/生产部署/完整 MVP；确认 #526 已 MERGED 于 `31ab1534`，将普通账号授权→契约再生成→逐段结果→全材料双产物→人工修订保护细化为第7节依赖链；API #525 单列 OPEN 并行缺陷，笔记上传不声称修复；同步 5 份关联文档过时状态/锚点；32 能力、13 个 MIG 与 32 行差距台账完整保留 |
| 2026-10-29 | 用户要求以当天最终可核事实校对 v0.32，并保留旧版本历史失败 | **v0.32 as-of**：API #528 MERGED`a3b35dc`，PR CI 40825191098 SUCCESS（6122/15/0），master CI 40829847795整体SUCCESS并发布[api-v26.10.995](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.995)（target`a3b35dc`、4 assets，保留为历史）；**GitHub Release ≠ production**，生产升级/健康NOT-VERIFIED。P0 API #529 PR/CI [40838578113](https://github.com/2746267826/pim-api/actions/runs/40838578113)最终SUCCESS/CLEAN（6150/15/0，total6165），MERGED到`c5ff06fe`@08:31:12Z；master CI 40842715856 SUCCESS，current Release[api-v26.10.997](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.997) target`c5ff06fe`@08:56:39Z、4 assets。P0 Mobile focused68/middleware+shape46/真PG8 PASS，Swagger匹配`eff87d96`。Android bot #49 master CI40839118734 SUCCESS、Release`android-v26.10.1112`→`e629febe`；Windows bot #37 masterCI40839109286 SUCCESS、Release`windows-v26.10.1102`。Android ASR当前PR#51@`dae87c32` all required CI/Gate SUCCESS；sourceTag`f9a6bd5`同卡merged semantics与QA`7b41a176` bool/exact state/逐片断言已提交，但未build成最终test APK/device binary；旧frozen`852bce4`/app`5da8`/test`6a84`为Tests run6/Failures3另有3 focused PASS只作历史，不转移到当前QA。Android P0 PR#50 source`8c55dc15`/run40845315327 required CI全SUCCESS、3项P1修复静态review关闭，已普通MERGED到`235f1203`@09:08:31Z；组件检查与真实升级仍NOT-VERIFIED，#525 OPEN；旧`089391ba`绿CI仅历史，5k stress Windows JBR21 C2 native crash历史NOT-VERIFIED，501 max1单测plain PASS。PG已停止且数据保留。45稳定编号、13 MIG、32差距行不变；#525/#505 OPEN，生产/真实升级/OEM/OPPO/课堂另证 |
| 2026-10-29 | 用户要求以 actual merge、最终 QA/CI、Release 与清理回执定稿 v0.33 | v0.33 阶段交付：Android ASR PR#51已普通MERGED exact head`8b1d091`→`93d934379b9a0e10318a9369e4d9d80a0c47e5e2`@12:53:42Z，tree`6294c2ab...`与test source tree相同；CI40874958748/master40877083407全SUCCESS/CLEAN；Release`android-v26.10.1123` published@13:05:35Z non-draft/non-prerelease target`93d93437`，3 assets/version JSON含SHA。QA FINAL33/33PASS0FAIL：phone12+narrow12+tablet9，跨R6/R7/R8；App source`1eaa3c5fe09d13460288eee8e0d6b3723f297cbc`/sha`001546`prod不变，2024JVM同product lineage保留。Source组件3按同源R5 lineage另记，不拼成36 PASS。P2 UI limitation：窄Legacy四字段/取消确认实见，说明尾部截于“拒绝”、浅过渡帧不足，fullBody NOT-VERIFIED。清理回执`qa-cleanup-receipt-20261029.json` SHA-256`75e66767...4ddcc335`；Host74716停、9098不可达、59b912 temp source移除、owned5556 nameguard后emu kill、adb空、未碰5554/PG。P0#50 Release1116历史；old queue upgrade E2E/production303/OEM/100-180课堂/全MVP仍NOT-VERIFIED；全材料双产物/人工修订保护仍为下一MVP切片 |
| 2026-10-29 | 以真实后端、Web、Android 与 QA 回执更新全材料双产物切片的当前状态 | v0.34 更新为 API/Web 已实现、受控验收中：v2 Test Swagger HTTP 200，contract `b745bcd4...b250b5`，官方 Web 生成/check exit 0；API build 0 errors/64 warnings，专项首次`50/57`（7 fail历史）、下次`24/25`（1 fail）、失败项单回归`1/1 PASS`，不可相加。Web 最终`349/349`（46files，23:10:22，25.27s）、note-derived`36/36`、storage`20/20`+TS/lint、lint/build exit0，日志`web-final-p1-*`；双视口合成QA各8状态但非真实API。真实Test API首phonecore在旧WebIndex`f6bd...`探路PASS：ordinary/admin0、ASR3+vision2+text4=9、确原图bytes、无重ASR、手工后再生成保head；最新dist index`c915835a...`的phone+narrow复验PENDING。Android draft`10/10`，纯结果UI在办，网络等待官方merge契约。P2公开coverage仍只聚合count，逐图/逐block receipt NOT-VERIFIED。45稳定ID、32差距行、13 MIG与历史事实保持；#505/#525 OPEN，未merge/release/fullMVP，production/303/OEM/100-180课堂仍NOT-VERIFIED |
| 2026-10-30 | 用户授权完成路线图/架构进度收尾并创建正常双语 docs PR | v0.35 记录 API #532 exact head`5026e455`→merge`3e3829aa959f34cd602d193a82462116b80d0737`，CI40907900018为6193pass/15skip/0fail（6208total、Web349/349），masterCI40994288275 Gate/Release/Notify success（build-mcp合法skipped），Release`api-v26.10.1011` target`3e3829aa`、03:22:29Z non-draft/non-prerelease；contract`e38d1991...f0f9`/1,119,472bytes/329paths/659schemas，CI真实artifact修复旧导出缺`/mcp`。Android bot#52与Windows bot#38仅契约同步，不是功能交付。最终Web`c915835a...`真实TestAPI+loopbackSDK phone/narrow core与negative PASS、late-success12/12独立PASS、9 calls/admin0；合成provider≠质量，narrow过渡截图≠完整视觉。Android derived仅本地HEAD`967ad5cb`、typed14ops、APK`2375cb77...`/20,476,254bytes、focused30/30+core69/69PASS；fullapp1985有33FAIL（旧ASRStore checkpoints写入，根因未证实）、testAPK未编译、设备E2E未执行、无functionalPR，不能称验收/Release。下一步33回归+testAPK→冻结APK/deviceE2E→functionalPR完整CI→真实课堂/OEM/长时→公开逐图receipt。45稳定ID、32差距行、13 MIG及历史失败保持；#505/#525 OPEN，production/fullMVP/OEM/OPPO/100-180课堂NOT-VERIFIED |
| 2026-10-30 | 用户要求为 Android functional 真实设备结果更新 v0.36，并保持 P1 未完成边界 | v0.36 保留 v0.35 全部历史事实：Android functional 从 officialbot`4a05a68b`固定为feature`2ae81d8a`（31files/7,534insertions），Draft PR#54，CI41004036926 head一致、6success+PR事件release按规则skipped、CI Gate success；`verified material writer`与完整revision metadata后本地App1990/Core69为0fail/0error/0skip、JBR21 BUILD SUCCESSFUL（日志当时worktree HEAD`967ad5cb`，源码随后固定；本地计数不与CI相加）。owned phone/R2真实正常E2E执行FAIL：一次确认新增task`41bf2402...`/`bb65ba87...`，同note/material/ASR且都引用同一CompleteRecord/IndependentAnalysis；本地FullRecord/Analysis intent相隔342ms；`NotesViewModel.kt:154`按kind fan-out而backend每task已双artifact，构成P1。summary ASR3/success3、vision0、text0，无重复视觉文本调用但不保证后续不重复收费。产品P1修复进行中且改动未提交，无新commit/APK/设备通过，P1=IN PROGRESS/NOT VERIFIED。下一步single-task fix→normal/negative/narrow→new APK+CI→课堂/OEM。contract-only PR52/38仍不是功能验收；45稳定ID、32差距行、13 MIG及111历史事实保留；issues收尾计划待独立核验收据：Android#37本机完整材料版本将正常close，#33/#35因明确剩余设备AC继续OPEN，#505/#525继续OPEN，不提前写CLOSED；production/fullMVP/OEM/OPPO/100-180/quality/public-receipts仍NOT-VERIFIED；真实模型调用本轮属禁止动作且未执行，真实模型质量仍未验；本条版本v0.36 |
| 2026-10-30 | 记录 duplicate-start P1 source READY、fresh JVM 与 issues 实际收尾 | duplicate-start P1 source READY，freeze `scratch/android-duplicate-start-20261030/READY-freeze.json`，11 product/test paths SHA-256 独立复核 11/11 match；一次 confirm 一 start、task 持久关联两 kind、VM/regen/UI 按 supports(kind)、费用同意说明每 task 双产物。targeted26/App1997/Core69 fresh JVM 均0fail/error/skip，统计不求和。11 paths 加旧 E2E 修正尚未 commit，新 CI/APK/device normal 未完成，不写 device PASS 或 Android 已交付。issues：Android#37于2026-10-30T06:52:09Z普通CLOSED(reason completed)，slice验收满足，[评论](https://github.com/2746267826/pim-android/issues/37#issuecomment-6516822157)，OPPO与完整MVP明确不含；Android#33/#35保持OPEN并已追加partial进度（[#33评论](https://github.com/2746267826/pim-android/issues/33#issuecomment-6516831970)、[#35评论](https://github.com/2746267826/pim-android/issues/35#issuecomment-6516835098)）；API#505/#525/#533保持OPEN且body未改，#533仅issue报告、未独立复验、无本轮修复。回执`scratch/note-derived-implementation/issues-followup-20261030/remote-actions-receipt-20261030.json`；本条版本v0.36 |
| 2026-10-30 | 记录 P1 exact commit 正常提交推送、新 CI/QA 待回执 | duplicate-start P1 source 已正常 commit+push：`d37db9cd3967aac31e40e8a3081bddb3204e4045`，parent`2ae81d8a`，title`fix(android): create one derived task per confirmation / 单次确认仅创建派生任务`，12files/373insertions/114deletions；远端分支head与该commit一致，PR#54仍Draft。freeze 11 paths SHA 11/11 match，targeted26/App1997/Core69 JVM对应fix freeze且0fail/error/skip、统计不求和。P1 source不再是uncommitted；但新exact-head CI PENDING，QA正从该commit build新App/Test APK并先跑normal，尚无新APK/device回执。R2 FAIL历史保留；不得写device PASS/Android已交付/merge/Release。上一条pre-commit checkpoint保留为历史；本条版本v0.36 |
| 2026-10-30 | 定稿 docs 交付快照：d37 exact CI、真实 normal 前段与 A/B 中间阻塞 | `d37db9cd` exact CI[41017246721](https://github.com/2746267826/pim-android/actions/runs/41017246721) required/CI Gate SUCCESS，PR事件release按规则SKIPPED；PR#54仍Draft。testfix1真实normal run`20261030-150514-normal-phone`前段PASS：一task、双artifact、Photo/Ink原字节、ASR3+vision1+text2=6；随后人工保存FullRecord USER head超时FAIL，versions仅`e11de5eb...`无USER version，原因待分类，normal/regen整体不PASS，negative/narrow未验。独立review确认A/B fixed-material scope P1，A入口可取较新B并按B创建收费task；旧JSON/`kinds` durable identity边界纳入窄修回归，当前IN PROGRESS/NOT-VERIFIED且无新收费/生产行为。新source/CI/device尚无；即使后续中间CI/normal为green也不得最终验收。保留R2双task FAIL历史、v0.35全部历史、45稳定ID、32能力/差距行、13 MIG与111历史事实；issues Android#37 CLOSED、#33/#35 OPEN、API#505/#525/#533 OPEN未改body；本条版本v0.36 |
| 2026-10-30 | A/B scope 与 legacy pending identity candidate READY，作为未提交中间快照 | `scratch/android-material-scope-20261030/READY-freeze.json` 为 `READY_PRODUCT_SOURCE_FROZEN`：6 paths SHA 6/6 match；显式 materialVersion pin refresh/publish/tasks/drafts/candidates，A重生成留在A；缺`kinds`按两server artifact解码，pending unknown复用同一TaskId，manual draft不混B。targeted28/App1999/Core69 fresh JVM均0fail/error/skip，统计不求和；此前26/1997/69与1990保留历史。该candidate尚未第二fix commit，真实normal/人工保存与新exact-head CI/device未验；d37 exact CI/normal前段不能替代最终验收。状态IN PROGRESS/NOT-VERIFIED，无新收费或生产行为；本条版本v0.36 |
| 2026-10-30 | 第二 fix commit 已提交推送；docs 交付快照定稿到该中间 source | A/B scope 与 legacy pending identity 修复已正常 commit+push `54f6ace22d7895e1d8a471d53fbe6f7ad4e83a6a`（parent`d37db9cd`，7files/418insertions/43deletions）：按固定材料隔离refresh/regen，legacy缺`kinds` pending复用ID；6paths SHA6/6、targeted28/App1999/Core69 fresh全绿且不求和，此前26/1997/69与1990保留历史。d37/testfix1真实normal前段一task/双artifact/Photo+Ink bytes/ASR3+vision1+text2=6 PASS后人工保存USERhead超时FAIL，原因未决，normal/regen整体不PASS，negative/narrow未验。`54f6ace2` exact CI与new-device PENDING，QA已从该head build；只有新normal实测才可写设备闭环PASS。PR#54仍Draft；保留v0.35历史、45/32/13/111及issues实际状态；本条版本v0.36 |
| 2026-10-30 | 记录 Android `afab38ee` 修复链与真实 normal 前段，准备 v0.37 | Android source链：`d37db9cd`单确认一task；`54f6ace2` A/B fixed scope+legacy原ID；`080f5c2`分离viewed/pending/confirmed head、head null仍可选、manual guard；`af24e2f`阻塞A稿Bhead与旧账号callback联网；`afab38ee`刷新detail回执经TaskStore持久化并发布当前task，修复server Succeeded/UI旧Running禁用regen。最新App2008/Repo11/Core69 zero，历史2006/2004/1999/1997/1985+33fail保留且不求和。af24 normal已证一task/双artifact/Photo+Ink actual bytes/USERhead2ac85107/provider6且无重ASR，但regen Disabled FAIL；afab新build normal/regen待测。PR#54仍Draft，无device PASS、negative/narrow、CI/merge/Release。issues Android37 CLOSED、33/35 OPEN，API505/525/533 OPEN；505 body base306f380b...aafca、17 checkbox11/6未comment/close。本条版本v0.37 |
| 2026-10-30 | 记录 afab normal/regen 真实 PASS、证据采集与签名 lineage | run164238 instrumentation OK1test/recover case-result PASS；initial`23d68e06...`/regen`41247e61...`各一次TaskId，USERhead`6fafd9ed...`，regenversion`8e902c5f...` nonhead parentUSERhead；ASR3+vision2+text4=9全success无重ASR，Photo120bytes`f40dea...`/Ink956bytes`79d099...`。首次外层FAIL仅runner evidencepull单文件Notadirectory，原FAIL保留且recover证据确认PASS、无模型重跑。rawafabE260/9FB与5556cert不兼容安装FAIL保留；同编译APK仅兼容重签App`D197CE5B...890E`/Test`D3B5A646...E2D`，source59A36F.../producttree201a4f...；独立核验ZIP payload App478/478/Test91/91同SHA、device manifest/runactual闭合且无新source。negative/conflict/unknown/narrow未验完；CI41027748619 buildandroid inprogress/Gate未final；PR54仍Draft，不写merge/Release/全矩阵PASS。本条版本v0.37 |
| 2026-10-30 | 最终 test-only head `f8fc4c0d` 已推送，等待 conflict/unknown/narrow 与 exact CI | f8fc4c0d parentafab，仅E2E2行真实UIrefresh；产品tree仍201a4f，不称App由f8重build；新E2Esource7AA5DA。afabGate历史全绿；normal/regen PASS仍由AppD197与已闭合lineage证明。f8 exactCI PENDING；conflict TestAPK正在build，unknown/narrow后续。PR54仍Draft，不写完整矩阵PASS/merge/Release。本条版本v0.37 |
| 2026-10-30 | f8 conflict-phone 真实 PASS，unknown/narrow 与 f8 exact CI 仍待验 | f8fc4c0 + source7AA5DA + sameAppD197；兼容testAPK`B76E37DA37322EAAED771080605F8E21D59E6DDE1D7381057C9AC8C70DB74EF0`，rawF286仅META-INF兼容重签且91payload同。instrumentOK1test39.628s；secondwriterhead`a4a02ee1...`，localconflict`62ebb80b...`为hasConflict/non-head，双方完整文本与本机draft保留；providerASR3+vision1+text2=6、冲突额外model0。初始runnerWindows反斜杠pull失败保留，同case完整recoverPASS且未重跑模型，非产品失败。normal的旧test source59A与本轮7AA分账；unknown/narrow、f8exactCI、PR54merge/Release仍未完成。本条版本v0.37 |
| 2026-10-30 | 最终 test-only 候选推进至 6afb，记录 unknown/narrow 测试修正且不提前签收 | 6afb6438 parentf8fc，2处test修正4+/4-补fixture与窄屏步骤，原断言不变；producttree仍201a4f，不称App由6afb重build；TestsourceDC2982、兼容TestAPK301E851D，CI41034029213 inprogress。normal afab与conflict f8虽同producttree但TestAPK/source不同，不拼成最终4/4。unknown旧FAIL是start回执丢后合法自动GET同IDreceipt→Accepted，server唯一task/providerASR3；临时QAproxy将GETtask503与POSTfault同时arm，保留VerificationPending/remote=null/显式同ID/0费用增量断言，R4fixture构建中且非产品补丁。narrow旧FAIL是先wait屏外LazyColumn目标再scroll而Dead等，现scroll优先后真实display/click，detail30s保留。旧FAIL保留、unknown/narrow当前仍未PASS、最终2case/CI/merge/Release待回执。本条版本v0.37 |
| 2026-10-30 | 最终 test-only 候选切换至 6dc70a0d，记录最后 E2E 与 unknown/narrow 边界 | 6dc70a0d 最后 E2E 14+/4-，只增加 coverage 异步 UI 等待与 narrow destination 滚动顺序；source45E3A6A8，兼容TestAPK`712788C98973FC981F816C1F921EB90F7AC8B6C5D9309BBC4B2DEEE2F0433B20`，AppD197/main tree201a4f 同源且无产品改动；CI41036134274 inprogress。unknown 上轮 fault/VerificationPending/explicit sameID/nocharge 核心 PASS，但 whole case 因即刻读旧 coverage 文案 FAIL，现补30s等待完整文案后仍最终assert；core PASS不等于whole case PASS。narrow上轮仍entry屏外wait顺序，minimal修正保全部原断言。旧FAIL保留、unknown/narrow未最终PASS、最终两case/CI/merge/Release待回执。本条版本v0.37 |
| 2026-10-30 | 四条功能链路真实 PASS 已齐，记录 a850 test commit 与条件交付边界 | normalphone afab+59A+TestD3B5 PASS/provider9；conflict f8+7AA+TestB76 PASS/provider6，双方全文与head正确；unknown 6dc+45E3+Test712788 PASS/provider6，VerificationPending/remote=null/fault本case各consume1、显式同ID模型0、完整coverage；narrow 6dc+dirtyFACCE/Test6CF 45.87s PASS/provider9，USERhead55ff...保留、regen8dee...nonhead parentUSER、dirty全文保留。四链均AppD197/main tree201a4f，但TestAPK/source不同，不拼同一TestAPK4/4。最终Androidhead a8507365正常testcommit并匹配FACCE blob，narrow构建在commit前，lineage如实分账。旧全部FAIL（含签名安装/runner pull）保留且与产品失败分账。PR54仍Draft；a850新exactGate全绿/PRclean才可普通merge，当前CIID/结果、masterCI/Release未来值不编造。非付费/production/OEM课堂/完整视觉验收。本条版本v0.37 |
| 2026-10-30 | 四链与cohort matrix独立复核；记录a850 CI中间态及交付后下一批门槛 | 四 cohort matrix 独立复核通过；CI41038364205自18:07:34（10:07:34Z）起Android unit/release build仍运行，其他required success，Gate未final。final source a850+FACCE blob/main201a4f/AppD197证据保持；条件merge仍需Gate全绿/PRclean。普通merge/Release后下一批按既有P0/P1/P2：先独立复现/验收#525/#533受控升级与上传故障链，#533仅报告/静态推断非已证根因且不做生产处置；再真实设备/OEM/100-180课堂/真实模型质量，未经授权不付费；再公开逐图/逐block receipt与未实现Windows/TASK/MIG，完整MVP不删不缩。GitHub Release不等于真实升级/production。本轮均为owned Android AVD 5556/API36.1 +真实TestAPI受控实测，非物理真机；cleanup final：`device negative-round-fast/device-cleanup-receipt.json`恢复1080x2424/density420/font1/rotation0、knownreverse清除、emu正常退出、adb空、无5554/wipe/uninstall；R4 `runtime-p1-5026e455-20261030-r4-6fe3e03fbc95/cleanup-receipt.sanitized.json` SHA`88C393F31F72CD4402B83D7F4A7D30923B241DC40F31BC9A6F35CBED51F84888`，7576无listener/health不可达，PID104740/144540/150828退出，host1AC09083 hashguard删除，API5026 tracked无diff、证据保留。本条版本v0.37 |
| 2026-10-30 | Android 切片普通 merge、master CI、Release 与资产回执定稿 | PR#54 heada8507365→merge3844ef62409765be0d5d229d8d3955cc31fcb261@10:21:27Z，main tree201a4f不变；exact PR CI41038364205所有required/Gate SUCCESS、PR release按规则SKIPPED；masterCI41040144538整体SUCCESS（Gate10:32:53Z/release10:33:03Z）；Release`android-v26.10.1137` published10:33:00Z non-draft/non-prerelease，target字段master但tag实际解析merge3844ef62。APK14,477,900bytes SHA`297b...ec89e`、shell2,550,430bytes SHA`fa3d...126ebd`、version.json480bytes SHA`8c1d...50ecf`。四 cohort 仍为真实TestAPI+owned AVD5556/API36.1分轮PASS（normal9/conflict6/unknown6/narrow9），同AppD197/debug兼容重签/main201a4f，narrow built-before-commit并匹配FACCE blob，TestAPK/source不同不拼4/4。GitHubRelease不是production/物理设备/完整视觉验收；OPPO/OEM/100-180课堂/真实模型/逐图receipt/fullMVP仍NOT-VERIFIED。旧FAIL/签名安装/runnerpull分账，45/32/13/111及新增历史保留。本条版本v0.37 |

| 2026-10-30 | 记录移动同步半开窗口、诊断导出、设备组件与 GitHub Release 交付回执 | v0.38：API tests #537 head`83b6e319`→merge`9631437d`@11:56:46Z，exact CI41048454669 required/Gate SUCCESS；masterCI41051709488 SUCCESS/Gate/release，Release`api-v26.10.1019`@12:26:48Z→`9631437d`，4 assets尺寸/SHA已核。Android product`dd90be25`+final test commit`5c4d8985`(parent dd90)，PR55 READY→merge`2968eea4`@12:19:41Z，exact CI41052159717 required/Gate SUCCESS；masterCI41054503798 SUCCESS/Gate/release，Release`android-v26.10.1140`@12:31:56Z，tag解析`2968eea4`即master head，3 assets尺寸/SHA已核。fullApp2023/Core69 0fail，window7/7/sync193、export focused133（与full重叠不求和），QA native Android emulator6/6(4 sync9.496s+2 export1.583s)；lineage append-only更正无精确runtimehead，源码blob/APK/log同源，首轮harness4 sync FAIL保留。日志只bounds，coreEntries仍全量DAO；旧APK升级/跨版本batchID/完整WorkManager/StatusUI/auth退避/极大DB/原flash/OEM/长课堂/production NOT-VERIFIED。#505/#525/#533 OPEN，#533 partial。45/32/13/111/history保留。GitHub Release不等于production。 |
| 2026-10-30 | 完成 v0.39 Web ASR 入口交付、Issue 收口与 #516 双通道排期 | Web PR540于14:29:51Z普通merge为`0e6d9f4adcf283138d68405d9ca65b04b7c51793`；exactCI41069510853 Gate/Web/Docker SUCCESS；masterCI41072022034 SUCCESS/Gate/release；Release`api-v26.10.1023`@14:38:55Z target merge、4 asset尺寸/SHA已核、pim-web-version26.10.1023/git`0e6d9f4`；管理员入口与普通用户权限隔离经preview/合成API浏览器QA6/6验证，非live-backend/production。执行按优先级/波次而非全串行：Web交付收口后，第一稳定性波次由#533 core安全修/受控设备QA与#539 semantic fix并行推进（无跨仓前置）；第二稳定性波次为#539显式processing/退避契约及Android消费者闭环；随后进入#516阶段1。#516内部阶段1→阶段2/3→阶段4→阶段5依赖不变；阶段1完成后阶段2云端多模态与阶段3 Windows模型管理可并行。物理OPPO/production未验收保留NOT-VERIFIED，不设为阻塞所有独立笔记规划的全局硬门。#533 OPEN，fullApp2027/0fail但只读复审发现分页并发cleanup漏行/空间预算退步，补修复测中。#539 OPEN，5/5为旧快照，day_overflow派生flag残留、较早late事件漏读及同设备并发边界补修中；5006rows回扫为隔离合成PG已复现事实但非现网唯一根因。#516产品仍PLANNED/NOT IMPLEMENTED/NOT-VERIFIED。API#525用户CLOSED/COMPLETED及4731入库仅为用户reported。两项用户决定待答。45/32/32/13/186保护基线保持。本条版本 v0.39 |
| 2026-10-31 | 记录 v0.40 当前交付、#539 修复 merge 与 #516 排期状态 | Web Release `api-v26.10.1023` verified/not production。Android PR #56 merge `998015d5b466c2d621c01beb3760c286a351037f`、Release `android-v26.10.1142`，fullApp `2034 / 0 fail`、focused `84 PASS` 为受控结果；#533 仍 `OPEN`，物理/OEM `NOT-VERIFIED`。API #539 仍 `OPEN`；PR #542 head `933a892b` 普通 merge 为 `f0ecf4501a81b6e8c105ca14cc1def844408d762`（2026-10-30T17:19:56Z），exact PR CI `41090756654` 整体 SUCCESS/Gate/API/Web/Docker。`api-v26.10.1031` published 17:55:13Z、非 draft/prerelease，target/tag `f0ecf450`；exact master CI `41097882594` 全部适用 job/Gate/release SUCCESS，4 assets 已上传，metadata SHA 不等于本地整包 byte hash，Release 不代表 production。新 processing 协议仍 WIP，尚在补齐契约、HTTP 证据与兼容；Android 旧 `202` 客户端测试已本地 commit `2d64024c41940560c53ff608623fc168d4bd2b73`（clean/no push），core `5` + app `8` PASS，新 consumer/not-before 未实现。`#525` 保持用户关闭；`#516` 五阶段/AC1–19 全计划、两项 decision pending、产品未实现。45/32/32/13/186 保护保持一致。本条版本 v0.40 |
| 2026-10-31 | 用户确认 #516 两项产品决策，v0.41 同步当前约束 | 本地 ASR 节点不可用时继续排队，只有用户主动手动切云端，禁止自动付费 fallback；手动切换沿用显式用户授权、不可变 route 与 attempt，queued 不得随默认配置漂移自动转云。Windows 节点仅处理当前登录账号任务，禁止跨账号或共享节点。两项自 v0.41 起为 confirmed，v0.40 及更早 pending 作为历史保留；模型/revision、运行时、硬件与阈值仍是工程前置。`#516` 保持 `PLANNED / NOT IMPLEMENTED`，五阶段依赖、AC1–19、45/32/32/13 与既有历史保护不变；阶段 1 最小负向验收仍 `NOT-VERIFIED`。本条版本 v0.41 |
| 2026-10-31 | v0.42 为 #516 追加 MiMo Batch 云端异步 provider 需求与排期 | MiMo Batch 是 `cloud-multimodal` 下独立 provider/异步 execution mode，不新增对外 route 枚举；保持 local 与 cloud 两类执行位置及既有 local/cloud-asr/cloud-multimodal 逻辑路由计划，旧三路由规划不表示新增 provider 已进入 OpenAPI enum 或已实现。每次 attempt 固定 owner、材料版本、segment、provider handle、region、所选模型、window、consent 与 `custom_id` 映射；管理员默认不追溯改写既有 attempt。已确认“local 离线仅手动切 cloud、Windows 仅当前账号”不变，不因节点离线自动转 Batch，也不改默认成本授权。候选模型只取 Batch 支持集与 audio capability 集交集；本轮范围仅 `/v1/chat/completions`，音频 Batch 端到端仍未实证。阶段 2 增加依赖阶段 1 与官方音频 Batch 能力确认的独立垂直切片；阶段 3/4 无新增依赖；阶段 5 覆盖 Batch history/partial/cancel/errors。五阶段、AC1–19 原编号及全部历史保护不变，产品 runtime 全为 `NOT-VERIFIED`，仍 `NOT IMPLEMENTED`。本条版本 v0.42 |
