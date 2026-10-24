import assert from 'node:assert/strict';
import {
  buildMobileAnalyticsDateRange,
  formatDuration,
  formatPercent,
  toMobileAnalyticsUtcRange,
} from '../../src/client-web/src/components/mobile/mobileFormatting';

const range = buildMobileAnalyticsDateRange('7d', new Date('2026-07-08T04:00:00.000Z'));

assert.deepEqual(range, {
  shortcut: '7d',
  startDate: '2026-07-02',
  endDate: '2026-07-08',
});

const utcRange = toMobileAnalyticsUtcRange(range);

assert.equal(utcRange.rangeStartUtc, '2026-07-01T16:00:00.000Z');
assert.equal(utcRange.rangeEndUtc, '2026-07-08T16:00:00.000Z');
assert.equal(utcRange.timezone, 'Asia/Shanghai');

assert.equal(formatDuration(52 * 60), '52分钟');
assert.equal(formatDuration(73 * 3600 + 23 * 60), '73小时23分钟');
assert.equal(formatDuration(0), '0秒');
assert.equal(formatPercent(0.68), '68%');
assert.equal(formatPercent(Number.NaN), '0%');
assert.equal(formatPercent(Number.POSITIVE_INFINITY), '0%');
