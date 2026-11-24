// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { TaskStatusFilter, toggleTaskStatus } from './task-status-filter'
import { TASK_STATUS } from '../types'

afterEach(cleanup)

describe('TaskStatusFilter', () => {
  it('toggles statuses without collapsing to single select', () => {
    expect(toggleTaskStatus([], TASK_STATUS.needsAction)).toEqual([TASK_STATUS.needsAction])
    expect(toggleTaskStatus([TASK_STATUS.needsAction], TASK_STATUS.inProcess)).toEqual([
      TASK_STATUS.needsAction,
      TASK_STATUS.inProcess,
    ])
    expect(toggleTaskStatus([TASK_STATUS.needsAction, TASK_STATUS.inProcess], TASK_STATUS.needsAction)).toEqual([
      TASK_STATUS.inProcess,
    ])
  })

  it('shows selected count and clears back to all statuses', () => {
    const changes: string[][] = []
    render(
      <TaskStatusFilter
        selected={[TASK_STATUS.needsAction, TASK_STATUS.inProcess]}
        onChange={(next) => changes.push(next)}
      />,
    )

    expect(screen.getByText('已选 2 个状态')).toBeTruthy()
    expect(screen.getByRole('button', { name: '待办' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: '已完成' }).getAttribute('aria-pressed')).toBe('false')

    fireEvent.click(screen.getByRole('button', { name: '已完成' }))
    fireEvent.click(screen.getByRole('button', { name: '全部状态' }))

    expect(changes).toEqual([
      [TASK_STATUS.needsAction, TASK_STATUS.inProcess, TASK_STATUS.completed],
      [],
    ])
  })
})
