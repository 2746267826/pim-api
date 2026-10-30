# 移动同步与诊断导出修复边界

更新日期：2026-10-30。状态：v0.38 代码与 GitHub Release 已交付并核验；
物理设备、旧 APK 升级、完整系统旅程与 production 仍未验收。

本文记录移动使用记录同步的半开窗口修复、诊断导出受控验证和未验边界。范围与
45 个稳定需求编号继续以[原生客户端与笔记路线图](../product/native-notes-roadmap.md#v038-current)
为准；本文不新增编号、不另建 backlog，也不把 GitHub Release 或组件测试写成
完整系统、物理设备或 production 验收。

## 半开窗口契约

- 服务端窗口为 `[windowStartUtc, windowEndUtc)`；旧客户端把点记录放在闭式
  上界会产生 400。服务端预算和窗口校验保持不变。
- Android 同步把 point event 的原始 `eventTimeUtc` 保留不变，只把批次
  exclusive end 调整为 `eventTime + 1ms`；无法表示的 `Long.MAX_VALUE`
  会阻止本地上传并保留 pending，不回绕、不删除。
- 普通批次目标上界为 2 小时；合法的 25 小时 fallback summary 保持原子完整，
  与 summary 同边界的 point 进入下一批次。批次仍按稳定 ID、owner/server
  binding、ack/retry 和同批次重放语义处理。

受控证据：新增 WebFactory + InMemory half-open HTTP 3/3 PASS（旧闭式 400、
修正后 `+1ms` 200、counts/rows 回读、同批次无重复）；relation/regression
focused 77 PASS / 8 SKIP；隔离 PostgreSQL 8/8 是复用 #529 既有
relation/regression 在新 cluster 的独立结果，不是 half-open HTTP 在 PG 上
运行，也不是真实外部后端 E2E。三组计数分账，不重复加总。Android window
targeted 7/7 PASS，sync suite 193/193 PASS。

## 诊断导出边界

本轮只让 `mobile-*.jsonl` 日志读取走 bounded/streaming：按行/文件/总量预算
处理，超限行整行丢弃并写 synthetic omission 记录；manifest 标记
`complete`/`partial`，状态 UI 显示 partial 提示。受控 JVM 133/133 PASS，
原生 Android 模拟器 export 组件 2/2 PASS：真实 Repository 生成有效
isolated ZIP、脱敏、partial、FileProvider chooser cancel 与 IO failure。

以下路径没有改为流式或分页：`coreEntries` 的 dropped-location、forensics、
24h recent-location 仍先取现有 DAO 全量 `List`，再 `joinToString` 后写入
ZIP。AC 9.3 不设条数上限，因此不得为掩盖内存风险截断行数。日志 bounds
不能表述为“整个诊断包任意大小内存有界”；极大 DB 表和原物理手机
flash/crash（无 stack/logcat）仍未验证。

## 隔离 QA 与 lineage

最终 6 个**原生 Android 模拟器组件实测** case 为 6/6 PASS：4 个 sync
（9.496s）覆盖 Room v3→v4、DAO、Coordinator、真实 bound client + 合成
MockWebServer 模拟器 loopback、cancel/owner/replay；2 个 export（1.583s）
覆盖真实 Repository 有效 ZIP/脱敏/partial/FileProvider chooser cancel/
IO failure。App APK `7B1DA6...`、Test APK `08C56...`、certificate `b18...`。
这不是物理手机证据，也不是 WebFactory 外部服务端到端。

`qa/lineage-correction.json` 是 append-only 更正：没有单独 snapshot
精确 runtime HEAD；第二次 debug build 时间晚于产品 commit，最终 test
commit 为 `5c4d8985`。同源结论来自源码 blob/hash、APK hash 与 raw
instrumentation logs 的匹配，不来自伪造的 runtime HEAD。READY2/3 原文
保留。测试 `finally` 恢复原测试会话/prefs，只使用 synthetic/no private
credentials；不能写成测试从未写 prefs。ZIP 文件由 test `finally` 清理，
保留 raw content 校验日志，不以伪造 ZIP artifact 替代。

## 交付与未验门槛

- API tests PR #537 已于 2026-10-30T11:56:46Z merge 到 `9631437d`；
  exact head CI `41048454669` required/Gate SUCCESS。master CI
  `41051709488` completed/SUCCESS，Gate/release SUCCESS，MCP/contract notify
  SKIP。Release `api-v26.10.1019` 于 12:26:48Z published，tag 精确解析到
  `9631437d`；4 个 uploaded assets 的尺寸和 SHA-256 已只读核验。
- Android PR #55 的最终 commit 为 `5c4d8985`（parent `dd90`），已 READY
  并普通 merge 为 `2968eea4`（2026-10-30T12:19:41Z）；exact CI
  `41052159717` 的 required/Gate 在 merge 前 SUCCESS。master CI
  `41054503798` @`2968eea4` completed/SUCCESS，Gate/release SUCCESS。
  Release `android-v26.10.1140` 于 12:31:56Z published，不可变 tag 精确
  解析到 `2968eea4`，并等于已验证 master head；3 个 uploaded assets 已核验。
- full App `2023/2023`、Core `69/69`；window targeted `7/7`、sync suite
  `193/193`、export focused `133/133`。focused 与 full 有重叠，不重复求和。
- 完整 `TodayStatusWorkManager` 系统编排、真实 Status 导出按钮、二次
  auth/退避/不重发的完整系统旅程、旧 APK 实际升级、OEM/OPPO、强杀掉电、
  真实长课堂与 production 继续 `NOT-VERIFIED`。
- GitHub Release 是发布产物，不是 production 部署或物理设备验收；原始物理
  flash/crash 根因没有 stack/logcat，仍只是推断，未证明修复。
- `#505`、`#525`、`#533` 保持 OPEN；`#533` 为 partial，不自动关闭。

## 下一批优先顺序（计划，未执行）

以下仅排定顺序，不是已执行结果，不新增需求 ID 或 backlog：

1. `P0-1` 隔离执行旧 APK→当前 APK 升级，用合成旧队列覆盖 batch ID 连续性、
   owner、ack/retry、完整 Worker/Status 读回；不操作 production pending。
2. `P0-2` 为 core DB 诊断 section 实现无损分页/流式，并验证真实 Status
   导出按钮、取消与 IO failure；不得以截断 AC 9.3 数据掩盖内存风险。
3. `P1` 再验证物理 OPPO/OEM、100–180 分钟真实课堂与逐图 receipt，随后按
   既有路线推进 Windows/TASK/MIG；任何付费模型调用须另行授权。
