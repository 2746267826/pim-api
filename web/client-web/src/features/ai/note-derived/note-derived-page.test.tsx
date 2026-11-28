// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router'
import { NAV_GROUPS } from '@/app/nav'
import { StorageCachePage } from '@/features/settings/pages/storage-cache-page'
import { NoteDerivedPage } from './note-derived-page'
import type { NoteDerivedArtifact, NoteDerivedTask, NoteDerivedVersion } from './api'
import type { NoteAsrTask } from '../api'
import {
  canonicalServerBinding,
  createStoredDraft,
  editStoredDraft,
  persistStoredDraft,
  type NoteDerivedDraftScope,
} from './draft-storage'

const auth = vi.hoisted(() => ({ user: { id: '11111111-1111-4111-8111-111111111111', role: 'user' } }))
const derived = vi.hoisted(() => ({
  sources: vi.fn(), destinations: vi.fn(), limits: vi.fn(), tasks: vi.fn(), task: vi.fn(), steps: vi.fn(),
  start: vi.fn(), stop: vi.fn(), retry: vi.fn(), artifacts: vi.fn(), artifact: vi.fn(), version: vi.fn(), saveManual: vi.fn(), select: vi.fn(),
}))
const assistant = vi.hoisted(() => ({
  noteAsrTasks: vi.fn(), noteAsrDestinations: vi.fn(), asrRouteConfig: vi.fn(), asrRoutingNodes: vi.fn(),
  noteAsrStart: vi.fn(), noteAsrTask: vi.fn(), noteAsrSegments: vi.fn(), asrReprocessHistory: vi.fn(),
  asrRoutingAttempts: vi.fn(), asrRoutingAttemptStop: vi.fn(), models: vi.fn(), asrPresets: vi.fn(),
}))
const taskSuggestions = vi.hoisted(() => ({
  list: vi.fn(), get: vi.fn(), startExtraction: vi.fn(), extraction: vi.fn(),
  update: vi.fn(), confirm: vi.fn(), dismiss: vi.fn(),
}))
const calendarApi = vi.hoisted(() => ({ list: vi.fn() }))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => auth }))
vi.mock('./api', () => ({ noteDerivedApi: derived }))
vi.mock('../api', () => ({ aiApi: assistant }))
vi.mock('./note-task-suggestions/api', () => ({ noteTaskSuggestionsApi: taskSuggestions }))
vi.mock('@/features/calendar/api', () => ({ calendarsApi: calendarApi }))

const owner = auth.user.id
const noteId = '22222222-2222-4222-8222-222222222222'
const sourceVersionId = '33333333-3333-4333-8333-333333333333'
const taskId = '44444444-4444-4444-8444-444444444444'
const artifactId = '55555555-5555-4555-8555-555555555555'
const versionId = '66666666-6666-4666-8666-666666666666'
const destinationId = '77777777-7777-4777-8777-777777777777'
const destinationRevision = '88888888-8888-4888-8888-888888888888'

const destination = {
  modelId: destinationId, modelRevision: destinationRevision, displayName: '合成共享目标', model: 'synthetic-vision-text',
  endpoint: 'https://synthetic.invalid/v1/', capabilities: ['Text', 'Vision'], backgroundProcessingAvailable: true,
}
const asrTask: NoteAsrTask = {
  taskId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', revision: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  noteId, versionId: sourceVersionId, inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64),
  phase: 'Failed', language: 'zh', destination: {
    modelId: destinationId, modelRevision: destinationRevision, endpoint: destination.endpoint,
    presetId: 'synthetic-asr', presetVersion: 'v1', confirmed: true,
  },
  createdAt: '2026-10-29T00:00:00Z', updatedAt: '2026-10-29T00:00:00Z', totalSegments: 1,
  completedSegments: 0, failedSegments: 1, totalSamples: 16000, completedSamples: 0, sampleRate: 16000,
  stopRequested: false, backgroundProcessingAvailable: true, route: 'cloud-asr',
}
const localAsrTask: NoteAsrTask = {
  ...asrTask,
  taskId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  phase: 'Queued',
  route: 'local',
  destination: null,
}
const succeededAsrTask: NoteAsrTask = {
  ...asrTask,
  taskId: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  phase: 'Succeeded',
  completedSegments: 1,
  failedSegments: 0,
}
const runningAsrTask: NoteAsrTask = {
  ...asrTask,
  phase: 'Running',
  completedSegments: 0,
  failedSegments: 0,
}
const inconsistentSucceededAsrTask: NoteAsrTask = {
  ...succeededAsrTask,
  failedSegments: 1,
}
const source = {
  noteId, versionId: sourceVersionId, title: '零录音材料', createdAt: '2026-10-29T00:00:00Z', isCurrent: true,
  hasConflict: false, recordings: 0, totalSamples: 0, sampleRate: 16000,
}
const coverage = {
  complete: true, textBlocks: 1, transcriptSegments: 0, completedTranscriptSegments: 0, photos: 0, completedPhotos: 0,
  inkPreviews: 0, completedInkPreviews: 0, visionBatches: 0, completedVisionBatches: 0, issues: [],
}
const task: NoteDerivedTask = {
  taskId, revision: '99999999-9999-4999-8999-999999999999', noteId, sourceVersionId, asrTaskId: null, asrRevision: null,
  inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), phase: 'Queued',
  destination: { modelId: destinationId, modelRevision: destinationRevision, endpoint: destination.endpoint, confirmed: true },
  coverage, artifacts: [{ artifactId, kind: 'CompleteRecord', headVersionId: null }], issues: [], steps: [], planVersion: 'v1',
  createdAt: '2026-10-29T00:00:00Z', updatedAt: '2026-10-29T00:00:00Z', stopRequested: false, backgroundProcessingAvailable: true,
}
const artifact: NoteDerivedArtifact = {
  artifactId, noteId, kind: 'CompleteRecord', revision: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', headVersionId: null,
  versions: [{
    versionId, author: 'Model', sourceVersionId, parentVersionId: null, sourceCompleteRecordVersionId: null,
    generationTaskId: taskId, contentSha256: 'c'.repeat(64), hasConflict: false, createdAt: '2026-10-29T00:00:00Z', isHead: false,
  }],
}
const version: NoteDerivedVersion = {
  versionId, artifactId, kind: 'CompleteRecord', author: 'Model', parentVersionId: null, observedHeadVersionId: null,
  sourceCompleteRecordVersionId: null, sourceVersionId, generationTaskId: taskId, contentMarkdown: '服务端生成正文',
  contentSha256: 'c'.repeat(64), inputSha256: 'a'.repeat(64), manifestSha256: 'b'.repeat(64), planVersion: 'v1', promptVersion: 'p1',
  aiCallLogId: null, modelProfileId: destinationId, modelRevision: destinationRevision, modelEndpoint: destination.endpoint,
  coverage, hasConflict: false, createdAt: '2026-10-29T00:00:00Z', isHead: false,
}

let client: QueryClient
function page() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } })
  const entry = `${window.location.pathname}${window.location.search}`
  return render(<MemoryRouter initialEntries={[entry]}><QueryClientProvider client={client}><NoteDerivedPage /></QueryClientProvider></MemoryRouter>)
}
async function chooseSource() {
  const label = auth.user.role === 'admin' ? '材料版本' : '选择笔记内容'
  await userEvent.click(await screen.findByRole('combobox', { name: label }))
  await userEvent.click(await screen.findByRole('option', { name: /零录音材料/ }))
}
async function chooseDestination() {
  await userEvent.click(await screen.findByRole('combobox', { name: '本次生成目标' }))
  await userEvent.click(await screen.findByRole('option', { name: '合成共享目标' }))
}

beforeEach(() => {
  localStorage.clear(); vi.resetAllMocks(); auth.user.id = owner; auth.user.role = 'admin'
  window.history.replaceState({}, '', '/')
  Object.defineProperties(HTMLElement.prototype, {
    hasPointerCapture: { configurable: true, value: () => false }, setPointerCapture: { configurable: true, value: () => undefined },
    releasePointerCapture: { configurable: true, value: () => undefined }, scrollIntoView: { configurable: true, value: () => undefined },
  })
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [source] })
  derived.destinations.mockResolvedValue({ total: 1, items: [destination] })
  derived.limits.mockResolvedValue({ maxImages: 20, maxImagesPerVisionCall: 4, maxImageBytes: 5242880, maxVisionCallBytes: 8388608, maxVisionCalls: 40, maxEvidenceCharsPerStep: 50000, maxEvidenceSteps: 4096, maxSynthesisInputChars: 500000, maxDerivedContentCharacters: 1000000 })
  derived.tasks.mockResolvedValue([]); derived.task.mockResolvedValue(task); derived.steps.mockResolvedValue({ total: 0, offset: 0, items: [] })
  derived.start.mockResolvedValue(task); derived.stop.mockResolvedValue(task); derived.retry.mockResolvedValue(task)
  derived.artifacts.mockResolvedValue([]); derived.version.mockResolvedValue(version)
  derived.saveManual.mockResolvedValue({ artifact, versionId, hasConflict: true })
  derived.select.mockResolvedValue({ ...artifact, headVersionId: versionId })
  assistant.noteAsrTasks.mockResolvedValue([])
  assistant.noteAsrDestinations.mockResolvedValue({ total: 0, items: [] })
  assistant.asrRouteConfig.mockResolvedValue({
    cloudConfigured: false, cloudModelId: null, consentVersion: 'consent-1', defaultRoute: 'local',
    localModelId: null, localModelRevision: null, revision: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', updatedAt: '2026-10-29T00:00:00Z',
  })
  assistant.asrRoutingNodes.mockResolvedValue([])
  assistant.noteAsrTask.mockResolvedValue(asrTask)
  assistant.noteAsrSegments.mockResolvedValue({ total: 0, offset: 0, items: [] })
  assistant.asrReprocessHistory.mockResolvedValue({ parentTaskId: asrTask.taskId, total: 0, offset: 0, limit: 10, items: [] })
  assistant.asrRoutingAttempts.mockResolvedValue([])
  taskSuggestions.list.mockResolvedValue({ total: 0, items: [] })
  calendarApi.list.mockResolvedValue([])
})
afterEach(() => { cleanup(); client?.clear() })

it('loads ordinary sources without version controls and never calls admin model settings', async () => {
  auth.user.role = 'user'
  const collection = NAV_GROUPS.find((group) => group.label === '收集')
  expect(collection?.items).toContainEqual(expect.objectContaining({ to: '/native-notes', label: '笔记' }))
  page()
  expect(await screen.findByRole('heading', { name: '继续处理这篇笔记' })).toBeTruthy()
  expect(screen.getByText('选择笔记内容后，按需转写录音或直接整理。')).toBeTruthy()
  expect(screen.getByText('隐私与处理说明').closest('details')?.open).toBe(false)
  await chooseSource()
  expect(screen.getByText('这份内容没有录音，可直接整理成完整记录和分析。')).toBeTruthy()
  expect(screen.queryByText('先完成录音转写，再整理成完整记录和分析。')).toBeNull()
  expect(screen.getByRole('heading', { name: '选择笔记内容' }).previousElementSibling).toBeNull()
  expect(screen.queryByRole('combobox', { name: '材料版本' })).toBeNull()
  expect(screen.queryByText('零录音版本')).toBeNull()
  expect(screen.queryByText(/固定版本/)).toBeNull()
  expect((await screen.findAllByText(/没有录音/)).length).toBeGreaterThan(0)
  expect(derived.sources).toHaveBeenCalled()
  expect(derived.destinations).toHaveBeenCalled()
  expect(assistant.noteAsrTasks).not.toHaveBeenCalled()
  expect(screen.queryByText('模型设置')).toBeNull()
  expect(screen.getByText('隐私与处理说明').closest('details')?.textContent).toContain('普通账号只能使用管理员启用的共享目标')
})

it('preselects the NoteOrganization purpose default returned by the server', async () => {
  derived.destinations.mockResolvedValue({
    total: 1,
    items: [destination],
    defaultModelId: destinationId,
    purpose: 'NoteOrganization',
  })
  page()
  await chooseSource()

  await waitFor(() => expect(
    screen.getByRole('combobox', { name: '本次生成目标' }).textContent,
  ).toContain('合成共享目标'))
  expect(derived.destinations).toHaveBeenCalledWith('NoteOrganization', expect.any(AbortSignal))
})

it('renders an ordinary processing page from note and action context without version controls', async () => {
  auth.user.role = 'user'
  window.history.replaceState({}, '', `/native-notes/processing?noteId=${noteId}&version=${sourceVersionId}&action=organize`)

  page()

  expect(await screen.findByRole('heading', { name: '继续处理这篇笔记' })).toBeTruthy()
  await waitFor(() => expect(screen.getByRole('link', { name: '返回所选笔记' }).getAttribute('href')).toBe(`/native-notes?note=${noteId}`))
  expect(screen.queryByRole('combobox', { name: '材料版本' })).toBeNull()
})

it('explains an empty shared destination catalogue without exposing admin configuration to ordinary users', async () => {
  auth.user.role = 'user'
  derived.destinations.mockResolvedValue({ total: 0, items: [] })
  page(); await chooseSource()

  expect(await screen.findByText(/暂无可用的共享生成目标/)).toBeTruthy()
  expect(screen.getByText(/普通账号不能查看或配置模型凭据/)).toBeTruthy()
  expect(screen.queryByRole('link', { name: /ASR 配置/ })).toBeNull()
})

it('shows only the shared destination name to ordinary users in the generation summary', async () => {
  auth.user.role = 'user'
  page(); await chooseSource(); await chooseDestination()

  const disclosure = screen.getByRole('group', { name: '本次生成目标、材料范围与费用' })
  expect(disclosure.textContent).toContain('本次目标：合成共享目标')
  expect(disclosure.textContent).not.toContain(destination.model)
  expect(screen.queryByText(new RegExp(destination.model))).toBeNull()
})

it('shows the ordinary-user ASR start entry on native notes without admin model queries', async () => {
  auth.user.role = 'user'
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  page()
  await chooseSource()
  expect(await screen.findByText('启动录音转写')).toBeTruthy()
  expect(screen.getByText('先完成录音转写，再整理成完整记录和分析。')).toBeTruthy()
  expect(screen.getByRole('heading', { name: '选择笔记内容' }).previousElementSibling).toBeNull()
  expect(screen.queryByText(/固定材料版本/)).toBeNull()
  expect(screen.queryByText(/当前固定版本/)).toBeNull()
  expect(screen.queryByText(/本版本录音转写任务/)).toBeNull()
  expect(await screen.findByText('没有本机模型')).toBeTruthy()
  await waitFor(() => expect(assistant.noteAsrDestinations).toHaveBeenCalledOnce())
  expect(assistant.asrRouteConfig).toHaveBeenCalled()
  expect(assistant.models).not.toHaveBeenCalled()
  expect(assistant.asrPresets).not.toHaveBeenCalled()
})

it('hides raw ASR phase values from the ordinary task summary', async () => {
  auth.user.role = 'user'
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([{ ...asrTask, phase: 'VendorPhase' }])

  page(); await chooseSource()

  const summary = await screen.findByRole('status', { name: '当前转写任务摘要' })
  expect(summary.textContent).toContain('处理状态待核对')
  expect(summary.textContent).not.toContain('VendorPhase')
})

it('uses the task-list selection as the sole completed ASR choice for generation', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([succeededAsrTask])
  page()
  await chooseSource()

  expect(await screen.findByText('已选用完整成功结果，可直接继续整理；如需其他结果可在任务列表中切换。')).toBeTruthy()
  expect(screen.queryByText('先完成录音转写，再整理成完整记录和分析。')).toBeNull()
  expect(screen.queryByRole('combobox', { name: '同版本已完成 ASR' })).toBeNull()
  expect(screen.getByText('重新转写（可选）').closest('details')?.open).toBe(false)
  const consent = screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ }) as HTMLInputElement
  expect(consent.disabled).toBe(true)

  await chooseDestination()
  expect(consent.disabled).toBe(false)
  await userEvent.click(consent)
  await userEvent.click(screen.getByRole('button', { name: /生成完整记录与分析/ }))
  await waitFor(() => expect(derived.start).toHaveBeenCalledOnce())
  expect(derived.start.mock.calls[0]?.[0]).toEqual(expect.objectContaining({ asrTaskId: succeededAsrTask.taskId }))
})

it('requires fresh consent after switching ASR tasks away and back', async () => {
  const secondSucceededAsrTask: NoteAsrTask = {
    ...succeededAsrTask,
    taskId: 'abababab-abab-4bab-8bab-abababababab',
    totalSegments: 2,
    completedSegments: 2,
  }
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([succeededAsrTask, secondSucceededAsrTask])
  page()
  await chooseSource()
  await chooseDestination()

  const consent = screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ }) as HTMLInputElement
  const generate = screen.getByRole('button', { name: /生成完整记录与分析/ }) as HTMLButtonElement
  expect(screen.getByRole('combobox', { name: '本次生成目标' }).textContent).toContain('合成共享目标')
  expect(consent.disabled).toBe(false)
  await userEvent.click(consent)
  expect(consent.checked).toBe(true)
  expect(generate.disabled).toBe(false)

  await userEvent.click(screen.getByText(/^更多：转写任务与恢复/))
  await userEvent.click(await screen.findByRole('button', { name: /2\/2 片/ }))
  await userEvent.click(await screen.findByRole('button', { name: /1\/1 片/ }))

  expect(consent.checked).toBe(false)
  expect(generate.disabled).toBe(true)
  expect(screen.getByRole('combobox', { name: '本次生成目标' }).textContent).toContain('合成共享目标')
  expect(derived.start).not.toHaveBeenCalled()
})

it('updates the next step when an active ASR task becomes reusable', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  let currentAsrTasks = [runningAsrTask]
  assistant.noteAsrTasks.mockImplementation(() => Promise.resolve(currentAsrTasks))
  page()
  await chooseSource()

  expect(await screen.findByText('录音正在转写中；完成后可复用结果，也可以先查看任务进度。')).toBeTruthy()
  expect(screen.queryByText('重新转写（可选）')).toBeNull()

  currentAsrTasks = [succeededAsrTask]
  await waitFor(() => expect(screen.getByText('已选用完整成功结果，可直接继续整理；如需其他结果可在任务列表中切换。')).toBeTruthy(), { timeout: 7000, interval: 50 })
  expect(screen.getByText('重新转写（可选）').closest('details')?.open).toBe(false)
  expect(screen.getByRole('status', { name: '当前转写任务摘要' }).textContent).toContain('转写完成')
}, 10000)

it('does not enable a new ASR start while the task list is pending or failed', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockReturnValue(new Promise(() => undefined))
  const pending = page()
  await chooseSource()
  expect(screen.queryByText('启动录音转写')).toBeNull()
  pending.unmount()

  assistant.noteAsrTasks.mockRejectedValue(new Error('任务读取失败'))
  page()
  await chooseSource()
  expect(await screen.findByText('任务读取失败，请刷新核对；页面不会自动重发。')).toBeTruthy()
  expect(screen.queryByText('启动录音转写')).toBeNull()
})

it('does not offer a duplicate transcription start while this version is queued or running', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([runningAsrTask])
  page()
  await chooseSource()

  expect(await screen.findByText('录音正在转写中；完成后可复用结果，也可以先查看任务进度。')).toBeTruthy()
  expect(await screen.findByRole('status', { name: '当前转写任务摘要' })).toBeTruthy()
  expect(screen.queryByText('启动录音转写')).toBeNull()
  expect(screen.queryByRole('heading', { name: /ASR 路由/ })).toBeNull()

  await userEvent.click(screen.getByText(/^更多：转写任务与恢复/))
  expect(await screen.findByRole('button', { name: /0\/1 片/ })).toBeTruthy()
  expect(screen.queryByText('启动录音转写')).toBeNull()
  expect(assistant.noteAsrStart).not.toHaveBeenCalled()
})

it('does not authorize generation from a succeeded ASR task with failed segments', async () => {
  auth.user.role = 'user'
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([inconsistentSucceededAsrTask])
  page()
  await chooseSource()
  await chooseDestination()

  expect(await screen.findByText(/请在转写任务列表中选择当前内容完整成功的结果/)).toBeTruthy()
  expect((screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ }) as HTMLInputElement).disabled).toBe(true)
  expect((screen.getByRole('button', { name: /生成完整记录与分析/ }) as HTMLButtonElement).disabled).toBe(true)
  expect(derived.start).not.toHaveBeenCalled()
})

it('clears an ASR selection when switching to a different material version', async () => {
  const otherVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  derived.sources.mockResolvedValue({ total: 2, offset: 0, items: [
    { ...source, recordings: 1, totalSamples: 16000 },
    { ...source, versionId: otherVersionId, title: '另一版录音', recordings: 1, totalSamples: 16000, isCurrent: false },
  ] })
  assistant.noteAsrTasks.mockResolvedValue([succeededAsrTask])
  page()
  await chooseSource()
  await userEvent.click(screen.getByText(/^更多：转写任务与恢复/))
  await userEvent.click(await screen.findByRole('button', { name: /1\/1 片/ }))
  await chooseDestination()
  expect((screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ }) as HTMLInputElement).disabled).toBe(false)

  await userEvent.click(screen.getByRole('combobox', { name: '材料版本' }))
  await userEvent.click(await screen.findByRole('option', { name: /另一版录音/ }))
  expect(await screen.findByText(/请在转写任务列表中选择本版本完整成功的结果/)).toBeTruthy()
  expect((screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ }) as HTMLInputElement).disabled).toBe(true)
})

it('keeps the current ASR summary visible and collapses task recovery by default', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([asrTask])
  page()
  await chooseSource()
  expect(await screen.findByText(/更多：转写任务与恢复 · 1 项/)).toBeTruthy()
  const taskSummary = await screen.findByRole('status', { name: '当前转写任务摘要' })
  const startPanel = screen.getByText('启动录音转写')
  expect(taskSummary.compareDocumentPosition(startPanel) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  const disclosure = screen.getByText(/^更多：转写任务与恢复/).closest('details')
  expect(disclosure?.open).toBe(false)
  expect(screen.getByText('逐片进度与恢复').closest('details')).toBe(disclosure)
  await userEvent.click(screen.getByText(/^更多：转写任务与恢复/))
  expect(disclosure?.open).toBe(true)
  expect(await screen.findByRole('status', { name: '当前转写任务摘要' })).toBeTruthy()
  await userEvent.click(await screen.findByRole('button', { name: /0\/1 片/ }))
  expect(await screen.findByText('逐片进度与恢复')).toBeTruthy()
  expect(await screen.findByText('重处理（显式确认）')).toBeTruthy()
  expect(await screen.findByText('重处理历史（不可变）')).toBeTruthy()
  expect(assistant.models).not.toHaveBeenCalled()
  expect(assistant.asrPresets).not.toHaveBeenCalled()
})

it('presents transcription before generation and keeps nonessential controls collapsed', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.asrRouteConfig.mockResolvedValue({
    cloudConfigured: true, cloudModelId: destinationId, consentVersion: 'consent-1', defaultRoute: 'cloud-asr',
    localModelId: null, localModelRevision: null, revision: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', updatedAt: '2026-10-29T00:00:00Z',
  })
  assistant.noteAsrDestinations.mockResolvedValue({ total: 1, items: [{ ...destination, presetId: 'standard-asr', presetVersion: 'v1' }] })
  page()
  await chooseSource()
  const ordered = await screen.findAllByRole('heading', { name: /先完成录音转写|整理并生成/ })
  expect(ordered.map((heading) => heading.textContent)).toEqual(['1 · 先完成录音转写', '2 · 整理并生成'])
  expect(screen.getByRole('button', { name: '确认创建云端任务' })).toBeTruthy()
  expect(screen.getByRole('combobox', { name: '执行位置' }).closest('details')).toBeNull()
  expect(screen.getByRole('checkbox', { name: '确认创建录音转写任务' })).toBeTruthy()
  expect(screen.queryByRole('combobox', { name: '执行方式' })).toBeNull()
  const options = screen.getByText('高级选项：识别语言').closest('details')
  expect(options?.open).toBe(false)
  expect(screen.getByRole('combobox', { name: '执行位置' }).closest('details')).toBeNull()
  const consentSummary = screen.getByRole('group', { name: '本次转写执行位置、目标与费用' })
  expect(consentSummary.textContent).toContain('云端 · 实时')
  expect(consentSummary.textContent).toContain('合成共享目标 · synthetic-vision-text')
  expect(consentSummary.textContent).toContain('当前固定版本的全部录音')
  expect(consentSummary.textContent).toContain('音频将离开本机')
  expect(consentSummary.textContent).toContain('现金费用')
  expect(screen.getByRole('checkbox', { name: '确认创建录音转写任务' }).getAttribute('aria-checked')).toBe('false')
  expect(assistant.noteAsrStart).not.toHaveBeenCalled()
  expect(options?.contains(screen.getByRole('combobox', { name: '识别语言' }))).toBe(true)
  await userEvent.click(screen.getByText('高级选项：识别语言'))
  expect(options?.open).toBe(true)
})

  it('keeps the selected task summary visible while history and controls stay collapsed', async () => {
    derived.tasks.mockResolvedValue([task])
    derived.task.mockResolvedValue(task)
    page()
    await chooseSource()
    await userEvent.click(await screen.findByRole('button', { name: /材料覆盖完整/ }))
    expect(await screen.findByRole('status', { name: '当前整理任务摘要' })).toBeTruthy()
    const history = screen.getByText(/更多：历史任务与处理进度/).closest('details')
    const controls = screen.getByText(/更多：任务详情、停止与恢复/).closest('details')
    expect(history?.open).toBe(false)
    expect(controls?.open).toBe(false)
    expect(screen.getByRole('button', { name: /停止后续步骤/ }).closest('details')).toBe(controls)
    await userEvent.click(screen.getByText(/更多：任务详情、停止与恢复/))
    expect(controls?.open).toBe(true)
    expect(await screen.findByRole('button', { name: /停止后续步骤/ })).toBeTruthy()
  })

it('preselects the linked material version and preserves it in the return-to-note link', async () => {
  window.history.replaceState({}, '', `/native-notes/processing?version=${sourceVersionId}&from=processing`)
  page()
  const sourcePicker = await screen.findByRole('combobox', { name: '材料版本' })
  expect(sourcePicker.textContent).toContain('零录音材料')
  expect(await screen.findByRole('heading', { name: '1 · 整理并生成' })).toBeTruthy()
  expect(screen.getByRole('link', { name: '返回所选笔记' }).getAttribute('href')).toBe(`/native-notes?version=${sourceVersionId}&from=processing`)
})

it('opens the linked ordinary AI draft from settings and selects its note and result version', async () => {
  auth.user.role = 'user'
  const candidateVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const candidateArtifact: NoteDerivedArtifact = {
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...artifact.versions[0]!, isHead: true },
      {
        ...artifact.versions[0]!,
        versionId: candidateVersionId,
        createdAt: '2026-10-29T00:01:00Z',
        isHead: false,
      },
    ],
  }
  derived.artifacts.mockResolvedValue([candidateArtifact])
  derived.version.mockImplementation((_artifactId: string, id: string) => Promise.resolve(id === candidateVersionId
    ? {
        ...version,
        versionId: candidateVersionId,
        contentMarkdown: '候选整理正文',
        createdAt: '2026-10-29T00:01:00Z',
        isHead: false,
      }
    : version))
  const draftScope: NoteDerivedDraftScope = {
    ownerId: owner,
    serverBinding: canonicalServerBinding(),
    artifactId,
    sourceVersionId,
    kind: 'CompleteRecord',
    artifactVersionId: candidateVersionId,
    noteId,
  }
  const clean = persistStoredDraft(createStoredDraft(draftScope, '候选整理正文', candidateVersionId))
  expect(clean.ok).toBe(true)
  if (!clean.ok) return
  const edited = editStoredDraft(clean.value, '普通账号尚未保存的候选草稿')
  expect(edited.ok).toBe(true)
  if (!edited.ok) return
  expect(persistStoredDraft(edited.value).ok).toBe(true)
  window.history.replaceState({}, '', '/settings/storage')
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } })

  render(
    <MemoryRouter initialEntries={['/settings/storage']}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/settings/storage" element={<StorageCachePage />} />
          <Route path="/native-notes/processing" element={<NoteDerivedPage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )

  await userEvent.click(await screen.findByRole('link', { name: '去处理页核对' }))

  expect(await screen.findByRole('heading', { name: '继续处理这篇笔记' })).toBeTruthy()
  await waitFor(() => expect(screen.getByRole('combobox', { name: '选择笔记内容' }).textContent).toContain('零录音材料'))
  expect(await screen.findByDisplayValue('普通账号尚未保存的候选草稿')).toBeTruthy()
  expect(derived.version).toHaveBeenCalledWith(artifactId, candidateVersionId, expect.any(AbortSignal))
})

it('reports an ordinary AI draft without a note binding as manual verification instead of guessing', () => {
  auth.user.role = 'user'
  localStorage.setItem('pim.apiBase', 'https://pim-a.example')
  const unboundScope: NoteDerivedDraftScope = {
    ownerId: owner,
    serverBinding: canonicalServerBinding(),
    artifactId,
    sourceVersionId,
    kind: 'CompleteRecord',
    artifactVersionId: versionId,
  }
  const dirty = editStoredDraft(createStoredDraft(unboundScope, '已保存正文', versionId), '未保存正文')
  expect(dirty.ok).toBe(true)
  if (!dirty.ok) return
  expect(persistStoredDraft(dirty.value).ok).toBe(true)
  client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })

  render(
    <MemoryRouter initialEntries={['/settings/storage']}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route path="/settings/storage" element={<StorageCachePage />} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )

  const link = screen.getByRole('link', { name: '去处理页手动核对' })
  expect(link.getAttribute('href')).toBe('/native-notes/processing')
})

it('falls back to the current result version when an ordinary draft result version is no longer listed', async () => {
  auth.user.role = 'user'
  const missingVersionId = 'abababab-abab-4bab-8bab-abababababab'
  derived.artifacts.mockResolvedValue([artifact])
  window.history.replaceState({}, '', `/native-notes/processing?noteId=${noteId}&artifactVersionId=${missingVersionId}`)

  page()

  expect(await screen.findByText(/关联的结果版本已不在服务器返回的列表中/)).toBeTruthy()
  await waitFor(() => expect(screen.getByRole('combobox', { name: '选择笔记内容' }).textContent).toContain('零录音材料'))
  expect(await screen.findByDisplayValue('服务端生成正文')).toBeTruthy()
  expect(derived.version).toHaveBeenCalledWith(artifactId, versionId, expect.any(AbortSignal))
  expect(derived.version).not.toHaveBeenCalledWith(artifactId, missingVersionId, expect.any(AbortSignal))
})

it('does not silently open a historical source version from an ordinary draft link', async () => {
  auth.user.role = 'user'
  const historyVersionId = 'abababab-abab-4bab-8bab-abababababab'
  derived.sources.mockResolvedValue({
    total: 2,
    offset: 0,
    items: [{ ...source, versionId: historyVersionId, title: '历史材料不应出现', isCurrent: false }, source],
  })
  window.history.replaceState({}, '', `/native-notes/processing?noteId=${noteId}&version=${historyVersionId}&artifactVersionId=${versionId}`)

  page()

  expect(await screen.findByText(/关联的材料版本已不是当前内容/)).toBeTruthy()
  await waitFor(() => expect(screen.getByRole('combobox', { name: '选择笔记内容' }).textContent).toContain('零录音材料'))
  expect(screen.queryByText('历史材料不应出现')).toBeNull()
  expect(screen.queryByText(new RegExp(historyVersionId))).toBeNull()
})

it('consumes note and action context, selects its current version and focuses the requested next step', async () => {
  const scrollIntoView = vi.spyOn(HTMLElement.prototype, 'scrollIntoView')
  window.history.replaceState({}, '', `/native-notes/processing?noteId=${noteId}&action=organize`)
  page()
  const sourcePicker = await screen.findByRole('combobox', { name: '材料版本' })
  expect(sourcePicker.textContent).toContain('零录音材料')
  expect(await screen.findByRole('heading', { name: '1 · 整理并生成' })).toBeTruthy()
  await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({ block: 'start' }))
  await waitFor(() => expect(screen.getByRole('link', { name: '返回所选笔记' }).getAttribute('href')).toBe(`/native-notes?noteId=${noteId}&version=${sourceVersionId}`))
})

it('finds a linked material version beyond the first page', async () => {
  const olderVersions = Array.from({ length: 20 }, (_, index) => ({
    ...source, versionId: `00000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`, title: `较早笔记 ${index + 1}`,
  }))
  derived.sources.mockImplementation((offset: number) => Promise.resolve(offset === 0
    ? { total: 21, offset, items: olderVersions }
    : { total: 21, offset, items: [source] }))
  window.history.replaceState({}, '', `/native-notes/processing?version=${sourceVersionId}`)
  page()
  expect(await screen.findByRole('heading', { name: '1 · 整理并生成' })).toBeTruthy()
  expect(derived.sources).toHaveBeenCalledWith(20, expect.any(AbortSignal))
})

it('renders a nullable local task without touching destination fields or admin settings', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([localAsrTask])
  assistant.noteAsrTask.mockResolvedValue(localAsrTask)
  page()
  await chooseSource()
  expect(await screen.findByText(/更多：转写任务与恢复/)).toBeTruthy()
  await userEvent.click(screen.getByText(/^更多：转写任务与恢复/))
  await userEvent.click(await screen.findByRole('button', { name: /0\/1 片/ }))
  expect(await screen.findByText('本机排队转写 · 未发送云端')).toBeTruthy()
  expect(await screen.findByText('逐片进度与恢复')).toBeTruthy()
  expect(screen.queryByText(/本任务发送目标/)).toBeNull()
  expect(assistant.models).not.toHaveBeenCalled()
})

it('requires exact destination consent and creates with a persisted UUID intent', async () => {
  derived.tasks.mockImplementation(() => Promise.resolve(derived.start.mock.calls.length ? [task] : []))
  page(); await chooseSource(); await chooseDestination()
  const disclosure = screen.getByRole('group', { name: '本次生成目标、材料范围与费用' })
  expect(disclosure.textContent).toContain('本次目标：合成共享目标 · synthetic-vision-text')
  expect(disclosure.textContent).toContain('固定版本的完整正文、照片和手写渲染图')
  expect(disclosure.textContent).toContain('可能产生服务商费用')
  expect(disclosure.textContent).toContain('不会自动创建或自动付费')
  expect(disclosure.closest('details')).toBeNull()
  expect(screen.getByRole('combobox', { name: '本次生成目标' }).closest('details')).toBeNull()
  expect(screen.queryByRole('combobox', { name: '同版本已完成 ASR' })).toBeNull()
  expect(screen.getByText('诊断与高级限制').closest('details')?.open).toBe(false)
  expect(screen.getByText(`服务地址：${destination.endpoint}`).closest('details')?.open).toBe(false)
  const checkbox = screen.getByRole('checkbox', { name: /确认将上述材料发送至上方所选生成目标/ })
  expect(checkbox.closest('details')).toBeNull()
  expect((checkbox as HTMLInputElement).disabled).toBe(false)
  const taskListReadsBeforeStart = derived.tasks.mock.calls.length
  await userEvent.click(checkbox)
  await userEvent.click(screen.getByRole('button', { name: /生成完整记录与分析/ }))
  await waitFor(() => expect(derived.start).toHaveBeenCalledOnce())
  const body = derived.start.mock.calls[0]?.[0]
  expect(body).toEqual(expect.objectContaining({
    expectedUserId: owner, versionId: sourceVersionId, asrTaskId: null,
    consent: { modelId: destinationId, modelRevision: destinationRevision, endpoint: destination.endpoint, confirmed: true },
  }))
  expect(body.taskId).toMatch(/^[0-9a-f-]{36}$/)
  expect(await screen.findByRole('status', { name: '当前整理任务摘要' })).toBeTruthy()
  expect(derived.tasks.mock.calls.length).toBeGreaterThan(taskListReadsBeforeStart)
  expect(screen.getByRole('status', { name: '当前整理任务摘要' }).textContent).toContain('整理进度：等待处理')
  const detailDisclosure = screen.getByText(/更多：任务详情、停止与恢复/).closest('details')
  expect(detailDisclosure?.open).toBe(false)
  await userEvent.click(screen.getByText(/更多：任务详情、停止与恢复/))
  expect(await screen.findByRole('heading', { name: '当前任务进度' })).toBeTruthy()
})

it('shows generated candidates and requires an explicit head selection before manual editing', async () => {
  derived.artifacts.mockResolvedValue([artifact])
  page(); await chooseSource()
  expect(await screen.findByText('完整记录')).toBeTruthy()
  expect(await screen.findByText('等待明确选择')).toBeTruthy()
  await waitFor(() => expect(derived.version).toHaveBeenCalled())
  expect(screen.queryByRole('button', { name: '采用这份整理结果' })).toBeNull()
  const history = screen.getByText('高级选项：版本历史与处理详情')
  expect(history.closest('details')?.open).toBe(false)
  await userEvent.click(history)
  await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))
  await waitFor(() => expect(derived.select).toHaveBeenCalledWith(artifactId, expect.objectContaining({ expectedHeadRevision: artifact.revision, versionId })))
})

it('keeps version history and internal processing identifiers out of the ordinary-user derived page', async () => {
  auth.user.role = 'user'
  const historySource = {
    ...source,
    versionId: 'abababab-abab-4bab-8bab-abababababab',
    title: '历史材料不应出现',
    isCurrent: false,
  }
  derived.sources.mockResolvedValue({ total: 2, offset: 0, items: [source, historySource] })
  derived.artifacts.mockResolvedValue([artifact])
  derived.tasks.mockResolvedValue([task])
  derived.task.mockResolvedValue(task)
  taskSuggestions.list.mockResolvedValue({
    total: 1,
    items: [{
      id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      revision: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      versionId,
      ordinal: 1,
      status: 'Draft',
      title: '跟进客户报价',
      description: '确认报价单。',
      priority: 5,
      due: null,
      taskId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
      sourceContentSha256: 'd'.repeat(64),
    }],
  })

  page(); await chooseSource()
  expect(await screen.findByRole('combobox', { name: '选择笔记内容' })).toBeTruthy()
  expect(await screen.findByText('从笔记提取任务与日程建议')).toBeTruthy()
  expect(screen.getByText(/只从当前内容的正文提取/)).toBeTruthy()
  await userEvent.click(screen.getByRole('combobox', { name: '选择笔记内容' }))
  expect(screen.queryByRole('option', { name: /历史材料不应出现/ })).toBeNull()
  expect(screen.getByRole('option', { name: /零录音材料/ })).toBeTruthy()
  await userEvent.keyboard('{Escape}')
  expect(screen.queryByRole('button', { name: '明确设为当前版本' })).toBeNull()
  expect(screen.queryByText('高级选项：版本历史与处理详情')).toBeNull()
  expect(screen.queryByText(/材料来源版本/)).toBeNull()
  expect(screen.queryByText(/固定内容 SHA-256/)).toBeNull()
  expect(screen.queryByText(/来源摘要/)).toBeNull()
  expect(screen.queryByText(new RegExp(sourceVersionId))).toBeNull()
  expect(screen.queryByText(new RegExp(taskId))).toBeNull()
  expect(screen.queryByText(new RegExp(versionId))).toBeNull()

  await userEvent.click(await screen.findByText(/更多：历史任务与处理进度/))
  await userEvent.click(await screen.findByRole('button', { name: /材料覆盖完整/ }))
  expect(await screen.findByRole('status', { name: '当前整理任务摘要' })).toBeTruthy()
  await userEvent.click(await screen.findByText(/更多：任务详情、停止与恢复/))
  expect(await screen.findByRole('heading', { name: '当前任务进度' })).toBeTruthy()
  expect(screen.queryByText(/任务 ID/)).toBeNull()
  expect(screen.queryByText(/材料来源版本/)).toBeNull()
  expect(screen.queryByText(/输入摘要/)).toBeNull()

  await userEvent.click(await screen.findByRole('button', { name: '采用这份整理结果' }))
  await waitFor(() => expect(derived.select).toHaveBeenCalledWith(artifactId, expect.objectContaining({ versionId })))
})

it('hides raw derived phases, attempt diagnostics and issue messages from ordinary users', async () => {
  auth.user.role = 'user'
  const rawTask: NoteDerivedTask = {
    ...task,
    phase: 'VendorPhase',
    issues: [{
      code: 'provider_issue_code',
      message: 'provider issue diagnostic',
      sourcePath: 'internal/path',
      stepKey: 'internal-step-key',
    }],
  }
  const rawStep = {
    key: 'internal-step-key',
    kind: 'VendorStepKind' as never,
    status: 'VendorStepStatus',
    attempts: [{
      requestId: 'abababab-abab-4bab-8bab-abababababab',
      status: 'VendorAttemptStatus',
      errorCode: 'provider_attempt_code',
      errorMessage: 'provider attempt diagnostic',
      logId: 'cdcdcdcd-cdcd-4cdc-8cdc-cdcdcdcdcdcd',
    }],
  }
  derived.tasks.mockResolvedValue([rawTask])
  derived.task.mockResolvedValue(rawTask)
  derived.steps.mockResolvedValue({ total: 1, offset: 0, items: [rawStep] })
  derived.stop.mockRejectedValue(new Error('provider stop diagnostic'))

  page(); await chooseSource()
  await userEvent.click(await screen.findByText(/更多：历史任务与处理进度/))
  await userEvent.click(await screen.findByRole('button', { name: /材料覆盖完整/ }))

  const summary = await screen.findByRole('status', { name: '当前整理任务摘要' })
  expect(summary.textContent).toContain('处理状态待核对')
  await userEvent.click(await screen.findByText(/更多：任务详情、停止与恢复/))
  expect(await screen.findByRole('heading', { name: '当前任务进度' })).toBeTruthy()
  expect(screen.queryAllByText(/VendorPhase/)).toHaveLength(0)
  expect(screen.queryAllByText(/provider issue diagnostic/)).toHaveLength(0)
  expect(screen.getByText('这项材料或步骤尚未完整处理，请刷新核对。')).toBeTruthy()

  await userEvent.click(screen.getByText('步骤与尝试详情'))
  expect(await screen.findByText(/第 1 次/)).toBeTruthy()
  expect(screen.queryAllByText(/VendorStepKind|VendorStepStatus|VendorAttemptStatus|provider attempt diagnostic|provider_attempt_code|provider_issue_code|internal-step-key|internal\/path/)).toHaveLength(0)
  expect(screen.getAllByText('处理步骤').length).toBeGreaterThan(0)
  expect(screen.getAllByText(/处理状态待核对/).length).toBeGreaterThan(0)

  await userEvent.click(screen.getByRole('button', { name: /停止后续步骤/ }))
  expect(await screen.findByText(/停止状态未确认/)).toBeTruthy()
  expect(screen.queryAllByText(/provider stop diagnostic/)).toHaveLength(0)
})

it('keeps raw derived phases and attempt diagnostics for admins', async () => {
  const rawTask: NoteDerivedTask = {
    ...task,
    phase: 'VendorPhase',
    issues: [{
      code: 'provider_issue_code',
      message: 'provider issue diagnostic',
      sourcePath: 'internal/path',
      stepKey: 'internal-step-key',
    }],
  }
  const rawStep = {
    key: 'internal-step-key',
    kind: 'VendorStepKind' as never,
    status: 'VendorStepStatus',
    attempts: [{
      requestId: 'abababab-abab-4bab-8bab-abababababab',
      status: 'VendorAttemptStatus',
      errorCode: 'provider_attempt_code',
      errorMessage: 'provider attempt diagnostic',
      logId: 'cdcdcdcd-cdcd-4cdc-8cdc-cdcdcdcdcdcd',
    }],
  }
  derived.tasks.mockResolvedValue([rawTask])
  derived.task.mockResolvedValue(rawTask)
  derived.steps.mockResolvedValue({ total: 1, offset: 0, items: [rawStep] })

  page(); await chooseSource()
  await userEvent.click(await screen.findByText(/更多：历史任务与处理进度/))
  await userEvent.click(await screen.findByRole('button', { name: /材料覆盖完整/ }))

  const summary = await screen.findByRole('status', { name: '当前整理任务摘要' })
  expect(summary.textContent).toContain('VendorPhase')
  await userEvent.click(await screen.findByText(/更多：任务详情、停止与恢复/))
  expect(await screen.findByRole('heading', { name: '当前任务进度' })).toBeTruthy()
  expect(screen.getAllByText(/VendorPhase/).length).toBeGreaterThan(0)
  expect(screen.getByText('provider issue diagnostic')).toBeTruthy()
  expect(screen.getByText(/provider_issue_code/)).toBeTruthy()

  await userEvent.click(screen.getByText('步骤、尝试与 endpoint 详情'))
  expect(await screen.findByText(/第 1 次/)).toBeTruthy()
  expect(screen.getAllByText(/VendorStepKind/).length).toBeGreaterThan(0)
  expect(screen.getAllByText(/VendorStepStatus/).length).toBeGreaterThan(0)
  expect(screen.getByText(/VendorAttemptStatus/)).toBeTruthy()
  expect(screen.getByText(/provider attempt diagnostic/)).toBeTruthy()
  expect(screen.getByText(/provider_attempt_code/)).toBeTruthy()
})

it('hides raw derived read errors from ordinary users', async () => {
  auth.user.role = 'user'
  derived.tasks.mockResolvedValue([task])
  derived.destinations.mockRejectedValue(new Error('provider destination diagnostic'))
  derived.steps.mockRejectedValue(new Error('provider steps diagnostic'))

  page(); await chooseSource()

  expect(await screen.findByText(/安全目标加载失败/)).toBeTruthy()
  expect(screen.queryAllByText(/provider destination diagnostic/)).toHaveLength(0)
  await userEvent.click(await screen.findByText(/更多：历史任务与处理进度/))
  await userEvent.click(await screen.findByRole('button', { name: /材料覆盖完整/ }))
  await userEvent.click(await screen.findByText(/更多：任务详情、停止与恢复/))
  expect(await screen.findByText(/步骤读取失败/)).toBeTruthy()
  expect(screen.queryAllByText(/provider steps diagnostic/)).toHaveLength(0)
})

it('hides raw artifact and manual-save errors from ordinary users', async () => {
  auth.user.role = 'user'
  const headArtifact: NoteDerivedArtifact = { ...artifact, headVersionId: versionId }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockRejectedValue(new Error('provider version diagnostic'))

  page(); await chooseSource()

  expect(await screen.findByText(/内容读取失败：请刷新核对/)).toBeTruthy()
  expect(screen.queryAllByText(/provider version diagnostic/)).toHaveLength(0)

  cleanup()
  derived.version.mockResolvedValue({ ...version, isHead: true })
  derived.saveManual.mockRejectedValue(new Error('provider manual-save diagnostic'))
  page(); await chooseSource()
  await userEvent.click(await screen.findByRole('button', { name: '保存修改' }))

  expect(await screen.findByText(/保存状态未确认/)).toBeTruthy()
  expect(screen.queryAllByText(/provider manual-save diagnostic/)).toHaveLength(0)
})

it('keeps raw artifact and manual-save errors for admins', async () => {
  const headArtifact: NoteDerivedArtifact = { ...artifact, headVersionId: versionId }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockRejectedValue(new Error('provider version diagnostic'))

  page(); await chooseSource()

  expect(await screen.findByText(/版本读取失败：provider version diagnostic/)).toBeTruthy()

  cleanup()
  derived.version.mockResolvedValue({ ...version, isHead: true })
  derived.saveManual.mockRejectedValue(new Error('provider manual-save diagnostic'))
  page(); await chooseSource()
  await userEvent.click(await screen.findByRole('button', { name: '保存为人工修订' }))

  expect(await screen.findByText(/provider manual-save diagnostic/)).toBeTruthy()
})

it('ignores a historical version in an ordinary-user processing link', async () => {
  auth.user.role = 'user'
  const historySource = {
    ...source,
    versionId: 'abababab-abab-4bab-8bab-abababababab',
    title: '历史材料不应出现',
    isCurrent: false,
  }
  derived.sources.mockResolvedValue({ total: 2, offset: 0, items: [historySource, source] })
  window.history.replaceState({}, '', `/?noteId=${noteId}&version=${historySource.versionId}`)

  page()

  expect(await screen.findByText('这份内容没有录音，可直接整理成完整记录和分析。')).toBeTruthy()
  expect(screen.queryByText('历史材料不应出现')).toBeNull()
  expect(screen.queryByText(new RegExp(historySource.versionId))).toBeNull()
})

it('lets ordinary users inspect and adopt a newer result without version history', async () => {
  auth.user.role = 'user'
  const nextVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const headArtifact: NoteDerivedArtifact = {
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...artifact.versions[0]!, isHead: true },
      {
        ...artifact.versions[0]!,
        versionId: nextVersionId,
        createdAt: '2026-10-29T00:01:00Z',
        isHead: false,
      },
    ],
  }
  const nextVersion: NoteDerivedVersion = {
    ...version,
    versionId: nextVersionId,
    contentMarkdown: '新生成候选正文',
    createdAt: '2026-10-29T00:01:00Z',
    isHead: false,
  }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockImplementation((_artifactId: string, id: string) =>
    Promise.resolve(id === nextVersionId ? nextVersion : version))
  derived.select.mockResolvedValue({
    ...headArtifact,
    revision: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    headVersionId: nextVersionId,
  })

  page(); await chooseSource()

  expect(await screen.findByText('有新的整理结果尚未采用；当前内容保持不变。')).toBeTruthy()
  expect(screen.queryByText('高级选项：版本历史与处理详情')).toBeNull()
  expect(screen.queryByText(new RegExp(nextVersionId))).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '查看新结果' }))

  expect(await screen.findByDisplayValue('新生成候选正文')).toBeTruthy()
  expect(screen.queryByText(new RegExp(nextVersionId))).toBeNull()
  await userEvent.click(screen.getByRole('button', { name: '采用这份整理结果' }))

  await waitFor(() => expect(derived.select).toHaveBeenCalledWith(
    artifactId,
    expect.objectContaining({ versionId: nextVersionId }),
  ))
})

it('offers only the newest unselected result that is newer than the current head', async () => {
  auth.user.role = 'user'
  const headCreatedAt = '2026-10-30T00:00:00Z'
  const staleVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const newestVersionId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
  const baseSummary = artifact.versions[0]!
  const headArtifact: NoteDerivedArtifact = {
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...baseSummary, versionId: staleVersionId, createdAt: '2026-10-29T00:00:00Z', isHead: false },
      { ...baseSummary, versionId, createdAt: headCreatedAt, isHead: true },
      { ...baseSummary, versionId: newestVersionId, createdAt: '2026-10-31T00:00:00Z', isHead: false },
    ],
  }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockImplementation((_artifactId: string, id: string) => Promise.resolve(id === newestVersionId
    ? {
        ...version,
        versionId: newestVersionId,
        contentMarkdown: '最新候选正文',
        createdAt: '2026-10-31T00:00:00Z',
        isHead: false,
      }
    : { ...version, versionId, createdAt: headCreatedAt, isHead: true }))

  page(); await chooseSource()

  expect(await screen.findByText('有新的整理结果尚未采用；当前内容保持不变。')).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: '查看新结果' }))
  expect(await screen.findByDisplayValue('最新候选正文')).toBeTruthy()
  expect(screen.queryByText(new RegExp(staleVersionId))).toBeNull()
})

it('does not prompt for an older unselected result after a newer head is current', async () => {
  auth.user.role = 'user'
  const staleVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const baseSummary = artifact.versions[0]!
  derived.artifacts.mockResolvedValue([{
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...baseSummary, versionId: staleVersionId, createdAt: '2026-10-28T00:00:00Z', isHead: false },
      { ...baseSummary, versionId, createdAt: '2026-10-30T00:00:00Z', isHead: true },
    ],
  }])
  derived.version.mockResolvedValue({
    ...version,
    versionId,
    contentMarkdown: '当前已采用正文',
    createdAt: '2026-10-30T00:00:00Z',
    isHead: true,
  })

  page(); await chooseSource()

  expect(await screen.findByDisplayValue('当前已采用正文')).toBeTruthy()
  expect(screen.queryByText('有新的整理结果尚未采用；当前内容保持不变。')).toBeNull()
  expect(screen.queryByRole('button', { name: '查看新结果' })).toBeNull()
})

it('shows the selected new result instead of an existing local draft and restores the draft when returning', async () => {
  auth.user.role = 'user'
  const nextVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const headArtifact: NoteDerivedArtifact = {
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...artifact.versions[0]!, isHead: true },
      {
        ...artifact.versions[0]!,
        versionId: nextVersionId,
        createdAt: '2026-10-29T00:01:00Z',
        isHead: false,
      },
    ],
  }
  const nextVersion: NoteDerivedVersion = {
    ...version,
    versionId: nextVersionId,
    contentMarkdown: '新生成候选正文',
    createdAt: '2026-10-29T00:01:00Z',
    isHead: false,
  }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockImplementation((_artifactId: string, id: string) =>
    Promise.resolve(id === nextVersionId ? nextVersion : version))

  page(); await chooseSource()

  const editor = await screen.findByLabelText('完整记录正文')
  await userEvent.clear(editor)
  await userEvent.type(editor, '当前内容的本机旧草稿')
  expect(screen.getByText('有未保存修改')).toBeTruthy()

  await userEvent.click(screen.getByRole('button', { name: '查看新结果' }))
  expect(await screen.findByDisplayValue('新生成候选正文')).toBeTruthy()
  expect(screen.queryByDisplayValue('当前内容的本机旧草稿')).toBeNull()

  await userEvent.click(screen.getByRole('button', { name: '返回当前内容' }))
  expect(await screen.findByDisplayValue('当前内容的本机旧草稿')).toBeTruthy()
})

it('mounts task suggestions only for the selected owned artifact version', async () => {
  derived.artifacts.mockResolvedValue([artifact])
  page(); await chooseSource()

  expect(await screen.findByText('从笔记提取任务与日程建议')).toBeTruthy()
  await waitFor(() => expect(taskSuggestions.list).toHaveBeenCalledWith(noteId, expect.any(AbortSignal)))
  expect(derived.version).toHaveBeenCalledWith(artifactId, versionId, expect.any(AbortSignal))
})

it('keeps a dirty local draft across selecting a new server version and reload', async () => {
  const nextVersionId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
  const headArtifact: NoteDerivedArtifact = {
    ...artifact,
    headVersionId: versionId,
    versions: [
      { ...artifact.versions[0]!, isHead: true },
      { ...artifact.versions[0]!, versionId: nextVersionId, createdAt: '2026-10-29T00:01:00Z', isHead: false },
    ],
  }
  const nextVersion: NoteDerivedVersion = {
    ...version, versionId: nextVersionId, contentMarkdown: '新生成候选正文', createdAt: '2026-10-29T00:01:00Z',
  }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockImplementation((_artifactId: string, id: string) => Promise.resolve(id === nextVersionId ? nextVersion : version))
  derived.select.mockResolvedValue({ ...headArtifact, revision: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', headVersionId: nextVersionId })

  const first = page(); await chooseSource()
  const editor = await screen.findByLabelText('完整记录正文')
  await userEvent.clear(editor); await userEvent.type(editor, '不能因选择新版本而丢失的本机全文')
  await userEvent.click(screen.getByRole('combobox', { name: '完整记录版本历史' }))
  const versionOptions = await screen.findAllByRole('option')
  await userEvent.click(versionOptions[1]!)
  await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))
  await screen.findByText(/本机草稿基于 .*已保留/)
  first.unmount()

  page(); await chooseSource()
  expect((await screen.findByLabelText('完整记录正文') as HTMLTextAreaElement).value).toBe('不能因选择新版本而丢失的本机全文')
  expect(screen.getByText('有未保存修改')).toBeTruthy()
})

it('keeps multiple consecutive edits after reload using the returned local CAS metadata', async () => {
  derived.artifacts.mockResolvedValue([artifact])
  const first = page(); await chooseSource()
  const editor = await screen.findByLabelText('完整记录正文')
  await userEvent.clear(editor)
  await userEvent.type(editor, '第一次输入')
  await userEvent.type(editor, '，第二次输入')
  await userEvent.type(editor, '，第三次输入')
  first.unmount()

  page(); await chooseSource()
  expect((await screen.findByLabelText('完整记录正文') as HTMLTextAreaElement).value).toBe('第一次输入，第二次输入，第三次输入')
  expect(screen.getByText('有未保存修改')).toBeTruthy()
})

it('preserves input typed while a manual save is awaiting its receipt', async () => {
  const headArtifact: NoteDerivedArtifact = { ...artifact, headVersionId: versionId }
  derived.artifacts.mockResolvedValue([headArtifact])
  derived.version.mockResolvedValue({ ...version, isHead: true })
  let resolveSave: ((value: unknown) => void) | undefined
  derived.saveManual.mockReturnValue(new Promise((resolve) => { resolveSave = resolve }))

  page(); await chooseSource()
  const editor = await screen.findByLabelText('完整记录正文')
  await userEvent.clear(editor); await userEvent.type(editor, '保存前文本')
  await userEvent.click(await screen.findByRole('button', { name: '保存为人工修订' }))
  await waitFor(() => expect(derived.saveManual).toHaveBeenCalledOnce())
  const body = derived.saveManual.mock.calls[0]?.[1] as { revisionId: string }
  await userEvent.type(editor, '，保存期间的新输入')
  resolveSave?.({ artifact: { ...headArtifact, headVersionId: body.revisionId }, versionId: body.revisionId, hasConflict: false, isHead: true })

  await screen.findByText(/保存期间的新输入仍保留/)
  expect((screen.getByLabelText('完整记录正文') as HTMLTextAreaElement).value).toBe('保存前文本，保存期间的新输入')
  expect(screen.getByText('有未保存修改')).toBeTruthy()
})
