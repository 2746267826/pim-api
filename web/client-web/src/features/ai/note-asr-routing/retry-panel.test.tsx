// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RetryPanel } from './retry-panel'
import type { RecoveryItemView, RecoveryRequestView } from './types'

const items: RecoveryItemView[] = [
  { id: 'ok', label: '第 1 片', status: 'succeeded', resultPreview: '成功原文必须保留' },
  { id: 'failed', label: '第 2 片', status: 'failed', failureLabel: '输出截断' },
  { id: 'unknown', label: '第 3 片', status: 'unknown', failureLabel: '结果未知' },
  { id: 'running', label: '第 4 片', status: 'running' },
]

afterEach(cleanup)

describe('RetryPanel', () => {
  it('partial中的成功/运行项不可选，未填原因与确认时0调用', async () => {
    const retry = vi.fn()
    render(<RetryPanel items={items} onRetry={retry} />)

    expect((screen.getByRole('checkbox', { name: '选择重跑：第 1 片' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('checkbox', { name: '选择重跑：第 4 片' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(screen.getByRole('checkbox', { name: '选择重跑：第 2 片' }))
    const submit = screen.getByRole('button', { name: '确认重跑所选分片' }) as HTMLButtonElement
    expect(submit.disabled).toBe(true)
    expect(retry).not.toHaveBeenCalled()
  })

  it('只提交失败/待核对分片，带原因和重复计费确认', async () => {
    const retry = vi.fn<(request: RecoveryRequestView) => Promise<void>>().mockResolvedValue(undefined)
    render(<RetryPanel items={items} onRetry={retry} />)
    await userEvent.click(screen.getByRole('checkbox', { name: '选择重跑：第 2 片' }))
    await userEvent.click(screen.getByRole('checkbox', { name: '选择重跑：第 3 片' }))
    await userEvent.type(screen.getByLabelText('重跑原因'), '已核对未知结果，允许可能重复计费')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认选择性重跑并保留旧历史' }))
    await userEvent.click(screen.getByRole('button', { name: '确认重跑所选分片' }))

    await waitFor(() => expect(retry).toHaveBeenCalledOnce())
    expect(retry.mock.calls[0]?.[0]).toEqual({
      itemIds: ['failed', 'unknown'],
      reason: '已核对未知结果，允许可能重复计费',
      possibleDuplicateCharge: true,
      confirmed: true,
    })
  })

  it('回调失败时保留成功结果、失败项与历史，不自动重发', async () => {
    const retry = vi.fn().mockRejectedValue(new Error('平台连接中断'))
    render(<RetryPanel items={items} onRetry={retry} />)
    await userEvent.click(screen.getByRole('checkbox', { name: '选择重跑：第 2 片' }))
    await userEvent.type(screen.getByLabelText('重跑原因'), '核对后恢复失败片')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认选择性重跑并保留旧历史' }))
    await userEvent.click(screen.getByRole('button', { name: '确认重跑所选分片' }))

    expect(await screen.findByText('重跑未确认')).toBeTruthy()
    expect(screen.getByText(/旧结果与执行历史继续保留/)).toBeTruthy()
    expect(screen.getByText(/成功原文必须保留/)).toBeTruthy()
    expect(screen.getByText('第 2 片')).toBeTruthy()
    expect(retry).toHaveBeenCalledOnce()
  })
})
