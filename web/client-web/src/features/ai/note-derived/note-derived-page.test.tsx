// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NAV_GROUPS } from '@/app/nav'
import { NoteDerivedPage } from './note-derived-page'
import type { NoteDerivedArtifact, NoteDerivedTask, NoteDerivedVersion } from './api'
import type { NoteAsrTask } from '../api'

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
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => auth }))
vi.mock('./api', () => ({ noteDerivedApi: derived }))
vi.mock('../api', () => ({ aiApi: assistant }))

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
  return render(<QueryClientProvider client={client}><NoteDerivedPage /></QueryClientProvider>)
}
async function chooseSource() {
  await userEvent.click(await screen.findByRole('combobox', { name: '材料版本' }))
  await userEvent.click(await screen.findByRole('option', { name: /零录音材料/ }))
}
async function chooseDestination() {
  await userEvent.click(await screen.findByRole('combobox', { name: '本次生成目标' }))
  await userEvent.click(await screen.findByRole('option', { name: '合成共享目标' }))
}

beforeEach(() => {
  localStorage.clear(); vi.resetAllMocks(); auth.user.id = owner; auth.user.role = 'user'
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
})
afterEach(() => { cleanup(); client.clear() })

it('loads ordinary sources including zero recordings and never calls admin model settings', async () => {
  const collection = NAV_GROUPS.find((group) => group.label === '收集')
  expect(collection?.items).toContainEqual(expect.objectContaining({ to: '/native-notes', label: '笔记处理' }))
  page()
  expect(await screen.findByRole('heading', { name: '笔记处理' })).toBeTruthy()
  await chooseSource()
  expect((await screen.findAllByText(/零录音版本/)).length).toBeGreaterThan(0)
  expect(derived.sources).toHaveBeenCalled()
  expect(derived.destinations).toHaveBeenCalled()
  expect(assistant.noteAsrTasks).not.toHaveBeenCalled()
  expect(screen.queryByText('模型设置')).toBeNull()
  expect(screen.getByText(/普通账号只消费管理员已启用的共享目标/)).toBeTruthy()
})

it('explains an empty shared destination catalogue without exposing admin configuration to ordinary users', async () => {
  derived.destinations.mockResolvedValue({ total: 0, items: [] })
  page(); await chooseSource()

  expect(await screen.findByText(/暂无可用的共享生成目标/)).toBeTruthy()
  expect(screen.getByText(/普通账号不能查看或配置模型凭据/)).toBeTruthy()
  expect(screen.queryByRole('link', { name: /ASR 配置/ })).toBeNull()
})

it('shows the ordinary-user ASR start entry on native notes without admin model queries', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  page()
  await chooseSource()
  expect(await screen.findByText('启动录音转写')).toBeTruthy()
  expect(await screen.findByText('没有本机模型')).toBeTruthy()
  await waitFor(() => expect(assistant.noteAsrDestinations).toHaveBeenCalledOnce())
  expect(assistant.asrRouteConfig).toHaveBeenCalled()
  expect(assistant.models).not.toHaveBeenCalled()
  expect(assistant.asrPresets).not.toHaveBeenCalled()
})

it('shows ordinary-owner ASR task detail, retry and immutable history without admin settings', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([asrTask])
  page()
  await chooseSource()
  expect(await screen.findByText('本版本录音转写任务')).toBeTruthy()
  await userEvent.click(await screen.findByRole('button', { name: /synthetic-asr|synthetic\.invalid/ }))
  expect(await screen.findByText('逐片进度与恢复')).toBeTruthy()
  expect(await screen.findByText('重处理（显式确认）')).toBeTruthy()
  expect(await screen.findByText('重处理历史（不可变）')).toBeTruthy()
  expect(assistant.models).not.toHaveBeenCalled()
  expect(assistant.asrPresets).not.toHaveBeenCalled()
})

it('renders a nullable local task without touching destination fields or admin settings', async () => {
  derived.sources.mockResolvedValue({ total: 1, offset: 0, items: [{ ...source, recordings: 1, totalSamples: 16000 }] })
  assistant.noteAsrTasks.mockResolvedValue([localAsrTask])
  assistant.noteAsrTask.mockResolvedValue(localAsrTask)
  page()
  await chooseSource()
  expect(await screen.findByText('本机执行 · 未发送云端')).toBeTruthy()
  await userEvent.click(await screen.findByRole('button', { name: /本机执行/ }))
  expect(await screen.findByText('逐片进度与恢复')).toBeTruthy()
  expect(screen.getByText(/本任务发送目标：本机执行/)).toBeTruthy()
  expect(assistant.models).not.toHaveBeenCalled()
})

it('requires exact destination consent and creates with a persisted UUID intent', async () => {
  page(); await chooseSource(); await chooseDestination()
  const checkbox = screen.getByRole('checkbox', { name: /确认将固定版本的完整正文/ })
  expect((checkbox as HTMLInputElement).disabled).toBe(false)
  await userEvent.click(checkbox)
  await userEvent.click(screen.getByRole('button', { name: /创建派生任务/ }))
  await waitFor(() => expect(derived.start).toHaveBeenCalledOnce())
  const body = derived.start.mock.calls[0]?.[0]
  expect(body).toEqual(expect.objectContaining({
    expectedUserId: owner, versionId: sourceVersionId, asrTaskId: null,
    consent: { modelId: destinationId, modelRevision: destinationRevision, endpoint: destination.endpoint, confirmed: true },
  }))
  expect(body.taskId).toMatch(/^[0-9a-f-]{36}$/)
  await screen.findByText('任务步骤与覆盖')
})

it('shows generated candidates and requires an explicit head selection before manual editing', async () => {
  derived.artifacts.mockResolvedValue([artifact])
  page(); await chooseSource()
  expect(await screen.findByText('完整记录')).toBeTruthy()
  expect(await screen.findByText('等待明确选择')).toBeTruthy()
  await waitFor(() => expect(derived.version).toHaveBeenCalled())
  await userEvent.click(await screen.findByRole('button', { name: '明确设为当前版本' }))
  await waitFor(() => expect(derived.select).toHaveBeenCalledWith(artifactId, expect.objectContaining({ expectedHeadRevision: artifact.revision, versionId })))
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
