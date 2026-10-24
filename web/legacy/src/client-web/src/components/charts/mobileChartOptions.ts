import type { EChartsOption } from '../../lib/echarts';
import { chartColors } from './chartColors';
import type { HeatmapMatrix, HeatmapMatrixCell } from '../mobile/mobileHeatmapMatrix';
import type { MobileAnalyticsChart, MobileTimelineBlock } from '../../api/mobile';

/**
 * 手机记录页 ECharts option 纯函数：输入数据、输出 EChartsOption，不依赖组件/页面。
 * 点击反查：heatmap 数据项携带 cell（含 sourceBuckets），timeline strip 数据项携带 blockId，
 * analytics chart 数据项携带 lifeCategory/packageName 原始值；组件在 onEvents click 中读取并回调。
 */

function formatDuration(seconds: number) {
  const safeSeconds = Math.max(0, Math.round(seconds));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  if (hours > 0) return minutes > 0 ? `${hours}小时${minutes}分钟` : `${hours}小时`;
  if (minutes > 0) return `${minutes}分钟`;
  return `${safeSeconds}秒`;
}

/**
 * 按图表单位格式化 tooltip 数值：count → 「X 次」；seconds/缺省 → 分钟/秒时长（与 formatDuration 语义一致）。
 */
export function formatChartValue(value: number, unit?: string): string {
  if (unit === 'count') return `${Math.round(Number(value) || 0)} 次`;
  return formatDuration(Number(value) || 0);
}

export interface UsageHeatmapDatum {
  value: [number, number, number];
  cell: HeatmapMatrixCell;
  itemStyle?: { borderColor: string; borderWidth: number };
}

/**
 * 使用热力图：x = hours 0..23、y = days 倒序（最早一天在最上方，与旧 CSS grid 一致），
 * data = [hourIdx, dayIdx, seconds]（0 值也入 data 以保格子），qualityFlags 非空格子标 amber 边框。
 */
export function buildUsageHeatmapOption(matrix: HeatmapMatrix): EChartsOption {
  const data: UsageHeatmapDatum[] = [];
  matrix.days.forEach((day, dayIndex) => {
    const y = matrix.days.length - 1 - dayIndex;
    day.cells.forEach(cell => {
      const datum: UsageHeatmapDatum = {
        value: [cell.localHour, y, cell.foregroundSeconds],
        cell,
      };
      if (cell.qualityFlags.length > 0) {
        datum.itemStyle = { borderColor: chartColors.warning, borderWidth: 1 };
      }
      data.push(datum);
    });
  });

  const option: EChartsOption = {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { data?: UsageHeatmapDatum } | undefined;
        const cell = p?.data?.cell;
        if (!cell) return '';
        const categories = cell.categories.map(item => item.lifeCategory).join(' / ') || '空闲';
        return `${cell.localDate} ${cell.localHour}:00 · ${categories} · ${formatDuration(cell.foregroundSeconds)}`;
      },
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      textStyle: { color: '#fff', fontSize: 11 },
    },
    grid: { left: 76, right: 8, top: 8, bottom: 24 },
    xAxis: [
      {
        type: 'category',
        data: matrix.hours,
        axisLabel: { fontSize: 10, color: chartColors.textMuted, interval: 2 },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    yAxis: [
      {
        type: 'category',
        data: matrix.days.map(day => day.label).reverse(),
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    visualMap: {
      min: 0,
      max: matrix.maxSeconds,
      calculable: false,
      show: false,
      inRange: { color: chartColors.heatmapTeal },
    },
    series: [
      {
        type: 'heatmap',
        data,
        itemStyle: { borderColor: '#fff', borderWidth: 1 },
        emphasis: { itemStyle: { shadowBlur: 4, shadowColor: 'rgba(0, 0, 0, 0.3)' } },
        label: { show: false },
      },
    ],
  };
  return option as EChartsOption;
}

/** 热力图点击反查：ECharts params.dataIndex 是 series.data 数组下标，而 data 按天×小时顺序排列，可直接映射回 cells。 */
export function findCellByParams(matrix: HeatmapMatrix, params: unknown): HeatmapMatrixCell | null {
  const p = (Array.isArray(params) ? params[0] : params) as { dataIndex?: number } | undefined;
  if (typeof p?.dataIndex !== 'number' || p.dataIndex < 0) return null;
  const flat = matrix.days.flatMap(day => day.cells);
  return flat[p.dataIndex] ?? null;
}

interface AnalyticsDatum {
  value: number;
  lifeCategory?: string | null;
  packageName?: string | null;
}

const axisLabel = { fontSize: 10, color: chartColors.textMuted };
const axisLine = { lineStyle: { color: chartColors.borderSoft } };

/** category-trend 未知分类轮转调色板（7 色板外按出现顺序循环取色，避免同色混淆） */
const CATEGORY_TREND_FALLBACK_COLORS = [
  chartColors.primary,
  chartColors.activity,
  chartColors.warning,
  chartColors.danger,
  '#8b5cf6',
  '#06b6d4',
];

/** 手机分析图表按 chartType 分派：category-share→donut pie、top-apps→横向 bar、daily-total→line、
 * hour-distribution→bar、category-trend→line 多 series（按 lifeCategory 分组）、switch-trend→bar。
 * 可点数据项携带 lifeCategory/packageName 原始值供点击反查。 */
function formatAxisValue(value: number, unit: string): string {
  if (unit === 'count') return `${Math.round(value)}次`;
  if (unit === 'seconds') {
    const v = Math.round(value);
    if (v >= 3600) {
      let h = Math.floor(v / 3600);
      let m = Math.round((v % 3600) / 60);
      if (m === 60) { h += 1; m = 0; }
      if (m === 0) return `${h}小时`;
      return `${h}小时${m}分钟`;
    }
    if (v >= 60) return `${Math.round(v / 60)}分钟`;
    return `${v}秒`;
  }
  if (unit === 'ratio') return `${Math.round(value * 100)}%`;
  return String(Math.round(value));
}

export function buildAnalyticsChartOption(chart: MobileAnalyticsChart): EChartsOption {
  const points = chart.points ?? [];

  switch (chart.chartType) {
    case 'category-share': {
      return {
        tooltip: {
          trigger: 'item',
          formatter: (params: unknown) => {
            const p = (Array.isArray(params) ? params[0] : params) as { name?: string; value?: number } | undefined;
            return p ? `${p.name} · ${formatChartValue(Number(p.value) || 0, chart.unit)}` : '';
          },
          backgroundColor: 'rgba(15, 23, 42, 0.92)',
          textStyle: { color: '#fff', fontSize: 11 },
        },
        legend: {
          bottom: 0,
          textStyle: { fontSize: 10, color: chartColors.textMuted },
          type: 'scroll',
        },
        series: [
          {
            type: 'pie',
            radius: ['45%', '70%'],
            center: ['50%', '44%'],
            data: points.map(point => ({
              name: point.label,
              value: point.value,
              lifeCategory: point.lifeCategory ?? null,
              packageName: point.packageName ?? null,
            })),
            label: { show: false },
            itemStyle: { borderRadius: 2 },
          },
        ],
      } as EChartsOption;
    }

    case 'top-apps': {
      const data: AnalyticsDatum[] = points.map(point => ({
        value: point.value,
        lifeCategory: point.lifeCategory ?? null,
        packageName: point.packageName ?? null,
      }));
      return {
        tooltip: {
          trigger: 'item',
          formatter: (params: unknown) => {
            const p = (Array.isArray(params) ? params[0] : params) as { name?: string; value?: number } | undefined;
            return p ? `${p.name} · ${formatChartValue(Number(p.value) || 0, chart.unit)}` : '';
          },
          backgroundColor: 'rgba(15, 23, 42, 0.92)',
          textStyle: { color: '#fff', fontSize: 11 },
        },
        // #162: bottom 24 预留时长 label 高度（比常规 22 多 2px 防裁剪），hideOverlap 避免 5h33m 等长文本在 value 轴上重叠
        grid: { left: 96, right: 16, top: 8, bottom: 24 },
        xAxis: [
          {
            type: 'value',
            axisLabel: { ...axisLabel, formatter: (value: number) => formatDuration(value), hideOverlap: true },
            splitLine: { lineStyle: { color: chartColors.borderSoft } },
          },
        ],
        yAxis: [
          {
            type: 'category',
            // #161: 优先显示 displayName（label），trim 后为空则回退 packageName；均为空白则回退空串避免空白刻度
            data: points.map(point => (point.label?.trim() ? point.label.trim() : (point.packageName?.trim() ? point.packageName.trim() : ''))),
            axisLabel: { ...axisLabel, overflow: 'truncate', width: 88 }, // width 88 = grid.left 96 - 8 gutter，超长包名截断省略
            axisLine,
            axisTick: { show: false },
          },
        ],
        series: [
          {
            type: 'bar',
            data,
            itemStyle: { color: chartColors.activity, borderRadius: [0, 3, 3, 0] },
            barMaxWidth: 14,
          },
        ],
      } as EChartsOption;
    }

    case 'daily-total':
    case 'hour-distribution':
    case 'switch-trend': {
      const isLine = chart.chartType === 'daily-total';
      const data: AnalyticsDatum[] = points.map(point => ({
        value: point.value,
        lifeCategory: point.lifeCategory ?? null,
        packageName: point.packageName ?? null,
      }));
      return {
        tooltip: {
          trigger: 'axis',
          formatter: (params: unknown) => {
            const list = Array.isArray(params) ? params : [params];
            const p = list[0] as { name?: string; value?: number } | undefined;
            return p ? `${p.name} · ${formatChartValue(Number(p.value) || 0, chart.unit)}` : '';
          },
          backgroundColor: 'rgba(15, 23, 42, 0.92)',
          textStyle: { color: '#fff', fontSize: 11 },
        },
        grid: { left: 56, right: 16, top: 8, bottom: 22 },
        xAxis: [
          {
            type: 'category',
            data: points.map(point => point.label),
            axisLabel,
            axisLine,
            axisTick: { show: false },
          },
        ],
        yAxis: [
          {
            type: 'value',
            axisLabel: { ...axisLabel, formatter: (value: number) => formatAxisValue(value, chart.unit) },
            splitLine: { lineStyle: { color: chartColors.borderSoft } },
          },
        ],
        series: [
          {
            type: isLine ? 'line' : 'bar',
            data,
            smooth: true,
            symbolSize: 6,
            itemStyle: { color: chartColors.activity, borderRadius: isLine ? 0 : [3, 3, 0, 0] },
            lineStyle: { color: chartColors.activity, width: 2 },
          },
        ],
      } as EChartsOption;
    }

    case 'category-trend': {
      const groups = new Map<string, MobileAnalyticsChart['points']>();
      for (const point of points) {
        const category = point.lifeCategory ?? '未分类';
        const list = groups.get(category) ?? [];
        list.push(point);
        groups.set(category, list);
      }
      const xLabels = [...new Set(points.map(point => point.label))];
      let fallbackColorIndex = 0;
      const series = [...groups.entries()].map(([category, groupPoints]) => {
        const color = chartColors.category[category]
          ?? CATEGORY_TREND_FALLBACK_COLORS[fallbackColorIndex++ % CATEGORY_TREND_FALLBACK_COLORS.length];
        return {
          type: 'line' as const,
          name: category,
          data: xLabels.map(label => {
            const point = groupPoints.find(item => item.label === label);
            if (!point) return null;
            return {
              value: point.value,
              lifeCategory: point.lifeCategory ?? null,
              packageName: point.packageName ?? null,
            };
          }),
          smooth: true,
          symbolSize: 5,
          itemStyle: { color },
          lineStyle: { width: 2, color },
        };
      });
      return {
        tooltip: { trigger: 'axis', backgroundColor: 'rgba(15, 23, 42, 0.92)', textStyle: { color: '#fff', fontSize: 11 } },
        legend: {
          bottom: 0,
          textStyle: { fontSize: 10, color: chartColors.textMuted },
          type: 'scroll',
        },
        grid: { left: 56, right: 16, top: 8, bottom: 30 },
        xAxis: [
          {
            type: 'category',
            data: xLabels,
            axisLabel,
            axisLine,
            axisTick: { show: false },
          },
        ],
        yAxis: [
          {
            type: 'value',
            axisLabel: { ...axisLabel, formatter: (value: number) => formatAxisValue(value, chart.unit) },
            splitLine: { lineStyle: { color: chartColors.borderSoft } },
          },
        ],
        series,
      } as EChartsOption;
    }

    default:
      // comparison / goal-marker：暂无可视数据，组件层显示「暂无数据」HTML
      return { series: [{ type: 'bar', data: [] }] } as EChartsOption;
  }
}

export interface TimelineStripDatum {
  value: [number, number, number];
  blockId: string;
  block: MobileTimelineBlock;
  itemStyle: { color: string };
}

function formatClock(ms: number): string {
  const d = new Date(ms);
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * 时间线条带：xAxis time、custom series rect（高度 110 容器），每块一个 data 项携带 blockId 供点击反查。
 * 着色：有 lifeCategory 且命中分类色板 → 分类色；否则 activity/primary 交替。
 */
export function buildTimelineStripOption(blocks: MobileTimelineBlock[]): EChartsOption {
  let fallbackIndex = 0;
  const data: TimelineStripDatum[] = blocks.flatMap(block => {
    const start = new Date(block.startUtc).getTime();
    const end = new Date(block.endUtc).getTime();
    if (Number.isNaN(start) || Number.isNaN(end)) return [];
    const categoryColor = block.lifeCategory ? chartColors.category[block.lifeCategory] : undefined;
    const color = categoryColor ?? (fallbackIndex++ % 2 === 0 ? chartColors.activity : chartColors.primary);
    return [
      {
        value: [0, start, end],
        blockId: block.id,
        block,
        itemStyle: { color },
      },
    ];
  });

  const option: EChartsOption = {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { data?: { block?: MobileTimelineBlock } } | undefined;
        const block = p?.data?.block;
        if (!block) return '';
        const start = new Date(block.startUtc);
        const end = new Date(block.endUtc);
        return `${block.lifeCategory || '未分类'} · ${formatClock(start.getTime())} - ${formatClock(end.getTime())} · ${formatDuration(block.foregroundSeconds)}`;
      },
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      textStyle: { color: '#fff', fontSize: 11 },
    },
    grid: { left: 8, right: 8, top: 8, bottom: 22 },
    xAxis: [
      {
        type: 'time',
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
        splitLine: { show: false },
      },
    ],
    yAxis: [
      {
        type: 'category',
        data: [''],
        show: false,
      },
    ],
    series: [
      {
        type: 'custom',
        encode: { x: [1, 2], y: 0 },
        renderItem: (params: unknown, api: unknown) => {
          const p = params as { value?: number[]; data?: { itemStyle?: { color?: string } } };
          const a = api as { coord?: (v: number[]) => number[]; size?: (v: number[]) => number[] };
          const value = Array.isArray(p.value) ? p.value : [0, 0, 0];
          const startMs = Number(value[1]);
          const endMs = Number(value[2]);
          let x = 0;
          let y = 0;
          let width = 4;
          let rowH = 30;
          try {
            const start = a.coord ? a.coord([startMs, 0]) : [0, 0];
            const end = a.coord ? a.coord([endMs, 0]) : [0, 0];
            const size = a.size ? a.size([0, 1]) : [1, 30];
            rowH = Number(size[1]) || 30;
            x = start[0];
            width = Math.max(end[0] - start[0], 2);
            y = start[1];
          } catch {
            // 纯函数环境下 api 不可用时返回占位 rect，不影响 option 结构断言
          }
          const fill = p.data?.itemStyle?.color || chartColors.activity;
          return {
            type: 'rect',
            shape: { x, y: y - rowH * 0.4, width, height: rowH * 0.8, r: 3 },
            style: { fill, stroke: '#ffffff', lineWidth: 1 },
          };
        },
        data,
        emphasis: { focus: 'series' },
      },
    ],
  };
  return option as EChartsOption;
}
