import type { NoteLibrarySource } from './api'

export interface NoteLibraryEntry {
  noteId: string
  title: string
  currentVersionId: string
  hasConflict: boolean
}

export function groupNoteLibrary(items: NoteLibrarySource[]): NoteLibraryEntry[] {
  return items.map((item) => ({
    noteId: item.noteId,
    title: item.title || '未命名笔记',
    currentVersionId: item.currentVersionId,
    hasConflict: item.hasConflict,
  }) satisfies NoteLibraryEntry)
}
