# 原生笔记全材料派生稿：设计与验收

状态：**API/Web 既有交付不变；v0.37 Android 切片已普通 merge、master CI 与 Release 交付。head [`a8507365ba35b4cc1c8af0efa67a1bd5e8b7af2e`](https://github.com/2746267826/pim-android/commit/a8507365ba35b4cc1c8af0efa67a1bd5e8b7af2e) → merge [`3844ef62409765be0d5d229d8d3955cc31fcb261`](https://github.com/2746267826/pim-android/commit/3844ef62409765be0d5d229d8d3955cc31fcb261)，main tree `201a4f...` 不变。exact PR CI 41038364205 全 required/Gate SUCCESS、PR release skipped；master CI 41040144538 SUCCESS；Release android-v26.10.1137 target merge 3844ef62，3 assets SHA/尺寸已核。normal、conflict、unknown、narrow 四 cohort 在真实 Test API + owned Android AVD 5556/API36.1 分轮 PASS，App D197/debug 兼容重签；四链 Test APK/source 不同，不拼成同一 Test APK 4/4。narrow 匹配 dirty FACCE blob但构建早于 a850 commit。GitHub Release 不是 physical-device、production、OEM/课堂、完整视觉或完整 MVP 验收**。更新日期：2026-10-30。

本文定义“固定版本全部材料 → 完整记录 + 独立分析 → 人工修订/重生成保护”的领域边界、必须成立的不变量和验收矩阵。API/Web 已完成 PR #532、最终 CI 与 `api-v26.10.1011`；Web 真实 Test API + loopback SDK phone/narrow core 与负向路径已有证据。Android 从 officialbot 进入 `d37db9cd`→`54f6ace2`→`080f5c2`→`af24e2f`→`afab38ee`→`f8fc4c0d`→`6afb6438`→`6dc70a0d`→`a8507365`→merge `3844ef62` 修复/测试/交付链：产品修复依次收紧 one-task、A/B scope/legacy identity、head 语义、manual-save guard 与 task 状态刷新；测试链补真实 UI refresh、fixture/窄屏步骤、coverage 异步等待与 destination 滚动顺序。最新本地 JVM 为 App2008/Repo11/Core69，均零 fail/error/skip；四 cohort 在 owned Android AVD 5556/API36.1 + 真实 Test API 分轮 PASS，但 Test APK/source 不同。exact PR CI、master CI 与 Release 已成功；旧全部 FAIL 保留并与产品失败分账。GitHub Release 仍有明确边界：物理设备、production、OEM/课堂、完整视觉与完整 MVP 未验。

需求映射：AI-03/04/05/06/07/08、NOTE-04/05、SYNC-01/02、SAFE-01。本文不新增稳定需求编号，不替代 [总路线图](../product/native-notes-roadmap.md) 的 45 个稳定 ID、32 项能力差距行、13 个 MIG 或既有历史失败台账。

## 1. 已冻结约束与当前实现

### 1.1 已冻结的产品行为约束（不代表 API 契约）

以下结论是产品与验收约束，不依赖具体端点和字段名：

1. 一次派生处理必须固定 owner、笔记/材料版本、源清单及摘要、现有成功 ASR 转写/回执（仅当存在录音）、照片、Markdown、与同一版本匹配且已上传的手写渲染图、模型目标、预设/地址/语言和明确同意。没有录音的纯文字/照片笔记允许 ASR 任务关联为空，不得强制伪造或创建 ASR。
2. 完整记录与独立分析分别对应独立的 `record-synthesis` 与 `analysis-synthesis` 产物身份、版本、状态、来源快照和生成/修订历史，不得用一份内容或一个版本号代替另一份。
3. 自动生成、人工修订和再次生成都只能追加不可变候选版本。生成成功不移动 head；人工提交使用 CAS，冲突候选可以保存，但不得改变现有 head。canonical contract 已包含 `hasConflict` 与 `isHead`。
4. 任一必需材料缺失、失败、未完整确认、版本不匹配或被替换时，不得把产物标记为“完整”。停止、未知和部分成功都必须保留已有结果并显示缺口。
5. 重放、并发、恢复和重生成不得覆盖较新人工修订，不得重复产生未确认的付费调用，也不得把旧目标的成功结果挂到新目标下。

### 1.2 canonical 契约与已交付实现现状

canonical contract 现为 `e38d1991b372174a3a4bac10df0a35eee90de92d268a5f1d35762d76da07f0f9`（1,119,472 bytes，329 paths，659 schemas）。CI 从真实生成 artifact 校验收紧了旧本机导出缺 `/mcp` 的问题，所有历史失败保留。Android officialbot PR #52 与 Windows bot PR #38 已同步契约/生成物，分别 merge `4a05a68b`、`69defe7a`；这证明消费者契约同步，不证明 Android derived 原生功能已实现或验收。

| 范围 | 当前实现与证据 | 仍未完成 |
| --- | --- | --- |
| API 领域/编排 | `NoteDerivedDtos`、endpoint、planner、coverage、service、store、job 已随 PR #532 合并；exact CI 40907900018 与 master 40994288275 成功，Release 1011 指向 merge；固定正文、ASR 计划/回执、原图字节、双产物、候选/head、CAS、停止/未知/恢复均有最终门禁和真实受控回执 | production、真实课堂质量和 Android functional E2E 仍未验证 |
| Web（同仓） | 已使用正式生成类型实现“笔记处理”并随 PR #532 合并；exact PR Web `349/349`（46 files）、note-derived 聚焦 `36/36`、storage `20/20` + TS/lint、lint/build exit 0；最终 `c915835a...` 真实 Test API + loopback SDK phone/narrow core 与 negative PASS | 合成 provider 不代表真实质量；narrow 过渡截图不证明完整视觉；公开逐图 receipt 仍 P2 |
| Android（跨仓） | a850→merge3844ef62，PRCI41038364205全required/GateSUCCESS、masterCI41040144538/Release`android-v26.10.1137`及3 assets成功；owned AVD5556/API36.1+真实TestAPI四 cohort分轮PASS：normal9/conflict6/unknown6/narrow9；AppD197/debug兼容重签、main tree201a4f同源，narrow匹配FACCE blob但构建早于commit | TestAPK/source不同，不拼同一TestAPK4/4；GitHubRelease不是物理设备/production/OEM课堂/完整视觉/fullMVP；旧FAIL/签名安装/runnerpull分账 |

当前 API 已实现的关键不变量不只停留在设计：固定版本 `document.md` 原文和 hash 校验；成功 ASR 按完整计划、slice offset、audio/owner/version/receipt 目标核对；照片/手写图使用原字节 SHA 并读取原图；视觉按每批最多 4 张、单图 4 MiB、单批 8 MiB，最多 32 图/8 批；合成输入上限 200,000 字符、单证据 50,000 字符；WebP/超限明确 blocked，且不改变上传 32/40 MiB 与既有 WebP 能力。`record-synthesis` 与 `analysis-synthesis` 独立调用、独立不可变 version；分析实际读取记录正文与来源；生成不移动人工 head；人工 payload 幂等，CAS 冲突保存双方全文，`Mutation.IsHead` 区分 crash 候选；locator 可修复；no-op 不变 Revision；stop 保留 success/unknown、不自动重发，retry 只覆盖显式范围并要求 ack。

**保留的公开可观察性限制**：当前 P2 公开 coverage 只聚合 count，没有逐图/逐 block 成功 receipt；内部测试已证明实际 provider bytes 和逐图消费，但不能据此宣称用户已获得逐项 audit 能力。DA-07/DA-31 因此只能记“内部受控覆盖 PASS，公开逐图回执 NOT-VERIFIED”。

## 2. 概念模型

下列名称只表达领域概念，不是最终契约字段名：

| 概念 | 职责 | 不可变内容 |
| --- | --- | --- |
| 材料快照 | 指向一个已发布固定版本，固定 owner、源清单摘要和文件摘要 | 版本身份、源清单、文件集合与摘要、来源块/顺序 |
| ASR 证据快照 | 固化既有成功转写及其执行回执，作为有录音时的材料覆盖证据 | 任务、分片身份、成功状态、转写、回执关联、原目标和尝试身份；无录音时任务关联可为空 |
| 派生运行 | 一次完整记录或独立分析的生成/恢复尝试 | owner、产物种类、来源快照、目标/同意快照、操作键与输入指纹 |
| 派生产物 | 具有稳定身份的 `record-synthesis` 或 `analysis-synthesis` | 产物种类、属主、创建来源；其版本只追加不覆盖 |
| 产物版本 | 一次自动生成、人工提交或恢复完成后的不可变内容 | 内容、来源快照、父版本、生成/修订来源、时间与覆盖结论 |
| 覆盖结论 | 解释为何完整或存在缺口 | 逐项必需来源、已解析状态、缺口/未知、是否可发布为完整 |
| 候选版本 | 新生成、恢复或 CAS 冲突产生、尚待显式采用的版本 | 内容、父版本、冲突标记与非秘密回执；创建候选不移动 head |
| head/选择指针 | 指向当前默认展示的产物版本 | 当前版本身份与乐观并发版本；只能由显式选择改变，改变时不改写历史版本 |

“完整”是覆盖结论，不是模型质量、事实正确性或课堂效果结论。全部必需来源已处理且覆盖证明成立，仍只表示输入完整；真实内容准确性和课堂质量需要单独验收。

## 3. 固定全部材料与覆盖证明

### 3.1 必需来源集合

必需来源由固定版本的材料清单确定，不读取处理时“最新”的笔记。至少包括：

| 材料 | 固定与覆盖要求 |
| --- | --- |
| 录音及成功 ASR | 遍历该版本全部录音/分片；只复用同一 owner、同一版本下终态成功且有转写和回执的既有分片结果，不重新发送成功音频。每个成功分片必须能关联到任务、分片身份、原目标和调用尝试。 |
| Markdown | 固定该版本的导出的 Markdown 原文及摘要，并保留其与结构化块身份/顺序的关系；不得用之后当前笔记的 Markdown 替换。 |
| 照片 | 固定清单中全部关联照片的原文件、类型、摘要和顺序；缺图、损坏、未知类型或仅有占位引用均为缺口。 |
| 手写图 | Android 固定包已把每个手写块渲染为图片并随固定版本上传，**上传前置不是本切片缺口**。设计要求下游按同一版本匹配图片、块身份、笔迹摘要、尺寸和清单关系实际消费；真正未验证的是 SDK 图像输入、逐图覆盖、批次合并与结果关联证据。图片版本错配或未能实际进入下游时均为缺口。 |

录音清单、未知块和附件关系也属于覆盖证明范围。未知块不能被静默忽略或当作空文本；只要它可能承载用户材料，就必须明确阻止“完整”结论。

没有录音时，固定版本仍可由 Markdown 和照片组成；此时 ASR 任务关联为空是合法状态。只有存在录音时，才要求对应成功 ASR 转写与回执；不能因 `AsrTaskId` 为空而拒绝纯文字/照片笔记，也不能凭空生成 ASR 证据。

### 3.2 当前 SDK 图像与正文限制

本切片直接复用当前视觉 SDK 的既有输入边界，不扩成新的上传限制：

| 项目 | 当前本切片限制 | 处理要求 |
| --- | --- | --- |
| 图片格式 | 下游 SDK 仅接受 PNG/JPEG | WebP 或其他未支持格式必须明确阻断并显示缺口；上传侧既有 WebP 能力保持不变，不得因上传成功冒充模型可消费 |
| 单次图片数 | 最多 4 张 | 超过 4 张必须设计可审计的多批覆盖，不得只发送前 N 张 |
| 单张大小 | 最多 4 MiB | 超过时明确阻断；不得静默缩放、截取或转码来绕过 |
| 单次图片总量 | 最多 8 MiB | 可按批次拆分，但每批、每图及批次合并结果都要进入覆盖证明 |
| 正文/转写 | 受当前 SDK 输入上限约束 | 超限明确阻断；不得静默截正文、转写或其他材料。只有完整分块及可核对的合并覆盖才可使用，不能用前几段冒充全量 |

多批不是降低完整性：必须记录每个原始图片/文本来源在哪一批被消费、得到了何种结果及如何合并。任何一批失败、未知或未覆盖时，完整记录和独立分析都不能标记完整。若多批方案无法证明全部材料被实际消费，必须阻断，不能退回“只处理前 N 张”。

上传容量的既有边界保持不变：照片 32 MiB、手写派生图 40 MiB，并保留既有 WebP 上传能力。上传接受不等于当前 SDK 能直接推理；在本切片限制内，超过 4 MiB 的单图、WebP 或总图数/总量超限资料必须被明确标为不可处理/缺口，由后续独立能力扩展解决，不能在本切片静默转码或降级。

### 3.3 成功、部分与未知

每个必需来源必须有且仅有一个可审计的解析结果：

- **已解析**：来源身份和摘要匹配，处理结果成功，回执或内容证据完整。
- **缺口**：来源缺失、版本不匹配、摘要不符、处理明确失败，或清单要求但未找到。
- **未知**：已有外发或生成尝试，但无法确认是否完成、是否计费或结果是否已持久化；未知不等于失败，也不等于成功。

部分成功记录可以保留和展示，但只能标为“不完整/存在缺口”。只有全部必需来源均已解析，且没有缺失、替代、重复归属或未确认结果时，才能产生“完整”结论。

空转写必须有明确占位和试听/复核提示，不能被静默删除。它可证明调用已完成，但不证明识别质量或语义完整；是否允许发布为“完整记录”需由冻结后的契约把“调用覆盖”和“内容质量”分开表达，且不得把空转写当成无问题文本。

### 3.4 固定后不漂移

派生运行创建后，以下变化不得回写原运行：

- 当前笔记切换到更新材料版本；
- Markdown、照片或手写再次编辑；
- 默认模型、目标或预设变化；
- 账号状态、权限或同意变化；
- 原 ASR 成功分片后来出现新尝试。

需要包含新材料的操作必须新建运行和新产物版本，并明确展示来源版本变化。旧运行可以保留并可审计，但它只能引用创建时的快照。

## 4. 双产物与版本关系

### 4.1 独立身份

`record-synthesis` 与 `analysis-synthesis` 各自具有独立产物身份、版本序列、head/选择指针、生成运行和覆盖结论。禁止：

- 用同一个版本号同时代表两份内容；
- 让分析覆盖记录，或让记录更新时原地改写分析；
- 只保存分析摘要而丢弃被引用的完整记录版本；
- 只保存完整记录而无法追溯分析使用了哪个记录版本、模型目标和材料快照。

### 4.2 分析对记录版本的依赖

一次完整分析应引用一个准确、不可变的完整记录版本，并复用该版本的同一材料快照。记录后来被人工修订时，旧分析保持原样并标记为“基于旧记录版本”，不得自动声称已分析新内容。用户显式再次生成后产生新的分析版本；旧分析版本仍可读取。

若完整记录本身存在缺口，不得产生“完整分析”结论。任何部分模型输出都必须标为部分/草稿，不能作为独立分析完成证据。

### 4.3 不可变版本图

版本关系允许分叉，不允许原地改写：

```text
自动生成 A0
├─ 人工修订 H1（父 A0）
│  └─ 人工修订 H2（父 H1）
├─ 再次生成 R1（父 A0，与 H1 并列）
└─ 恢复完成 R2（父 A0，显式恢复范围）
```

自动生成结果先成为候选版本，**不自动替换或移动 head**；首次生成、再次生成、恢复和 CAS 冲突候选都遵守这一规则。head 只能由用户显式选择改变；版本内容创建后不可修改。人工自动保存也只能写入当前人工版本或新草稿，不得覆盖自动稿的历史内容。

## 5. 人工修订、重生成与并发

### 5.1 人工修订

- 人工提交产生新的不可变版本，记录父版本、修订来源和准确材料快照。
- 人工修订不得改变源 ASR 转写、原照片、手写向量或同版本渲染图。
- 对完整记录的人工修订不会自动改写分析；对分析的人工修订不会反向覆盖记录或原始笔记。
- 删除、隐藏或重新选择版本是独立操作，不得通过覆盖历史来“清理”版本。

### 5.2 重生成

- 默认重生成使用原材料快照和原目标语义；若要用新材料或新目标，必须新建运行并重新确认。
- 重生成产生并排新版本；不能清空、重命名或覆盖人工版本。
- 已成功的 ASR 分片不重跑。若重生成只需已有转写，不得以“重新整理”为由重复发送录音。
- 生成失败、停止或未知时，候选版本不能被标为完成，也不能使选择指针自动跳过当前人工版本。

### 5.3 并发与冲突

所有人工写操作必须携带或服务端核验“预期当前版本/修订”并执行 CAS。两个客户端基于同一父版本提交人工修订时，后到的 CAS 冲突候选可以保存，但不得改变现有 head，也不得覆盖先到版本；由用户显式选择、合并或另存新版本。

CAS 冲突候选的版本 ID 表达和 `hasConflict`/`isHead` 已进入 canonical contract 与最终 CI。行为不变：冲突内容不丢失、当前 head 不变、冲突可观察、用户选择后才改变 head。

较旧的生成响应晚到，而当前已有更新人工版本时：

- 生成结果可作为旧父版本下的候选版本保留；
- 不得覆盖当前人工版本或改变 head/选择指针；
- 页面必须明确显示“基于旧版本”；
- 用户选择旧候选前必须看到版本关系。

### 5.4 幂等重放

每次创建、生成、停止、恢复和人工提交都需要 owner 作用域内的操作身份与输入指纹：

- 同一操作身份、同一输入指纹再次到达：返回原操作结果或进行核对，不创建第二个付费运行、版本或人工修订。
- 同一操作身份、不同输入指纹：显式冲突，复用到其他材料/目标/父版本均被拒绝。
- 响应丢失：先核对已有回执；不得仅因客户端未收到响应就自动重发。
- 未知结果：保留未知状态，人工选择恢复后才分配新的尝试身份；若可能重复计费，必须明确提示。

## 6. 所有者、目标、同意与出站

每次外发前重新核验：

- owner 与材料/任务/产物属主一致；非 owner 不得读取、停止、恢复、修订或重放。
- 材料版本、源清单摘要、ASR 回执和产物父版本仍匹配。
- 目标仍启用，模型/预设/地址/语言与创建时快照一致，且属于管理员允许的共享目标目录。
- 本次运行具有明确、可追溯的模型同意；上传同意不能替代模型调用同意。
- 目标或配置改变、账号停用或权限失效时停止后续外发，保留已有记录，不静默切换目标。

令牌、JWT、私有下载地址和模型凭据不得进入任务 WorkData、派生版本或日志。允许持久化的是 owner 标识、公开/内部目标身份、摘要、同意快照和非秘密回执引用。

## 7. 停止、未知与恢复

| 场景 | 必须行为 | 禁止行为 |
| --- | --- | --- |
| 用户停止 | 持久化停止意图，不再发起新外发；已发送调用继续核对并保存成功/失败结果 | 把停止立即当成完整、删除已成功结果、撤销已发生费用 |
| 已保存成功但检查点未更新 | 按 owner + 操作/调用身份核对回执并补记覆盖 | 重新调用并生成重复版本 |
| 调用仍执行中 | 保留进行中状态，等待原期限或继续核对 | 并行分配新调用 |
| 结果未知/超时 | 保留未知状态和旧回执，不自动重试或声称失败 | 用“没有响应”推断未执行 |
| 明确失败/缺口 | 保留成功来源，只允许恢复明确缺口范围 | 全量重跑成功 ASR 或覆盖人工稿 |
| 恢复 | 绑定原 owner、来源快照、产物父版本、目标/同意；同一恢复身份不同输入冲突 | 换材料/目标后复用旧恢复身份 |
| 来源变化 | 新建来源快照和运行；旧产物保留 | 续写旧运行后声称仍基于原版本 |

恢复完成后仍须重新计算完整覆盖。恢复成功不自动使所有产物完成，也不自动改变人工版本的选择指针。

## 8. 验收矩阵

状态缩写：**API/Web PASS** = 已进入 PR #532 的最终 CI、merge/release，或明确列出的最终 Web 真实受控复验；**部分 PASS** = 只有明确列出的界面或内部路径有证据；**PENDING** = 尚未执行成功；**NOT-VERIFIED** = 没有可计入的当前证据。Android functional 必须另有 fullapp、test APK 编译、真实设备 E2E 与 functional PR 证据；本表、设计文档、合成夹具和聊天记录都不能替代。

### 8.1 v0.36 Android 最新证据与真实设备 P1

| 证据层 | 当前可核事实 | 状态边界 |
| --- | --- | --- |
| Source / PR / CI | feature commit `2ae81d8af0aea6ca46e5d7c43e9566d11a938c99` 基于 officialbot `4a05a68b`，31 files / 7,534 insertions；Draft PR #54 head 一致；CI 41004036926 为 6 success + PR事件 `release` 按规则 skipped，required/CI Gate success | Draft PR 不是 merge/Release；skipped release 不算 pass |
| Local full regression | `54f6ace2` 对应 targeted 28/28、fresh App 1,999/1,999、fresh Core 69/69，均 fail 0 / error 0 / skip 0，JBR 21.0.9；此前 `26/26 + 1,997 + 69` 与 App 1,990 checkpoint 保留为历史 | targeted、App、Core 是不同证据集，不求和；本地全绿不替代 54f6ace2 CI、新 APK 或 device |
| Real device normal E2E | owned phone/R2 执行 FAIL：同一次确认新增 TaskId `41bf2402-b10e-48b3-88ce-4e2a26b0e519` 与 `bb65ba87-00f4-436c-a578-504d3799e1fc`；两 task 的 note/material/ASR 相同，并都引用同一 `CompleteRecord`/`IndependentAnalysis` artifact | 正常用例未通过；negative/narrow 在 P1 修复前不能作为完整验收 |
| d37/testfix1 normal | Run `scratch/note-derived-implementation/android-qa/runs/20261030-150514-normal-phone`：前段 PASS 一 task/双 artifact、Photo/Ink 原字节、provider ASR3+vision1+text2=6；随后人工保存 FullRecord USER head 超时 FAIL，versions 仅 `[e11de5eb-0d40-ac50-a4b9-9d2f78f84a51]` 无 USER version。诊断 `scratch/note-derived-implementation/android-qa/human-save-diagnosis-20261030.md` | normal/regen 整体 FAIL；原因待分类。不得用前段 PASS 或 d37 CI 代替最终验收；negative/narrow 未验 |
| Root cause | `NotesViewModel.kt:154` 对每个选中 kind 调 `artifacts.start(...)`；backend 每个 task 已同时生成两种 artifact，UI 不应 fan-out 为多个 server task。失败时 provider summary 为 ASR 3/success 3、vision 0、text 0 | 没有已发生的重复 vision/text 调用，不代表未来不会重复收费；仍需 single-task invariant |
| A/B / legacy identity source | 独立 review 报告 `scratch/note-derived-implementation/android-p1-independent-review-d37db9cd.md`。A/B scope 与 legacy pending identity 修复已提交 `54f6ace2`：`scratch/android-material-scope-20261030/READY-freeze.json` 6 paths、SHA6/6；显式 materialVersion pin refresh/publish/tasks/drafts/candidates，A重生成留在A；缺kinds解码两artifact，pending unknown复用同一TaskId；无新收费/生产行为 | **COMMITTED / NEW CI+DEVICE PENDING**；真实 normal/人工保存与新 exact-head device未验，不得写已修复/最终验收 |
| Repair state | d37 exact CI 41017246721 success；testfix1 normal 前段 PASS、人工保存失败。A/B/legacy fix 已提交 `54f6ace2`，6 paths SHA6/6，targeted28/App1999/Core69；一次 confirm 一 start、task 保留两 kind、VM/regen/UI 按 `supports(kind)`、费用同意说明每 task 双产物 | **IN PROGRESS / NOT-VERIFIED**；54f6ace2 新 CI/device PENDING，normal/regen 整体不 PASS，negative/narrow 未验，禁止写 device PASS/已交付 |
| Historical v0.35 snapshot | 旧 worktree HEAD `967ad5cb`、Debug APK `2375cb77...` / 20,476,254 bytes、focused 30/30 + core 69/69、fullapp 1985/33 FAIL、test APK 未编译、device E2E 未执行、无 functional PR 全部保留 | 不得用 v0.36 新证据删除或改写 v0.35 当时事实 |

### 8.2 v0.37 Android 修复链与最新候选

| 证据层 | 当前可核事实 | 状态边界 |
| --- | --- | --- |
| Source chain | `d37db9cd` 单确认一 task；`54f6ace2` 按固定材料隔离 refresh/regen 并兼容 legacy 缺 `kinds` 原 TaskId；`080f5c2` 分离 viewed/pending/confirmed head；`af24e2f` 阻止 A 稿使用 B confirmed head 和旧账号 callback 在联网前继续；`afab38ee` 持久化 detail 回执并发布当前 scoped task；`f8fc4c0d` 增加两行 E2E 真实 UI refresh；`6afb6438` 2 处 test 修正补 fixture 与窄屏步骤；`6dc70a0d` 增加 coverage 异步 UI 等待与 narrow destination 滚动顺序；`a8507365` 正常提交 narrow dirty `FACCE` blob并普通 merge 3844ef62 | App/product tree 仍 `201a4f...`。narrow 构建早于 commit；PR #54 merge、exact CI、master CI、Release 1137 已完成 |
| Release | `android-v26.10.1137` published 2026-10-30T10:33:00Z、non-draft/non-prerelease，target 字段 master但 tag 实际解析 merge `3844ef62`；APK 14,477,900 bytes `297b029a...ec89e`、shell 2,550,430 bytes `fa3d2a4c...126ebd`、version.json 480 bytes `8c1df66c...50ecf` | GitHub Release 不是 production、Release APK 物理设备验证、完整视觉或完整 MVP；资产只证明发布对象 |
| Local regression | 最新 full App `2008/2008`、Repository targeted `11/11`、Core `69/69`，均 0 fail/error/skip；Core 源未变并沿 fresh 结果，统计不求和。历史 `2006/2004/1999/1997/1985+33 FAIL` 保留 | JVM 全绿不替代真实设备；不同 focused/full 计数不得相加 |
| af24 AVD normal | owned Android AVD（5556/API36.1）+真实 Test API 的 normal cohort 已完成一 task、`CompleteRecord` + `IndependentAnalysis` 双 artifact、Photo/Ink actual bytes、普通 USER head `2ac85107-e738-43bd-a41f-91698e0b356b` 与 ASR3 + vision1 + text2 = 6，未重复 ASR；Disabled 断言后无额外 provider 调用 | regeneration 未执行，human-head retention 未验证；该轮整体 FAIL，不是物理真机/PASS |
| afab normal/regen PASS | run `164238` instrumentation OK 1 test；recover case-result PASS。initial TaskId `23d68e06...`、regen TaskId `41247e61...` 各一次；CompleteRecord USER head `6fafd9ed...`，regen version `8e902c5f...` 为非 head 子版本且 parent 为 USER head。Photo 120 bytes `f40dea...`、Ink 956 bytes `79d099...`；ASR3+vision2+text4=9 全部 success，无重复 ASR | normal/regen 通过；不等于 negative/conflict/unknown/narrow 或完整设备矩阵通过 |
| f8 conflict-phone PASS | `f8fc4c0d` + test source `7AA5DA...` + same App `D197CE5B...890E`；兼容 test APK `B76E37DA37322EAAED771080605F8E21D59E6DDE1D7381057C9AC8C70DB74EF0`，raw test `F286...` 仅 META-INF 兼容重签且 91 payload 相同。instrumentation OK 1 test 39.628s；second writer head `a4a02ee1...`，local conflict `62ebb80b...` 为 hasConflict/non-head；双方完整文本和本机 draft 保留；provider ASR3+vision1+text2=6，冲突额外 model 0 | conflict-phone PASS；初始 runner Windows 反斜杠 pull FAIL 保留，同 case recovered PASS，不是产品失败。normal 的 test source `59A` 与 conflict 的 `7AA` 分账，不得拼成同一 test APK 的四项 PASS；unknown/narrow 仍待验 |
| unknown final PASS | `6dc70a0d` + test source `45E3A6A8...` + Test APK `712788C98973FC981F816C1F921EB90F7AC8B6C5D9309BBC4B2DEEE2F0433B20`，provider 6 PASS。`VerificationPending`、`remote=null` 与 fault 本 case 各 consume 1；显式同 ID 核对模型 0；完整 coverage 文案通过 | unknown whole case PASS；此前的 receipt/Accepted 与 coverage 旧文案 FAIL 保留；临时 QA proxy 不是产品补丁 |
| narrow final PASS | 运行 HEAD `6dc70a0d` + dirty `FACCE...`，Test APK `6CF705BFCB8421A256F6C95EC1C80615E7D45D248F2111D12D6729709972D2AB`，45.87s PASS、provider9。USER head `55ff0f71-d2ae-4fc6-9a9f-4cc3a7e07e5c` 保留；regen `8deec9ce-f062-8f5a-bcd3-50320b4c5e1f` 为 non-head 且 parent 为 USER head；dirty 全文保留 | 最终 `a8507365` commit 匹配 dirty `FACCE` blob但构建早于 commit；本链 PASS 不证明完整视觉或同一 Test APK 4/4 |
| Evidence collection / signing | 首次外层 result FAIL 为 runner evidence pull 单文件 “Not a directory”；原 FAIL 保留，recover 证据与 case-result PASS 已确认，无模型任务重跑。raw afab `E260.../9FB...` 与 owned 5556 cert 不兼容安装 FAIL 保留；同编译 APK 仅兼容重签，实际 App `D197CE5B29B2F09C3D250616B4AADE0110C4BB8FA76424D01C60E19BE877890E`、Test `D3B5A646924B37922B1B6A5963DA1625334F249A6E8F92427F97E39F21FF3E2D`，source `59A36F...`、product tree `201a4f...` | 采集失败不是产品失败；重签只解决 cert compatibility，不改变 source/product tree。不得写“所有轮次 PASS” |
| Independent lineage | 独立核验的 ZIP 非签名 payload 与安装 APK 同 SHA：App 478/478、Test 91/91；device manifest 与 run actual source/product tree 闭合，无新 source | lineage 只确认证据对应关系，不代表剩余 negative/narrow 或整卡验收通过 |
| Previous head blocker | `54f6ace2` 的 testfix-select 在 owned Android AVD 原生模拟器实测中 API 为单 Model `isHead=false`/`head=null`，UI 却把同 candidate 标为“当前选用”且 Disabled，manual-save 409 是该状态结果。后续 head 语义/manual guard 已提交 | v0.36 的旧 FAIL 与“原因待分类”历史保持；新 source 仍需真实选择、保存和 USER-head 断言 |
| Device/test boundary | 四链均使用真实 Compose UI 控件、真实 Test API、App `D197CE5B...890E` 与 product tree `201a4f...`；normal 用 `59A`/D3B5，conflict 用 `7AA`/B76，unknown 用 `45E3`/712788，narrow 用 dirty `FACCE`/6CF，最终 test commit 为 a850并已 merge 3844ef62 | 四组 Test APK/source 不拼接为最终 4/4；PR/CI/merge/release 已完成；不证明物理真机、完整视觉或课堂 |
| Unknown/narrow history and final | 旧 unknown FAIL：自动 GET 命中同 ID receipt 后变 Accepted；为测持续 unknown 与显式核对，临时 QA proxy 同时 arm GET task 503 与 POST fault，保留 VerificationPending/remote=null/显式同 ID/0 费用增量断言。旧 narrow FAIL：entry 屏外目标先 wait 后 scroll；最小改为先 scroll 再真实 display/click。后续 unknown PASS6、narrow 45.87s PASS9 | 两处旧 FAIL 与签名安装/runner pull 故障全部保留，与产品失败分账；最终 PASS 只覆盖对应受控功能链 |
| Next batch gates | Android 切片普通 merge/Release 已完成；下一批按既有 P0/P1/P2 独立复现/验收 #525/#533 受控升级与上传故障链；再物理设备/OEM/100-180课堂/真实模型质量，再公开逐图/逐block receipt及未实现 Windows/TASK/MIG | #533 仅issue报告/静态推断，不是已证根因；不做生产处置。GitHub Release不是真实升级/production证据；未经授权不付费；完整MVP不删不缩 |
| Environment cleanup | `device negative-round-fast/device-cleanup-receipt.json`：5556 nameguard，恢复1080x2424/density420/font1/rotation0，knownreverse清除，emulator正常退出、adb空、无5554/wipe/uninstall；R4 `runtime-p1-5026e455-20261030-r4-6fe3e03fbc95/cleanup-receipt.sanitized.json` SHA-256 `88C393F31F72CD4402B83D7F4A7D30923B241DC40F31BC9A6F35CBED51F84888`，7576无listener/health不可达，PID104740/144540/150828退出，临时host1AC09083 hashguard删除，API5026 tracked无diff，证据保留 | cleanup全部完成；环境清理不等于物理真机、production或Release验收 |
| Issues | Android #37 CLOSED(completed)；#33/#35 OPEN(partial)。API #505/#525/#533 OPEN；#505 body base `306f380b658da6960f67c819de2826c5abbf03151904eecf4e3d8468f00aafca`，17 checkbox 11/6；#525/#533 body 未改，#533 无本轮修复 | 不写 issue；issue 状态不替代功能验收；#37 不含 OPPO/完整 MVP |
| Scope | production、完整 MVP、OEM、OPPO、100/180 分钟真实课堂、长时后台、跨端完整流程、真实模型质量、公开逐图 receipt 均 `NOT-VERIFIED` | 本轮候选、JVM 或 af24 前段不能关闭总跟踪 |

| ID | 需求 | 场景与预期 | 必需证据 | 当前状态 |
| --- | --- | --- | --- | --- |
| DA-01 | AI-03/06、NOTE-04/05、SAFE-01 | 创建派生运行时固定 owner、材料版本、源清单摘要和全部文件摘要；后续编辑当前笔记不改变旧运行 | 领域单测、持久化重开、版本竞争测试 | API/Web PASS（PR#532 exact CI） |
| DA-02 | AI-03/06 | 有录音时，全部录音分片均复用同版本既有成功转写与回执，无新的 ASR 外发 | 调用记录断言、假上游计数、真实隔离 Test API 回执 | API/Web 真实受控 PASS（121 秒三 slice 与完整计划）；Android functional E2E NOT-VERIFIED |
| DA-03 | AI-03/06 | 任一录音分片失败、停止后未完成或结果未知时，记录/分析均不得标记完整，并逐项显示缺口 | 部分成功、停止、未知和恢复测试 | API/Web PASS（部分/停止/未知/恢复进入最终 CI）；Android functional E2E NOT-VERIFIED |
| DA-04 | AI-03/06 | 成功但空转写保留占位、试听/复核提示和调用回执；不被静默剔除或冒充有效内容 | 空转写夹具与界面状态测试 | 实现；空转写专项与真实界面 NOT-VERIFIED |
| DA-05 | NOTE-04、AI-06 | Markdown 使用固定版本原文/摘要及块顺序，不使用更新后的当前笔记内容 | 摘要/顺序篡改与版本竞争负向测试 | API/Web PASS（固定 document.md 与篡改阻断进入最终 CI） |
| DA-06 | NOTE-04、AI-06/07 | 全部关联照片使用固定版本原图、摘要和顺序；缺图、损坏或未知类型阻止完整 | 文件缺失/替换/类型错误负向测试 | API/Web PASS（最终受控复验确认 Photo/Ink 原图 bytes match） |
| DA-07 | NOTE-04、INK-04、AI-06/07 | Android 已渲染并上传的手写图实际进入下游 SDK；每个手写块使用同版本图片/块身份/笔迹摘要/尺寸，并有逐图实际消费与覆盖回执 | 从固定包到 SDK 请求的逐图证据、批次覆盖、错配/缺失测试；仅证明上传不算通过 | 内部实际字节/逐图覆盖 PASS；公开逐图/block receipt NOT-VERIFIED；Android functional E2E NOT-VERIFIED |
| DA-08 | AI-03/06、NOTE-04 | 未知块、无法解释的附件关系或清单外引用阻止完整，不能静默忽略 | 未知块夹具与契约回归 | 实现；未知块专项 NOT-VERIFIED |
| DA-09 | AI-03/06 | 全部必需来源恰好解析一次；重复归属、跨版本替换或来源重复计数均拒绝 | 覆盖证明单元/集成测试 | API/Web PASS（进入 PR#532 最终 CI） |
| DA-10 | AI-03/06 | 无录音纯文字/照片笔记允许 ASR 任务关联为空并可处理；不伪造空转写，也不因缺少预期录音而误判；有录音时仍要求成功 ASR | 无录音合法双产物样本、`AsrTaskId` 为空、缺失录音负向样本 | API/Web PASS；Android functional E2E NOT-VERIFIED |
| DA-11 | AI-04/06/07/08 | `record-synthesis` 拥有独立产物身份、版本和覆盖结论，不写入 ASR 分片状态 | 持久化模型、重启恢复和 API 断言 | API/Web PASS（PR#532 exact CI） |
| DA-12 | AI-04/06/07/08 | `analysis-synthesis` 拥有独立产物身份、版本和目标快照，不能替代或覆盖 `record-synthesis` | 双产物分离、独立选择和读取测试 | API/Web PASS（PR#532 exact CI） |
| DA-13 | AI-04/06/08 | 分析准确引用完整记录版本与同一材料快照；记录新修订后旧分析标记为基于旧版本 | 版本关系、陈旧标记和再次生成测试 | 部分 PASS（API/Web 来源/版本路径进入最终 CI）；Android E2E NOT-VERIFIED |
| DA-14 | AI-04 | 人工修订创建不可变子版本，可追溯父版本与来源；不改变原始材料 | 人工提交、重开和历史读取测试 | API/Web PASS（PR#532 exact CI） |
| DA-15 | AI-04 | 首次生成、再次生成和恢复都只追加候选，不覆盖较新人工版本且不移动 head | 自动 A0 → 人工 H1 → 重生成 R1 场景及 head 前后断言 | API/Web PASS（最终受控复验保人工 head） |
| DA-16 | AI-04、SYNC-02 | 两个并发人工提交基于同一父版本时，CAS 冲突候选可保存但现 head 不变，双方内容不丢失 | 并发 CAS、冲突候选保存、head 不变和服务端回执测试 | API/Web 真实受控 PASS（CAS 双留、head 不变）；Android E2E NOT-VERIFIED |
| DA-17 | AI-04、SYNC-02 | 晚到的旧生成响应只能成为旧父版本候选，不改变当前人工版本或 head/选择指针 | 延迟响应、人工先行和版本图测试 | Web 真实受控 PASS（late-success 12/12 独立）；Android E2E NOT-VERIFIED |
| DA-18 | AI-04、SAFE-01 | 同一幂等身份和同一输入返回原结果，不新增付费调用、版本或修订 | 重放请求、调用计数和持久化断言 | API/Web PASS（503→200 同版本 model call 不增） |
| DA-19 | AI-04、SAFE-01 | 同一幂等身份换材料、目标、父版本或输入指纹时显式冲突 | 输入指纹不一致负向测试 | API/Web PASS（fingerprint mismatch 409） |
| DA-20 | AI-04、SAFE-01 | 响应丢失和未知结果先核对既有回执，不自动重发；恢复显式提示可能重复计费 | 断连、超时、回执恢复和付费提示测试 | API/Web PASS（failed/unknown replay + ack retry；503→200无增调用） |
| DA-21 | SAFE-01 | 非 owner 不能读取、生成、修订、停止、恢复或重放他人的材料/产物 | 跨 owner API 与持久化负向测试 | API/Web PASS（进入最终 CI） |
| DA-22 | AI-02/03/04/05、SAFE-01 | 每次外发固定模型/预设/地址/语言和明确同意；上传同意不能替代模型同意 | 同意快照、目标展示和拒绝路径测试 | API/Web PASS；Android 真实目标 E2E NOT-VERIFIED |
| DA-23 | AI-02/03/04、SAFE-01 | 目标停用、配置变化、权限失效或疑似目标漂移时停止后续外发并保留已有结果 | 配置变化、禁用账号与目标不一致测试 | API/Web PASS（禁停账号与 precheck 后目标变化）；Android E2E NOT-VERIFIED |
| DA-24 | SAFE-01 | 任务、版本、WorkData 和日志不持久化令牌、JWT、私有下载地址或模型凭据 | 持久化扫描、日志断言和安全审查 | 源码边界已审查；自动持久化/日志扫描 NOT-VERIFIED |
| DA-25 | AI-03/04、REC-04 | 停止立即阻止新外发，但保留成功/未知回执并继续核对已发送调用；停止后不得标记完整 | 停止竞态、已发送调用和恢复测试 | API/Web PASS（PR#532 exact CI）；Android E2E NOT-VERIFIED |
| DA-26 | AI-03/04 | 明确恢复只处理缺口/未知范围，不重跑成功 ASR，不覆盖人工稿，不复用不同输入的恢复身份 | 局部恢复、成功片跳过和版本保护测试 | API/Web PASS（PR#532 exact CI）；Android E2E NOT-VERIFIED |
| DA-27 | NOTE-04/05、SYNC-01/02 | 界面可区分完整、部分、停止、未知、失败、缺口和基于旧版本；重开页面状态不丢失 | Web 桌面/窄屏、Android 手机/平板/大字号状态测试 | Web 最终 phone/narrow core PASS；narrow 过渡截图不证明完整视觉；Android 完整结果 UI/设备 E2E NOT-VERIFIED |
| DA-28 | AI-04、SYNC-02 | 用户可比较自动稿、人工稿、重生成稿和来源差异，并显式选择；选择不改写历史 | 比较/选择界面与 API 集成测试 | Web 候选/选择路径受控 PASS；完整差异比较视觉与 Android E2E NOT-VERIFIED |
| DA-29 | 跨端 | Web/Android 先完成本机人工稿；API 官方合并契约冻结后，两端口径只消费同一实际 Swagger/官方生成类型，网络读取与双产物状态可端到端读取 | 本机人工稿测试、真实 Test API、生成物 freshness、受控 Web/Android E2E | Web 最终真实受控 PASS；Android typed 14 operations、PR/CI/merge/release 与四链分轮次 PASS（9/6/6/9）；OPPO/OEM/课堂、完整视觉与 production/fullMVP 仍 NOT-VERIFIED |
| DA-30 | AI-03/04/06/07/08 | 真实受控 API 全链：全部材料 → 双产物 → 人工修订 → 材料变化/重生成 → 并发/重放/恢复选择 | 隔离 Test API、受控真实模型目标、完整审计回执；不使用生产 | Web 最终 phone/narrow core + negative PASS；完整并发/恢复压力与 Android E2E NOT-VERIFIED |
| DA-31 | AI-06/07 | 当前 SDK 以 PNG/JPEG、单次最多 4 张、单张最多 4 MiB、单次合计最多 8 MiB 消费材料；超过单批时经可审计多批覆盖，不丢图/文 | SDK 请求回执、逐图/逐块覆盖清单、多批失败与合并测试 | 内部本机 PASS（32 图/8 批上限、真实 loopback 字节）；公开逐图 receipt NOT-VERIFIED |
| DA-32 | AI-06/07 | WebP、超大单图、超总量或正文超限等当前 SDK 不支持/超限资料明确阻断并显示缺口；不静默前 N 张、截正文或转码 | 负向夹具、原始上传字节不变、无隐式转码/截断断言 | API/Web 负向受控 PASS；Android E2E NOT-VERIFIED |
| DA-33 | AI-03/04、NOTE-04 | 无录音纯文字/照片笔记可以生成 `record-synthesis` 与 `analysis-synthesis`，ASR 任务关联为空；有录音但无成功 ASR 时阻断完整 | 无录音合法闭环、有录音缺 ASR 负向路径 | API/Web PASS（进入最终 CI）；Android functional E2E NOT-VERIFIED |
| DA-34 | AI-04 | `record-synthesis` 与 `analysis-synthesis` 的类型、head 和候选互不混用；任一自动生成完成后 head 保持原值 | 双产物存储、API 和 UI 的候选/head 断言 | API/Web PASS（最终受控 head 断言）；Android E2E NOT-VERIFIED |
| DA-35 | AI-04、SYNC-02 | 人工 CAS 冲突保存候选且现 head 不变；最终版本 ID 与 `hasConflict` 表达在唯一契约冻结后补齐 | 并发冲突回执、head 前后快照、冻结契约对照 | canonical contract 含 `hasConflict`/`isHead`，API/Web CAS 双留 PASS；Android E2E NOT-VERIFIED |
| DA-36 | 跨端 | Web 以真实 Test Swagger 和正式生成物与 API 同 PR 接入；Android 先本机人工稿，取得上游官方契约快照后接网络；均不手写猜测字段 | 同仓 Web 生成物 freshness 与网络 E2E、Android 本机稿与官方快照后网络 E2E | Web 官方生成/check、PR#532 CI 与真实受控 E2E PASS；Android typed 14 operations、PR#54 merge/CI/release 与四链分轮次 PASS（9/6/6/9）；物理设备、完整视觉、production/fullMVP 仍 NOT-VERIFIED |

## 9. canonical 契约与已合并检查

PR #532 合并后的 canonical contract（SHA-256 `e38d1991...f0f9`）已能直接回答下列结构性问题；Android officialbot 与 Windows bot 已同步生成物，但 Android functional 结果界面与设备 E2E 仍须另验：

1. 如何固定 owner、材料版本、源清单摘要、可选 ASR 任务关联（无录音时为空）、有录音时的成功 ASR 证据和目标/同意快照。
2. 如何逐项表达 `record-synthesis`/`analysis-synthesis` 的独立身份、版本、父版本、候选、head 和覆盖缺口。
3. 人工提交、再次生成、停止、恢复和未知核对分别使用何种操作身份与 CAS 并发前提；冲突候选如何表达版本 ID/`hasConflict` 且不改 head。
4. 同一操作身份重复到达、不同指纹到达、较旧响应晚到时的可观察结果。
5. 当前 SDK 的 PNG/JPEG、4 张/4 MiB/8 MiB 边界如何映射为明确的可用/阻断/多批覆盖结果，且不改变上传 32/40 MiB 与 WebP 能力。
6. Web/Android 如何区分完整、部分、停止、未知、失败、冲突候选和基于旧版本，而无需猜测状态字符串。
7. 非 owner、停用账号、目标撤销和发布版本/配置变化如何一致拒绝。
8. 哪些读取操作允许查看旧版本和冲突候选，哪些操作必须要求明确选择 head。

API 已具备 `derived-tasks` 的 start/list/get/steps/stop/retry 与 limits/destinations/sources，`derived-artifacts` 的 list/get/version/manual-revisions/select；产物 kind 为 `CompleteRecord`/`IndependentAnalysis`，mutation 含 `hasConflict`/`isHead`。coverage 当前仅公开聚合 count 与 issues，尚未公开逐图/逐 block 成功 receipt。Android officialbot 已同步契约，Android functional 已通过 typed 14 operations、PR #54 exact CI、普通 merge、master CI 与 `android-v26.10.1137` Release 交付；四 cohort 已在真实 Test API + owned AVD 分轮 PASS，但不等于物理真机、真实课堂或完整 MVP。

## 10. 证据边界

- API/Web 已完成 [PR #532](https://github.com/2746267826/pim-api/pull/532) 普通 merge、[CI 40907900018](https://github.com/2746267826/pim-api/actions/runs/40907900018)、[master CI 40994288275](https://github.com/2746267826/pim-api/actions/runs/40994288275) 与 [api-v26.10.1011](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.1011)。exact head `5026e455`，merge `3e3829aa959f34cd602d193a82462116b80d0737`；PR 6193 pass / 15 skip / 0 fail（6208 total），Web 349/349；master Gate/Release/Notify success，`build-mcp` 合法 skipped，不计 pass。
- canonical contract 为 `e38d1991b372174a3a4bac10df0a35eee90de92d268a5f1d35762d76da07f0f9`（1,119,472 bytes，329 paths，659 schemas）。CI 用真实生成 artifact 修复旧本机导出缺 `/mcp`；历史失败保留。JSON evidence 在不入仓的 `.codex-tmp/note-derived-implementation/final-planning/api-master-run.json` 与 `api-releases.json`。
- Web exact PR 全量 `349/349`（46 files），note-derived focused `36/36`，storage `20/20` + TS/lint，lint/build exit 0。最终 `dist/index.html` 为 `c915835a992295e13c3e26735d2cea0cc4148909b7cc50e62053be47ab6c17c9`；先前 `f6bd...` 手机探路、`347/347` 等只作历史检查点。专项/focused 计数不与完整 CI 6193 相加。
- 最终 Web 真实 Test API + loopback SDK：phone 390×844 core PASS、narrow 320×720 core PASS；CAS 双留、丢失响应 503→200 且同版本 model call 不增、dirty 刷新/切候选保留、late-success 12/12 独立 PASS。Photo/Ink bytes match，ASR 3 + vision 2 + text 4 = 9，普通账号 admin 0。public evidence 为 `.codex-tmp/note-derived-implementation/qa-runtime/public-evidence/final-C915-20261029`；合成 provider 不代表真实质量，narrow 过渡截图不证明完整视觉。
- Android base functional commit 为 `2ae81d8a`，CI 41004036926 success；duplicate-start P1 中间 commit `d37db9cd` 的 exact CI 41017246721 required/Gate SUCCESS，PR事件release SKIPPED，11 paths SHA11/11、targeted26/App1997/Core69。后续 A/B/legacy fix commit `54f6ace2` 的 6 paths SHA6/6、targeted28/App1999/Core69 保留为历史。最终 a850 exact PR CI 41038364205、merge `3844ef62`、master CI 41040144538 与 Release `android-v26.10.1137` 已完成。
- Android owned phone/R2 的真实 normal E2E 已执行但 FAIL：一次确认新增两个 TaskId `41bf2402-b10e-48b3-88ce-4e2a26b0e519`、`bb65ba87-00f4-436c-a578-504d3799e1fc`，同 note/material version/ASR 且都引用同一对 `CompleteRecord`/`IndependentAnalysis` artifact；本地 FullRecord/Analysis intent 相隔 342ms。`NotesViewModel.kt:154` 对每个 kind 调 `artifacts.start(...)`，但 backend 每 task 已生成两种 artifact。Provider summary 为 ASR 3/success 3、vision 0、text 0，未发生重复 vision/text 调用，但不保证后续不重复收费。失败归档 SHA-256 `D592A15264214155A1D50D84FA90B822E13553F294B08811DE4FE0503AB81A18`。
- `d37/testfix1` 真实 normal 前段 PASS：一 task/双 artifact、Photo/Ink 原字节、ASR3+vision1+text2=6；随后人工保存 FullRecord USER head 断言超时 FAIL，versions 仅见 `e11de5eb-0d40-ac50-a4b9-9d2f78f84a51`，原因待分类。normal/regen 整体不 PASS，negative/narrow 未验。证据 `scratch/note-derived-implementation/android-qa/runs/20261030-150514-normal-phone` 与 `human-save-diagnosis-20261030.md`。
- A/B fixed-material scope 与 legacy pending identity 修复当时由 `54f6ace2` 提交，无新收费或生产行为；该中间状态的历史 FAIL/未验事实保留。最终修复链、四 cohort、PR 41038364205、merge 3844ef62、master 41040144538 与 Release 1137 已在后续完成，不能用中间 checkpoint 替代最终回执。
- v0.35 历史快照仍保留：旧 worktree HEAD `967ad5cb`、Debug APK SHA-256 `2375cb77...` / 20,476,254 bytes、focused 30/30 + core 69/69 PASS、fullapp 1985 有 33 FAIL（旧 ASRStore checkpoints 写入，根因未证实）、instrumentation test APK 未编译、真实设备 E2E 未执行、无 functional PR；owned AVD/Test API 当时只是准备状态。
- Android officialbot PR #52 与 Windows bot PR #38 只同步 contract/generated：Android merge `4a05a68b`、master CI 40995211845、Release `android-v26.10.1125`；Windows merge `69defe7a`、master CI 40995706728、Release `windows-v26.10.1104`。不得把消费者 bot Release 当成本地 derived 功能或完整 MVP 发布。
- 手写图已由 Android 固定包渲染上传，API/Web 内部与最终受控证据验证原图 bytes 进入 loopback SDK；公开逐图 audit、Android functional 设备 E2E 和真实目标质量仍为 `NOT-VERIFIED`。
- 单元测试、API 集成、Web 真实受控状态、Android focused/fullapp、instrumentation test APK、真实设备和隔离 Test API 分别记账，不能互相替代。
- GitHub Release 不等于 production；真实 upgrade E2E、#525 历史 303 pending、OEM、OPPO、100/180 分钟真实课堂、长时后台、全 MVP 和课堂质量仍为 `NOT-VERIFIED`。
- issues 实际收尾：[Android #37](https://github.com/2746267826/pim-android/issues/37) 已于 2026-10-30T06:52:09Z 普通 CLOSED（reason completed），slice 验收满足，[评论](https://github.com/2746267826/pim-android/issues/37#issuecomment-6516822157)明确 OPPO 与完整 MVP 不含；[#33](https://github.com/2746267826/pim-android/issues/33) 与 [#35](https://github.com/2746267826/pim-android/issues/35) 已追加 partial 进度并保持 OPEN（[#33评论](https://github.com/2746267826/pim-android/issues/33#issuecomment-6516831970)、[#35评论](https://github.com/2746267826/pim-android/issues/35#issuecomment-6516835098)）；API [#505](https://github.com/2746267826/pim-api/issues/505)、[#525](https://github.com/2746267826/pim-api/issues/525)、[#533](https://github.com/2746267826/pim-api/issues/533) 保持 OPEN 且 body 未改；#533 仅有 issue 报告，本轮未独立复验且无修复。回执为 `scratch/note-derived-implementation/issues-followup-20261030/remote-actions-receipt-20261030.json`。本文不修改 issue，也不执行课堂或生产验证。
