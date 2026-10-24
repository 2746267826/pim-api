/*
 * 数字枚举归一化（坑位清单）：后端多数枚举按数字序列化（无 JsonStringEnumConverter），
 * 旧前端因类型声明为字符串而失效。所有枚举消费必须经过本文件的 normalize 函数。
 */

/* ── 健康状态（PimHealthStatus：Unknown=0 Healthy=1 Warning=2 Critical=3） ── */

export type HealthStatus = 'unknown' | 'healthy' | 'warning' | 'critical'

export function normalizeHealthStatus(v: unknown): HealthStatus {
  if (typeof v === 'number') {
    return (['unknown', 'healthy', 'warning', 'critical'] as const)[v] ?? 'unknown'
  }
  switch (String(v).toLowerCase()) {
    case '0':
    case 'unknown':
      return 'unknown'
    case '1':
    case 'healthy':
      return 'healthy'
    case '2':
    case 'warning':
      return 'warning'
    case '3':
    case 'critical':
      return 'critical'
    default:
      return 'unknown'
  }
}

export const HEALTH_LABEL: Record<HealthStatus, string> = {
  unknown: '未知',
  healthy: '正常',
  warning: '有警告',
  critical: '故障',
}

/* ── 风险等级（OperationRiskLevel：Low=0 Medium=1 High=2 L0=10 … L4=14） ── */

export type RiskLevel =
  | 'Low'
  | 'Medium'
  | 'High'
  | 'L0AutomaticArtifact'
  | 'L1LowRiskAction'
  | 'L2PimFactChange'
  | 'L3ExternalSourceOrWriteback'
  | 'L4BatchOrDestructiveGovernance'

const RISK_BY_NUMBER: Record<number, RiskLevel> = {
  0: 'Low',
  1: 'Medium',
  2: 'High',
  10: 'L0AutomaticArtifact',
  11: 'L1LowRiskAction',
  12: 'L2PimFactChange',
  13: 'L3ExternalSourceOrWriteback',
  14: 'L4BatchOrDestructiveGovernance',
}

export function normalizeRiskLevel(v: unknown): RiskLevel {
  if (typeof v === 'number') return RISK_BY_NUMBER[v] ?? 'Low'
  return String(v) as RiskLevel
}

export const RISK_LABEL: Record<RiskLevel, string> = {
  Low: '低风险',
  Medium: '中风险',
  High: '高风险',
  L0AutomaticArtifact: '自动产物',
  L1LowRiskAction: '低风险操作',
  L2PimFactChange: '事实变更',
  L3ExternalSourceOrWriteback: '外部写回',
  L4BatchOrDestructiveGovernance: '批量/破坏性',
}

/** 风险徽标色调（soft 底语义色 tone） */
export function riskTone(level: RiskLevel): 'ok' | 'info' | 'warn' | 'crit' | 'neutral' {
  switch (level) {
    case 'Low':
    case 'L1LowRiskAction':
      return 'ok'
    case 'Medium':
    case 'L0AutomaticArtifact':
      return 'info'
    case 'High':
    case 'L2PimFactChange':
    case 'L3ExternalSourceOrWriteback':
      return 'warn'
    case 'L4BatchOrDestructiveGovernance':
      return 'crit'
    default:
      return 'neutral'
  }
}

/* ── 确认单状态（OperationConfirmationStatus：Pending=0 … Executed=4） ── */

export type ConfirmationStatus = 'Pending' | 'Confirmed' | 'Rejected' | 'Expired' | 'Executed'

const CONFIRM_STATUS_BY_NUMBER: Record<number, ConfirmationStatus> = {
  0: 'Pending',
  1: 'Confirmed',
  2: 'Rejected',
  3: 'Expired',
  4: 'Executed',
}

export function normalizeConfirmationStatus(v: unknown): ConfirmationStatus {
  if (typeof v === 'number') return CONFIRM_STATUS_BY_NUMBER[v] ?? 'Pending'
  return String(v) as ConfirmationStatus
}

export const CONFIRMATION_STATUS_LABEL: Record<ConfirmationStatus, string> = {
  Pending: '待确认',
  Confirmed: '已确认',
  Rejected: '已拒绝',
  Expired: '已过期',
  Executed: '已执行',
}

/* ── 习惯频率（HabitCadence：Daily=0 Weekly=1 Monthly=2 Custom=3，实测后端按数字序列化） ── */

export type HabitCadence = 'Daily' | 'Weekly' | 'Monthly' | 'Custom'

const CADENCE_BY_NUMBER: Record<number, HabitCadence> = {
  0: 'Daily',
  1: 'Weekly',
  2: 'Monthly',
  3: 'Custom',
}

export function normalizeHabitCadence(v: unknown): HabitCadence {
  if (typeof v === 'number') return CADENCE_BY_NUMBER[v] ?? 'Custom'
  switch (String(v)) {
    case '0':
    case 'Daily':
      return 'Daily'
    case '1':
    case 'Weekly':
      return 'Weekly'
    case '2':
    case 'Monthly':
      return 'Monthly'
    default:
      return 'Custom'
  }
}

export const CADENCE_LABEL: Record<HabitCadence, string> = {
  Daily: '每日',
  Weekly: '每周',
  Monthly: '每月',
  Custom: '自定义',
}

/* ── 手机生活分类（服务端枚举，颜色映射见 tokens.css --color-cat-*） ── */

export const LIFE_CATEGORIES = [
  '编程/折腾',
  '学习',
  '视频',
  '聊天',
  '文档',
  '游戏',
  '其他',
  '工具/系统',
] as const

export type LifeCategory = (typeof LIFE_CATEGORIES)[number]

/** 分类 → CSS 颜色变量值（图表/徽标用） */
export const LIFE_CATEGORY_COLOR: Record<LifeCategory, string> = {
  '编程/折腾': 'var(--color-cat-code)',
  学习: 'var(--color-cat-learn)',
  视频: 'var(--color-cat-video)',
  聊天: 'var(--color-cat-chat)',
  文档: 'var(--color-cat-doc)',
  游戏: 'var(--color-cat-game)',
  其他: 'var(--color-cat-other)',
  '工具/系统': 'var(--color-cat-noise)',
}
