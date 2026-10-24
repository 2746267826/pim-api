import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { PC_BUSINESS_HOURS, pcHourLabel } from '../../src/client-web/src/utils/pcBusinessDay';
import { chartColors } from '../../src/client-web/src/components/charts/chartColors';
import {
  buildTodayActivityAreaOption,
  buildCategoryDonutOption,
  buildQualityRingOption,
  buildFocusSummary,
} from '../../src/client-web/src/components/charts/pcTodayOptions';
import type { HeatmapBucket } from '../../src/client-web/src/types';
import type {
  PcCategoryDistributionItem,
  PcFocusBlockItem,
} from '../../src/client-web/src/api/pcTracker';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const { QueryClient, QueryClientProvider } = requireFromClient('@tanstack/react-query');
(globalThis as typeof globalThis & { React: typeof React }).React = React;
const TodayPcOverview = require('../../src/client-web/src/components/today/TodayPcOverview').default;

function test(name: string, run: () => void) { run(); }

const heatmap: HeatmapBucket[] = PC_BUSINESS_HOURS.map((hour, index) => ({
  start: `2026-08-15T${String(hour).padStart(2, '0')}:00:00`,
  end: `2026-08-15T${String(hour + 1).padStart(2, '0')}:00:00`,
  hour,
  activeMinutes: index * 5,
  totalEvents: index * 2,
  intensityLevel: index % 5,
  intensityMax: 5,
}));

test('buildTodayActivityAreaOption maps 24 business hours to a smooth line with area', () => {
  const option = buildTodayActivityAreaOption(heatmap) as any;
  assert.equal(option.xAxis[0].data.length, 24);
  assert.deepEqual(option.xAxis[0].data, PC_BUSINESS_HOURS.map(pcHourLabel));
  assert.equal(option.series[0].type, 'line');
  assert.ok(option.series[0].areaStyle, 'areaStyle should exist');
  assert.deepEqual(option.series[0].data, heatmap.map(item => item.activeMinutes));
});

test('buildCategoryDonutOption builds pie donut with item colors', () => {
  const items: PcCategoryDistributionItem[] = [
    { categoryName: '编程/折腾', color: '#6B5EE4', minutes: 120, percentage: 40 },
    { categoryName: '学习', color: '#14b8a6', minutes: 90, percentage: 30 },
  ];
  const option = buildCategoryDonutOption(items) as any;
  assert.equal(option.series[0].type, 'pie');
  assert.deepEqual(option.series[0].radius, ['52%', '74%']);
  const first = option.series[0].data[0];
  assert.equal(first.name, '编程/折腾');
  assert.equal(first.value, 120);
  assert.deepEqual(first.itemStyle, { color: '#6B5EE4' });
});

test('buildCategoryDonutOption returns empty data for empty input', () => {
  const option = buildCategoryDonutOption([]) as any;
  assert.equal(option.series[0].type, 'pie');
  assert.deepEqual(option.series[0].data, []);
});

test('buildQualityRingOption renders donut with healthy ratio and center percent', () => {
  const option = buildQualityRingOption(3, 4) as any;
  assert.equal(option.series[0].type, 'pie');
  assert.ok(Array.isArray(option.series[0].radius));
  const data = option.series[0].data;
  assert.equal(data[0].value, 3);
  assert.deepEqual(data[0].itemStyle, { color: chartColors.activity });
  assert.equal(data[1].value, 1);
  assert.deepEqual(data[1].itemStyle, { color: chartColors.borderSoft });
  const graphicText = option.graphic.find((g: any) => g.type === 'text');
  assert.ok(graphicText, 'graphic text should exist');
  assert.ok(graphicText.style.text.includes('75%'), `center text should be 75%, got ${graphicText.style.text}`);
});

test('buildFocusSummary aggregates count, longest and total minutes', () => {
  const blocks: PcFocusBlockItem[] = [
    { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 30, mainApp: 'A', topApps: [] },
    { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 82, mainApp: 'B', topApps: [] },
    { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 12, mainApp: 'C', topApps: [] },
  ];
  assert.deepEqual(buildFocusSummary(blocks), { count: 3, longestMinutes: 82, totalMinutes: 124 });
  assert.deepEqual(buildFocusSummary([]), { count: 0, longestMinutes: 0, totalMinutes: 0 });
});

test('TodayPcOverview renders static area chart and loading aggregation cards', () => {
  const section = {
    id: 'pc.activity',
    kind: 'pc.activity',
    status: 'available',
    generatedAt: '2026-08-15T00:00:00Z',
    data: {
      summary: {
        keystats: null,
        heatmap,
        appRanking: [],
        timeline: [],
        sessions: [],
        metrics: null,
        categories: [],
      },
    },
    links: [],
    error: null,
  } as any;
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const html = renderToStaticMarkup(
    React.createElement(
      QueryClientProvider,
      { client: qc },
      React.createElement(TodayPcOverview, { section })
    )
  );
  assert.ok(html.includes('分类分布'), 'should render 分类分布 card title');
  assert.ok(html.includes('专注段'), 'should render 专注段 card title');
  // 2026-09-19 紧凑化：无数据/加载态下分类图不再画「空圆环」，改为文字态；
  // 面积图占位（role="img"）始终渲染。
  const imgCount = (html.match(/role="img"/g) || []).length;
  assert.ok(imgCount >= 1, `should render at least 1 role="img" placeholder (area chart), got ${imgCount}`);
  assert.ok(html.includes('今日 24 小时 PC 活跃面积图'), 'area chart aria label should exist');
  assert.ok(html.includes('加载中') || html.includes('暂无数据'), 'aggregation cards should show loading/empty state under SSR');
});

console.log('pcTodayCharts tests passed');
