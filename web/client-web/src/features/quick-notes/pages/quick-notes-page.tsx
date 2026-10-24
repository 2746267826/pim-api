import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Paperclip, Plus } from 'lucide-react'
import { useQuickNotes } from '../queries'
import { quickNotesApi } from '../api'
import { QuickNoteDialog, type QuickNoteDialogTarget } from '../components/quick-note-dialog'
import { NOTE_CATEGORIES, noteCategory, type QuickNoteListItem } from '../types'
import { Button, Card, Chip, EmptyState, Input, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { formatTime } from '@/lib/datetime'

const STATUS_OPTIONS = [
  { value: 'all', label: '全部' },
  { value: 'inbox', label: '收集箱' },
  { value: 'processed', label: '已处理' },
  { value: 'archived', label: '已归档' },
]

const STATUS_BADGE: Record<string, { label: string; tone: 'info' | 'ok' | 'neutral' }> = {
  inbox: { label: '收集箱', tone: 'info' },
  processed: { label: '已处理', tone: 'ok' },
  archived: { label: '已归档', tone: 'neutral' },
}

/** 快速记录页（02 §quick-notes：状态 chips + 搜索逐键直查 + 分类 pills + 瀑布流） */
export function QuickNotesPage() {
  const [params, setParams] = useSearchParams()
  const [status, setStatus] = useState('all')
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<string>('全部')
  const [target, setTarget] = useState<QuickNoteDialogTarget | null>(null)
  const [opening, setOpening] = useState(false)

  const { data, isLoading } = useQuickNotes({ status, search })
  const prefill = params.get('prefill') ?? params.get('text')

  /* Shell 分享预填：?prefill= 打开草稿并预填（不预先建记录） */
  useEffect(() => {
    if (prefill != null && target == null) {
      setTarget({ mode: 'draft', initialMarkdown: prefill, source: 'web-page' })
      setParams({}, { replace: true })
    }
  }, [prefill, target, setParams])

  const items = useMemo(() => {
    // 历史遗留：早期版本「打开即建记录」留下过空闪念；列表不展示无内容且无附件的记录
    const list = (data?.items ?? []).filter((n) => n.contentPreview.trim().length > 0 || (n.attachmentCount ?? 0) > 0)
    if (category === '全部') return list
    return list.filter((n) => noteCategory(n.contentPreview) === category)
  }, [data, category])

  function createNote() {
    setTarget({ mode: 'draft', initialMarkdown: '', source: 'web-page' })
  }

  async function openNote(item: QuickNoteListItem) {
    setOpening(true)
    try {
      const detail = await quickNotesApi.get(item.id)
      setTarget({ mode: 'edit', note: detail })
    } finally {
      setOpening(false)
    }
  }

  return (
    <div>
      <PageHeader
        title="快速记录"
        subtitle={`${data?.totalCount ?? 0} 条闪念`}
        actions={
          <Button variant="primary" size="sm" onClick={() => void createNote()}>
            <Plus className="size-4" aria-hidden /> 写闪念
          </Button>
        }
      />

      {/* 状态 chips + 搜索 */}
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="flex gap-1.5">
          {STATUS_OPTIONS.map((o) => (
            <Chip key={o.value} active={status === o.value} onClick={() => setStatus(o.value)}>{o.label}</Chip>
          ))}
        </div>
        <Input
          className="ml-auto w-56"
          placeholder="搜索闪念…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {/* 分类 pills */}
      <div className="mb-3 flex flex-wrap gap-1.5">
        {NOTE_CATEGORIES.map((c) => (
          <Chip key={c} active={category === c} onClick={() => setCategory(c)}>{c}</Chip>
        ))}
      </div>

      {/* 瀑布流卡片墙 */}
      {isLoading ? (
        <div className="columns-1 gap-3 sm:columns-2 xl:columns-3 2xl:columns-4">
          {Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mb-3 h-32" />)}
        </div>
      ) : items.length === 0 ? (
        <Card>
          <EmptyState
            title="收集箱是空的"
            description="右上角写一条闪念，或从任意页面的 FAB 快速记录。"
            action={<Button variant="primary" size="sm" onClick={() => void createNote()}><Plus className="size-4" aria-hidden /> 写闪念</Button>}
          />
        </Card>
      ) : (
        <div className="columns-1 gap-3 sm:columns-2 xl:columns-3 2xl:columns-4">
          {items.map((n) => {
            const cat = noteCategory(n.contentPreview)
            const badge = STATUS_BADGE[n.status]
            return (
              <button
                key={n.id}
                type="button"
                onClick={() => void openNote(n)}
                disabled={opening}
                className="mb-3 block w-full break-inside-avoid rounded-card border border-border bg-bg p-4 text-left shadow-card transition-colors outline-none hover:border-border-strong disabled:opacity-70"
              >
                <div className="flex items-center gap-1.5">
                  {cat && <Chip active className="h-5 cursor-default px-2 text-[11px]">{cat}</Chip>}
                  <span className="ml-auto tnum text-[11px] text-text-4">{formatTime(n.createdAt)}</span>
                </div>
                <p className="mt-2 line-clamp-6 whitespace-pre-wrap text-[13px] leading-5 text-text-1">
                  {n.contentPreview || '（空闪念）'}
                </p>
                <div className="mt-2 flex items-center gap-2">
                  {(n.attachments?.length ?? 0) > 0 && (
                    <span className="inline-flex items-center gap-1 text-[11px] text-text-4">
                      <Paperclip className="size-3" aria-hidden /> {n.attachmentCount}
                    </span>
                  )}
                  <StatusBadge tone={badge.tone} dot={false} className="ml-auto">{badge.label}</StatusBadge>
                </div>
              </button>
            )
          })}
        </div>
      )}

      <QuickNoteDialog target={target} onClose={() => setTarget(null)} />
    </div>
  )
}
