## 技术修改 / Technical changes

新增按账号隔离的笔记发现、当前版本读取和材料保全克隆接口。续写复用已校验的原材料，最终保存仍检查当前版本基线并保留并发冲突。契约由 Test 宿主的真实 Swagger HTTP 响应导出，Web 类型通过官方脚本生成。

Add owner-scoped note discovery, current-version reads, and verified material cloning. Finalization checks the version baseline and retains concurrent edits. Export the contract from the Test host's Swagger HTTP response and regenerate Web types.

本地 Web 交付提交链为 `78c4a17a`、`d229185b`、`f5037e08`、`e6f5ea82`、`d0b796f1`。本 PR 的远端最新 head 与 CI 状态须以 GitHub 实时记录为准；本文不以本地提交链推断远端 CI 已通过。Normal cross-device sync does not require Android package import: clients adapt the shared cloud contract; package import is only for offline exchange, backup/restore, or legacy-format compatibility. / 正常跨端同步不需要 Android 包导入：各客户端适配共同云端契约；包导入仅用于离线交换、备份恢复或旧格式兼容。

MiMo Batch has an engineering implementation slice, but this PR does not complete the #516 Phase 2 user workflow. Real audio Batch capability, transcription quality, cost, region/TTL behavior, and production execution remain NOT-VERIFIED. / MiMo Batch 已有工程实现切片，但本 PR 不代表 #516 阶段 2 用户闭环完成。真实音频 Batch 能力、转写质量、费用、区域/TTL 行为与生产运行仍为 NOT-VERIFIED。

The local Web delivery chain is `78c4a17a`, `d229185b`, `f5037e08`, `e6f5ea82`, and `d0b796f1`. Verify the actual PR head and CI in GitHub; this local chain does not imply remote CI success.

## 功能变化 / Feature changes

为正常笔记列表发现云端笔记，以及修改正文时保全已有录音、照片和手写材料提供服务端能力。本提交本身尚不构成完整跨端同步。

Provide the server support needed to discover synced notes and retain existing audio, photos, and ink when continuing a note. This commit alone does not complete cross-device sync.

## 如何体验 / How to try it

从笔记列表打开已有笔记，继续编辑并保存；含录音的笔记先转写，纯文字笔记可直接整理；高级设置默认收起，处理目标、材料范围及可能费用在确认前直接可见。管理员在「设置 → AI 与 ASR → ASR 配置」管理凭据；普通用户不显示凭据配置，阅读写作无需配置。

Open a note from the library, continue writing and save, transcribe audio when present, and organize text directly when audio is absent. Advanced options start collapsed, while destination, material scope, and possible charges remain visible before confirmation. Administrators manage credentials under Settings → AI & ASR → ASR Configuration; ordinary users do not see credential settings, and reading/writing does not depend on ASR setup.

## 测试 / Tests

- Materials store and HTTP endpoint tests: 26 passed, 0 failed, 0 skipped.
- Test-host `GET /swagger/v1/swagger.json`: HTTP 200; contract normalized and exported.
- Official `web/scripts/ci/update-contract.sh` and `check-contract.sh`: passed.
- Targeted Notes + Contract regression: 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout. The 108 skipped tests are not PASS.
- Web local commit chain: `78c4a17a`, `d229185b`, `f5037e08`, `e6f5ea82`, `d0b796f1`; four native-notes focused files: 52/52 passed; `npm run build`: PASS; lint: existing warnings only; material-preservation coverage added.
- Playwright: FAIL because the Chromium executable is missing; it did not enter the page and produced no screenshot. Browser acceptance: NOT-VERIFIED.
- Android Draft PR #70 / commit `3f6b1d4`: `:app:compileDebugKotlin` PASS and generated contract artifacts match. Of 2,256 JVM tests, 14 failed, but complete XML/logs are unavailable, so all 14 cannot be classified. The first test name is `NoteEditHistoryTest.oversizedDocumentDegradesHistoryWhileTheNoteStillSaves`; this is not enough to call it a pre-existing failure, and the other 13 cannot be precisely attributed. Connected UI is NOT-VERIFIED because ADB, Android SDK, and a verified dedicated device are unavailable. / Android Draft PR #70 / commit `3f6b1d4`：`:app:compileDebugKotlin` 通过且契约生成物一致。2256 项 JVM 测试中 14 项失败，但缺少完整 XML/日志，无法分类全部失败。首个用例名为 `NoteEditHistoryTest.oversizedDocumentDegradesHistoryWhileTheNoteStillSaves`，不能据此称为既有失败，其余 13 项也无法精确归因。Connected UI 因 ADB、Android SDK 与已核实专用设备均不可用而 NOT-VERIFIED。
- Normal cross-device discovery, complete restore, continuation, and conflict handling are not accepted end-to-end. M0–M5 and issues #516, #539, #551–554, Android #59–63 are not complete and remain open. No production, paid-provider, or physical-device acceptance is claimed.

## 需求与验收映射 / Requirement & Acceptance Mapping

| Requirement / 需求 | Acceptance / 验收标准 | Evidence / 证据 | Result / 结果 |
| --- | --- | --- | --- |
| Discover saved notes / 发现已保存笔记 | Owner-scoped current-note listing and reads / 按账号列出并读取当前笔记 | Material HTTP/store tests / 材料 HTTP 与存储测试 | PASS |
| Preserve source materials / 保全原材料 | Continue with validated original attachments and conflict protection / 续写保全已校验附件并保留冲突 | Material clone/store tests / 克隆与存储测试 | PASS |
| Use real contract / 使用真实契约 | Test Swagger export and generated Web types match / 真实契约与 Web 生成物一致 | Official generation/check scripts / 官方生成和检查脚本 | PASS |
| Follow the user's note workflow / 按使用顺序组织界面 | Library, editing, saving, and processing form a clear flow / 笔记库、编辑、保存及处理衔接清楚 | Web focused tests 52/52 and build PASS; Playwright did not enter page because Chromium is missing / Web 聚焦测试 52/52、build 通过；Playwright 因缺 Chromium 未进入页面 | NOT-VERIFIED |
| Normal cross-device continuation / 正常跨端续写 | Discover, restore, continue, and preserve materials without manual file exchange or package import / 不手动搬文件或导入笔记包完成发现、还原、续写与材料保全 | Android compile PASS; 14/2256 JVM failures not fully classified; connected UI blocked by unavailable ADB/SDK/verified device / Android 编译通过；14/2256 JVM 失败未能完整分类；ADB/SDK/已核实设备不可用，connected UI 未验证 | NOT-VERIFIED |
