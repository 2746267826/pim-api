// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrReprocessPanel } from './note-asr-reprocess-panel'
import type { NoteAsrTask } from './api'
import { noteAsrReprocessIntentScope } from './note-asr-reprocess-intent'

const api = vi.hoisted(() => ({
  asrRouteConfig: vi.fn(), noteAsrDestinations: vi.fn(), models: vi.fn(), asrPresets: vi.fn(),
  asrReprocessHistory: vi.fn(), asrRoutingAttempts: vi.fn(),
  asrReprocess: vi.fn(), asrRoutingAttemptStop: vi.fn(),
}))
vi.mock('./api', () => ({ aiApi: api }))

beforeAll(() => {
  if (!HTMLElement.prototype.hasPointerCapture) HTMLElement.prototype.hasPointerCapture = () => false
  if (!HTMLElement.prototype.releasePointerCapture) HTMLElement.prototype.releasePointerCapture = () => {}
  if (!HTMLElement.prototype.scrollIntoView) HTMLElement.prototype.scrollIntoView = () => {}
})
afterEach(cleanup)

const userId = '11111111-1111-4111-8111-111111111111'
const serverBinding = 'https://synthetic.invalid'
const task: NoteAsrTask = {
  taskId: '22222222-2222-4222-8222-222222222222',
  revision: '33333333-3333-4333-8333-333333333333',
  noteId: '44444444-4444-4444-8444-444444444444',
  versionId: '55555555-5555-4555-8555-555555555555', inputSha256: 'sha', manifestSha256: 'manifest',
  phase: 'Failed', language: 'zh', destination: {
    modelId: '66666666-6666-4666-8666-666666666666', modelRevision: 'model-rev',
    endpoint: 'https://asr.example.test/v1/chat/completions', presetId: 'mimo-multimodal', presetVersion: '1', confirmed: true,
  },
  createdAt: '2026-10-31T10:00:00Z', updatedAt: '2026-10-31T10:10:00Z',
  totalSegments: 3, completedSegments: 1, failedSegments: 2, totalSamples: 48000, completedSamples: 16000,
  sampleRate: 16000, stopRequested: false, backgroundProcessingAvailable: true, route: 'cloud-multimodal',
  attemptId: '77777777-7777-4777-8777-777777777777', routeConfigRevision: '88888888-8888-4888-8888-888888888888',
  routeConsentVersion: 'consent-v1', executionMode: 'realtime',
}
const config = {
  revision: '88888888-8888-4888-8888-888888888888', defaultRoute: 'cloud-multimodal',
  localModelId: null, localModelRevision: null, cloudConfigured: true,
  cloudModelId: '66666666-6666-4666-8666-666666666666', consentVersion: 'consent-v1',
  updatedAt: '2026-10-31T10:00:00Z',
}
const model = {
  id: config.cloudModelId, revision: 'model-rev', name: 'MiMo Flash', baseUrl: 'https://asr.example.test/v1/',
  model: 'mimo-v2.6-flash', enabled: true, capabilities: ['Transcription'], hasApiKey: true,
  timeoutSeconds: 600, maxOutputTokens: 8192, createdAt: config.updatedAt, updatedAt: config.updatedAt,
  asrPresetId: 'mimo-multimodal', asrBatchBaseUrl: null, asrPrompt: null, asrPromptRevision: null,
}
const preset = {
  id: 'mimo-multimodal', version: '1', name: 'MiMo 多模态', defaultBaseUrl: model.baseUrl,
  operationPath: 'chat/completions', defaultModel: model.model, models: [model.model],
  formats: ['wav', 'mp3', 'flac', 'm4a', 'ogg'], languages: ['auto', 'zh', 'en'],
  maxAudioBytes: 50 * 1024 * 1024, streaming: false,
}
const destination = {
  backgroundProcessingAvailable: true, displayName: 'MiMo Flash 多模态目标',
  endpoint: model.baseUrl + preset.operationPath, languages: preset.languages,
  model: model.model, modelId: model.id, modelRevision: model.revision,
  presetId: preset.id, presetVersion: preset.version,
}
const attempt = {
  attemptId: '77777777-7777-4777-8777-777777777777', ownerId: userId, taskId: task.taskId,
  noteId: task.noteId, versionId: task.versionId, inputSha256: 'sha', route: 'cloud-multimodal',
  state: 'Running', stopRequested: false, configRevision: config.revision, consentVersion: 'consent-v1',
  modelId: model.id, modelRevision: model.revision, presetId: preset.id, presetVersion: preset.version,
  model: model.model, endpoint: 'https://asr.example.test/v1/chat/completions', promptTemplateRevision: 'p1',
  promptRevision: 'p1', executionMode: 'realtime', providerHandle: null, localModelId: null,
  localModelRevision: null, language: 'zh', fencingToken: 1, expectedSegments: 3, completedSegments: 1,
  failedSegments: 0, lastErrorCode: null, lastErrorMessage: null,
  createdAt: task.createdAt, updatedAt: task.updatedAt,
}
const history = {
  parentTaskId: task.taskId, total: 1, offset: 0, limit: 10,
  items: [{
    parentTaskId: task.taskId, taskId: '99999999-9999-4999-8999-999999999999',
    revision: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', versionId: task.versionId,
    reason: 'manual: 已核对旧结果', phase: 'Succeeded', route: 'cloud-multimodal',
    executionMode: 'realtime', createdAt: task.createdAt, updatedAt: task.updatedAt,
  }],
}

beforeEach(() => {
  vi.restoreAllMocks()
  vi.clearAllMocks()
  localStorage.clear()
  api.asrRouteConfig.mockResolvedValue(config)
  api.noteAsrDestinations.mockResolvedValue({ total: 1, items: [destination] })
  api.asrReprocessHistory.mockResolvedValue(history)
  api.asrRoutingAttempts.mockResolvedValue([attempt])
  api.asrRoutingAttemptStop.mockResolvedValue({ ...attempt, stopRequested: true, state: 'Unknown' })
  api.asrReprocess.mockResolvedValue({ task: { ...task, taskId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }, parentTaskId: task.taskId, reason: 'manual' })
})

function page() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={qc}><NoteAsrReprocessPanel task={task} userId={userId} serverBinding={serverBinding} /></QueryClientProvider>)
}

describe('NoteAsrReprocessPanel', () => {
  it('展示不可变重处理历史与原因，不自动重跑', async () => {
    page()
    expect(await screen.findByText('重处理历史（不可变）')).toBeTruthy()
    expect(await screen.findByText(/manual: 已核对旧结果/)).toBeTruthy()
    expect(api.asrReprocess).not.toHaveBeenCalled()
    expect(api.asrRoutingAttemptStop).not.toHaveBeenCalled()
  })

  it('强制云端重跑需原因与显式确认，发送真实云路由和当前配置版本', async () => {
    page()
    await screen.findAllByText(/MiMo Flash 多模态目标/)
    expect(api.asrReprocess).not.toHaveBeenCalled()
    await userEvent.type(screen.getByLabelText('原因说明'), '已核对原音频和目标')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认云端强制重处理并可能产生费用' }))
    await userEvent.click(screen.getByRole('button', { name: '确认创建云端重处理任务' }))
    await waitFor(() => expect(api.asrReprocess).toHaveBeenCalledOnce())
    const [sourceTaskId, body] = api.asrReprocess.mock.calls[0]!
    expect(sourceTaskId).toBe(task.taskId)
    expect(body).toMatchObject({
      expectedUserId: userId, expectedRevision: task.revision,
      reason: { kind: 'manual', text: '已核对原音频和目标' },
      route: {
        route: 'cloud-multimodal', expectedConfigRevision: config.revision,
        expectedConsentVersion: config.consentVersion, executionMode: 'realtime',
        confirmed: true, batchScopeConfirmed: false,
      },
      consent: {
        modelId: model.id, modelRevision: model.revision, endpoint: destination.endpoint,
        presetId: preset.id, presetVersion: preset.version, confirmed: true,
      },
    })
    expect(body.clientTaskId).toMatch(/^[0-9a-f-]{36}$/)
    expect(api.models).not.toHaveBeenCalled()
    expect(api.asrPresets).not.toHaveBeenCalled()
    expect(await screen.findByText(/已按原标识提交重处理/)).toBeTruthy()
  })

  it('shows an invalid last cloud target without guessing another cloud model', async () => {
    api.noteAsrDestinations.mockResolvedValue({ total: 0, items: [] })
    page()
    expect(await screen.findByText('上次云端目标不可用')).toBeTruthy()
    expect((screen.getByRole('button', { name: '确认创建云端重处理任务' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.asrReprocess).not.toHaveBeenCalled()
  })

  it('allows an explicit local rerun without cloud credentials or consent', async () => {
    const localTask: NoteAsrTask = { ...task, route: 'local', executionMode: 'realtime' }
    api.noteAsrDestinations.mockResolvedValue({ total: 0, items: [] })
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={qc}><NoteAsrReprocessPanel task={localTask} userId={userId} serverBinding={serverBinding} /></QueryClientProvider>)
    expect(await screen.findByText('本机重处理')).toBeTruthy()
    await userEvent.type(screen.getByLabelText('原因说明'), '本机重新核对')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认创建本机重处理任务' }))
    await userEvent.click(screen.getByRole('button', { name: '确认创建本机重处理任务' }))
    await waitFor(() => expect(api.asrReprocess).toHaveBeenCalledOnce())
    const body = api.asrReprocess.mock.calls[0]?.[1]
    expect(body).toMatchObject({ route: { route: 'local' }, reason: { text: '本机重新核对' } })
    expect(body.consent).toBeUndefined()
  })

  it('freezes a failed reprocess request and re-saves the same client task id', async () => {
    api.asrReprocess.mockRejectedValueOnce(new Error('response lost'))
    page()
    await screen.findAllByText(/MiMo Flash 多模态目标/)
    await userEvent.type(screen.getByLabelText('原因说明'), '核对后重跑')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认云端强制重处理并可能产生费用' }))
    await userEvent.click(screen.getByRole('button', { name: '确认创建云端重处理任务' }))
    const first = api.asrReprocess.mock.calls[0]?.[1]
    expect((await screen.findAllByText(/已冻结原提交标识/)).length).toBeGreaterThan(0)
    expect((screen.getByLabelText('原因说明') as HTMLTextAreaElement).disabled).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: '按原标识保存' }))
    await waitFor(() => expect(api.asrReprocess).toHaveBeenCalledTimes(2))
    expect(api.asrReprocess.mock.calls[1]?.[1]).toEqual(first)
  })

  it('blocks a corrupt stored reprocess intent instead of replacing it', async () => {
    localStorage.setItem(noteAsrReprocessIntentScope(userId, serverBinding, task.taskId), '{broken')
    page()
    expect(await screen.findByText(/重处理提交意图无法读取/)).toBeTruthy()
    expect((screen.getByRole('button', { name: '确认创建云端重处理任务' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.asrReprocess).not.toHaveBeenCalled()
  })

  it('does not POST when the frozen reprocess intent cannot be persisted', async () => {
    page()
    await screen.findAllByText(/MiMo Flash 多模态目标/)
    await userEvent.type(screen.getByLabelText('原因说明'), '保存失败不发送')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认云端强制重处理并可能产生费用' }))
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota') })
    await userEvent.click(screen.getByRole('button', { name: '确认创建云端重处理任务' }))
    expect(await screen.findByText(/本机无法保存冻结的提交标识/)).toBeTruthy()
    expect(api.asrReprocess).not.toHaveBeenCalled()
  })

  it('停止执行需显式确认且只调用一次，失败不自动重试', async () => {
    page()
    await userEvent.click(await screen.findByRole('button', { name: '停止该执行' }))
    expect(api.asrRoutingAttemptStop).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认停止后续分片' }))
    await userEvent.click(screen.getByRole('button', { name: '确认停止' }))
    await waitFor(() => expect(api.asrRoutingAttemptStop).toHaveBeenCalledOnce())
    expect(api.asrRoutingAttemptStop).toHaveBeenCalledWith(attempt.attemptId)
  })
})
