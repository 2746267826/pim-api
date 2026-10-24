### 目标
落实视觉评估中选定的三项：空态样式统一、今日页网格节奏（消除孤行）、三处顺手小修（xs 徽章/小标题锚点/脉搏空轨道底纹）。纯 UI 改动，一笔提交。

### 改动一：空态样式统一（17 处）
1. `src/components/ui/empty-state.tsx`：`icon` 未传时默认 `Inbox` 图标——全部既有 EmptyState 自动统一。
2. 16 处卡片级裸文字空态改为 `<EmptyState size="sm" title="…" />`（去旧 py 类）：
   - today-page.tsx:234（分区卡，重点）
   - pc-tracker-page.tsx:316/340/435、status-page.tsx:85/126、device-detail-page.tsx:91、microsoft-page.tsx:358、endpoint-shell-page.tsx:67、reports-page.tsx:198、category-timeline.tsx:355、workbench-page.tsx:214/241、file-dialogs.tsx:240、location-history-page.tsx:313
3. 嵌入式小空态（会话明细/无异常等 6 处）保持一行小字不动。

### 改动二：今日页网格节奏
- today-page.tsx：`calendar.schedule`/`calendar.tasks` 两张列表型卡 lg 下 `col-span-2` 全宽（StaggerItem className 传递），行动区网格加 `grid-flow-dense` 自动填缝；「数据」区保持半宽。

### 改动三：顺手小修
1. `status-badge.tsx` 增加 `size?: 'sm'|'xs'`（xs：h-18px/px-1.5/text-10px/色点缩小）；应用于：任务列表行、数据可信度尺子行、回收站表格行、PC 标注队列行。
2. 章节小标题锚点：今日页「行动/数据/其它」h2 与数据可信度分组卡标题加左侧 3px 主色竖条（局部实现，不动共享 CardTitle）。
3. `today-pulse.tsx`：空轨道铺 45° 低透明斜纹；三轨全空时轨道下加一行"区间内暂无日程 / 任务段 / 习惯投射"。

### 验证与交付
- tsc + vitest 146 例全绿
- 浏览器截图验收：今日页、数据可信度页、PC 记录页
- 一笔提交（约 12 个文件）