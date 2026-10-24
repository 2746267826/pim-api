import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { chartColors } from '../../src/client-web/src/components/charts/chartColors';
import {
  buildAppUsageBarOption,
  buildFocusGaugeOption,
  buildReviewMetrics,
  buildWeeklyTrendOption,
  parseDurationToMinutes,
} from '../../src/client-web/src/components/charts/pcPanelOptions';
import type { PcSummaryResponse, DerivedMetrics } from '../../src/client-web/src/types';
import type {
  PcAppUsageResponse,
  PcCategoryDistributionResponse,
  PcFocusBlocksResponse,
  PcLateNightResponse,
  DailyProductivity,
} from '../../src/client-web/src/api/pcTracker';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const { QueryClient, QueryClientProvider } = requireFromClient('@tanstack/react-query');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

const PcQualitySummary = require('../../src/client-web/src/components/pc-tracker/PcQualitySummary').default;
const PcReviewSummary = require('../../src/client-web/src/components/pc-tracker/PcReviewSummary').default;
const DailyActivityPanel = require('../../src/client-web/src/components/pc-tracker/DailyActivityPanel').default;
const ProductivityDashboardPanel = require('../../src/client-web/src/components/pc-tracker/ProductivityDashboard').default;

function test(name: string, run: () => void) { run(); }

const appUsage: PcAppUsageResponse = {
  totalMinutes: 620,
  items: [
    { appName: 'Code.exe', displayName: 'Visual Studio Code', totalMinutes: 120, percentage: 40 },
    { appName: '学习', displayName: '学习 App', totalMinutes: 90, percentage: 30 },
    { appName: 'msedge.exe', displayName: 'Edge', totalMinutes: 70, percentage: 25 },
    { appName: 'wps.exe', displayName: 'WPS', totalMinutes: 50, percentage: 15 },
    { appName: 'notion.exe', displayName: 'Notion', totalMinutes: 40, percentage: 12 },
    { appName: 'wechat.exe', displayName: '微信', totalMinutes: 30, percentage: 9 },
    { appName: 'music.exe', displayName: '音乐', totalMinutes: 20, percentage: 6 },
    { appName: 'game.exe', displayName: '游戏', totalMinutes: 10, percentage: 3 },
    { appName: 'extra.exe', displayName: '额外', totalMinutes: 5, percentage: 2 },
    { appName: 'extra2.exe', displayName: '额外二', totalMinutes: 4, percentage: 1 },
  ],
};

test('buildAppUsageBarOption renders horizontal top-8 bar with category colors and right minutes label', () => {
  const option = buildAppUsageBarOption(appUsage) as any;
  assert.equal(option.yAxis[0].type, 'category');
  assert.equal(option.yAxis[0].inverse, true);
  assert.equal(option.yAxis[0].data.length, 8, 'should keep only the first 8 items by totalMinutes');
  assert.equal(option.yAxis[0].data[0], 'Visual Studio Code', 'should use displayName ?? appName');
  assert.equal(option.series[0].type, 'bar');
  assert.equal(option.series[0].data.length, 8);
  assert.equal(option.series[0].label.show, true);
  assert.equal(option.series[0].label.position, 'right');
  assert.equal(option.series[0].label.formatter, '{c} 分钟');
  assert.equal(option.xAxis[0].type, 'value');
  assert.equal(option.xAxis[0].show, false);
  assert.deepEqual(option.grid, { left: 8, right: 56, top: 8, bottom: 8, containLabel: true });
  assert.equal(option.series[0].data[0].itemStyle.color, chartColors.primary, 'unknown app falls back to primary');
  assert.equal(
    option.series[0].data[1].itemStyle.color,
    chartColors.category['学习'],
    'app matching a category name uses the category color'
  );
  assert.ok(option.tooltip, 'tooltip should exist');
});

test('buildAppUsageBarOption returns empty option for empty input', () => {
  const option = buildAppUsageBarOption(undefined) as any;
  assert.equal(option.yAxis[0].data.length, 0);
  assert.equal(option.series[0].data.length, 0);
});

const summary: PcSummaryResponse = {
  keystats: null,
  heatmap: [],
  appRanking: [],
  timeline: [],
  sessions: [],
  metrics: {
    totalRecordedDuration: '8h 12m',
    activeInputDuration: '5h 40m',
    idleDuration: '2h 32m',
    sessionCount: 4,
    activeAppCount: 9,
    totalKeyPresses: 1234,
    totalClicks: 456,
    appSwitchCount: 78,
    switchFrequency: 9.5,
    mostFocusedApp: 'Code.exe',
    keyClickRatio: 2.7,
  },
  categories: [
    { categoryName: '编程/折腾', color: '#6B5EE4', share: 0.62, keyPresses: 1000, totalClicks: 300 },
  ],
};

const focusBlocks: PcFocusBlocksResponse = {
  items: [
    { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 30, mainApp: 'A', topApps: [] },
    { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 82, mainApp: 'B', topApps: [] },
  ],
};

const lateNight: PcLateNightResponse = {
  items: [
    { date: '2026-08-14', minutes: 0, hadActivity: false },
    { date: '2026-08-15', minutes: 25, hadActivity: true },
    { date: '2026-08-16', minutes: 10, hadActivity: false },
  ],
};

const distribution: PcCategoryDistributionResponse = {
  items: [
    { categoryName: '编程/折腾', color: '#6B5EE4', minutes: 100, percentage: 50 },
    { categoryName: '学习', color: '#14b8a6', minutes: 80, percentage: 40 },
    { categoryName: '其他', color: '#64748b', minutes: 20, percentage: 10 },
  ],
};

test('buildReviewMetrics returns 6+ cards with focus, late night and coverage', () => {
  const metrics = buildReviewMetrics(summary, focusBlocks, lateNight, distribution, '2026-08-15');
  assert.ok(metrics.length >= 6, `expected at least 6 cards, got ${metrics.length}`);
  const byLabel = Object.fromEntries(metrics.map(m => [m.label, m]));
  assert.equal(byLabel['记录时长'].value, '8h 12m');
  assert.equal(byLabel['活跃输入'].value, '5h 40m');
  assert.equal(byLabel['主要分类'].value, '编程/折腾');
  assert.equal(byLabel['上下文切换'].value, '78');
  assert.equal(byLabel['专注块'].value, '2 段');
  assert.equal(byLabel['专注块'].helper, '最长 82 分钟');
  assert.equal(byLabel['深夜使用'].value, '25 分钟');
  assert.equal(byLabel['深夜使用'].helper, '23:30 后');
  assert.equal(byLabel['分类覆盖率'].value, '90%', 'coverage should be 100 - 其他 percentage');
});

test('buildReviewMetrics picks the late-night item matching the page business date', () => {
  const metrics = buildReviewMetrics(summary, focusBlocks, lateNight, distribution, '2026-08-16');
  const lateCard = metrics.find(m => m.label === '深夜使用');
  assert.equal(lateCard?.value, '10 分钟', 'dateStr match wins over items order');
  const unmatched = buildReviewMetrics(summary, focusBlocks, lateNight, distribution, '2026-08-17');
  assert.equal(unmatched.find(m => m.label === '深夜使用')?.value, '10 分钟', 'no date match falls back to last item');
});

test('buildReviewMetrics falls back to last item for late night when nothing had activity', () => {
  const metrics = buildReviewMetrics(
    summary,
    focusBlocks,
    { items: [{ date: '2026-08-15', minutes: 8, hadActivity: false }] },
    distribution
  );
  const lateCard = metrics.find(m => m.label === '深夜使用');
  assert.equal(lateCard?.value, '8 分钟');
});

test('buildReviewMetrics shows em dash placeholders when aggregation data is missing', () => {
  const metrics = buildReviewMetrics(summary, undefined, undefined, undefined);
  const byLabel = Object.fromEntries(metrics.map(m => [m.label, m]));
  assert.equal(byLabel['专注块'].value, '—');
  assert.equal(byLabel['专注块'].helper, '等待同步');
  assert.equal(byLabel['深夜使用'].value, '—');
  assert.equal(byLabel['分类覆盖率'].value, '—');
  assert.equal(byLabel['记录时长'].value, '8h 12m', 'summary metrics still render');
});

test('buildReviewMetrics handles distribution without 其他 entry as 100% coverage', () => {
  const noOther = buildReviewMetrics(
    summary,
    focusBlocks,
    lateNight,
    { items: [{ categoryName: '编程/折腾', color: '#6B5EE4', minutes: 100, percentage: 100 }] }
  );
  assert.equal(noOther.find(m => m.label === '分类覆盖率')?.value, '100%');
});

test('PcReviewSummary statically renders focus, late night and coverage card labels', () => {
  const html = renderToStaticMarkup(
    React.createElement(PcReviewSummary, {
      summary,
      pendingSuggestions: [],
      focusBlocks,
      lateNight,
      categoryDistribution: distribution,
      dateStr: '2026-08-15',
    })
  );
  assert.equal(html.includes('专注块'), true);
  assert.equal(html.includes('深夜使用'), true);
  assert.equal(html.includes('分类覆盖率'), true);
  assert.equal(html.includes('2 段'), true);
  assert.equal(html.includes('25 分钟'), true);
});

test('PcQualitySummary statically renders a quality ring placeholder in normal and compact modes', () => {
  const quality = {
    overallStatus: 'Healthy',
    label: '正常',
    message: 'PC 数据质量检查完成',
    checkedAt: '2026-08-15T10:00:00',
    components: [
      { key: 'c1', name: '组件一', status: 'Healthy', message: '', details: {} },
      { key: 'c2', name: '组件二', status: 'Warning', message: '', details: {} },
    ],
    issues: [],
    nextSteps: [],
  } as any;
  const normalHtml = renderToStaticMarkup(React.createElement(PcQualitySummary, { quality }));
  const compactHtml = renderToStaticMarkup(React.createElement(PcQualitySummary, { quality, compact: true }));
  assert.ok((normalHtml.match(/role="img"/g) || []).length >= 1, `normal mode should render ring placeholder, got ${normalHtml}`);
  assert.ok((compactHtml.match(/role="img"/g) || []).length >= 1, `compact mode should render ring placeholder, got ${compactHtml}`);
});

test('DailyActivityPanel statically renders app usage bar title with appUsage present', () => {
  const html = renderToStaticMarkup(
    React.createElement(DailyActivityPanel, {
      metrics: summary.metrics,
      categories: summary.categories,
      appRanking: [],
      selectedCategory: null,
      onSelectCategory: () => undefined,
      selectedApp: null,
      onSelectApp: () => undefined,
      appUsage,
    })
  );
  assert.equal(html.includes('应用时长排行'), true);
  assert.equal(html.includes('分类排行'), true, 'category ranking list must stay');
  assert.ok((html.match(/role="img"/g) || []).length >= 1, `should render chart placeholder, got ${html}`);
});

test('parseDurationToMinutes parses backend duration strings', () => {
  assert.equal(parseDurationToMinutes('8h 12m'), 492);
  assert.equal(parseDurationToMinutes('45m'), 45);
  assert.equal(parseDurationToMinutes('3h'), 180);
  assert.equal(parseDurationToMinutes('0m'), 0);
  assert.equal(parseDurationToMinutes(undefined), 0);
  assert.equal(parseDurationToMinutes(''), 0);
});

test('buildFocusGaugeOption renders gauge with focus ratio and primary-to-activity gradient', () => {
  const option = buildFocusGaugeOption(focusBlocks, summary.metrics as DerivedMetrics) as any;
  assert.equal(option.series[0].type, 'gauge');
  assert.equal(option.series[0].min, 0);
  assert.equal(option.series[0].max, 100);
  assert.equal(option.series[0].data[0].name, '专注占比');
  assert.equal(option.series[0].detail.formatter, '{value}%');
  const gradient = option.series[0].progress.itemStyle.color;
  assert.equal(gradient.type, 'linear');
  assert.equal(gradient.colorStops[0].color, chartColors.primary);
  assert.equal(gradient.colorStops[1].color, chartColors.activity);
  const expected = Math.round((112 / 492) * 100);
  assert.equal(option.series[0].data[0].value, expected, 'focus 112min / recorded 492min');
});

test('buildFocusGaugeOption clamps value to 100 and returns 0 when recorded duration is 0', () => {
  const overFocus: PcFocusBlocksResponse = {
    items: [
      { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 60, mainApp: 'A', topApps: [] },
      { startUtc: '', endUtc: '', startLocal: '', endLocal: '', durationMinutes: 60, mainApp: 'B', topApps: [] },
    ],
  };
  const clamped = buildFocusGaugeOption(overFocus, { totalRecordedDuration: '1h' } as DerivedMetrics) as any;
  assert.equal(clamped.series[0].data[0].value, 100, '120/60*100 clamps to 100');

  const zeroDenominator = buildFocusGaugeOption(focusBlocks, undefined) as any;
  assert.equal(zeroDenominator.series[0].data[0].value, 0, 'no recorded duration means 0');

  const zeroMinutes = buildFocusGaugeOption(focusBlocks, { totalRecordedDuration: '0m' } as DerivedMetrics) as any;
  assert.equal(zeroMinutes.series[0].data[0].value, 0, '0m recorded duration means 0');
});

test('buildWeeklyTrendOption plots recorded minutes per day with date x axis', () => {
  const daily: DailyProductivity[] = [
    { date: '2026-08-10', productiveMinutes: 100, distractingMinutes: 20, neutralMinutes: 30, totalMinutes: 150, productiveRatio: 0.67 },
    { date: '2026-08-11', productiveMinutes: 200, distractingMinutes: 10, neutralMinutes: 40, totalMinutes: 250, productiveRatio: 0.8 },
  ];
  const option = buildWeeklyTrendOption(daily) as any;
  assert.deepEqual(option.xAxis[0].data, ['2026-08-10', '2026-08-11']);
  assert.equal(option.series[0].type, 'bar');
  assert.deepEqual(option.series[0].data, [150, 250]);
  assert.ok(option.tooltip, 'tooltip should exist to note the recorded-duration scope');
});

test('buildWeeklyTrendOption handles empty daily input', () => {
  const option = buildWeeklyTrendOption([]) as any;
  assert.deepEqual(option.xAxis[0].data, []);
  assert.deepEqual(option.series[0].data, []);
});

test('ProductivityDashboard renders descriptive focus dashboard without subjective score words', () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // 显式注入业务日期，测试不依赖「跑在什么时刻」：组件在收到 dateStr 时用它作为
  // query key。此前用 format(new Date())（UTC 当天）当 key，而组件默认走
  // getPcBusinessDate()（Asia/Shanghai + 04:00 业务日边界），两者只在
  // CST 04:00-08:00（= UTC 20:00-24:00）相差一天，导致 CI 每天在该窗口失败。
  const today = '2026-08-15';
  qc.setQueryData(['productivity-dashboard', today], {
    todayScore: 80,
    productiveHours: 4,
    distractingHours: 1,
    neutralHours: 2,
    targetHours: 6,
    goalMet: true,
    weeklyTrend: [
      { date: '2026-08-10', productiveMinutes: 100, distractingMinutes: 20, neutralMinutes: 30, totalMinutes: 150, productiveRatio: 0.67 },
    ],
  });
  const html = renderToStaticMarkup(
    React.createElement(
      QueryClientProvider,
      { client: qc },
      React.createElement(ProductivityDashboardPanel, {
        focusBlocks,
        lateNight,
        summaryMetrics: summary.metrics,
        dateStr: today,
      })
    )
  );
  assert.equal(html.includes('专注概况'), true);
  assert.equal(html.includes('最长专注'), true);
  assert.equal(html.includes('碎片化'), true);
  assert.equal(html.includes('深夜使用'), true);
  assert.equal(html.includes('今日效率'), false, 'subjective title must be gone');
  assert.equal(html.includes('生产性'), false, 'subjective productive row must be gone');
  assert.equal(html.includes('分心'), false, 'subjective distracting row must be gone');
  const imgCount = (html.match(/role="img"/g) || []).length;
  assert.ok(imgCount >= 2, `gauge + weekly trend placeholders expected, got ${imgCount}`);
});

console.log('pcPanelCharts tests passed');
