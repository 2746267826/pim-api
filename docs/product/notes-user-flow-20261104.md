# 笔记主流程推进记录（2026-11-04；11/5收口）

本记录保留该批次的本地 UI 验证。后续契约集成、Android 接入与原始材料兼容修复以 [11/5 续篇](notes-continuation-20261105.md) 为准。

2026-11-05 用户改了上传与整理规则：结束后自动上传、成功后以云端为准；整理直接用原始录音并自动生成标题；转写只用于逐段查看。下文“有音频先转写再整理”是本批当时的口径，现行规则见[总路线图](native-notes-roadmap.md)第 9 节 2026-11-05 条。

更新时间：2026-11-05（11/5收口）。

## 本轮目标

把日常笔记路径收敛为：从笔记库发现并打开笔记，编辑正文并保存；有音频时先转写再整理，无音频时直接整理正文及支持的图片材料。高级配置默认折叠，但发送目标、材料范围、可能费用和授权信息在确认前常驻可见。

本轮包括 API/Web 与 Android 的开发；Windows 未实施。以下只记录当前可复核进度，不把局部测试、PR 创建或 CI 状态当作跨端、生产或发布验收。当前 Web worktree branch `codex-win/notes-user-flow-20261104` HEAD `302210428b471e5add970988d2b7f32c10ab98c4`；页面/测试 UX 改动尚未提交，本轮不提交。

## 接受标准

- 用户能从笔记库找到笔记，进入适合日常使用的正文阅读/编辑体验；修改可保存，保存与同步状态清楚。
- 有音频可先转写再整理；无音频不被转写阻塞，可直接整理正文及支持的材料。
- 日常云端路径不要求手动导入包：Android 初始化自动刷新云端列表，用户点击同一篇后才下载、校验并安装本机副本。当前同 `noteId` 已有本机副本时会拒绝覆盖，因此不是完整自动双向同步/自动合并。在线版本通过共同 cloud-material API contract（`inputJson`、`manifestJson`、`files` 与生成 DTO）交换，各端本地数据库/编辑器可不同。
- 高级配置默认折叠；目标、范围、费用和授权在发送前可见，不自动使用未授权付费服务。
- 缺配置或处理暂不可用时不阻塞本机阅读、编辑和保存；不覆盖人工修改。

## 当前进度

- **API/契约**：材料定向测试 26/26；Test Swagger HTTP 200；官方 Web `update-contract` 与 `check-contract` 通过。有界 Notes+Contract 回归为 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout；108 skipped 不计 PASS。该回归只证明对应锁定源码范围。
- **Web**：当前未提交 UX 改动让处理页按零录音/进行中/失败/成功状态自适应提示，并修复任务列表与生成表单重复选择；笔记库将旧记录/离线包移至默认折叠“更多”，明确云端列表无需导入。focused 两文件共 `49/49`（derived `23/23`、native `26/26`），真实 5 秒轮询状态转移及 A→B→A 授权复位回归通过；`npm run build`、四份源码/测试的局部 `npm run lint --` 通过，保留 2 条既有 React warning，`git diff --check` 与 Node check 通过。ASR 已固定为 5 秒轮询，`pending`/`error`/`active` 不启动重复任务，成功默认复用，重新转写默认折叠，主操作按钮为“生成完整记录与分析”；切换转写结果时，即使从 A 切回原结果 A，也必须重新确认，生成目标与持久提交保留。浏览器实际运行通过：1440x1000 与 390x844 两视口均通过，每视口 11 个状态无横向溢出，`unmatched`、`external`、`pageErrors`、`consoleErrors` 均为空；覆盖库新建/搜索、正文阅读编辑、显式保存、冲突候选只读及明确选用、录音/照片/手写引用保全、ASR 无重复启动与成功复用、重新转写折叠、返回重进、零录音整理入口及旧记录/导入折叠。新 runner 使用实际 `npm run build` 的 dist，并由 Playwright `route.fulfill` 提供本地静态资源；API 为合成 mock，未访问真实服务、Provider、生产或设备。报告仅使用合成 mock 和离线构建产物，不扩大为真实服务覆盖。主源码 SHA256 为 `BD5CE53A065D898A6F673CB2F1DEBDE3768425AE917F4AE987EBF6535BEBD045`。证据：[最终验证 receipt](../../scratch/qa/notes-user-flow/validation-20261105.json)、[最终浏览器报告](../../scratch/qa/notes-user-flow/offline-20261105/run-2026-11-05T03-33-28-452Z/report.json)；前 Vite 网络阻断仅保留为历史记录。
- **Android**：当前 worktree HEAD `dfa9558df11c0996eec5e98da75ce86acb948b99`。历史 2256 tests 仅对应父 `3f6b1d4`，不是当前 HEAD 验证。API #561 freshness 依赖仍在；须待契约进入 API master 后执行官方 fetch/generate/freshness 与精确 CI。发现、下载、材料还原、续写和冲突处理仍未整体验收。
- **ASR 配置与可用性**：管理员在 Web「设置 → AI 与 ASR → ASR 配置」管理凭据；普通用户不显示凭据配置。阅读、编辑和写作不依赖 ASR 配置，缺配置不应阻塞日常笔记。
- **路线图与 issues**：M0–M5、#516、#539、#551–554、Android #59–63 均未整体完成；issues 保持开放，不因本地实现或局部测试关闭。
- **Windows**：本轮未实施，不声称已有同步、PR、CI 或发布。
- **#516 MiMo Batch**：已有 Batch 工程实现切片与测试，不代表 Phase 2 用户闭环已完成。真实音频 Batch 能力、转写质量、费用、region/TTL 和生产运行仍未验证；音频 Batch 不应在验证前向用户开放。

schema-v1 只确认 Markdown/照片/手写基础材料有共同理解，不能称离线 ZIP 全通用：Android 完整包可含录音，Web 导入拒绝 `recordings/`、导出不含录音；跨端 converter 与录音互导未交付。离线文件用于支持范围内交换/备份/旧格式迁移，格式不通用不妨碍在线云端契约同步。

API #561 契约尚未进入 API master，Issue/PR 远端状态不刷新。正确顺序为：先核实并收敛 API #561 上游契约与 PR 重叠 → API #561 契约经授权审查/CI并进入 master → Android 官方 fetch/generate/freshness 与精确 CI → 跨端材料往返。M0–M5 均未整体完成；此处不刷新 issues 状态，也不批量关闭。

## 用户仍不能做什么

当前不能承诺用户在 Web/Android 间正常发现同一笔记后，完整还原全部材料、在支持范围内续写保存并重开仍保全材料；也不能承诺完整 Web 交互或最终 Android 组合已验收。没有生产部署、真实 provider、实机/OEM 或端到端验收证据。

## 下一步

1. 核实并收敛 API #561 上游契约与 PR 重叠。
2. API #561 契约经授权进入 master 后，执行 Android 官方 fetch/generate/freshness 与精确 CI；历史 2256 tests 不得沿用为当前 HEAD 结果。
3. 再做跨端材料往返；生产、真实 Provider/费用和实体设备/OEM 测试须另行授权。

以上局部通过项不改变 M0–M5 未整体完成的状态；不批量关闭 issue，也不提前标记全部 PASS、已发布或可生产使用。
