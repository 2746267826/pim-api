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
    (value): value is NoteAsrBatchInitialIntent => initialIntent(value, userId, versionId),
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
    (value): value is NoteAsrBatchFailedIntent => failedIntent(value, userId, sourceJobId),
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

function load<T>(key: string, valid: (value: unknown) => value is T, blocked: string): NoteAsrBatchIntentState<T> {
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return { kind: 'none' }
    const value: unknown = JSON.parse(raw)
    return valid(value) ? { kind: 'ready', value } : { kind: 'blocked', message: blocked }
  } catch {
    return { kind: 'blocked', message: blocked }
  }
}

type UnknownRecord = Record<string, unknown>

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const isRecord = (value: unknown): value is UnknownRecord =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const isUuid = (value: unknown): value is string => typeof value === 'string' && UUID.test(value)
const isText = (value: unknown, max = 512): value is string =>
  typeof value === 'string' && value.length > 0 && value.length <= max && !/[\u0000-\u001f\u007f]/.test(value)

function initialIntent(value: unknown, userId: string, versionId: string): boolean {
  if (!isRecord(value) || value.kind !== 'initial' || !isUuid(value.taskId) ||
      value.expectedUserId !== userId || value.versionId !== versionId ||
      (value.phase !== 'prepare' && value.phase !== 'submit')) return false
  const taskId = value.taskId
  if (!start(value.start, userId, versionId, taskId)) return false
  if (value.phase === 'prepare') {
    return route(value.route) && target(value.target) && reason(value.reason) &&
      startMatches(value.start, value.route, value.target)
  }
  return request(value.request, {
    clientTaskId: taskId,
    sourceTaskId: taskId,
    expectedUserId: userId,
  }) && startMatchesRequest(value.start, value.request)
}

function failedIntent(value: unknown, userId: string, sourceJobId: string): boolean {
  return isRecord(value) && value.kind === 'failed' && value.expectedUserId === userId &&
    isUuid(value.sourceTaskId) &&
    request(value.request, {
      sourceJobId,
      expectedUserId: userId,
    })
}

function start(value: unknown, userId: string, versionId: string, taskId: string): value is NoteAsrStart {
  if (!isRecord(value) || value.taskId !== taskId || value.expectedUserId !== userId ||
      value.versionId !== versionId || value.parentTaskId != null || value.reprocessReason != null ||
      (value.language != null && !isText(value.language, 32))) return false
  return consent(value.consent) && startRoute(value.route)
}

function consent(value: unknown): boolean {
  return isRecord(value) && isUuid(value.modelId) && isUuid(value.modelRevision) &&
    isText(value.endpoint, 2048) && value.presetId === 'mimo-multimodal' &&
    isText(value.presetVersion, 64) && value.confirmed === true
}

function startRoute(value: unknown): boolean {
  return isRecord(value) && value.route === 'cloud-multimodal' && isUuid(value.configRevision) &&
    isText(value.consentVersion, 64) && value.confirmed === true &&
    value.executionMode === 'batch' && value.batchScopeConfirmed === true
}

function route(value: unknown): value is NoteAsrReprocessRoute {
  return isRecord(value) && value.route === 'cloud-multimodal' &&
    isUuid(value.expectedConfigRevision) && isText(value.expectedConsentVersion, 64) &&
    value.confirmed === true && value.executionMode === 'batch' && value.batchScopeConfirmed === true
}

function target(value: unknown): value is NoteAsrBatchTarget {
  return isRecord(value) && isUuid(value.modelId) && isUuid(value.modelRevision) &&
    isText(value.consentRef, 128) && value.confirmed === true &&
    value.batchScopeConfirmed === true && value.completionWindow === '24h'
}

function reason(value: unknown): value is NoteAsrReprocessReason {
  return isRecord(value) && (value.kind === 'manual' || value.kind === 'quality') &&
    (value.text == null || (typeof value.text === 'string' && value.text.length <= 200 &&
      !/[\u0000-\u001f\u007f]/.test(value.text)))
}

function request(value: unknown, expected: {
  clientTaskId?: string
  sourceTaskId?: string
  sourceJobId?: string
  expectedUserId: string
}): value is NoteAsrBatchFromTaskRequest | NoteAsrBatchRetryFailedRequest {
  if (!isRecord(value) || !isUuid(value.clientTaskId) ||
      value.expectedUserId !== expected.expectedUserId || !route(value.route) ||
      !target(value.target) || !reason(value.reason)) return false
  if (expected.clientTaskId && value.clientTaskId !== expected.clientTaskId) return false
  if (expected.sourceTaskId) {
    return value.sourceTaskId === expected.sourceTaskId && isUuid(value.expectedSourceRevision)
  }
  return value.sourceJobId === expected.sourceJobId
}

function startMatches(start: unknown, routeValue: unknown, targetValue: unknown): boolean {
  if (!isRecord(start) || !isRecord(routeValue) || !isRecord(targetValue)) return false
  return isRecord(start.consent) && start.consent.modelId === targetValue.modelId &&
    start.consent.modelRevision === targetValue.modelRevision &&
    isRecord(start.route) && start.route.configRevision === routeValue.expectedConfigRevision &&
    start.route.consentVersion === routeValue.expectedConsentVersion &&
    targetValue.consentRef === routeValue.expectedConsentVersion
}

function startMatchesRequest(startValue: unknown, requestValue: unknown): boolean {
  if (!isRecord(requestValue) || !isRecord(requestValue.route) || !isRecord(requestValue.target)) return false
  if (!isRecord(startValue) || !isRecord(startValue.consent) || !isRecord(startValue.route)) return false
  return startValue.consent.modelId === requestValue.target.modelId &&
    startValue.consent.modelRevision === requestValue.target.modelRevision &&
    startValue.route.configRevision === requestValue.route.expectedConfigRevision &&
    startValue.route.consentVersion === requestValue.route.expectedConsentVersion &&
    startValue.route.consentVersion === requestValue.target.consentRef
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
