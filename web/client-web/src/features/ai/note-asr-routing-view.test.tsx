// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrRoutingView } from './note-asr-routing-view'

const api = vi.hoisted(() => ({
  asrRouteConfig: vi.fn(),
  asrRouteConfigSave: vi.fn(),
  asrBatchJobs: vi.fn(),
  asrBatchPoll: vi.fn(),
  asrBatchCancel: vi.fn(),
  asrRoutingNodes: vi.fn(),
  models: vi.fn(),
}))
vi.mock('./api', () => ({ aiApi: api }))

beforeAll(() => {
  if (!HTMLElement.prototype.hasPointerCapture) HTMLElement.prototype.hasPointerCapture = () => false
  if (!HTMLElement.prototype.releasePointerCapture) HTMLElement.prototype.releasePointerCapture = () => {}
  if (!HTMLElement.prototype.scrollIntoView) HTMLElement.prototype.scrollIntoView = () => {}
})
afterEach(cleanup)

const config = {
  revision: '00000000-0000-4000-8000-00000000c001',
  defaultRoute: 'local',
  localModelId: '00000000-0000-4000-8000-00000000a001',
  localModelRevision: 'local-rev-1',
  cloudConfigured: true,
  cloudModelId: '00000000-0000-4000-8000-00000000b001',
  consentVersion: 'consent-v1',
  updatedAt: '2026-10-31T10:00:00Z',
}
const model = {
  id: config.cloudModelId, revision: 'cloud-rev-1', name: 'MiMo Flash', baseUrl: 'https://example.test/v1',
  model: 'mimo-v2.6-flash', enabled: true, capabilities: ['Transcription'], hasApiKey: true,
  timeoutSeconds: 60, maxOutputTokens: 8192, createdAt: config.updatedAt, updatedAt: config.updatedAt,
  asrPresetId: 'mimo-multimodal', asrBatchBaseUrl: null, asrPrompt: null,
}
const local = { ...model, id: config.localModelId, revision: 'local-rev-1', name: '本机 Whisper', baseUrl: 'http://127.0.0.1:9999' }
const node = {
  nodeId: '00000000-0000-4000-8000-000000000f01', nodeIdExternal: 'windows-station-1',
  enabled: true, modelId: config.localModelId, modelRevision: 'local-rev-1', modelVerified: true,
  capabilities: ['Transcription'], lastHeartbeatAt: config.updatedAt, claimable: true, createdAt: config.updatedAt,
  runtimeId: null, runtimeVersion: null, resourceSnapshotJson: null, sampledAt: config.updatedAt,
}
const batch = {
  id: '00000000-0000-4000-8000-00000000d001', taskId: '00000000-0000-4000-8000-00000000e001',
  status: 'IN_PROGRESS', batchId: 'batch_1', inputFileId: 'file_1', outputFileId: null, errorFileId: null,
  inputExpireAt: '2026-11-30T10:00:00Z', batchExpiresAt: '2026-11-01T10:00:00Z', model: 'mimo-v2.6-flash',
  baseUrl: 'https://batch.example.test/v1', consentRef: 'consent-ref', completionWindow: '24h',
  totalItems: 4, completedItems: 1, failedItems: 0, resultJson: null, outputImported: false,
  lastErrorCode: null, lastErrorMessage: null, lastPolledAt: null, createdAt: config.updatedAt,
  updatedAt: config.updatedAt, audioCapability: 'NOT-VERIFIED',
}

beforeEach(() => {
  vi.clearAllMocks()
  api.asrRouteConfig.mockResolvedValue(config)
  api.asrRouteConfigSave.mockResolvedValue(config)
  api.asrBatchJobs.mockResolvedValue([batch])
  api.asrBatchPoll.mockResolvedValue(batch)
  api.asrBatchCancel.mockResolvedValue({ ...batch, status: 'CANCELLING' })
  api.asrRoutingNodes.mockResolvedValue([node])
  api.models.mockResolvedValue([local, model])
})

function page() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={qc}><NoteAsrRoutingView userId="user-1" /></QueryClientProvider>)
}

async function choose(name: string, option: string) {
  const trigger = await screen.findByRole('combobox', { name })
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
  const listbox = await screen.findByRole('listbox')
  fireEvent.click(within(listbox).getByRole('option', { name: option }))
}

describe('NoteAsrRoutingView actual generated consumers', () => {
  it('默认本机保存会完整回填并保留云端配置', async () => {
    page()
    await screen.findByText(/本机与云端配置彼此独立保存/)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(api.asrRouteConfigSave).toHaveBeenCalledOnce())
    expect(api.asrRouteConfigSave.mock.calls[0]?.[0]).toEqual({
      expectedRevision: config.revision,
      defaultRoute: 'local',
      localModelId: config.localModelId,
      localModelRevision: config.localModelRevision,
      cloudModelId: config.cloudModelId,
      consentVersion: config.consentVersion,
    })
  })

  it('切换云端默认并显式清空本机时云端保留为完整payload目标', async () => {
    page()
    await screen.findByText(/本机与云端配置彼此独立保存/)
    await choose('默认转写路由', '默认云端执行')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(api.asrRouteConfigSave).toHaveBeenCalledOnce())
    expect(api.asrRouteConfigSave.mock.calls[0]?.[0]).toMatchObject({
      defaultRoute: 'cloud-multimodal',
      localModelId: config.localModelId,
      localModelRevision: config.localModelRevision,
      cloudModelId: config.cloudModelId,
    })
  })

  it('Batch任务只展示真实验证失败标记，不自动轮询或重建', async () => {
    page()
    expect(await screen.findByText('MiMo Batch 长任务')).toBeTruthy()
    expect(screen.getByText('音频能力 NOT-VERIFIED')).toBeTruthy()
    expect(screen.getByText(/Batch任务截止/)).toBeTruthy()
    expect(api.asrBatchPoll).not.toHaveBeenCalled()
    expect(api.asrBatchCancel).not.toHaveBeenCalled()
  })

  it('Batch异步取消必须显式确认，只调用一次并保留历史', async () => {
    page()
    await screen.findByText('MiMo Batch 长任务')
    await userEvent.click(screen.getByRole('button', { name: '请求取消' }))
    expect(api.asrBatchCancel).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认请求取消 Batch mimo-v2.6-flash' }))
    await userEvent.click(screen.getByRole('button', { name: '确认异步取消' }))
    await waitFor(() => expect(api.asrBatchCancel).toHaveBeenCalledOnce())
    expect(api.asrBatchCancel).toHaveBeenCalledWith(batch.id)
  })

  it('本机模型来自已注册节点而非云端AI profile', async () => {
    page()
    await screen.findByText(/本机与云端配置彼此独立保存/)
    expect(await screen.findByRole('combobox', { name: '本机模型' })).toBeTruthy()
    expect(screen.queryByRole('option', { name: /MiMo Flash/ })).toBeNull()
  })

  it.each(['cloud-asr', 'cloud-multimodal'])('真实API %s 默认云端回显为云端并保存真实枚举', async (route) => {
    api.asrRouteConfig.mockResolvedValue({ ...config, defaultRoute: route })
    page()
    expect(await screen.findByText('默认云端执行')).toBeTruthy()
    expect(screen.queryByText('本机默认路由缺少可用模型')).toBeNull()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(api.asrRouteConfigSave).toHaveBeenCalledOnce())
    expect(api.asrRouteConfigSave.mock.calls[0]?.[0]).toMatchObject({
      defaultRoute: route,
      localModelId: config.localModelId,
      localModelRevision: config.localModelRevision,
      cloudModelId: config.cloudModelId,
    })
    expect(api.asrRouteConfigSave.mock.calls[0]?.[0]).not.toMatchObject({ defaultRoute: 'cloud' })
  })

  it('同modelId多revision时保留已保存revision，不静默采用其他版本', async () => {
    api.asrRoutingNodes.mockResolvedValue([{ ...node, modelRevision: 'local-rev-2' }])
    page()
    expect(await screen.findByText(/已保存配置（当前节点未提供匹配版本）/)).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(api.asrRouteConfigSave).toHaveBeenCalledOnce())
    expect(api.asrRouteConfigSave.mock.calls[0]?.[0]).toMatchObject({
      localModelId: config.localModelId, localModelRevision: config.localModelRevision,
    })
  })

  it('Completed但存在失败项或错误码仍显示部分成功，不冒充完整成功', async () => {
    api.asrBatchJobs.mockResolvedValue([{
      ...batch, status: 'COMPLETED', outputImported: true, failedItems: 1,
      lastErrorCode: 'partial_import', outputFileId: 'file_output',
    }])
    page()
    expect(await screen.findAllByText('部分成功')).toHaveLength(2)
    expect(screen.getAllByText('部分成功').some((element) => element.tagName === 'SPAN')).toBe(true)
    expect(screen.queryByText('已完成')).toBeNull()
  })
})
