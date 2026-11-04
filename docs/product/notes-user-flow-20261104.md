# 笔记主流程推进记录（2026-11-04）

## 本轮目标

把日常笔记路径收敛为：从笔记库发现并打开笔记，编辑正文并保存；有音频时先转写再整理，无音频时直接整理正文及支持的图片材料。高级配置默认折叠，但发送目标、材料范围、可能费用和授权信息在确认前常驻可见。

本轮包括 API/Web 与 Android 的开发；Windows 未实施。以下只记录当前可复核进度，不把局部测试、PR 创建或 CI 状态当作跨端、生产或发布验收。

## 接受标准

- 用户能从笔记库找到笔记，进入适合日常使用的正文阅读/编辑体验；修改可保存，保存与同步状态清楚。
- 有音频可先转写再整理；无音频不被转写阻塞，可直接整理正文及支持的材料。
- 日常同步自动发现并还原笔记与已支持材料，不要求手动导入、导出或搬文件。
- 高级配置默认折叠；目标、范围、费用和授权在发送前可见，不自动使用未授权付费服务。
- 缺配置或处理暂不可用时不阻塞本机阅读、编辑和保存；不覆盖人工修改。

## 当前进度

- **API/契约**：后端发现列表、current 读取及材料保全克隆定向测试 26/26 通过。Test Swagger 实际导出 HTTP 200，并经官方 Web `update-contract` / `check-contract` 校验。核心提交 `faa6ab30` 已通过正常 GitHub Git ref API 建立远端分支并创建草稿 [PR #561](https://github.com/2746267826/pim-api/pull/561)，当前 head 为 `faa6ab30`。此前两次 Git push 的服务端 500 不改变当前已有 PR 的事实。
- **API CI 边界**：`faa6ab30` 旧 head 的 web-build 已 SUCCESS，backend/docker 仍在运行。该结果不继承到父级尚未推送的新源码；新 head 仍需完整 CI。
- **笔记处理**：AI 笔记处理/ASR 顺序、上下文与返回、默认折叠、目标/范围/费用常驻的 focused tests 41/41 通过。父级本地提交 `78c4a17a` 尚未 push，尚无对应远端 CI。
- **Web**：native-notes 前任开发已结束，工作拆为 Mendel（模型保全）与 Godel（页面测试）两个代理继续收口；最终组合尚未验证。完整 Web tests、lint、build 和最终浏览器验收均未完成，当前不能称用户流程验收通过。
- **Android**：autosubmit 5/5 与 compile 通过，但发生在新 DTO 接入前，不能直接证明最终组合源码。真实契约已按 pinned `faa6ab30` 官方 fetch，生成及 generated check 通过；freshness 命令审批审查超时，状态为 NOT-VERIFIED，不是自动审核拒绝。稳定提交 `d840094` 已正常 push，尚无 Android PR。Confucius 正在该树开发发现、材料还原及普通笔记库更多折叠，暂不能称完整同步。
- **广域回归**：父级独立解析有界 Notes+Contract 回归 TRX，438 total / 330 passed / 108 skipped / 0 failed / 0 timeout，正常完成，耗时 6.34 分钟。108 项因外部 PostgreSQL 等条件未启用，计为 skipped 而非 PASS。此前无输出中断只是历史状态；此次有完整 TRX 和正常完成结果。Kant 正执行一次有界后端诊断，结果待补。
- **Windows**：本轮未实施，不声称已有同步、PR、CI 或发布。

## 用户仍不能做什么

当前不能承诺用户在 Web/Android 间正常发现同一笔记后，完整还原全部材料、在支持范围内续写保存并重开仍保全材料；也不能承诺完整 Web 交互或最终 Android 组合已验收。没有生产部署、真实 provider、实机/OEM 或端到端验收证据。

## 下一步

1. Mendel/Godel 收敛最终 Web 模型保全与页面测试；父级按最终 Web/契约与 Android 组合源码运行完整测试、lint、build、浏览器和 CI，并更新 PR 正文与截图。
2. 补齐跨端发现、材料下载还原、支持范围内续写及冲突保全的可复现证据。
3. 将 Android 新 DTO 接入后的 compile/流程与 freshness 重新验明；有稳定组合后再创建 Android PR。
4. 完成 Kant 的有界诊断并根据结果决定是否重跑受影响的 Notes+Contract 范围。

以上局部通过项不改变 M0–M5 未整体完成的状态；不批量关闭 issue，也不提前标记全部 PASS、已发布或可生产使用。
