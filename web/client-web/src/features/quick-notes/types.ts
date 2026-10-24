export type QuickNoteStatus = 'inbox' | 'processed' | 'archived'

export interface QuickNoteAttachment {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  downloadUrl: string
  previewUrl: string | null
  createdAt: string
}

export interface QuickNoteListItem {
  id: string
  contentPreview: string
  status: QuickNoteStatus
  source: string
  attachmentCount: number
  attachments: QuickNoteAttachment[] | null
  createdAt: string
  updatedAt: string
  archivedAt: string | null
}

export interface QuickNoteDetail extends QuickNoteListItem {
  contentMarkdown: string
  metadataJson: string
}

/** 分类从内容前缀自动提取（02 §quick-notes：灵感/学业/开发/运维/生活） */
const CATEGORY_PREFIXES: { prefix: string; name: string }[] = [
  { prefix: '灵感', name: '灵感' },
  { prefix: '学业', name: '学业' },
  { prefix: '开发', name: '开发' },
  { prefix: '运维', name: '运维' },
  { prefix: '生活', name: '生活' },
]

export function noteCategory(content: string): string | null {
  for (const { prefix, name } of CATEGORY_PREFIXES) {
    if (content.includes(`#${prefix}`)) return name
  }
  return null
}

export const NOTE_CATEGORIES = ['全部', ...CATEGORY_PREFIXES.map((c) => c.name)] as const
