// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrOwnerBatches } from './note-asr-owner-batches'
import { noteAsrBatchFailedIntentScope } from './note-asr-batch-intent'
import type { NoteAsrBatchJob, NoteAsrTask } from './api'

const api = vi.hoisted(() => ({
  asrBatchJobs: vi.fn(), asrBatchPoll: vi.fn(), asrBatchCancel: vi.fn(),
  asrRouteConfig: vi.fn(), noteAsrDestinations: vi.fn(), asrBatchRetryFailed: vi.fn(),
  noteAsrTaskOrNull: vi.fn(),
}))
vi.mock('./api', () => ({ aiApi: api }))

const userId = '11111111-1111-4111-8111-111111111111'
const serverBinding = 'https://synthetic.invalid'
const modelRevision = '66666666-6666-4666-8666-666666666666'
const task: NoteAsrTask = {
  taskId: '22222222-2222-4222-8222-222222222222',
  revision: '33333333-3333-4333-8333-333333333333',
  noteId: '44444444-4444-4444-8444-444444444444',
  versionId: '55555555-5555-4555-8555-555555555555',
  inputSha256: 'sha', manifestSha256: 'manifest', phase: 'NeedsAttention', language: 'zh',
  destination: {
    modelId: '66666666-6666-4666-8666-666666666666', modelRevision,
    endpoint: 'https://asr.example.test/v1/chat/completions', presetId: 'mimo-multimodal',
    presetVersion: '1', confirmed: true,
  },
  createdAt: '2026-10-31T10:00:00Z', updatedAt: '2026-10-31T10:10:00Z',
  totalSegments: 3, completedSegments: 2, failedSegments: 1, totalSamples: 48000,
  completedSamples: 32000, sampleRate: 16000, stopRequested: false,
  backgroundProcessingAvailable: true, route: 'cloud-multimodal', executionMode: 'batch',
}
const job: NoteAsrBatchJob = {
  id: '77777777-7777-4777-8777-777777777777',
  taskId: task.taskId,
  attemptId: '88888888-8888-4888-8888-888888888888',
  batchId: 'batch_synthetic',
  baseUrl: 'https://asr.example.test/v1/',
  model: 'mimo-v2.6-flash',
  consentRef: 'consent-1',
  completionWindow: '24h',
  status: 'Completed',
  statusRaw: 'Completed',
  outputImported: true,
  totalItems: 3,
  completedItems: 2,
  failedItems: 1,
  lastErrorCode: 'partial',
  inputFileId: 'file-input',
  outputFileId: 'file-output',
  errorFileId: 'file-error',
  inputExpireAt: '2026-11-30T10:00:00Z',
  batchExpiresAt: '2026-11-01T10:00:00Z',
  createdAt: '2026-10-31T10:00:00Z',
  updatedAt: '2026-10-31T10:30:00Z',
}
const config = {
  revision: '99999999-9999-4999-8999-999999999999',
  defaultRoute: 'cloud-multimodal', localModelId: null, localModelRevision: null,
  cloudConfigured: true, cloudModelId: task.destination?.modelId,
  consentVersion: 'consent-2', updatedAt: '2026-10-31T10:00:00Z',
}
const destination = {
  backgroundProcessingAvailable: true, displayName: 'MiMo Batch 目标',
  endpoint: 'https://asr.example.test/v1/chat/completions', languages: ['zh'],
  model: job.model, modelId: task.destination!.modelId, modelRevision: task.destination!.modelRevision,
  presetId: 'mimo-multimodal', presetVersion: '1',
}

beforeEach(() => {
  cleanup()
  localStorage.clear()
  vi.clearAllMocks()
  Object.defineProperties(HTMLElement.prototype, {
    hasPointerCapture: { configurable: true, value: () => false },
    setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined },
    scrollIntoView: { configurable: true, value: () => undefined },
  })
  api.asrBatchJobs.mockResolvedValue([job])
  api.asrBatchPoll.mockResolvedValue(job)
  api.asrBatchCancel.mockResolvedValue({ ...job, status: 'Cancelling' })
  api.asrRouteConfig.mockResolvedValue(config)
  api.noteAsrDestinations.mockResolvedValue({ total: 1, items: [destination] })
  api.asrBatchRetryFailed.mockResolvedValue({
    job: { ...job, id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', taskId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' },
    retryScope: 'failed_missing',
    sourceTaskId: task.taskId,
    task: { ...task, taskId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' },
  })
  api.noteAsrTaskOrNull.mockResolvedValue(null)
})

function page(taskValue = task, binding = serverBinding) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(<QueryClientProvider client={client}>
    <NoteAsrOwnerBatches task={taskValue} userId={userId} serverBinding={binding} />
  </QueryClientProvider>)
}

async function prepareRetry() {
  await screen.findByText('失败项 Batch 重试')
  await userEvent.type(screen.getByLabelText('原因说明'), '只恢复失败分片')
  await userEvent.click(screen.getByRole('checkbox', { name: '确认只重试Batch失败分片并产生现金费用' }))
  await userEvent.click(screen.getByRole('button', { name: '创建失败项 Batch 子任务' }))
}

describe('NoteAsrOwnerBatches', () => {
  it('shows Completed with failed items as partial and keeps cancel asynchronous', async () => {
    page()
    expect((await screen.findAllByText('部分成功')).length).toBeGreaterThan(0)
    expect(screen.getByText(/成功请求已经产生费用/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: '请求取消' })).toBeNull()
    api.asrBatchJobs.mockResolvedValue([{ ...job, status: 'IN_PROGRESS', outputImported: false, failedItems: 0, completedItems: 1 }])
    cleanup()
    page()
    await userEvent.click(await screen.findByRole('button', { name: '请求取消' }))
    await userEvent.click(screen.getByRole('checkbox', { name: /确认请求取消/ }))
    await userEvent.click(screen.getByRole('button', { name: '确认异步取消' }))
    await waitFor(() => expect(api.asrBatchCancel).toHaveBeenCalledWith(job.id))
    expect(await screen.findByText(/取消请求已发送/)).toBeTruthy()
    expect(screen.getByText('运行中')).toBeTruthy()
  })

  it('freezes failed-only retry and reuses the exact body after a lost response', async () => {
    api.asrBatchRetryFailed.mockRejectedValueOnce(new Error('response lost'))
    page()
    await prepareRetry()
    await waitFor(() => expect(api.asrBatchRetryFailed).toHaveBeenCalledOnce())
    const [sourceJobId, body] = api.asrBatchRetryFailed.mock.calls[0]!
    expect(sourceJobId).toBe(job.id)
    expect(body).toMatchObject({
      sourceJobId: job.id,
      route: { route: 'cloud-multimodal', executionMode: 'batch', batchScopeConfirmed: true },
      target: { modelId: destination.modelId, consentRef: job.consentRef, completionWindow: '24h' },
      reason: { kind: 'manual', text: '只恢复失败分片' },
    })
    expect(localStorage.getItem(noteAsrBatchFailedIntentScope(userId, serverBinding, job.id))).toContain(body.clientTaskId)
    await userEvent.click(screen.getByRole('button', { name: '按原标识重新提交失败项' }))
    await waitFor(() => expect(api.asrBatchRetryFailed).toHaveBeenCalledTimes(2))
    expect(api.asrBatchRetryFailed.mock.calls[1]).toEqual(api.asrBatchRetryFailed.mock.calls[0])
    expect(localStorage.getItem(noteAsrBatchFailedIntentScope(userId, serverBinding, job.id))).toBeNull()
  })

  it('does not reuse a frozen failed-only intent on another server', async () => {
    api.asrBatchRetryFailed.mockRejectedValueOnce(new Error('response lost'))
    const view = page()
    await prepareRetry()
    const originalId = api.asrBatchRetryFailed.mock.calls[0]?.[1].clientTaskId as string
    expect(await screen.findByText(/已冻结失败项子任务标识/)).toBeTruthy()
    const other = 'https://other.synthetic.invalid'
    view.rerender(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <NoteAsrOwnerBatches task={task} userId={userId} serverBinding={other} />
    </QueryClientProvider>)
    await waitFor(() => expect(screen.queryByText(originalId)).toBeNull())
    expect(localStorage.getItem(noteAsrBatchFailedIntentScope(userId, serverBinding, job.id))).toContain(originalId)
    expect(localStorage.getItem(noteAsrBatchFailedIntentScope(userId, other, job.id))).toBeNull()
    expect(api.asrBatchRetryFailed).toHaveBeenCalledOnce()
  })

  it('keeps a missing provider id as UNKNOWN without poll, cancel, or automatic resubmit', async () => {
    api.asrBatchJobs.mockResolvedValue([{
      ...job, batchId: null, inputFileId: null, outputFileId: null, errorFileId: null,
      status: 'Unknown', statusRaw: 'unknown', outputImported: false, failedItems: null,
    }])
    page()
    expect(await screen.findByText('UNKNOWN 待人工核对')).toBeTruthy()
    expect(screen.getByText(/不得自动创建新任务、重新上传或假定成功/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: '立即查询状态' })).toBeNull()
    expect(screen.queryByRole('button', { name: '请求取消' })).toBeNull()
    expect(screen.queryByText('失败项 Batch 重试')).toBeNull()
    expect(api.asrBatchPoll).not.toHaveBeenCalled()
    expect(api.asrBatchCancel).not.toHaveBeenCalled()
    expect(api.asrBatchRetryFailed).not.toHaveBeenCalled()
  })

  it('blocks a shallow corrupt failed intent without any POST', async () => {
    localStorage.setItem(noteAsrBatchFailedIntentScope(userId, serverBinding, job.id), JSON.stringify({
      kind: 'failed',
      expectedUserId: userId,
      sourceTaskId: task.taskId,
      request: {
        clientTaskId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
        sourceJobId: job.id,
        expectedUserId: userId,
        route: { route: 'cloud-multimodal' },
        target: { modelId: destination.modelId },
      },
    }))
    page()
    expect(await screen.findByText(/失败项 Batch 提交意图无法读取/)).toBeTruthy()
    expect((screen.getByRole('button', { name: '创建失败项 Batch 子任务' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.asrBatchRetryFailed).not.toHaveBeenCalled()
  })

  it('does not reset reason or confirmation when the jobs query refetches', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    render(<QueryClientProvider client={client}>
      <NoteAsrOwnerBatches task={task} userId={userId} serverBinding={serverBinding} />
    </QueryClientProvider>)
    await screen.findByText('失败项 Batch 重试')
    await userEvent.type(screen.getByLabelText('原因说明'), '轮询中保留')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认只重试Batch失败分片并产生现金费用' }))
    api.asrBatchJobs.mockResolvedValue([{ ...job }])
    await client.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'] })
    await waitFor(() => expect(api.asrBatchJobs.mock.calls.length).toBeGreaterThan(1))
    expect((screen.getByLabelText('原因说明') as HTMLTextAreaElement).value).toBe('轮询中保留')
    expect((screen.getByRole('checkbox', { name: '确认只重试Batch失败分片并产生现金费用' }) as HTMLButtonElement).getAttribute('aria-checked')).toBe('true')
  })

  it('never guesses a failed-retry target by model name', async () => {
    page({ ...task, destination: null })
    expect(await screen.findByText('原 Batch 目标当前不可用')).toBeTruthy()
    expect((screen.getByRole('button', { name: '创建失败项 Batch 子任务' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.type(screen.getByLabelText('原因说明'), '不按名称猜目标')
    expect(api.asrBatchRetryFailed).not.toHaveBeenCalled()
  })
})
