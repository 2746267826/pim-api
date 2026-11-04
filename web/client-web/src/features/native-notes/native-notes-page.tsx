import { useEffect, useState } from 'react'
import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight, BookOpenText, FileText, Image, NotebookPen, RefreshCw, ShieldCheck } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { Button, Card, EmptyState, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { formatTime } from '@/lib/datetime'
import { canonicalServerBinding } from '@/features/ai/note-derived/draft-storage'
import { nativeNotesApi, type NoteMaterialVersion, type NativeNotePage, type NativeNoteSummary } from './api'

const PAGE_SIZE = 20
const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请刷新后重试。'

interface SourceBlock {
  id?: string
  kind: string
  text?: string
  assetId?: string
  mimeType?: string
  width?: number
  height?: number
}

function parseSource(inputJson: string): { title: string; blocks: SourceBlock[] } | null {
  try {
    const value: unknown = JSON.parse(inputJson)
    if (!value || typeof value !== 'object' || !('note' in value)) return null
    const note = (value as { note?: unknown }).note
    if (!note || typeof note !== 'object') return null
    const record = note as { title?: unknown; blocks?: unknown }
    const blocks = Array.isArray(record.blocks) ? record.blocks.flatMap((item): SourceBlock[] => {
      if (!item || typeof item !== 'object') return []
      const block = item as Record<string, unknown>
      if (typeof block.kind !== 'string') return []
      return [{
        kind: block.kind,
        ...(typeof block.id === 'string' ? { id: block.id } : {}),
        ...(typeof block.text === 'string' ? { text: block.text } : {}),
        ...(typeof block.assetId === 'string' ? { assetId: block.assetId } : {}),
        ...(typeof block.mimeType === 'string' ? { mimeType: block.mimeType } : {}),
        ...(typeof block.width === 'number' ? { width: block.width } : {}),
        ...(typeof block.height === 'number' ? { height: block.height } : {}),
      }]
    }) : []
    return { title: typeof record.title === 'string' && record.title.trim() ? record.title : '未命名笔记', blocks }
  } catch {
    return null
  }
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** 当前账号已提交笔记原稿的只读入口；派生处理仍保留在 /native-notes。 */
export function NativeNotesPage() {
  const { user } = useAuth()
  const { noteId } = useParams()
  const [serverBinding, setServerBinding] = useState(canonicalServerBinding)
  useEffect(() => {
    const update = () => setServerBinding(canonicalServerBinding())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])
  const ownerId = user?.id ?? ''
  return <Workspace key={`${ownerId}/${serverBinding}/${noteId ?? 'list'}`} ownerId={ownerId} serverBinding={serverBinding} noteId={noteId ?? ''} />
}

function Workspace({ ownerId, serverBinding, noteId }: { ownerId: string; serverBinding: string; noteId: string }) {
  const [offset, setOffset] = useState(0)
  const list = useQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'list', offset],
    queryFn: ({ signal }) => nativeNotesApi.list(offset, signal),
    enabled: Boolean(ownerId) && !noteId,
  })
  const current = useQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'current', noteId],
    queryFn: ({ signal }) => nativeNotesApi.current(noteId, signal),
    enabled: Boolean(ownerId && noteId),
  })

  return noteId
    ? <CurrentNotePage query={current} />
    : <NativeNoteListPage offset={offset} setOffset={setOffset} query={list} />
}

function NativeNoteListPage({ offset, setOffset, query }: {
  offset: number
  setOffset: (offset: number) => void
  query: UseQueryResult<NativeNotePage>
}) {
  const data = query.data
  const items = data?.items ?? []
  const first = data && data.total > 0 ? offset + 1 : 0
  const last = Math.min(offset + items.length, data?.total ?? 0)

  return <div className="space-y-4">
    <PageHeader
      title="笔记原稿"
      subtitle="查看当前账号已提交的原始笔记材料"
      actions={<Link to="/native-notes" className="inline-flex h-8 items-center gap-1.5 rounded-ctl border border-border bg-bg px-3 text-[13px] font-medium text-text-2 hover:bg-surface hover:text-text-1"><NotebookPen className="size-4" aria-hidden />笔记处理</Link>}
    />
    <div className="flex items-start gap-2.5 rounded-ctl border border-info-border bg-info-soft px-3 py-2.5 text-[13px] text-text-2">
      <ShieldCheck className="mt-0.5 size-4 shrink-0 text-info" aria-hidden />
      <p>已同步原稿 · 只读。选择一篇笔记查看正文和随附材料；需要生成整理结果时，可前往“笔记处理”。</p>
    </div>

    {query.isPending ? (
      <div className="space-y-2" aria-label="正在加载笔记"><Skeleton className="h-20" /><Skeleton className="h-20" /><Skeleton className="h-20" /></div>
    ) : query.isError ? (
      <Card className="space-y-3 p-5">
        <EmptyState title="笔记列表加载失败" description={errorText(query.error)} />
        <div className="flex justify-center"><Button size="sm" onClick={() => void query.refetch()}><RefreshCw className="size-3.5" aria-hidden />重试加载</Button></div>
      </Card>
    ) : (
      <>
        {items.length > 0 ? (
          <div className="space-y-2">
            {items.map((item) => <NoteListItem key={item.noteId} item={item} />)}
          </div>
        ) : (
          <Card className="p-5">
            <EmptyState
              icon={BookOpenText}
              title={offset === 0 ? '还没有已提交的笔记原稿' : '这一页没有笔记'}
              description={offset === 0 ? '请先从支持的客户端提交完整笔记材料；提交完成后会显示在这里。' : '列表可能已更新，请返回上一页继续查看。'}
            />
          </Card>
        )}
        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-3">
          <p className="text-xs text-text-3">显示 {first}–{last} 篇，共 {data?.total ?? 0} 篇</p>
          <div className="flex gap-2">
            <Button size="sm" disabled={offset === 0 || query.isFetching} onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}><ArrowLeft className="size-3.5" aria-hidden />上一页</Button>
            <Button size="sm" disabled={items.length === 0 || offset + PAGE_SIZE >= (data?.total ?? 0) || query.isFetching} onClick={() => setOffset(offset + PAGE_SIZE)}>下一页<ArrowRight className="size-3.5" aria-hidden /></Button>
          </div>
        </div>
      </>
    )}
  </div>
}

function NoteListItem({ item }: { item: NativeNoteSummary }) {
  return <Link to={`/native-notes/library/${encodeURIComponent(item.noteId)}`} className="block rounded-card border border-border bg-bg p-4 shadow-card outline-none transition-colors hover:border-border-strong hover:bg-surface focus-visible:ring-2 focus-visible:ring-primary-ring">
    <div className="flex flex-wrap items-start gap-3">
      <span className="grid size-9 shrink-0 place-items-center rounded-ctl bg-primary/10 text-primary"><FileText className="size-4" aria-hidden /></span>
      <div className="min-w-0 flex-1">
        <p className="break-words text-sm font-semibold text-text-1">{item.title.trim() || '未命名笔记'}</p>
        <p className="mt-1 text-xs text-text-3">已同步原稿 · 只读查看</p>
      </div>
      {item.hasConflict && <StatusBadge tone="warn" dot={false}>有冲突版本保全</StatusBadge>}
      <ArrowRight className="mt-1 size-4 shrink-0 text-text-4" aria-hidden />
    </div>
  </Link>
}

function CurrentNotePage({ query }: {
  query: UseQueryResult<NoteMaterialVersion>
}) {
  const version = query.data
  const source = version ? parseSource(version.inputJson) : null
  const title = source?.title ?? '笔记原稿'
  const files = version?.upload.files ?? []

  return <div className="space-y-4">
    <PageHeader
      title={title}
      subtitle="当前原稿材料 · 只读查看"
      actions={<Link to="/native-notes/library" className="inline-flex h-8 items-center gap-1.5 rounded-ctl px-2 text-[13px] text-text-3 hover:bg-surface hover:text-text-1"><ArrowLeft className="size-4" aria-hidden />返回列表</Link>}
    />
    <div className="flex items-start gap-2.5 rounded-ctl border border-info-border bg-info-soft px-3 py-2.5 text-[13px] text-text-2">
      <ShieldCheck className="mt-0.5 size-4 shrink-0 text-info" aria-hidden />
      <p>已同步当前原稿，页面只读。正文按原文显示，若要生成整理结果，请前往“笔记处理”。</p>
    </div>

    {query.isPending ? (
      <div className="space-y-3" aria-label="正在加载原稿"><Skeleton className="h-24" /><Skeleton className="h-48" /></div>
    ) : query.isError || !version ? (
      <Card className="space-y-3 p-5">
        <EmptyState title="当前原稿读取失败" description={errorText(query.error)} />
        <div className="flex justify-center"><Button size="sm" onClick={() => void query.refetch()}><RefreshCw className="size-3.5" aria-hidden />重试读取</Button></div>
      </Card>
    ) : (
      <>
        <Card className="space-y-3 p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div><h2 className="text-sm font-semibold text-text-1">当前版本信息</h2><p className="mt-1 text-xs text-text-3">提交于 {formatTime(version.upload.createdAt)}</p></div>
            <StatusBadge tone={version.upload.hasConflict ? 'warn' : 'ok'} dot={false}>{version.upload.hasConflict ? '存在冲突版本' : '当前版本'}</StatusBadge>
          </div>
          <details className="border-t border-border pt-3 text-xs text-text-3">
            <summary className="cursor-pointer">查看材料来源信息</summary>
            <div className="mt-2 space-y-2 rounded-ctl bg-surface p-3">
              <p className="text-text-2">笔记 ID</p><p className="break-all font-mono text-[11px]">{version.upload.noteId}</p>
              <p className="mt-2 text-text-2">当前版本 ID</p><p className="break-all font-mono text-[11px]">{version.upload.versionId}</p>
            </div>
          </details>
        </Card>

        <Card className="space-y-4 p-4">
          <div>
            <h2 className="text-sm font-semibold text-text-1">原稿正文与内容块</h2>
            <p className="mt-1 text-xs text-text-3">正文按原文形式呈现。</p>
          </div>
          {!source ? <p role="alert" className="text-sm text-text-2">原稿结构无法解析。可在下方展开原始 JSON 核对。</p>
            : source.blocks.length === 0 ? <p className="text-sm text-text-3">此版本没有可显示的内容块。</p>
              : <div className="space-y-3">{source.blocks.map((block, index) => <SourceBlockView key={block.id ?? `${block.kind}-${index}`} block={block} index={index} />)}</div>}
          <details className="border-t border-border pt-3 text-xs text-text-3">
            <summary className="cursor-pointer">查看原始输入 JSON</summary>
            <pre className="mt-2 max-h-96 overflow-auto whitespace-pre-wrap break-words rounded-ctl bg-surface p-3 font-mono text-[11px] leading-5 text-text-2">{version.inputJson}</pre>
          </details>
        </Card>

        <Card className="space-y-3 p-4">
          <div><h2 className="text-sm font-semibold text-text-1">随版本提交的材料文件</h2><p className="mt-1 text-xs text-text-3">共 {files.length} 个文件；此入口展示文件清单，不修改或重新提交材料。</p></div>
          {files.length === 0 ? <p className="text-sm text-text-3">该版本没有附带文件。</p> : <ul className="divide-y divide-border">{files.map((file) => <li key={`${file.index}/${file.path}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-xs">
            <span className="min-w-0 flex-1 break-all text-text-1">{file.path}</span><span className="text-text-3">{file.kind}</span><span className="tnum text-text-3">{formatBytes(file.sizeBytes)}</span>
          </li>)}</ul>}
          <details className="border-t border-border pt-3 text-xs text-text-3">
            <summary className="cursor-pointer">查看材料清单 JSON</summary>
            <pre className="mt-2 max-h-72 overflow-auto whitespace-pre-wrap break-words rounded-ctl bg-surface p-3 font-mono text-[11px] leading-5 text-text-2">{version.manifestJson}</pre>
          </details>
        </Card>
      </>
    )}
  </div>
}

function SourceBlockView({ block, index }: { block: SourceBlock; index: number }) {
  if (block.kind === 'markdown') return <section className="rounded-ctl border border-border p-3">
    <h3 className="mb-2 text-xs font-medium text-text-3">正文材料 · {index + 1}</h3>
    <pre className="whitespace-pre-wrap break-words text-[13px] leading-6 text-text-1">{block.text || '（空白正文）'}</pre>
  </section>
  if (block.kind === 'photo') return <div className="flex items-start gap-2 rounded-ctl border border-border p-3 text-xs text-text-2">
    <Image className="mt-0.5 size-4 shrink-0 text-text-3" aria-hidden />
    <div className="min-w-0"><p className="font-medium">图片材料 · {index + 1}</p><p className="mt-1 break-all text-text-3">{block.mimeType ?? '图片'}{block.assetId ? ` · ${block.assetId}` : ''}</p></div>
  </div>
  if (block.kind === 'ink') return <div className="flex items-start gap-2 rounded-ctl border border-border p-3 text-xs text-text-2">
    <NotebookPen className="mt-0.5 size-4 shrink-0 text-text-3" aria-hidden />
    <div className="min-w-0"><p className="font-medium">手写笔迹材料 · {index + 1}</p><p className="mt-1 text-text-3">画布 {block.width ?? '—'} × {block.height ?? '—'}{block.assetId ? ` · ${block.assetId}` : ''}</p></div>
  </div>
  return <section className="rounded-ctl border border-border p-3 text-xs text-text-2"><h3 className="font-medium">内容块 · {block.kind}</h3>{block.text && <pre className="mt-2 whitespace-pre-wrap break-words text-[13px] leading-6">{block.text}</pre>}</section>
}
