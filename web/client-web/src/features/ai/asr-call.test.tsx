// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AsrCall, CallAudio } from './asr-call'
import { ModelEditor } from './model-editor'
import type { AsrPreset, Model, ModelSettings } from './api'

const api = vi.hoisted(() => ({ asrPresets: vi.fn(), asrCall: vi.fn(), asrLookup: vi.fn(), audio: vi.fn(), audioContent: vi.fn() }))
vi.mock('./api', () => ({ aiApi: api }))
vi.mock('@/lib/notify', () => ({ notifyError: vi.fn() }))
const owner = '11111111-1111-4111-8111-111111111111', logId = '22222222-2222-4222-8222-222222222222'
let model: Model, settings: ModelSettings, preset: AsrPreset, qc: QueryClient
const wave = new Uint8Array(364)
wave.set(new TextEncoder().encode('RIFF'), 0); new DataView(wave.buffer).setUint32(4, 356, true); wave.set(new TextEncoder().encode('WAVEfmt '), 8)
new DataView(wave.buffer).setUint32(16, 16, true); new DataView(wave.buffer).setUint16(20, 1, true); new DataView(wave.buffer).setUint16(22, 1, true)
new DataView(wave.buffer).setUint32(24, 16000, true); new DataView(wave.buffer).setUint32(28, 32000, true); new DataView(wave.buffer).setUint16(32, 2, true); new DataView(wave.buffer).setUint16(34, 16, true)
wave.set(new TextEncoder().encode('data'), 36); new DataView(wave.buffer).setUint32(40, 320, true)
const wrap = (content: React.ReactNode) => <QueryClientProvider client={qc}>{content}</QueryClientProvider>
const page = () => <AsrCall userId={owner} models={[model]} settings={settings} changed={() => undefined} showDetail={() => undefined} renderStatus={(state) => <span>{state}</span>} />
async function chooseAudio() {
  await screen.findByText(/预设：MiMo/)
  await userEvent.upload(screen.getByLabelText('本次音频'), new File([wave], '示例.wav', { type: 'audio/wav' }))
}
beforeEach(() => {
  vi.resetAllMocks(); Object.defineProperties(URL, { createObjectURL: { configurable: true, value: vi.fn(() => 'blob:synthetic-audio') }, revokeObjectURL: { configurable: true, value: vi.fn() } })
  // jsdom lacks pointer-capture/scroll APIs used by the real Radix Select. Browser QA uses the native APIs.
  Object.defineProperties(HTMLElement.prototype, { hasPointerCapture: { configurable: true, value: () => false },
    setPointerCapture: { configurable: true, value: () => undefined }, releasePointerCapture: { configurable: true, value: () => undefined },
    scrollIntoView: { configurable: true, value: () => undefined } })
  preset = { id: 'mimo-asr', version: '1', name: 'MiMo 语音识别', defaultBaseUrl: 'https://synthetic-asr.invalid/v1/', operationPath: 'chat/completions',
    defaultModel: 'mimo-v2.5-asr', models: ['mimo-v2.5-asr'], formats: ['wav', 'mp3'], languages: ['auto', 'zh', 'en'], maxAudioBytes: 4 * 1024 * 1024, streaming: false }
  model = { id: '33333333-3333-4333-8333-333333333333', revision: '44444444-4444-4444-8444-444444444444', name: 'ASR模型', model: preset.defaultModel, baseUrl: preset.defaultBaseUrl,
    asrPresetId: preset.id, capabilities: ['Transcription'], enabled: true, hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024, createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }
  settings = { revision: model.revision, enabled: true, textModelId: null, visionModelId: null, transcriptionModelId: model.id }
  qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  api.asrPresets.mockResolvedValue([preset]); api.asrCall.mockResolvedValue({ logId, status: 'Succeeded', responseText: '完整转写', replayed: false, errorMessage: null })
  api.asrLookup.mockResolvedValue({ logId, status: 'Cancelled', responseText: null, replayed: true, errorMessage: '已取消' })
})
afterEach(() => { cleanup(); qc.clear() })

describe('ASR预设和调用', () => {
  it('预设自动填入默认配置且以独立凭据保存，页面无需识别厂商名称', async () => {
    const another = { ...preset, id: 'test-only-asr', name: '测试新增厂商', defaultBaseUrl: 'http://test.invalid/api/', operationPath: 'transcribe', defaultModel: 'test-asr', models: ['test-asr'] }
    api.asrPresets.mockResolvedValue([preset, another]); const save = vi.fn().mockResolvedValue({})
    render(wrap(<ModelEditor model={null} initialAsr save={save} close={() => undefined} />))
    expect(screen.getByRole('heading', { name: '添加 ASR 配置' })).toBeTruthy()
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'ASR接入预设' }) as HTMLButtonElement).disabled).toBe(false))
    await userEvent.click(screen.getByRole('combobox', { name: 'ASR接入预设' })); await userEvent.click(await screen.findByRole('option', { name: '测试新增厂商 · v1' }))
    expect((screen.getByLabelText('API 根地址') as HTMLInputElement).value).toBe(another.defaultBaseUrl)
    expect((screen.getByLabelText('模型标识') as HTMLInputElement).value).toBe('test-asr')
    expect((screen.getByLabelText('模型标识') as HTMLInputElement).readOnly).toBe(true)
    await userEvent.type(screen.getByLabelText('API Key'), 'synthetic-ui-asr-key'); await userEvent.click(screen.getByRole('button', { name: '保存模型' }))
    await waitFor(() => expect(save).toHaveBeenCalledOnce())
    expect(save.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ asrPresetId: another.id, baseUrl: another.defaultBaseUrl, model: 'test-asr', capabilities: ['Transcription'] }))
    expect(localStorage.length).toBe(0)
  })

  it('单音频载荷、语言、预设版本和具体目标均绑定并要求确认，只发送一次', async () => {
    render(wrap(page())); await chooseAudio()
    const send = screen.getByRole('button', { name: '开始转写并记录' }) as HTMLButtonElement
    expect(send.disabled).toBe(true); expect(screen.getByText(model.baseUrl + preset.operationPath)).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox'))
    await userEvent.click(screen.getByRole('combobox', { name: '识别语言' })); await userEvent.click(screen.getByRole('option', { name: '中文' }))
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(false)
    await userEvent.click(screen.getByRole('checkbox')); await userEvent.click(send); await screen.findByLabelText('实际ASR转写')
    expect(api.asrCall).toHaveBeenCalledOnce(); const body = api.asrCall.mock.calls[0]?.[0]
    expect(body).toEqual(expect.objectContaining({ expectedUserId: owner, language: 'zh', audio: { name: '示例.wav', format: 'wav', base64: btoa(String.fromCharCode(...wave)) },
      consent: { modelId: model.id, modelRevision: model.revision, endpoint: model.baseUrl + preset.operationPath, presetId: preset.id, presetVersion: preset.version, confirmed: true } }))
    expect(body).not.toHaveProperty('prompt'); expect(body).not.toHaveProperty('systemPrompt')
    await userEvent.click(send); expect(api.asrCall).toHaveBeenCalledOnce()
  })

  it('修改目标版本后旧确认不能授权新目的地', async () => {
    const view = render(wrap(page())); await chooseAudio(); await userEvent.click(screen.getByRole('checkbox'))
    model = { ...model, revision: '55555555-5555-4555-8555-555555555555', baseUrl: 'https://changed.invalid/v1/' }; view.rerender(wrap(page()))
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(false)
    expect((screen.getByRole('button', { name: '开始转写并记录' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.asrCall).not.toHaveBeenCalled()
  })

  it('取消后只核对已有状态，不重发音频', async () => {
    api.asrCall.mockImplementation((_body, signal: AbortSignal) => new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true })))
    render(wrap(page())); await chooseAudio(); await userEvent.click(screen.getByRole('checkbox')); await userEvent.click(screen.getByRole('button', { name: '开始转写并记录' }))
    await waitFor(() => expect(api.asrCall).toHaveBeenCalledOnce()); await userEvent.click(screen.getByRole('button', { name: '取消ASR等待' }))
    await screen.findByText(/已请求取消/); await userEvent.click(screen.getByRole('button', { name: '核对ASR状态' })); await screen.findByText('已取消')
    expect(api.asrCall).toHaveBeenCalledOnce(); expect(api.asrLookup).toHaveBeenCalledWith(api.asrCall.mock.calls[0]?.[0].clientRequestId)
  })

  it('大于预设限制的文件拒绝提交，旧ASR声明不会借用新预设', async () => {
    render(wrap(page())); await screen.findByText(/预设：MiMo/)
    await userEvent.upload(screen.getByLabelText('本次音频'), new File([new Uint8Array(4 * 1024 * 1024 + 1)], 'big.wav', { type: 'audio/wav' }))
    await screen.findByText(/音频格式或大小不符合/); expect(api.asrCall).not.toHaveBeenCalled()
    cleanup(); model = { ...model, asrPresetId: null }; render(wrap(page()))
    await screen.findByText(/旧能力声明不能直接转写/); expect((screen.getByRole('button', { name: '选择音频' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('原音频只为发起账号读取并在关闭时释放URL', async () => {
    const denied = render(wrap(<CallAudio logId={logId} owner="another" viewer={owner} />))
    expect(screen.getByText('原音频仅调用发起账号可查看。')).toBeTruthy(); expect(api.audio).not.toHaveBeenCalled(); denied.unmount()
    api.audio.mockResolvedValue({ id: model.id, name: '示例.wav', contentType: 'audio/wav', sizeBytes: wave.length, sha256: 'synthetic', stored: true })
    api.audioContent.mockResolvedValue(new Blob([wave], { type: 'audio/wav' }))
    const own = render(wrap(<CallAudio logId={logId} owner={owner} viewer={owner} />)); await screen.findByLabelText('本次原音频')
    expect(api.audioContent).toHaveBeenCalledOnce(); own.unmount(); expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-audio')
  })
})
