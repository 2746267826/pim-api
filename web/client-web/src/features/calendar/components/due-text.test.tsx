// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, act } from '@testing-library/react'
import { DueText } from './due-text'

const NOW = Date.parse('2026-10-24T12:00:00.000Z')

beforeEach(() => {
  vi.useFakeTimers()
  vi.setSystemTime(NOW)
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('DueText（四处入口共用呈现）', () => {
  it('过去为红并带“已逾期”；未来到 3 天为黄并带“即将到期”', () => {
    render(<DueText due="2026-10-23T12:00:00.000Z" />)
    expect(screen.getByText(/已逾期/).className).toContain('text-crit')

    cleanup()
    render(<DueText due="2026-10-25T12:00:00.000Z" />)
    expect(screen.getByText(/即将到期/).className).toContain('text-warn')
  })

  it('超过 3 天为灰；已完成/已取消一律灰且不加紧迫后缀', () => {
    render(<DueText due="2030-01-01T00:00:00.000Z" />)
    const distant = screen.getByText(/截止/)
    expect(distant.className).toContain('text-text-4')
    expect(distant.textContent).not.toContain('已逾期')
    expect(distant.textContent).not.toContain('即将到期')

    cleanup()
    render(<DueText due="2000-01-01T00:00:00.000Z" inactive />)
    const inactive = screen.getByText(/截止/)
    expect(inactive.className).toContain('text-text-4')
    expect(inactive.textContent).not.toContain('已逾期')
  })

  it('无效 due 不显示假时间', () => {
    render(<DueText due="not-a-date" />)
    expect(screen.getByText('截止 时间无效').className).toContain('text-crit')
  })

  it('时间推进跨过截止点后重新分档', async () => {
    render(<DueText due="2026-10-24T12:00:30.000Z" />)
    expect(screen.getByText(/即将到期/).className).toContain('text-warn')

    await act(async () => {
      vi.setSystemTime(NOW + 31_000)
      vi.advanceTimersByTime(30_000)
    })
    expect(screen.getByText(/已逾期/).className).toContain('text-crit')
  })
})
