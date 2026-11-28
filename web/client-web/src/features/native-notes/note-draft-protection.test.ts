import { describe, expect, it } from 'vitest'
import { noteDraftRegistrations, type NoteDraftProtectionInput } from './note-draft-protection'
import type { MaterialDraftBlock } from './material-model'

const markdown: MaterialDraftBlock = { kind: 'markdown', id: 'text-1', text: '正文' }
const newPhoto: MaterialDraftBlock = {
  kind: 'photo',
  id: 'photo-new',
  assetId: 'asset-new',
  mimeType: 'image/png',
  bytes: new Uint8Array([1]),
}
const savedPhoto: MaterialDraftBlock = {
  kind: 'photo',
  id: 'photo-saved',
  assetId: 'asset-saved',
  mimeType: 'image/png',
  sha256: 'a'.repeat(64),
  fileIndex: 1,
  bytes: new Uint8Array([2]),
}
const newInk: MaterialDraftBlock = {
  kind: 'ink',
  id: 'ink-new',
  ink: { strokes: [] },
  bytes: new Uint8Array([3]),
}
const savedInk: MaterialDraftBlock = {
  kind: 'ink',
  id: 'ink-saved',
  ink: { strokes: [] },
  sha256: 'b'.repeat(64),
  fileIndex: 2,
  bytes: new Uint8Array([4]),
}

const base: NoteDraftProtectionInput = {
  noteId: 'note-1',
  title: '课堂笔记',
  baseVersionId: 'version-1',
  textDirty: false,
  dirty: false,
  blocks: [markdown],
  inkPending: false,
  inkEditorOpen: false,
}

describe('笔记编辑器未保存内容保护', () => {
  it('正文/标题修改登记为未保存笔记，并提供返回保存入口', () => {
    const drafts = noteDraftRegistrations({ ...base, textDirty: true, dirty: true })

    expect(drafts.text.active).toBe(true)
    expect(drafts.text.descriptor).toMatchObject({
      id: 'note-text:note-1',
      kind: 'note',
      title: '笔记「课堂笔记」',
      saveHref: '/native-notes?note=note-1',
    })
    expect(drafts.photos.active).toBe(false)
    expect(drafts.ink.active).toBe(false)
  })

  it('新增照片与手写材料在 dirty 时登记，已有 sha256 的材料不登记', () => {
    const newDrafts = noteDraftRegistrations({ ...base, dirty: true, blocks: [markdown, newPhoto, newInk] })
    expect(newDrafts.photos.active).toBe(true)
    expect(newDrafts.ink.active).toBe(true)
    expect(newDrafts.material.active).toBe(false)

    const savedDrafts = noteDraftRegistrations({ ...base, dirty: true, blocks: [markdown, savedPhoto, savedInk] })
    expect(savedDrafts.photos.active).toBe(false)
    expect(savedDrafts.ink.active).toBe(false)
    expect(savedDrafts.material.active).toBe(true)
  })

  it('保存成功后 dirty 复位，不因页面仍持有旧草稿块而继续登记', () => {
    const afterSave = noteDraftRegistrations({
      ...base,
      dirty: false,
      textDirty: false,
      blocks: [markdown, newPhoto, newInk],
    })

    expect(afterSave.text.active).toBe(false)
    expect(afterSave.photos.active).toBe(false)
    expect(afterSave.ink.active).toBe(false)
    expect(afterSave.material.active).toBe(false)
  })

  it('手写编辑器打开或待核对时始终保护，不依赖 dirty', () => {
    expect(noteDraftRegistrations({ ...base, inkEditorOpen: true }).ink.active).toBe(true)
    expect(noteDraftRegistrations({ ...base, inkPending: true }).ink.active).toBe(true)
  })

  it('新建笔记没有版本 ID 时回到笔记列表，标题为空时使用未命名笔记', () => {
    const drafts = noteDraftRegistrations({
      ...base,
      baseVersionId: null,
      title: '   ',
      textDirty: true,
      dirty: true,
    })

    expect(drafts.text.descriptor.title).toBe('笔记「未命名笔记」')
    expect(drafts.text.descriptor.saveHref).toBe('/native-notes')
  })
})
