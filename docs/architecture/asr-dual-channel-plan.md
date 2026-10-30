# ASR 双通道增量方案（#516 排期）

状态：**Web ASR 入口 GITHUB RELEASE VERIFIED / NOT PRODUCTION-VERIFIED；#516 产品未实现**。更新日期：2026-10-30。本文把 [API #516](https://github.com/2746267826/pim-api/issues/516) 的已提出需求整理为依赖明确、顺序合理、可独立验收的增量计划，不是工期承诺或功能完成报告。当前没有对应的 #516 产品源码、API 契约或 Windows 实现，也没有 #516 的 PR、merge、CI、Release 或 production 验收结论。

唯一需求、45 个稳定 requirement ID、32 项 capability/差距台账、13 个既有业务迁移 MIG 与全部历史事实继续由[原生客户端与笔记路线图](../product/native-notes-roadmap.md)维护。本文不新增或修改稳定编号，不复制第二套 backlog。`#516` 是既有 ASR 能力的扩展依赖来源，不是新编号。

## 1. 鲜读范围与事实校正

本次只读鲜读 API #516 得到：状态 `OPEN`、标签 `feature`、0 comments、更新于 `2026-10-30T13:36:30Z`；body SHA-256 为 `720e789e88af5241f7f3975666e569f9cb29b0873ba76be1b6fefaefe3a20ef9`。正文要求双通道配置、Windows 本机模型管理、服务端任务由客户端拉取、资源检查与自动退避、强制资源释放、状态可见性、MiMo 多模态预设、完成态重跑和 19 条验收标准。

Issue 开头的“PIM 目前没有语音转写能力”是过时背景，不能覆盖已经交付的能力：

- 已有受信任 `IAiAsrPreset` 注册目录、MiMo 专用 ASR 预设和单音频调用闭环；
- 已有固定材料持久分段 ASR、逐片覆盖/样本时偏、停止、明确失败片恢复与 Hangfire 调度；
- 已有普通账号 owner、固定材料版本、管理员维护的共享 destinations、目标/语言/同意快照；
- 已有 Android/Web ASR 任务消费者基础，以及 AI-04 的不可变候选、head/CAS 与重生成保护基础。

#516 的增量是“执行位置和通道路由、Windows 节点、拉取/租约、资源退避、本机推理清退、云端多模态语义、完成态重跑”，不是从零增加转写，也不授权本轮把整套本地 ASR 塞入实现。

Issue 中的 MiMo 6 段、CER、成功率和 8–10 分钟体验只作为需求依据；它们不是本仓实测，不能写成本仓 PASS，也不在本轮重跑或产生付费请求。

## 2. 目标边界

### 2.1 本轮纳入排期的能力

1. 服务端权威地解析通道路由，区分 `local`、`cloud-asr`、`cloud-multimodal`，保存任务/尝试的实际路由快照。
2. Windows 节点登记能力、安装模型、心跳和主动 claim；服务端不反向连接客户端。
3. claim/lease/heartbeat/ack/expiry 的幂等、owner 与 fencing 语义，迟到或重复回执不能覆盖新结果。
4. 领取前资源门槛、结构化退避原因、自动尝试上限和手动触发。
5. 本地推理子进程/作业清理，覆盖成功、失败、取消、客户端退出和崩溃重启。
6. 独立 MiMo 多模态预设、音频送达防护、截断切片、模型估算时间戳和纯文本回退。
7. 完成态跨通道重跑、不可变历史、原音频/任务元数据保护与异常提示。

### 2.2 Issue 明确非目标

- 服务端本机加载或运行 ASR 模型。
- 实时流式转写。
- 多客户端负载均衡与任务分片。
- macOS / Linux 客户端。
- 模型训练与微调。
- 用多模态模型做摘要、问答或说话人识别分析。
- 多模态模型的本地部署。

以下也不在本提案内：删除或重写既有 AI-01/REC-05/TASK-01；把 ASR 执行队列混同于任务板块的 TASK-01；改写 45/32/13/186 历史；将 Web ASR 配置入口的当前在建工作重复实现或提前宣称完成。

## 3. 与现有能力的对应

| 层 | 可复用能力 | #516 增量缺口 |
| --- | --- | --- |
| 预设与调用 | `IAiAsrPreset`、加密凭据、显式目标、SDK 调用审计、幂等/取消/未知恢复 | 目前只有专用 MiMo 预设；缺多模态类型、音频送达防护、`finish_reason` 截断恢复和模型估算时间戳语义 |
| 固定材料 ASR | 固定 owner/材料版本、分片计划、样本偏移、逐片回执、Hangfire 检查点、停止和明确恢复 | 当前主要由服务端执行；缺 Windows capability 登记、claim/lease、资源门槛、本地推理与清退 |
| AI 配置 | 模型能力、凭据、默认目标、共享 destinations、实际调用记录 | 缺 local/cloud/cloud-multimodal 分组、强制云端单条路径和节点模型能力 |
| Windows 宿主 | WPF/.NET、单实例、托盘、心跳、日志、计划任务、唯一契约生成基础 | 缺模型生命周期、CPU/内存/显存/电源探测、本机推理 worker、显存与残留进程清理 |
| 结果版本 | 固定输入、不可变候选、head/CAS、重生成不覆盖人工稿 | ASR 任务完成态重跑还需要独立的不可变 attempt/version 历史、选择与回读 |
| Web | 既有 AI 设置和笔记处理消费者基础；管理员 ASR 配置入口已交付 | PR、master CI、Release 与资产已核验；普通用户权限隔离和未知/失败状态经冻结 preview 合成 API QA，未改变权限模型。尚无 live-backend E2E，GitHub Release 不等于 production |

### 3.1 稳定编号扩展映射

本表只解释 #516 如何依赖既有编号，不改原文、不新增编号、不重定基线。

| 既有编号 | #516 扩展关系 | 保留边界 |
| --- | --- | --- |
| AI-02 | 正式转写、逐段可查、覆盖和状态可见性的执行位置扩展 | 保留“服务器调用 ASR”的既有原文与历史；新架构中 server 仍是编排权威，local 仅由登记节点执行，不把服务端变成本地推理器 |
| AI-04 | 原音频/任务元数据不变，重跑产生新版本并保留旧结果 | 不覆盖历史任务，不移动人工内容 head |
| AI-05 | 多类型配置、凭据、用量、隐私和能力类型扩展 | local/cloud 配置是能力分组；不把模型配置 owner 与材料 owner 混为一谈 |
| AI-06 | 全部音频与材料覆盖、时间戳仅用于定位 | 模型估算时间戳不能冒充样本级强制对齐，不能静默省略音频 |
| SAFE-01 | owner、明确同意、日志脱敏、节点不得读取无权账号音频 | 节点跨账号范围是待用户决定项，不能由实现自行扩大 |
| NATIVE-02 | Windows 原生宿主、认证、后台生命周期扩展 | 不等于 REC-05 Windows 主动记录或完整 MIG-13 |
| WEB-01 | 配置入口、任务详情和失败/重跑呈现 | ASR 配置入口已 GitHub Release 交付，管理员入口与普通用户权限隔离有受控 QA；任务详情、失败/重跑呈现仍属后续能力，不能把入口交付写成 WEB-01 全部完成 |
| MIG-10 | AI 设置中新增本机/云端/多模态配置与模型状态 | 不新增既有 MIG 清单之外编号，也不宣称既有 AI 设置迁移完成 |
| MIG-13 | 本功能相关的安装更新、托盘/通知、队列与客户端生命周期 | 只覆盖 #516 功能边界，不关闭 MIG-13 |

明确不借用：AI-01 是可选实时转写；TASK-01 是任务建议写入任务板块；REC-05 是 Windows 主动录音。三者都不因 #516 改变范围。

## 4. 路由与任务权威

### 4.1 配置与路由优先级

- 配置分为本地与云端两组；云端再区分专用 ASR 与多模态转写。
- 两者都配置时，Issue 要求默认本地；单条任务可显式覆盖，笔记页“云端处理”必须强制云端。
- 每次创建/重跑保存不可变路由快照：配置 ID/版本、通道类型、模型、节点（如适用）、语言、预设版本、实际地址/目标标识和同意版本。
- 后续修改默认配置、模型 revision、节点能力或卸载模型，不追溯篡改已存在的尝试；未发送工作按当时快照重新校验，失效则显式失败或重新确认。
- 本地不可用时是否自动切云端是 `PENDING USER DECISION`。安全默认候选是继续排队，仅用户手动切云端；未答复前不得把自动付费兜底写成默认行为或通过条件。

### 4.2 Claim / lease / fencing

- 服务端任务保持权威；Windows 通过心跳/轮询 claim，服务端不反向连接客户端。
- claim 返回一次性 lease、attempt ID、fencing token、固定输入/分片清单和路由快照。
- 同一 attempt 的重试只能复用同一幂等身份；重复 claim、lease 过期、旧 fencing token、错误 owner、节点离线、客户端退出或迟到 ack 均不得重复完成或覆盖较新结果。
- 租约过期不等于已经证明本机停止推理。服务端将其标为执行未确认并等待核对/显式恢复；不得在没有证据时推断上游未执行或免费重发。
- 服务端不把 Windows 任务派给 Hangfire 本机 worker；Hangfire 只承载服务端已有的编排/核对工作。

### 4.3 节点能力与账号范围

- 节点登记至少包含稳定节点 ID、当前账号/租户绑定、已安装模型与版本、资源能力、可用状态和最后心跳。
- 未安装、校验失败、卸载中、租约冲突或心跳过期的节点不得被列为可执行。
- 是否允许 Windows 节点处理其他账号音频是 `PENDING USER DECISION`。安全默认候选是仅当前登录账号；未答复前不得实现管理员共享节点或扩大音频范围。

## 5. 资源退避与强制清退

- claim 前检查 CPU 占用、可用内存、可用显存、电源和可选用户活跃状态。具体阈值属于目标模型/硬件工程决策，不在文档中猜值。
- 不满足门槛时任务保持待领取，并持久化结构化 `backoff` 原因；页面/客户端不得静默失败或显示“处理中”。
- 自动退避有可配置上限；达到上限后停止自动领取。客户端手动“处理堆积”可以绕过退避计数，但仍须重新通过资源检查。
- 本地推理必须置于可控子进程/作业边界，模型加载、卸载和资源采样均为可审计生命周期。
- 成功、失败、取消、客户端退出后都必须卸载模型并释放内存/显存；重启时先清理上次残留进程，再登记可用能力。
- 只证明“进程消失”不够。目标硬件验收要比较任务前、任务后和重启后的显存/内存基线，并证明无遗留模型进程。

## 6. 云端多模态增量

### 6.1 独立预设

计划新增独立的 MiMo 多模态预设，不改现有 MiMo 专用 ASR 预设和历史任务的语义。预设只包含 base URL、推荐/备选模型、格式/大小限制和模板；API Key 只进入既有加密凭据存储，不进入预设、UI 回显、日志或提交物。

Issue 记录的候选边界为 wav/mp3/flac/m4a/ogg、URL 音频不超过 100 MB、Base64 音频不超过 50 MB、模型估算约 `音频秒数 × 6.25 token`。这些是待实现/验收的需求输入，不是当前产品能力；实现时仍须以实际传输格式、SDK和服务端资源上限校验。

### 6.2 结果完整性

- 使用固定提示词模板要求逐字转写并按行给出起始秒-结束秒、说话人和文本；模板可由管理员修改，但版本进入调用快照。
- `content` 为空时读取 `reasoning_content`；两者均为空不得当成功。
- `prompt_tokens_details.audio_tokens` 为 0 或缺失时，本次调用明确失败并显示“音频未确认送达”，禁止把空串或模型自由发挥当转写。
- `finish_reason=length` 或单次失败时，按音频时长和已确认的单次边界切片重试；结果必须按源顺序拼接并保存每段覆盖，漏段/乱序不得发布为完整成功。
- 时间戳解析成功时映射到展示分段并标“模型估算”；格式缺失/畸形时回退整段纯文本并记录告警，不因此误判任务失败。
- 模型时间戳不替代固定样本偏移、覆盖与来源；多模态输出不得被描述为强制对齐。

### 6.3 当前证据边界

本阶段计划先用合成 HTTP/SDK 响应覆盖 `audio_tokens`、`content/reasoning_content`、格式缺失、截断和拼接顺序，再用经独立授权的短样或 8–10 分钟材料的受控测试。Issue 中用户 benchmark、CER 和成功率不能转写为本仓 PASS；没有付费授权时相关项保持 `NOT-VERIFIED`。

## 7. 重跑与不可变历史

- 已完成任务可“重新处理”，指定 local、cloud-asr 或 cloud-multimodal；默认沿用上次通道但允许修改。
- 重跑创建新的不可变 attempt/result version，保存触发原因（手动重跑、质量不合格）、路由快照、模型/节点和调用记录；旧结果可查看和选择，不直接覆盖。
- 原音频 SHA、任务 ID、owner、固定材料版本与原始任务元数据不因重跑改变。
- 重跑仍须经过目标校验、资源检查和音频未送达防护；“强制重跑”不等于绕过安全检查或自动产生付费调用。
- 空文本、明显循环重复、`finish_reason=length` 截断要标红并提示“建议重跑”。自动检测的结论与用户手动重跑原因分开记录，不能自动吞掉异常或伪报成功。

## 8. #516 五阶段实施顺序与真实依赖

阶段编号用于表达本表列出的真实前置，不是日历承诺。后续阶段局部证据不能替代尚未满足的本阶段前置或验收缺口。

| 阶段 | 独立交付 | 前置依赖 | 最小验收 | 当前状态 |
| --- | --- | --- | --- | --- |
| 1. 路由、节点与 claim/lease 契约骨架 | 通道枚举、配置分组、路由/尝试快照、节点 capability heartbeat、claim/lease/fencing/idempotency；不执行模型 | #505 当前保护基线；#520/#528/#532 既有固定输入/授权/契约；唯一 OpenAPI 再生成；两项用户 decision 作为参数化输入 | 隔离 Test API 证明单/双配置路由快照、合法/重复/过期 claim、错误 owner、旧回执和配置变化负向；不发送真实音频 | `PLANNED / NOT IMPLEMENTED` |
| 2. 云端多模态垂直切片 | 独立 MiMo 多模态预设、模板/凭据边界、响应解析、audio token 防护、截断切片和有序拼接 | 阶段 1 的 route/attempt 快照；Web 入口作为配置验收依赖；付费质量样另行授权 | 合成 SDK/HTTP 与隔离 API 覆盖第 6 节负向；8–10 分钟完整转写无授权时仅 `NOT-VERIFIED` | `PLANNED / NOT IMPLEMENTED` |
| 3. Windows 模型板块与节点登记 | 模型清单、资源需求/本机资源、下载/安装/卸载、摘要校验、节点心跳和能力登记；不领取任务 | 阶段 1 capability 契约；选定首个模型/运行时/目标硬件；Windows 原生宿主和唯一契约生成 | 安装/重启恢复/卸载/损坏摘要/权限不足/无卡或显存不足均可见；只有校验完成的模型被登记为可执行 | `PLANNED / NOT IMPLEMENTED` |
| 4. 拉取、资源退避、本地执行与清退 | Windows 主动 claim、资源检查、退避/自动上限、手动触发、本地推理子进程、完成/失败/取消/退出/崩溃清退 | 阶段 1、3；两项用户 decision；目标硬件/运行时；待确认的 owner/节点范围 | 受控 Windows + Test API 覆盖 AC3–7；资源原因可见；显存/内存回基线且无残留进程；无真实硬件证据不得写 PASS | `PLANNED / NOT IMPLEMENTED` |
| 5. 重跑、异常展示与 AC1–19 全链验收 | 笔记页强制云端、跨通道重跑、不可变历史、结果/失败/退避展示、异常标红、全矩阵回执 | 阶段 2、4、Web 配置入口；阶段 1 路由/租约；AI-04 既有版本保护模式 | 三通道重跑与旧结果回读、原音频/元数据不变、异常 UI、AC1–19 逐项证据；真实硬件/付费效果缺失时保持 `NOT-VERIFIED` | `PLANNED / NOT IMPLEMENTED` |

阶段 1 完成后，阶段 2 云端多模态与阶段 3 Windows 模型管理可并行推进。阶段 4 必须先具备阶段 3 的真实模型生命周期，阶段 5 必须等待阶段 2、阶段 4 以及路由/租约都可审计。该内部依赖不把等待物理 OPPO 或 production 验收写成阻塞所有 #516 规划与独立工作的全局硬门；尚未取得的证据继续标 `NOT-VERIFIED`。

## 9. AC1–19 阶段映射

下表全部是计划映射，不代表任何一项已经通过。结果列只允许 `PASS`、`FAIL`、`BLOCKED`、`NOT-VERIFIED`、`APPROVED-EXCEPTION`；本轮没有执行，故统一为 `NOT-VERIFIED`。

| AC | 验收要求摘要 | 阶段 | 关键依赖 | 计划最小证据 | 当前结果 |
| --- | --- | --- | --- | --- | --- |
| 1 | 仅本地配置走本地；仅云端配置走云端 | 1 + 4 | 双通道配置、claim、本地执行 | Test API 路由断言 + Windows 受控闭环 | `NOT-VERIFIED` |
| 2 | 两者均配置时默认本地 | 1 + 4 | 路由快照；自动兜底决定待确认 | 默认选择、任务覆盖、配置变化负向 | `NOT-VERIFIED` |
| 3 | 未安装模型或离线节点不获分配 | 3 + 4 | 模型登记、节点心跳、lease | 离线/未安装/卸载中/过期租约 claim 拒绝 | `NOT-VERIFIED` |
| 4 | 资源超阈值不领取，显示退避及原因 | 4 | CPU/内存/显存/电源探测和阈值 | 边界拒绝 + 持久状态/原因回读 | `NOT-VERIFIED` |
| 5 | 连续失败达上限停止自动，手动可继续 | 4 | 退避计数、手动入口 | 自动停止与手动仍过资源检查 | `NOT-VERIFIED` |
| 6 | 完成后显存回任务前基线 | 4 | 可控推理子进程、显存采样 | 目标硬件逐任务前后基线 | `NOT-VERIFIED` |
| 7 | 退出/崩溃重启后无模型进程与显存残留 | 4 | 作业边界、启动清理 | 正常退出、强制崩溃、重启三组证据 | `NOT-VERIFIED` |
| 8 | 笔记页“云端处理”强制云端并显示结果/明确错误 | 5 | Web 入口、强制路由、云端失败契约 | 真实 Test API + 页面交互与负向错误 | `NOT-VERIFIED` |
| 9 | 多模态预设只需填 Key，预设无密钥 | 2 | 加密凭据、独立预设 | 配置/序列化/日志/产物无密钥扫描 | `NOT-VERIFIED` |
| 10 | 8–10 分钟音频单次调用完整转写，不只开头 | 2 | 模型边界、切片与覆盖 | 经授权的独立样本；Issue benchmark 不可替代 | `NOT-VERIFIED` |
| 11 | `content` 空时读取 `reasoning_content` | 2 | 原始响应保留与适配器 | 空/有值组合的合成响应测试 | `NOT-VERIFIED` |
| 12 | `audio_tokens` 为 0 或缺失时失败并显因 | 2 | usage 解析与失败契约 | 0、缺失、正常三组响应 | `NOT-VERIFIED` |
| 13 | `finish_reason=length` 自动切片并有序拼接 | 2 | 时长切片、顺序/覆盖 | 截断响应、边界、拼接顺序、无漏段 | `NOT-VERIFIED` |
| 14 | 详情区分专用 ASR 与多模态 | 1 + 5 | attempt 快照与 UI | API 回读 + 实际通道/模型/节点展示 | `NOT-VERIFIED` |
| 15 | 时间戳解析成段；缺失时纯文本且任务不失败 | 2 | 解析器与模型估算标记 | 合法、畸形、缺失三组测试 | `NOT-VERIFIED` |
| 16 | 已完成任务可重跑并选择通道 | 5 | 不可变尝试、路由授权、资源检查 | 三种通道各一条重跑 | `NOT-VERIFIED` |
| 17 | 重跑旧结果可回溯，不覆盖 | 5 | AI-04 版本模式 | 旧/新结果回读、选择与并发负向 | `NOT-VERIFIED` |
| 18 | 重跑不改变原音频和任务元数据 | 5 | 固定源摘要与任务快照 | 原音频 SHA、任务 ID/owner/版本回读不变 | `NOT-VERIFIED` |
| 19 | 空文本、循环重复、截断标红提示重跑 | 2 + 5 | 异常检测与详情 UI | 三类异常判定与页面提示 | `NOT-VERIFIED` |

AC 中的“本地/云端”是通道语义；不能用服务端合成 provider、Hangfire 本机执行、CI 绿灯或文档计划替代 Windows 本机硬件证据。

## 10. 用户决策门与工程前置

### 10.1 PENDING USER DECISION

1. **本地不可用时是否自动切云端（会产生付费和新外发边界）**：安全默认候选是继续排队，只允许用户手动切云端。未答复前不实现自动兜底，也不把 AC1–4、8 的最终产品口径写成已批准。
2. **Windows 节点是否可处理其他账号音频**：安全默认候选是仅当前登录账号。未答复前节点能力、claim 和模型下载都必须按单账号隔离设计，不得猜测管理员共享。

两项决定不阻止 Web ASR 配置入口、#533/#539 或本提案的契约骨架等其它已授权工作；只阻塞阶段 4 的最终领取规则和 AC1–4、8 的最终验收口径。

### 10.2 工程前置，不是产品默认

- 选定首个 Windows 本地模型、推理运行时、目标硬件、资源阈值和模型包来源/许可/摘要策略。
- 确认服务端可承受的音频下载/流式传输方式、节点存储清理策略和重启恢复边界。
- 由唯一 OpenAPI 导出再生成 Windows/Web 类型；不手写猜测 endpoint 或字段。
- Web ASR 配置入口已 GitHub Release 交付；production 与 live-backend E2E 仍需独立证据，不重复实现，也不把 Release 写成生产部署。

## 11. 稳定性波次、并行边界与依赖阻塞

```text
#505 fresh protection baseline
        |
        +--> Phase 1 route / node / claim / lease contract
        |        |\
        |        | +--> Phase 2 cloud multimodal
        |        |            \
        |        +--> Phase 3 Windows model registry
        |                     \
        |                      +--> Phase 4 local pull / execute / cleanup
        |                                      \
        +--------------------------------------+--> Phase 5 rerun / UI / AC1-19

Pending decisions: automatic paid cloud fallback? node cross-account scope?
Engineering: Windows model/runtime/hardware/threshold selection
Priority/wave, not a fully serial hard dependency:
Web delivery closeout
  -> Stability wave 1 (parallel): #533 core safety/device QA
                                  + #539 semantic-preserving fix
  -> Stability wave 2: #539 processing/backoff + Android consumer
  -> Phase 1

True dependencies are only those stated in each phase. Cross-repo #533 and
#539 do not gate one another. A later wave may start when its own stated
prerequisites are met; physical OPPO or production evidence gaps do not block
all independent note planning.
```

旁路 issue 状态只用于当前状态整合，不是 #516 已完成前置：

- API #525 已由用户关闭为 `CLOSED / COMPLETED`。用户评论 `6523656382` 报告 server 997 + App 1140 的大批积压正常入库 4731、不再 500、重传风暴未再出现；这仅是用户 reported 生产观测，不是独立 production PASS。旧“OPEN”文字留在 v0.38 及更早历史，不重开、不改写。
- Android #533 仍 `OPEN`，body 已收窄为 App 1140 状态中心导出转圈/闪退；仍无 logcat，旧 `snapshotFiles` 源码引用未反映 1140 有界日志。当前 fullApp `2027 / 0 fail`，但只读复审发现分页并发 cleanup 可能漏行、空间预算存在退步，作者正补修并复测；`2027/0` 不能作为新修复最终 PASS。
- API #539 仍 `OPEN`，body 于 `2026-10-30T13:11:30Z` 增加小队列最后 5 条、1 ms window、200 `failedCount` + 空 `itemResults`、15 s 重试、处理 2–30 分钟、pending 15 分钟租约等报告。性能与 ID 保持测试当前 `5/5 green`，但正常 in-flight 协议尚未修改，用户线上尾批未验证；既有 2 PASS / 1 FAIL 与 5006 rows 回扫观察不足以认定现网根因。
- Web ASR 配置入口 [PR #540](https://github.com/2746267826/pim-api/pull/540) 已普通 merge 为 `0e6d9f4adcf283138d68405d9ca65b04b7c51793`。exact CI `41069510853` Gate/Web/Docker SUCCESS；master CI `41072022034` SUCCESS/Gate/release，Release `api-v26.10.1023` target merge、4 assets 尺寸/SHA 已核，`pim-web-version` 为 26.10.1023/git`0e6d9f4`。管理员入口与普通用户权限隔离经冻结 preview/合成 API desktop/mobile QA 6/6，未改变权限模型；不是 live-backend E2E 或 production。

当前执行按优先级/稳定性波次组织，不是一条全串行链，也不作日历承诺：

1. Web ASR 配置入口已完成 GitHub Release 交付收口；production 仍另证。
2. 第一稳定性波次：#533 core 安全修/受控设备 QA 与 #539 semantic fix 并行；二者无跨仓前置，可独立推进和验收。
3. 第二稳定性波次：#539 processing/退避契约与 Android 消费者成套闭环；该协议增强尚未实现。
4. 随后进入 #516 阶段 1 路由、节点与 claim/lease 契约骨架。

这里的“波次”是优先级而非全串行硬门；独立工作满足自身前置后即可推进。物理 OPPO 或 production 未验收只让对应证据保持 `NOT-VERIFIED`，不阻塞所有独立笔记规划。

## 12. 验收口径与禁止表述

- 每个阶段必须同时记录：准确 source/commit、契约版本、测试/证据、未验边界、对应稳定编号与 issue/AC 映射。
- 计划、代码存在、合成响应、隔离 API、Windows 真实硬件、云端真实效果、production 分别记账，不能相互替代。
- 文档合并只表示计划/状态更新，不表示 #516 实现、PR、Release 或生产完成。
- 未获独立付费授权不向真实 ASR/多模态模型发送请求；Issue 的用户 benchmark 不是本仓实测。
- 不写未来 PR 号、tag、CI run、merge SHA、发布日期或 production 结论。
- 任何一项真实证据缺失均写 `NOT-VERIFIED` 或 `BLOCKED`，不得以“计划完成”“接口可保存”或“测试已编写”写成模型已运行。
- 资源测试需要目标硬件与基线；没有显存/内存采样和残留进程检查，不写 AC6/7 PASS。
- 模型返回空文本、循环重复或截断时不得显示成功；建议重跑与已自动恢复必须区分。

## 13. 历史保护

本方案及 v0.39 当前状态覆盖层不重定原 v0.38 和更早正文：45 个稳定 requirement ID、32 个 capability ID、32 行差距、13 个 MIG、111 条早期历史事实及后续历史，总计至少 186 条保护事实不得删除、缩短或重定。新增状态不在既有清单之外新增 MIG/TASK 编号或其他伪稳定编号。
