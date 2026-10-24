# 数据面板展览馆 — README

> 480 种数据可视化组合的“画廊”，用于挑选最适合 PIM 的图表并一键落地。

## 1. 结构

```
src/client-web/src/components/dashboard-exhibition/
  Exhibition.html          # 静态展览馆本体（双击可开，CDN echarts+leaflet）
  design-tokens.css        # 设计 Token（与 Exhibition.html <style> 同源）
  README.md                # 本文
src/client-web/public/dashboard-exhibition/
  Exhibition.html          # 静态页的 public 拷贝，构建后位于 wwwroot/dashboard-exhibition/Exhibition.html
src/client-web/src/components/charts/
  fakeData.ts              # 12 种数据类型的确定性假数据（与 Exhibition.html 同源，hash01）
  exhibitionOptions.ts     # 40 种图表的 option 工厂（供静态页与 React 组件复用）
  *.tsx                    # 21 个生产级 React 图表组件（EChartBox + 四态）
  __tests__/*.test.tsx     # 13 个单测（vitest + @testing-library/react）
src/client-web/src/pages/
  ExhibitionPage.tsx       # React 画廊页（/exhibition），20+ 卡片网格，与静态页共享 localStorage
```

## 2. 数据生成逻辑

### 2.1 确定性哈希

```ts
export function hash01(seed: number){ const x=Math.sin(seed*12.9898+78.233)*43758.5453; return x-Math.floor(x); }
```

所有假数据均用 `hash01` 而非 `MathRandom`，保证刷新一致、可分享链接复现。

### 2.2 12 种数据类型（`getFakeData(dtId)`）

| dtId | 类型 | 分布特征 | 关联 |
|------|------|----------|------|
| 1 | 日使用时长 | Top3占60%（微信3420s>VS Code2880s>抖音2160s），与分类联动 | 与 `dt3` 分类占比同源 |
| 2 | 周趋势 | 4周 W1 1380→W4 1680，周一/五双峰，周末-20% | 与 `dt1` 同源，byCategory 拆分 |
| 3 | 分类占比 | 8类和100，聊天27.5+视频21.3占半壁 | 与 `dt1` 一致 |
| 4 | 24h热力 | 8-12/19-23双峰，凌晨1-5<5%，4点缝隙 | 5分类×24小时矩阵 |
| 5 | GPS轨迹 | 3段 家→地铁→公司 连续，速度与段绑定 | 与 `dt6` 起终点一致 |
| 6 | 常去地点 | 家128 公司96 学校42 | 与 `dt5` 起终点一致 |
| 7 | 速度分布 | 三峰 4/20/300 km/h | 直方图 |
| 8 | PC应用时长 | VS Code320 Chrome280 占65%，AFK30单独 | 长尾 |
| 9 | 键盘热力 | QWERTY 真实频率 Space3410 Q85 Z45 | 3行矩阵 |
| 10 | 任务完成率 | 30天 68→85%，带7日均线 ma7 | 与日期联动 |
| 11 | 习惯打卡 | 5习惯×30天，早起72%运动45%，周末-10% | 与 `dt2` 周趋势相关 |
| 12 | 设备健康 | 2在线1离线1告警，健康92/88/43/58 | 与 lastSync 联动 |

### 2.3 图表工厂

`exhibitionOptions.ts` 导出 `build*Option(labels, values)` 40 个，静态页 `optVerticalBar..optHexbin` 与 React 组件同源。新增图表类型时：

1. 在 `exhibitionOptions.ts` 新增 `buildXxxOption` 并导出
2. 在 `Exhibition.html` 的 `CHART_TYPES` 数组追加一项（id 41...）
3. 在 `fakeData.ts` 若需新数据类型则追加 `genType13` 并在 `getFakeData` 加分支

## 3. 如何新增一种数据类型 / 图表类型

### 新增数据类型（例：新增 “专注时长”）

```ts
// fakeData.ts
export function genType13(){
  return [{label:"深度专注", value: 120}, {label:"浅度", value: 80}];
}
// getFakeData switch 加 case 13
// Exhibition.html DATA_TYPES 数组追加 {id:13, name:"专注时长", ...}
// React 组件新建 src/components/charts/FocusDurationBar.tsx 并引用 genType13
```

### 新增图表类型（例：新增 “雷达面积”）

```ts
// exhibitionOptions.ts
export function buildRadarAreaOption(labels, values){ ... }
// Exhibition.html CHART_TYPES 追加 {id:41, name:"雷达面积", type:"radar"}
// Exhibition.html buildOption switch 加 if(ctId===41) return optRadarArea(...)
// 新建 React 组件 FocusRadar.tsx 调用 buildRadarAreaOption
```

## 4. 如何从展览馆挑卡落地到 PIM 页面

1. 在静态页 `Exhibition.html`（或 React 画廊 `/exhibition`）筛选后，勾选心仪卡片右上角复选框，可打 1-5 星
2. 点击底部“导出选中 JSON”下载 `pim-exhibition-selected-*.json`，内容示例：

```json
{
  "exportedAt": "2026-08-26T03:40:00.000Z",
  "totalSelected": 3,
  "items": [{ "id":"3-10", "title":"分类占比 × 环形图", "file":"src/client-web/src/components/charts/MobileCategoryDonut.tsx" }]
}
```

3. 在 `src/client-web/src/components/charts/` 找到对应 `file` 的 React 组件（若无则新建，参考 `WeekTrendLine.tsx` 的 Props 模板）
4. 在目标页面（如 `TodayPage.tsx` 或 `ReportsPage.tsx`）`import` 该组件并传入真实 API 数据：

```tsx
import WeekTrendLine from '../components/charts/WeekTrendLine';
import { getMobileAnalyticsCharts } from '../../api/mobile';
// ...
const { data } = useQuery({ queryKey:['week-trend'], queryFn: getMobileAnalyticsCharts });
<WeekTrendLine data={data} height={180} onSelect={(d)=> console.log(d)} />
```

5. 若需静态页复用，直接 `import { buildWeekTrendOption } from '../components/charts/exhibitionOptions'` 并传 `EChartBox`

## 5. 设计系统

- Token：`design-tokens.css` 与 Exhibition.html `<style>` 的 `:root` 同源，`--pim-bg/surface/primary/border/radius/shadow/font` 全页只用变量
- 排版：标题 13px/600、描述 11px/400、标签 10px、栅格 gap 14px、圆角 12px
- 深色：`@media (prefers-color-scheme: dark)` 四色自动切换，echarts 在 JS 中 `echarts.init(el, isDark?'dark':undefined)` 并设 `axisLabel/splitLine` 为 `#94a3b8`
- 动效：卡片 `150ms ease-out`（`translateY(-2px)+shadow-lg`），图表 `animationDuration:400`
- 四态：每卡 `loading(Skeleton 120ms闪烁)` / `empty(插画+清空筛选)` / `error(重试)` / `ready(图表)`

## 6. 交互

- URL 状态：`location.hash` 同步 `dt/ct/mod/q/sort/page/sel`，刷新不丢，可分享；`?card=dt-ct` 直达
- 键盘：`/` 聚焦搜索、`j/k` 翻页、`Enter` 预览、`Esc` 关闭
- 对比：选中≥2时顶部悬浮“对比(2)”条，Modal 2-3张等高并排，支持导出对比图
- 复制链接：每卡右上角 🔗 拷贝带 `?card=` 的直达链接
- 埋点：`exhibition_ratings` / `exhibition_selected` / `exhibition_views` 三 key 本地存储
- 空态：无结果时插画+“清空筛选”按钮

## 7. 构建与测试

```bash
npm --prefix src/client-web run build    # tsc -b && vite build，0 Error，ExhibitionPage ~40kB
npm --prefix src/client-web run test     # vitest run，13 文件 38 用例
npm --prefix src/client-web run test:charts # 仅图表组件
dotnet build Pim.sln                      # 0 Error
```

包体积：`vite.config.ts` 已配 `manualChunks: { echarts, react, vendor }`，单包 gzip 已控。

## 8. 交付

- 静态页：`src/client-web/src/components/dashboard-exhibition/Exhibition.html`（2535行→~2800行）可直接双击
- React 画廊：`/exhibition`（21 组件，筛选/打分/选中/导出与静态页打通）
- 单测：`src/client-web/src/components/charts/__tests__/` 13 文件
- 文档：本文 + 单测 + 截图录屏（PR 附 5 图 1 录屏）
