/**
 * 数据可信度体检接口（#260）的前端契约类型。
 * 字段名与后端 `Pim.Core.Invariants.DataReliabilityInspectionReport` 的 JSON 序列化（camelCase）逐一对齐。
 */

export type DataReliabilityStatus = 'red' | 'yellow' | 'green' | 'unknown';

export type DataReliabilityGroupValue = 'self-consistency' | 'coverage' | 'pipeline';

export interface S2ThreeStateDistribution {
  inputActiveSeconds: number;
  mediaActiveSeconds: number;
  suspectedUnclosedSeconds: number;
  totalSeconds: number;
  inputActiveCount: number;
  mediaActiveCount: number;
  suspectedUnclosedCount: number;
  declaredGapSeconds: number;
}

export interface DataReliabilityRuleReport {
  code: string;
  invariantCode: string;
  key: string;
  order: number;
  name: string;
  group: string;
  groupLabel: string;
  status: string;
  statusLabel: string;
  detail: string;
  currentValue: number | null;
  currentValueUnit: string | null;
  currentValueLabel: string | null;
  threshold: string;
  criterion: string;
  rationale: string;
  relatedIssues: number[];
  totalViolations: number;
  windowViolations: number;
  historicalViolations: number;
  earliestOccurrenceUtc: string | null;
  latestOccurrenceUtc: string | null;
  samples: string[];
  thresholdFallback: boolean;
  thresholdNote: string | null;
  coveredLayers: string | null;
  trend: string;
  trendDelta: number | null;
  trendBaselineUtc: string | null;
  threeState: S2ThreeStateDistribution | null;
  scanTruncated: boolean;
}

export interface DataReliabilityInspectionReport {
  inspectedAtUtc: string;
  version: number;
  elapsedMilliseconds: number;
  status: string;
  redCount: number;
  yellowCount: number;
  greenCount: number;
  unknownCount: number;
  totalViolations: number;
  windowViolations: number;
  historicalViolations: number;
  notices: Record<string, string>;
  rules: DataReliabilityRuleReport[];
  message: string;
}

export interface DataReliabilityViolationItem {
  ruleCode: string;
  id: string;
  deviceId: string;
  occurredAtUtc: string;
  fields: Record<string, string>;
}

export interface DataReliabilityViolationExport {
  ruleCode: string;
  generatedAtUtc: string;
  totalCount: number;
  truncated: boolean;
  items: DataReliabilityViolationItem[];
}
