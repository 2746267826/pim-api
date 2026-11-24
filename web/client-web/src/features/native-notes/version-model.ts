import { parseMaterialVersion, type NoteMaterialUpload, type NoteMaterialVersion } from './material-model'

export type NoteVersionKind = 'current' | 'conflict' | 'history' | 'uploading'

export interface NoteVersionView {
  upload: NoteMaterialUpload
  kind: NoteVersionKind
  kindLabel: string
  phaseLabel: string
  canOpen: boolean
  canPromote: boolean
}

export interface SafeVersionSummary {
  title: string
  revision: number | null
  excerpt: string
  markdownBlocks: number
  photoBlocks: number
  inkBlocks: number
  recordings: number
}

export function buildNoteVersionViews(versions: NoteMaterialUpload[]): NoteVersionView[] {
  return versions.map((upload) => {
    const kind = noteVersionKind(upload)
    return {
      upload,
      kind,
      kindLabel: noteVersionKindLabel(kind),
      phaseLabel: noteVersionPhaseLabel(upload.phase),
      canOpen: kind !== 'uploading',
      canPromote: kind !== 'uploading' && !upload.isCurrent,
    }
  })
}

export function noteVersionKind(upload: NoteMaterialUpload): NoteVersionKind {
  if (upload.phase === 'Uploading') return 'uploading'
  if (upload.hasConflict || upload.phase === 'Conflict') return 'conflict'
  return upload.isCurrent ? 'current' : 'history'
}

export function noteVersionKindLabel(kind: NoteVersionKind): string {
  return {
    current: '当前版',
    conflict: '冲突候选',
    history: '历史版',
    uploading: '未完成上传',
  }[kind]
}

export function noteVersionPhaseLabel(phase: string): string {
  return {
    Uploading: '上传中',
    Uploaded: '已完整保存',
    Conflict: '等待选择',
  }[phase] ?? '状态待核对'
}

export function versionShortId(versionId: string): string {
  return versionId.slice(0, 8)
}

export function safeVersionSummary(material: NoteMaterialVersion | undefined): SafeVersionSummary | null {
  if (!material) return null
  try {
    const parsed = parseMaterialVersion(material)
    const markdown = parsed.blocks
      .filter((block) => block.kind === 'markdown')
      .map((block) => block.text)
      .join('\n')
    return {
      title: parsed.title || '未命名笔记',
      revision: Number.isSafeInteger(parsed.revision) ? parsed.revision : null,
      excerpt: plainTextExcerpt(markdown),
      markdownBlocks: parsed.blocks.filter((block) => block.kind === 'markdown').length,
      photoBlocks: parsed.blocks.filter((block) => block.kind === 'photo').length,
      inkBlocks: parsed.blocks.filter((block) => block.kind === 'ink').length,
      recordings: parsed.recordings,
    }
  } catch {
    return null
  }
}

function plainTextExcerpt(markdown: string, limit = 180): string {
  const text = markdown
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/!\[[^\]]*]\([^)]*\)/g, ' ')
    .replace(/\[([^\]]+)]\([^)]*\)/g, '$1')
    .replace(/<[^>]+>/g, ' ')
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')
    .replace(/^\s{0,3}>\s?/gm, '')
    .replace(/[`*_~]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
  return text.length > limit ? `${text.slice(0, limit)}…` : text
}
