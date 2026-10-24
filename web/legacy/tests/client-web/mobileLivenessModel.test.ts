import assert from 'node:assert/strict';
import type { MobileLivenessOverview } from '../../src/client-web/src/api/mobile';
import {
  FULFILLMENT_DEFINITION,
  formatCoverage,
  formatFulfillment,
  formatPayload,
  silenceSeverityClass,
  groupDeviceBlocks,
} from '../../src/client-web/src/components/mobile/deviceLivenessModel';

assert.equal(formatCoverage(0.107), '10.7%');
assert.equal(formatCoverage(null), '—');
assert.equal(silenceSeverityClass('warning'), 'bg-amber-100 text-amber-900');
assert.equal(silenceSeverityClass('critical'), 'bg-red-100 text-red-900');
assert.equal(silenceSeverityClass('none'), '');
assert.equal(formatPayload('{"a":1}'), '{\n  "a": 1\n}');
assert.equal(formatPayload('不是 JSON'), '不是 JSON');

const groups = groupDeviceBlocks({
  rangeStartUtc: '',
  rangeEndUtc: '',
  expectedHeartbeatIntervalMinutes: 15,
  phones: [],
  tablets: [],
  unclassified: [],
} satisfies MobileLivenessOverview);
assert.deepEqual(groups.map(group => group.title), ['手机', '平板', '未分类机型']);
assert.deepEqual(groups.map(group => group.devices.length), [0, 0, 0]);

// ── REQ-18 兑现率（AC-18.2 / AC-18.3）──
// 最关键的一条：没有闹钟数据时必须是空态，**不得显示 0%**——
// 0% 会被读成「保活完全失效」，而实际只是这台设备还没有闹钟数据。
assert.equal(formatFulfillment(null), '无数据（区间内没有已执行的叫醒）');
assert.equal(
  formatFulfillment({ rate: null, fulfilled: 0, considered: 0, excludedNoActualTime: 3 }),
  '无数据（区间内没有已执行的叫醒）',
);
assert.ok(
  !formatFulfillment(null).includes('0%'),
  '无数据不得显示成 0%（AC-18.2）',
);

// 确实执行但都不按时 → 这才是真的 0%
assert.equal(formatFulfillment({ rate: 0, fulfilled: 0, considered: 2, excludedNoActualTime: 0 }), '0.0%（0/2 次按时）');

// 正常情况一并给出分子/分母，便于复算
assert.equal(formatFulfillment({ rate: 0.5, fulfilled: 1, considered: 2, excludedNoActualTime: 0 }), '50.0%（1/2 次按时）');

// AC-18.3：口径说明必须写明分母排除规则
assert.ok(FULFILLMENT_DEFINITION.includes('不计入分母'), '口径说明必须写明未执行不进分母');

console.error('PASS: mobileLivenessModel');
