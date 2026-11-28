<!-- GENERATED FILE - DO NOT EDIT -->
<!-- 由 `scripts/ci/render-delivery-ledger.py` 从 `docs/product/delivery/` 生成；请修改结构化源，不要手工编辑本文件。 -->

# PIM 全量交付台账（当前视图）

> 生成视图 / Generated view。结构化事实源：
> - `docs/product/delivery/ledger.json`（161 项主表）
> - `docs/product/delivery/prs.json`、`ci-runs.json`、`real-flows.json`、`evidence.json`、`decisions.json`
> - 规则说明：`docs/product/delivery/README.md`

数据快照：`2746267826/pim-api@555b5d4d`（2026-11-27T23:10:26+08:00）。迁移时间 2026-11-28。

状态与证据来自旧台账快照；本次迁移未重跑 CI、未执行真实设备/浏览器/provider 验收。

## 摘要 / Summary

- 范围：交付包 A–R、Z 共 160 项，加补充项 S01，共 **161** 项。
- 状态：PASS 0，FAIL 103，NOT-VERIFIED 50，BLOCKED 2，APPROVED-EXCEPTION 6。
- Freshness：current 0，stale 161。 所有迁移项均为 stale：旧台账证据没有被重新认定为当前通过。
- 证据：211 条（CI 22、真实流程 18、决议 10）。
- 真实流程：18 条记录，其中 current 通过 0 条。
- PR：53 条，merged 30，open 23。

本视图不宣称任何产品功能完成；`PASS 0` 表示当前结构中没有可用的 current 端到端证据。

## 状态计数 / Status counts

| 状态 | 数量 |
| --- | ---: |
| PASS | 0 |
| FAIL | 103 |
| NOT-VERIFIED | 50 |
| BLOCKED | 2 |
| APPROVED-EXCEPTION | 6 |
| **合计** | **161** |

| ID 前缀 | 范围 | 项数 |
| --- | --- | ---: |
| A | 笔记列表与组织 | 10 |
| B | 编辑器 | 16 |
| C | 录音 | 22 |
| D | 手写 | 8 |
| E | 材料与文件输入 | 3 |
| F | 上传与同步 | 11 |
| G | ASR 与转写 | 13 |
| H | 多模态整理 | 11 |
| I | 任务与日程建议 | 4 |
| J | AI 配置与调用 | 9 |
| K | 文件与云盘 | 4 |
| L | Android 原生客户端 | 14 |
| M | Windows 原生客户端 | 6 |
| N | Web 客户端 | 3 |
| O | 视觉与体验 | 6 |
| P | 采集与定位 | 5 |
| Q | 基础服务 | 8 |
| R | MCP / API | 5 |
| S | Release 与发布安全（补充项） | 1 |
| Z | 反向要求 | 2 |
| **合计** | A–R、Z + S01 | **161** |

## 平台矩阵 / Platform matrix

| 平台 | 必需 required | 只读 read_only | 协调 coordination | 不适用 not_applicable |
| --- | ---: | ---: | ---: | ---: |
| API | 76 | 0 | 21 | 64 |
| MCP | 5 | 0 | 0 | 156 |
| Web | 110 | 20 | 21 | 10 |
| Android | 133 | 1 | 1 | 26 |
| Windows | 119 | 1 | 1 | 40 |
| Release | 1 | 0 | 0 | 160 |

角色含义：`required` 必须在当前 head 验收；`read_only` 只消费不产出；`coordination` 只做契约/协调；`not_applicable` 不在范围内。

## 批次 / Batches

| 批次 | 主题 | 项数 | 状态 | 关闭条件 |
| --- | --- | ---: | --- | --- |
| B1 | A–C 笔记、编辑器与录音 | 48 | open | A–C 全部 required 平台在冻结 head 上完成验收并附真实流程证据。；录音格式与分片以 C12 为准，C14 例外决议保持有效。；任何状态升级为 PASS 前必须提供 current freshness 的真实设备/浏览器证据。 |
| B2 | D–F 手写、材料与上传 | 22 | open | 手写三端往返使用同一 ink JSON，D04 工具集完整。；上传队列、断点续传、冲突保留与清理语义在 Q07 裁决后收口。；不允许用旧契约快照证明当前客户端契约兼容。 |
| B3 | G–I ASR、整理与建议 | 28 | open | 真实 ASR/多模态 provider 与 Hangfire 多进程流程有记录。；H03 长音频分批与汇总通过真实预算验收。；I03/I04 三端建议与确认幂等在真实客户端验收。 |
| B4 | J–K AI 配置与文件 | 13 | open | 真实第三方 provider 的连接、审计、Token 限额与失败恢复可观察。；云盘 OAuth、容量显示与文件客户端入口可用。 |
| B5 | L–N 原生与 Web 客户端 | 23 | open | 旧 WebView/壳入口退役，客户端 master 契约 freshness 转绿。；真实 Android 实机/OEM 与 Windows 桌面交互完成验收。 |
| B6 | O–Q 体验、采集与基础服务 | 19 | open | Q01/Q07 产品裁决解除，或在台账中保留 BLOCKED 原因。；HTTPS/TLS、诊断、通知、回收站有真实公网/部署证据。 |
| B7 | R–S–Z MCP、发布安全与反向要求 | 8 | open | R05 工具集与外部独立 MCP 客户端验收完成。；S01 真实 release dispatch、并发重跑与生产拉取有记录。；Z01/Z02 在真实浏览器与客户端完成普通用户视角验收。 |

### B1 A–C 笔记、编辑器与录音

目标：完成笔记创建、编辑、录音链路的当前版本验收

状态分布：PASS 0，FAIL 32，NOT-VERIFIED 13，BLOCKED 0，APPROVED-EXCEPTION 3

关闭条件：
- A–C 全部 required 平台在冻结 head 上完成验收并附真实流程证据。
- 录音格式与分片以 C12 为准，C14 例外决议保持有效。
- 任何状态升级为 PASS 前必须提供 current freshness 的真实设备/浏览器证据。

### B2 D–F 手写、材料与上传

目标：打通手写、附件材料与上传同步

状态分布：PASS 0，FAIL 12，NOT-VERIFIED 9，BLOCKED 0，APPROVED-EXCEPTION 1

关闭条件：
- 手写三端往返使用同一 ink JSON，D04 工具集完整。
- 上传队列、断点续传、冲突保留与清理语义在 Q07 裁决后收口。
- 不允许用旧契约快照证明当前客户端契约兼容。

### B3 G–I ASR、整理与建议

目标：完成转写、多模态整理与任务/日程建议闭环

状态分布：PASS 0，FAIL 21，NOT-VERIFIED 7，BLOCKED 0，APPROVED-EXCEPTION 0

关闭条件：
- 真实 ASR/多模态 provider 与 Hangfire 多进程流程有记录。
- H03 长音频分批与汇总通过真实预算验收。
- I03/I04 三端建议与确认幂等在真实客户端验收。

### B4 J–K AI 配置与文件

目标：完成 AI 供应商配置、用量与文件/云盘能力

状态分布：PASS 0，FAIL 11，NOT-VERIFIED 2，BLOCKED 0，APPROVED-EXCEPTION 0

关闭条件：
- 真实第三方 provider 的连接、审计、Token 限额与失败恢复可观察。
- 云盘 OAuth、容量显示与文件客户端入口可用。

### B5 L–N 原生与 Web 客户端

目标：完成 Android/Windows 原生入口与 Web 客户端收口

状态分布：PASS 0，FAIL 12，NOT-VERIFIED 11，BLOCKED 0，APPROVED-EXCEPTION 0

关闭条件：
- 旧 WebView/壳入口退役，客户端 master 契约 freshness 转绿。
- 真实 Android 实机/OEM 与 Windows 桌面交互完成验收。

### B6 O–Q 体验、采集与基础服务

目标：完成视觉一致、采集质量与基础服务安全

状态分布：PASS 0，FAIL 11，NOT-VERIFIED 4，BLOCKED 2，APPROVED-EXCEPTION 2

关闭条件：
- Q01/Q07 产品裁决解除，或在台账中保留 BLOCKED 原因。
- HTTPS/TLS、诊断、通知、回收站有真实公网/部署证据。

### B7 R–S–Z MCP、发布安全与反向要求

目标：完成 MCP 契约、发布提升与用户可见性反向要求

状态分布：PASS 0，FAIL 4，NOT-VERIFIED 4，BLOCKED 0，APPROVED-EXCEPTION 0

关闭条件：
- R05 工具集与外部独立 MCP 客户端验收完成。
- S01 真实 release dispatch、并发重跑与生产拉取有记录。
- Z01/Z02 在真实浏览器与客户端完成普通用户视角验收。

## 全量表 / Full table

`implementation` 与 `legacy_status` 保留旧台账原文（历史基线），不代表当前 head 已完成实现；当前判断以 `status`、`freshness` 与证据为准。

| 编号 | 标题 | 批次 | 平台角色 | 状态 | Freshness | PR | 证据 | 下一工单 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A01 | 上课/开会开始时一键新建笔记并开始录音 | B1 | Web；Android；Windows | FAIL | stale | #96 | EV-A01-AUDIT | WO-A01-FULL |
| A02 | 笔记列表按时间排列，显示标题、时间、录音时长、处理状态 | B1 | Web；Android；Windows | FAIL | stale | #102 | EV-A02-AUDIT | WO-A02-LIBRARY |
| A03 | 安卓笔记页去掉「笔记」「本机·离线记录」等多余文字和提示 | B1 | Android | FAIL | stale | #102 | EV-A03-AUDIT | WO-A03-BODY-SYNC |
| A04 | 笔记全文搜索（标题、正文、转写、整理结果） | B1 | Web；Android；Windows | FAIL | stale | #600 | EV-A04-AUDIT、EV-CI-45662774385 | WO-A04-CLIENTS |
| A05 | 文件夹/分类/标签整理笔记 | B1 | Web；Android；Windows | FAIL | stale | — | EV-A05-AUDIT | WO-A05-A06-ORG |
| A06 | 置顶、收藏 | B1 | Web；Android；Windows | FAIL | stale | — | EV-A06-AUDIT | WO-A05-A06-ORG |
| A07 | 删除进回收站，可恢复 | B1 | Web；Android；Windows | FAIL | stale | #614 | EV-A07-AUDIT、EV-CI-45863519886 | WO-A07-Q03 |
| A08 | 平板列表+编辑双栏布局 | B1 | Web；Android；Windows | FAIL | stale | — | EV-A08-AUDIT | WO-A08-A09-WORKSPACE |
| A09 | 批量操作（多选删除、移动） | B1 | Web；Android；Windows | FAIL | stale | — | EV-A09-AUDIT | WO-A08-A09-WORKSPACE |
| A10 | 列表空状态、加载中、出错时有像样的界面 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-A10-AUDIT | WO-A10-ACCEPT |
| B01 | 正文是普通 Markdown，自上而下的文档 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B01-AUDIT | WO-B01-04-ACCEPT |
| B02 | 编辑页是记录中最大的页面，正文占主空间 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B02-AUDIT | WO-B01-04-ACCEPT |
| B03 | 顶部不一直显示「PIM 笔记」 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B03-AUDIT | WO-B01-04-ACCEPT |
| B04 | 「结束」按钮悬浮在右上角 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B04-AUDIT | WO-B01-04-ACCEPT |
| B05 | 拍照直接插入正文 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B05-AUDIT | WO-B05-B06-MEDIA |
| B06 | 从相册选图插入 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B06-AUDIT | WO-B05-B06-MEDIA |
| B07 | 图片可看大图、删除、调整并支持复制/剪切/粘贴 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B07-AUDIT | WO-B07-IMAGE |
| B08 | 常用格式工具栏（标题、列表、待办勾选、加粗、引用） | B1 | Web；Android；Windows | FAIL | stale | — | EV-B08-AUDIT | WO-B08-B09-B16-EDITOR |
| B09 | 撤销/重做 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B09-AUDIT | WO-B08-B09-B16-EDITOR |
| B10 | 自动保存，崩溃或被杀后不丢字 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B10-AUDIT | WO-B10-12-ACCEPT |
| B11 | 中文输入法输入稳定（不吞字、不跳光标） | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B11-AUDIT | WO-B10-12-ACCEPT |
| B12 | 记录时不需要联网，离线可用 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B12-AUDIT | WO-B10-12-ACCEPT |
| B13 | 处理期间照常编辑，不锁编辑器 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B13-AUDIT | WO-B13-WEB-UPLOAD |
| B14 | 软键盘弹出时悬浮条和工具栏不被挡住 | B1 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-B14-AUDIT | WO-B14-ACCEPT |
| B15 | 原决议：不做 100 分钟长笔记性能验收 | B1 | Web；Android；Windows | APPROVED-EXCEPTION | stale | — | EV-B15-AUDIT、EV-DEC-B15 | 不排期 |
| B16 | 编辑核心用离线打包的成熟开源编辑器（Tiptap/ProseMirror），不自己造 | B1 | Web；Android；Windows | FAIL | stale | — | EV-B16-AUDIT | WO-B08-B09-B16-EDITOR |
| C01 | 手动开始、结束录音 | B1 | Web(只读)；Android；Windows | NOT-VERIFIED | stale | — | EV-C01-AUDIT | WO-C-ACCEPT |
| C02 | 暂停/继续 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C02-AUDIT | WO-C-WIN-REC |
| C03 | 锁屏、切换应用后继续录音（前台服务） | B1 | Web(只读)；Android；Windows | NOT-VERIFIED | stale | — | EV-C03-AUDIT | WO-C-ACCEPT |
| C04 | Android 后台录音持续显示状态 | B1 | Web(只读)；Android；Windows | NOT-VERIFIED | stale | #89 | EV-C04-AUDIT | WO-C-ACCEPT |
| C05 | 通知里可暂停/停止 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C05-AUDIT | WO-C-WIN-REC |
| C06 | 实时波形，由真实音频驱动 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C06-AUDIT | WO-C-WIN-REC |
| C07 | 录音控制缩小成左下角悬浮条，不占大位置 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C07-AUDIT | WO-C-WIN-REC |
| C08 | 一篇笔记支持多段录音 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C08-AUDIT | WO-C-WIN-REC |
| C09 | 每段录音 3 小时上限 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C09-AUDIT | WO-C-WIN-REC |
| C10 | 到 3 小时自动保存并续下一段 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C10-AUDIT | WO-C-WIN-REC |
| C11 | 暂停不计入录音时长 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C11-AUDIT | WO-C-WIN-REC |
| C12 | 每 15 秒一个 WAV 分片（当前 60 秒） | B1 | Web(只读)；Android；Windows | FAIL | stale | #596 | EV-C12-AUDIT、EV-RF-C12-FFMPEG、EV-CI-45850989877 | WO-C-FORMAT |
| C13 | 被打断（来电、被杀、出错）自动继续录制 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C13-AUDIT | WO-C-WIN-REC |
| C14 | 原决议：由 C12 直接采集 Opus/Ogg 取代“结束后 WAV 合成 Opus 再删除” | B1 | Android；Windows | APPROVED-EXCEPTION | stale | — | EV-C14-AUDIT、EV-DEC-C14 | 随 C12 |
| C15 | 结束后可在同篇继续补录新段 | B1 | Web(只读)；Android；Windows | NOT-VERIFIED | stale | — | EV-C15-AUDIT | WO-C-ACCEPT |
| C16 | 录音回放：播放/暂停、拖动进度、倍速 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C16-AUDIT | WO-C-PLAYBACK |
| C17 | 点转写文字跳到对应录音位置 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C17-AUDIT | WO-C-PLAYBACK |
| C18 | 首次使用引导麦克风、通知、电池优化白名单权限 | B1 | Web(只读)；Android；Windows | FAIL | stale | #96 | EV-C18-AUDIT | WO-C-WIN-REC |
| C19 | 存储空间不足时提前提醒 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C19-AUDIT | WO-C19-STORAGE |
| C20 | 录音中打时间点标记 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C20-AUDIT | WO-C-PLAYBACK |
| C21 | 原决议：实时转写暂缓 | B1 | Android | APPROVED-EXCEPTION | stale | — | EV-C21-AUDIT、EV-DEC-C21 | 不排期 |
| C22 | App 被杀/重启后能找回已录内容，并标明中断 | B1 | Web(只读)；Android；Windows | FAIL | stale | — | EV-C22-AUDIT | WO-C-WIN-REC |
| D01 | 手写卡片像代码块一样插在正文里 | B2 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-D01-AUDIT | WO-D01-WEB-WIN-INLINE |
| D02 | 右下角拖拽柄调大小，拉多大就是多大 | B2 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-D02-AUDIT | WO-D02-RESIZE |
| D03 | 手写随首个安卓可用版一起交付 | B2 | Android | NOT-VERIFIED | stale | #97 | EV-D03-AUDIT | WO-D03-RELEASE-APK |
| D04 | 笔、橡皮、颜色、粗细、撤销 | B2 | Web；Android；Windows | FAIL | stale | #594、#97 | EV-D04-AUDIT、EV-RF-D04-CROSS-END、EV-CI-45664309218 | WO-D04-WINDOWS-TOOLS、WO-D04-WEB-REEDIT、WO-D04-CROSS-END-E2E |
| D05 | 平板手写笔压感、防误触（掌触） | B2 | Web；Android；Windows | FAIL | stale | — | EV-D05-AUDIT | WO-D05-PRESSURE-PALM |
| D06 | 保存原始笔迹，可再次编辑 | B2 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-D06-AUDIT | WO-D06-WEB-REEDIT |
| D07 | 手写部分转成图片交给 AI | B2 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-D07-AUDIT | WO-D07-VISION-PROVIDER |
| D08 | Windows 鼠标可连续绘制、选择/整组移动、撤销重做、复制粘贴、保存重开、删除当前版本手写 | B2 | Windows | NOT-VERIFIED | stale | #52 | EV-D08-AUDIT | WO-D08-WINDOWS-DESKTOP |
| E01 | 附件放进「文件」系统 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-E01-AUDIT | WO-E01-NOTE-FS-ZIP |
| E02 | 材料上传列表默认收起隐藏 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-E02-AUDIT | WO-E02-MERGE-102 |
| E03 | 原决议：不做通用 PDF/文档附件 | B2 | API；Web；Android；Windows | APPROVED-EXCEPTION | stale | — | EV-E03-AUDIT、EV-DEC-E03 | 不排期 |
| F01 | 结束后整篇笔记和录音上传云端 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F01-AUDIT | WO-F01-F02-ONLINE-MEMBERS |
| F02 | 结束后自动上传，进入上传队列 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F02-AUDIT | WO-F01-F02-ONLINE-MEMBERS |
| F03 | 确认上传成功后变成纯云端（本机清理） | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F03-AUDIT | WO-F03-CLEANUP-UPLOADED、WO-Q07-CONFLICT-DECISION |
| F04 | 没网显示「已保存，待上传」，有网自动续传 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F04-AUDIT | WO-F04-OFFLINE-QUEUE |
| F05 | 大文件分片、断点续传 | B2 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-F05-AUDIT | WO-F05-TRANSFER-E2E |
| F06 | 上传、转写、整理三个状态分开显示 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F06-AUDIT | WO-F06-STATUS-UX |
| F07 | 不用等上传完就能离开 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F07-AUDIT | WO-F07-BACKGROUND-UPLOAD |
| F08 | 其他设备（平板、电脑、Web）打开同一篇笔记 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F08-AUDIT | WO-F08-ONLINE-SAME-NOTE |
| F09 | 多端冲突先保留两份，不默认覆盖 | B2 | API；Web；Android；Windows | FAIL | stale | — | EV-F09-AUDIT | WO-F09-BLOCK-MERGE |
| F10 | 仅 Wi‑Fi 上传选项 / 流量提醒 | B2 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-F10-AUDIT | WO-F10-NETWORK-COPY |
| F11 | 上传失败显示原因并可重试 | B2 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-F11-AUDIT | WO-F11-ATTENTION-NOTIFY |
| G01 | 服务器收到录音后交给 ASR 处理 | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | #620 | EV-G01-AUDIT | WO-G01-AUTO-PROCESSING-E2E |
| G02 | 每段录音的转写可以单独查看 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G02-AUDIT | WO-G02-H-G-UX |
| G03 | ASR 厂商做成可添加的预设模板，先放 mimo | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-G03-AUDIT | WO-G03-G05-PROVIDER |
| G04 | mimo 批量推理 provider（#516） | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G04-AUDIT | WO-G03-G05-PROVIDER |
| G05 | Web 端能配置 ASR | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-G05-AUDIT | WO-G03-G05-PROVIDER |
| G06 | 云端转写/处理并发进行，每个 provider 可设并发数 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G06-AUDIT | WO-G06-PROVIDER-CONCURRENCY |
| G07 | 单独重新转写某一片段 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G07-AUDIT | WO-G07-QUALITY-RETRY |
| G08 | 把片段标为无内容（课间），直接丢弃其结果 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G08-AUDIT | WO-G08-NO-CONTENT |
| G09 | 转写带时间戳 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G09-AUDIT | WO-G09-G10-TRANSCRIPT-DTO |
| G10 | 区分说话人 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G10-AUDIT | WO-G09-G10-TRANSCRIPT-DTO |
| G11 | 失败只重试失败的那段 | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-G11-AUDIT | WO-G11-RECOVERY-E2E |
| G12 | 手动修改转写文字 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-G12-AUDIT | WO-G12-TRANSCRIPT-EDIT |
| G13 | 长录音按厂商限制自动切分，用户仍看到原来的段 | B3 | API；Web；Android；Windows | FAIL | stale | #596 | EV-G13-AUDIT、EV-CI-45850989877 | WO-C-FORMAT、WO-G13-CONTINUOUS |
| H01 | 输出一份完整记录 + 一份分析 | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | #620 | EV-H01-AUDIT | WO-H01-AUTO-DERIVED-E2E |
| H02 | 参考 OPPO「笔记速览图」做可视化速览 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H02-AUDIT | WO-H02-VISUAL-SUMMARY |
| H03 | 固定材料原始录音可进入整理上下文 | B3 | API；Web；Android；Windows | FAIL | stale | #583、#612 | EV-H03-AUDIT、EV-RF-H03-MULTIMODAL、EV-CI-45857319607 | WO-H03-LONG-AUDIO |
| H04 | 手动笔记、照片、手写图片一起参与分析 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H04-AUDIT | WO-H04-IMAGE-ADAPT |
| H05 | 整理时同时生成标题，自动填到笔记标题 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H05-AUDIT | WO-H05-TITLE |
| H06 | 完整记录和分析可手动修改，再次生成不覆盖改过的内容 | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | — | EV-H06-AUDIT | WO-H06-DERIVED-PROVENANCE |
| H07 | 补写后点「更新整理」，不每改一个字就调 AI | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H07-AUDIT | WO-H07-UPDATE-DERIVED |
| H08 | 整理进度显示，完成后推送通知 | B3 | API；Web；Android；Windows | FAIL | stale | #616 | EV-H08-AUDIT、EV-CI-45926319904 | WO-H08-NOTIFY |
| H09 | 课堂/会议等不同整理模板 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H09-AUDIT | WO-H09-TEMPLATES |
| H10 | 导出、分享整理结果（图片/PDF/Markdown） | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H10-AUDIT | WO-H10-EXPORT |
| H11 | 再次整理用全部录音和最新正文，不只分析新增部分 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-H11-AUDIT | WO-H11-LATEST-CONTEXT |
| I01 | AI 从笔记提取待办 | B3 | API；Web；Android；Windows | FAIL | stale | — | EV-I01-AUDIT | WO-I01-INLINE-SUGGESTION |
| I02 | 待办可修改后一键加入「任务」 | B3 | API；Web；Android；Windows | NOT-VERIFIED | stale | #100、#62 | EV-I02-AUDIT、EV-CI-45671814150 | WO-I02-I04-CLIENTS |
| I03 | 提取日程，加入日历 | B3 | API；Web；Android；Windows | FAIL | stale | #589, #93, #100, #60 等 5 项 | EV-I03-AUDIT, EV-RF-I03-I04-CLIENTS, EV-CI-45640493402 等 5 项 | WO-I03-I04-CLIENTS |
| I04 | 重复点击不重复创建 | B3 | API；Web；Android；Windows | FAIL | stale | #589, #93, #100, #60 等 5 项 | EV-I04-AUDIT, EV-RF-I03-I04-CLIENTS, EV-CI-45640493402 等 5 项 | WO-I03-I04-CLIENTS |
| J01 | 不自己写调用协议，用成熟开源组件（MEAI + OpenAI .NET SDK） | B4 | API；Web | FAIL | stale | #617 | EV-J01-AUDIT、EV-CI-45835093386 | WO-J01-SDK-ADAPTER |
| J02 | 按类型设置模型（文本、视觉、ASR） | B4 | API；Web | NOT-VERIFIED | stale | — | EV-J02-AUDIT | WO-J02-ADMIN-BROWSER |
| J03 | 调用记录保留实际提示词和回复 | B4 | API；Web | FAIL | stale | #590 | EV-J03-AUDIT、EV-CI-45848909302 | WO-J03-BATCH-AUDIT |
| J04 | 调用统计（次数、成功率、耗时、Token） | B4 | API；Web | FAIL | stale | #590 | EV-J04-AUDIT、EV-CI-45848909302 | WO-J04-USAGE-STATS |
| J05 | 稳定后移除 LiteLLM | B4 | API；Web | FAIL | stale | #617 | EV-J05-AUDIT、EV-CI-45835093386 | WO-J05-LITELLM-REMOVAL |
| J06 | 每种用途选默认模型 | B4 | API；Web | FAIL | stale | #617、#620 | EV-J06-AUDIT、EV-CI-45835093386 | WO-J06-PURPOSE-DEFAULTS、WO-Z02-CONSENT-E2E |
| J07 | API Key 安全保存，界面不回显明文 | B4 | API；Web | FAIL | stale | — | EV-J07-AUDIT | WO-J07-SECRET-BOUNDARY |
| J08 | 「测试连接」按钮 | B4 | API；Web | FAIL | stale | #617 | EV-J08-AUDIT、EV-CI-45835093386 | WO-J08-CONNECTION-TEST |
| J09 | 按 AI 服务连接累计 Token/费用 | B4 | API；Web | FAIL | stale | #585、#590 | EV-J09-AUDIT、EV-RF-J09-PROVIDER、EV-CI-45848909302 | WO-J09-USAGE-COST |
| K01 | 没配云盘时默认带一个本地盘，以云盘形式接入 | B4 | API；Web | NOT-VERIFIED | stale | — | EV-K01-AUDIT | WO-K01-FILES-E2E |
| K02 | OneDrive / Google Drive 接入 | B4 | API；Web | FAIL | stale | — | EV-K02-AUDIT | WO-K02-CLOUD-OAUTH-CONFIG |
| K03 | 文件浏览、上传、下载、预览 | B4 | API；Web；Android；Windows | FAIL | stale | — | EV-K03-AUDIT | WO-K03-FILES-CLIENTS |
| K04 | 存储空间占用显示 | B4 | API；Web；Android；Windows | FAIL | stale | — | EV-K04-AUDIT | WO-K04-STORAGE-USAGE |
| L01 | 原生客户端为主要入口，Web 保留；壳方案放弃 | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L01-AUDIT | WO-L01-SHELL-RETIRE |
| L02 | 守护（采集）程序放进客户端，一个安装入口 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L02-AUDIT | WO-L02-DEVICE-SINGLE-ENTRY |
| L03 | 今日原生页 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L03-AUDIT | WO-L03-DEVICE-TODAY |
| L04 | 任务原生页 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L04-AUDIT | WO-L04-DEVICE-TASKS |
| L05 | Android 原生日/周/月视图、日期导航、分页事件读取、时区/DST/重复日程和本地日期展示 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | #88、#91 | EV-L05-AUDIT | WO-L05-ANDROID-CALENDAR |
| L06 | 提醒原生页 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L06-AUDIT | WO-L06-DEVICE-REMINDERS |
| L07 | 文件原生页 | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L07-AUDIT | WO-L07-ANDROID-FILES |
| L08 | 设置原生页（账户、服务器、AI、同步） | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L08-AUDIT | WO-L08-ANDROID-SETTINGS |
| L09 | 登录、服务器地址配置 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L09-AUDIT | WO-L09-DEVICE-LOGIN |
| L10 | 桌面小组件/快捷方式一键开始记录 | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L10-AUDIT | WO-L10-ANDROID-LAUNCH |
| L11 | 深色模式 | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L11-AUDIT | WO-L11-ANDROID-DARK |
| L12 | 应用内检查更新 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L12-AUDIT | WO-L12-DEVICE-UPDATE |
| L13 | 不再用 WebView 嵌网页承载日常页面 | B5 | API(协调)；Web(协调)；Android | FAIL | stale | — | EV-L13-AUDIT | WO-L13-ANDROID-WEBVIEW-RETIRE |
| L14 | 正式（release）包可安装使用，不只 debug 版有笔记 | B5 | API(协调)；Web(协调)；Android | NOT-VERIFIED | stale | — | EV-L14-AUDIT | WO-L14-DEVICE-RELEASE |
| M01 | 电脑端也能记录（不只看） | B5 | API(协调)；Web(协调)；Windows | NOT-VERIFIED | stale | — | EV-M01-AUDIT | WO-M01-DEVICE-RECORDING |
| M02 | 原生笔记列表、阅读、编辑 | B5 | API(协调)；Web(协调)；Windows | FAIL | stale | — | EV-M02-AUDIT | WO-M02-WINDOWS-NOTES |
| M03 | 电脑端录音 | B5 | API(协调)；Web(协调)；Windows | FAIL | stale | — | EV-M03-AUDIT | WO-M03-WINDOWS-RECORDING |
| M04 | 托盘采集保留 | B5 | API(协调)；Web(协调)；Windows | NOT-VERIFIED | stale | — | EV-M04-AUDIT | WO-M04-DEVICE-TRAY |
| M05 | Windows 原生今日/任务/日历页可从入口打开，支持读取/新建/完成任务并与服务器联动 | B5 | API(协调)；Web(协调)；Windows | NOT-VERIFIED | stale | #58 | EV-M05-AUDIT、EV-RF-M05-WINDOWS-TODAY | WO-M05-WINDOWS-TODAY |
| M06 | Windows 日常页面由原生工作区承载 | B5 | API(协调)；Web(协调)；Windows | FAIL | stale | #53、#59、#60 | EV-M06-AUDIT、EV-CI-45652799869 | WO-M06-WINDOWS-SHELL |
| N01 | Web 保留，可查看编辑笔记 | B5 | API；Web | FAIL | stale | — | EV-N01-AUDIT、EV-RF-N01-BROWSER | WO-N01-WEB-NOTES |
| N02 | Web 处理状态页 | B5 | API；Web | FAIL | stale | — | EV-N02-AUDIT | WO-N02-WEB-STATUS |
| N03 | Web 设置齐全（AI、ASR、文件、同步） | B5 | API；Web | FAIL | stale | — | EV-N03-AUDIT | WO-N03-WEB-SETTINGS |
| O01 | 延续 PIM 蓝色基调 | B6 | Web；Android；Windows | NOT-VERIFIED | stale | — | EV-O01-AUDIT | WO-VISUAL-FINISH |
| O02 | 颜色和基础组件一处定义，多处引用 | B6 | Web；Android；Windows | FAIL | stale | — | EV-O02-AUDIT | WO-VISUAL-FINISH |
| O03 | 安卓引入 Material 3 / Compose Animation / Adaptive / Coil / Lottie | B6 | Android | FAIL | stale | — | EV-O03-AUDIT | WO-VISUAL-FINISH |
| O04 | 原决议：跳过 OPPO 笔记动画和字体 | B6 | Android | APPROVED-EXCEPTION | stale | — | EV-O04-AUDIT、EV-DEC-O04 | 不排期 |
| O05 | 界面不工程化，文案说人话，不堆版本/审计信息 | B6 | Web；Android；Windows | FAIL | stale | — | EV-O05-AUDIT | WO-Z01-USER-FACING、WO-VISUAL-FINISH |
| O06 | 空状态、加载、错误都有设计 | B6 | Web；Android；Windows | FAIL | stale | — | EV-O06-AUDIT | WO-VISUAL-FINISH |
| P01 | 安卓定位采集误判少 | B6 | API；Web；Android | FAIL | stale | — | EV-P01-AUDIT | WO-P01-LOCATION-QUALITY |
| P02 | 手机使用记录 | B6 | API；Web；Android | NOT-VERIFIED | stale | — | EV-P02-AUDIT | WO-P02-PHONE-USAGE-E2E |
| P03 | 电脑使用记录、浏览器使用 | B6 | API；Web；Android(只读)；Windows | NOT-VERIFIED | stale | — | EV-P03-AUDIT | WO-P03-EXTENSION-E2E |
| P04 | 采集状态可见，缺权限时提示 | B6 | Web；Android；Windows | NOT-VERIFIED | stale | #95 | EV-P04-AUDIT | WO-P04-PERMISSIONS-E2E |
| P05 | 历史位置可手动纠正 | B6 | API；Web；Android；Windows(只读) | FAIL | stale | #602 | EV-P05-AUDIT、EV-RF-P05-BROWSER、EV-CI-45856428856 | WO-P05-LOCATION-CORRECTION-E2E |
| Q01 | 注册/登录/续期、首账号管理员、公开注册关闭、管理员建号/停用、UserId 隔离及三端呈现 | B6 | API(协调)；Web(协调)；Android(协调)；Windows(协调) | BLOCKED | stale | — | EV-Q01-AUDIT、EV-DEC-Q01 | WO-Q01-AUTH-SCOPE |
| Q02 | 原决议：当前不做全量备份与导出 | B6 | API；Web；Android；Windows | APPROVED-EXCEPTION | stale | — | EV-Q02-AUDIT、EV-DEC-Q02 | 不排期 |
| Q03 | 回收站 | B6 | API；Web；Android；Windows | FAIL | stale | #614 | EV-Q03-AUDIT、EV-RF-Q03-CROSS-DEVICE、EV-CI-45863519886 | WO-Q03-Q08-E2E |
| Q04 | 敏感数据传输加密（HTTPS） | B6 | API；Web；Android；Windows | FAIL | stale | #607、#101、#63 | EV-Q04-AUDIT、EV-RF-Q04-PUBLIC-TLS、EV-CI-45841606243 | WO-Q04-PUBLIC-TLS |
| Q05 | 崩溃/错误上报，一键导出诊断 | B6 | API；Web；Android；Windows | FAIL | stale | #609 | EV-Q05-AUDIT | WO-Q05-DIAGNOSTICS-E2E |
| Q06 | 推送通知 | B6 | API；Web；Android；Windows | FAIL | stale | #616 | EV-Q06-AUDIT、EV-RF-Q06-PUSH、EV-CI-45926319904 | WO-Q06-PUSH-E2E |
| Q07 | 服务器返回完整 Uploaded/Conflict 回执后清理本机持久副本 | B6 | Web；Android；Windows | BLOCKED | stale | #611 | EV-Q07-AUDIT、EV-DEC-Q07 | WO-Q07-UPLOAD-RETENTION |
| Q08 | 跨任务、日历、笔记的全局搜索 | B6 | API；Web；Android；Windows | FAIL | stale | #600 | EV-Q08-AUDIT、EV-RF-Q08-BROWSER、EV-CI-45662774385 | WO-Q08-SEARCH-E2E |
| R01 | 14 个 snake_case 参数统一为 camelCase 并拒绝旧名 | B7 | API；MCP | NOT-VERIFIED | stale | #588 | EV-R01-AUDIT、EV-CI-45507678632 | WO-R01-EXTERNAL-MCP |
| R02 | 事件时区可判定（#450） | B7 | API；MCP；Web | FAIL | stale | #610 | EV-R02-AUDIT | WO-R02-R03-GRAPH |
| R03 | create_event 能写回 Outlook（#458） | B7 | API；MCP | FAIL | stale | #615 | EV-R03-AUDIT、EV-CI-45867495126 | WO-R02-R03-GRAPH |
| R04 | REST 重复 status 为 OR、MCP statuses 数组、Web/Android/Windows 多选 | B7 | API；MCP；Web；Android；Windows | NOT-VERIFIED | stale | #576、#94、#57 | EV-R04-AUDIT, EV-RF-R04-ISOLATED, EV-RF-R04-AC8-EXTERNAL-MCP 等 6 项 | WO-R04-CLIENTS-EXTERNAL |
| R05 | MCP 可读写笔记 | B7 | API；MCP | FAIL | stale | #613、#621 | EV-R05-AUDIT、EV-RF-R05-EXTERNAL-MCP、EV-CI-45881012067 | WO-R05-COMPLETE-MCP、WO-R05-AC05-CAPACITY |
| S01 | Release 版本与发布安全 | B7 | API；Web；Release | NOT-VERIFIED | stale | #589, #601, #603, #604 等 5 项 | EV-S01-AUDIT, EV-RF-S01-RELEASE, EV-CI-45640493402 等 5 项 | WO-RELEASE-REAL-PROMOTION |
| Z01 | 普通用户不见数据中心/版本历史/版本 ID/保存编号/审计字段 | B7 | API；Web；Android；Windows | FAIL | stale | #597 | EV-Z01-AUDIT、EV-RF-Z01-BROWSER、EV-CI-45750942089 | WO-Z01-USER-FACING |
| Z02 | 服务器唯一事实源 | B7 | API；Web；Android；Windows | NOT-VERIFIED | stale | #572、#82、#50 | EV-Z02-AUDIT、EV-RF-Z02-CROSS-DEVICE | WO-Z02-CONSENT-E2E、WO-Z02-CROSS-DEVICE |

## 阻塞与例外 / Blocked and exceptions

| 编号 | 状态 | 原因 | 解除 / 批准 |
| --- | --- | --- | --- |
| B15 | APPROVED-EXCEPTION | 不做 100 分钟长笔记性能验收，仅登记性能风险。 | user / PIM功能确认-最终交付-20261122 |
| C14 | APPROVED-EXCEPTION | 由 C12 直接采集 Opus/Ogg 取代结束后 WAV 合成，随 C12 验收。 | user / PIM功能确认-最终交付-20261122 |
| C21 | APPROVED-EXCEPTION | 实时转写暂缓，仅保留本地小模型定义勘误。 | user / PIM功能确认-最终交付-20261122 |
| E03 | APPROVED-EXCEPTION | 不做通用 PDF/文档附件，仅保留 Markdown、照片、手写和录音。 | user / PIM功能确认-最终交付-20261122 |
| O04 | APPROVED-EXCEPTION | 跳过 OPPO 笔记动画和字体采用，原素材清单移出接入计划。 | user / PIM功能确认-最终交付-20261122 |
| Q01 | BLOCKED | 需求确认缺口：平台范围与验收口径未确认，不能按跳过处理。 | 用户补齐 Q01 的平台范围与验收口径。；在真实注册/登录/续期/管理员建号/停用流程上执行验收。 |
| Q02 | APPROVED-EXCEPTION | 当前不做全量备份与导出，Kopia 仅保留为早期文件基础能力。 | user / PIM功能确认-最终交付-20261122 |
| Q07 | BLOCKED | 产品语义冲突：Uploaded 与 Conflict 终态清理本机副本的要求，与 Android 冲突保护保留双方原件冲突。 | 用户裁决 Conflict 终态是否清理本机持久副本。；裁决后实现 Android/Windows 清理并补真实设备验收。 |

## PR 索引 / PR index

| PR | 仓库 | 状态 | head | merge | 状态 freshness | 引用项 |
| --- | --- | --- | --- | --- | --- | --- |
| [#82](https://github.com/2746267826/pim-android/pull/82) | pim-android | merged | `—` | `—` | stale | Z02 |
| [#88](https://github.com/2746267826/pim-android/pull/88) | pim-android | open | `139fec4` | `—` | stale | L05 |
| [#89](https://github.com/2746267826/pim-android/pull/89) | pim-android | merged | `a0a980c0` | `629e4c5` | stale | C04 |
| [#91](https://github.com/2746267826/pim-android/pull/91) | pim-android | merged | `174018ff` | `a1b0b7a7` | stale | L05 |
| [#93](https://github.com/2746267826/pim-android/pull/93) | pim-android | open | `63ce05dd` | `—` | stale | I03、I04 |
| [#94](https://github.com/2746267826/pim-android/pull/94) | pim-android | merged | `e3e496bc` | `1ee2fe28` | stale | R04 |
| [#95](https://github.com/2746267826/pim-android/pull/95) | pim-android | open | `8d95efae` | `—` | stale | P04 |
| [#96](https://github.com/2746267826/pim-android/pull/96) | pim-android | merged | `—` | `—` | stale | A01、C18 |
| [#97](https://github.com/2746267826/pim-android/pull/97) | pim-android | merged | `cdbda8fb` | `c7ff54f7` | stale | D03、D04 |
| [#100](https://github.com/2746267826/pim-android/pull/100) | pim-android | merged | `d7fb9052` | `b23fbe89` | stale | I02、I03、I04 |
| [#101](https://github.com/2746267826/pim-android/pull/101) | pim-android | open | `539f4231` | `—` | stale | Q04 |
| [#102](https://github.com/2746267826/pim-android/pull/102) | pim-android | open | `0324ddfc` | `—` | stale | A02、A03 |
| [#572](https://github.com/2746267826/pim-api/pull/572) | pim-api | merged | `5a9c3b5f` | `5a9c3b5f` | current | Z02 |
| [#576](https://github.com/2746267826/pim-api/pull/576) | pim-api | merged | `84f43d6b` | `cb96fc30` | current | R04 |
| [#583](https://github.com/2746267826/pim-api/pull/583) | pim-api | merged | `103b6d89` | `103b6d89` | current | H03 |
| [#585](https://github.com/2746267826/pim-api/pull/585) | pim-api | merged | `4212668` | `65ed0935` | current | J09 |
| [#588](https://github.com/2746267826/pim-api/pull/588) | pim-api | merged | `5e2768d7` | `667da4fc` | current | R01 |
| [#589](https://github.com/2746267826/pim-api/pull/589) | pim-api | merged | `743a77ac` | `05552e84` | current | I03、I04、S01 |
| [#590](https://github.com/2746267826/pim-api/pull/590) | pim-api | open | `0e6262f2` | `—` | stale | J03、J04、J09 |
| [#594](https://github.com/2746267826/pim-api/pull/594) | pim-api | merged | `d3a55ceb` | `1dd3032d` | current | D04 |
| [#596](https://github.com/2746267826/pim-api/pull/596) | pim-api | open | `3594289e` | `—` | stale | C12、G13 |
| [#597](https://github.com/2746267826/pim-api/pull/597) | pim-api | merged | `bcee101f` | `c47542e1` | current | Z01 |
| [#600](https://github.com/2746267826/pim-api/pull/600) | pim-api | merged | `a3b6b568` | `a4919618` | current | A04、Q08 |
| [#601](https://github.com/2746267826/pim-api/pull/601) | pim-api | merged | `94419049` | `f79cc3e8` | current | S01 |
| [#602](https://github.com/2746267826/pim-api/pull/602) | pim-api | open | `65d1d2b5` | `—` | stale | P05 |
| [#603](https://github.com/2746267826/pim-api/pull/603) | pim-api | merged | `776bf46e` | `b0f92b16` | current | S01 |
| [#604](https://github.com/2746267826/pim-api/pull/604) | pim-api | merged | `033f3a5f` | `fa60b58e` | current | S01 |
| [#606](https://github.com/2746267826/pim-api/pull/606) | pim-api | open | `047563cc` | `—` | stale | — |
| [#607](https://github.com/2746267826/pim-api/pull/607) | pim-api | open | `cfd4f573` | `—` | stale | Q04 |
| [#608](https://github.com/2746267826/pim-api/pull/608) | pim-api | merged | `—` | `ae99b28a` | current | — |
| [#609](https://github.com/2746267826/pim-api/pull/609) | pim-api | open | `bee3d45c` | `—` | stale | Q05 |
| [#610](https://github.com/2746267826/pim-api/pull/610) | pim-api | open | `fd7b7c7a` | `—` | stale | R02 |
| [#611](https://github.com/2746267826/pim-api/pull/611) | pim-api | open | `9787ae26` | `—` | stale | Q07 |
| [#612](https://github.com/2746267826/pim-api/pull/612) | pim-api | open | `f12c4c23` | `—` | stale | H03 |
| [#613](https://github.com/2746267826/pim-api/pull/613) | pim-api | open | `add843e9` | `—` | stale | R05 |
| [#614](https://github.com/2746267826/pim-api/pull/614) | pim-api | open | `e1c15b99` | `—` | stale | A07、Q03 |
| [#615](https://github.com/2746267826/pim-api/pull/615) | pim-api | merged | `9d7af01d` | `555b5d4d` | current | R03 |
| [#616](https://github.com/2746267826/pim-api/pull/616) | pim-api | open | `e5de6753` | `—` | stale | H08、Q06 |
| [#617](https://github.com/2746267826/pim-api/pull/617) | pim-api | merged | `95568663` | `63cc716a` | current | J01、J05、J06、J08 |
| [#618](https://github.com/2746267826/pim-api/pull/618) | pim-api | merged | `—` | `d4ad06b3` | current | — |
| [#619](https://github.com/2746267826/pim-api/pull/619) | pim-api | merged | `—` | `4123f73f` | current | — |
| [#620](https://github.com/2746267826/pim-api/pull/620) | pim-api | open | `b884f3ec` | `—` | stale | G01、H01、J06 |
| [#621](https://github.com/2746267826/pim-api/pull/621) | pim-api | open | `493a0522` | `—` | stale | R05 |
| [#50](https://github.com/2746267826/pim-windows/pull/50) | pim-windows | merged | `—` | `—` | stale | Z02 |
| [#52](https://github.com/2746267826/pim-windows/pull/52) | pim-windows | merged | `2d2b49b` | `d120a88` | stale | D08 |
| [#53](https://github.com/2746267826/pim-windows/pull/53) | pim-windows | merged | `4677763` | `ae5f61ae` | stale | M06 |
| [#57](https://github.com/2746267826/pim-windows/pull/57) | pim-windows | merged | `bd45e21e` | `f278996a` | stale | R04 |
| [#58](https://github.com/2746267826/pim-windows/pull/58) | pim-windows | merged | `911ad809` | `af558d03` | stale | M05 |
| [#59](https://github.com/2746267826/pim-windows/pull/59) | pim-windows | merged | `c4266cc` | `70b735a5` | stale | M06 |
| [#60](https://github.com/2746267826/pim-windows/pull/60) | pim-windows | open | `7bf52b7a` | `—` | stale | I03、I04、M06 |
| [#61](https://github.com/2746267826/pim-windows/pull/61) | pim-windows | open | `ee730075` | `—` | stale | S01 |
| [#62](https://github.com/2746267826/pim-windows/pull/62) | pim-windows | open | `e7be0936` | `—` | stale | I02、I03、I04 |
| [#63](https://github.com/2746267826/pim-windows/pull/63) | pim-windows | open | `4636dca6` | `—` | stale | Q04 |

## CI 索引 / CI index

| Run | PR | head | 结论 | 关键 job | freshness |
| --- | --- | --- | --- | --- | --- |
| `45926319904` | PR-pim-api-616 | `e5de6753` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45881012067` | PR-pim-api-613 | `add843e9` | success | CI Gate, 6 shards, build-docker | stale |
| `45867495126` | PR-pim-api-615 | `9d7af01d` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45863519886` | PR-pim-api-614 | `e1c15b99` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45857319607` | PR-pim-api-612 | `f12c4c23` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45856428856` | PR-pim-api-602 | `65d1d2b5` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45850989877` | PR-pim-api-596 | `3594289e` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45848909302` | PR-pim-api-590 | `0e6262f2` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45841606243` | PR-pim-api-607 | `cfd4f573` | success | CI Gate, 6 shards, web-build, build-docker | stale |
| `45835093386` | PR-pim-api-617 | `95568663` | success | CI Gate, 6 shards, web-build, build-mcp, build-docker | stale |
| `45750942089` | PR-pim-api-597 | `bcee101f` | success | CI Gate, 6 shards, web-build | stale |
| `45671814150` | PR-pim-android-100 | `b23fbe89` | failure | Contract freshness | stale |
| `45664309218` | PR-pim-api-594 | `d3a55ceb` | success | CI Gate, web-build, build-api, build-docker | stale |
| `45662774385` | PR-pim-api-600 | `a3b6b568` | success | CI Gate, 6 shards, web-build, build-mcp, build-docker | stale |
| `45653708869` | PR-pim-api-589 | `05552e84` | success | CI Gate, build-api, web-build, build-docker, release | stale |
| `45652799869` | PR-pim-windows-59 | `70b735a5` | success | CI Gate, build-windows, contract-freshness | stale |
| `45640493402` | PR-pim-api-589 | `743a77ac` | success | CI Gate, build-api, web-build, build-docker | stale |
| `45630816563` | PR-pim-api-604 | `033f3a5f` | success | CI Gate, build-api, build-mcp, build-docker | stale |
| `45515208111` | PR-pim-android-94 | `e3e496bc` | success | CI Gate, build-android, Contract freshness | stale |
| `45511045329` | PR-pim-windows-57 | `bd45e21e` | success | CI Gate, build-windows | stale |
| `45507678632` | PR-pim-api-588 | `5e2768d7` | success | CI Gate, build-api, web-build, build-docker | stale |
| `45412194523` | PR-pim-api-576 | `84f43d6b` | success | CI Gate, build-api, web-build, build-docker | stale |

## 真实流程索引 / Real-flow index

| 流程 | 覆盖项 | 平台 | 结果 | freshness | 环境 | 边界 |
| --- | --- | --- | --- | --- | --- | --- |
| RF-R04-ISOLATED | R04 | API、MCP、Web | 历史通过（stale） | stale | 隔离 Docker Test API + PostgreSQL + Chromium | 旧台账记录的真实链路未在本轮重跑；不能证明外部独立 MCP 客户端。 |
| RF-R04-AC8-EXTERNAL-MCP | R04 | MCP | 未执行 | not-verified | — | 尚无外部独立 MCP 客户端执行记录，R04 不得记 PASS。 |
| RF-M05-WINDOWS-TODAY | M05 | Windows | 未执行 | not-verified | — | 登录、托盘点击、刷新、新建与完成任务未在真实桌面执行。 |
| RF-D04-CROSS-END | D04 | Android、Windows、Web | 未执行 | not-verified | — | Windows 工具缺口、Web 已保存块不可再编辑、真机与往返回读均未验。 |
| RF-Z02-CROSS-DEVICE | Z02 | API、Android、Windows、Web | 未执行 | not-verified | — | 真实 provider、Hangfire 多进程、浏览器与客户端换端未执行。 |
| RF-S01-RELEASE | S01 | Release | 未执行 | not-verified | — | 正式 bump/promote、并发重跑、`:latest` 收敛与生产部署未执行。 |
| RF-R05-EXTERNAL-MCP | R05 | MCP | 未执行 | not-verified | — | #613 仍 OPEN；外部客户端与真实部署未验。 |
| RF-Q08-BROWSER | Q08 | Web | 未执行 | not-verified | — | 双账号、持久索引/重建/离线缓存与字段级定位未验。 |
| RF-Z01-BROWSER | Z01 | Web | 未执行 | not-verified | — | 协议/诊断例外仍存在，真实浏览器与客户端呈现未验。 |
| RF-Q04-PUBLIC-TLS | Q04 | API、Web | 未执行 | not-verified | — | 仅隔离拓扑通过；1Panel/OpenResty/证书链与生产入口未验。 |
| RF-H03-MULTIMODAL | H03 | API | 未执行 | not-verified | — | 真实 provider 质量/费用、Hangfire 多进程与 PostgreSQL 行锁未验。 |
| RF-C12-FFMPEG | C12 | Android、Windows | 未执行 | not-verified | — | 客户端仍为 WAV 分片/整段；runner 无 FFmpeg，相关用例 skipped。 |
| RF-I03-I04-CLIENTS | I03、I04 | Android、Windows、Web | 未执行 | not-verified | — | Windows 客户端 PR 仍 OPEN；Android 契约 freshness 红；真实三端未验。 |
| RF-Q03-CROSS-DEVICE | Q03 | Android、Windows、Web | 未执行 | not-verified | — | Android/Windows 原生回收站未交付，真实浏览器跨端未验。 |
| RF-P05-BROWSER | P05 | Web | 未执行 | not-verified | — | API/Web 实现 OPEN；真实浏览器 E2E 与客户端只读消费未验。 |
| RF-Q06-PUSH | Q06 | API、Android、Windows、Web | 未执行 | not-verified | — | FCM/WNS/Web Push、设备级 dismiss/click 与保留策略未验。 |
| RF-J09-PROVIDER | J09 | API | 未执行 | not-verified | — | 真实第三方 provider 记账/限额、管理员浏览器与崩溃恢复未验。 |
| RF-N01-BROWSER | N01 | Web | 未执行 | not-verified | — | 无录音播放器、正文分段 Textarea、已有材料不可删除，真实浏览器未验。 |

## 决策索引 / Decision index

| 决定 | 类型 | 状态 | 批准人 | 涉及项 | 依据 |
| --- | --- | --- | --- | --- | --- |
| DEC-B15 | scope-exception | approved | user | B15 | PIM功能确认-最终交付-20261122/01-讨论记录/B08-B16-编辑器基础能力.md |
| DEC-C14 | scope-exception | approved | user | C14 | PIM功能确认-最终交付-20261122/01-讨论记录/C12-压缩录音格式与处理链路.md |
| DEC-C21 | scope-exception | approved | user | C21 | PIM功能确认-最终交付-20261122/01-讨论记录/C21-实时转写.md |
| DEC-E03 | scope-exception | approved | user | E03 | PIM功能确认-最终交付-20261122/01-讨论记录/E03-文档附件.md |
| DEC-O04 | scope-exception | approved | user | O04 | PIM功能确认-最终交付-20261122/01-讨论记录/O03-Android视觉组件接入.md |
| DEC-Q02 | scope-exception | approved | user | Q02 | PIM功能确认-最终交付-20261122/01-讨论记录/Q02-全量备份与导出.md |
| DEC-Q01 | product-decision | pending | — | Q01 | PIM功能确认-最终交付-20261122/README.md |
| DEC-Q07 | product-decision | pending | — | Q07 | PIM功能确认-最终交付-20261122/01-讨论记录/Q07-本地缓存清理.md |
| DEC-R05-AC05 | product-decision | pending | — | — | docs/product/pim-delivery-ledger-20261125.md |
| DEC-MIGRATION-STRUCTURE | migration | recorded | — | — | docs/product/pim-delivery-ledger-20261125.md |

## 证据等级 / Evidence levels

| 等级 | 含义 | 条数 |
| --- | ---: | ---: |
| E0 | 决议/范围记录：只证明有人批准范围或做出记录，不证明实现。 | 10 |
| E1 | 源码/文档只读检查：证明代码或文本存在，不证明运行行为。 | 161 |
| E2 | 单元/组件测试：证明被测切片，不证明端到端或真实环境。 | 0 |
| E3 | CI 运行：证明该 run 的 head 上自动化通过/失败，不证明用户流程。 | 22 |
| E4 | 真实环境流程：真实 API/数据库/浏览器/客户端链路，需记录环境与产物。 | 18 |
| E5 | 真实设备/桌面：Android 实机/OEM、Windows 交互桌面，需记录设备与步骤。 | 0 |
| E6 | 生产/发布验收：正式发布、生产部署或线上只读验证。 | 0 |

## 续跑协议 / Resume protocol

1. 先更新 `source_snapshot`，记录三仓 `origin/master` SHA；旧 head 只能用于定位。
2. 只改结构化源：item 的 `status`、`freshness`、`evidence`、`prs`、`next`，然后重新渲染。
3. `PASS` 需要 current freshness 的 E4/E5/E6 证据覆盖全部 required 平台；CI 绿不算 PASS。
4. `BLOCKED` 必须写 `blocked_reason` 与 `unblock_conditions`；解除后改回 FAIL/NOT-VERIFIED。
5. `APPROVED-EXCEPTION` 必须引用 `approved` 状态的 decision，且不得把验收条件标为 verified。
6. 旧证据保持 `stale`，迁移时保留 `legacy_status` 与 `legacy_source`，不得静默改写。

---

验证命令：

```bash
python3 scripts/ci/validate-delivery-ledger.py
python3 scripts/ci/render-delivery-ledger.py
python3 scripts/ci/validate-delivery-ledger.py
```
