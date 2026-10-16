// @vitest-environment jsdom
/*
 * 任务计时面板：正在计时的「开放段」行，显示风格必须与已闭合段一致（返修单 B7）。
 *
 * B7 现象：开放段走 `toLocalInputValue(seg.startsAt)`，拼出 `2026-10-15T09:33 ~ 进行中`
 * （ISO 原样串，带年份和 `T`），而同一列表里的闭合段走 `formatRange(...)` → `10-15 09:33 ~ 10:12`，
 * 同一份数据两种风格。
 *
 * 本测试不复制实现，而是**交叉核对两行**：把「起点相同的开放段」和「已闭合段」一起渲染，
 * 断言开放段行的左端逐字符等于闭合段行的左端（两行起点相同 ⇒ 必须渲染成同一串），
 * 右端是「进行中」，且整行不再出现 ISO 原样串。
 * 故障对照：把 task-segments-dialog.tsx 那一行改回 toLocalInputValue，本测试转红
 * （左端变成 `2026-10-15T09:33`，正则与 not.toContain 双双失败）。
 *
 * 只替换 query hooks：本文件断言的是「列表怎么显示」，不重复验证 hook 内部的 HTTP 拼装
 * （calendar-api.test.ts 已覆盖 method/path/body 形状）。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'

import { TaskSegmentsDialog } from './task-segments-dialog'

/** 开放段与闭合段共用同一起点，用来核对两行的左端是否同格式 */
const SHARED_START = '2026-10-15T09:33:00Z'
const CLOSED_END = '2026-10-15T10:12:00Z'

/* 契约值直接写成字面量：vi.mock 工厂会被提升，闭包引用本文件顶层的 import 绑定有 TDZ 风险。 */
vi.mock('../queries', () => ({
  useTaskSegments: () => ({
    data: [
      {
        id: 'seg-run',
        taskId: 'task-1',
        taskTitle: '写周报',
        startsAt: '2026-10-15T09:33:00Z',
        endsAt: null,
        status: 'active',
        source: 'timer',
        planningReason: null,
        confirmationId: null,
      },
      {
        id: 'seg-closed',
        taskId: 'task-1',
        taskTitle: '写周报',
        startsAt: '2026-10-15T09:33:00Z',
        endsAt: '2026-10-15T10:12:00Z',
        status: 'completed',
        source: 'timer',
        planningReason: null,
        confirmationId: null,
      },
    ],
    isLoading: false,
  }),
  useTaskTimer: () => ({
    data: {
      taskId: 'task-1',
      taskStatus: 'IN-PROCESS',
      runningSegmentId: 'seg-run',
      runningSince: '2026-10-15T09:33:00Z',
      accumulatedSeconds: 4740,
      segmentCount: 2,
    },
    isPending: false,
  }),
  useTaskTimerActions: () => ({
    start: { mutateAsync: async () => undefined, isPending: false },
    pause: { mutateAsync: async () => undefined, isPending: false },
    stop: { mutateAsync: async () => undefined, isPending: false },
  }),
  useAddSegment: () => ({ mutateAsync: async () => undefined, isPending: false }),
  useDeleteSegment: () => ({ mutateAsync: async () => undefined, isPending: false }),
}))

afterEach(cleanup)

/** 取某一段那一行的「时间段」span 文本（行内唯一含 `~` 的 span，避开状态徽标与时长列） */
function rangeTextOf(segmentId: string): string {
  const button = screen.getByLabelText(`删除时间段 ${segmentId}`)
  const li = button.closest('li')
  expect(li, `未找到段 ${segmentId} 所在的行`).not.toBeNull()
  const span = Array.from(li!.querySelectorAll('span')).find((s) => (s.textContent ?? '').includes('~'))
  expect(span, `段 ${segmentId} 所在行没有时间段文本`).toBeDefined()
  return span!.textContent ?? ''
}

describe('任务计时面板：开放段行与闭合段行同格式（B7）', () => {
  it('开放段左端与同起点的闭合段逐字符相同，右端为「进行中」，行内不再是 ISO 原样串', () => {
    render(<TaskSegmentsDialog taskId="task-1" taskTitle="写周报" onClose={() => {}} />)

    const runningText = rangeTextOf('seg-run')
    const closedText = rangeTextOf('seg-closed')
    const [closedStart, closedEnd] = closedText.split(' ~ ')

    // 闭合段走既有 formatRange：先钉住它的形状，才能用它当开放段的对照基准
    expect(closedStart).toMatch(/^\d{2}-\d{2} \d{2}:\d{2}$/)
    expect(closedEnd).toBeTruthy()

    // 同一起点 ⇒ 开放段左端必须与闭合段左端逐字符相同
    expect(runningText).toBe(`${closedStart} ~ 进行中`)

    // 正面形态：MM-dd HH:mm ~ 进行中（回退到 toLocalInputValue 会因 `T` 与年份失配）
    expect(runningText).toMatch(/^\d{2}-\d{2} \d{2}:\d{2} ~ 进行中$/)
    expect(runningText).not.toContain(SHARED_START)
    expect(runningText).not.toContain('T')
    expect(closedText).not.toContain(CLOSED_END)
  })
})
