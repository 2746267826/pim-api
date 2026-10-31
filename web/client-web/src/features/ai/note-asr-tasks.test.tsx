// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteAsrTasks } from './note-asr-tasks'
import type { AsrPreset, Model, ModelSettings, NoteAsrTask } from './api'

const api = vi.hoisted(() => ({ noteAsrSources: vi.fn(), noteAsrTasks: vi.fn(), asrPresets: vi.fn(), noteAsrStart: vi.fn(), noteAsrTask: vi.fn(), noteAsrSegments: vi.fn(), noteAsrRetry: vi.fn(), noteAsrStop: vi.fn() }))
vi.mock('./api', () => ({ aiApi: api }))
const owner = '11111111-1111-4111-8111-111111111111', version = '22222222-2222-4222-8222-222222222222', id = '33333333-3333-4333-8333-333333333333'
let qc: QueryClient, model: Model, settings: ModelSettings, task: NoteAsrTask
const showDetail = vi.fn()
beforeEach(() => {
  vi.resetAllMocks()
  Object.defineProperties(HTMLElement.prototype, { hasPointerCapture: { configurable: true, value: () => false }, setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined }, scrollIntoView: { configurable: true, value: () => undefined } })
  qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  const preset: AsrPreset = { id: 'mimo-asr', version: '1', name: 'MiMo', defaultBaseUrl: 'https://synthetic.invalid/v1/', operationPath: 'chat/completions', defaultModel: 'mimo-v2.5-asr',
    models: ['mimo-v2.5-asr'], formats: ['wav'], languages: ['auto', 'zh'], maxAudioBytes: 4 * 1024 * 1024, streaming: false }
  model = { id, revision: version, name: '语音模型', baseUrl: preset.defaultBaseUrl, model: preset.defaultModel, enabled: true, capabilities: ['Transcription'], hasApiKey: true,
    timeoutSeconds: 30, maxOutputTokens: 100, createdAt: '2026-10-28T00:00:00Z', updatedAt: '2026-10-28T00:00:00Z', asrPresetId: preset.id }
  settings = { revision: version, enabled: true, textModelId: null, visionModelId: null, transcriptionModelId: id }
  task = { taskId: id, revision: version, noteId: id, versionId: version, inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), phase: 'NeedsAttention', language: 'auto',
    destination: { modelId: id, modelRevision: version, endpoint: model.baseUrl + preset.operationPath, presetId: preset.id, presetVersion: '1', confirmed: true },
    createdAt: '2026-10-28T00:00:00Z', updatedAt: '2026-10-28T00:00:00Z', totalSegments: 2, completedSegments: 1, failedSegments: 1,
    totalSamples: 1920000, completedSamples: 960000, sampleRate: 16000, stopRequested: false, backgroundProcessingAvailable: true }
  api.asrPresets.mockResolvedValue([preset]); api.noteAsrTasks.mockResolvedValue([])
  api.noteAsrSources.mockResolvedValue({ total: 1, offset: 0, items: [{ noteId: id, versionId: version, title: '课堂材料', createdAt: task.createdAt, isCurrent: true, hasConflict: false, recordings: 1, totalSamples: 1920000, sampleRate: 16000 }] })
  api.noteAsrTask.mockResolvedValue(task); api.noteAsrStart.mockResolvedValue(task); api.noteAsrRetry.mockResolvedValue(task)
  api.noteAsrSegments.mockResolvedValue({ total: 2, offset: 0, items: [0, 1].map((index) => ({ index, recordingId: id, recordingSegment: 1, chunkIndex: index + 1, sourceFileIndex: index,
    sourcePath: `recordings/source-${index}.wav`, sourceSha256: 'a'.repeat(64), sourceOffsetSamples: 0, recordingOffsetSamples: index * 960000, timelineOffsetSamples: index * 960000,
    samples: 960000, status: index === 0 ? 'Succeeded' : 'Incomplete', transcript: index === 0 ? '保留成功分片原文' : null,
    attempts: [{ requestId: version + index, logId: id, status: index === 0 ? 'Succeeded' : 'Incomplete', errorMessage: index === 0 ? null : '结果未知，请核对' }] })) })
})
afterEach(() => { cleanup(); qc.clear() })
function page() { return render(<QueryClientProvider client={qc}><NoteAsrTasks userId={owner} models={[model]} settings={settings} showDetail={showDetail} /></QueryClientProvider>) }
async function choose() {
  await screen.findByRole('combobox', { name: '固定材料版本' }); await userEvent.click(screen.getByRole('combobox', { name: '固定材料版本' }))
  await userEvent.click(await screen.findByRole('option', { name: /课堂材料/ }))
}

it('binds fixed material, identity and destination consent, with no sending before explicit confirmation', async () => {
  page(); await choose()
  expect((screen.getByRole('button', { name: '开始后台转写' }) as HTMLButtonElement).disabled).toBe(true)
  await userEvent.click(screen.getByRole('checkbox', { name: '确认将该固定版本的全部录音发送至上述模型' }))
  await userEvent.click(screen.getByRole('button', { name: '开始后台转写' }))
  await screen.findByLabelText('第1片转写')
  expect(api.noteAsrStart).toHaveBeenCalledOnce()
  expect(api.noteAsrStart.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ expectedUserId: owner, versionId: version, consent: task.destination }))
  expect(screen.getByText('保留成功分片原文')).toBeTruthy()
})

it('keeps a lost start intent and looks it up without allocating another call', async () => {
  api.noteAsrStart.mockRejectedValue(new Error('连接中断'))
  page(); await choose(); await userEvent.click(screen.getByRole('checkbox', { name: '确认将该固定版本的全部录音发送至上述模型' }))
  await userEvent.click(screen.getByRole('button', { name: '开始后台转写' })); await screen.findByRole('button', { name: '核对已提交任务' })
  const intent = api.noteAsrStart.mock.calls[0]?.[0]
  await userEvent.click(screen.getByRole('button', { name: '核对已提交任务' })); await screen.findByLabelText('第1片转写')
  expect(api.noteAsrTask).toHaveBeenCalledWith(intent.taskId)
  expect(api.noteAsrStart).toHaveBeenCalledOnce()
})

it('offers only failed or queued segments for explicit recovery and preserves successful receipts', async () => {
  api.noteAsrTasks.mockResolvedValue([task]); page()
  await userEvent.click(await screen.findByRole('button', { name: /1\/2 片/ }))
  await screen.findByLabelText('第1片转写')
  expect(screen.queryByRole('checkbox', { name: '恢复第1片' })).toBeNull()
  await userEvent.click(screen.getByRole('checkbox', { name: '恢复第2片' }))
  expect((screen.getByRole('button', { name: '恢复所选分片' }) as HTMLButtonElement).disabled).toBe(true)
  await userEvent.click(screen.getByRole('checkbox', { name: '确认分片恢复和可能重复计费' }))
  await userEvent.click(screen.getByRole('button', { name: '恢复所选分片' }))
  await waitFor(() => expect(api.noteAsrRetry).toHaveBeenCalledOnce())
  expect(api.noteAsrRetry.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ expectedUserId: owner, expectedRevision: task.revision, segmentIndexes: [1], consent: task.destination, acknowledgePossibleDuplicateCharge: true }))
})

it('shows immutable history for the chosen version and opens old results without sending', async () => {
  api.noteAsrTasks.mockResolvedValue([task])
  page(); await choose()
  await screen.findByText(/该版本已有 1 个转写任务/)
  await userEvent.click(screen.getByRole('button', { name: /历史任务/ }))
  await screen.findByLabelText('第1片转写')
  expect(api.noteAsrStart).not.toHaveBeenCalled()
})

it('rerun for a version with history needs explicit consent and creates a new task without touching history', async () => {
  api.noteAsrTasks.mockResolvedValue([task])
  page(); await choose()
  await screen.findByText(/再次开始会新建任务/)
  expect((screen.getByRole('button', { name: '开始后台转写' }) as HTMLButtonElement).disabled).toBe(true)
  await userEvent.click(screen.getByRole('checkbox', { name: '确认将该固定版本的全部录音发送至上述模型' }))
  await userEvent.click(screen.getByRole('button', { name: '开始后台转写' }))
  await waitFor(() => expect(api.noteAsrStart).toHaveBeenCalledOnce())
  expect(api.noteAsrStart.mock.calls[0]?.[0].versionId).toBe(version)
  expect(api.noteAsrStart.mock.calls[0]?.[0].consent).toEqual(task.destination)
  await screen.findByText(/该版本已有 1 个转写任务/)
})
