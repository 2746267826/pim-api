import type {
  DataReliabilityInspectionReport,
  DataReliabilityRuleReport,
  DataReliabilityViolationExport,
} from '../../src/client-web/src/api/dataReliabilityTypes';

// 编译期契约检查：字段名与可空性必须与后端 JSON（camelCase）一致。
// 这里刻意构造完整对象（而不是 as any），任何字段漂移都会让 tsc -p tsconfig.data-reliability.json 失败。

const rule: DataReliabilityRuleReport = {
  code: 'S1',
  invariantCode: 'INV-P16',
  key: 'S1_INV-P16',
  order: 1,
  name: '同类型事件不重叠',
  group: 'SelfConsistency',
  groupLabel: '数据自洽',
  status: 'red',
  statusLabel: '红',
  detail: 'INV-P16 FAIL',
  currentValue: 12,
  currentValueUnit: '对',
  currentValueLabel: null,
  threshold: '重叠对数 = 0',
  criterion: '同设备同类型事件区间两两不相交',
  rationale: '同一时刻不可能有两个互斥状态',
  relatedIssues: [249],
  totalViolations: 12,
  windowViolations: 3,
  historicalViolations: 9,
  earliestOccurrenceUtc: '2026-09-01T00:00:00+00:00',
  latestOccurrenceUtc: null,
  samples: ['sample'],
  thresholdFallback: false,
  thresholdNote: null,
  coveredLayers: null,
  trend: 'decreasing',
  trendDelta: -4,
  trendBaselineUtc: '2026-09-10T00:00:00+00:00',
  threeState: null,
  scanTruncated: false,
};

const report: DataReliabilityInspectionReport = {
  inspectedAtUtc: '2026-09-14T10:00:00+00:00',
  version: 3,
  elapsedMilliseconds: 812,
  status: 'red',
  redCount: 1,
  yellowCount: 0,
  greenCount: 12,
  unknownCount: 0,
  totalViolations: 12,
  windowViolations: 3,
  historicalViolations: 9,
  notices: {},
  rules: [rule],
  message: '体检完成',
};

const exportPayload: DataReliabilityViolationExport = {
  ruleCode: 'S1',
  generatedAtUtc: '2026-09-14T10:00:05+00:00',
  totalCount: 1,
  truncated: false,
  items: [
    {
      ruleCode: 'S1',
      id: '42',
      deviceId: 'DESKTOP-ARJ75IN',
      occurredAtUtc: '2026-09-14T01:00:00+00:00',
      fields: { eventType: 'window' },
    },
  ],
};

if (report.rules.length !== 1 || report.rules[0].key !== 'S1_INV-P16') {
  throw new Error('report contract mismatch');
}
if (exportPayload.items[0].fields.eventType !== 'window') {
  throw new Error('violation export contract mismatch');
}
if (rule.threeState !== null || rule.trendDelta !== -4) {
  throw new Error('nullable rule fields mismatch');
}

console.error('PASS: dataReliabilityTypes');
