// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrStartPanel } from './note-asr-start-panel'
import { noteAsrStartIntentScope } from './note-asr-start-intent'

const ai = vi.hoisted(() => ({
  noteAsrDestinations: vi.fn(), asrRouteConfig: vi.fn(), noteAsrStart: vi.fn(), noteAsrTaskOrNull: vi.fn(),
  models: vi.fn(), asrPresets: vi.fn(),
}))
vi.mock('./api', () => ({ aiApi: ai }))

const owner = '11111111-1111-4111-8111-111111111111'
const serverBinding = 'https://synthetic.invalid'
const versionId = '22222222-2222-4222-8222-222222222222'
const destination = {
  backgroundProcessingAvailable: true,
  displayName: '合成云端ASR',
  endpoint: 'https://synthetic.invalid/v1/audio/transcriptions',
  languages: ['auto', 'zh'],
  model: 'synthetic-asr',
  modelId: '33333333-3333-4333-8333-333333333333',
  modelRevision: '44444444-4444-4444-8444-444444444444',
  presetId: 'openai-asr',
  presetVersion: 'v1',
}
const config = {
  cloudConfigured: true,
  cloudModelId: destination.modelId,
  consentVersion: 'consent-1',
  defaultRoute: 'cloud-asr',
  localModelId: null,
  localModelRevision: null,
  revision: '55555555-5555-4555-8555-555555555555',
  updatedAt: '2026-10-30T00:00:00Z',
}
const task = {
  taskId: '66666666-6666-4666-8666-666666666666',
  versionId,
  phase: 'Queued',
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  render(<QueryClientProvider client={client}><NoteAsrStartPanel userId={owner} serverBinding={serverBinding} versionId={versionId} /></QueryClientProvider>)
  return client
}
async function confirmCloud() {
  await userEvent.click(await screen.findByRole('checkbox', { name: '确认创建录音转写任务' }))
  await userEvent.click(screen.getByRole('button', { name: '确认创建云端任务' }))
}

beforeEach(() => {
  cleanup()
  localStorage.clear()
  vi.restoreAllMocks()
  vi.resetAllMocks()
  Object.defineProperties(HTMLElement.prototype, {
    hasPointerCapture: { configurable: true, value: () => false },
    setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined },
    scrollIntoView: { configurable: true, value: () => undefined },
  })
  ai.noteAsrDestinations.mockResolvedValue({ total: 1, items: [destination] })
  ai.asrRouteConfig.mockResolvedValue(config)
  ai.noteAsrStart.mockResolvedValue(task)
  ai.noteAsrTaskOrNull.mockResolvedValue(null)
})

describe('NoteAsrStartPanel', () => {
  it('starts cloud only after explicit consent using the public destination catalog', async () => {
    renderPanel()
    await screen.findByText(/合成云端ASR/)
    expect(ai.noteAsrStart).not.toHaveBeenCalled()
    await confirmCloud()
    await waitFor(() => expect(ai.noteAsrStart).toHaveBeenCalledOnce())
    expect(ai.noteAsrStart.mock.calls[0]?.[0]).toEqual(expect.objectContaining({
      expectedUserId: owner,
      versionId,
      consent: {
        modelId: destination.modelId,
        modelRevision: destination.modelRevision,
        endpoint: destination.endpoint,
        presetId: destination.presetId,
        presetVersion: destination.presetVersion,
        confirmed: true,
      },
      route: expect.objectContaining({ route: 'cloud-asr', configRevision: config.revision }),
    }))
    expect(ai.models).not.toHaveBeenCalled()
    expect(ai.asrPresets).not.toHaveBeenCalled()
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, serverBinding, versionId))).toBeNull()
  })

  it('does not switch to cloud when the saved default is local without a local model', async () => {
    ai.asrRouteConfig.mockResolvedValue({ ...config, defaultRoute: 'local' })
    renderPanel()
    expect(await screen.findByText('没有本机模型')).toBeTruthy()
    expect(ai.noteAsrStart).not.toHaveBeenCalled()
    expect(ai.models).not.toHaveBeenCalled()
  })

  it('creates a local queue task with nullable consent and no cloud credentials', async () => {
    ai.noteAsrDestinations.mockResolvedValue({ total: 0, items: [] })
    ai.asrRouteConfig.mockResolvedValue({
      ...config, defaultRoute: 'local', cloudConfigured: false, cloudModelId: null,
      localModelId: '99999999-9999-4999-8999-999999999999',
      localModelRevision: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    })
    renderPanel()
    expect(await screen.findByText('本机执行')).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认创建录音转写任务' }))
    await userEvent.click(screen.getByRole('button', { name: '创建本机排队任务' }))
    await waitFor(() => expect(ai.noteAsrStart).toHaveBeenCalledOnce())
    expect(ai.noteAsrStart.mock.calls[0]?.[0]).toEqual(expect.objectContaining({
      expectedUserId: owner,
      versionId,
      consent: null,
      route: expect.objectContaining({ route: 'local', configRevision: config.revision }),
    }))
    expect(ai.models).not.toHaveBeenCalled()
    expect(ai.asrPresets).not.toHaveBeenCalled()
  })

  it('keeps one frozen task id after a lost response and re-saves it without a new task', async () => {
    ai.noteAsrStart.mockRejectedValueOnce(new Error('network response lost'))
    const first = renderPanel()
    await screen.findByText(/合成云端ASR/)
    await confirmCloud()
    const originalId = ai.noteAsrStart.mock.calls[0]?.[0].taskId as string
    expect(await screen.findByText(/已冻结原任务标识/)).toBeTruthy()
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, serverBinding, versionId))).toContain(originalId)
    first.clear()
    cleanup()

    const second = renderPanel()
    await screen.findByText(/已冻结原任务标识/)
    await userEvent.click(screen.getByRole('button', { name: '按原标识保存' }))
    await waitFor(() => expect(ai.noteAsrStart).toHaveBeenCalledTimes(2))
    expect(ai.noteAsrStart.mock.calls[1]?.[0]).toEqual(ai.noteAsrStart.mock.calls[0]?.[0])
    expect(ai.noteAsrStart.mock.calls[1]?.[0].taskId).toBe(originalId)
    second.clear()
  })

  it('reconciles the existing task by id without resending', async () => {
    ai.noteAsrStart.mockRejectedValueOnce(new Error('timeout'))
    renderPanel()
    await screen.findByText(/合成云端ASR/)
    await confirmCloud()
    const body = ai.noteAsrStart.mock.calls[0]?.[0]
    ai.noteAsrTaskOrNull.mockResolvedValue({ ...task, taskId: body.taskId })
    await screen.findByText(/已冻结原任务标识/)
    await userEvent.click(screen.getByRole('button', { name: '核对原任务' }))
    await waitFor(() => expect(ai.noteAsrTaskOrNull).toHaveBeenCalledWith(body.taskId))
    expect(ai.noteAsrStart).toHaveBeenCalledOnce()
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, serverBinding, versionId))).toBeNull()
  })

  it('shows an explicit invalid saved cloud profile instead of a stuck loader', async () => {
    ai.asrRouteConfig.mockResolvedValue({ ...config, cloudModelId: '77777777-7777-4777-8777-777777777777' })
    renderPanel()
    expect(await screen.findByText(/已保存云端目标失效/)).toBeTruthy()
    expect((screen.getByRole('button', { name: '云端目标失效' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('blocks the entry when a stored intent cannot be parsed', async () => {
    localStorage.setItem(noteAsrStartIntentScope(owner, serverBinding, versionId), '{not-json')
    renderPanel()
    expect(await screen.findByText(/提交意图无法读取/)).toBeTruthy()
    expect((screen.getByRole('button', { name: '确认创建云端任务' }) as HTMLButtonElement).disabled).toBe(true)
    expect(ai.noteAsrStart).not.toHaveBeenCalled()
  })

  it('does not POST when the frozen intent cannot be persisted', async () => {
    renderPanel()
    await screen.findByText(/合成云端ASR/)
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota') })
    await confirmCloud()
    expect(await screen.findByText(/本机无法保存冻结的提交标识/)).toBeTruthy()
    expect(ai.noteAsrStart).not.toHaveBeenCalled()
  })

  it('does not reuse an intent from the previous account or material version', async () => {
    ai.noteAsrStart.mockRejectedValueOnce(new Error('lost'))
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    const view = render(<QueryClientProvider client={client}><NoteAsrStartPanel userId={owner} serverBinding={serverBinding} versionId={versionId} /></QueryClientProvider>)
    await screen.findByText(/合成云端ASR/)
    await confirmCloud()
    const originalId = ai.noteAsrStart.mock.calls[0]?.[0].taskId as string
    await screen.findByText(/已冻结原任务标识/)
    const nextVersion = '88888888-8888-4888-8888-888888888888'
    view.rerender(<QueryClientProvider client={client}><NoteAsrStartPanel userId={owner} serverBinding={serverBinding} versionId={nextVersion} /></QueryClientProvider>)
    await waitFor(() => expect(screen.queryByText(originalId)).toBeNull())
    expect(screen.queryByText(/已冻结原任务标识/)).toBeNull()
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, serverBinding, nextVersion))).toBeNull()
    expect(ai.noteAsrStart).toHaveBeenCalledOnce()
    client.clear()
  })

  it('isolates destination cache and frozen intent for the same owner on a different server', async () => {
    const otherBinding = 'https://other.synthetic.invalid'
    ai.noteAsrStart.mockRejectedValueOnce(new Error('lost'))
    ai.asrRouteConfig
      .mockResolvedValueOnce(config)
      .mockResolvedValueOnce({ ...config, cloudConfigured: false, cloudModelId: null })
    ai.noteAsrDestinations
      .mockResolvedValueOnce({ total: 1, items: [destination] })
      .mockResolvedValueOnce({ total: 0, items: [] })
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    const view = render(<QueryClientProvider client={client}><NoteAsrStartPanel userId={owner} serverBinding={serverBinding} versionId={versionId} /></QueryClientProvider>)
    await screen.findByText(/合成云端ASR/)
    await confirmCloud()
    const originalId = ai.noteAsrStart.mock.calls[0]?.[0].taskId as string
    await screen.findByText(/已冻结原任务标识/)
    view.rerender(<QueryClientProvider client={client}><NoteAsrStartPanel userId={owner} serverBinding={otherBinding} versionId={versionId} /></QueryClientProvider>)
    await waitFor(() => expect(ai.noteAsrDestinations).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.queryByText(originalId)).toBeNull())
    expect(await screen.findByText(/云端尚未配置/)).toBeTruthy()
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, serverBinding, versionId))).toContain(originalId)
    expect(localStorage.getItem(noteAsrStartIntentScope(owner, otherBinding, versionId))).toBeNull()
    expect(ai.noteAsrStart).toHaveBeenCalledOnce()
    client.clear()
  })
})
