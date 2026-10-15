import { TASK_SEGMENT_STATUS } from './types'
import type { TaskExecutionSegment } from './types'

/**
 * 任务计时的纯逻辑（工单 03 D2/D3）。
 *
 * 与服务端口径**逐条对齐**（pim-api `PlanningModelService.AccumulatedSeconds` /
 * `TaskSegmentStatuses.CountsAsExecuted`）：
 * - `active`（endsAt = null）/ `paused` / `completed` / `cancelled` 计入实际执行时长；
 * - 只有 `planned`（还没做）不计 —— `cancelled` 段虽然被中止，但那段墙钟时间确实花在了任务上，
 *   不算进去会出现「跑一会儿再取消 → 时长凭空消失」而「先暂停再取消 → 时长保留」的顺序不一致；
 * - 开放段以「现在」为右端 —— 这也是暂停期间不会被计入的原因（暂停时没有开放段）。
 */

const COUNTED = new Set<string>([
  TASK_SEGMENT_STATUS.active,
  TASK_SEGMENT_STATUS.paused,
  TASK_SEGMENT_STATUS.completed,
  TASK_SEGMENT_STATUS.cancelled,
])

/** 正在计时的段（服务端不变量：同一任务同时最多一条） */
export function runningSegment(segments: readonly TaskExecutionSegment[]): TaskExecutionSegment | null {
  return segments.find((s) => s.endsAt == null) ?? null
}

export function isCountedSegment(segment: TaskExecutionSegment): boolean {
  return COUNTED.has(segment.status)
}

/**
 * 单段时长（秒）；开放段算到 nowMs。起点晚于终点时记 0（不产生负时长）。
 * 四舍五入与服务端 `PlanningModelService.AccumulatedSeconds` 同口径 ——
 * 否则界面上「各行之和」会和「累计值」差几秒。
 */
export function segmentSeconds(segment: TaskExecutionSegment, nowMs: number): number {
  const start = Date.parse(segment.startsAt)
  const end = segment.endsAt == null ? nowMs : Date.parse(segment.endsAt)
  if (Number.isNaN(start) || Number.isNaN(end) || end <= start) return 0
  return Math.round((end - start) / 1000)
}

/** 累计实际执行时长（秒）：与服务端同一规则 */
export function accumulatedSeconds(segments: readonly TaskExecutionSegment[], nowMs: number): number {
  return segments.reduce(
    (total, segment) => (isCountedSegment(segment) ? total + segmentSeconds(segment, nowMs) : total),
    0,
  )
}

/** 已闭合段的时长（秒）；开放段返回 0（它的时长由服务端计时器给出，见下） */
export function closedSegmentSeconds(segment: TaskExecutionSegment): number {
  return segment.endsAt == null ? 0 : segmentSeconds(segment, 0)
}

/**
 * **当前这一轮**已跑时长（秒）= 服务端累计值 − 已闭合段的合计。
 *
 * 用服务端的累计值反推，而不是在组件里读本地时钟：这样界面上显示的就是
 * 报告侧将来读到的同一个数字，不会出现「界面 5 分钟、库里 4 分 58 秒」的分叉。
 */
export function currentRunSeconds(
  segments: readonly TaskExecutionSegment[],
  accumulated: number,
): number {
  const closed = segments
    .filter((s) => s.endsAt != null)
    .reduce((total, segment) => (isCountedSegment(segment) ? total + closedSegmentSeconds(segment) : total), 0)
  return Math.max(0, Math.round(accumulated) - closed)
}

/** 计时器显示：HH:MM:SS（超过 24 小时继续累加小时位，不回绕） */
export function formatClock(totalSeconds: number): string {
  const safe = Math.max(0, Math.floor(totalSeconds))
  const h = Math.floor(safe / 3600)
  const m = Math.floor((safe % 3600) / 60)
  const s = safe % 60
  return [h, m, s].map((v) => String(v).padStart(2, '0')).join(':')
}
