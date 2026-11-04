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

- **API/契约**：后端发现列表、current 读取及材料保全克隆定向测试 26/26 通过。Test Swagger 实际导出 HTTP 200，并经官方 Web `update-contract` / `check-contract` 校验。提交 `faa6ab30` 是草稿 [PR #561](https://github.com/2746267826/pim-api/pull/561) 的初始远端 head；之后的本地提交和工作树更改未证明已经推送，不能写成当前 PR 最新 head。
- **API CI 边界**：此前旧 head 的 Web build SUCCESS 不适用于后续代码。基于当前可核对材料，PR #561 最新 head 与对应 CI 状态为 NOT-VERIFIED；不得把历史运行结果继承给未推送代码。
- **笔记处理**：AI 笔记处理/ASR 顺序、上下文与返回、默认折叠、目标/范围/费用常驻的 focused tests 41/41 通过。父级本地提交 `78c4a17a` 尚未 push，尚无对应远端 CI。
- **Web**：AI 处理页 focused tests 41/41 通过；页面/模型和材料往返仍有本地改动。只读审查发现正文块未知扩展属性丢失风险，以及 clone 未知结果恢复可能未校验 `baseVersionId`；这两项修复测试及最终组合验证均 NOT-VERIFIED。完整 Web tests、lint、build 和浏览器桌面/窄屏验收尚未完成。
- **Android**：基线 autosubmit 5/5 与 compile 通过，但发生在云端 DTO/恢复接入前。真实契约 fetch/生成/generated check 通过；freshness NOT-VERIFIED。Android worktree 后续有发现、下载、恢复相关未提交 WIP；组合 compile、单测、正常笔记库自动还原与续写仍 NOT-VERIFIED，不能称完整同步。
- **广域回归**：有界 Notes+Contract 回归 TRX 为 438 total / 330 passed / 108 skipped / 0 failed / 0 timeout，正常完成，耗时 6.34 分钟。108 项因外部 PostgreSQL 等条件未启用，计为 skipped 而非 PASS。此前“更广回归仍 pending”和“Kant 正进行诊断”均为过时状态；该回归已有完成结果，不应继续写成进行中。此结果仅适用于当时锁定源码，不代表后续 PR、Web 或 Android 组合通过。
- **Windows**：本轮未实施，不声称已有同步、PR、CI 或发布。

## 用户仍不能做什么

当前不能承诺用户在 Web/Android 间正常发现同一笔记后，完整还原全部材料、在支持范围内续写保存并重开仍保全材料；也不能承诺完整 Web 交互或最终 Android 组合已验收。没有生产部署、真实 provider、实机/OEM 或端到端验收证据。

## 下一步

1. 先修复并加测 Web 未知正文扩展与 clone 基线恢复风险，再对最终 Web 源码运行 focused/full tests、lint、build 和桌面/窄屏浏览器流程；同步核实并更新 PR #561 实际 head/CI。
2. 在 Android 云端 DTO 接入后验证 compile、契约 freshness、发现—下载—材料还原—续写/冲突流程，覆盖原件路径/hash/大小/预算，随后才形成 Android PR。
3. 按用户顺序闭合跨端链路：从笔记列表自动找到 → 正文与材料可读 → 续写保存且状态清楚 → 有音频先转写、无音频直接整理 → 返回后仍保留人工修改和原件。设备/OEM 及生产/provider 结论单列 NOT-VERIFIED。
4. 438/330/108/0/0 的既有回归结果无需标记 pending；只有代码范围改变或发现具体缺口时，才对受影响范围重跑并追加精确结果。

以上局部通过项不改变 M0–M5 未整体完成的状态；不批量关闭 issue，也不提前标记全部 PASS、已发布或可生产使用。
