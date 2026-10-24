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
  /** S3 专用：本次取数实际覆盖到的业务日数（AC-6.4）；其余尺子为 null。 */
  scanCoveredDays: number | null;
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
  /** 本次体检的考核线（面板上的「考核账本起始时刻」）：max(体检时刻 − 考核窗, 进程启动时刻)。 */
  assessmentStartUtc: string;
  /** 本次体检生效的考核窗时长（小时），默认 168（7 天）。 */
  assessmentWindowHours: number;
}

export interface DataReliabilityViolationItem {
  ruleCode: string;
  id: string;
  deviceId: string;
  occurredAtUtc: string;
  fields: Record<string, string>;
  /** 分档标记：true = 窗内（决定颜色），false = 历史欠账（只计数）。 */
  isNew: boolean;
}

export interface DataReliabilityViolationExport {
  ruleCode: string;
  generatedAtUtc: string;
  totalCount: number;
  truncated: boolean;
  items: DataReliabilityViolationItem[];
}
