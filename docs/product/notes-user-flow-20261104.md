# 笔记主流程推进记录（2026-11-04）

## 本轮目标

把日常笔记路径收敛为：从笔记库发现并打开笔记，编辑正文并保存；有音频时先转写再整理，无音频时直接整理正文及支持的图片材料。高级配置默认折叠，但发送目标、材料范围、可能费用和授权信息在确认前常驻可见。

本轮包括 API/Web 与 Android 的开发；Windows 未实施。以下只记录当前可复核进度，不把局部测试、PR 创建或 CI 状态当作跨端、生产或发布验收。

## 接受标准

- 用户能从笔记库找到笔记，进入适合日常使用的正文阅读/编辑体验；修改可保存，保存与同步状态清楚。
- 有音频可先转写再整理；无音频不被转写阻塞，可直接整理正文及支持的材料。
- 日常同步自动发现并还原笔记与已支持材料，不要求手动导入、导出或搬文件；正常同步不需要 Android 包，各客户端负责适配共同云端契约，包导入仅用于离线交换、备份恢复或旧格式兼容。
- 高级配置默认折叠；目标、范围、费用和授权在发送前可见，不自动使用未授权付费服务。
- 缺配置或处理暂不可用时不阻塞本机阅读、编辑和保存；不覆盖人工修改。

## 当前进度

- **API/契约**：材料定向测试 26/26；Test Swagger HTTP 200；官方 Web `update-contract` 与 `check-contract` 通过。有界 Notes+Contract 回归为 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout；108 skipped 不计 PASS。该回归只证明对应锁定源码范围。
- **Web**：当前提交链为 `78c4a17a`、`d229185b`、`f5037e08`、`e6f5ea82`、`d0b796f1`。四个 native-notes 聚焦测试文件 52/52 通过；`npm run build` PASS；lint 仅保留既有 warnings。材料保全已补测。Playwright 因 Chromium executable 缺失而 FAIL，未进入页面、未生成截图，浏览器验收 NOT-VERIFIED。
- **Android**：Draft PR [#70](https://github.com/2746267826/pim-android/pull/70)，当前 head `3f6b1d4`。Android Studio JDK、已安装 Android SDK、Gradle 8.14 环境下 `:app:testDebugUnitTest --no-daemon --stacktrace` 完整重跑通过：193 XML / 2256 tests，0 failures、0 errors、0 skipped；`:app:compileDebugKotlin` 通过，契约生成物一致。此前曾汇总 14 项失败，当时没有 XML；精确 head 重跑未复现，旧失败原因未知。ADB 可读取设备列表但没有连接设备，因此 connected UI 仍 NOT-VERIFIED。正常跨端发现、完整材料恢复、续写与冲突处理仍未整体验收。
- **ASR 配置与可用性**：管理员在 Web「设置 → AI 与 ASR → ASR 配置」管理凭据；普通用户不显示凭据配置。阅读、编辑和写作不依赖 ASR 配置，缺配置不应阻塞日常笔记。
- **路线图与 issues**：M0–M5、#516、#539、#551–554、Android #59–63 均未整体完成；issues 保持开放，不因本地实现或局部测试关闭。
- **Windows**：本轮未实施，不声称已有同步、PR、CI 或发布。
- **#516 MiMo Batch**：已有 Batch 工程实现切片与测试，不代表 Phase 2 用户闭环已完成。真实音频 Batch 能力、转写质量、费用、region/TTL 和生产运行仍未验证；音频 Batch 不应在验证前向用户开放。

## 用户仍不能做什么

当前不能承诺用户在 Web/Android 间正常发现同一笔记后，完整还原全部材料、在支持范围内续写保存并重开仍保全材料；也不能承诺完整 Web 交互或最终 Android 组合已验收。没有生产部署、真实 provider、实机/OEM 或端到端验收证据。

## 下一步

1. 补齐真实 Chromium 环境后重跑 Playwright 桌面与窄屏用户流程；当前浏览器验收 NOT-VERIFIED。
2. 继续 Android 验证发现—下载—材料还原—续写/冲突流程，覆盖原件路径/hash/大小/预算，并补跑 connected UI；当前不得声称完整同步。
3. 按用户顺序闭合跨端链路：从笔记列表自动找到 → 正文与材料可读 → 续写保存且状态清楚 → 有音频先转写、无音频直接整理 → 返回后仍保留人工修改和原件。设备/OEM 及生产/provider 结论单列 NOT-VERIFIED。
4. 438/330/108/0/0 是已完成的有界回归，108 skipped 不计 PASS；只在代码范围改变或发现具体缺口时重跑受影响范围。

以上局部通过项不改变 M0–M5 未整体完成的状态；不批量关闭 issue，也不提前标记全部 PASS、已发布或可生产使用。
