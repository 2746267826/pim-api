// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { CallImages, VisionCall } from './vision-call'
import type { Model, ModelSettings } from './api'

const api = vi.hoisted(() => ({ visionCall: vi.fn(), visionLookup: vi.fn(), images: vi.fn(), image: vi.fn() }))
vi.mock('./api', () => ({ aiApi: api }))
const userId = '11111111-1111-4111-8111-111111111111'
const logId = '22222222-2222-4222-8222-222222222222'
const png = 'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8oKHBwMDAxMDAwMDAAAAP8gFEwVJxKAAAAABJRU5ErkJggg=='
const bytes = Uint8Array.from(atob(png), (c) => c.charCodeAt(0))
let model: Model; let settings: ModelSettings; let qc: QueryClient
const wrap = (content: React.ReactNode) => <QueryClientProvider client={qc}>{content}</QueryClientProvider>
const page = () => <VisionCall userId={userId} models={[model]} settings={settings} changed={() => undefined} showDetail={() => undefined} renderStatus={(s) => <span>{s}</span>} />
async function select() {
  await userEvent.upload(screen.getByLabelText('本次图片'), [new File([bytes], '照片.png', { type: 'image/png' }), new File([bytes], '手写.png', { type: 'image/png' })])
  await userEvent.type(screen.getByLabelText('正文与图片问题'), '  完整正文\npassword=普通材料  ')
}
beforeEach(() => {
  vi.resetAllMocks()
  Object.defineProperties(URL, { createObjectURL: { configurable: true, value: vi.fn(() => 'blob:synthetic-image') }, revokeObjectURL: { configurable: true, value: vi.fn() } })
  model = { id: '33333333-3333-4333-8333-333333333333', revision: '44444444-4444-4444-8444-444444444444', name: '视觉模型', model: 'synthetic-vision', baseUrl: 'https://synthetic-model.invalid/v1/',
    enabled: true, capabilities: ['Vision'], hasApiKey: true, timeoutSeconds: 60, maxOutputTokens: 1024, createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z' }
  settings = { revision: model.revision, enabled: true, textModelId: null, visionModelId: model.id, transcriptionModelId: null }
  qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  api.visionCall.mockResolvedValue({ logId, status: 'Succeeded', responseText: '完整视觉回复', errorMessage: null, replayed: false })
  api.visionLookup.mockResolvedValue({ logId, status: 'Cancelled', responseText: null, errorMessage: '用户取消', replayed: true })
})
afterEach(() => { cleanup(); qc.clear() })

describe('多图视觉调用', () => {
  it('展示具体目的地并要求确认，精确保留正文、全部图片和目标版本，只发送一次', async () => {
    render(wrap(page())); await select()
    const send = screen.getByRole('button', { name: '发送图片并记录' }) as HTMLButtonElement
    expect(send.disabled).toBe(true); expect(screen.getByText(model.baseUrl)).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认将本次图片发送到上述模型地址' }))
    await userEvent.click(send); await screen.findByLabelText('实际视觉回复')
    expect(api.visionCall).toHaveBeenCalledOnce()
    expect(api.visionCall.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ expectedUserId: userId, prompt: '  完整正文\npassword=普通材料  ', imageCount: 2,
      images: [{ name: '照片.png', contentType: 'image/png', base64: png }, { name: '手写.png', contentType: 'image/png', base64: png }],
      consent: { modelId: model.id, modelRevision: model.revision, endpoint: model.baseUrl, confirmed: true } }))
    expect(send.disabled).toBe(true)
    await userEvent.click(send); expect(api.visionCall).toHaveBeenCalledOnce()
    expect((screen.getByLabelText('正文与图片问题') as HTMLTextAreaElement).readOnly).toBe(true)
  })

  it('目的地或版本刷新后旧确认失效，不把确认自动套到新配置', async () => {
    const view = render(wrap(page())); await select()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认将本次图片发送到上述模型地址' }))
    model = { ...model, revision: '55555555-5555-4555-8555-555555555555', baseUrl: 'https://changed-model.invalid/v1/' }
    view.rerender(wrap(page()))
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(false)
    expect((screen.getByRole('button', { name: '发送图片并记录' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.getByText(model.baseUrl)).toBeTruthy(); expect(api.visionCall).not.toHaveBeenCalled()
  })

  it('取消后只核对状态，图片和模型不会自动重发', async () => {
    api.visionCall.mockImplementation((_body, signal: AbortSignal) => new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true })))
    render(wrap(page())); await select()
    await userEvent.click(screen.getByRole('checkbox')); await userEvent.click(screen.getByRole('button', { name: '发送图片并记录' }))
    await waitFor(() => expect(api.visionCall).toHaveBeenCalledOnce())
    await userEvent.click(screen.getByRole('button', { name: '取消视觉等待' })); await screen.findByText(/已请求取消/)
    await userEvent.click(screen.getByRole('button', { name: '核对视觉调用状态' })); await screen.findByText('用户取消')
    expect(api.visionLookup).toHaveBeenCalledWith(api.visionCall.mock.calls[0]?.[0].clientRequestId); expect(api.visionCall).toHaveBeenCalledOnce()
  })

  it('超限选择不发送，并清空已授权的材料选择', async () => {
    render(wrap(page()))
    await userEvent.upload(screen.getByLabelText('本次图片'), [new File([new Uint8Array(4 * 1024 * 1024 + 1)], 'too-big.png', { type: 'image/png' })])
    await screen.findByText(/请选择1至4张PNG\/JPEG/)
    expect((screen.getByRole('button', { name: '发送图片并记录' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.visionCall).not.toHaveBeenCalled()
  })

  it('原始图片只为发起账号读取，所有临时图片URL在关闭后释放', async () => {
    const other = render(wrap(<CallImages logId={logId} owner="another-owner" viewer={userId} />))
    expect(screen.getByText('原始图片仅调用发起账号可查看。')).toBeTruthy(); expect(api.images).not.toHaveBeenCalled(); other.unmount()
    api.images.mockResolvedValue([{ id: model.id, index: 0, name: '照片.png', contentType: 'image/png', sizeBytes: bytes.length, sha256: 'synthetic-sha', stored: true }])
    api.image.mockResolvedValue(new Blob([bytes], { type: 'image/png' }))
    const own = render(wrap(<CallImages logId={logId} owner={userId} viewer={userId} />))
    await screen.findByAltText('本次输入图1：照片.png')
    expect(api.image).toHaveBeenCalledOnce(); own.unmount(); expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:synthetic-image')
  })
})
