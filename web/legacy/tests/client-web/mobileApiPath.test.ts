import assert from 'node:assert/strict';
import {
  MOBILE_DEFAULT_TIMEZONE,
  MOBILE_LIFE_CATEGORIES,
  mobileApiPaths,
} from '../../src/client-web/src/api/mobile';

const day = '2026-07-06';
const deviceId = 'phone/main';
const start = '2026-07-06T00:00:00Z';
const end = '2026-07-06T23:59:59Z';
const rangeStartUtc = '2026-07-01T16:00:00Z';
const rangeEndUtc = '2026-07-08T16:00:00Z';

const expectedLifeCategories = [
  '社交通讯',
  '短视频/娱乐',
  '游戏',
  '音乐/音频',
  '阅读/资讯',
  '学习',
  '工作/生产力',
  '工具/系统',
  '浏览器/搜索',
  '出行/地图',
  '购物/外卖',
  '金融/支付',
  '健康/运动',
  '相机/创作',
  '生活服务',
  '未分类',
] as const;

assert.equal(MOBILE_DEFAULT_TIMEZONE, 'Asia/Shanghai');
assert.deepEqual(MOBILE_LIFE_CATEGORIES, expectedLifeCategories);
assert.equal(mobileApiPaths.devices, '/mobile/devices');
assert.equal(mobileApiPaths.summary(day), '/mobile/summary?date=2026-07-06');
assert.equal(
  mobileApiPaths.summary(day, deviceId),
  '/mobile/summary?date=2026-07-06&deviceId=phone%2Fmain',
);
assert.equal(mobileApiPaths.timeline(day), '/mobile/timeline?date=2026-07-06');
assert.equal(
  mobileApiPaths.timeline(day, deviceId),
  '/mobile/timeline?date=2026-07-06&deviceId=phone%2Fmain',
);
// #330：不传分页参数时 URL 不带 page/pageSize（服务端默认页），传了就按页请求。
assert.equal(
  mobileApiPaths.timeline(day, undefined, 2, 500),
  '/mobile/timeline?date=2026-07-06&page=2&pageSize=500',
);
assert.equal(
  mobileApiPaths.timeline(day, deviceId, 1),
  '/mobile/timeline?date=2026-07-06&deviceId=phone%2Fmain&page=1',
);
assert.equal(
  mobileApiPaths.locations(start, end),
  '/mobile/location/history?start=2026-07-06T00%3A00%3A00Z&end=2026-07-06T23%3A59%3A59Z&maxAccuracyMeters=50',
);
assert.equal(
  mobileApiPaths.locationHistory({ start, end, deviceId, maxAccuracyMeters: 25 }),
  '/mobile/location/history?start=2026-07-06T00%3A00%3A00Z&end=2026-07-06T23%3A59%3A59Z&maxAccuracyMeters=25&deviceId=phone%2Fmain',
);
assert.equal(
  mobileApiPaths.locationAnalyticsOverview({
    rangeStartUtc,
    rangeEndUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    maxAccuracyMeters: 50,
    includeRejected: false,
  }),
  '/mobile/location/analytics/overview?rangeStartUtc=2026-07-01T16%3A00%3A00Z&rangeEndUtc=2026-07-08T16%3A00%3A00Z&timezone=Asia%2FShanghai&maxAccuracyMeters=50&includeRejected=false',
);
assert.equal(
  mobileApiPaths.locationAnalyticsTracks({ timezone: MOBILE_DEFAULT_TIMEZONE }),
  '/mobile/location/analytics/tracks?timezone=Asia%2FShanghai',
);
assert.equal(
  mobileApiPaths.locationAnalyticsSegment('segment/一'),
  '/mobile/location/analytics/segments/segment%2F%E4%B8%80',
);
assert.equal(
  mobileApiPaths.locationAnalyticsSegmentPoints('segment/一', { pageSize: 20 }),
  '/mobile/location/analytics/segments/segment%2F%E4%B8%80/points?pageSize=20',
);
assert.equal(mobileApiPaths.quality(), '/mobile/quality');
assert.equal(
  mobileApiPaths.quality(day, deviceId),
  '/mobile/quality?date=2026-07-06&deviceId=phone%2Fmain',
);
assert.equal(
  mobileApiPaths.analyticsOverview({
    rangeStartUtc,
    rangeEndUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    deviceId: '',
    category: null,
    packageName: undefined,
  }),
  '/mobile/analytics/overview?rangeStartUtc=2026-07-01T16%3A00%3A00Z&rangeEndUtc=2026-07-08T16%3A00%3A00Z&timezone=Asia%2FShanghai',
);
assert.equal(
  mobileApiPaths.analyticsHeatmap({
    rangeStartUtc,
    rangeEndUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    category: '社交通讯',
    includeSystemNoise: false,
    granularity: '15m',
  }),
  '/mobile/analytics/heatmap?rangeStartUtc=2026-07-01T16%3A00%3A00Z&rangeEndUtc=2026-07-08T16%3A00%3A00Z&timezone=Asia%2FShanghai&category=%E7%A4%BE%E4%BA%A4%E9%80%9A%E8%AE%AF&includeSystemNoise=false&granularity=15m',
);
assert.equal(
  mobileApiPaths.analyticsTimelineBlocks({
    timezone: MOBILE_DEFAULT_TIMEZONE,
    includeSystemNoise: false,
    page: 2,
    pageSize: 50,
  }),
  '/mobile/analytics/timeline-blocks?timezone=Asia%2FShanghai&includeSystemNoise=false&page=2&pageSize=50',
);
assert.equal(
  mobileApiPaths.timelineBlockSessions('block/一', {
    timezone: MOBILE_DEFAULT_TIMEZONE,
    minDurationSeconds: 1,
  }),
  '/mobile/analytics/timeline-blocks/block%2F%E4%B8%80/sessions?timezone=Asia%2FShanghai&minDurationSeconds=1',
);
assert.equal(
  mobileApiPaths.sessionEvents('session/一'),
  '/mobile/analytics/sessions/session%2F%E4%B8%80/events',
);
assert.equal(mobileApiPaths.appCatalogOverrides(), '/mobile/apps/catalog-overrides');
assert.equal(mobileApiPaths.appCategoryRules(), '/mobile/apps/category-rules');
assert.equal(
  mobileApiPaths.appCategoryRule('rule/一'),
  '/mobile/apps/category-rules/rule%2F%E4%B8%80',
);
assert.equal(mobileApiPaths.usageGoals(), '/mobile/analytics/goals');
assert.equal(
  mobileApiPaths.locationAnalyticsFrequentPlaces({
    rangeStartUtc,
    rangeEndUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    deviceId,
  }),
  '/mobile/location/analytics/frequent-places?rangeStartUtc=2026-07-01T16%3A00%3A00Z&rangeEndUtc=2026-07-08T16%3A00%3A00Z&timezone=Asia%2FShanghai&deviceId=phone%2Fmain',
);
assert.equal(
  mobileApiPaths.locationAnalyticsMovementStats({
    rangeStartUtc,
    timezone: MOBILE_DEFAULT_TIMEZONE,
    deviceId,
  }),
  '/mobile/location/analytics/movement-stats?rangeStartUtc=2026-07-01T16%3A00%3A00Z&timezone=Asia%2FShanghai&deviceId=phone%2Fmain',
);
assert.equal(
  mobileApiPaths.locationAnalyticsMovementStats({ timezone: MOBILE_DEFAULT_TIMEZONE }),
  '/mobile/location/analytics/movement-stats?timezone=Asia%2FShanghai',
);
