// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  MAX_LOCAL_DRAFT_CHARACTERS,
  canAdoptSelectedVersion,
  canonicalServerBinding,
  clearStoredIntent,
  createStoredDraft,
  editStoredDraft,
  loadStoredDraft,
  noteDerivedDraftStorageKey,
  persistStoredDraft,
  reserveAndPersistIntent,
  type NoteDerivedDraftScope,
  type StoredNoteDerivedDraft,
} from './draft-storage'

const scope: NoteDerivedDraftScope = {
  ownerId: '11111111-1111-4111-8111-111111111111',
  serverBinding: 'https://pim-a.example',
  artifactId: '22222222-2222-4222-8222-222222222222',
  sourceVersionId: '33333333-3333-4333-8333-333333333333',
  kind: 'CompleteRecord',
}

beforeEach(() => localStorage.clear())
afterEach(() => {
  delete (globalThis as { __PIM_HOST_CHANNEL__?: unknown }).__PIM_HOST_CHANNEL__
  vi.restoreAllMocks()
})

describe('note-derived local draft storage', () => {
  it('restores body, dirty state and pending intent after a refresh', () => {
    const base = createStoredDraft(scope, '已保存正文', 'head-1')
    const edited = editStoredDraft(base, '尚未保存的人工正文')
    expect(edited.ok).toBe(true)
    if (!edited.ok) return
    const intent = reserveAndPersistIntent(edited.value, 'manual-save', '44444444-4444-4444-8444-444444444444')
    expect(intent.ok).toBe(true)

    const loaded = loadStoredDraft(scope)
    expect(loaded.ok).toBe(true)
    if (!loaded.ok) return
    expect(loaded.value?.draft.text).toBe('尚未保存的人工正文')
    expect(loaded.value?.dirty).toBe(true)
    expect(loaded.value?.pending).toEqual(expect.objectContaining({
      kind: 'manual-save',
      intentId: '44444444-4444-4444-8444-444444444444',
      submittedText: '尚未保存的人工正文',
      submittedBaseToken: 'head-1',
    }))
  })

  it('isolates drafts by owner, server, artifact, source version and kind', () => {
    const record = createStoredDraft(scope, 'A 的私有草稿')
    expect(persistStoredDraft(record).ok).toBe(true)

    expect(loadStoredDraft({ ...scope, ownerId: 'another-owner' })).toEqual({ ok: true, value: null })
    expect(loadStoredDraft({ ...scope, serverBinding: 'https://pim-b.example' })).toEqual({ ok: true, value: null })
    expect(loadStoredDraft({ ...scope, artifactId: 'another-artifact' })).toEqual({ ok: true, value: null })
    expect(loadStoredDraft({ ...scope, sourceVersionId: 'another-version' })).toEqual({ ok: true, value: null })
    expect(loadStoredDraft({ ...scope, kind: 'IndependentAnalysis' })).toEqual({ ok: true, value: null })
  })

  it('keeps configured base paths in the canonical server binding', () => {
    localStorage.setItem('pim.apiBase', 'https://pim-a.example/custom-base')
    expect(canonicalServerBinding('http://document.example')).toBe('https://pim-a.example/custom-base')
  })

  it('uses actual apiUrl host rules and strips credentials, query and fragment', () => {
    localStorage.setItem('pim.apiBase', 'https://draft-user:draft-pass@pim-a.example/custom-base?x=1#fragment')
    expect(canonicalServerBinding('http://document.example/app')).toBe('https://pim-a.example/custom-base')

    localStorage.setItem('pim.apiBase', 'ftp://pim-a.example/custom-base')
    expect(canonicalServerBinding('http://document.example')).toBe('')
  })

  it('uses the document origin, not configured apiBase, in host channel', () => {
    ;(globalThis as { __PIM_HOST_CHANNEL__?: unknown }).__PIM_HOST_CHANNEL__ = {
      v: 1,
      product: 'pim-host-windows',
    }
    localStorage.setItem('pim.apiBase', 'https://ignored.example/custom-base')
    expect(canonicalServerBinding('https://document.example')).toBe('https://document.example')
  })

  it('only adopts a selected server version when the local draft is clean, unblocked and based on it', () => {
    const clean = createStoredDraft(scope, '服务端原文', 'version-a')
    const edited = editStoredDraft(clean, '本机修改')
    expect(edited.ok).toBe(true)
    if (!edited.ok) return
    expect(canAdoptSelectedVersion(clean, 'version-a')).toBe(true)
    expect(canAdoptSelectedVersion(edited.value, 'version-a')).toBe(false)
    expect(canAdoptSelectedVersion({ ...clean, pending: { kind: 'manual-save', intentId: 'id', reservedAt: new Date().toISOString() } }, 'version-a')).toBe(false)
    expect(canAdoptSelectedVersion(clean, 'version-b')).toBe(false)
  })

  it('does not read any draft without a bound owner and canonical server', () => {
    const record = createStoredDraft(scope, '不能串给未绑定会话')
    expect(persistStoredDraft(record).ok).toBe(true)

    expect(loadStoredDraft({ ...scope, ownerId: '' })).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'unbound' }),
    }))
    expect(loadStoredDraft({ ...scope, serverBinding: '' })).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'unbound' }),
    }))
  })

  it('stores only the draft domain and never copies access tokens', () => {
    localStorage.setItem('pim.accessToken', 'secret-token-value')
    const persisted = persistStoredDraft(createStoredDraft(scope, '正文不含凭据'))
    expect(persisted.ok).toBe(true)

    const key = noteDerivedDraftStorageKey(scope)
    const raw = key ? localStorage.getItem(key) : null
    expect(raw).toContain('正文不含凭据')
    expect(raw).not.toContain('secret-token-value')
    expect(raw).not.toContain('accessToken')
    expect(raw).not.toContain('refreshToken')
  })

  it('reports corrupted storage instead of silently starting empty', () => {
    const key = noteDerivedDraftStorageKey(scope)
    expect(key).toBeTruthy()
    localStorage.setItem(key!, '{"schemaVersion":1,"draft":')

    expect(loadStoredDraft(scope)).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'corrupt' }),
    }))
  })

  it('refuses to overwrite corrupted raw storage and preserves the new text in a recovery copy', () => {
    const key = noteDerivedDraftStorageKey(scope)
    localStorage.setItem(key!, '{"broken":')
    expect(loadStoredDraft(scope)).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'corrupt' }),
    }))

    const recovered = editStoredDraft(createStoredDraft(scope), '仍可恢复的本机全文')
    expect(recovered.ok).toBe(true)
    if (!recovered.ok) return
    const persisted = persistStoredDraft(recovered.value)
    expect(persisted).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'corrupt' }),
    }))
    expect(localStorage.getItem(key!)).toBe('{"broken":')
    const copyKeys = Object.keys(localStorage).filter((item) => item.startsWith(`${key}/conflict/`))
    expect(copyKeys.length).toBe(1)
    expect(localStorage.getItem(copyKeys[0]!)).toContain('仍可恢复的本机全文')
  })

  it('preserves a cross-tab draft as a conflict copy instead of overwriting it', () => {
    const first = editStoredDraft(createStoredDraft(scope), '标签页 A 的人工全文')
    expect(first.ok).toBe(true)
    if (!first.ok) return
    expect(persistStoredDraft(first.value).ok).toBe(true)
    const originalRaw = localStorage.getItem(noteDerivedDraftStorageKey(scope)!)

    const second = editStoredDraft(createStoredDraft(scope), '标签页 B 的另一份人工全文')
    expect(second.ok).toBe(true)
    if (!second.ok) return
    const persisted = persistStoredDraft(second.value)
    expect(persisted).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'cross-tab-conflict' }),
    }))
    expect(localStorage.getItem(noteDerivedDraftStorageKey(scope)!)).toBe(originalRaw)
    const copyKeys = Object.keys(localStorage).filter((item) => item.startsWith(`${noteDerivedDraftStorageKey(scope)}/conflict/`))
    expect(copyKeys.length).toBe(1)
    expect(localStorage.getItem(copyKeys[0]!)).toContain('标签页 B 的另一份人工全文')
  })

  it('keeps both full drafts and pending request bodies when two tabs save from the same base', () => {
    const seeded = persistStoredDraft(createStoredDraft(scope, 'base'))
    expect(seeded.ok).toBe(true)

    const loadedForA = loadStoredDraft(scope)
    const loadedForB = loadStoredDraft(scope)
    expect(loadedForA.ok && loadedForB.ok).toBe(true)
    if (!loadedForA.ok || !loadedForB.ok || !loadedForA.value || !loadedForB.value) return

    const editA = editStoredDraft(loadedForA.value, 'tab A full text')
    const editB = editStoredDraft(loadedForB.value, 'tab B full text')
    expect(editA.ok && editB.ok).toBe(true)
    if (!editA.ok || !editB.ok) return

    const bodyA = JSON.stringify({ taskId: 'intent-a', content: 'tab A full text' })
    const bodyB = JSON.stringify({ taskId: 'intent-b', content: 'tab B full text' })
    const savedA = reserveAndPersistIntent(editA.value, 'create', 'intent-a', {}, bodyA)
    expect(savedA.ok).toBe(true)
    const savedB = reserveAndPersistIntent(editB.value, 'create', 'intent-b', {}, bodyB)
    expect(savedB).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'cross-tab-conflict', preservedCopyKey: expect.any(String) }),
    }))
    if (savedB.ok) return

    const current = loadStoredDraft(scope)
    expect(current.ok && current.value?.draft.text).toBe('tab A full text')
    expect(current.ok && current.value?.pending?.intentId).toBe('intent-a')
    expect(current.ok && current.value?.pending?.requestJson).toBe(bodyA)

    const copyRaw = localStorage.getItem(savedB.error.preservedCopyKey!)
    expect(copyRaw).toBeTruthy()
    const copy = JSON.parse(copyRaw!) as StoredNoteDerivedDraft
    expect(copy.draft.text).toBe('tab B full text')
    expect(copy.pending?.intentId).toBe('intent-b')
    expect(copy.pending?.requestJson).toBe(bodyB)
    expect(localStorage.getItem(noteDerivedDraftStorageKey(scope)!)).toContain('tab A full text')
  })

  it('does not overwrite a record whose stored binding does not match its key', () => {
    const key = noteDerivedDraftStorageKey(scope)!
    const stored = persistStoredDraft(createStoredDraft(scope, 'protected current'))
    expect(stored.ok).toBe(true)
    if (!stored.ok) return
    const wrongBinding: StoredNoteDerivedDraft = {
      ...stored.value,
      binding: { ...scope, ownerId: 'different-owner' },
    }
    const raw = JSON.stringify(wrongBinding)
    localStorage.setItem(key, raw)

    const attempted = editStoredDraft(createStoredDraft(scope), 'must not replace')
    expect(attempted.ok).toBe(true)
    if (!attempted.ok) return
    const persisted = persistStoredDraft(attempted.value)
    expect(persisted).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'scope-mismatch' }),
    }))
    expect(localStorage.getItem(key)).toBe(raw)
  })

  it('makes the local character cap explicit and keeps the previous stored body', () => {
    const base = createStoredDraft(scope, '原文')
    expect(persistStoredDraft(base).ok).toBe(true)

    const rejected = editStoredDraft(base, '超出上限', { maxCharacters: 3 })
    expect(rejected).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'text-too-large' }),
    }))
    const loaded = loadStoredDraft(scope)
    expect(loaded.ok && loaded.value?.draft.text).toBe('原文')
    expect(MAX_LOCAL_DRAFT_CHARACTERS).toBe(1_000_000)
  })

  it('surfaces storage write failure without pretending the intent was persisted', () => {
    const record = createStoredDraft(scope, '正文')
    const failingStorage = {
      get: () => null,
      set: () => { throw new DOMException('quota', 'QuotaExceededError') },
    }

    expect(reserveAndPersistIntent(record, 'create', 'new-task-id', { storage: failingStorage })).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'write-failed' }),
    }))
  })

  it('supports consecutive edits and reload even when the caller keeps the pre-persist record', () => {
    let record = createStoredDraft(scope, '')
    for (const text of ['a', 'ab', 'abc']) {
      const edited = editStoredDraft(record, text)
      expect(edited.ok).toBe(true)
      if (!edited.ok) return
      const persisted = persistStoredDraft(edited.value)
      expect(persisted.ok).toBe(true)
      // ArtifactEditor commits the edited record; persistence must still advance CAS.
      record = edited.value
    }

    const loaded = loadStoredDraft(scope)
    expect(loaded.ok && loaded.value?.draft.text).toBe('abc')
    expect(loaded.ok && loaded.value?.localRevision).toBe(3)
  })

  it('loads and upgrades legacy schema-1 drafts without losing their content', () => {
    const legacy = createStoredDraft(scope, 'legacy body', 'head-1')
    const legacyWithoutCas = {
      schemaVersion: legacy.schemaVersion,
      binding: legacy.binding,
      draft: legacy.draft,
      dirty: legacy.dirty,
      updatedAt: legacy.updatedAt,
    }
    localStorage.setItem(noteDerivedDraftStorageKey(scope)!, JSON.stringify(legacyWithoutCas))

    const loaded = loadStoredDraft(scope)
    expect(loaded.ok && loaded.value?.draft.text).toBe('legacy body')
    expect(loaded.ok && loaded.value?.localRevision).toBe(0)
    expect(loaded.ok && typeof loaded.value?.localWriteId).toBe('string')
    if (!loaded.ok || !loaded.value) return

    const edited = editStoredDraft(loaded.value, 'legacy body updated')
    expect(edited.ok).toBe(true)
    if (!edited.ok) return
    expect(persistStoredDraft(edited.value).ok).toBe(true)
    const reloaded = loadStoredDraft(scope)
    expect(reloaded.ok && reloaded.value?.draft.text).toBe('legacy body updated')
  })

  it('replays only the same intent id and payload without replacing it', () => {
    const base = createStoredDraft(scope, '待创建任务')
    const body = JSON.stringify({ taskId: 'task-id-1', versionId: 'version-1' })
    const first = reserveAndPersistIntent(base, 'create', 'task-id-1', {}, body)
    expect(first.ok).toBe(true)
    if (!first.ok) return
    const raw = localStorage.getItem(noteDerivedDraftStorageKey(scope)!)

    const replay = reserveAndPersistIntent(first.value.record, 'create', 'task-id-1', {}, body)
    expect(replay).toEqual(expect.objectContaining({
      ok: true,
      value: expect.objectContaining({ reused: true, intent: expect.objectContaining({ intentId: 'task-id-1' }) }),
    }))
    expect(localStorage.getItem(noteDerivedDraftStorageKey(scope)!)).toBe(raw)

    expect(reserveAndPersistIntent(first.value.record, 'create', 'task-id-1', {}, '{"taskId":"task-id-1","versionId":"changed"}')).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'intent-conflict' }),
    }))
    expect(reserveAndPersistIntent(first.value.record, 'create', 'task-id-2', {}, body)).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'intent-conflict' }),
    }))
    const loaded = loadStoredDraft(scope)
    expect(loaded.ok && loaded.value?.pending?.intentId).toBe('task-id-1')
    expect(loaded.ok && loaded.value?.pending?.requestJson).toBe(body)
  })

  it('does not overwrite an unresolved intent with another mutation kind', () => {
    const base = createStoredDraft(scope, '人工正文')
    const first = reserveAndPersistIntent(base, 'manual-save', 'manual-id-1')
    expect(first.ok).toBe(true)
    if (!first.ok) return

    expect(reserveAndPersistIntent(first.value.record, 'retry', 'retry-id-1')).toEqual(expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'intent-conflict' }),
    }))
  })

  it('clears only the matching confirmed intent', () => {
    const base = createStoredDraft(scope, '人工正文')
    const reserved = reserveAndPersistIntent(base, 'retry', 'retry-id-1')
    expect(reserved.ok).toBe(true)
    if (!reserved.ok) return

    const stale = clearStoredIntent(reserved.value.record, 'another-id')
    expect(stale.ok && stale.value.pending?.intentId).toBe('retry-id-1')
    const cleared = clearStoredIntent(reserved.value.record, 'retry-id-1')
    expect(cleared.ok && cleared.value.pending).toBeUndefined()
  })
})
