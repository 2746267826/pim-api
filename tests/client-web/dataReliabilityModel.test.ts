import assert from 'node:assert/strict';
import type {
  DataReliabilityRuleReport,
  S2ThreeStateDistribution,
} from '../../src/client-web/src/api/dataReliabilityTypes';
import type { DataReliabilityInspectionReport } from '../../src/client-web/src/api/dataReliabilityTypes';
import {
  buildLedgerLine,
  buildOverviewCounts,
  dataReliabilityStaleAfterHours,
  formatAssessmentWindow,
  formatLedgerTimestamp,
  isInspectionStale,
  buildThreeStateBuckets,
  buildViolationExportFileName,
  describeTrend,
  formatCurrentValue,
  formatDurationSeconds,
  formatFreshness,
  groupRules,
  isRedStatus,
  normalizeStatus,
  serializeViolationExport,
  statusPresentation,
} from '../../src/client-web/src/components/data-reliability/dataReliabilityModel';

function rule(overrides: Partial<DataReliabilityRuleReport>): DataReliabilityRuleReport {
  return {
    code: 'S1',
    invariantCode: 'INV-P16',
    key: 'S1_INV-P16',
    order: 1,
    name: '同类型事件不重叠',
    group: 'SelfConsistency',
    groupLabel: '数据自洽',
    status: 'green',
    statusLabel: '绿',
    detail: '',
    currentValue: null,
    currentValueUnit: null,
    currentValueLabel: null,
    threshold: '',
    criterion: '',
    rationale: '',
    relatedIssues: [],
    totalViolations: 0,
    windowViolations: 0,
    historicalViolations: 0,
    earliestOccurrenceUtc: null,
    latestOccurrenceUtc: null,
    samples: [],
    thresholdFallback: false,
    thresholdNote: null,
    coveredLayers: null,
    trend: 'unknown',
    trendDelta: null,
    trendBaselineUtc: null,
    threeState: null,
    scanTruncated: false,
    scanCoveredDays: null,
    ...overrides,
  };
}

function report(overrides: Partial<DataReliabilityInspectionReport> = {}): DataReliabilityInspectionReport {
  return {
    inspectedAtUtc: '2026-09-14T10:00:00Z',
    version: 1,
    elapsedMilliseconds: 1,
    status: 'green',
    redCount: 0,
    yellowCount: 0,
    greenCount: 13,
    unknownCount: 0,
    totalViolations: 0,
    windowViolations: 0,
    historicalViolations: 0,
    notices: {},
    rules: [rule({})],
    message: '',
    assessmentStartUtc: '2026-09-07T10:00:00Z',
    assessmentWindowHours: 168,
    ...overrides,
  };
}

// ---- 状态映射 ----
assert.equal(normalizeStatus('RED'), 'red');
assert.equal(normalizeStatus('yellow'), 'yellow');
assert.equal(normalizeStatus(null), 'unknown');
assert.equal(normalizeStatus('莫名其妙的字符串'), 'unknown');
assert.equal(isRedStatus('red'), true);
assert.equal(isRedStatus('green'), false);
// 徽标必须同时给出图标与中文标签，不能只靠颜色区分。
assert.deepEqual(statusPresentation('red'), { icon: '🔴', label: '红', tone: 'danger' });
assert.deepEqual(statusPresentation('yellow'), { icon: '🟡', label: '黄', tone: 'warning' });
assert.deepEqual(statusPresentation('green'), { icon: '🟢', label: '绿', tone: 'activity' });
assert.deepEqual(statusPresentation('unknown'), { icon: '⚪', label: '未知', tone: 'neutral' });

// ---- 分组 ----
const grouped = groupRules([
  rule({ code: 'S12', order: 12, groupLabel: '链路健康' }),
  rule({ code: 'S1', order: 1, groupLabel: '数据自洽' }),
  rule({ code: 'S6', order: 6, groupLabel: '覆盖完整' }),
  rule({ code: 'S2', order: 2, groupLabel: '数据自洽' }),
]);
assert.deepEqual(grouped.map(g => g.label), ['数据自洽', '覆盖完整', '链路健康']);
assert.deepEqual(grouped[0].rules.map(r => r.code), ['S1', 'S2']);
// 未知分组不能被静默丢弃。
const groupedUnknown = groupRules([rule({ code: 'S1' }), rule({ code: 'SX', groupLabel: '新分组' })]);
assert.deepEqual(groupedUnknown.map(g => g.label), ['数据自洽', '新分组']);

// ---- 新鲜度（注入时钟）----
const now = new Date('2026-09-14T12:00:00Z');
assert.equal(formatFreshness('2026-09-14T11:59:40Z', now), '刚刚');
assert.equal(formatFreshness('2026-09-14T11:30:00Z', now), '30 分钟前');
assert.equal(formatFreshness('2026-09-14T09:00:00Z', now), '3 小时前');
assert.equal(formatFreshness('2026-09-12T12:00:00Z', now), '2 天前');
// 浏览器与服务端时钟偏差导致的"未来"时间不能显示成负数。
assert.equal(formatFreshness('2026-09-14T12:05:00Z', now), '刚刚');
assert.equal(formatFreshness(null, now), '未知');
assert.equal(formatFreshness('不是时间', now), '未知');

// ---- 过期判定（与后端 DataReliabilityGate 的 26 小时窗口一致）----
assert.equal(dataReliabilityStaleAfterHours, 26);
assert.equal(isInspectionStale('2026-09-14T11:00:00Z', now), false);
assert.equal(isInspectionStale('2026-09-13T09:00:00Z', now), true);
assert.equal(isInspectionStale(null, now), false);
assert.equal(isInspectionStale('不是时间', now), false);
// 阈值可注入，避免测试依赖真实时钟。
assert.equal(isInspectionStale('2026-09-14T11:00:00Z', now, 2), false);
assert.equal(isInspectionStale('2026-09-14T09:00:00Z', now, 2), true);

// ---- 时长与当前值 ----
assert.equal(formatDurationSeconds(0), '0 分钟');
assert.equal(formatDurationSeconds(90), '2 分钟');
assert.equal(formatDurationSeconds(3600), '1 小时');
assert.equal(formatDurationSeconds(5400), '1 小时 30 分钟');

assert.equal(formatCurrentValue(rule({ currentValue: 12, currentValueUnit: '对' })), '12 对');
assert.equal(formatCurrentValue(rule({ currentValue: 0.873, currentValueLabel: '87.3%' })), '87.3%');
assert.equal(formatCurrentValue(rule({ currentValue: null })), '暂无');

// ---- 历史欠账趋势（旧术语「存量」已退役）----
const decreasing = describeTrend(
  rule({ trend: 'decreasing', trendDelta: -12, trendBaselineUtc: '2026-09-14T10:00:00Z' })
);
assert.ok(decreasing.startsWith('历史欠账较'), `decreasing 文案应以"历史欠账较"开头: ${decreasing}`);
assert.ok(decreasing.includes('减少 12 条'), `decreasing 文案应包含减量: ${decreasing}`);
assert.ok(decreasing.includes('消化'), `decreasing 文案应说明欠账在消化: ${decreasing}`);
assert.ok(
  describeTrend(rule({ trend: 'increasing', trendDelta: 5 })).includes('增加 5 条'),
  'increasing 趋势文案应包含增量'
);
assert.ok(describeTrend(rule({ trend: 'flat', trendDelta: 0, historicalViolations: 7 })).includes('持平'));
assert.ok(describeTrend(rule({ trend: 'unknown' })).includes('暂无对比基线'));
for (const trend of ['decreasing', 'increasing', 'flat', 'unknown'] as const) {
  const text = describeTrend(rule({ trend }));
  assert.ok(!text.includes('存量'), `趋势文案不得再出现「存量」: ${text}`);
}

// ---- 考核账本信息行（AC-7.1：逐字对照工单 §五）----
assert.equal(formatAssessmentWindow(168), '最近 7 天');
assert.equal(formatAssessmentWindow(24), '最近 1 天');
assert.equal(formatAssessmentWindow(36), '最近 36 小时');
assert.equal(formatAssessmentWindow(null), '未配置');
// 账本时刻按**浏览器本地时区**渲染（与"本次体检时间"一致），因此断言格式 + 与本地时间分量一致，
// 而不是把 UTC 字面量写死（否则容器时区一变用例就假红）。
const ledgerAt = new Date('2026-09-07T02:30:00Z');
const pad2 = (part: number) => String(part).padStart(2, '0');
const expectedLedger = `${ledgerAt.getFullYear()}-${pad2(ledgerAt.getMonth() + 1)}-${pad2(ledgerAt.getDate())} ` +
  `${pad2(ledgerAt.getHours())}:${pad2(ledgerAt.getMinutes())}`;
assert.equal(formatLedgerTimestamp('2026-09-07T02:30:00Z'), expectedLedger);
assert.match(formatLedgerTimestamp('2026-09-07T02:30:00Z'), /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
assert.equal(formatLedgerTimestamp(null), '未知');

const ledgerReport = report({
  inspectedAtUtc: '2026-09-14T10:00:00Z',
  assessmentStartUtc: '2026-09-07T02:30:00Z',
  assessmentWindowHours: 168,
});
const ledgerLine = buildLedgerLine(ledgerReport);
assert.ok(ledgerLine.startsWith(`考核账本自 ${expectedLedger} 起`), `账本行前缀不符: ${ledgerLine}`);
assert.ok(ledgerLine.includes('容器更新/重启后自动重置'), `账本行必须说明重启自动重置: ${ledgerLine}`);
assert.ok(ledgerLine.includes('考核窗：最近 7 天'), `账本行必须给出考核窗时长: ${ledgerLine}`);

// ---- S2 三态分布 ----
const distribution: S2ThreeStateDistribution = {
  inputActiveSeconds: 159 * 60,
  mediaActiveSeconds: 90 * 60,
  suspectedUnclosedSeconds: 444 * 60,
  totalSeconds: (159 + 90 + 444) * 60,
  inputActiveCount: 1,
  mediaActiveCount: 1,
  suspectedUnclosedCount: 1,
  declaredGapSeconds: 536 * 60,
};
const buckets = buildThreeStateBuckets(distribution);
assert.deepEqual(buckets.map(b => b.label), ['操作活跃', '观看活跃', '疑似未收尾']);
assert.equal(Math.round(buckets[0].seconds / 60), 159);
assert.ok(Math.abs(buckets[2].percent - (444 / 693) * 100) < 0.001, '疑似未收尾占比应基于三态合计');
assert.ok(Math.abs(buckets.reduce((sum, b) => sum + b.percent, 0) - 100) < 0.001, '三态占比应合计 100%');

// 全为 0 时不能出现 NaN 宽度。
const zeroBuckets = buildThreeStateBuckets({ ...distribution, inputActiveSeconds: 0, mediaActiveSeconds: 0, suspectedUnclosedSeconds: 0, totalSeconds: 0 });
assert.deepEqual(zeroBuckets.map(b => b.percent), [0, 0, 0]);

// ---- 导出 ----
assert.equal(buildViolationExportFileName('S1', new Date('2026-09-14T12:34:56Z')), 'data-reliability-S1-violations-2026-09-14-12-34-56.json');
const serialized = serializeViolationExport({
  ruleCode: 'S1',
  generatedAtUtc: '2026-09-14T12:00:00Z',
  totalCount: 1,
  truncated: false,
  items: [{ ruleCode: 'S1', id: '7', deviceId: 'dev', occurredAtUtc: '2026-09-14T01:00:00Z', fields: { eventType: 'window' }, isNew: false }],
});
assert.ok(serialized.includes('"id": "7"'), '导出内容应包含违规 ID');
assert.ok(serialized.includes('"eventType": "window"'), '导出内容应包含关键字段');

// ---- 总览计数 ----
const counts = buildOverviewCounts({
  inspectedAtUtc: '2026-09-14T12:00:00Z',
  version: 1,
  elapsedMilliseconds: 1,
  status: 'red',
  redCount: 9,
  yellowCount: 0,
  greenCount: 3,
  unknownCount: 1,
  totalViolations: 20,
  windowViolations: 2,
  historicalViolations: 18,
  notices: {},
  rules: [rule({}), rule({ code: 'S2' })],
  message: '',
  assessmentStartUtc: '2026-09-07T12:00:00Z',
  assessmentWindowHours: 168,
});
assert.deepEqual(counts, { red: 9, yellow: 0, green: 3, unknown: 1, total: 2 });

console.error('PASS: dataReliabilityModel');
