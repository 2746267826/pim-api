// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { TaskHistoryPanel } from './task-history-panel'
import type { TaskHistoryView } from './types'

function entry(index: number): TaskHistoryView {
  return {
    id: `attempt-${index}`,
    title: `执行记录 ${index}`,
    createdAtLabel: `2026-10-31 10:0${index}`,
    routeLabel: index % 2 === 0 ? '本机执行' : '云端多模态',
    destinationLabel: '固定目标快照',
    statusLabel: index === 1 ? '部分成功' : '已完成',
    statusTone: index === 1 ? 'warn' : 'ok',
    summary: `保留第 ${index} 次旧结果`,
    attemptLabel: `第 ${index + 1} 次尝试`,
  }
}

afterEach(cleanup)

describe('TaskHistoryPanel', () => {
  it('读取失败时仍保留已加载历史，不把错误解释成删除或失败', () => {
    render(<TaskHistoryPanel entries={[entry(1), entry(2)]} loadError="第二页网络中断" />)
    expect(screen.getByText('部分历史读取失败')).toBeTruthy()
    expect(screen.getByText(/下方已加载记录继续保留/)).toBeTruthy()
    expect(screen.getByText('执行记录 1')).toBeTruthy()
    expect(screen.getByText('保留第 2 次旧结果')).toBeTruthy()
  })

  it('长历史有界分页，点击只选择记录不自动重跑', async () => {
    const select = vi.fn()
    render(<TaskHistoryPanel entries={[1, 2, 3, 4].map(entry)} pageSize={3} onSelect={select} />)
    expect(screen.getByText('第 1 / 2 页')).toBeTruthy()
    expect(screen.getByText('执行记录 3')).toBeTruthy()
    expect(screen.queryByText('执行记录 4')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '下一页历史' }))
    expect(screen.getByText('第 2 / 2 页')).toBeTruthy()
    expect(screen.getByText('执行记录 4')).toBeTruthy()
    await userEvent.click(screen.getByText('执行记录 4').closest('button')!)
    expect(select).toHaveBeenCalledWith('attempt-4')
    expect(select).toHaveBeenCalledOnce()
  })

  it('只读状态不能选择或执行副作用', async () => {
    const select = vi.fn()
    render(<TaskHistoryPanel entries={[entry(0)]} onSelect={select} readOnly />)
    expect(screen.getByText(/只读历史/)).toBeTruthy()
    expect((screen.getByText('执行记录 0').closest('button') as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(screen.getByText('执行记录 0'))
    expect(select).not.toHaveBeenCalled()
  })
})
