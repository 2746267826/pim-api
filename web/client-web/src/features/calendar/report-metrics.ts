/*
 * 报告关键指标格式化（api#495）：
 * metricsJson 是扁平对象，值只有数字或「本周期无数据」两种（ReportService.BuildMetrics）。
 * 0 是真实统计值必须展示；「无数据」是模块结论，绝不能伪装成 0。
 */

export const REPORT_NO_DATA = '本周期无数据'
/** 值形状损坏（null/boolean/object 等）时的明确降级文案，不伪装成正常无数据 */
export const REPORT_METRIC_INVALID = '指标格式异常'

const MINUTE_KEYS = /Minutes$/

interface MetricMeta {
  label: string
  unit: string | null
  group: MetricGroup
}

type MetricGroup = 'task-progress' | 'plan-vs-actual' | 'calendar' | 'collection'

const GROUP_LABELS: Record<MetricGroup, string> = {
  'plan-vs-actual': '计划 vs 实际',
  'task-progress': '任务进展',
  calendar: '日历占用',
  collection: '收集质量',
}

/* 已知 key 的中文标签/单位/分组；未登记 key 走 describeUnknownKey 兜底，不猜测含义 */
const KNOWN_METRICS: Record<string, MetricMeta> = {
  tasks: { label: '已排期任务', unit: '项', group: 'plan-vs-actual' },
  completedTasks: { label: '其中已完成', unit: '项', group: 'plan-vs-actual' },
  plannedEstimatedMinutes: { label: '任务预估时长', unit: '分钟', group: 'plan-vs-actual' },
  plannedScheduledMinutes: { label: '已排入日程时长', unit: '分钟', group: 'plan-vs-actual' },
  actualExecutedMinutes: { label: '实际执行时长', unit: '分钟', group: 'plan-vs-actual' },
  calendarOccupiedMinutes: { label: '日历占用时长', unit: '分钟', group: 'calendar' },
  calendarActiveDays: { label: '有日程的天数', unit: '天', group: 'calendar' },
  tasksCreated: { label: '新增任务', unit: '项', group: 'task-progress' },
  tasksCompleted: { label: '完成任务', unit: '项', group: 'task-progress' },
  tasksOverdue: { label: '逾期任务', unit: '项', group: 'task-progress' },
  mobileUsageEvents: { label: '手机使用事件', unit: '条', group: 'collection' },
  mobileLocationPoints: { label: '手机定位点', unit: '个', group: 'collection' },
  pcEvents: { label: 'PC 采集事件', unit: '条', group: 'collection' },
  pcMissingHours: { label: 'PC 缺数小时', unit: '小时', group: 'collection' },
}

/** 未知 key 安全可读兜底：驼峰拆词 + 已知单位后缀，不翻译语义 */
export function describeUnknownKey(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .split(/\s+/)
    .filter(Boolean)
  const label = words.length > 0 ? words.join(' ') : key
  return MINUTE_KEYS.test(key) ? `${label}（分钟）` : label
}

export interface ReportMetric {
  key: string
  label: string
  unit: string | null
  group: MetricGroup
  /** null 表示「本周期无数据」等非数值结论 */
  value: number | null
  display: string
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toLocaleString('zh-CN', { maximumFractionDigits: 2 })
}

/**
 * 把 metricsJson 解析为分组后的可展示指标。
 * 解析失败返回 null（调用方呈现明确的失败态，不当作空指标）；数字之外的值一律
 * 按字面结论展示（后端只有「本周期无数据」，但未知字符串兜底同样可读）。
 */
export function parseReportMetrics(metricsJson: string): ReportMetric[] | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(metricsJson)
  } catch {
    return null
  }
  if (parsed == null || typeof parsed !== 'object' || Array.isArray(parsed)) return null
  const entries = Object.entries(parsed as Record<string, unknown>)
  if (entries.length === 0) return null
  return entries.map(([key, raw]) => {
    const meta = KNOWN_METRICS[key]
    const value = typeof raw === 'number' && Number.isFinite(raw) ? raw : null
    let display: string
    if (value !== null) {
      display = formatNumber(value)
    } else if (typeof raw === 'string' && raw.trim() !== '') {
      display = raw
    } else if (typeof raw === 'string') {
      display = REPORT_NO_DATA
    } else {
      display = REPORT_METRIC_INVALID
    }
    return {
      key,
      label: meta?.label ?? describeUnknownKey(key),
      unit: value === null ? null : meta?.unit ?? null,
      group: meta?.group ?? 'collection',
      value,
      display,
    }
  })
}

export { GROUP_LABELS as REPORT_METRIC_GROUP_LABELS }
