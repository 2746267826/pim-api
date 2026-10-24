/*
 * 「时间块热力」逐小时数据合并（纯函数，无 React 依赖）。
 *
 * 为什么两个来源合并：
 * - summary.heatmap 覆盖当天全部小时（activeMinutes / intensityLevel 0–5 / totalEvents），
 *   与 summary.timeline 的覆盖一致，是逐小时活跃度的可靠来源；
 * - activity-analysis 的块数据覆盖明显稀疏（同一业务日常只有少数几小时非零，
 *   且块内时长合计可超过块本身），但「待分类计数」与「块内应用分布」仅它提供。
 * 故条形取 heatmap，待分类/主要应用取 activity-analysis，按 +08:00 本地小时对齐。
 * timeline（窗口事件口径）另拆出每小时覆盖分钟：热力条深色段 = 时间线覆盖，
 * 浅色段 = 仅有键鼠输入（窗口事件采集缺口，全屏应用等场景）。
 *
 * 时刻一律按 +08:00 墙钟换算（UTC 毫秒 + 固定偏移），不使用浏览器本地时区。
 */

export interface HeatmapBucketInput {
  start?: string | null
  activeMinutes?: number | null
  /** 0–5 强度档（PC-3 修复后由 intensityScore 改名而来） */
  intensityLevel?: number | null
  totalEvents?: number | null
}

export interface ActivityBlockInput {
  start?: string | null
  pendingClassificationCount?: number | null
  apps?: { appName?: string | null; durationSeconds?: number | null }[] | null
}

/** summary.timeline 记录（只取画覆盖段需要的字段） */
export interface TimelineCoverageInput {
  start?: string | null
  durationMinutes?: number | null
}

export interface HourlyHeatRow {
  /** 本地小时（0–23） */
  hour: number
  /** HH:00 标签 */
  label: string
  /** 该小时活跃分钟（输入口径，条宽用，60 = 整小时） */
  activeMinutes: number
  /** 其中时间线（窗口事件记录）覆盖的分钟，≤ activeMinutes 常态，坏数据时可能略大 */
  coveredMinutes: number
  /** 服务端强度档（0–5） */
  intensity: number
  /** 事件数 */
  totalEvents: number
  /** 待分类记录数（activity-analysis） */
  pending: number
  /** 块内时长最长的应用（activity-analysis） */
  topApp: string | null
}

const WALL_OFFSET_MINUTES = 8 * 60
const MS_PER_MINUTE = 60_000

/** UTC 毫秒 → 墙钟当天第几分钟 */
function toMinutes(utcMs: number): number {
  const dayMs = 24 * 60 * MS_PER_MINUTE
  const shifted = utcMs + WALL_OFFSET_MINUTES * MS_PER_MINUTE
  return (((shifted % dayMs) + dayMs) % dayMs) / MS_PER_MINUTE
}

/** 解析后端时间戳（含 7 位小数形态），失败返回 null */
function parseUtc(value: string | null | undefined): number | null {
  if (typeof value !== 'string' || !value.trim()) return null
  const m = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})?$/.exec(value.trim())
  if (!m) {
    const fallback = Date.parse(value)
    return Number.isNaN(fallback) ? null : fallback
  }
  const [, date, hh, mm, ss, frac, zone] = m
  const ms = frac ? Math.round(Number(`0.${frac}`) * 1000) : 0
  const parsed = Date.parse(`${date}T${hh}:${mm}:${ss}.${String(ms).padStart(3, '0')}${zone ?? 'Z'}`)
  return Number.isNaN(parsed) ? null : parsed
}

/**
 * 把一条时间线记录的覆盖分钟按 +08:00 墙钟小时拆分累加（记录可跨小时/跨日）。
 * 按半开区间 [start, start+duration) 逐段推进；guard 防坏数据（超长时长）死循环。
 */
function addCoverage(coverage: Map<number, number>, startMs: number, durationMinutes: number): void {
  let cursor = startMs
  let remainingMs = durationMinutes * MS_PER_MINUTE
  for (let guard = 0; remainingMs > 0 && guard < 48; guard++) {
    const minutes = toMinutes(cursor)
    const hour = Math.floor(minutes / 60) % 24
    const remainInHourMs = (60 - (minutes % 60)) * MS_PER_MINUTE
    const takeMs = Math.min(remainInHourMs, remainingMs)
    coverage.set(hour, (coverage.get(hour) ?? 0) + takeMs / MS_PER_MINUTE)
    cursor += takeMs
    remainingMs -= takeMs
  }
}

/**
 * 合并 summary.heatmap 与 activity-analysis.blocks → 逐小时行（按本地小时升序）。
 * timeline（窗口事件记录）可选传入：拆分出每小时的「时间线覆盖分钟」，
 * 与输入口径 activeMinutes 分开——窗口事件采集有缺口（全屏应用等），
 * 两者差值 = 仅有键鼠输入、无窗口记录的时间。无法解析的时间戳跳过；
 * 同一小时以 heatmap 为准、activity/timeline 数据叠加。
 */
export function buildHourlyHeatRows(
  heatmap: readonly HeatmapBucketInput[] | null | undefined,
  blocks: readonly ActivityBlockInput[] | null | undefined,
  timeline?: readonly TimelineCoverageInput[] | null,
): HourlyHeatRow[] {
  const rows = new Map<number, HourlyHeatRow>()
  const coverage = new Map<number, number>()

  for (const rec of Array.isArray(timeline) ? timeline : []) {
    const ms = parseUtc(rec?.start)
    const dur = Number(rec?.durationMinutes ?? 0)
    if (ms == null || !Number.isFinite(dur) || dur <= 0) continue
    addCoverage(coverage, ms, dur)
  }

  for (const b of Array.isArray(heatmap) ? heatmap : []) {
    const ms = parseUtc(b?.start)
    if (ms == null) continue
    const minutes = toMinutes(ms)
    const hour = Math.floor(minutes / 60) % 24
    rows.set(hour, {
      hour,
      label: `${String(hour).padStart(2, '0')}:00`,
      activeMinutes: Math.max(0, Math.round(b?.activeMinutes ?? 0)),
      coveredMinutes: Math.round(coverage.get(hour) ?? 0),
      intensity: Math.max(0, Math.round(b?.intensityLevel ?? 0)),
      totalEvents: Math.max(0, Math.round(b?.totalEvents ?? 0)),
      pending: 0,
      topApp: null,
    })
  }

  for (const blk of Array.isArray(blocks) ? blocks : []) {
    const ms = parseUtc(blk?.start)
    if (ms == null) continue
    const hour = Math.floor(toMinutes(ms) / 60) % 24
    const row = rows.get(hour)
    if (!row) continue // heatmap 没有的小时不凭空造行（保持口径一致）
    row.pending = Math.max(row.pending, Math.max(0, Math.round(blk?.pendingClassificationCount ?? 0)))
    const apps: NonNullable<ActivityBlockInput['apps']> = Array.isArray(blk?.apps) ? blk.apps : []
    const top = apps
      .slice()
      .sort((a, b) => Number(b?.durationSeconds ?? 0) - Number(a?.durationSeconds ?? 0))[0]
    if (top?.appName) row.topApp = top.appName

  }

  return [...rows.values()].sort((a, b) => a.hour - b.hour)
}
