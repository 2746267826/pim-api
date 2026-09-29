import type {
  DataReliabilityInspectionReport,
  DataReliabilityRuleReport,
  DataReliabilityStatus,
  DataReliabilityViolationExport,
  S2ThreeStateDistribution,
} from '../../api/dataReliabilityTypes';

/** 状态徽标：图标 + 中文标签同时出现，绝不只靠颜色区分。 */
export interface DataReliabilityStatusPresentation {
  icon: string;
  label: string;
  tone: 'danger' | 'warning' | 'activity' | 'neutral';
}

export const dataReliabilityStatusPresentation: Record<DataReliabilityStatus, DataReliabilityStatusPresentation> = {
  red: { icon: '🔴', label: '红', tone: 'danger' },
  yellow: { icon: '🟡', label: '黄', tone: 'warning' },
  green: { icon: '🟢', label: '绿', tone: 'activity' },
  unknown: { icon: '⚪', label: '未知', tone: 'neutral' },
};

/** 三层分组展示顺序（与 EPIC #254 §4 一致）。 */
export const dataReliabilityGroupOrder = ['数据自洽', '覆盖完整', '链路健康'] as const;

export function normalizeStatus(status: string | null | undefined): DataReliabilityStatus {
  const value = (status ?? '').toLowerCase();
  if (value === 'red' || value === 'yellow' || value === 'green') return value;
  return 'unknown';
}

export function statusPresentation(status: string | null | undefined): DataReliabilityStatusPresentation {
  return dataReliabilityStatusPresentation[normalizeStatus(status)];
}

export function isRedStatus(status: string | null | undefined): boolean {
  return normalizeStatus(status) === 'red';
}

/** 按分组标签归并；未知分组排到末尾，避免后端新增分组时条目被静默丢弃。 */
export function groupRules(
  rules: readonly DataReliabilityRuleReport[]
): { label: string; rules: DataReliabilityRuleReport[] }[] {
  const buckets = new Map<string, DataReliabilityRuleReport[]>();
  for (const rule of rules) {
    const label = rule.groupLabel || '未分组';
    const bucket = buckets.get(label);
    if (bucket) bucket.push(rule);
    else buckets.set(label, [rule]);
  }

  const ordered: { label: string; rules: DataReliabilityRuleReport[] }[] = [];
  for (const label of dataReliabilityGroupOrder) {
    const bucket = buckets.get(label);
    if (bucket && bucket.length > 0) {
      ordered.push({ label, rules: [...bucket].sort((a, b) => a.order - b.order) });
      buckets.delete(label);
    }
  }
  for (const [label, bucket] of buckets) {
    ordered.push({ label, rules: [...bucket].sort((a, b) => a.order - b.order) });
  }
  return ordered;
}

/**
 * 体检结果视为"过期"的时长（小时）。与后端 `DataReliabilityGate.DefaultMaxAge`（26 小时）保持一致：
 * 后台巡检每小时一次，26 小时足够覆盖一次漏跑。
 */
export const dataReliabilityStaleAfterHours = 26;

/** 结果是否已过期；无结果或时间非法时返回 false（由"暂无数据"分支单独处理）。 */
export function isInspectionStale(
  inspectedAtUtc: string | null | undefined,
  now: Date,
  staleAfterHours: number = dataReliabilityStaleAfterHours
): boolean {
  if (!inspectedAtUtc) return false;
  const at = new Date(inspectedAtUtc);
  if (Number.isNaN(at.getTime())) return false;
  return now.getTime() - at.getTime() > staleAfterHours * 3600_000;
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { hour12: false });
}

/**
 * 新鲜度文案。`now` 必须可注入，避免测试依赖真实时钟。
 * 未来时间（服务端与浏览器时钟偏差）按"刚刚"处理。
 */
export function formatFreshness(inspectedAtUtc: string | null | undefined, now: Date): string {
  if (!inspectedAtUtc) return '未知';
  const at = new Date(inspectedAtUtc);
  if (Number.isNaN(at.getTime())) return '未知';

  const minutes = Math.max(0, Math.round((now.getTime() - at.getTime()) / 60000));
  if (minutes < 1) return '刚刚';
  if (minutes < 60) return `${minutes} 分钟前`;
  if (minutes < 1440) return `${Math.round(minutes / 60)} 小时前`;
  return `${Math.round(minutes / 1440)} 天前`;
}

/** 把秒数写成"X 小时 Y 分钟"这类人话，便于看"有多少时长其实是挂机"。 */
export function formatDurationSeconds(seconds: number): string {
  const total = Number.isFinite(seconds) ? Math.max(0, Math.round(seconds)) : 0;
  const minutes = Math.round(total / 60);
  if (minutes < 60) return `${minutes} 分钟`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} 小时` : `${hours} 小时 ${rest} 分钟`;
}

export function formatCurrentValue(rule: DataReliabilityRuleReport): string {
  if (rule.currentValueLabel) return rule.currentValueLabel;
  if (rule.currentValue === null || rule.currentValue === undefined) return '暂无';
  const unit = rule.currentValueUnit ? ` ${rule.currentValueUnit}` : '';
  return `${rule.currentValue}${unit}`;
}

/**
 * 历史欠账趋势文案（#260 要求"给出趋势，便于判断修复是否起作用"）。
 * D5：本轮不改口径，仍对**欠账数**环比；重启后无基线 → 暂无对比基线。
 */
export function describeTrend(rule: DataReliabilityRuleReport): string {
  const baseline = rule.trendBaselineUtc
    ? `较 ${formatDateTime(rule.trendBaselineUtc)} 的体检`
    : '较上一次体检';
  const delta = rule.trendDelta ?? 0;

  switch (rule.trend) {
    case 'decreasing':
      return `历史欠账${baseline}减少 ${Math.abs(delta)} 条（历史欠账在消化）`;
    case 'increasing':
      return `历史欠账${baseline}增加 ${Math.abs(delta)} 条（历史欠账在增长）`;
    case 'flat':
      return `历史欠账${baseline}持平（${rule.historicalViolations} 条）`;
    default:
      return `暂无对比基线（历史欠账 ${rule.historicalViolations} 条）`;
  }
}

/** 考核窗时长的人话（168 → "最近 7 天"）；非整天数按小时显示。 */
export function formatAssessmentWindow(hours: number | null | undefined): string {
  if (!Number.isFinite(hours) || (hours ?? 0) <= 0) return '未配置';
  const value = hours as number;
  if (value % 24 === 0) return `最近 ${value / 24} 天`;
  return `最近 ${value} 小时`;
}

/** `<YYYY-MM-DD HH:mm>`（浏览器本地时区，与面板上其它时间显示一致）。 */
export function formatLedgerTimestamp(value: string | null | undefined): string {
  if (!value) return '未知';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const pad = (part: number) => String(part).padStart(2, '0');
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

/**
 * 面板头部的账本信息行（逐字规格见工单 §五）：
 * `考核账本自 <YYYY-MM-DD HH:mm> 起（容器更新/重启后自动重置）｜ 考核窗：最近 7 天`
 */
export function buildLedgerLine(report: DataReliabilityInspectionReport): string {
  return (
    `考核账本自 ${formatLedgerTimestamp(report.assessmentStartUtc)} 起（容器更新/重启后自动重置）` +
    `｜ 考核窗：${formatAssessmentWindow(report.assessmentWindowHours)}`
  );
}

export interface ThreeStateBucket {
  key: 'inputActive' | 'mediaActive' | 'suspectedUnclosed';
  label: string;
  seconds: number;
  count: number;
  percent: number;
  className: string;
}

export function buildThreeStateBuckets(distribution: S2ThreeStateDistribution): ThreeStateBucket[] {
  const total = distribution.totalSeconds > 0 ? distribution.totalSeconds : 0;
  const percent = (seconds: number) => (total > 0 ? (seconds / total) * 100 : 0);
  return [
    {
      key: 'inputActive',
      label: '操作活跃',
      seconds: distribution.inputActiveSeconds,
      count: distribution.inputActiveCount,
      percent: percent(distribution.inputActiveSeconds),
      className: 'bg-emerald-500',
    },
    {
      key: 'mediaActive',
      label: '观看活跃',
      seconds: distribution.mediaActiveSeconds,
      count: distribution.mediaActiveCount,
      percent: percent(distribution.mediaActiveSeconds),
      className: 'bg-blue-500',
    },
    {
      key: 'suspectedUnclosed',
      label: '疑似未收尾',
      seconds: distribution.suspectedUnclosedSeconds,
      count: distribution.suspectedUnclosedCount,
      percent: percent(distribution.suspectedUnclosedSeconds),
      className: 'bg-red-500',
    },
  ];
}

export function buildViolationExportFileName(code: string, now: Date): string {
  const stamp = now.toISOString().slice(0, 19).replace(/[:T]/g, '-');
  return `data-reliability-${code}-violations-${stamp}.json`;
}

/** 导出清单序列化为 JSON，字段与接口一致（ID + 业务时间 + 设备 + 关键字段）。 */
export function serializeViolationExport(payload: DataReliabilityViolationExport): string {
  return JSON.stringify(payload, null, 2);
}

export interface DataReliabilityOverviewCounts {
  red: number;
  yellow: number;
  green: number;
  unknown: number;
  total: number;
}

export function buildOverviewCounts(report: DataReliabilityInspectionReport): DataReliabilityOverviewCounts {
  return {
    red: report.redCount,
    yellow: report.yellowCount,
    green: report.greenCount,
    unknown: report.unknownCount,
    total: report.rules.length,
  };
}
