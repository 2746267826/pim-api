// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteTaskSuggestionsPanel } from './note-task-suggestions-panel'
import {
  confirmIntentScope,
  extractionIntentScope,
  saveConfirmIntent,
} from './intent-storage'
import type {
  NoteTaskSuggestion,
  NoteTaskSuggestionExtraction,
} from './api'
import type { NoteDerivedDestination, NoteDerivedVersion } from '../api'

const derived = vi.hoisted(() => ({
  version: vi.fn(),
  destinations: vi.fn(),
}))
const api = vi.hoisted(() => ({
  list: vi.fn(),
  get: vi.fn(),
  startExtraction: vi.fn(),
  extraction: vi.fn(),
  update: vi.fn(),
  confirm: vi.fn(),
  dismiss: vi.fn(),
}))
vi.mock('../api', () => ({ noteDerivedApi: derived }))
vi.mock('./api', () => ({ noteTaskSuggestionsApi: api }))

beforeAll(() => {
  if (!HTMLElement.prototype.hasPointerCapture) HTMLElement.prototype.hasPointerCapture = () => false
  if (!HTMLElement.prototype.releasePointerCapture) HTMLElement.prototype.releasePointerCapture = () => {}
  if (!HTMLElement.prototype.scrollIntoView) HTMLElement.prototype.scrollIntoView = () => {}
})

const userId = '11111111-1111-4111-8111-111111111111'
const serverBinding = 'https://synthetic.invalid'
const noteId = '22222222-2222-4222-8222-222222222222'
const artifactId = '33333333-3333-4333-8333-333333333333'
const versionId = '44444444-4444-4444-8444-444444444444'
const extractionId = '55555555-5555-4555-8555-555555555555'
const suggestionId = '66666666-6666-4666-8666-666666666666'
const suggestionRevision = '77777777-7777-4777-8777-777777777777'
const modelId = '88888888-8888-4888-8888-888888888888'
const modelRevision = '99999999-9999-4999-8999-999999999999'
const contentSha256 = 'a'.repeat(64)

const destination: NoteDerivedDestination = {
  backgroundProcessingAvailable: true,
  capabilities: ['Text'],
  displayName: '合成文本目标',
  endpoint: 'https://text.synthetic.invalid/v1/',
  model: 'synthetic-text',
  modelId,
  modelRevision,
}
const version: NoteDerivedVersion = {
  aiCallLogId: null,
  artifactId,
  author: 'Model',
  contentMarkdown: '明天给客户发送报价单。',
  contentSha256,
  createdAt: '2026-11-01T10:00:00Z',
  generationTaskId: null,
  hasConflict: false,
  inputSha256: 'b'.repeat(64),
  isHead: true,
  kind: 'CompleteRecord',
  manifestSha256: 'c'.repeat(64),
  modelEndpoint: destination.endpoint,
  modelProfileId: modelId,
  modelRevision,
  observedHeadVersionId: null,
  parentVersionId: null,
  planVersion: 'v1',
  promptVersion: 'p1',
  sourceCompleteRecordVersionId: null,
  sourceVersionId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  versionId,
}
const suggestion: NoteTaskSuggestion = {
  artifactId,
  calendarId: null,
  confirmedAt: null,
  createdAt: '2026-11-01T10:01:00Z',
  description: '报价单需要在下午前核对。',
  dismissedAt: null,
  due: null,
  extractionId,
  id: suggestionId,
  noteId,
  ordinal: 0,
  priority: 5,
  revision: suggestionRevision,
  sourceContentSha256: contentSha256,
  sourceGenerationTaskId: null,
  sourceInputSha256: version.inputSha256,
  sourceManifestSha256: version.manifestSha256,
  status: 'Draft',
  taskBookId: null,
  taskId: null,
  title: '发送客户报价单',
  updatedAt: '2026-11-01T10:01:00Z',
  versionId,
}
const extraction: NoteTaskSuggestionExtraction = {
  aiCallLogId: null,
  artifactId,
  attemptCount: 1,
  createdAt: '2026-11-01T10:02:00Z',
  errorCode: null,
  errorMessage: null,
  finishedAt: null,
  id: extractionId,
  modelEndpoint: destination.endpoint,
  modelProfileId: modelId,
  modelRevision,
  noteId,
  promptVersion: 'task-suggestions-v1',
  requestId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
  revision: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  sourceContentSha256: contentSha256,
  sourceGenerationTaskId: null,
  sourceInputSha256: version.inputSha256,
  sourceManifestSha256: version.manifestSha256,
  startedAt: '2026-11-01T10:02:00Z',
  status: 'Queued',
  suggestionCount: 0,
  updatedAt: '2026-11-01T10:02:00Z',
  versionId,
}

let client: QueryClient

function page() {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  return render(
    <QueryClientProvider client={client}>
      <NoteTaskSuggestionsPanel
        userId={userId}
        serverBinding={serverBinding}
        noteId={noteId}
        artifactId={artifactId}
        versionId={versionId}
      />
    </QueryClientProvider>,
  )
}

async function chooseDestination() {
  const trigger = await screen.findByRole('combobox', { name: '文本模型目标' })
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
  fireEvent.click(trigger)
  const listbox = await screen.findByRole('listbox')
  fireEvent.click(within(listbox).getByRole('option', { name: /合成文本目标/ }))
}

async function chooseAndConfirmCharge() {
  await chooseDestination()
  await userEvent.click(screen.getByRole('checkbox', {
    name: /确认将固定版本完整正文发送到所选文本模型/,
  }))
}

beforeEach(() => {
  localStorage.clear()
  vi.resetAllMocks()
  derived.version.mockResolvedValue(version)
  derived.destinations.mockResolvedValue({ total: 1, items: [destination] })
  api.list.mockResolvedValue({ total: 0, items: [] })
  api.startExtraction.mockResolvedValue(extraction)
  api.extraction.mockResolvedValue(extraction)
})

afterEach(() => {
  cleanup()
  client?.clear()
})

describe('NoteTaskSuggestionsPanel', () => {
  it('does not request extraction before explicit charge consent and freezes the owned artifact digest', async () => {
    page()
    await chooseDestination()
    expect(api.startExtraction).not.toHaveBeenCalled()
    expect((screen.getByRole('button', {
      name: '确认并提取任务建议',
    }) as HTMLButtonElement).disabled).toBe(true)

    await userEvent.click(screen.getByRole('checkbox', {
      name: /确认将固定版本完整正文发送到所选文本模型/,
    }))
    await userEvent.click(screen.getByRole('button', { name: '确认并提取任务建议' }))

    await waitFor(() => expect(api.startExtraction).toHaveBeenCalledOnce())
    expect(api.startExtraction).toHaveBeenCalledWith(expect.objectContaining({
      expectedUserId: userId,
      noteId,
      artifactId,
      versionId,
      expectedContentSha256: contentSha256,
      acknowledgePossibleCharge: true,
      consent: {
        modelId,
        modelRevision,
        endpoint: destination.endpoint,
        confirmed: true,
      },
    }))
    expect(api.startExtraction.mock.calls[0]?.[0].requestId).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('reuses the persisted extraction UUID after a lost response', async () => {
    api.startExtraction.mockRejectedValueOnce(new Error('response lost'))
    page()
    await chooseAndConfirmCharge()
    await userEvent.click(screen.getByRole('button', { name: '确认并提取任务建议' }))

    expect(await screen.findByText(/已冻结请求标识/)).toBeTruthy()
    const firstRequestId = api.startExtraction.mock.calls[0]?.[0].requestId
    expect(localStorage.getItem(extractionIntentScope(userId, serverBinding, versionId))).toContain(firstRequestId)

    await userEvent.click(screen.getByRole('button', { name: '按原提取标识重试' }))
    await waitFor(() => expect(api.startExtraction).toHaveBeenCalledTimes(2))
    expect(api.startExtraction.mock.calls[1]?.[0].requestId).toBe(firstRequestId)
  })

  it('reuses the frozen confirm UUID after a lost response', async () => {
    api.list.mockResolvedValue({ total: 1, items: [suggestion] })
    api.confirm
      .mockRejectedValueOnce(new Error('response lost'))
      .mockResolvedValueOnce({
        ...suggestion,
        revision: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
        status: 'Confirmed',
        taskId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
      })
    page()

    await userEvent.click(await screen.findByRole('button', { name: '确认创建任务' }))
    await userEvent.click(screen.getByRole('button', { name: '再次点击创建任务' }))
    expect(await screen.findByText(/确认请求标识已冻结/)).toBeTruthy()
    const firstRequestId = api.confirm.mock.calls[0]?.[1].requestId
    expect(localStorage.getItem(confirmIntentScope(userId, serverBinding, suggestionId))).toContain(firstRequestId)

    await userEvent.click(screen.getByRole('button', { name: '重试未确认的创建' }))
    await userEvent.click(screen.getByRole('button', { name: '按原请求再次确认' }))
    await waitFor(() => expect(api.confirm).toHaveBeenCalledTimes(2))
    expect(api.confirm.mock.calls[1]).toEqual([
      suggestionId,
      { requestId: firstRequestId, expectedRevision: suggestionRevision },
    ])
  })

  it('fails closed when a frozen confirm UUID belongs to an older revision', async () => {
    const oldRevision = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    const oldRequestId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    api.list.mockResolvedValue({ total: 1, items: [suggestion] })
    expect(saveConfirmIntent(
      userId, serverBinding, suggestionId, oldRevision, oldRequestId)).toBeNull()

    page()
    expect(await screen.findByText(/属于该建议的旧修订/)).toBeTruthy()
    expect((screen.getByRole('button', {
      name: '确认创建任务',
    }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.confirm).not.toHaveBeenCalled()
    expect(localStorage.getItem(confirmIntentScope(userId, serverBinding, suggestionId))).toContain(oldRequestId)
  })

  it('keeps edit and dismiss failures visible without turning the suggestion into success', async () => {
    api.list.mockResolvedValue({ total: 1, items: [suggestion] })
    api.update.mockRejectedValue(new Error('任务建议已被修改，请刷新后重试'))
    api.dismiss.mockRejectedValue(new Error('任务建议已被修改，请刷新后重试'))
    page()

    await userEvent.click(await screen.findByRole('button', { name: '编辑' }))
    await userEvent.click(screen.getByRole('button', { name: '保存修订' }))
    await waitFor(() => expect(api.update).toHaveBeenCalledOnce())
    expect(api.update).toHaveBeenCalledWith(suggestionId, expect.objectContaining({
      expectedRevision: suggestionRevision,
    }))
    expect(await screen.findByText(/任务建议已被修改/)).toBeTruthy()
    await userEvent.click(screen.getByRole('button', { name: '取消' }))

    await userEvent.click(screen.getByRole('button', { name: '放弃' }))
    await userEvent.click(screen.getByRole('button', { name: '再次点击确认放弃' }))
    await waitFor(() => expect(api.dismiss).toHaveBeenCalledOnce())
    expect(api.dismiss).toHaveBeenCalledWith(suggestionId, {
      expectedRevision: suggestionRevision,
    })
    expect(await screen.findAllByText(/任务建议已被修改/)).not.toHaveLength(0)
    expect(screen.getAllByText('待确认')).not.toHaveLength(0)
  })

  it('keeps confirmed and dismissed suggestions as history without task actions', async () => {
    api.list.mockResolvedValue({
      total: 2,
      items: [
        {
          ...suggestion,
          id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
          ordinal: 1,
          status: 'Confirmed',
          taskId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
        },
        {
          ...suggestion,
          id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
          ordinal: 2,
          status: 'Dismissed',
          title: '已放弃的建议',
        },
      ],
    })
    page()

    expect(await screen.findByText('已创建任务')).toBeTruthy()
    expect(screen.getByText('已放弃')).toBeTruthy()
    expect(screen.queryByRole('button', { name: '确认创建任务' })).toBeNull()
    expect(screen.queryByRole('button', { name: '编辑' })).toBeNull()
  })
})
