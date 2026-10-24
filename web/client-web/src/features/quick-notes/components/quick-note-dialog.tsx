import { useEffect, useRef, useState } from 'react'
import { Archive, CheckCircle2, GripHorizontal, Paperclip, RotateCcw, Save, Trash2, X } from 'lucide-react'
import { fetchBlob } from '@/api/client'
import { QuickNoteEditor } from './quick-note-editor'
import { quickNotesApi } from '../api'
import { useQuickNoteMutations } from '../queries'
import type { QuickNoteAttachment, QuickNoteDetail } from '../types'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import { cn } from '@/lib/utils'
import { ConfirmDialog, Button, Select } from '@/components/ui'

/**
 * 弹窗目标：
 * - edit：已有记录（PUT 保存）
 * - draft：尚未在后端创建的新闪念，保存时才 POST —— 避免「打开即建记录」导致
 *   用户什么都没写就关掉时留下空记录（此前因此堆积了多条空闪念）。
 */
export type QuickNoteDialogTarget =
  | { mode: 'edit'; note: QuickNoteDetail }
  | { mode: 'draft'; initialMarkdown: string; source: string }

export interface QuickNoteDialogProps {
  target: QuickNoteDialogTarget | null
  onClose: () => void
}

const CATEGORIES = [
  { value: '', label: '无分类' },
  { value: '灵感', label: '灵感' },
  { value: '学业', label: '学业' },
  { value: '开发', label: '开发' },
  { value: '运维', label: '运维' },
  { value: '生活', label: '生活' },
]

/** 闪念编辑弹窗（规格：可拖拽、位置记忆 localStorage、TipTap、附件、状态操作） */
export function QuickNoteDialog({ target, onClose }: QuickNoteDialogProps) {
  const mutations = useQuickNoteMutations()
  const isDraft = target?.mode === 'draft'
  const note = target?.mode === 'edit' ? target.note : null
  const [markdown, setMarkdown] = useState('')
  const [category, setCategory] = useState('')
  const [attachments, setAttachments] = useState<QuickNoteAttachment[]>([])
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [saving, setSaving] = useState(false)
  const dragRef = useRef<HTMLDivElement>(null)

  /* 弹窗位置记忆 */
  /*
   * 弹窗位置记忆：读取时做视口收敛，避免历史坐标（旧版本按 left 语义写入，
   * 或窗口尺寸变化后）把弹窗甩到屏幕外或看起来偏心。
   */
  const [pos, setPos] = useState(() => {
    const saved = getJSON<{ x: number; y: number } | null>(STORAGE_KEYS.quickNoteDialogPosition, null)
    if (!saved) return null
    if (typeof window === 'undefined') return saved
    const w = 560
    const maxX = Math.max(0, window.innerWidth - w)
    if (!Number.isFinite(saved.x) || !Number.isFinite(saved.y)) return null
    return {
      x: Math.min(Math.max(0, saved.x), maxX),
      y: Math.min(Math.max(0, saved.y), Math.max(0, window.innerHeight - 120)),
    }
  })

  /* 载入内容：编辑态取记录；草稿态取预填文本（无后端记录） */
  useEffect(() => {
    if (!target) return
    if (target.mode === 'edit') {
      setMarkdown(target.note.contentMarkdown)
      setAttachments(target.note.attachments ?? [])
    } else {
      setMarkdown(target.initialMarkdown)
      setAttachments([])
    }
    setCategory('')
    setConfirmDelete(false)
  }, [target])

  if (!target) return null

  /*
   * 保存：
   * - 编辑态 → PUT
   * - 草稿态 → POST（仅当有内容或附件时才创建；空内容直接关闭，不产生记录）
   */
  async function save() {
    if (!target) return
    const attachmentIds = attachments.map((a) => a.id)
    const hasContent = markdown.trim().length > 0 || attachmentIds.length > 0
    if (target.mode === 'draft' && !hasContent) {
      onClose()
      return
    }
    setSaving(true)
    try {
      await mutations.save.mutateAsync({
        id: target.mode === 'edit' ? target.note.id : undefined,
        contentMarkdown: markdown,
        attachmentIds,
        ...(target.mode === 'draft' ? { source: target.source } : {}),
      })
      onClose()
    } finally {
      setSaving(false)
    }
  }

  /** 关闭：草稿态若有内容则自动保存，无内容直接丢弃（不建记录） */
  async function close() {
    if (target?.mode === 'draft' && (markdown.trim().length > 0 || attachments.length > 0)) {
      await save()
      return
    }
    onClose()
  }

  async function upload(files: FileList | null) {
    if (!files?.length) return
    for (const file of files) {
      const att = await mutations.upload.mutateAsync(file)
      setAttachments((prev) => [...prev, att])
    }
  }

  function startDrag(e: React.MouseEvent) {
    const startX = e.clientX - (pos?.x ?? 0)
    const startY = e.clientY - (pos?.y ?? 0)
    const onMove = (ev: MouseEvent) => {
      const next = { x: ev.clientX - startX, y: ev.clientY - startY }
      setPos(next)
      setJSON(STORAGE_KEYS.quickNoteDialogPosition, next)
    }
    const onUp = () => {
      window.removeEventListener('mousemove', onMove)
      window.removeEventListener('mouseup', onUp)
    }
    window.addEventListener('mousemove', onMove)
    window.addEventListener('mouseup', onUp)
  }

  const statusActions: { label: string; icon: typeof Save; run: () => Promise<unknown>; tone?: 'danger' }[] =
    note == null
      ? []
      : note.status === 'archived'
        ? [{ label: '恢复到收集箱', icon: RotateCcw, run: () => mutations.restore.mutateAsync(note.id) }]
        : [
            { label: '标记已处理', icon: CheckCircle2, run: () => mutations.process.mutateAsync(note.id) },
            { label: '归档', icon: Archive, run: () => mutations.archive.mutateAsync(note.id) },
          ]

  return (
    <>
      <div className="fixed inset-0 z-40 bg-text-1/10" onClick={onClose} />
      <div
        /*
         * 居中方式：无记忆位置时用 left-1/2 + -translate-x-1/2（Tailwind v4 编译到 translate 属性），
         * 而不是内联 transform —— 内联 transform 会被 pim-pop-in 动画的 transform 覆盖，
         * 入场瞬间先落在偏右位置，动画结束才回到中央。
         */
        className={cn(
          'fixed z-50 flex max-h-[80dvh] w-[560px] max-w-[calc(100dvw-24px)] flex-col rounded-card border border-border bg-bg shadow-modal outline-none animate-[pim-pop-in_200ms_cubic-bezier(.32,.72,.24,1)]',
          !pos && 'top-[12vh] left-1/2 -translate-x-1/2',
        )}
        style={pos ? { top: pos.y, left: pos.x } : undefined}
      >
        {/* 可拖拽标题栏 */}
        <div ref={dragRef} onMouseDown={startDrag} className="flex cursor-grab items-center gap-2 border-b border-divider px-3 py-2 active:cursor-grabbing">
          <GripHorizontal className="size-4 text-text-4" aria-hidden />
          <span className="text-[13px] font-semibold text-text-1">{isDraft ? '新建闪念' : '编辑闪念'}</span>
          <span className="rounded-badge bg-surface-2 px-1.5 py-0.5 text-[11px] text-text-3">
            {isDraft ? '未保存' : (note?.status ?? '')}
          </span>
          <button type="button" aria-label="关闭" onClick={() => void close()} className="ml-auto rounded-ctl p-1 text-text-3 hover:bg-surface hover:text-text-1 outline-none">
            <X className="size-4" aria-hidden />
          </button>
        </div>

        <div className="flex-1 space-y-3 overflow-y-auto p-4">
          <div className="flex items-center gap-2">
            <span className="text-[13px] text-text-3">分类（从内容 #前缀 自动提取）</span>
            <Select
              className="ml-auto h-8 w-28"
              value={category}
              onValueChange={(v) => {
                const next = v ? `#${v} ` : ''
                setCategory(v)
                if (v && !markdown.includes(`#${v}`)) setMarkdown(`${next}${markdown}`)
              }}
              options={CATEGORIES}
              ariaLabel="分类"
            />
          </div>

          <div className="rounded-ctl border border-border">
            <QuickNoteEditor markdown={markdown} onChange={setMarkdown} className="p-3" />
          </div>

          {/* 附件 */}
          <div>
            <div className="flex items-center gap-2">
              <span className="text-[13px] text-text-3">附件（{attachments.length}）</span>
              <label className="ml-auto inline-flex cursor-pointer items-center gap-1 text-xs text-primary hover:underline outline-none">
                <Paperclip className="size-3.5" aria-hidden /> 上传附件
                <input type="file" multiple className="hidden" onChange={(e) => void upload(e.target.files)} />
              </label>
            </div>
            <div className="mt-1.5 flex flex-wrap gap-1.5">
              {attachments.map((a) => (
                <span key={a.id} className="inline-flex items-center gap-1 rounded-full border border-border bg-surface px-2.5 py-1 text-xs text-text-2">
                  <button
                    type="button"
                    className="max-w-48 truncate outline-none hover:text-primary"
                    onClick={() => void fetchBlob(a.downloadUrl).then((b) => {
                      const url = URL.createObjectURL(b)
                      window.open(url)
                    })}
                  >
                    {a.fileName} · {(a.sizeBytes / 1024).toFixed(0)}KB
                  </button>
                  <button
                    type="button"
                    aria-label={`删除附件 ${a.fileName}`}
                    className="text-text-4 hover:text-crit outline-none"
                    onClick={async () => {
                      await quickNotesApi.removeAttachment(a.id).catch(() => {})
                      setAttachments((prev) => prev.filter((x) => x.id !== a.id))
                    }}
                  >
                    <X className="size-3" aria-hidden />
                  </button>
                </span>
              ))}
              {attachments.length === 0 && <span className="text-xs text-text-4">未添加附件（支持编辑器内粘贴图片）</span>}
            </div>
          </div>
        </div>

        {/* 底部操作（草稿态无删除/状态操作：记录尚未创建） */}
        <div className="flex shrink-0 items-center gap-2 border-t border-divider px-4 py-3">
          {!isDraft && (
            <Button
              variant="danger-soft"
              size="sm"
              onClick={() => setConfirmDelete(true)}
              title="删除闪念"
            >
              <Trash2 className="size-4" aria-hidden />
            </Button>
          )}
          <div className="ml-auto flex gap-2">
            {statusActions.map((a) => (
              <Button key={a.label} variant="secondary" size="sm" loading={mutations.process.isPending || mutations.archive.isPending} onClick={() => void a.run().then(onClose)}>
                <a.icon className="size-4" aria-hidden /> {a.label}
              </Button>
            ))}
            <Button variant="primary" size="sm" loading={mutations.save.isPending || saving} onClick={() => void save()}>
              <Save className="size-4" aria-hidden /> {isDraft ? '保存' : '保存'}
            </Button>
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        tone="danger"
        title="删除这条闪念？"
        description="软删除（附件元数据一并删除）。"
        confirmLabel="删除"
        loading={mutations.remove.isPending}
        onConfirm={async () => {
          if (note) await mutations.remove.mutateAsync(note.id)
          onClose()
        }}
      />
    </>
  )
}
