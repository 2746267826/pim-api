import type { NoteLibrarySource } from './api'

export interface NoteLibraryEntry {
  noteId: string
  title: string
  versionId: string
  isCurrent: boolean
  hasConflict: boolean
  recordings: number
  totalSamples: number
  createdAt: string
  versionCount: number
  conflictVersionIds: string[]
}

export function groupNoteLibrary(items: NoteLibrarySource[]): NoteLibraryEntry[] {
  const groups = new Map<string, NoteLibrarySource[]>()
  for (const item of items) {
    const current = groups.get(item.noteId)
    if (current) current.push(item)
    else groups.set(item.noteId, [item])
  }
  return [...groups.entries()]
    .map(([noteId, versions]) => {
      const ordered = [...versions].sort((left, right) =>
        Date.parse(right.createdAt) - Date.parse(left.createdAt))
      const selected = ordered.find((item) => item.isCurrent) ?? ordered[0]!
      return {
        noteId,
        title: selected.title || '未命名笔记',
        versionId: selected.versionId,
        isCurrent: selected.isCurrent,
        hasConflict: versions.some((item) => item.hasConflict),
        recordings: selected.recordings,
        totalSamples: selected.totalSamples,
        createdAt: selected.createdAt,
        versionCount: versions.length,
        conflictVersionIds: ordered.filter((item) => item.hasConflict || !item.isCurrent)
          .map((item) => item.versionId),
      } satisfies NoteLibraryEntry
    })
    .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))
}
