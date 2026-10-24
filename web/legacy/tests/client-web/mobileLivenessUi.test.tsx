import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import type { MobileLivenessOverview } from '../../src/client-web/src/api/mobile';
import { DeviceLivenessGroups } from '../../src/client-web/src/components/mobile/DeviceLivenessPanel';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

const overview: MobileLivenessOverview = {
  rangeStartUtc: '2026-09-01T00:00:00Z', rangeEndUtc: '2026-09-08T00:00:00Z',
  expectedHeartbeatIntervalMinutes: 15,
  phones: [{
    deviceId: 'phone-1', displayName: '主力手机', deviceKind: 'phone', deviceKindLabel: '手机',
    hasData: true, conclusion: '最近一周设备存活稳定。', coverageByHour: 0.107,
    coverageByExpectedHeartbeat: 0.074, observedHours: 36, totalHours: 336,
    observedHeartbeats: 50, expectedHeartbeats: 672, expectedHeartbeatIntervalMinutes: 15,
    longestSilenceMinutes: 90, longestSilenceStartUtc: '2026-09-03T01:00:00Z',
    longestSilenceEndUtc: '2026-09-03T02:30:00Z', longestSilenceSeverity: 'critical',
    hasSilenceOverOneHour: true,
    silences: [{ startUtc: '2026-09-03T01:00:00Z', endUtc: '2026-09-03T02:30:00Z', minutes: 90, severity: 'critical', severityLabel: '严重' }],
    causes: [{ cause: 'unknown', label: '未知', count: 2, inference: '推断依据：同期未收到进程退出事件。' }],
    lastEventAtUtc: '2026-09-07T20:00:00Z', coverageByHourDefinition: '每小时至少收到一次心跳的小时比例。',
    coverageByExpectedHeartbeatDefinition: '收到心跳数除以预期心跳数。',
    fulfillment: { rate: 0.5, fulfilled: 1, considered: 2, excludedNoActualTime: 1 },
  }],
  tablets: [],
  unclassified: [{
    deviceId: 'unknown-1', displayName: '未分类设备', deviceKind: 'unknown', deviceKindLabel: '未分类机型',
    hasData: false, conclusion: '所选范围内未上报。', coverageByHour: null,
    coverageByExpectedHeartbeat: null, observedHours: 0, totalHours: 168, observedHeartbeats: 0,
    expectedHeartbeats: 672, expectedHeartbeatIntervalMinutes: 15, longestSilenceMinutes: 0,
    longestSilenceStartUtc: null, longestSilenceEndUtc: null, longestSilenceSeverity: 'none',
    hasSilenceOverOneHour: false, silences: [], causes: [], lastEventAtUtc: null,
    coverageByHourDefinition: '小时定义。', coverageByExpectedHeartbeatDefinition: '心跳定义。',
    // 无闹钟数据 -> 必须渲染空态，而不是 0%（AC-18.2）
    fulfillment: null,
  }],
};

const markup = renderToStaticMarkup(React.createElement(DeviceLivenessGroups, { overview }));
for (const text of ['最近一周设备存活稳定。', '手机', '平板', '未分类机型', '10.7%（36/336 小时）', '7.4%（50/672 次）', '每小时至少收到一次心跳的小时比例。', '收到心跳数除以预期心跳数。', '未知', '推断依据：同期未收到进程退出事件。', '无数据/未上报', '—']) {
  assert.ok(markup.includes(text), `设备存活页面应显示「${text}」`);
}
assert.ok(markup.includes('data-severity="critical"'), '严重静默应带红色等级标记');

// ── REQ-18 兑现率在页面上的呈现（AC-18.2 / AC-18.3）──
assert.ok(markup.includes('叫醒兑现率'), '存活页应显示兑现率一项');
assert.ok(markup.includes('50.0%（1/2 次按时）'), '有数据时应显示兑现率与分子分母');
assert.ok(markup.includes('无数据（区间内没有已执行的叫醒）'), '无闹钟数据的设备应显示空态');
assert.ok(markup.includes('不计入分母'), '页面应写明代谢率口径（排除未执行）');
// 关键反面：整页不得把「无数据」渲染成 0%
assert.ok(!markup.includes('0.0%（0/0 次按时）'), '无数据不得显示为 0%（AC-18.2）');

console.error('PASS: mobileLivenessUi');
