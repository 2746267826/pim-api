// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrReprocessPanel } from './note-asr-reprocess-panel'
import type { NoteAsrTask } from './api'

const api = vi.hoisted(() => ({
  asrRouteConfig: vi.fn(), models: vi.fn(), asrPresets: vi.fn(),
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
  vi.clearAllMocks()
  api.asrRouteConfig.mockResolvedValue(config)
  api.models.mockResolvedValue([model])
  api.asrPresets.mockResolvedValue([preset])
  api.asrReprocessHistory.mockResolvedValue(history)
  api.asrRoutingAttempts.mockResolvedValue([attempt])
  api.asrRoutingAttemptStop.mockResolvedValue({ ...attempt, stopRequested: true, state: 'Unknown' })
  api.asrReprocess.mockResolvedValue({ task: { ...task, taskId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }, parentTaskId: task.taskId, reason: 'manual' })
})

function page() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={qc}><NoteAsrReprocessPanel task={task} userId={userId} /></QueryClientProvider>)
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
    await screen.findByText(/MiMo 多模态 v1/)
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
      consent: { modelId: model.id, modelRevision: model.revision, presetId: preset.id, presetVersion: preset.version, confirmed: true },
    })
    expect(body.clientTaskId).toMatch(/^[0-9a-f-]{36}$/)
    expect(await screen.findByText(/已创建新的云端重处理任务/)).toBeTruthy()
  })

  it('没有可用云端目标时不提供重处理调用', async () => {
    api.asrRouteConfig.mockResolvedValue({ ...config, cloudModelId: null, cloudConfigured: false })
    page()
    expect(await screen.findByText('没有可用的云端目标')).toBeTruthy()
    expect((screen.getByRole('button', { name: '确认创建云端重处理任务' }) as HTMLButtonElement).disabled).toBe(true)
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
