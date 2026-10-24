// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render } from '@testing-library/react'
import { useDueNow } from './due-urgency'
import { useEffect } from 'react'

function Probe({ onChange }: { onChange: (now: number) => void }) {
  const now = useDueNow()
  useEffect(() => {
    onChange(now)
  }, [now, onChange])
  return null
}

describe('useDueNow 共享时钟', () => {
  afterEach(() => {
    cleanup()
    vi.useRealTimers()
  })

  it('document visibilitychange 触发刷新，卸载后解绑监听', async () => {
    vi.useFakeTimers()
    const base = Date.parse('2026-10-24T12:00:00.000Z')
    vi.setSystemTime(base)
    const updates: number[] = []
    const onChange = (now: number) => updates.push(now)

    const { unmount } = render(<Probe onChange={onChange} />)
    expect(updates).toEqual([base])

    await act(async () => {
      vi.setSystemTime(base + 5_000)
      document.dispatchEvent(new Event('visibilitychange'))
    })
    expect(updates).toEqual([base, base + 5_000])

    unmount()
    await act(async () => {
      vi.setSystemTime(base + 9_000)
      document.dispatchEvent(new Event('visibilitychange'))
      window.dispatchEvent(new Event('focus'))
    })
    expect(updates).toEqual([base, base + 5_000])
  })

  it('卸载共享时钟最后一个订阅后，新订阅仍能继续收时钟', async () => {
    vi.useFakeTimers()
    const base = Date.parse('2026-10-24T12:00:00.000Z')
    vi.setSystemTime(base)
    const updates: number[] = []
    const onChange = (now: number) => updates.push(now)

    const first = render(<Probe onChange={onChange} />)
    first.unmount()

    vi.setSystemTime(base + 31_000)
    const second = render(<Probe onChange={onChange} />)
    await act(async () => {
      vi.advanceTimersByTime(30_000)
    })
    expect(updates).toEqual([base, base + 31_000, base + 61_000])
    second.unmount()
  })
})
