// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AiSettingsPage } from './ai-settings-page'
import type { CallDetail, Model, ModelSettings } from './api'

const fixture = vi.hoisted(() => ({
  user: { id: '11111111-1111-4111-8111-111111111111', role: 'admin' },
  api: { models: vi.fn(), settings: vi.fn(), saveModel: vi.fn(), removeModel: vi.fn(), saveSettings: vi.fn(),
    call: vi.fn(), lookup: vi.fn(), history: vi.fn(), detail: vi.fn(), usage: vi.fn(), legacy: vi.fn() },
}))
vi.mock('./api', () => ({ aiApi: fixture.api }))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => ({ user: fixture.user }) }))
vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))
const id = '22222222-2222-4222-8222-222222222222'
const revision = '33333333-3333-4333-8333-333333333333'
const logId = '44444444-4444-4444-8444-444444444444'
let models: Model[]
let settings: ModelSettings
let client: QueryClient

function open() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: 0 } } })
  return render(<QueryClientProvider client={client}><AiSettingsPage /></QueryClientProvider>)
}

beforeEach(() => {
  vi.resetAllMocks(); fixture.user.role = 'admin'
  models = [{ id, revision, name: '文字主模型', baseUrl: 'https://model.fixture/v1/', model: 'fixture-model', enabled: true,
    capabilities: ['Text'], hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024,
    createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }]
  settings = { revision, enabled: true, textModelId: id, visionModelId: null, transcriptionModelId: null }
  fixture.api.models.mockImplementation(async () => [...models])
  fixture.api.settings.mockImplementation(async () => ({ ...settings }))
  fixture.api.saveSettings.mockImplementation(async (body) => {
    settings = { ...settings, ...body, revision: '55555555-5555-4555-8555-555555555555' }; return settings
  })
  fixture.api.saveModel.mockImplementation(async (existing, body) => {
    const value = { ...models[0], ...body, id: existing ?? '66666666-6666-4666-8666-666666666666', hasApiKey: true }
    delete value.apiKey; delete value.expectedRevision
    models = existing ? models.map((m) => m.id === existing ? value : m) : [...models, value]; return value
  })
  fixture.api.call.mockResolvedValue({ logId, status: 'Succeeded', responseText: '完整回复\n没有裁剪', errorMessage: null, replayed: false })
  fixture.api.lookup.mockResolvedValue({ logId, status: 'Cancelled', responseText: null, errorMessage: '用户取消', replayed: true })
  fixture.api.history.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 })
  fixture.api.usage.mockResolvedValue({ requestCount: 3, successCount: 1, failureCount: 1, runningCount: 1,
    promptTokens: 7, completionTokens: 11, totalTokens: 18, estimatedCost: 0, byModel: [], byModule: [], byPurpose: [], byStatus: [],
    unknownTokenUsageCount: 2, knownCostRequestCount: 0, averageDurationMs: 123 })
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
})
