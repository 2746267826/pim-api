// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AsrCall, CallAudio } from './asr-call'
import { ModelEditor } from './model-editor'
import type { AsrPreset, Model, ModelSettings } from './api'
import { ApiError } from '@/api/client'

const api = vi.hoisted(() => ({ asrPresets: vi.fn(), asrCall: vi.fn(), asrUrlCall: vi.fn(), asrUrlLookup: vi.fn(), asrLookup: vi.fn(), detail: vi.fn(), audio: vi.fn(), audioContent: vi.fn() }))
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
  api.asrUrlCall.mockResolvedValue({ logId, status: 'Succeeded', responseText: 'URL转写结果', replayed: false, errorMessage: null,
    sourceOrigin: 'https://audio.example.test', preflightSha256: 'f'.repeat(64), preflightVerified: true,
    providerFetchVerified: false, warning: '本地预检不证明服务商读取相同字节。' })
  api.asrUrlLookup.mockResolvedValue({ logId, status: 'Succeeded', responseText: 'URL转写结果', replayed: true, errorMessage: null,
    sourceOrigin: 'https://audio.example.test', preflightSha256: 'f'.repeat(64), preflightVerified: true,
    providerFetchVerified: false, warning: '本地预检不证明服务商读取相同字节。' })
  api.asrLookup.mockResolvedValue({ logId, status: 'Cancelled', responseText: null, replayed: true, errorMessage: '已取消' })
  api.detail.mockResolvedValue({ responseRawJson: '{}' })
})
afterEach(() => { cleanup(); qc.clear() })

function useMultimodalPreset() {
  preset = { ...preset, id: 'mimo-multimodal', name: 'MiMo 多模态', formats: ['wav', 'mp3', 'flac', 'm4a', 'ogg'], maxAudioBytes: 50 * 1024 * 1024 }
  model = { ...model, asrPresetId: preset.id }
  api.asrPresets.mockResolvedValue([preset])
}

async function chooseUrlMode() {
  await screen.findByText(/预设：MiMo/)
  await userEvent.click(screen.getByRole('combobox', { name: '音频来源' }))
  await userEvent.click(await screen.findByRole('option', { name: '公开 HTTPS URL' }))
}

async function enterUrl(value = 'https://audio.example.test/课堂录音.wav?token=synthetic') {
  await chooseUrlMode()
  await userEvent.type(screen.getByLabelText('公开HTTPS音频地址'), value)
}

async function confirmUrl() {
  await userEvent.click(screen.getByRole('checkbox', { name: '确认PIM服务器读取公开HTTPS音频' }))
  await userEvent.click(screen.getByRole('checkbox', { name: '确认ASR服务将再次读取该URL且内容可能不同' }))
  await userEvent.click(screen.getByRole('checkbox', { name: '确认本次URL转写目标与可能费用' }))
}

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

  it('MiMo多模态展示五格式/50MiB，Flash推荐且Pro明确不推荐长音频', async () => {
    preset = { ...preset, id: 'mimo-multimodal', name: 'MiMo 多模态', defaultModel: 'mimo-v2.6-flash',
      models: ['mimo-v2.6-flash', 'mimo-v2.6-pro'], formats: ['wav', 'mp3', 'flac', 'm4a', 'ogg'],
      maxAudioBytes: 50 * 1024 * 1024 }
    api.asrPresets.mockResolvedValue([preset])
    const save = vi.fn().mockResolvedValue({})
    render(wrap(<ModelEditor model={null} initialAsr save={save} close={() => undefined} />))
    await waitFor(() => expect((screen.getByRole('combobox', { name: 'ASR接入预设' }) as HTMLButtonElement).disabled).toBe(false))
    await userEvent.click(screen.getByRole('combobox', { name: 'ASR接入预设' }))
    await userEvent.click(await screen.findByRole('option', { name: 'MiMo 多模态 · v1' }))
    expect(screen.getByText(/wav \/ mp3 \/ flac \/ m4a \/ ogg · 单音频最多 50 MiB/)).toBeTruthy()
    await userEvent.click(screen.getByRole('combobox', { name: '模型标识' }))
    expect(await screen.findByRole('option', { name: 'mimo-v2.6-flash（推荐，长音频默认）' })).toBeTruthy()
    await userEvent.click(screen.getByRole('option', { name: 'mimo-v2.6-pro（Pro，不推荐长音频）' }))
    expect(screen.getByText(/MiMo Pro 不推荐用于长音频/)).toBeTruthy()
  })

  it('旧ASR预设不提供公开URL来源', async () => {
    render(wrap(page())); await screen.findByText(/预设：MiMo/)
    await userEvent.click(screen.getByRole('combobox', { name: '音频来源' }))
    expect(screen.queryByRole('option', { name: '公开 HTTPS URL' })).toBeNull()
    expect(screen.getByRole('option', { name: '本机文件' })).toBeTruthy()
  })

  it('仅多模态预设显示URL模式，非法HTTPS地址零调用', async () => {
    useMultimodalPreset(); render(wrap(page())); await enterUrl('http://audio.example.test/file.wav')
    await screen.findByText('只允许HTTPS地址')
    expect((screen.getByRole('button', { name: '确认发送URL并转写' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.asrUrlCall).not.toHaveBeenCalled()
    expect(document.querySelector('audio')).toBeNull()
  })

  it('URL模式需三项确认，修改地址撤销确认并按typed body只提交一次', async () => {
    useMultimodalPreset(); render(wrap(page()))
    await enterUrl()
    const send = () => screen.getByRole('button', { name: '确认发送URL并转写' }) as HTMLButtonElement
    expect(send().disabled).toBe(true)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认PIM服务器读取公开HTTPS音频' }))
    expect(send().disabled).toBe(true)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认ASR服务将再次读取该URL且内容可能不同' }))
    expect(send().disabled).toBe(true)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认本次URL转写目标与可能费用' }))
    expect(send().disabled).toBe(false)
    await userEvent.click(screen.getByRole('combobox', { name: '识别语言' })); await userEvent.click(screen.getByRole('option', { name: '中文' }))
    expect(send().disabled).toBe(true)
    expect((screen.getByRole('checkbox', { name: '确认PIM服务器读取公开HTTPS音频' }) as HTMLInputElement).checked).toBe(false)
    await confirmUrl()
    const urlInput = screen.getByLabelText('公开HTTPS音频地址')
    await userEvent.clear(urlInput)
    await userEvent.type(urlInput, 'https://audio.example.test/changed.wav?token=changed')
    expect((screen.getByRole('checkbox', { name: '确认PIM服务器读取公开HTTPS音频' }) as HTMLInputElement).checked).toBe(false)
    await confirmUrl(); await userEvent.click(send())
    await screen.findByLabelText('实际URL ASR转写')
    expect(api.asrUrlCall).toHaveBeenCalledOnce()
    const body = api.asrUrlCall.mock.calls[0]?.[0]
    expect(body).toEqual(expect.objectContaining({
      expectedUserId: owner,
      language: 'zh',
      audio: { name: 'changed.wav', format: 'wav', url: 'https://audio.example.test/changed.wav?token=changed', sourceConfirmed: true, providerFetchConfirmed: true },
      consent: { modelId: model.id, modelRevision: model.revision, endpoint: model.baseUrl + preset.operationPath, presetId: preset.id, presetVersion: preset.version, confirmed: true },
    }))
    expect(body.clientRequestId).toEqual(expect.any(String))
    expect(screen.queryByDisplayValue(/token=changed/)).toBeNull()
    expect(localStorage.length).toBe(0)
  })

  it('URL响应丢失后先核对，404后才允许同ID显式重发', async () => {
    useMultimodalPreset(); api.asrUrlCall.mockRejectedValueOnce(new Error('response lost'))
    render(wrap(page())); await enterUrl(); await confirmUrl()
    await userEvent.click(screen.getByRole('button', { name: '确认发送URL并转写' }))
    await screen.findByText(/response lost/)
    const firstBody = api.asrUrlCall.mock.calls[0]?.[0]
    expect(screen.queryByRole('button', { name: '按原标识重发URL' })).toBeNull()
    api.asrUrlLookup.mockRejectedValueOnce(new ApiError('not found', 404, 404))
    await userEvent.click(screen.getByRole('button', { name: '核对URL ASR状态' }))
    await screen.findByText(/服务端尚未找到该URL请求标识/)
    expect(api.asrUrlCall).toHaveBeenCalledOnce()
    await userEvent.click(screen.getByRole('button', { name: '按原标识重发URL' }))
    await screen.findByLabelText('实际URL ASR转写')
    expect(api.asrUrlCall).toHaveBeenCalledTimes(2)
    expect(api.asrUrlCall.mock.calls[1]?.[0]).toEqual(firstBody)
  })

  it('URL核对找到已有记录时不重发', async () => {
    useMultimodalPreset(); api.asrUrlCall.mockRejectedValueOnce(new Error('response lost'))
    render(wrap(page())); await enterUrl(); await confirmUrl()
    await userEvent.click(screen.getByRole('button', { name: '确认发送URL并转写' }))
    await screen.findByText(/response lost/)
    await userEvent.click(screen.getByRole('button', { name: '核对URL ASR状态' }))
    await screen.findByLabelText('实际URL ASR转写')
    expect(api.asrUrlLookup).toHaveBeenCalledWith(api.asrUrlCall.mock.calls[0]?.[0].clientRequestId)
    expect(api.asrUrlCall).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: '按原标识重发URL' })).toBeNull()
  })

  it('URL提交失败时脱敏完整地址与查询值并保留冻结意图', async () => {
    useMultimodalPreset()
    const source = 'https://audio.example.test/file.wav?token=synthetic-secret&sig=private'
    api.asrUrlCall.mockRejectedValueOnce(new Error(`读取失败：${source}；查询 ${new URL(source).search}`))
    render(wrap(page())); await enterUrl(source); await confirmUrl()
    await userEvent.click(screen.getByRole('button', { name: '确认发送URL并转写' }))
    await screen.findByText(/读取失败：\[已脱敏源URL\]/)
    expect(screen.getByText(/已冻结公开URL调用/)).toBeTruthy()
    expect(document.body.textContent).not.toContain(source)
    expect(document.body.textContent).not.toContain('token=synthetic-secret')
    expect(localStorage.length).toBe(0)
    expect(api.asrUrlCall).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: '按原标识重发URL' })).toBeNull()
  })

  it('URL预检通过但Provider未确认时仍显示风险，非成功状态不自动重试', async () => {
    useMultimodalPreset()
    api.asrUrlCall.mockResolvedValue({ logId, status: 'TimedOut', responseText: null, replayed: false,
      errorMessage: 'URL ASR上游超时，结果UNKNOWN，未自动重试', sourceOrigin: 'https://audio.example.test',
      preflightSha256: 'f'.repeat(64), preflightVerified: true, providerFetchVerified: false,
      warning: '本地预检不证明服务商读取相同字节。' })
    render(wrap(page())); await enterUrl(); await confirmUrl()
    await userEvent.click(screen.getByRole('button', { name: '确认发送URL并转写' }))
    await screen.findByText(/状态 TimedOut 未确认为成功/)
    expect(screen.getByText(/Provider再次读取：未核验/)).toBeTruthy()
    expect(screen.getByText(/本地预检不证明服务商读取相同字节/)).toBeTruthy()
    expect(api.asrUrlCall).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: '按原标识重发URL' })).toBeNull()
  })

  it('ASR调用完成后读取现有详情快照，显示红色audio_tokens缺失而不伪报成功', async () => {
    api.detail.mockResolvedValue({ responseRawJson: JSON.stringify({
      recordFormat: 'mimo-multimodal-v1', finishReason: 'stop', contentSource: 'content',
      flags: { empty: false, repetition: false, truncation: false, timestampFallback: true },
      transcript: { text: '没有可信时间轴的逐字文本', segments: [], issues: [] },
      upstream: { choices: [{ message: { content: '没有可信时间轴的逐字文本' }, finish_reason: 'stop' }], usage: { audio_tokens: 100 } },
    }) })
    render(wrap(page())); await chooseAudio(); await userEvent.click(screen.getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: '开始转写并记录' }))
    expect(await screen.findByText('音频处理未确认')).toBeTruthy()
    expect(screen.getByText(/usage\.audio_tokens=100/)).toBeTruthy()
    expect(screen.getByText(/时间戳为回退状态/)).toBeTruthy()
    expect(api.detail).toHaveBeenCalledWith(logId, expect.any(AbortSignal))
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
