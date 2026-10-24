import { echarts, type EChartsOption } from '../../lib/echarts';
import { chartColors } from './chartColors';
import { PC_BUSINESS_HOURS, pcHourLabel } from '../../utils/pcBusinessDay';
import type { HeatmapBucket } from '../../types';
import type {
  PcCategoryDistributionItem,
  PcFocusBlockItem,
} from '../../api/pcTracker';

/**
 * 今日页 PC 图表 option 纯函数：输入数据、输出 EChartsOption，不依赖组件/页面。
 * 测试直接断言 option 结构，绕开 canvas 静态渲染盲区。
 */

/** 24 小时活跃面积图：x = 业务小时标签（04:00 起），y = activeMinutes，primary 渐变面积。 */
export function buildTodayActivityAreaOption(heatmap: HeatmapBucket[]): EChartsOption {
  const buckets = heatmap ?? [];
  const bucketByHour = new Map(buckets.map(item => [item.hour, item]));
  const data = PC_BUSINESS_HOURS.map(hour => {
    const bucket = bucketByHour.get(hour);
    return {
      value: bucket?.activeMinutes ?? 0,
      totalEvents: bucket?.totalEvents ?? 0,
    };
  });

  return {
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const axisParams = Array.isArray(params) ? params : [params];
        const first = axisParams[0] as { dataIndex?: number; value?: number } | undefined;
        if (first?.dataIndex === undefined) return '';
        const hour = PC_BUSINESS_HOURS[first.dataIndex] ?? 0;
        const bucket = data[first.dataIndex];
        return `${pcHourLabel(hour)} 活跃 ${bucket?.value ?? 0} 分钟 / 事件 ${bucket?.totalEvents ?? 0} 次`;
      },
    },
    grid: { left: 28, right: 8, top: 8, bottom: 18 },
    xAxis: [
      {
        type: 'category',
        data: PC_BUSINESS_HOURS.map(pcHourLabel),
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
        type: 'line',
        smooth: true,
        symbol: 'none',
        lineStyle: { color: chartColors.primary, width: 2 },
        itemStyle: { color: chartColors.primary },
        areaStyle: {
          color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: 'rgba(37, 99, 235, 0.28)' },
            { offset: 1, color: 'rgba(37, 99, 235, 0.02)' },
          ]),
        },
        data: data.map(item => item.value),
      },
    ],
  };
}

/** 分类分布环图：donut pie，色取服务端下发分类色，label 外置「分类 占比%」。 */
export function buildCategoryDonutOption(
  items: PcCategoryDistributionItem[],
  _opts?: { center?: string },
): EChartsOption {
  return {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = params as { name?: string; value?: number; percent?: number } | undefined;
        return p ? `${p.name}：${p.value ?? 0} 分钟（${p.percent ?? 0}%）` : '';
      },
    },
    series: [
      {
        type: 'pie',
        radius: ['52%', '74%'],
        center: ['50%', '50%'],
        avoidLabelOverlap: true,
        label: {
          formatter: '{b} {d}%',
          fontSize: 11,
          color: chartColors.textMuted,
        },
        labelLine: { length: 10, length2: 8 },
        data: items.map(item => ({
          name: item.categoryName,
          value: item.minutes,
          itemStyle: { color: item.color },
        })),
      },
    ],
  };
}

/** 数据质量完成率环：healthy 用 activity 色、余量 borderSoft，中心 graphic 文本百分比。 */
export function buildQualityRingOption(healthy: number, total: number): EChartsOption {
  const percent = total > 0 ? Math.round((healthy / total) * 100) : 0;
  return {
    series: [
      {
        type: 'pie',
        radius: ['68%', '84%'],
        center: ['50%', '50%'],
        silent: true,
        label: { show: false },
        data: [
          { value: healthy, itemStyle: { color: chartColors.activity } },
          { value: Math.max(total - healthy, 0), itemStyle: { color: chartColors.borderSoft } },
        ],
      },
    ],
    graphic: [
      {
        type: 'text',
        left: 'center',
        top: 'center',
        style: {
          text: `${percent}%`,
          textAlign: 'center',
          fill: chartColors.textMuted,
          fontSize: 16,
          fontWeight: 600,
        },
      },
    ],
  };
}

/** 专注段摘要纯对象：段数 / 最长分钟 / 合计分钟。 */
export function buildFocusSummary(items: PcFocusBlockItem[]) {
  let longestMinutes = 0;
  let totalMinutes = 0;
  for (const item of items) {
    totalMinutes += item.durationMinutes;
    if (item.durationMinutes > longestMinutes) longestMinutes = item.durationMinutes;
  }
  return { count: items.length, longestMinutes, totalMinutes };
}

/**
 * REQ-8 · 「PC 活跃」口径说明。
 *
 * 后端把「只有键鼠输入、没有窗口事件」的分钟也算作活跃（`input-minute`），
 * 同一业务日数值会明显变大（实测 9/27：569 → 722.8 分钟），因此「活跃」可能
 * 大于时间线覆盖时长。这是上游有意统一口径的结果，不是 bug —— 说明用于避免
 * 用户把口径变化报成缺陷。
 */
export const PC_ACTIVE_MINUTES_NOTE =
  '「PC 活跃」包含只有键鼠输入、无窗口记录的分钟，因此可能大于时间线覆盖时长（数值会变大）。';

/** 「PC 活跃」小时数：与 summary.heatmap.activeMinutes 求和一致，不夹到时间线时长。 */
export function sumHeatmapActiveMinutes(heatmap: HeatmapBucket[] | undefined): number {
  return (heatmap ?? []).reduce((acc, bucket) => acc + (bucket?.activeMinutes || 0), 0);
}

/** 分钟 → 「X.Xh」展示（PC 记录概览的「活跃」数值口径）。 */
export function formatActiveHours(minutes: number): string {
  return `${(minutes / 60).toFixed(1)}h`;
}
