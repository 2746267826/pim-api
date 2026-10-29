import { describe, expect, it } from 'vitest'
import {
  beginGeneration,
  beginManualSave,
  classifyGenerationArrival,
  createTextDraft,
  editTextDraft,
  isVersionHead,
  isTextDraftDirty,
  resolveManualSave,
  selectTextVersion,
} from './draft-protection'

describe('local note-derived draft protection', () => {
  it('judges manual revision selection by head identity, not by conflict or success text', () => {
    expect(isVersionHead('version-a', 'version-a')).toBe(true)
    expect(isVersionHead('version-b', 'version-a')).toBe(false)
    expect(isVersionHead(null, 'version-a')).toBe(false)
  })

  it('tracks local edits without changing the saved baseline', () => {
    const clean = createTextDraft('课堂正文', 'head-1')
    const dirty = editTextDraft(clean, '课堂正文与补充结论')

    expect(isTextDraftDirty(clean)).toBe(false)
    expect(isTextDraftDirty(dirty)).toBe(true)
    expect(dirty.editRevision).toBe(1)
    expect(dirty.savedText).toBe('课堂正文')
    expect(editTextDraft(dirty, dirty.text)).toBe(dirty)
  })

  it('keeps a generation result as a candidate without changing the draft or head', () => {
    const state = createTextDraft('原始正文', 'head-1')
    const started = beginGeneration(state)
    const arrival = classifyGenerationArrival(started.state, started.snapshot)

    expect(arrival).toEqual({ status: 'candidate', reason: 'draft-clean', preserveDraft: false })
    expect(started.state.text).toBe('原始正文')
    expect(started.state.baseToken).toBe('head-1')
  })

  it('preserves edits made while generation was running', () => {
    const started = beginGeneration(createTextDraft('原始正文', 'head-1'))
    const edited = editTextDraft(started.state, '用户新写的人工修订')

    expect(classifyGenerationArrival(edited, started.snapshot)).toEqual({
      status: 'candidate',
      reason: 'draft-changed',
      preserveDraft: true,
    })
    expect(edited.text).toBe('用户新写的人工修订')
  })

  it('rejects out-of-order generation receipts from an older request', () => {
    const first = beginGeneration(createTextDraft('原始正文', 'head-1'))
    const second = beginGeneration(first.state)

    expect(classifyGenerationArrival(second.state, first.snapshot)).toEqual({
      status: 'superseded',
      reason: 'newer-request',
    })
  })

  it('rejects generation receipts after the selected source version changes', () => {
    const started = beginGeneration(createTextDraft('原始正文', 'head-1'))
    const moved = selectTextVersion(started.state, '历史版本正文', 'head-2')

    expect(classifyGenerationArrival(moved, started.snapshot)).toEqual({
      status: 'source-mismatch',
      reason: 'base-changed',
    })
    expect(moved.baseToken).toBe('head-2')
  })

  it('changes head only after an explicit version selection', () => {
    const state = editTextDraft(createTextDraft('原始正文', 'head-1'), '本地草稿')
    const selected = selectTextVersion(state, '生成候选正文', 'head-2')

    expect(selected.text).toBe('生成候选正文')
    expect(selected.savedText).toBe('生成候选正文')
    expect(selected.baseToken).toBe('head-2')
    expect(isTextDraftDirty(selected)).toBe(false)
    expect(selected.editRevision).toBe(state.editRevision + 1)
  })

  it('keeps edits typed while a manual save is in flight', () => {
    const started = beginManualSave(createTextDraft('第一次修订', 'head-1'))
    const edited = editTextDraft(started.state, '保存期间继续输入')
    const result = resolveManualSave(edited, started.snapshot, { kind: 'saved', headToken: 'head-2' })

    expect(result.status).toBe('edited-during-save')
    expect(result.state.text).toBe('保存期间继续输入')
    expect(result.state.savedText).toBe('第一次修订')
    expect(result.state.baseToken).toBe('head-1')
  })

  it('preserves the local draft when a manual save reports a retained conflict', () => {
    const started = beginManualSave(createTextDraft('人工修订', 'head-1'))
    const result = resolveManualSave(started.state, started.snapshot, {
      kind: 'conflict',
      candidatePreserved: true,
    })

    expect(result.status).toBe('conflict-preserved')
    expect(result.state).toBe(started.state)
    expect(result.state.text).toBe('人工修订')
    expect(result.state.baseToken).toBe('head-1')
  })

  it('ignores a stale save receipt after a newer save begins', () => {
    const first = beginManualSave(createTextDraft('修订 A', 'head-1'))
    const second = beginManualSave(editTextDraft(first.state, '修订 B'))
    const result = resolveManualSave(second.state, first.snapshot, { kind: 'saved', headToken: 'head-a' })

    expect(result.status).toBe('superseded')
    expect(result.state.text).toBe('修订 B')
    expect(result.state.baseToken).toBe('head-1')
  })
})
