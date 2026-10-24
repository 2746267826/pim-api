import assert from 'node:assert/strict';
import {
  addPcDays,
  addPcMonths,
  formatPcDate,
  formatPcDateCn,
  getPcBusinessDate,
} from '../../src/client-web/src/utils/pcBusinessDay';

function test(name: string, run: () => void) { run(); }

test('getPcBusinessDate shifts to previous day before 04:00 Shanghai', () => {
  // 上海 03:59 = 前日 19:59Z（即 08-16 03:59 → 业务日 08-15）；上海 04:00 = 当日 20:00Z（业务日 08-16）
  const beforeCut = getPcBusinessDate(new Date('2026-08-15T19:59:00Z'));
  assert.equal(formatPcDate(beforeCut), '2026-08-15');
  const atCut = getPcBusinessDate(new Date('2026-08-15T20:00:00Z'));
  assert.equal(formatPcDate(atCut), '2026-08-16');
});

test('getPcBusinessDate handles month and year boundaries', () => {
  // 上海 01-01 02:00 = 2025-12-31T18:00Z → 业务日 2025-12-31
  const newYearEarly = getPcBusinessDate(new Date('2025-12-31T18:00:00Z'));
  assert.equal(formatPcDate(newYearEarly), '2025-12-31');
  // 上海 02-01 02:00 = 2026-01-31T18:00Z → 业务日 2026-01-31
  const monthStartEarly = getPcBusinessDate(new Date('2026-01-31T18:00:00Z'));
  assert.equal(formatPcDate(monthStartEarly), '2026-01-31');
});

test('formatPcDateCn renders weekday from UTC fields', () => {
  // 2026-08-16 是星期日（UTC 周日）
  assert.equal(formatPcDateCn(new Date(Date.UTC(2026, 7, 16))), '2026年8月16日 星期日');
});

test('addPcDays crosses month boundary', () => {
  assert.equal(formatPcDate(addPcDays(new Date(Date.UTC(2026, 6, 31)), 1)), '2026-08-01');
  assert.equal(formatPcDate(addPcDays(new Date(Date.UTC(2026, 7, 1)), -1)), '2026-07-31');
});

test('addPcMonths clamps to last day of target month', () => {
  assert.equal(formatPcDate(addPcMonths(new Date(Date.UTC(2024, 1, 29)), -12)), '2023-02-28');
  assert.equal(formatPcDate(addPcMonths(new Date(Date.UTC(2024, 2, 31)), -1)), '2024-02-29');
});

console.log('pcBusinessDay tests passed');
