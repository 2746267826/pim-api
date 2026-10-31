import type {
  NoteAsrBatchFromTaskRequest,
  NoteAsrBatchRetryFailedRequest,
  NoteAsrBatchTarget,
  NoteAsrReprocessReason,
  NoteAsrReprocessRoute,
  NoteAsrStart,
} from './api'

const initialPrefix = 'pim.note-asr.batch-initial-intent.v1'
const failedPrefix = 'pim.note-asr.batch-failed-intent.v1'

export const noteAsrBatchInitialIntentScope = (userId: string, serverBinding: string, versionId: string) =>
  `${initialPrefix}/${encodeURIComponent(userId)}/${encodeURIComponent(serverBinding)}/${encodeURIComponent(versionId)}`

export const noteAsrBatchFailedIntentScope = (userId: string, serverBinding: string, sourceJobId: string) =>
  `${failedPrefix}/${encodeURIComponent(userId)}/${encodeURIComponent(serverBinding)}/${encodeURIComponent(sourceJobId)}`

export type NoteAsrBatchInitialIntent =
  | {
      kind: 'initial'
      phase: 'prepare'
      taskId: string
      expectedUserId: string
      versionId: string
      start: NoteAsrStart
      route: NoteAsrReprocessRoute
      target: NoteAsrBatchTarget
      reason: NoteAsrReprocessReason
    }
  | {
      kind: 'initial'
      phase: 'submit'
      taskId: string
      expectedUserId: string
      versionId: string
      start: NoteAsrStart
      request: NoteAsrBatchFromTaskRequest
    }

export type NoteAsrBatchFailedIntent = {
  kind: 'failed'
  expectedUserId: string
  sourceTaskId: string
  request: NoteAsrBatchRetryFailedRequest
}

export type NoteAsrBatchIntentState<T> =
  | { kind: 'none' }
  | { kind: 'ready'; value: T }
  | { kind: 'blocked'; message: string }

export function loadNoteAsrBatchInitialIntent(
  userId: string,
  serverBinding: string,
  versionId: string,
): NoteAsrBatchIntentState<NoteAsrBatchInitialIntent> {
  return load(
    noteAsrBatchInitialIntentScope(userId, serverBinding, versionId),
    (value) => value.kind === 'initial' && Boolean(value.taskId) &&
      value.expectedUserId === userId && value.versionId === versionId &&
      (value.phase === 'prepare' || value.phase === 'submit'),
    '本机保存的 Batch 提交意图无法读取或与当前账号、材料不匹配。为避免创建新的付费任务，本入口已锁定，请先核对本机记录。',
  )
}

export function saveNoteAsrBatchInitialIntent(
  userId: string,
  serverBinding: string,
  versionId: string,
  intent: NoteAsrBatchInitialIntent,
): string | null {
  return save(noteAsrBatchInitialIntentScope(userId, serverBinding, versionId), intent)
}

export function clearNoteAsrBatchInitialIntent(
  userId: string,
  serverBinding: string,
  versionId: string,
  taskId: string,
) {
  clearIf(noteAsrBatchInitialIntentScope(userId, serverBinding, versionId), (value: NoteAsrBatchInitialIntent) =>
    value.kind === 'initial' && value.taskId === taskId)
}

export function loadNoteAsrBatchFailedIntent(
  userId: string,
  serverBinding: string,
  sourceJobId: string,
): NoteAsrBatchIntentState<NoteAsrBatchFailedIntent> {
  return load(
    noteAsrBatchFailedIntentScope(userId, serverBinding, sourceJobId),
    (value) => value.kind === 'failed' && Boolean(value.request?.clientTaskId) &&
      value.expectedUserId === userId && value.request.sourceJobId === sourceJobId,
    '本机保存的失败项 Batch 提交意图无法读取或与当前账号、原任务不匹配。为避免创建新的付费任务，本入口已锁定，请先核对本机记录。',
  )
}

export function saveNoteAsrBatchFailedIntent(
  userId: string,
  serverBinding: string,
  sourceJobId: string,
  intent: NoteAsrBatchFailedIntent,
): string | null {
  return save(noteAsrBatchFailedIntentScope(userId, serverBinding, sourceJobId), intent)
}

export function clearNoteAsrBatchFailedIntent(
  userId: string,
  serverBinding: string,
  sourceJobId: string,
  clientTaskId: string,
) {
  clearIf(noteAsrBatchFailedIntentScope(userId, serverBinding, sourceJobId), (value: NoteAsrBatchFailedIntent) =>
    value.kind === 'failed' && value.request.clientTaskId === clientTaskId)
}

function load<T>(key: string, valid: (value: T) => boolean, blocked: string): NoteAsrBatchIntentState<T> {
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return { kind: 'none' }
    const value = JSON.parse(raw) as T
    return valid(value) ? { kind: 'ready', value } : { kind: 'blocked', message: blocked }
  } catch {
    return { kind: 'blocked', message: blocked }
  }
}

function save(key: string, value: unknown): string | null {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
    return null
  } catch {
    return '本机无法保存冻结的 Batch 提交标识，已停止发送请求。请检查本机存储后重试。'
  }
}

function clearIf<T>(key: string, matches: (value: T) => boolean) {
  try {
    const raw = window.localStorage.getItem(key)
    if (raw && matches(JSON.parse(raw) as T)) window.localStorage.removeItem(key)
  } catch {
    // Clearing is best-effort after a server-confirmed terminal response.
  }
}
