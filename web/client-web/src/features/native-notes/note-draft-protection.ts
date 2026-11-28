import type { UnsavedDraftDescriptor } from '@/lib/unsaved-drafts'
import type { MaterialDraftBlock } from './material-model'

export interface NoteDraftRegistration {
  readonly active: boolean
  readonly descriptor: UnsavedDraftDescriptor
}

export interface NoteDraftProtectionInput {
  readonly noteId: string
  readonly title: string
  readonly baseVersionId: string | null
  readonly textDirty: boolean
  readonly dirty: boolean
  readonly blocks: readonly MaterialDraftBlock[]
  readonly inkPending: boolean
  readonly inkEditorOpen: boolean
}

export function noteDraftRegistrations(input: NoteDraftProtectionInput): {
  text: NoteDraftRegistration
  photos: NoteDraftRegistration
  ink: NoteDraftRegistration
  material: NoteDraftRegistration
} {
  const unsavedPhotoCount = input.blocks.filter((block) => block.kind === 'photo' && !('sha256' in block)).length
  const unsavedInkCount = input.blocks.filter((block) => block.kind === 'ink' && !('sha256' in block)).length
  const saveHref = input.baseVersionId
    ? `/native-notes?note=${encodeURIComponent(input.noteId)}`
    : '/native-notes'
  const title = input.title.trim() || '未命名笔记'

  return {
    text: {
      active: input.textDirty,
      descriptor: {
        id: `note-text:${input.noteId}`,
        kind: 'note',
        title: `笔记「${title}」`,
        detail: '标题或正文修改仍在当前页面内存中，尚未保存到服务器。',
        saveHint: '返回笔记页后点击“保存笔记”。',
        saveHref,
      },
    },
    photos: {
      active: input.dirty && unsavedPhotoCount > 0,
      descriptor: {
        id: `note-photos:${input.noteId}`,
        kind: 'image',
        title: `笔记「${title}」中的照片草稿`,
        detail: `${unsavedPhotoCount} 张照片仍在当前页面内存中，尚未保存到服务器。`,
        saveHint: '返回笔记页后点击“保存笔记”。',
        saveHref,
      },
    },
    ink: {
      active: input.inkEditorOpen || input.inkPending || (input.dirty && unsavedInkCount > 0),
      descriptor: {
        id: `note-ink:${input.noteId}`,
        kind: 'handwriting',
        title: `笔记「${title}」中的手写内容`,
        detail: '手写字迹或笔迹预览仍在当前页面内存中，尚未保存到服务器。',
        saveHint: '返回笔记页后点击“保存笔记”。',
        saveHref,
      },
    },
    material: {
      active: input.dirty && !input.textDirty && unsavedPhotoCount === 0 && unsavedInkCount === 0 && !input.inkEditorOpen,
      descriptor: {
        id: `note-material:${input.noteId}`,
        kind: 'note',
        title: `笔记「${title}」的未确认材料`,
        detail: '当前材料仍保留在页面中，尚未收到完整保存回执。',
        saveHint: '返回笔记页核对并重试保存。',
        saveHref,
      },
    },
  }
}
