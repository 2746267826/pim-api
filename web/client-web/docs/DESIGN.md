# PIM 设计系统（DESIGN.md）

> 视觉基调定稿：**浅色效率单主题 · 效率蓝 · 柔和描边风 · 紧凑密度 · 浅色同底侧边栏**。
> 本文档是视觉规范的唯一事实来源；颜色/圆角/阴影/字体的机器可读版本在 `src/styles/tokens.css`（Tailwind 4 `@theme`），组件**禁止写裸 hex**。
> 例外：服务端下发的颜色（日历本色、PC 分类色、Outlook 图层色）按 API 值直接渲染。

## 1. 设计原则

1. **数据墨水优先**：界面退后，数据向前。颜色只用于语义（健康/风险）与分类（日历本/生活分类），无装饰性用色。
2. **服务端色是事实源**：任务段 `#22C55E`、习惯 `#A855F7`、可用时间 `#0EA5E9`、AI 占位 `#F97316`、日程本默认 `#3B82F6` 由 API 下发；主题只提供中性底。
3. **三态成对**：每个数据模块必须同时设计 骨架屏 / 空态 / 错误态，缺一不验收。
4. **桌面紧凑、触屏宽松**：同组件两档密度，靠断点与 `pointer: coarse` 切换。

## 2. 色彩

### 2.1 中性色（Slate）

| Token | 值 | 用途 |
|---|---|---|
| `bg` | `#FFFFFF` | 页面底 |
| `surface` | `#F8FAFC` | 侧边栏底、表头底、表格 hover、卡片内分区 |
| `surface-2` | `#F1F5F9` | 分段控件容器、骨架屏、图标底 |
| `border` | `#E2E8F0` | 卡片/输入框主边框 |
| `border-strong` | `#CBD5E1` | hover 边框、开关未选态 |
| `divider` | `#EEF2F6` | 表格行分隔线（比 border 浅一档） |
| `text-1` | `#0F172A` | 主文字 |
| `text-2` | `#334155` | 次级文字 |
| `text-3` | `#64748B` | 辅助说明、轴标签、分组头 |
| `text-4` | `#94A3B8` | 占位符、禁用、空态图标 |
| `inverse` | `#0F172A` | Tooltip / Toast 反白底 |

### 2.2 主色（效率蓝）

```
primary        #2563EB   按钮、选中、链接
primary-hover  #1D4ED8   hover/active；soft 底上的文字
primary-soft   #EFF6FF   选中底（侧边栏选中项、chips 选中）
primary-ring   #BFDBFE   焦点环
primary-fg     #FFFFFF   主色上的文字
```

### 2.3 语义色三件套（实色 / soft 底 / 边框）

| 语义 | 实色 | soft | 边框 | 用途 |
|---|---|---|---|---|
| ok | `#16A34A` | `#F0FDF4` | `#BBF7D0` | 健康、成功、完成 |
| warn | `#D97706` | `#FFFBEB` | `#FDE68A` | 告警、截断提示、武装确认 |
| crit | `#DC2626` | `#FEF2F2` | `#FECACA` | 故障、危险操作、删除确认 |
| info | `#0EA5E9` | `#F0F9FF` | `#BAE6FD` | 同步中、进行中 |
| neutral | `#64748B` | `#F8FAFC` | `#E2E8F0` | 离线、未知 |

### 2.4 手机生活分类固定色

编程/折腾 `#2563EB` · 学习 `#8B5CF6` · 视频 `#EC4899` · 聊天 `#16A34A` · 文档 `#F59E0B` · 游戏 `#F97316` · 其他 `#64748B` · 工具/系统(噪音) `#94A3B8`（tokens：`--color-cat-*`）

### 2.5 图表与数据可视化

- **分类序列 10 色**：`#2563EB #16A34A #F59E0B #8B5CF6 #EC4899 #14B8A6 #F97316 #6366F1 #84CC16 #64748B`
- **热力 ramp 5 档**：空 `#F1F5F9` → `#DBEAFE` → `#93C5FD` → `#3B82F6` → `#1D4ED8`；图例五格 + 最大值数字
- **甘特时间条**：分类色 85% 透明度，hover 100%
- **ECharts 主题**：轴线 `#E2E8F0`、轴字 12px `text-3`、网格线 `#EEF2F6` 虚线 `[3,3]`、折线 2px 圆点 3px、柱顶圆角 3px、tooltip 白底圆角 8 阴影 overlay
- 无数据时图表区内嵌 120px"暂无数据"，不空白

### 2.6 状态灯

healthy `#16A34A` / warning `#D97706` / critical `#DC2626`（带呼吸光环 `.status-dot-critical`）/ unknown `#94A3B8`

## 3. 字体排印

- 字体栈：`Inter Variable` + `Noto Sans SC Variable`（@fontsource 本地打包）→ 系统回退；等宽 `JetBrains Mono Variable`（`.mono`，用于设备 ID/JSON/SQL/地址）
- 中文字重上限 **600**
- 所有数量字加 `.tnum`（tabular-nums）

| Token | 字号/行高 | 字重 | 用途 |
|---|---|---|---|
| display | 24/32 | 600 | 页头标题 |
| title | 16/24 | 600 | 卡片标题、抽屉标题 |
| body | 14/20 | 400 | 正文默认 |
| body-strong | 14/20 | 600 | 强调 |
| small | 13/18 | 400 | 表格、列表辅助 |
| caption | 12/16 | 400 | 徽标、时间戳、轴标签 |
| metric | 28/36 | 600 tnum | 指标卡大数 |

时间戳：同日 `HH:mm`；跨日 `MM-dd HH:mm`。

## 4. 间距 / 圆角 / 阴影

- 间距基数 4px，档位：4/8/12/16/20/24/32/40
- 页面留白：桌面 24 / 平板 20 / 手机 16；卡片 gap：桌面 16 / 手机 12
- 圆角：卡片 12（`rounded-card`）、弹窗 16（`rounded-modal`）、控件 8（`rounded-ctl`）、徽标 6（`rounded-badge`）、chips 999、热力格 3
- 阴影（描边为主）：
  - `shadow-card` `0 1px 2px rgb(15 23 42/.05)`
  - `shadow-overlay` `0 4px 16px rgb(15 23 42/.08)`（popover/下拉）
  - `shadow-modal` `0 12px 40px rgb(15 23 42/.16)`（弹窗/抽屉/FAB）

## 5. 断点与外壳

| 断点 | 外壳行为 |
|---|---|
| `<640` 手机 | 侧边栏→280px 抽屉；顶部条 56px（汉堡+logo）；宽表格→卡片列表；文件三栏→树抽屉+全屏预览；编辑面板→全屏抽屉；显示 FAB |
| `640–1279` 平板 | 64px 图标栏（hover 显示 title） |
| `≥1280` 桌面 | 232px 常驻侧边栏（bg surface + 1px 右边框） |

侧边栏导航行：高 36px、圆角 8、图标 20px stroke 1.75；选中 `bg-primary-soft` + `text-primary-hover` + 左缘 3px 主色指示条。分组头 12px `text-3`。

## 6. 组件规范（已实现于 components/ui）

| 组件 | 关键尺寸/行为 |
|---|---|
| Button | 高 36（sm 32 / lg 40）；变体 primary/secondary/ghost/danger/danger-soft |
| Input | 高 36、圆角 8；focus 边框转主色（不放大） |
| Segmented | 高 28 胶囊、容器 `surface-2` padding 3px；选中白底+卡片阴影+主色字 |
| Chip | 高 28 全圆；选中 `primary-soft` 底 + 主色边 |
| StatusBadge | 高 22、6px 圆点 + 12px 字、soft 底 |
| MetricCard | padding 16、约 92px 高、26px tnum 大数 |
| Card | 圆角 12 + 1px 边 + shadow-card |
| Drawer | 桌面右抽屉 420px / 左抽屉 280px（移动导航）；240ms 滑入；右上关闭钮 |
| Dialog | 居中弹窗、max-w 560、圆角 16 |
| ConfirmDialog | 三档：普通 / 影响预览（summary+样例≤5）/ 输入确认；danger 变体 |
| TwoStepButton | 首击变 danger 武装态，10s 自动复位 |
| EmptyState | 图标 24 text-4 + 标题 14 + 说明 13 + 可选操作；py-12 |
| Skeleton | `surface-2` 圆角 8，1.5s 呼吸 |
| InlineAlert | 语义三件套横幅（操作结果） |
| ErrorBoundary | page / card 两级 |
| Toast(sonner) | inverse 底白字圆角 10；key 去重 3s；5xx 静音规则见 notify.ts |
| FAB | 56px 主色圆钮右下 16px；展开三项菜单（写闪念/建任务/排日程）；/quick-notes 与内嵌页隐藏 |

## 7. 动效

| 场景 | 时长 |
|---|---|
| hover/active | 120–150ms ease-out |
| 淡入 | 200ms（`pim-fade-in`） |
| 抽屉滑入 | 240ms `cubic-bezier(.32,.72,.24,1)`（`pim-slide-in-*`） |
| 弹窗/菜单弹出 | 200/150ms `pim-pop-in` |
| 骨架呼吸 | 1.5s 循环 |

`prefers-reduced-motion: reduce` 时全部禁用（base.css 全局兜底）。拖拽（日历拖选、任务拖放）用原生光标反馈，无自定义动画。

## 8. 图标

lucide-react：行内 16px、控件内 20px、空态 24px，strokeWidth 1.75，颜色随文字。
