// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router'
import { AiSettingsPage } from './ai-settings-page'
import type { AiProviderConnection, AsrPreset, CallDetail, Model, ModelSettings } from './api'

const fixture = vi.hoisted(() => ({
  user: { id: '11111111-1111-4111-8111-111111111111', role: 'admin' },
  api: { models: vi.fn(), settings: vi.fn(), connections: vi.fn(), createConnection: vi.fn(), updateConnection: vi.fn(),
    connectionUsageLimit: vi.fn(), saveConnectionUsageLimit: vi.fn(), connectionUsage: vi.fn(), usageReminders: vi.fn(),
    markUsageReminderRead: vi.fn(), saveModel: vi.fn(), removeModel: vi.fn(), saveSettings: vi.fn(),
    call: vi.fn(), lookup: vi.fn(), history: vi.fn(), detail: vi.fn(), usage: vi.fn(), legacy: vi.fn(), asrPresets: vi.fn(),
    testConnection: vi.fn(), connectionTestLatest: vi.fn(), connectionTests: vi.fn() },
}))
vi.mock('./api', () => ({ aiApi: fixture.api }))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => ({ user: fixture.user }) }))
vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))
const id = '22222222-2222-4222-8222-222222222222'
const revision = '33333333-3333-4333-8333-333333333333'
const logId = '44444444-4444-4444-8444-444444444444'
const asrId = '77777777-7777-4777-8777-777777777777'
const preset: AsrPreset = { id: 'mimo-asr', version: '1', name: 'MiMo 语音识别', defaultBaseUrl: 'https://synthetic-asr.invalid/v1/',
  operationPath: 'chat/completions', defaultModel: 'mimo-v2.5-asr', models: ['mimo-v2.5-asr'], formats: ['wav', 'mp3'],
  languages: ['auto', 'zh', 'en'], maxAudioBytes: 4 * 1024 * 1024, streaming: false }
let models: Model[]
let settings: ModelSettings
let connections: AiProviderConnection[]
let client: QueryClient

function LocationProbe() {
  const location = useLocation()
  return <output data-testid="location">{location.pathname + location.search}</output>
}

function open(path = '/settings/ai') {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: 0 } } })
  return render(<MemoryRouter initialEntries={[path]}><QueryClientProvider client={client}><AiSettingsPage /><LocationProbe /></QueryClientProvider></MemoryRouter>)
}

beforeEach(() => {
  vi.resetAllMocks(); fixture.user.role = 'admin'
  Object.defineProperties(HTMLElement.prototype, {
    hasPointerCapture: { configurable: true, value: () => false },
    setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined },
    scrollIntoView: { configurable: true, value: () => undefined },
  })
  models = [{ id, revision, name: '文字主模型', baseUrl: 'https://model.fixture/v1/', model: 'fixture-model', enabled: true,
    capabilities: ['Text'], hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024,
    createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
  settings = { revision, enabled: true, textModelId: id, visionModelId: null, transcriptionModelId: null }
  connections = [{ id: '88888888-8888-4888-8888-888888888888', revision, name: '共享模型连接', modelCount: 1,
    limit: { connectionId: '88888888-8888-4888-8888-888888888888', revision, enabled: false, period: 'month', warningPercent: 80,
      tokenLimit: null, costLimit: null, currency: null, timeZoneId: 'Asia/Shanghai' },
    updatedAt: '2026-10-27T00:00:00Z' }]
  fixture.api.models.mockImplementation(async () => [...models])
  fixture.api.settings.mockImplementation(async () => ({ ...settings }))
  fixture.api.connections.mockImplementation(async () => [...connections])
  fixture.api.saveSettings.mockImplementation(async (body) => {
    settings = { ...settings, ...body, revision: '55555555-5555-4555-8555-555555555555' }; return settings
  })
  fixture.api.saveModel.mockImplementation(async (existing, body) => {
    const value = { ...models[0], ...body, id: existing ?? '66666666-6666-4666-8666-666666666666', hasApiKey: true }
    delete value.apiKey; delete value.expectedRevision
    models = existing ? models.map((m) => m.id === existing ? value : m) : [...models, value]; return value
  })
  fixture.api.asrPresets.mockResolvedValue([preset])
  fixture.api.connectionTests.mockResolvedValue([])
  fixture.api.connectionTestLatest.mockResolvedValue(null)
  fixture.api.call.mockResolvedValue({ logId, status: 'Succeeded', responseText: '完整回复\n没有裁剪', errorMessage: null, replayed: false })
  fixture.api.lookup.mockResolvedValue({ logId, status: 'Cancelled', responseText: null, errorMessage: '用户取消', replayed: true })
  fixture.api.history.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 })
  fixture.api.usage.mockResolvedValue({ requestCount: 3, successCount: 1, failureCount: 1, runningCount: 1,
    promptTokens: 7, completionTokens: 11, totalTokens: 18, estimatedCost: 0, byModel: [], byModule: [], byPurpose: [], byStatus: [],
    unknownTokenUsageCount: 2, knownCostRequestCount: 0, averageDurationMs: 123 })
  fixture.api.connectionUsage.mockResolvedValue({ connectionId: connections[0].id, connectionName: connections[0].name,
    period: 'month', cycleKey: '2026-11', startsAt: '2026-11-01T00:00:00Z', endsAt: '2026-12-01T00:00:00Z',
    timeZoneId: 'Asia/Shanghai', requestCount: 0, runningRequestCount: 0, incompleteRequestCount: 0,
    tokenReportedRequestCount: 0, tokenUnknownRequestCount: 0, totalTokens: 0, costKnownRequestCount: 0,
    costUnknownRequestCount: 0, estimatedCost: 0, currency: null, tokenLimit: null, costLimit: null,
    warningPercent: 80, tokenStatus: 'unknown', costStatus: 'unknown', status: 'unknown', coverageNotes: [] })
  fixture.api.usageReminders.mockResolvedValue([])
})
afterEach(() => { cleanup(); client?.clear() })

describe('AI文字调用完整页面', () => {
  it('新增模型保存类型和凭据，成功后清空凭据表单', async () => {
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '添加模型' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('配置名称'), '新模型')
    await userEvent.type(within(dialog).getByLabelText('API 根地址'), 'https://new.fixture/v1')
    await userEvent.type(within(dialog).getByLabelText('模型标识'), 'new-model')
    await userEvent.type(within(dialog).getByLabelText('API Key'), 'fake-ui-key')
    await userEvent.click(within(dialog).getByLabelText('视觉'))
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(fixture.api.saveModel).toHaveBeenCalledWith(undefined, expect.objectContaining({ name: '新模型', apiKey: 'fake-ui-key', capabilities: ['Text', 'Vision'] }))
    expect(screen.queryByDisplayValue('fake-ui-key')).toBeNull()
  })

  it('新模型可以绑定已有AI服务连接，保存后纳入连接级用量限制', async () => {
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '添加模型' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('配置名称'), '共享连接模型')
    await userEvent.type(within(dialog).getByLabelText('API 根地址'), 'https://shared.fixture/v1')
    await userEvent.type(within(dialog).getByLabelText('模型标识'), 'shared-model')
    await userEvent.type(within(dialog).getByLabelText('API Key'), 'shared-key')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'AI服务连接' }))
    await userEvent.click(await screen.findByRole('option', { name: /共享模型连接/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(fixture.api.saveModel).toHaveBeenCalledWith(undefined, expect.objectContaining({
      providerConnectionId: connections[0].id,
    }))
  })

  it('编辑不回显或伪造凭据，留空保留旧密钥并携带版本', async () => {
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '编辑 文字主模型' }))
    const dialog = screen.getByRole('dialog')
    expect((within(dialog).getByLabelText('API Key（已保存）') as HTMLInputElement).value).toBe('')
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(fixture.api.saveModel).toHaveBeenCalled())
    expect(fixture.api.saveModel).toHaveBeenCalledWith(id, expect.objectContaining({ apiKey: null, expectedRevision: revision }))
  })

  it('编辑API地址时禁止沿用旧密钥，重新输入后才保存', async () => {
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '编辑 文字主模型' }))
    const dialog = screen.getByRole('dialog')
    const endpoint = within(dialog).getByLabelText('API 根地址')
    await userEvent.clear(endpoint)
    await userEvent.type(endpoint, 'https://moved.fixture/v1')
    await screen.findByText(/API 地址已变化，必须重新输入 API Key/)
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    expect(fixture.api.saveModel).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog')).toBeTruthy()

    await userEvent.type(within(dialog).getByLabelText('API Key（已保存）'), 'new-address-key')
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(fixture.api.saveModel).toHaveBeenCalledWith(id, expect.objectContaining({
      baseUrl: 'https://moved.fixture/v1', apiKey: 'new-address-key', expectedRevision: revision,
    }))
  })

  it('单次发送精确保留输入、绑定当前账号，重复点击不重复发模型请求', async () => {
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    const prompt = '  原始上下文\npassword=普通文本  '
    await userEvent.type(screen.getByLabelText('正文与上下文'), prompt)
    await userEvent.type(screen.getByLabelText('系统提示（可选）'), '系统上下文')
    const send = screen.getByRole('button', { name: '发送并记录' })
    await userEvent.click(send); await screen.findByLabelText('实际模型回复')
    expect(fixture.api.call).toHaveBeenCalledOnce()
    expect(fixture.api.call.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ prompt, systemPrompt: '系统上下文', modelId: null, expectedUserId: fixture.user.id }))
    expect(fixture.api.call.mock.calls[0]?.[0].clientRequestId).toMatch(/^[0-9a-f-]{36}$/)
    await userEvent.click(send); expect(fixture.api.call).toHaveBeenCalledOnce()
    expect(screen.getByLabelText('实际模型回复').textContent).toBe('完整回复\n没有裁剪')
  })

  it('取消与核对仅查已有记录，不自动重发', async () => {
    fixture.api.call.mockImplementation((_body, signal: AbortSignal) => new Promise((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true })
    }))
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.type(screen.getByLabelText('正文与上下文'), '等待中的输入')
    await userEvent.click(screen.getByRole('button', { name: '发送并记录' }))
    await userEvent.click(screen.getByRole('button', { name: '取消等待' }))
    await screen.findByText(/已请求取消/)
    await userEvent.click(screen.getByRole('button', { name: '核对本次状态' }))
    await screen.findByText('用户取消')
    expect(fixture.api.lookup).toHaveBeenCalledWith(fixture.api.call.mock.calls[0]?.[0].clientRequestId)
    expect(fixture.api.call).toHaveBeenCalledOnce()
  })

  it('调用详情展示实际消息和回复，统计不把未知用量或费用解释为零', async () => {
    const detail = { id: logId, status: 'Succeeded', purpose: 'ai.text-call', module: 'system', model: 'fixture-model',
      modelProfileName: '文字主模型', startedAt: '2026-10-27T00:00:00Z', finishReason: 'stop', requestMessagesJson: '[{"role":"user","content":"<script>正文</script>"}]',
      requestPayloadJson: '{}', responseRawJson: '{}', responseText: '逐字回复\n第二行', contentRedacted: false,
      usage: { promptTokens: 7, completionTokens: 11, totalTokens: 18 }, durationMs: 123 } as CallDetail
    fixture.api.detail.mockResolvedValue(detail)
    fixture.api.history.mockResolvedValue({ items: [detail], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 })
    open(); await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByText('调用记录', { exact: true }))
    await userEvent.click(await screen.findByRole('button', { name: /文字主模型/ }))
    await screen.findByRole('dialog')
    expect((screen.getByLabelText('实际回复') as HTMLTextAreaElement).value).toBe(detail.responseText)
    expect((screen.getByLabelText('实际请求消息／上下文') as HTMLTextAreaElement).value).toContain('<script>正文</script>')
    expect([...document.scripts].some((s) => s.textContent?.includes('正文'))).toBe(false)
    await userEvent.click(within(screen.getByRole('dialog')).getAllByRole('button', { name: '关闭' })[0]!)
    await userEvent.click(screen.getByText('统计', { exact: true }))
    await screen.findByText('50.0%')
    expect(screen.getByText(/2 条未完整上报/)).toBeTruthy()
    expect(screen.getByText(/费用未提供，不能按零费用解释/)).toBeTruthy()
  })

  it('普通账号不读取任何管理员AI数据', () => {
    fixture.user.role = 'user'; open()
    expect(screen.getByText('需要管理员权限')).toBeTruthy()
    expect(fixture.api.models).not.toHaveBeenCalled(); expect(fixture.api.history).not.toHaveBeenCalled()
  })

  it('ASR query tab shows configuration state and keeps the selected tab in the URL', async () => {
    models = [...models, { id: asrId, revision, name: '课堂语音模型', baseUrl: preset.defaultBaseUrl, model: preset.defaultModel, enabled: true,
      capabilities: ['Transcription'], hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024, asrPresetId: preset.id,
      createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
    settings = { ...settings, transcriptionModelId: asrId }
    open('/settings/ai?tab=asr')

    expect(await screen.findByRole('heading', { name: 'ASR 转写配置' })).toBeTruthy()
    expect(screen.getByText('已配置')).toBeTruthy()
    expect(screen.getByText(/MiMo 语音识别 v1/)).toBeTruthy()
    expect(screen.getByText(/密钥只保存在服务端，保存后不再回显/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '添加 ASR 配置' })).toBeTruthy()
    expect(fixture.api.asrPresets).toHaveBeenCalledOnce()
    const enabled = screen.getByRole('switch', { name: '启用 AI / ASR 全局直连' })
    expect((enabled as HTMLButtonElement).getAttribute('aria-checked')).toBe('true')
    await userEvent.click(enabled)
    await waitFor(() => expect(fixture.api.saveSettings).toHaveBeenCalledWith(expect.objectContaining({ enabled: false })))
    expect(await screen.findByText('待完成')).toBeTruthy()

    await userEvent.click(screen.getByText('统计', { exact: true }))
    expect(screen.getByTestId('location').textContent).toBe('/settings/ai?tab=usage')
  })

  it('saves explicit purpose defaults while preserving omitted fields', async () => {
    const secondId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    models = [...models, { ...models[0], id: secondId, name: '第二文字模型' }]
    settings = { ...settings, noteOrganizationModelId: id, noteTodoModelId: null, calendarPlanningModelId: null }
    open()
    await screen.findByRole('button', { name: '编辑 文字主模型' })

    expect(screen.getByRole('combobox', { name: '笔记整理默认模型' }).textContent).toContain('文字主模型')
    await userEvent.click(screen.getByRole('combobox', { name: '提取待办默认模型' }))
    await userEvent.click(await screen.findByRole('option', { name: '第二文字模型' }))

    await waitFor(() => expect(fixture.api.saveSettings).toHaveBeenCalledWith(expect.objectContaining({
      expectedRevision: revision,
      textModelId: id,
      noteOrganizationModelId: id,
      noteTodoModelId: secondId,
      calendarPlanningModelId: null,
    })))
  })

  it('tests a saved model only after explicit confirmation and shows a leak-free real-provider result', async () => {
    const testLogId = '99999999-9999-4999-8999-999999999999'
    fixture.api.testConnection.mockResolvedValue({
      clientRequestId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      modelId: id,
      modelRevision: revision,
      capability: 'Text',
      status: 'Succeeded',
      probe: 'text-minimal',
      responsePreview: 'OK',
      errorCode: null,
      errorMessage: null,
      logId: testLogId,
      durationMs: 42,
      replayed: false,
      testedAt: '2026-11-26T00:00:00Z',
      providerAttempted: true,
    })
    open()
    await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '测试连接 文字主模型' }))
    const dialog = screen.getByRole('dialog')
    expect((within(dialog).getByRole('button', { name: '开始测试' }) as HTMLButtonElement).disabled).toBe(true)
    expect(within(dialog).queryByDisplayValue(/fake-ui-key/)).toBeNull()

    await userEvent.click(within(dialog).getByRole('checkbox', { name: /我已核对模型、地址与能力/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: '开始测试' }))

    expect(await within(dialog).findByText('连接成功')).toBeTruthy()
    expect(fixture.api.testConnection).toHaveBeenCalledWith(id, {
      clientRequestId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      expectedRevision: revision,
      capability: 'Text',
    })
    expect(within(dialog).getByText('服务商请求已实际发出。')).toBeTruthy()
    expect(within(dialog).getByText('42 ms')).toBeTruthy()
    expect(within(dialog).getByText(/调用记录 99999999/)).toBeTruthy()
    expect(screen.queryByText(/fake-ui-key/)).toBeNull()
  })

  it('reopens the latest result for the saved revision and starts a new request only on explicit retest', async () => {
    const testLogId = '99999999-9999-4999-8999-999999999999'
    fixture.api.connectionTestLatest.mockResolvedValue({
      clientRequestId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      modelId: id,
      modelRevision: revision,
      capability: 'Text',
      status: 'Succeeded',
      probe: 'text-minimal',
      responsePreview: 'OK',
      errorCode: null,
      errorMessage: null,
      logId: testLogId,
      durationMs: 42,
      replayed: true,
      testedAt: '2026-11-26T00:00:00Z',
      providerAttempted: true,
    })
    open()
    await screen.findByRole('button', { name: '编辑 文字主模型' })
    await userEvent.click(screen.getByRole('button', { name: '测试连接 文字主模型' }))
    const dialog = screen.getByRole('dialog')

    expect(await within(dialog).findByText('连接成功')).toBeTruthy()
    expect(within(dialog).getByText('· 已有记录')).toBeTruthy()
    expect(fixture.api.testConnection).not.toHaveBeenCalled()
    expect(fixture.api.connectionTestLatest).toHaveBeenCalledWith(id, revision, 'Text')

    await userEvent.click(within(dialog).getByRole('button', { name: '重新测试（新请求）' }))
    expect(within(dialog).queryByText('连接成功')).toBeNull()
    expect((within(dialog).getByRole('button', { name: '开始测试' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /我已核对模型、地址与能力/ }))
    fixture.api.testConnection.mockResolvedValue({
      clientRequestId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      modelId: id,
      modelRevision: revision,
      capability: 'Text',
      status: 'Blocked',
      probe: 'text-minimal',
      responsePreview: null,
      errorCode: 'busy',
      errorMessage: '并发已满',
      logId: testLogId,
      durationMs: 3,
      replayed: false,
      testedAt: '2026-11-26T00:01:00Z',
      providerAttempted: false,
    })
    await userEvent.click(within(dialog).getByRole('button', { name: '开始测试' }))
    expect(await within(dialog).findByText('未发送服务商')).toBeTruthy()
    expect(fixture.api.testConnection).toHaveBeenCalledWith(id, expect.objectContaining({
      capability: 'Text', expectedRevision: revision,
    }))
  })

  it('marks a pass from an older revision as requiring retest in the model list', async () => {
    fixture.api.connectionTests.mockResolvedValue([{
      modelId: id,
      modelRevision: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      capability: 'Text',
      status: 'Succeeded',
      testedAt: '2026-11-25T00:00:00Z',
      providerAttempted: true,
      logId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
    }])
    open()

    expect(await screen.findByText('连接成功（旧修订，需重测）')).toBeTruthy()
    expect(fixture.api.connectionTests).toHaveBeenCalledOnce()
  })

  it('统计页展示连接级限制、周期用量与提醒入口', async () => {
    open('/settings/ai?tab=usage')
    expect(await screen.findByText('连接级用量限制')).toBeTruthy()
    expect(await screen.findByText('共享模型连接')).toBeTruthy()
    expect(await screen.findByText('当前周期用量')).toBeTruthy()
    expect(fixture.api.connectionUsage).toHaveBeenCalledWith(
      connections[0].id, 'month', undefined, expect.any(AbortSignal),
    )
    expect(fixture.api.usageReminders).toHaveBeenCalledWith(
      { connectionId: connections[0].id, limit: 50 }, expect.any(AbortSignal),
    )
  })

  it('marks ASR readiness as undetermined while presets load or fail', async () => {
    models = [...models, { id: asrId, revision, name: '课堂语音模型', baseUrl: preset.defaultBaseUrl, model: preset.defaultModel, enabled: true,
      capabilities: ['Transcription'], hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024, asrPresetId: preset.id,
      createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
    settings = { ...settings, transcriptionModelId: asrId }
    fixture.api.asrPresets.mockReturnValue(new Promise(() => undefined))
    const pending = open('/settings/ai?tab=asr')
    expect(await screen.findByText('待判定')).toBeTruthy()
    expect(screen.queryByText('待完成')).toBeNull()
    pending.unmount()

    fixture.api.asrPresets.mockRejectedValue(new Error('预设不可用'))
    open('/settings/ai?tab=asr')
    expect(await screen.findByText('待判定')).toBeTruthy()
    expect(screen.getByText(/ASR 接入预设加载失败，暂时无法判断/)).toBeTruthy()
    expect(screen.queryByText('待完成')).toBeNull()
  })

  it('marks MiMo Pro ASR configuration as not recommended for long audio', async () => {
    const proPreset = { ...preset, id: 'mimo-multimodal', name: 'MiMo 多模态', defaultModel: 'mimo-v2.6-flash',
      models: ['mimo-v2.6-flash', 'mimo-v2.6-pro'], formats: ['wav', 'mp3', 'flac', 'm4a', 'ogg'],
      maxAudioBytes: 50 * 1024 * 1024 }
    fixture.api.asrPresets.mockResolvedValue([proPreset])
    models = [...models, { id: asrId, revision, name: '长课堂 Pro 配置', baseUrl: proPreset.defaultBaseUrl, model: 'mimo-v2.6-pro', enabled: true,
      capabilities: ['Transcription'], hasApiKey: true, timeoutSeconds: 600, maxOutputTokens: 8192, asrPresetId: proPreset.id,
      createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
    settings = { ...settings, transcriptionModelId: asrId }
    open('/settings/ai?tab=asr')

    expect(await screen.findByText('Pro 不推荐长音频')).toBeTruthy()
    expect(screen.getByText(/MiMo 多模态 v1/)).toBeTruthy()
  })

  it('edits the shared ASR prompt and explicit MiMo Batch origin', async () => {
    const multimodal = { ...preset, id: 'mimo-multimodal', name: 'MiMo 多模态', defaultModel: 'mimo-v2.6-flash',
      models: ['mimo-v2.6-flash', 'mimo-v2.6-pro'], formats: ['wav', 'mp3', 'flac', 'm4a', 'ogg'],
      maxAudioBytes: 50 * 1024 * 1024 }
    fixture.api.asrPresets.mockResolvedValue([multimodal])
    models = [...models, { id: asrId, revision, name: 'MiMo Flash 长音频', baseUrl: multimodal.defaultBaseUrl,
      model: 'mimo-v2.6-flash', enabled: true, capabilities: ['Transcription'], hasApiKey: true,
      timeoutSeconds: 600, maxOutputTokens: 8192, asrPresetId: multimodal.id,
      asrPrompt: '旧的转写提示词', asrPromptRevision: 'prompt-rev-1',
      createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
    settings = { ...settings, transcriptionModelId: asrId }
    open('/settings/ai?tab=asr')

    await userEvent.click(await screen.findByRole('button', { name: '编辑 ASR MiMo Flash 长音频' }))
    const dialog = screen.getByRole('dialog')
    expect((within(dialog).getByLabelText('转写提示词') as HTMLTextAreaElement).value).toBe('旧的转写提示词')
    expect(within(dialog).getByText(/prompt-rev-1/)).toBeTruthy()
    await userEvent.clear(within(dialog).getByLabelText('转写提示词'))
    await userEvent.type(within(dialog).getByLabelText('转写提示词'), '逐字转写，不要分析或总结')
    await userEvent.type(within(dialog).getByLabelText('MiMo Batch API 根地址（可选）'), 'https://batch-api-test.xiaomimimo.com/v1')
    await userEvent.click(within(dialog).getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(fixture.api.saveModel).toHaveBeenCalledOnce())
    expect(fixture.api.saveModel).toHaveBeenCalledWith(asrId, expect.objectContaining({
      asrPresetId: 'mimo-multimodal',
      asrPrompt: '逐字转写，不要分析或总结',
      asrBatchBaseUrl: 'https://batch-api-test.xiaomimimo.com/v1',
      expectedRevision: revision,
    }))
  })

  it('legacy AI settings URL remains compatible and defaults to model settings', async () => {
    open()
    expect(await screen.findByRole('button', { name: '编辑 文字主模型' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'ASR 转写配置' })).toBeNull()
    expect(fixture.api.asrPresets).not.toHaveBeenCalled()
  })
})
