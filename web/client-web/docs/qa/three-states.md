# 三态复查（P5-B3）

> 方法：Playwright 路由拦截确定性制造三态——loading（API 延迟 2s）、
> error（500 页面级失败，放行 /auth/me 保持登录态）、empty（注入空数据 / 真实空数据日）。
> 代表页 × 3 态 = 15 张截图：`docs/qa/screenshots/state-*.png`；
> 脚本 `scripts/qa-three-states.mjs` 可重复执行。

## 代表页结果（2026-10-05 实测）

| 页面 | loading | error | empty |
| --- | --- | --- | --- |
| 今日 | ✓ 骨架栅格 | ✓ InlineAlert crit（页内已有 isError 分支） | ✓ 真实空数据日（假期）各分区"暂无内容" |
| 任务 | ✓ 行骨架 | ✓ **修复后**：EmptyState「任务加载失败」+ 原因 + 重试按钮（此前误用空态） | ✓ 搜索无结果空态 |
| 文件 | ✓ 骨架 | ✓ 空态 + 全局错误 toast（同任务页模式，可后续补页内错误分支） | ✓ 目录空列表 |
| PC 记录 | ✓ 骨架 | ✓ 瓦片归零 + 空态文案 | ✓ 真实空数据日（瓦片 0 + 各卡"暂无"） |
| 日历（周） | ✓ 网格骨架 | ✓ 空网格 + toast | ✓ 无日程周（真实） |

## 复查中修复的两个真问题

1. **auth 引导 5xx 误踢登录**：`/auth/me` 遇到非 401 错误（网络抖动、瞬时 500）时，
   `AuthProvider` 原来一律 `setUser(null)` → RequireAuth 把已登录用户踢回登录页。
   现区分：401 → 未登录（client 已清令牌）；其他 → `bootError`，
   守卫渲染「会话恢复失败 + 重试」错误页，令牌保留。
2. **任务页错误态冒充空态**：查询失败时列表渲染"没有符合条件的任务"，
   用户会误以为没有任务。现增加 `isError` 分支：错误原因 + 重试按钮。

## 全局错误语义（横向）

- **401**：client 清令牌 + 广播 → RequireAuth 跳登录（回跳原 URL）。
- **429**：按 Retry-After（≤5s 封顶）等待后重试一次（本阶段补实现 + 单测 2 例）。
- **409/412**：Outlook 写回白名单语义，`allowStatuses` 正常解包（单测覆盖）。
- **懒加载 chunk 失败**：ErrorBoundary（page 级）捕获渲染错误。
- **全局错误 toast**：QueryCache/MutationCache onError → sonner 去重。
