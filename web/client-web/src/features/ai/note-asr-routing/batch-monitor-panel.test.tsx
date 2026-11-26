// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { BatchMonitorPanel } from './batch-monitor-panel'
import type { BatchJobView } from './types'

const unknownJob: BatchJobView = {
  id: 'batch-1',
  title: '课堂长音频 Batch',
  status: 'unknown',
  createdAtLabel: '2026-10-31 09:00',
  destinationLabel: 'MiMo Flash 云端',
  completionWindowLabel: '24h',
  cashEstimateLabel: '现金余额计费，套餐额度不抵扣',
  progressLabel: '完成 3 / 失败 2 / 总数 5',
  totalItems: 5,
  completedItems: 3,
  failedItems: 2,
  providerRequestId: null,
  inputExpiresAtLabel: '2026-11-30 09:00',
  batchExpiresAtLabel: '2026-11-01 09:00',
  outputAvailable: false,
}

const waitingJob: BatchJobView = {
  ...unknownJob,
  id: 'batch-2',
  title: '等待中的 Batch',
  status: 'waiting',
  providerRequestId: 'batch_provider_123',
  progressLabel: '完成 0 / 失败 0 / 总数 5',
  completedItems: 0,
  failedItems: 0,
}

afterEach(cleanup)

describe('BatchMonitorPanel', () => {
  it('无id UNKNOWN只提示平台人工核对，不显示自动重发或查询', () => {
    const poll = vi.fn()
    render(<BatchMonitorPanel jobs={[unknownJob]} onPoll={poll} />)
    expect(screen.getByText('UNKNOWN 待人工核对')).toBeTruthy()
    expect(screen.getByText('没有可核对的供应商任务标识')).toBeTruthy()
    expect(screen.getByText(/不得自动创建新任务、重新上传或假定成功/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: '立即查询状态' })).toBeNull()
    expect(screen.queryByRole('button', { name: /重发|重建/ })).toBeNull()
    expect(poll).not.toHaveBeenCalled()
  })

  it('展示长等待、现金、partial和文件/任务两个不同截止时间', () => {
    const partial = { ...unknownJob, status: 'partial' as const, providerRequestId: 'batch_provider_456' }
    render(<BatchMonitorPanel jobs={[partial]} />)
    expect(screen.getByText(/现金余额计费/)).toBeTruthy()
    expect(screen.getAllByText('部分成功')).toHaveLength(2)
    expect(screen.getByText(/成功请求已经产生费用并保留结果/)).toBeTruthy()
    expect(screen.getByText(/输入文件保留至 2026-11-30/)).toBeTruthy()
    expect(screen.getByText(/Batch任务截止 2026-11-01/)).toBeTruthy()
    expect(screen.getByText('音频能力 NOT-VERIFIED')).toBeTruthy()
  })

  it('取消必须先显式确认，回调成功后仍不伪造终态', async () => {
    const cancel = vi.fn().mockResolvedValue(undefined)
    render(<BatchMonitorPanel jobs={[waitingJob]} onCancel={cancel} />)
    await userEvent.click(screen.getByRole('button', { name: '请求取消' }))
    const confirm = screen.getByRole('button', { name: '确认异步取消' }) as HTMLButtonElement
    expect(confirm.disabled).toBe(true)
    expect(cancel).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认请求取消 等待中的 Batch' }))
    await userEvent.click(confirm)

    await waitFor(() => expect(cancel).toHaveBeenCalledWith('batch-2'))
    expect(screen.getByText('供应商排队中')).toBeTruthy()
    expect(screen.queryByText('已取消')).toBeNull()
    expect(screen.getByText(/取消请求已发送/)).toBeTruthy()
    expect(screen.getByText(/页面不会直接标记完成或成功/)).toBeTruthy()
  })

  it('只读Batch不轮询、不取消', () => {
    const poll = vi.fn()
    const cancel = vi.fn()
    render(<BatchMonitorPanel jobs={[waitingJob]} onPoll={poll} onCancel={cancel} readOnly />)
    expect((screen.getByRole('button', { name: '立即查询状态' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '请求取消' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.getByText(/只读模式不会轮询/)).toBeTruthy()
  })

  it('长Batch列表有界分页', async () => {
    const jobs = Array.from({ length: 6 }, (_, index) => ({
      ...waitingJob,
      id: `batch-${index}`,
      title: `Batch ${index}`,
      providerRequestId: `provider-${index}`,
    }))
    render(<BatchMonitorPanel jobs={jobs} pageSize={4} />)
    expect(screen.getByText('第 1 / 2 页')).toBeTruthy()
    expect(screen.getByText('Batch 3')).toBeTruthy()
    expect(screen.queryByText('Batch 4')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '下一页Batch' }))
    expect(screen.getByText('第 2 / 2 页')).toBeTruthy()
    expect(screen.getByText('Batch 5')).toBeTruthy()
  })

  it('hides provider names, raw status and backend errors from ordinary users', () => {
    const job = {
      ...unknownJob,
      title: '课堂长音频任务',
      destinationLabel: 'MiMo Flash 云端',
      providerRequestId: 'batch_provider_123',
      errorLabel: 'MiMo provider raw batch failure',
    }
    render(<BatchMonitorPanel jobs={[job]} showVersionDetails={false} onPoll={vi.fn()} />)

    expect(screen.getByText('批量转写任务')).toBeTruthy()
    expect(screen.getByText('结果待核对')).toBeTruthy()
    expect(screen.getByText('上游能力待验收')).toBeTruthy()
    expect(screen.getByText('本次处理未完整成功，请刷新核对；页面不会自动重发。')).toBeTruthy()
    expect(screen.queryByText(/MiMo/)).toBeNull()
    expect(screen.queryByText(/供应商/)).toBeNull()
    expect(screen.queryByText(/UNKNOWN/)).toBeNull()
    expect(screen.queryByText(/NOT-VERIFIED/)).toBeNull()
    expect(screen.queryByText(/batch_provider_123/)).toBeNull()
    expect(screen.queryByText(/MiMo provider raw batch failure/)).toBeNull()
  })

  it('keeps provider identity and diagnostics for admins', () => {
    const job = {
      ...unknownJob,
      providerRequestId: 'batch_provider_123',
      errorLabel: 'provider diagnostic detail',
    }
    render(<BatchMonitorPanel jobs={[job]} onPoll={vi.fn()} />)

    expect(screen.getByText('MiMo Batch 长任务')).toBeTruthy()
    expect(screen.getByText('UNKNOWN 待人工核对')).toBeTruthy()
    expect(screen.getByText('音频能力 NOT-VERIFIED')).toBeTruthy()
    expect(screen.getByText('供应商任务标识：batch_provider_123')).toBeTruthy()
    expect(screen.getByText('provider diagnostic detail')).toBeTruthy()
  })

  it('does not expose callback errors to ordinary users', async () => {
    const poll = vi.fn().mockRejectedValue(new Error('MiMo raw poll failure'))
    render(<BatchMonitorPanel jobs={[waitingJob]} onPoll={poll} showVersionDetails={false} />)

    await userEvent.click(screen.getByRole('button', { name: '立即查询状态' }))

    expect(await screen.findByText(/查询状态未确认，请刷新核对/)).toBeTruthy()
    expect(screen.queryByText(/MiMo raw poll failure/)).toBeNull()
  })

  it('keeps callback errors visible to admins', async () => {
    const poll = vi.fn().mockRejectedValue(new Error('provider poll diagnostic'))
    render(<BatchMonitorPanel jobs={[waitingJob]} onPoll={poll} />)

    await userEvent.click(screen.getByRole('button', { name: '立即查询状态' }))

    expect(await screen.findByText(/provider poll diagnostic/)).toBeTruthy()
  })
})
