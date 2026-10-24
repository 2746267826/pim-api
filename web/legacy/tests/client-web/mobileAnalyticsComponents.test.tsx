import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import type {
  MobileAnalyticsChart,
  MobileAnalyticsOverview,
  MobileDevice,
  MobileHeatmapBucket,
  MobileSessionEvent,
  MobileTimelineBlock,
  MobileTimelineBlockSession,
} from '../../src/client-web/src/api/mobile';
import MobileAnalyticsHeader from '../../src/client-web/src/components/mobile/MobileAnalyticsHeader';
import MobileInsightStrip from '../../src/client-web/src/components/mobile/MobileInsightStrip';
import MobileUsageHeatmap from '../../src/client-web/src/components/mobile/MobileUsageHeatmap';
import MobileUsageBucketDetail from '../../src/client-web/src/components/mobile/MobileUsageBucketDetail';
import { buildHeatmapMatrix } from '../../src/client-web/src/components/mobile/mobileHeatmapMatrix';
import MobileChartsGrid from '../../src/client-web/src/components/mobile/MobileChartsGrid';
import MobileTimelineBlocks from '../../src/client-web/src/components/mobile/MobileTimelineBlocks';
import MobileAnomalyPanel from '../../src/client-web/src/components/mobile/MobileAnomalyPanel';
import { buildAnalyticsChartOption } from '../../src/client-web/src/components/charts/mobileChartOptions';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

function test(_name: string, run: () => void) {
  run();
}

const device: MobileDevice = {
  id: 'device-row-1',
  deviceId: 'pixel-8',
  androidIdHash: 'hash-1',
  displayName: 'Pixel 8',
  manufacturer: 'Google',
  brand: 'google',
  model: 'Pixel 8',
  androidVersion: '16',
  sdkInt: 36,
  appVersion: '1.0.0',
  metadataJson: '{}',
  firstSeenAt: '2026-07-01T00:00:00Z',
  lastSeenAt: '2026-07-07T02:00:00Z',
  lastHeartbeatAt: '2026-07-07T02:01:00Z',
  lastSyncAt: '2026-07-07T02:02:00Z',
  isActive: true,
};

const overview: MobileAnalyticsOverview = {
  range: {
    rangeStartUtc: '2026-06-30T16:00:00.000Z',
    rangeEndUtc: '2026-07-07T16:00:00.000Z',
    timezone: 'Asia/Shanghai',
    localStartDate: '2026-07-01',
    localEndDate: '2026-07-07',
  },
  generatedAt: '2026-07-07T02:00:00Z',
  isStale: false,
  totalForegroundSeconds: 21600,
  dailyAverageSeconds: 3085,
  previousPeriodChange: 0.12,
  highestUseLocalDate: '2026-07-06',
  peakLocalHour: 22,
  appCount: 18,
  switchOrPickupCount: 96,
  completeness: 0.94,
  quality: {
    usageEventsCoverage: 0.91,
    fallbackShare: 0.08,
    missingMetadataAppCount: 2,
    systemNoiseShare: 0.03,
    shortEventShare: 0.05,
    failedOrPartialSyncBatchCount: 1,
    lastSyncAt: '2026-07-07T01:59:00Z',
    qualityFlags: ['fallback-present'],
  },
  goalProgress: {
    key: 'daily-total',
    label: '每日手机总量',
    limitSeconds: 7200,
    usedSeconds: 7800,
    isOverLimit: true,
    remainingSeconds: -600,
  },
  anomalies: [
    {
      code: 'late-night-spike',
      severity: 'Warning',
      title: '深夜使用偏高',
      evidence: '22 点后使用 1 小时 20 分钟',
      drilldownTarget: 'heatmap:night',
    },
  ],
  suggestions: [
    {
      code: 'review-short-video',
      text: '短视频在夜间集中出现，建议调低提醒频率。',
      drilldownTarget: 'category:短视频/娱乐',
    },
  ],
};

const buckets: MobileHeatmapBucket[] = [
  {
    bucketStartUtc: '2026-07-06T14:00:00.000Z',
    bucketEndUtc: '2026-07-06T15:00:00.000Z',
    localDate: '2026-07-06',
    localHour: 22,
    lifeCategory: '短视频/娱乐',
    foregroundSeconds: 2400,
    qualityFlags: [],
  },
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

const charts: MobileAnalyticsChart[] = [
  {
    key: 'category-share',
    title: '分类占比',
    chartType: 'category-share',
    unit: 'seconds',
    points: [
      { key: 'social', label: '社交通讯', value: 3600, lifeCategory: '社交通讯' },
      { key: 'work', label: '工作/生产力', value: 2400, lifeCategory: '工作/生产力' },
    ],
  },
  {
    key: 'top-apps',
    title: 'Top App',
    chartType: 'top-apps',
    unit: 'seconds',
    points: [
      { key: 'wechat', label: '微信', value: 3600, packageName: 'com.tencent.mm' },
      { key: 'reader', label: '阅读器', value: 1500, packageName: 'com.example.reader' },
    ],
  },
  {
    key: 'daily-total',
    title: '每日趋势',
    chartType: 'daily-total',
    unit: 'seconds',
    points: [
      { key: '2026-07-06', label: '7月6日', value: 7200, localDate: '2026-07-06' },
      { key: '2026-07-07', label: '7月7日', value: 5400, localDate: '2026-07-07' },
    ],
  },
  {
    key: 'hour-distribution',
    title: '小时分布',
    chartType: 'hour-distribution',
    unit: 'seconds',
    points: [
      { key: '09', label: '09:00', value: 900, localHour: 9 },
      { key: '22', label: '22:00', value: 2400, localHour: 22 },
    ],
  },
  {
    key: 'category-trend',
    title: '分类趋势',
    chartType: 'category-trend',
    unit: 'seconds',
    points: [
      { key: 'work-1', label: '工作', value: 1800, lifeCategory: '工作/生产力' },
      { key: 'video-1', label: '短视频', value: 2400, lifeCategory: '短视频/娱乐' },
    ],
  },
  {
    key: 'switch-trend',
    title: '切换趋势',
    chartType: 'switch-trend',
    unit: 'count',
    points: [
      { key: '2026-07-06', label: '7月6日', value: 42, localDate: '2026-07-06' },
      { key: '2026-07-07', label: '7月7日', value: 38, localDate: '2026-07-07' },
    ],
  },
];

const block: MobileTimelineBlock = {
  id: 'block-1',
  startUtc: '2026-07-06T14:00:00.000Z',
  endUtc: '2026-07-06T15:00:00.000Z',
  localStart: '2026-07-06 22:00',
  localEnd: '2026-07-06 23:00',
  lifeCategory: '短视频/娱乐',
  foregroundSeconds: 2400,
  sessionCount: 2,
  appCount: 1,
  topApps: [
    { packageName: 'com.ss.android.ugc.aweme', displayName: '抖音', foregroundSeconds: 2400 },
  ],
  qualityFlags: ['fallback'],
  sourceMix: { events: 1800, fallback: 600 },
  includesSystemNoise: false,
};

const session: MobileTimelineBlockSession = {
  id: 'session-1',
  deviceId: 'pixel-8',
  packageName: 'com.ss.android.ugc.aweme',
  displayName: '抖音',
  startUtc: '2026-07-06T14:05:00.000Z',
  endUtc: '2026-07-06T14:45:00.000Z',
  durationSeconds: 2400,
  lifeCategory: '短视频/娱乐',
  source: 'events',
  confidence: 0.96,
  qualityFlags: [],
};

const event: MobileSessionEvent = {
  id: 'event-1',
  sessionId: 'session-1',
  deviceId: 'pixel-8',
  packageName: 'com.ss.android.ugc.aweme',
  eventType: 'MOVE_TO_FOREGROUND',
  eventTimeUtc: '2026-07-06T14:05:00.000Z',
  className: 'MainActivity',
  rawJson: '{"eventType":"MOVE_TO_FOREGROUND"}',
};

test('mobile analytics workbench renders real Chinese copy and all major panels', () => {
  const html = [
    renderToStaticMarkup(
      React.createElement(MobileAnalyticsHeader, {
        rangeShortcut: '7d',
        rangeStartDate: '2026-07-01',
        rangeEndDate: '2026-07-07',
        selectedDeviceId: device.deviceId,
        devices: [device],
        selectedCategory: '',
        packageName: '',
        includeSystemNoise: false,
        isFetching: false,
        onShortcutChange: () => undefined,
        onCustomRangeChange: () => undefined,
        onDeviceChange: () => undefined,
        onCategoryChange: () => undefined,
        onPackageNameChange: () => undefined,
        onIncludeSystemNoiseChange: () => undefined,
        onRefresh: () => undefined,
      }),
    ),
    renderToStaticMarkup(React.createElement(MobileInsightStrip, { overview })),
    renderToStaticMarkup(
      React.createElement(MobileUsageHeatmap, {
        buckets,
        granularity: 'hour',
        isLoading: false,
        onGranularityChange: () => undefined,
        onBucketSelect: () => undefined,
      }),
    ),
    renderToStaticMarkup(
      React.createElement(MobileUsageBucketDetail, {
        cell: buildHeatmapMatrix(buckets).days[0].cells[22],
      }),
    ),
    renderToStaticMarkup(React.createElement(MobileChartsGrid, { charts, isLoading: false })),
    renderToStaticMarkup(
      React.createElement(MobileTimelineBlocks, {
        blocks: [block],
        sessionsByBlock: { [block.id]: [session] },
        eventsBySession: { [session.id]: [event] },
        expandedBlockId: block.id,
        expandedSessionId: session.id,
        page: 2,
        pageSize: 20,
        totalCount: 45,
        totalPages: 3,
        isLoading: false,
        onToggleBlock: () => undefined,
        onToggleSession: () => undefined,
        onPageChange: () => undefined,
        onPageSizeChange: () => undefined,
      }),
    ),
    renderToStaticMarkup(
      React.createElement(MobileAnomalyPanel, {
        anomalies: overview.anomalies,
        suggestions: overview.suggestions,
        quality: overview.quality,
        isLoading: false,
      }),
    ),
  ].join('\n');

  for (const text of [
    '手机记录',
    '今天',
    '7天',
    '30天',
    '自定义',
    '北京时间',
    '设备',
    '分类',
    'App',
    '噪声',
    '粒度',
    '范围',
    '总使用时长',
    '日均',
    '目标',
    'App 数',
    '使用热力图',
    '左侧是日期，顶部是小时',
    '选中时段',
    '40分钟',
    '分类占比',
    'Top App',
    '每日趋势',
    '小时分布',
    '分类趋势',
    '切换趋势',
    '异常与建议',
    '使用时间线',
  ]) {
    assert.equal(html.includes(text), true, `analytics UI should include: ${text}`);
  }

  assert.equal(html.includes('com.tencent.mm'), false, 'Top App 包名移到 ECharts 数据层，不再出现在静态 HTML');
  const topAppsOption = buildAnalyticsChartOption(charts.find(chart => chart.chartType === 'top-apps')!) as any;
  assert.equal(
    topAppsOption.series[0].data[0].packageName,
    'com.tencent.mm',
    'Top App 数据项携带包名供点击反查',
  );
  assert.equal(html.includes('深夜使用偏高'), true);
  assert.equal(html.includes('抖音'), true);
  assert.equal(html.includes('原始事件'), true);
  assert.equal(html.includes('第 2 / 3 页'), true);
  assert.equal(html.includes('加载更多'), false);
  assert.equal(html.includes('重复小时数字墙'), false);
  assert.equal(html.includes('\u93B5\u5B2B\u6E80'), false, 'new analytics UI should not render mojibake mobile labels');
});
