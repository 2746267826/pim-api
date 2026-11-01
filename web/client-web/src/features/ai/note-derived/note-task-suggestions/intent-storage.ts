import type { StartNoteTaskSuggestionExtraction } from './api'

const extractionPrefix = 'pim.note-task-suggestions.extraction-intent.v1'
const extractionStatePrefix = 'pim.note-task-suggestions.extraction-state.v1'
const confirmPrefix = 'pim.note-task-suggestions.confirm-intent.v1'

const scope = (prefix: string, userId: string, serverBinding: string, versionId: string, suffix = '') =>
  `${prefix}/${encodeURIComponent(userId)}/${encodeURIComponent(serverBinding)}/${encodeURIComponent(versionId)}${suffix}`

export const extractionIntentScope = (userId: string, serverBinding: string, versionId: string) =>
  scope(extractionPrefix, userId, serverBinding, versionId)

export const extractionStateScope = (userId: string, serverBinding: string, versionId: string) =>
  scope(extractionStatePrefix, userId, serverBinding, versionId)

export const confirmIntentScope = (
  userId: string,
  serverBinding: string,
  suggestionId: string,
) => scope(confirmPrefix, userId, serverBinding, suggestionId)

export type ExtractionIntentState =
  | { kind: 'none' }
  | { kind: 'ready'; value: StartNoteTaskSuggestionExtraction }
  | { kind: 'blocked'; message: string }

export type ConfirmIntentState =
  | { kind: 'none' }
  | { kind: 'ready'; value: { requestId: string; revision: string } }
  | { kind: 'blocked'; message: string }

export function loadExtractionIntent(
  userId: string,
  serverBinding: string,
  noteId: string,
  artifactId: string,
  versionId: string,
  contentSha256: string,
): ExtractionIntentState {
  try {
    const raw = window.localStorage.getItem(extractionIntentScope(userId, serverBinding, versionId))
    if (!raw) return { kind: 'none' }
    const value: unknown = JSON.parse(raw)
    if (validExtraction(value, userId, noteId, artifactId, versionId, contentSha256)) {
      return { kind: 'ready', value }
    }
    return {
      kind: 'blocked',
      message: '本机保存的任务提取意图与当前账号、固定笔记版本或内容摘要不一致。为避免使用旧模型目标发送，本入口已锁定。',
    }
  } catch {
    return {
      kind: 'blocked',
      message: '本机保存的任务提取意图无法读取。为避免创建未知的付费请求，本入口已锁定。',
    }
  }
}

export function saveExtractionIntent(
  userId: string,
  serverBinding: string,
  versionId: string,
  intent: StartNoteTaskSuggestionExtraction,
): string | null {
  return save(extractionIntentScope(userId, serverBinding, versionId), intent,
    '本机无法保存冻结的任务提取标识，已停止发送请求。')
}

export function clearExtractionIntent(
  userId: string,
  serverBinding: string,
  noteId: string,
  artifactId: string,
  versionId: string,
  contentSha256: string,
  requestId: string,
) {
  clearIf(extractionIntentScope(userId, serverBinding, versionId),
    (value: unknown) => validExtraction(
      value, userId, noteId, artifactId, versionId, contentSha256) &&
      value.requestId === requestId)
}

export function loadExtractionId(userId: string, serverBinding: string, versionId: string): string {
  try {
    const value = window.localStorage.getItem(extractionStateScope(userId, serverBinding, versionId))
    return value && UUID.test(value) ? value : ''
  } catch {
    return ''
  }
}

export function saveExtractionId(
  userId: string,
  serverBinding: string,
  versionId: string,
  extractionId: string,
) {
  try {
    window.localStorage.setItem(extractionStateScope(userId, serverBinding, versionId), extractionId)
  } catch {
    // The in-memory extraction id still lets this mounted screen poll its own result.
  }
}

export function loadConfirmIntent(
  userId: string,
  serverBinding: string,
  suggestionId: string,
  expectedRevision: string,
): ConfirmIntentState {
  try {
    const raw = window.localStorage.getItem(confirmIntentScope(userId, serverBinding, suggestionId))
    if (!raw) return { kind: 'none' }
    const value: unknown = JSON.parse(raw)
    if (isRecord(value) && value.expectedUserId === userId && value.suggestionId === suggestionId &&
        isUuid(value.revision) && isUuid(value.requestId)) {
      if (value.revision === expectedRevision) {
        return { kind: 'ready', value: { requestId: value.requestId, revision: value.revision } }
      }
      return {
        kind: 'blocked',
        message: '本机保存的确认请求属于该建议的旧修订，本入口已锁定，不会覆盖旧请求或新建第二个任务。',
      }
    }
    return {
      kind: 'blocked',
      message: '本机保存的确认请求与当前建议修订不一致，本入口已锁定，不会新建第二个任务。',
    }
  } catch {
    return {
      kind: 'blocked',
      message: '本机保存的确认请求无法读取，本入口已锁定，不会新建第二个任务。',
    }
  }
}

export function saveConfirmIntent(
  userId: string,
  serverBinding: string,
  suggestionId: string,
  revision: string,
  requestId: string,
): string | null {
  return save(confirmIntentScope(userId, serverBinding, suggestionId), {
    expectedUserId: userId,
    suggestionId,
    revision,
    requestId,
  }, '本机无法保存冻结的确认请求标识，已停止创建任务。')
}

export function clearConfirmIntent(
  userId: string,
  serverBinding: string,
  suggestionId: string,
  requestId: string,
) {
  clearIf(confirmIntentScope(userId, serverBinding, suggestionId),
    (value: unknown) => isRecord(value) &&
      value.expectedUserId === userId &&
      value.suggestionId === suggestionId &&
      value.requestId === requestId)
}

function save(key: string, value: unknown, error: string): string | null {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
    return null
  } catch {
    return error
  }
}

function clearIf(key: string, matches: (value: unknown) => boolean) {
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return
    const value: unknown = JSON.parse(raw)
    if (matches(value)) window.localStorage.removeItem(key)
  } catch {
    // Best effort: the server result remains authoritative.
  }
}

type UnknownRecord = Record<string, unknown>

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const SHA256 = /^[0-9a-f]{64}$/i
const isRecord = (value: unknown): value is UnknownRecord =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const isUuid = (value: unknown): value is string => typeof value === 'string' && UUID.test(value)
const hasControlCharacters = (value: string) =>
  [...value].some((character) => {
    const code = character.charCodeAt(0)
    return code <= 0x1f || code === 0x7f
  })
const isText = (value: unknown, max = 2048): value is string =>
  typeof value === 'string' && value.length > 0 && value.length <= max &&
  !hasControlCharacters(value)

function validExtraction(
  value: unknown,
  userId: string,
  noteId: string,
  artifactId: string,
  versionId: string,
  contentSha256: string,
): value is StartNoteTaskSuggestionExtraction {
  if (!isRecord(value) || !isUuid(value.requestId) || value.expectedUserId !== userId ||
      value.versionId !== versionId) return false
  if (noteId && value.noteId !== noteId) return false
  if (artifactId && value.artifactId !== artifactId) return false
  if (contentSha256 && value.expectedContentSha256 !== contentSha256) return false
  if (value.acknowledgePossibleCharge !== true || !isRecord(value.consent) ||
      !isUuid(value.consent.modelId) || !isUuid(value.consent.modelRevision) ||
      !isText(value.consent.endpoint) || value.consent.confirmed !== true) return false
  return SHA256.test(value.expectedContentSha256 as string)
}
