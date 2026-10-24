import type { EChartsOption } from '../../lib/echarts';
import type { PcBrowserDailyItem, PcBrowserTimelineItem } from '../../api/pcBrowserSite';
import { formatDurationMs, formatHourLabel, shanghaiHourOfDay } from './pcBrowserFormatting';

/** 浏览器使用页图表 option 纯函数：输入数据、输出 EChartsOption，不依赖组件/页面。 */

const TOOLTIP_BASE = {
  backgroundColor: 'rgba(15, 23, 42, 0.92)',
  textStyle: { color: '#fff', fontSize: 11 },
} as const;

/** 时段分布：每行一个 host，x 轴 0-24 时，custom series 用 startMs/durationMs 画矩形段。 */
export function buildBrowserDayTimelineOption(items: PcBrowserTimelineItem[]): EChartsOption {
  const hostTotals = new Map<string, number>();
  items.forEach(item => {
    hostTotals.set(item.host, (hostTotals.get(item.host) ?? 0) + (item.durationMs || 0));
  });
  const hosts = [...hostTotals.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([host]) => host);
  const hostIndex = new Map(hosts.map((host, index) => [host, index]));

  const data = items.map(item => {
    const startHour = shanghaiHourOfDay(item.startMs);
    const endHour = Math.min(24, startHour + (item.durationMs || 0) / 3600000);
    return [startHour, endHour, hostIndex.get(item.host) ?? 0, item.durationMs || 0];
  });

  return {
    tooltip: {
      trigger: 'item',
      ...TOOLTIP_BASE,
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { value?: number[] } | undefined;
        const value = Array.isArray(p?.value) ? p!.value : [];
        const host = hosts[Number(value[2])] ?? '';
        const durationMs = Number(value[3]) || 0;
        return `${host}<br/>${formatHourLabel(Number(value[0]))} - ${formatHourLabel(Number(value[1]))} · ${formatDurationMs(durationMs)}`;
      },
    },
    grid: { left: 110, right: 16, top: 8, bottom: 24 },
    xAxis: {
      type: 'value',
      min: 0,
      max: 24,
      interval: 2,
      axisLabel: {
        fontSize: 10,
        color: '#64748b',
        formatter: (value: number) => `${value}时`,
      },
      axisLine: { lineStyle: { color: '#e2e8f0' } },
      axisTick: { show: false },
      splitLine: { lineStyle: { color: '#f1f5f9' } },
    },
    yAxis: {
      type: 'category',
      data: hosts,
      inverse: true,
      axisLabel: {
        fontSize: 10,
        color: '#64748b',
        width: 100,
        overflow: 'truncate',
      },
      axisLine: { lineStyle: { color: '#e2e8f0' } },
      axisTick: { show: false },
    },
    series: [
      {
        type: 'custom',
        encode: { x: [0, 1], y: 2 },
        renderItem: (params: unknown, api: unknown) => {
          const p = params as { value?: number[] };
          const a = api as { coord?: (v: number[]) => number[]; size?: (v: number[]) => number[] };
          const value = Array.isArray(p.value) ? p.value : [0, 0, 0, 0];
          const startHour = Number(value[0]);
          const endHour = Number(value[1]);
          const yIndex = Number(value[2]);
          let x = 0;
          let y = 0;
          let width = 2;
          let bandHeight = 24;
          try {
            const start = a.coord ? a.coord([startHour, yIndex]) : [0, 0];
            const end = a.coord ? a.coord([endHour, yIndex]) : [0, 0];
            const size = a.size ? a.size([0, 1]) : [1, 24];
            bandHeight = Number(size[1]) || 24;
            x = start[0];
            y = start[1];
            width = Math.max(end[0] - start[0], 2);
          } catch {
            // 纯函数环境下 api 不可用时返回占位 rect，不影响 option 结构
          }
          return {
            type: 'rect',
            shape: { x, y: y - bandHeight * 0.28, width, height: bandHeight * 0.56, r: 3 },
            style: {
              fill: {
                type: 'linear',
                x: 0,
                y: 0,
                x2: 1,
                y2: 0,
                colorStops: [
                  { offset: 0, color: '#3b82f6' },
                  { offset: 1, color: '#8b5cf6' },
                ],
              },
            },
          };
        },
        data,
      },
    ],
  } as EChartsOption;
}

/** 趋势：按日汇总总专注时长（daily 数据按 host 拆分，这里聚合成每天一根柱）。 */
export function buildBrowserTrendOption(daily: PcBrowserDailyItem[]): EChartsOption {
  const focusByDate = new Map<string, number>();
  daily.forEach(item => {
    focusByDate.set(item.date, (focusByDate.get(item.date) ?? 0) + (item.focusMs || 0));
  });
  const dates = [...focusByDate.keys()].sort();
  const data = dates.map(date => {
    const focusMs = focusByDate.get(date) ?? 0;
    return {
      value: Math.round((focusMs / 3600000) * 10) / 10,
      focusMs,
    };
  });

  return {
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      ...TOOLTIP_BASE,
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as
          { dataIndex?: number; axisValue?: string } | undefined;
        const index = Number(p?.dataIndex ?? 0);
        const date = dates[index] ?? (p?.axisValue ?? '');
        const focusMs = data[index]?.focusMs ?? 0;
        return `${date}<br/>专注时长 ${formatDurationMs(focusMs)}`;
      },
    },
    grid: { left: 44, right: 16, top: 16, bottom: 24 },
    xAxis: {
      type: 'category',
      data: dates,
      axisLabel: {
        fontSize: 10,
        color: '#64748b',
        formatter: (value: string) => value.slice(5),
      },
      axisLine: { lineStyle: { color: '#e2e8f0' } },
      axisTick: { show: false },
    },
    yAxis: {
      type: 'value',
      axisLabel: {
        fontSize: 10,
        color: '#64748b',
        formatter: (value: number) => `${value}h`,
      },
      axisLine: { show: false },
      splitLine: { lineStyle: { color: '#f1f5f9' } },
    },
    series: [
      {
        type: 'bar',
        data,
        barMaxWidth: 28,
        itemStyle: {
          borderRadius: [4, 4, 0, 0],
          color: {
            type: 'linear',
            x: 0,
            y: 0,
            x2: 0,
            y2: 1,
            colorStops: [
              { offset: 0, color: '#8b5cf6' },
              { offset: 1, color: '#3b82f6' },
            ],
          },
        },
      },
    ],
  } as EChartsOption;
}
