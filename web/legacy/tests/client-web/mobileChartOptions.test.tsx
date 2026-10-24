import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chartColors } from '../../src/client-web/src/components/charts/chartColors';
import { buildHeatmapMatrix } from '../../src/client-web/src/components/mobile/mobileHeatmapMatrix';
import {
  buildUsageHeatmapOption,
  buildAnalyticsChartOption,
  buildTimelineStripOption,
  findCellByParams,
  formatChartValue,
} from '../../src/client-web/src/components/charts/mobileChartOptions';
import type {
  MobileAnalyticsChart,
  MobileHeatmapBucket,
  MobileTimelineBlock,
} from '../../src/client-web/src/api/mobile';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

const MobileUsageHeatmap = require('../../src/client-web/src/components/mobile/MobileUsageHeatmap').default;

function test(_name: string, run: () => void) {
  run();
}

const bucket: MobileHeatmapBucket = {
  bucketStartUtc: '2026-07-06T14:00:00.000Z',
  bucketEndUtc: '2026-07-06T15:00:00.000Z',
  localDate: '2026-07-06',
  localHour: 22,
  lifeCategory: '短视频/娱乐',
  foregroundSeconds: 1800,
  qualityFlags: [],
};

test('buildUsageHeatmapOption renders 24xN teal heatmap over reversed days', () => {
  const buckets: MobileHeatmapBucket[] = [
    bucket,
    {
      bucketStartUtc: '2026-07-07T01:00:00.000Z',
      bucketEndUtc: '2026-07-07T02:00:00.000Z',
      localDate: '2026-07-07',
      localHour: 9,
      lifeCategory: '工作/生产力',
      foregroundSeconds: 1200,
      qualityFlags: ['fallback'],
    },
  ];
  const matrix = buildHeatmapMatrix(buckets);
  const option = buildUsageHeatmapOption(matrix) as any;

  assert.equal(option.series[0].type, 'heatmap');
  assert.deepEqual(option.xAxis[0].data, Array.from({ length: 24 }, (_, hour) => hour), 'xAxis is hours 0..23');
  assert.deepEqual(
    option.yAxis[0].data,
    matrix.days.map(day => day.label).reverse(),
    'yAxis days reversed so earliest day renders at the top',
  );
  assert.equal(option.series[0].data.length, 24 * 2, '0-value cells stay in data to keep the grid');
  const firstDayCell = option.series[0].data.find(
    (d: any) => d.cell?.localDate === '2026-07-06' && d.cell.localHour === 22,
  );
  assert.deepEqual(firstDayCell.value, [22, 1, 1800], 'earliest day at reversed top row (y=1)');
  assert.equal(option.visualMap.min, 0);
  assert.equal(option.visualMap.max, matrix.maxSeconds);
  assert.deepEqual(option.visualMap.inRange.color, chartColors.heatmapTeal);
  assert.deepEqual(option.series[0].itemStyle, { borderColor: '#fff', borderWidth: 1 });
  const flagged = option.series[0].data.find((d: any) => d.cell?.qualityFlags.length > 0);
  assert.deepEqual(flagged.itemStyle, { borderColor: '#f59e0b', borderWidth: 1 }, 'quality-flagged cell gets amber border');
});

test('findCellByParams reverse-looks-up the clicked cell via dataIndex', () => {
  const matrix = buildHeatmapMatrix([bucket]);
  const cell = findCellByParams(matrix, { dataIndex: 22 });
  assert.equal(cell?.localDate, '2026-07-06');
  assert.equal(cell?.localHour, 22);
  assert.equal(cell?.sourceBuckets[0]?.bucketStartUtc, bucket.bucketStartUtc, 'cell exposes sourceBuckets for detail drilldown');
  assert.equal(findCellByParams(matrix, { dataIndex: 999 }), null);
  assert.equal(findCellByParams(matrix, {}), null);
});

test('buildAnalyticsChartOption dispatches by chartType', () => {
  const categoryShare: MobileAnalyticsChart = {
    key: 'category-share',
    title: '分类占比',
    chartType: 'category-share',
    unit: 'seconds',
    points: [{ key: 'a', label: '社交通讯', value: 3600, lifeCategory: '社交通讯' }],
  };
  const pie = buildAnalyticsChartOption(categoryShare) as any;
  assert.equal(pie.series[0].type, 'pie');
  assert.deepEqual(pie.series[0].radius, ['45%', '70%'], 'donut radius');
  assert.equal(pie.series[0].data[0].name, '社交通讯');
  assert.equal(pie.series[0].data[0].lifeCategory, '社交通讯');

  const topApps: MobileAnalyticsChart = {
    key: 'top-apps',
    title: 'Top App',
    chartType: 'top-apps',
    unit: 'seconds',
    points: [{ key: 'w', label: '微信', value: 3600, packageName: 'com.tencent.mm' }],
  };
  const bar = buildAnalyticsChartOption(topApps) as any;
  assert.equal(bar.series[0].type, 'bar');
  assert.equal(bar.yAxis[0].type, 'category');
  assert.deepEqual(bar.yAxis[0].data, ['微信'], 'horizontal bar yAxis shows displayName (label) not packageName #161');
  assert.equal(bar.series[0].data[0].packageName, 'com.tencent.mm', 'clickable data item carries raw packageName');
  // #161: fallback to packageName when label absent or whitespace
  const topAppsFallback: MobileAnalyticsChart = {
    key: 'top-apps',
    title: 'Top App',
    chartType: 'top-apps',
    unit: 'seconds',
    points: [{ key: 'x', label: '', value: 100, packageName: 'com.unknown.app' }],
  };
  const barFallback = buildAnalyticsChartOption(topAppsFallback) as any;
  assert.deepEqual(barFallback.yAxis[0].data, ['com.unknown.app'], 'fallback to packageName when label empty');
  const topAppsWs: MobileAnalyticsChart = {
    key: 'top-apps',
    title: 'Top App',
    chartType: 'top-apps',
    unit: 'seconds',
    points: [{ key: 'y', label: '   ', value: 100, packageName: 'com.example.ws' }],
  };
  assert.deepEqual((buildAnalyticsChartOption(topAppsWs) as any).yAxis[0].data, ['com.example.ws'], 'whitespace label falls back to packageName');
  // tooltip should show displayName
  const topAppsTooltip: MobileAnalyticsChart = {
    key: 'top-apps',
    title: 'Top App',
    chartType: 'top-apps',
    unit: 'seconds',
    points: [{ key: 't', label: '微信', value: 7200, packageName: 'com.tencent.mm' }],
  };
  const tipOpt = buildAnalyticsChartOption(topAppsTooltip) as any;
  assert.equal(tipOpt.tooltip.formatter({ name: '微信', value: 7200 }), '微信 · 2小时', 'tooltip uses displayName');
  // #162: xAxis anti-overlap and layout
  assert.equal(bar.xAxis[0].axisLabel.hideOverlap, true, 'xAxis labels hide overlap to prevent crowding #162');
  assert.equal(bar.grid.bottom, 24, 'grid bottom gives xAxis room #162');
  assert.equal(bar.yAxis[0].axisLabel.overflow, 'truncate', 'yAxis truncate long packageNames');
  assert.equal(bar.yAxis[0].axisLabel.width, 88, 'yAxis label width with truncate');

  const dailyTotal: MobileAnalyticsChart = {
    key: 'daily-total',
    title: '每日趋势',
    chartType: 'daily-total',
    unit: 'seconds',
    points: [{ key: 'd', label: '7月6日', value: 7200, localDate: '2026-07-06' }],
  };
  const line = buildAnalyticsChartOption(dailyTotal) as any;
  assert.equal(line.series[0].type, 'line');

  const hour: MobileAnalyticsChart = {
    key: 'hour-distribution',
    title: '小时分布',
    chartType: 'hour-distribution',
    unit: 'seconds',
    points: [{ key: '09', label: '09:00', value: 900, localHour: 9 }],
  };
  assert.equal((buildAnalyticsChartOption(hour) as any).series[0].type, 'bar');

  const trend: MobileAnalyticsChart = {
    key: 'category-trend',
    title: '分类趋势',
    chartType: 'category-trend',
    unit: 'seconds',
    points: [
      { key: 'w', label: '工作', value: 1800, lifeCategory: '工作/生产力' },
      { key: 'v', label: '短视频', value: 2400, lifeCategory: '短视频/娱乐' },
    ],
  };
  const trendOption = buildAnalyticsChartOption(trend) as any;
  assert.equal(trendOption.series.length, 2, 'one line series per lifeCategory');
  assert.ok(trendOption.series.every((s: any) => s.type === 'line'));
  assert.ok(trendOption.series[0].data.some((d: any) => d.lifeCategory), 'trend data items carry lifeCategory');

  const switchTrend: MobileAnalyticsChart = {
    key: 'switch-trend',
    title: '切换趋势',
    chartType: 'switch-trend',
    unit: 'count',
    points: [{ key: 'd', label: '7月6日', value: 42, localDate: '2026-07-06' }],
  };
  assert.equal((buildAnalyticsChartOption(switchTrend) as any).series[0].type, 'bar');
});

test('formatChartValue dispatches tooltip units by chart unit', () => {
  assert.equal(formatChartValue(42, 'count'), '42 次', 'count unit formats as count');
  assert.equal(formatChartValue(1800, 'seconds'), '30分钟', 'seconds unit formats as duration');
  assert.equal(formatChartValue(3599, 'seconds'), '59分钟', 'seconds stays consistent with existing formatDuration');
  assert.equal(formatChartValue(42, undefined), '42秒', 'missing unit falls back to duration seconds');
  assert.equal(formatChartValue(0, 'count'), '0 次');
});

test('analytics tooltip formatter uses chart unit (switch-trend count shows 次, not 秒)', () => {
  const switchTrend: MobileAnalyticsChart = {
    key: 'switch-trend',
    title: '切换趋势',
    chartType: 'switch-trend',
    unit: 'count',
    points: [{ key: 'd', label: '7月6日', value: 42, localDate: '2026-07-06' }],
  };
  const option = buildAnalyticsChartOption(switchTrend) as any;
  const text = option.tooltip.formatter({ name: '7月6日', value: 42 });
  assert.equal(text, '7月6日 · 42 次', 'switch-trend tooltip must not show 42秒');

  const dailyTotal: MobileAnalyticsChart = {
    key: 'daily-total',
    title: '每日趋势',
    chartType: 'daily-total',
    unit: 'seconds',
    points: [{ key: 'd', label: '7月6日', value: 7200, localDate: '2026-07-06' }],
  };
  const secondsText = (buildAnalyticsChartOption(dailyTotal) as any).tooltip.formatter({ name: '7月6日', value: 7200 });
  assert.equal(secondsText, '7月6日 · 2小时', 'seconds unit keeps duration formatting');
});

test('category-trend unknown categories rotate through fallback palette by appearance order', () => {
  const trend: MobileAnalyticsChart = {
    key: 'category-trend',
    title: '分类趋势',
    chartType: 'category-trend',
    unit: 'seconds',
    points: [
      { key: 'u1', label: 'L1', value: 1800, lifeCategory: '未知A' },
      { key: 'u2', label: 'L1', value: 2400, lifeCategory: '未知B' },
      { key: 'u3', label: 'L1', value: 900, lifeCategory: '未知C' },
    ],
  };
  const option = buildAnalyticsChartOption(trend) as any;
  assert.equal(option.series.length, 3);
  const colors = option.series.map((s: any) => s.itemStyle.color);
  assert.equal(new Set(colors).size, 3, 'unknown categories must not share one fallback color');
  assert.equal(colors[0], chartColors.primary);
  assert.equal(colors[1], chartColors.activity);
  assert.equal(colors[2], chartColors.warning);
  assert.ok(option.series.every((s: any) => s.itemStyle.color === s.lineStyle.color), 'line and item style stay consistent');
});

test('buildTimelineStripOption renders custom rect strip over time axis with block ids', () => {
  const blocks: MobileTimelineBlock[] = [
    {
      id: 'b1',
      startUtc: '2026-07-06T00:00:00.000Z',
      endUtc: '2026-07-06T01:00:00.000Z',
      localStart: '2026-07-06 08:00',
      localEnd: '2026-07-06 09:00',
      lifeCategory: '学习',
      foregroundSeconds: 3600,
      sessionCount: 1,
      appCount: 1,
      topApps: [],
      qualityFlags: [],
    },
    {
      id: 'b2',
      startUtc: '2026-07-06T02:00:00.000Z',
      endUtc: '2026-07-06T04:00:00.000Z',
      localStart: '2026-07-06 10:00',
      localEnd: '2026-07-06 12:00',
      lifeCategory: '未知分类',
      foregroundSeconds: 7200,
      sessionCount: 1,
      appCount: 1,
      topApps: [],
      qualityFlags: [],
    },
    {
      id: 'b3',
      startUtc: '2026-07-06T08:00:00.000Z',
      endUtc: '2026-07-06T09:00:00.000Z',
      localStart: '2026-07-06 16:00',
      localEnd: '2026-07-06 17:00',
      lifeCategory: '视频',
      foregroundSeconds: 3600,
      sessionCount: 1,
      appCount: 1,
      topApps: [],
      qualityFlags: [],
    },
  ];
  const option = buildTimelineStripOption(blocks) as any;
  assert.equal(option.series[0].type, 'custom');
  assert.equal(typeof option.series[0].renderItem, 'function');
  assert.equal(option.xAxis[0].type, 'time');
  assert.equal(option.series[0].data.length, 3);
  assert.deepEqual(option.series[0].data.map((d: any) => d.blockId), ['b1', 'b2', 'b3'], 'each data item carries block id');
  assert.equal(option.series[0].data[0].itemStyle.color, chartColors.category['学习']);
  assert.equal(option.series[0].data[1].itemStyle.color, chartColors.activity, 'unknown category falls back to activity/primary alternation');
  assert.equal(option.series[0].data[2].itemStyle.color, chartColors.category['视频']);
  const tooltipText = option.tooltip.formatter({ data: { block: blocks[0] } });
  assert.ok(tooltipText.includes('学习'));
  assert.ok(tooltipText.includes('1小时'));
});

test('MobileUsageHeatmap statically renders title, granularity buttons and chart placeholder', () => {
  const html = renderToStaticMarkup(
    React.createElement(MobileUsageHeatmap, {
      buckets: [bucket],
      granularity: 'hour',
      isLoading: false,
      onGranularityChange: () => undefined,
      onBucketSelect: () => undefined,
    }),
  );
  assert.equal(html.includes('使用热力图'), true);
  assert.equal(html.includes('小时'), true);
  assert.equal(html.includes('30m'), true);
  assert.equal(html.includes('15m'), true);
  assert.equal(html.includes('role="img"'), true, 'EChartBox placeholder renders');
});

console.log('mobileChartOptions tests passed');
