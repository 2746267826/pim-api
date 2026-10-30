import { apiUrl } from '@/lib/apiBase'
import { getString, setString } from '@/lib/storage'
import {
  createTextDraft,
  editTextDraft,
  isTextDraftDirty,
  type TextDraftState,
} from './draft-protection'

export type NoteDerivedArtifactKind = 'CompleteRecord' | 'IndependentAnalysis'
export type PendingMutationKind = 'create' | 'retry' | 'manual-save'

export interface NoteDerivedDraftScope {
  readonly ownerId: string
  readonly serverBinding: string
  readonly artifactId: string
  readonly sourceVersionId: string
  readonly kind: NoteDerivedArtifactKind
}

export interface PendingMutationIntent {
  readonly kind: PendingMutationKind
  readonly intentId: string
  readonly reservedAt: string
  readonly submittedText?: string
  readonly submittedEditRevision?: number
  readonly submittedBaseToken?: string
  readonly requestJson?: string
}

export interface StoredNoteDerivedDraft {
  readonly schemaVersion: 1
  readonly binding: NoteDerivedDraftScope
  readonly draft: TextDraftState
  readonly dirty: boolean
  readonly pending?: PendingMutationIntent
  readonly updatedAt: string
  /** CAS base revision; callers must adopt persistStoredDraft(...).value before the next write. */
  readonly localRevision?: number
  /** Identity of the exact local write represented by `localRevision`. */
  readonly localWriteId?: string
}

export type DraftStorageErrorCode =
  | 'unbound'
  | 'scope-mismatch'
  | 'corrupt'
  | 'text-too-large'
  | 'write-failed'
  | 'intent-conflict'
  | 'cross-tab-conflict'

export interface DraftStorageError {
  readonly code: DraftStorageErrorCode
  readonly message: string
  readonly preservedCopyKey?: string
}

export type DraftStorageResult<T> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly error: DraftStorageError }

export interface DraftStoragePort {
  get(key: string): string | null
  set(key: string, value: string): void
}

const browserStorage: DraftStoragePort = { get: getString, set: setString }

export const MAX_LOCAL_DRAFT_CHARACTERS = 1_000_000
const STORAGE_PREFIX = 'pim.noteDerivedDraft.v1'

function localRevisionOf(record: StoredNoteDerivedDraft): number {
  const revision = record.localRevision
  return revision !== undefined && Number.isSafeInteger(revision) && revision >= 0 ? revision : 0
}

function hashString(value: string): string {
  let hash = 0x811c9dc5
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193)
  }
  return (hash >>> 0).toString(36)
}

function legacyWriteId(raw: string): string {
  return `legacy-${raw.length}-${hashString(raw)}`
}

function createLocalWriteId(): string {
  const crypto = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto
  if (typeof crypto?.randomUUID === 'function') return crypto.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

/**
 * CAS/content identity. Local metadata and timestamps are deliberately omitted
 * so a caller that commits the pre-persist record can still be recognized.
 */
function draftContentFingerprint(record: StoredNoteDerivedDraft): string {
  return JSON.stringify({
    binding: record.binding,
    draft: record.draft,
    dirty: record.dirty,
    pending: record.pending ?? null,
  })
}

function localVersionOf(record: StoredNoteDerivedDraft, raw: string): {
  readonly localRevision: number
  readonly localWriteId: string
} {
  return {
    localRevision: localRevisionOf(record),
    localWriteId: record.localWriteId ?? legacyWriteId(raw),
  }
}

function currentDocumentOrigin(): string {
  return (globalThis as { location?: { origin?: string } }).location?.origin ?? 'http://localhost'
}

/** Canonical server identity follows the same configured base / host-origin rules as API calls. */
export function canonicalServerBinding(documentOrigin = currentDocumentOrigin()): string {
  try {
    const resolved = new URL(apiUrl('/'), documentOrigin)
    if (resolved.protocol !== 'http:' && resolved.protocol !== 'https:') return ''
    resolved.username = ''
    resolved.password = ''
    resolved.search = ''
    resolved.hash = ''
    return resolved.href.replace(/\/+$/, '')
  } catch {
    return ''
  }
}

export function isUsableDraftScope(scope: NoteDerivedDraftScope | null | undefined): scope is NoteDerivedDraftScope {
  return Boolean(scope?.ownerId.trim() && scope.serverBinding.trim() && scope.artifactId.trim() && scope.sourceVersionId.trim() && scope.kind)
}

export function sameDraftScope(left: NoteDerivedDraftScope, right: NoteDerivedDraftScope): boolean {
  return left.ownerId === right.ownerId &&
    left.serverBinding === right.serverBinding &&
    left.artifactId === right.artifactId &&
    left.sourceVersionId === right.sourceVersionId &&
    left.kind === right.kind
}

export function noteDerivedDraftStorageKey(scope: NoteDerivedDraftScope): string | null {
  if (!isUsableDraftScope(scope)) return null
  const parts = [scope.ownerId, scope.serverBinding, scope.artifactId, scope.sourceVersionId, scope.kind]
    .map((part) => encodeURIComponent(part))
  return `${STORAGE_PREFIX}/${parts.join('/')}`
}

export function createStoredDraft(
  scope: NoteDerivedDraftScope,
  text = '',
  baseToken = '',
  now = new Date(),
): StoredNoteDerivedDraft {
  const draft = createTextDraft(text, baseToken)
  return {
    schemaVersion: 1,
    binding: scope,
    draft,
    dirty: isTextDraftDirty(draft),
    updatedAt: now.toISOString(),
    localRevision: 0,
  }
}

export function canAdoptSelectedVersion(record: StoredNoteDerivedDraft, versionId: string): boolean {
  return !record.dirty && !record.pending && record.draft.baseToken === versionId
}

function isTextDraftState(value: unknown): value is TextDraftState {
  if (!value || typeof value !== 'object') return false
  const draft = value as Partial<TextDraftState>
  return typeof draft.text === 'string' &&
    typeof draft.savedText === 'string' &&
    typeof draft.baseToken === 'string' &&
    typeof draft.editRevision === 'number' &&
    typeof draft.generationSequence === 'number' &&
    typeof draft.saveSequence === 'number'
}

function isPendingIntent(value: unknown): value is PendingMutationIntent {
  if (!value || typeof value !== 'object') return false
  const pending = value as Partial<PendingMutationIntent>
  return (pending.kind === 'create' || pending.kind === 'retry' || pending.kind === 'manual-save') &&
    typeof pending.intentId === 'string' &&
    pending.intentId.length > 0 &&
    typeof pending.reservedAt === 'string' &&
    (pending.submittedText === undefined || typeof pending.submittedText === 'string') &&
    (pending.submittedEditRevision === undefined || typeof pending.submittedEditRevision === 'number') &&
    (pending.submittedBaseToken === undefined || typeof pending.submittedBaseToken === 'string') &&
    (pending.requestJson === undefined || typeof pending.requestJson === 'string')
}

function isStoredDraft(value: unknown): value is StoredNoteDerivedDraft {
  if (!value || typeof value !== 'object') return false
  const stored = value as Partial<StoredNoteDerivedDraft>
  if (stored.schemaVersion !== 1 || !isTextDraftState(stored.draft) || typeof stored.dirty !== 'boolean' || typeof stored.updatedAt !== 'string') return false
  if (stored.localRevision !== undefined && (!Number.isSafeInteger(stored.localRevision) || stored.localRevision < 0)) return false
  if (stored.localWriteId !== undefined && (typeof stored.localWriteId !== 'string' || !stored.localWriteId)) return false
  if (!stored.binding || typeof stored.binding !== 'object') return false
  if (stored.pending !== undefined && !isPendingIntent(stored.pending)) return false
  const scope = stored.binding as Partial<NoteDerivedDraftScope>
  return typeof scope.ownerId === 'string' &&
    typeof scope.serverBinding === 'string' &&
    typeof scope.artifactId === 'string' &&
    typeof scope.sourceVersionId === 'string' &&
    (scope.kind === 'CompleteRecord' || scope.kind === 'IndependentAnalysis')
}

export function loadStoredDraft(
  scope: NoteDerivedDraftScope | null | undefined,
  options: { storage?: DraftStoragePort } = {},
): DraftStorageResult<StoredNoteDerivedDraft | null> {
  if (!isUsableDraftScope(scope)) {
    return { ok: false, error: { code: 'unbound', message: '当前账号或服务器尚未绑定，无法读取本机草稿。' } }
  }
  const key = noteDerivedDraftStorageKey(scope)
  if (!key) return { ok: false, error: { code: 'unbound', message: '本机草稿作用域不完整。' } }
  const raw = (options.storage ?? browserStorage).get(key)
  if (raw == null) return { ok: true, value: null }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!isStoredDraft(parsed)) throw new Error('invalid draft shape')
    if (!sameDraftScope(parsed.binding, scope)) {
      return { ok: false, error: { code: 'scope-mismatch', message: '本机草稿与当前账号、服务器或材料版本不一致，未载入。' } }
    }
    return {
      ok: true,
      value: {
        ...parsed,
        localRevision: localRevisionOf(parsed),
        localWriteId: parsed.localWriteId ?? legacyWriteId(raw),
      },
    }
  } catch {
    return { ok: false, error: { code: 'corrupt', message: '本机草稿数据损坏，未载入；现有内容不会被静默替换。' } }
  }
}

export function persistStoredDraft(
  record: StoredNoteDerivedDraft,
  options: {
    maxCharacters?: number
    now?: Date
    storage?: DraftStoragePort
    allowBaseChange?: boolean
    allowPendingChange?: boolean
    expectedLocalRevision?: number
    expectedLocalWriteId?: string
  } = {},
): DraftStorageResult<StoredNoteDerivedDraft> {
  const maxCharacters = options.maxCharacters ?? MAX_LOCAL_DRAFT_CHARACTERS
  if (record.draft.text.length > maxCharacters) {
    return {
      ok: false,
      error: { code: 'text-too-large', message: `本机草稿超过 ${maxCharacters.toLocaleString()} 字符，未写入存储；当前页面内容仍保留。` },
    }
  }
  const key = noteDerivedDraftStorageKey(record.binding)
  if (!key) return { ok: false, error: { code: 'unbound', message: '本机草稿作用域不完整，未写入存储。' } }
  const storage = options.storage ?? browserStorage
  const next: StoredNoteDerivedDraft = {
    ...record,
    dirty: isTextDraftDirty(record.draft),
    updatedAt: (options.now ?? new Date()).toISOString(),
  }
  let raw: string | null
  try {
    raw = storage.get(key)
  } catch {
    return { ok: false, error: { code: 'write-failed', message: '无法核对本机草稿当前版本，未写入存储；当前页面内容仍保留。' } }
  }
  let currentRevision = 0
  if (raw != null) {
    let existing: StoredNoteDerivedDraft | null = null
    try {
      const parsed: unknown = JSON.parse(raw)
      if (!isStoredDraft(parsed)) throw new Error('invalid stored draft')
      existing = { ...parsed, ...localVersionOf(parsed, raw) }
    } catch {
      const copyKey = writeConflictCopy(storage, key, next, next.updatedAt)
      return {
        ok: false,
        error: {
          code: 'corrupt',
          message: `本机草稿原存储已损坏，拒绝覆盖；当前文本${copyKey ? '已另存为恢复副本' : '仅保留在当前页面'}。`,
          ...(copyKey ? { preservedCopyKey: copyKey } : {}),
        },
      }
    }
    if (!existing || !sameDraftScope(existing.binding, record.binding)) {
      const copyKey = writeConflictCopy(storage, key, next, next.updatedAt)
      return {
        ok: false,
        error: {
          code: 'scope-mismatch',
          message: `本机草稿绑定与当前账号/服务器不一致，拒绝覆盖；当前文本${copyKey ? '已另存为恢复副本' : '仅保留在当前页面'}。`,
          ...(copyKey ? { preservedCopyKey: copyKey } : {}),
        },
      }
    }
    const currentVersion = localVersionOf(existing, raw)
    currentRevision = currentVersion.localRevision
    if (draftContentFingerprint(existing) === draftContentFingerprint(next)) {
      return { ok: true, value: existing }
    }
    const expectedRevision = options.expectedLocalRevision ?? localRevisionOf(record)
    const expectedWriteId = options.expectedLocalWriteId ?? record.localWriteId
    if (currentVersion.localRevision !== expectedRevision || currentVersion.localWriteId !== expectedWriteId) {
      const copyKey = writeConflictCopy(storage, key, next, next.updatedAt)
      return {
        ok: false,
        error: {
          code: 'cross-tab-conflict',
          message: `另一标签页的本机草稿已变化，拒绝覆盖；当前文本${copyKey ? '已保留为冲突副本' : '仅保留在当前页面'}。`,
          ...(copyKey ? { preservedCopyKey: copyKey } : {}),
        },
      }
    }
    const sameRevision = existing.draft.editRevision === next.draft.editRevision
    const baseChanged = existing.draft.baseToken !== next.draft.baseToken
    const textChanged = existing.draft.text !== next.draft.text
    const pendingChanged = JSON.stringify(existing.pending ?? null) !== JSON.stringify(next.pending ?? null)
    const conflict = existing.draft.editRevision > next.draft.editRevision ||
      (sameRevision && (textChanged || (!options.allowBaseChange && baseChanged) || (!options.allowPendingChange && pendingChanged)))
    if (conflict) {
      const copyKey = writeConflictCopy(storage, key, next, next.updatedAt)
      return {
        ok: false,
        error: {
          code: 'cross-tab-conflict',
          message: `另一标签页的本机草稿已变化，拒绝覆盖；当前文本${copyKey ? '已保留为冲突副本' : '仅保留在当前页面'}。`,
          ...(copyKey ? { preservedCopyKey: copyKey } : {}),
        },
      }
    }
  } else {
    const expectedRevision = options.expectedLocalRevision ?? localRevisionOf(record)
    const expectedWriteId = options.expectedLocalWriteId ?? record.localWriteId
    if (expectedRevision !== 0 || expectedWriteId !== undefined) {
      const copyKey = writeConflictCopy(storage, key, next, next.updatedAt)
      return {
        ok: false,
        error: {
          code: 'cross-tab-conflict',
          message: `本机草稿的预期版本已不存在，拒绝静默重建；当前文本${copyKey ? '已保留为冲突副本' : '仅保留在当前页面'}。`,
          ...(copyKey ? { preservedCopyKey: copyKey } : {}),
        },
      }
    }
  }
  try {
    // localStorage has no atomic compare-and-swap. This synchronous get/set is
    // the strongest same-tab guard available; a cross-tab write can still race.
    const written: StoredNoteDerivedDraft = {
      ...next,
      localRevision: currentRevision + 1,
      localWriteId: createLocalWriteId(),
    }
    storage.set(key, JSON.stringify(written))
    return { ok: true, value: written }
  } catch {
    return { ok: false, error: { code: 'write-failed', message: '本机草稿写入失败，可能空间不足；当前页面内容仍保留。' } }
  }
}

export function editStoredDraft(
  record: StoredNoteDerivedDraft,
  text: string,
  options: { maxCharacters?: number; now?: Date; storage?: DraftStoragePort } = {},
): DraftStorageResult<StoredNoteDerivedDraft> {
  const maxCharacters = options.maxCharacters ?? MAX_LOCAL_DRAFT_CHARACTERS
  if (text.length > maxCharacters) {
    return {
      ok: false,
      error: { code: 'text-too-large', message: `本机草稿超过 ${maxCharacters.toLocaleString()} 字符，未接受本次修改；现有内容保持不变。` },
    }
  }
  const next: StoredNoteDerivedDraft = {
    ...record,
    draft: editTextDraft(record.draft, text),
    dirty: text !== record.draft.savedText,
    updatedAt: (options.now ?? new Date()).toISOString(),
  }
  const key = noteDerivedDraftStorageKey(record.binding)
  if (!key) return { ok: true, value: next }
  try {
    const raw = (options.storage ?? browserStorage).get(key)
    if (raw == null) return { ok: true, value: next }
    const parsed: unknown = JSON.parse(raw)
    if (!isStoredDraft(parsed) || !sameDraftScope(parsed.binding, record.binding)) return { ok: true, value: next }
    if (draftContentFingerprint(parsed) !== draftContentFingerprint(record)) return { ok: true, value: next }
    return { ok: true, value: { ...next, ...localVersionOf(parsed, raw) } }
  } catch {
    return { ok: true, value: next }
  }
}

function reserveIntent(
  record: StoredNoteDerivedDraft,
  kind: PendingMutationKind,
  proposedIntentId: string,
  now: Date,
  requestJson?: string,
): DraftStorageResult<{ readonly record: StoredNoteDerivedDraft; readonly intent: PendingMutationIntent; readonly reused: boolean }> {
  if (!proposedIntentId.trim()) {
    return { ok: false, error: { code: 'intent-conflict', message: '提交意图标识为空，未保存。' } }
  }
  if (record.pending) {
    if (record.pending.kind !== kind) {
      return { ok: false, error: { code: 'intent-conflict', message: '已有另一类提交等待核对；请先核对原提交，不能静默改派新标识。' } }
    }
    if (record.pending.intentId !== proposedIntentId) {
      return { ok: false, error: { code: 'intent-conflict', message: '已有提交等待核对；不能静默改派新的收费请求标识。' } }
    }
    if ((record.pending.requestJson ?? '') !== (requestJson ?? '')) {
      return { ok: false, error: { code: 'intent-conflict', message: '同一提交标识的请求内容已变化；请先核对原提交，不能覆盖原尝试。' } }
    }
    return { ok: true, value: { record, intent: record.pending, reused: true } }
  }
  const intent: PendingMutationIntent = {
    kind,
    intentId: proposedIntentId,
    reservedAt: now.toISOString(),
    ...(kind === 'manual-save' ? {
      submittedText: record.draft.text,
      submittedEditRevision: record.draft.editRevision,
      submittedBaseToken: record.draft.baseToken,
    } : {}),
    ...(requestJson ? { requestJson } : {}),
  }
  return { ok: true, value: { record: { ...record, pending: intent, updatedAt: now.toISOString() }, intent, reused: false } }
}

/**
 * The caller must persist the intent successfully before issuing the network
 * request. A repeated reservation returns the existing ID, never a new one.
 */
export function reserveAndPersistIntent(
  record: StoredNoteDerivedDraft,
  kind: PendingMutationKind,
  proposedIntentId: string,
  options: { maxCharacters?: number; now?: Date; storage?: DraftStoragePort } = {},
  requestJson?: string,
): DraftStorageResult<{ readonly record: StoredNoteDerivedDraft; readonly intent: PendingMutationIntent; readonly reused: boolean }> {
  const reserved = reserveIntent(record, kind, proposedIntentId, options.now ?? new Date(), requestJson)
  if (!reserved.ok) return reserved
  if (reserved.value.reused) return reserved
  const persisted = persistStoredDraft(reserved.value.record, { ...options, allowPendingChange: true })
  return persisted.ok ? { ok: true, value: { ...reserved.value, record: persisted.value } } : persisted
}

export function clearStoredIntent(
  record: StoredNoteDerivedDraft,
  intentId: string,
  now = new Date(),
  storage?: DraftStoragePort,
): DraftStorageResult<StoredNoteDerivedDraft> {
  if (!record.pending || record.pending.intentId !== intentId) {
    return { ok: true, value: record }
  }
  const { pending: _pending, ...rest } = record
  return persistStoredDraft({ ...rest, updatedAt: now.toISOString() }, { now, storage, allowPendingChange: true })
}

function writeConflictCopy(storage: DraftStoragePort, key: string, record: StoredNoteDerivedDraft, updatedAt: string): string | null {
  const suffix = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
  const copyKey = `${key}/conflict/${suffix}`
  try {
    storage.set(copyKey, JSON.stringify({ ...record, updatedAt }))
    return copyKey
  } catch {
    return null
  }
}
