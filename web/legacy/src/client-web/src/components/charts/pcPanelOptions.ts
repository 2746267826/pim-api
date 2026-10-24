import { chartColors } from './chartColors';
import { buildFocusSummary } from './pcTodayOptions';
import type { EChartsOption } from '../../lib/echarts';
import type { PcSummaryResponse, DerivedMetrics } from '../../types';
import type {
  PcAppUsageResponse,
  PcCategoryDistributionResponse,
  PcFocusBlocksResponse,
  PcLateNightResponse,
  DailyProductivity,
} from '../../api/pcTracker';

/** 电脑记录页图表 option 纯函数：输入数据、输出 EChartsOption，不依赖组件/页面。 */

export { buildQualityRingOption } from './pcTodayOptions';

export type MetricTone = 'primary' | 'activity' | 'warning' | 'danger' | 'neutral';

export interface ReviewMetric {
  label: string;
  value: string;
  helper: string;
  tone: MetricTone;
}

function formatCount(value: number) {
  return value.toLocaleString('zh-CN');
}

/**
 * 解析后端 DerivedMetrics 的时长字符串（'8h 12m' / '45m' / '3h' / '0m'）为分钟数。
 * 空串/null/undefined/非法输入返回 0。
 */
export function parseDurationToMinutes(duration: string | null | undefined): number {
  if (!duration) return 0;
  let total = 0;
  const hours = /(\d+(?:\.\d+)?)h/.exec(duration);
  const minutes = /(\d+(?:\.\d+)?)m/.exec(duration);
  if (hours) total += parseFloat(hours[1]) * 60;
  if (minutes) total += parseFloat(minutes[1]);
  return Math.round(total);
}

/**
 * 专注占比环形仪表：focus-blocks 总分钟 / summary.metrics 记录总分钟（字符串解析为分钟），
 * clamp 到 0..100；记录时长为 0 时 value 为 0。进度色 primary→activity 线性渐变，轴线浅灰。
 */
export function buildFocusGaugeOption(
  focusBlocks: PcFocusBlocksResponse | undefined,
  summaryMetrics: DerivedMetrics | undefined,
): EChartsOption {
  const focusMinutes = (focusBlocks?.items ?? []).reduce((sum, block) => sum + block.durationMinutes, 0);
  const totalRecordedMinutes = parseDurationToMinutes(summaryMetrics?.totalRecordedDuration);
  const raw = totalRecordedMinutes > 0 ? (focusMinutes / totalRecordedMinutes) * 100 : 0;
  const value = Math.round(Math.min(100, Math.max(0, raw)));

  return {
    series: [
      {
        type: 'gauge',
        min: 0,
        max: 100,
        startAngle: 210,
        endAngle: -30,
        axisLine: {
          lineStyle: {
            width: 14,
            color: [[1, chartColors.borderSoft]],
          },
        },
        progress: {
          show: true,
          width: 14,
          itemStyle: {
            color: {
              type: 'linear',
              x: 0,
              y: 0,
              x2: 1,
              y2: 0,
              colorStops: [
                { offset: 0, color: chartColors.primary },
                { offset: 1, color: chartColors.activity },
              ],
            },
          },
        },
        pointer: { show: false },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { show: false },
        title: {
          show: true,
          offsetCenter: [0, '34%'],
          fontSize: 12,
          color: chartColors.textMuted,
        },
        detail: {
          show: true,
          valueAnimation: true,
          offsetCenter: [0, 0],
          fontSize: 26,
          fontWeight: 'bold' as const,
          color: chartColors.primary,
          formatter: '{value}%',
        },
        data: [{ value, name: '专注占比' }],
      },
    ],
  };
}

/** 本周趋势：每根柱为该日记录时长（totalMinutes），tooltip 注明「记录时长」口径，x=日期。 */
export function buildWeeklyTrendOption(daily: DailyProductivity[]): EChartsOption {
  return {
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (params: unknown) => {
        const axisParams = Array.isArray(params) ? params : [params];
        const p = axisParams[0] as { dataIndex?: number } | undefined;
        const item = p?.dataIndex !== undefined ? daily[p.dataIndex] : undefined;
        if (!item) return '';
        return `${item.date} 记录时长 ${item.totalMinutes} 分钟`;
      },
    },
    grid: { left: 8, right: 12, top: 20, bottom: 8, containLabel: true },
    xAxis: [
      {
        type: 'category',
        data: daily.map(day => day.date),
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    yAxis: [
      {
        type: 'value',
        splitLine: { lineStyle: { color: chartColors.borderSoft } },
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
      },
    ],
    series: [
      {
        type: 'bar',
        barWidth: '55%',
        itemStyle: { borderRadius: [3, 3, 0, 0], color: chartColors.primary },
        data: daily.map(day => day.totalMinutes),
      },
    ],
  };
}

/** 应用时长横向 bar：yAxis category inverse（首行最高），色取分类色或 primary，label 右侧 `X 分钟`。 */
export function buildAppUsageBarOption(appUsage: PcAppUsageResponse | undefined): EChartsOption {
  const top = [...(appUsage?.items ?? [])]
    .sort((a, b) => b.totalMinutes - a.totalMinutes)
    .slice(0, 8);

  return {
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (params: unknown) => {
        const axisParams = Array.isArray(params) ? params : [params];
        const p = axisParams[0] as { dataIndex?: number } | undefined;
        const item = p?.dataIndex !== undefined ? top[p.dataIndex] : undefined;
        if (!item) return '';
        return `${item.appName}：${item.totalMinutes} 分钟（${item.percentage}%）`;
      },
    },
    grid: { left: 8, right: 56, top: 8, bottom: 8, containLabel: true },
    xAxis: [{ type: 'value', show: false }],
    yAxis: [
      {
        type: 'category',
        inverse: true,
        data: top.map(item => item.displayName ?? item.appName),
        axisLabel: { fontSize: 11, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    series: [
      {
        type: 'bar',
        barWidth: 14,
        label: { show: true, position: 'right', formatter: '{c} 分钟', fontSize: 10, color: chartColors.textMuted },
        itemStyle: { borderRadius: [0, 7, 7, 0], color: chartColors.primary },
        data: top.map(item => ({
          value: item.totalMinutes,
          itemStyle: { color: chartColors.category[item.appName] ?? chartColors.primary },
        })),
      },
    ],
  };
}

/**
 * 复盘指标行：summary 指标 + 聚合数据（专注块/深夜使用/分类分布）驱动的 7 张 MetricCard。
 * 聚合数据缺失时该卡值显示 '—'、helper 显示「等待同步」，不留空。
 */
export function buildReviewMetrics(
  summary: PcSummaryResponse | undefined,
  focusBlocks: PcFocusBlocksResponse | undefined,
  lateNight: PcLateNightResponse | undefined,
  distribution: PcCategoryDistributionResponse | undefined,
  dateStr?: string,
): ReviewMetric[] {
  const metrics = summary?.metrics;
  const focus = focusBlocks ? buildFocusSummary(focusBlocks.items) : null;

  let lateMinutes: number | null = null;
  const lateItems = lateNight?.items ?? [];
  if (lateItems.length > 0) {
    // 优先取页面业务日期当天的条目；items 顺序/末位不可靠，缺失时再退回末位。
    const pick = lateNight?.items?.find(item => item.date === dateStr) ?? lateItems[lateItems.length - 1];
    lateMinutes = pick.minutes;
  }

  let coverage: number | null = null;
  let otherPct = 0;
  const distItems = distribution?.items ?? [];
  if (distItems.length > 0) {
    const other = distItems.find(item => item.categoryName === '其他');
    otherPct = Math.round((other?.percentage ?? 0) * 10) / 10;
    otherPct = Math.max(0, Math.min(100, otherPct));
    coverage = Math.max(0, Math.min(100, Math.round((100 - otherPct) * 10) / 10));
  }

  const mainCategoryName = summary?.categories?.[0]?.categoryName || '暂无';

  return [
    {
      label: '记录时长',
      value: metrics?.totalRecordedDuration ?? '—',
      helper: metrics ? '今日记录' : '等待同步',
      tone: 'primary',
    },
    {
      label: '活跃输入',
      value: metrics?.activeInputDuration ?? '—',
      helper: metrics ? '有效输入时长' : '等待同步',
      tone: 'activity',
    },
    {
      label: '主要分类',
      value: mainCategoryName,
      helper: '占比最高',
      tone: 'warning',
    },
    {
      label: '上下文切换',
      value: metrics ? formatCount(metrics.appSwitchCount) : '—',
      helper: metrics ? '今日切换次数' : '等待同步',
      tone: 'neutral',
    },
    {
      label: '专注块',
      value: focus ? `${focus.count} 段` : '—',
      helper: focus ? `最长 ${focus.longestMinutes} 分钟` : '等待同步',
      tone: 'primary',
    },
    {
      label: '深夜使用',
      value: lateMinutes !== null ? `${lateMinutes} 分钟` : '—',
      helper: lateMinutes !== null ? '23:30 后' : '等待同步',
      tone: 'warning',
    },
    {
      label: '分类覆盖率',
      value: coverage !== null ? `${coverage}%` : '—',
      helper: coverage !== null ? `100% - 其他 ${otherPct}%` : '等待同步',
      tone: 'activity',
    },
  ];
}
