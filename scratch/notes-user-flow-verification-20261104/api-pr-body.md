## 技术修改 / Technical changes

新增按账号隔离的笔记发现、当前版本读取和材料保全克隆接口。续写复用已校验的原材料，最终保存仍检查当前版本基线并保留并发冲突。契约由 Test 宿主的真实 Swagger HTTP 响应导出，Web 类型通过官方脚本生成。

Add owner-scoped note discovery, current-version reads, and verified material cloning. Finalization checks the version baseline and retains concurrent edits. Export the contract from the Test host's Swagger HTTP response and regenerate Web types.

此草稿的远端初始提交包含后端及契约；Web 后续本地改动不属于已知远端 head，页面与处理流程验收完成后再更新正文和验证结果。

The initial remote commit contains the backend and contract. Later Web work is local and is not part of the known remote head; update this description after the integrated flow is verified.

## 功能变化 / Feature changes

为正常笔记列表发现云端笔记，以及修改正文时保全已有录音、照片和手写材料提供服务端能力。本提交本身尚不构成完整跨端同步。

Provide the server support needed to discover synced notes and retain existing audio, photos, and ink when continuing a note. This commit alone does not complete cross-device sync.

## 如何体验 / How to try it

本 PR 暂为草稿，等待 Web 用户流程接入后补充完整操作说明。最终体验目标是：从笔记列表打开已有笔记，继续编辑并保存；含录音的笔记先转写，纯文字笔记可直接整理；高级设置默认收起，处理目标、材料范围及可能费用在确认前直接可见。

This draft awaits the Web integration. The intended flow is to open a note from the library, continue writing and save, transcribe audio when present, and organize text directly when audio is absent. Advanced options start collapsed, while destination, material scope, and possible charges remain visible before confirmation.

## 测试 / Tests

- Materials store and HTTP endpoint tests: 26 passed, 0 failed, 0 skipped.
- Test-host `GET /swagger/v1/swagger.json`: HTTP 200; contract normalized and exported.
- Official `web/scripts/ci/update-contract.sh` and `check-contract.sh`: passed.
- Targeted Notes + Contract regression: 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout. The 108 skipped tests are not PASS.
- Final Web tests/build and browser scenarios: NOT-VERIFIED. No production, paid provider, or physical-device acceptance is claimed.

## 需求与验收映射 / Requirement & Acceptance Mapping

| Requirement / 需求 | Acceptance / 验收标准 | Evidence / 证据 | Result / 结果 |
| --- | --- | --- | --- |
| Discover saved notes / 发现已保存笔记 | Owner-scoped current-note listing and reads / 按账号列出并读取当前笔记 | Material HTTP/store tests / 材料 HTTP 与存储测试 | PASS |
| Preserve source materials / 保全原材料 | Continue with validated original attachments and conflict protection / 续写保全已校验附件并保留冲突 | Material clone/store tests / 克隆与存储测试 | PASS |
| Use real contract / 使用真实契约 | Test Swagger export and generated Web types match / 真实契约与 Web 生成物一致 | Official generation/check scripts / 官方生成和检查脚本 | PASS |
| Follow the user's note workflow / 按使用顺序组织界面 | Library, editing, saving, and processing form a clear flow / 笔记库、编辑、保存及处理衔接清楚 | Web integration and final browser check / Web 集成及最终浏览器检查 | NOT-VERIFIED |
| Normal cross-device continuation / 正常跨端续写 | Discover, restore, continue, and preserve materials without manual file exchange / 不手动搬文件完成发现、还原、续写与材料保全 | Client integration and device acceptance pending / 客户端及设备验收待完成 | NOT-VERIFIED |
