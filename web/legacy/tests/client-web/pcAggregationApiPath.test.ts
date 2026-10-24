import assert from 'node:assert/strict';
import { pcAggregationApiPaths } from '../../src/client-web/src/api/pcTracker';

// 1. focusBlocks 单日 date 参数
assert.equal(
  pcAggregationApiPaths.focusBlocks({ date: '2026-08-15' }),
  '/pc/aggregation/focus-blocks?date=2026-08-15'
);

// 2. appUsage limit 参数
const appUsagePath = pcAggregationApiPaths.appUsage({ date: '2026-08-15', limit: 8 });
assert.ok(appUsagePath.includes('limit=8'), `appUsage path should contain limit=8, got ${appUsagePath}`);

// 3. lateNight start/end 范围参数
const lateNightPath = pcAggregationApiPaths.lateNight({ start: '2026-08-01', end: '2026-08-15' });
assert.ok(lateNightPath.includes('start=2026-08-01'), `lateNight path should contain start, got ${lateNightPath}`);
assert.ok(lateNightPath.includes('end=2026-08-15'), `lateNight path should contain end, got ${lateNightPath}`);

// 4. categoryDistribution timezone 参数（URL 编码由 URLSearchParams 处理，服务端解码）
const categoryPath = pcAggregationApiPaths.categoryDistribution({ date: '2026-08-15', timezone: 'Asia/Shanghai' });
assert.ok(categoryPath.includes('timezone='), `category path should contain timezone param, got ${categoryPath}`);
assert.ok(categoryPath.includes('Asia%2FShanghai'), `timezone value should be URL-encoded, got ${categoryPath}`);

// 5. 空参数对象不产生多余 ?
assert.equal(pcAggregationApiPaths.focusBlocks({}), '/pc/aggregation/focus-blocks');
assert.equal(pcAggregationApiPaths.appUsage({}), '/pc/aggregation/app-usage');
assert.equal(pcAggregationApiPaths.lateNight({}), '/pc/aggregation/late-night');
assert.equal(pcAggregationApiPaths.categoryDistribution({}), '/pc/aggregation/category-distribution');

console.log('pcAggregationApiPath tests passed');
