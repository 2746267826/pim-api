import { describe, expect, it } from 'vitest'
import {
  accumulatedSeconds,
  closedSegmentSeconds,
  currentRunSeconds,
  formatClock,
  isCountedSegment,
  runningSegment,
  segmentSeconds,
} from './timer'
import { TASK_SEGMENT_STATUS, TASK_STATUS, isKnownTaskStatus, normalizeTaskStatus } from './types'
import type { TaskExecutionSegment } from './types'

/** 工单 03 D2/D3：计时口径与状态值域的纯逻辑回归 */

function segment(overrides: Partial<TaskExecutionSegment>): TaskExecutionSegment {
  return {
    id: 's',
    taskId: 't',
    taskTitle: 'T',
    startsAt: '2026-06-01T09:00:00.000Z',
    endsAt: '2026-06-01T09:10:00.000Z',
    status: TASK_SEGMENT_STATUS.completed,
    source: 'timer',
    planningReason: null,
    confirmationId: null,
    ...overrides,
  }
}

describe('任务状态值域（工单 03 D1）', () => {
  it('四个合法值都算「已知」（IN-PROCESS 不再被吞）', () => {
    for (const s of ['NEEDS-ACTION', 'IN-PROCESS', 'COMPLETED', 'CANCELLED']) {
      expect(isKnownTaskStatus(s)).toBe(true)
      expect(normalizeTaskStatus(s)).toBe(s)
    }
  })

  it('值域外的历史值回落到待办（并可由调用方提示用户）', () => {
    expect(isKnownTaskStatus('DOING')).toBe(false)
    expect(normalizeTaskStatus('DOING')).toBe(TASK_STATUS.needsAction)
    // 小写不是合法值：服务端同样拒绝
    expect(isKnownTaskStatus('in-process')).toBe(false)
  })
})

describe('计时累加口径（与服务端 PlanningModelService.AccumulatedSeconds 对齐）', () => {
  const now = Date.parse('2026-06-01T10:00:00.000Z')

  it('只统计 active / paused / completed', () => {
    expect(isCountedSegment(segment({ status: TASK_SEGMENT_STATUS.planned }))).toBe(false)
    expect(isCountedSegment(segment({ status: TASK_SEGMENT_STATUS.cancelled }))).toBe(false)
    expect(isCountedSegment(segment({ status: TASK_SEGMENT_STATUS.paused }))).toBe(true)
    expect(isCountedSegment(segment({ status: TASK_SEGMENT_STATUS.completed }))).toBe(true)
  })

  it('开放段以「现在」为右端，暂停区间不计入', () => {
    const segments = [
      // 09:00 → 09:10 暂停
      segment({
        id: 'a',
        startsAt: '2026-06-01T09:00:00.000Z',
        endsAt: '2026-06-01T09:10:00.000Z',
        status: TASK_SEGMENT_STATUS.paused,
      }),
      // 09:30 再开始 → 仍在跑（到 now=10:00 记 30 分钟）
      segment({
        id: 'b',
        startsAt: '2026-06-01T09:30:00.000Z',
        endsAt: null,
        status: TASK_SEGMENT_STATUS.active,
      }),
    ]

    expect(accumulatedSeconds(segments, now)).toBe(10 * 60 + 30 * 60)
  })

  it('计划段与取消段不计入', () => {
    const segments = [
      segment({ id: 'p', status: TASK_SEGMENT_STATUS.planned }),
      segment({ id: 'x', status: TASK_SEGMENT_STATUS.cancelled }),
      segment({ id: 'c', status: TASK_SEGMENT_STATUS.completed, startsAt: '2026-06-01T09:00:00.000Z', endsAt: '2026-06-01T09:05:00.000Z' }),
    ]

    expect(accumulatedSeconds(segments, now)).toBe(5 * 60)
  })

  it('单段不产生负时长；runningSegment 只认 endsAt 为空的段', () => {
    expect(segmentSeconds(segment({ startsAt: '2026-06-01T10:00:00.000Z', endsAt: '2026-06-01T09:00:00.000Z' }), now)).toBe(0)
    expect(runningSegment([segment({ id: 'a' })])).toBeNull()
    expect(runningSegment([segment({ id: 'a' }), segment({ id: 'b', endsAt: null })])?.id).toBe('b')
  })
})

describe('当前这一轮时长（由服务端累计值反推）', () => {
  const segments = [
    segment({ id: 'a', startsAt: '2026-06-01T09:00:00.000Z', endsAt: '2026-06-01T09:10:00.000Z', status: TASK_SEGMENT_STATUS.paused }),
    segment({ id: 'b', startsAt: '2026-06-01T09:30:00.000Z', endsAt: null, status: TASK_SEGMENT_STATUS.active }),
  ]

  it('累计 40 分钟、已闭合 10 分钟 → 本轮 30 分钟', () => {
    expect(closedSegmentSeconds(segments[0])).toBe(10 * 60)
    expect(closedSegmentSeconds(segments[1])).toBe(0)
    expect(currentRunSeconds(segments, 40 * 60)).toBe(30 * 60)
  })

  it('服务端累计值偏小时不产生负数', () => {
    expect(currentRunSeconds(segments, 60)).toBe(0)
  })
})

describe('计时器显示', () => {
  it('HH:MM:SS 且超过 24 小时不回绕', () => {
    expect(formatClock(0)).toBe('00:00:00')
    expect(formatClock(59)).toBe('00:00:59')
    expect(formatClock(3661)).toBe('01:01:01')
    expect(formatClock(90_000)).toBe('25:00:00')
    expect(formatClock(-5)).toBe('00:00:00')
  })
})
