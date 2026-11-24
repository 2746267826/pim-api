import { useRef, useState, type ReactNode } from 'react'
import { AlertTriangle, Check, History, RefreshCw } from 'lucide-react'
import { Button, Card, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import { formatTime } from '@/lib/datetime'
import { cn } from '@/lib/utils'
import type { NoteMaterialUpload, NoteMaterialVersion } from './material-model'
import {
  buildNoteVersionViews,
  safeVersionSummary,
  versionShortId,
  type NoteVersionView,
  type SafeVersionSummary,
} from './version-model'

export interface PromoteVersionOutcome {
  status: 'adopted' | 'stale' | 'failed'
  message: string
}

export interface ConflictMaterialState {
  material?: NoteMaterialVersion
  loading: boolean
  error?: unknown
}

export interface NoteVersionPanelProps {
  noteId: string
  versions: NoteMaterialUpload[]
  versionsLoading: boolean
  versionsError: unknown
  refreshIssue: string[]
  current: NoteMaterialVersion | undefined
  currentLoading: boolean
  currentError: unknown
  selectedVersionId: string
  conflictMaterials: Record<string, ConflictMaterialState | undefined>
  onOpenVersion: (versionId: string) => void
  onOpenCurrent: () => void
  onRefresh: () => void
  onCancelConflict: () => void
  onPromote: (versionId: string) => Promise<PromoteVersionOutcome>
}

export function NoteVersionPanel({
  noteId,
  versions,
  versionsLoading,
  versionsError,
  refreshIssue,
  current,
  currentLoading,
  currentError,
  selectedVersionId,
  conflictMaterials,
  onOpenVersion,
  onOpenCurrent,
  onRefresh,
  onCancelConflict,
  onPromote,
}: NoteVersionPanelProps) {
  const views = buildNoteVersionViews(versions)
  const conflictVersions = views.filter((view) => view.kind === 'conflict')
  const currentVersionId = current?.upload.currentVersionId ?? versions.find((version) => version.isCurrent)?.versionId ?? null
  const conflictKey = `${noteId}:${currentVersionId ?? 'none'}:${conflictVersions.map((view) => view.upload.versionId).join(',')}`
  const [dismissedConflictKey, setDismissedConflictKey] = useState('')
  const [busyVersionId, setBusyVersionId] = useState('')
  const [notice, setNotice] = useState<{ tone: 'ok' | 'warn' | 'crit'; text: string } | null>(null)
  const busyRef = useRef(false)
  const showConflict = refreshIssue.length === 0 && Boolean(current) &&
    conflictVersions.length > 0 && dismissedConflictKey !== conflictKey
  const currentSummary = safeVersionSummary(current)

  function cancelConflict() {
    if (busyRef.current) return
    setDismissedConflictKey(conflictKey)
    setNotice(null)
    onCancelConflict()
  }

  async function promote(versionId: string) {
    if (busyRef.current) return
    busyRef.current = true
    setBusyVersionId(versionId)
    setNotice(null)
    try {
      const outcome = await onPromote(versionId)
      setNotice({
        tone: outcome.status === 'adopted' ? 'ok' : outcome.status === 'stale' ? 'warn' : 'crit',
        text: outcome.message,
      })
    } catch (failure) {
      setNotice({
        tone: 'crit',
        text: `${errorText(failure)}。未确认本次操作是否成功，请刷新核对后再选择。`,
      })
    } finally {
      busyRef.current = false
      setBusyVersionId('')
    }
  }

  return (
    <Card className="space-y-4 p-4" aria-labelledby="note-version-heading">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 id="note-version-heading" className="flex items-center gap-1.5 text-sm font-semibold text-text-1">
            <History className="size-4 text-text-3" aria-hidden /> 版本与保存编号
          </h2>
          <p className="mt-1 text-xs leading-5 text-text-3">
            每个版本都保留在服务器上。历史版本只读；发生真实冲突时，请明确选择继续使用哪一份内容。
          </p>
        </div>
        <Button size="sm" variant="ghost" aria-label="刷新版本历史" onClick={onRefresh}>
          <RefreshCw className="size-3.5" aria-hidden /> 刷新
        </Button>
      </div>

      {notice && <InlineAlert tone={notice.tone} title={notice.tone === 'ok' ? '版本已更新' : '需要核对'}>{notice.text}</InlineAlert>}

      {refreshIssue.length > 0 && (
        <InlineAlert tone="warn" title="冲突状态未完成刷新">
          {refreshIssue.join('、')}未能重新读取。为避免在旧数据上继续选择，已隐藏冲突候选。
          <Button className="mt-2" size="sm" variant="secondary" onClick={onRefresh}>
            重新读取并核对
          </Button>
        </InlineAlert>
      )}

      {currentLoading ? <Skeleton className="h-20" />
        : currentError ? (
          <InlineAlert tone="crit" title="当前版本读取失败">
            {errorText(currentError)}。版本历史可能仍可查看，请刷新后重试。
          </InlineAlert>
        ) : current ? (
          <div className="rounded-ctl border border-border bg-surface p-3 text-xs leading-5 text-text-2">
            <div className="flex flex-wrap items-center gap-2">
              <StatusBadge tone="ok" dot={false}>当前版</StatusBadge>
              {currentSummary?.revision != null && <span className="text-text-4">修订 {currentSummary.revision}</span>}
            </div>
            <dl className="mt-2 grid gap-x-4 gap-y-1 sm:grid-cols-[7rem_minmax(0,1fr)]">
              <dt className="text-text-4">当前版本 ID</dt>
              <dd className="mono break-all text-text-1">{current.upload.versionId}</dd>
              <dt className="text-text-4">来源版本</dt>
              <dd className="mono break-all text-text-1">{current.upload.baseVersionId ?? '新建笔记'}</dd>
              <dt className="text-text-4">保存时间</dt>
              <dd className="tnum text-text-1">{formatTime(current.upload.createdAt)}</dd>
            </dl>
          </div>
        ) : (
          <p className="rounded-ctl border border-border bg-surface p-3 text-xs text-text-3">
            还没有已保存的当前版本。第一次保存后会在这里显示版本 ID、保存编号和来源版本。
          </p>
        )}

      {showConflict ? (
        <ConflictComparison
          current={current}
          currentSummary={currentSummary}
          candidates={conflictVersions}
          materials={conflictMaterials}
          busyVersionId={busyVersionId}
          onAdopt={promote}
          onCancel={cancelConflict}
        />
      ) : refreshIssue.length === 0 && current && conflictVersions.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2 rounded-ctl border border-warn-border bg-warn-soft px-3 py-2 text-xs text-warn">
          <AlertTriangle className="size-4 shrink-0" aria-hidden />
          <span className="mr-auto">还有 {conflictVersions.length} 个冲突候选待处理；当前版本没有改变。</span>
          <Button size="sm" variant="secondary" onClick={() => setDismissedConflictKey('')}>查看冲突</Button>
        </div>
      ) : null}

      <div>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 className="text-xs font-semibold text-text-1">版本历史</h3>
          <span className="tnum text-[11px] text-text-4">
            {versions.length} 个版本 · {conflictVersions.length} 个冲突候选
          </span>
        </div>
        {versionsLoading ? <div className="mt-2 space-y-2"><Skeleton className="h-16" /><Skeleton className="h-16" /></div>
          : versionsError ? (
            <InlineAlert className="mt-2" tone="crit" title="版本历史读取失败">
              <p>{errorText(versionsError)}</p>
              <Button className="mt-2" size="sm" variant="secondary" onClick={onRefresh}>重新读取</Button>
            </InlineAlert>
          ) : views.length === 0 ? (
            <p className="mt-2 rounded-ctl bg-surface p-3 text-xs text-text-3">还没有历史版本。</p>
          ) : (
            <ol className="mt-2 space-y-2" aria-label="版本历史列表">
              {views.map((view) => (
                <VersionHistoryItem
                  key={view.upload.versionId}
                  view={view}
                  selected={view.upload.versionId === selectedVersionId || (view.upload.isCurrent && !selectedVersionId)}
                  onOpen={() => view.upload.isCurrent ? onOpenCurrent() : onOpenVersion(view.upload.versionId)}
                />
              ))}
            </ol>
          )}
      </div>
    </Card>
  )
}

function VersionHistoryItem({
  view,
  selected,
  onOpen,
}: {
  view: NoteVersionView
  selected: boolean
  onOpen: () => void
}) {
  const { upload } = view
  const statusTone = view.kind === 'current' ? 'ok' : view.kind === 'conflict' ? 'warn' : view.kind === 'uploading' ? 'info' : 'neutral'
  const content = <div className="min-w-0 flex-1">
    <span className="flex flex-wrap items-center gap-1.5">
      <StatusBadge tone={statusTone} dot={false}>{view.kindLabel}</StatusBadge>
      <span className="text-xs font-medium text-text-1">{view.phaseLabel}</span>
      <time className="tnum ml-auto text-[11px] text-text-4" dateTime={upload.createdAt}>{formatTime(upload.createdAt)}</time>
    </span>
    <span className="mt-1.5 block text-[11px] leading-5 text-text-3">
      版本 ID / 保存编号 <span className="mono break-all text-text-2">{upload.versionId}</span>
    </span>
    <span className="block text-[11px] leading-5 text-text-3">
      来源版本 <span className="mono break-all text-text-2">{upload.baseVersionId ?? '新建笔记'}</span>
    </span>
  </div>

  if (!view.canOpen) {
    return <li className="rounded-ctl border border-border bg-surface p-3">{content}</li>
  }
  return <li>
    <button
      type="button"
      onClick={onOpen}
      aria-label={`查看版本 ${formatTime(upload.createdAt)}，${view.kindLabel}，版本 ID ${upload.versionId}`}
      aria-current={selected ? 'true' : undefined}
      className={cn(
        'flex w-full items-start gap-2 rounded-ctl border p-3 text-left outline-none transition-colors hover:border-border-strong hover:bg-surface',
        selected ? 'border-primary bg-primary/5' : 'border-border bg-bg',
      )}
    >
      {content}
      {selected && <Check className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden />}
    </button>
  </li>
}

function ConflictComparison({
  current,
  currentSummary,
  candidates,
  materials,
  busyVersionId,
  onAdopt,
  onCancel,
}: {
  current: NoteMaterialVersion | undefined
  currentSummary: SafeVersionSummary | null
  candidates: NoteVersionView[]
  materials: Record<string, ConflictMaterialState | undefined>
  busyVersionId: string
  onAdopt: (versionId: string) => void
  onCancel: () => void
}) {
  return (
    <section className="rounded-card border border-warn-border bg-warn-soft/50 p-3" aria-labelledby="note-conflict-heading">
      <div className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warn" aria-hidden />
        <div>
          <h3 id="note-conflict-heading" className="text-sm font-semibold text-text-1">发现内容冲突</h3>
          <p className="mt-1 text-xs leading-5 text-text-2">
            服务器已保存另一端的修改。下面两份内容都会保留；只有你明确采用的版本会成为当前版本。
          </p>
        </div>
      </div>
      <div className="mt-3 grid gap-3 lg:grid-cols-2">
        <ConflictSide
          label="服务器当前内容"
          versionId={current?.upload.versionId ?? null}
          createdAt={current?.upload.createdAt ?? null}
          summary={currentSummary}
          tone="ok"
          action={<Button size="sm" variant="secondary" disabled={Boolean(busyVersionId)} onClick={onCancel}>保留当前内容</Button>}
        />
        <div className="space-y-2">
          {candidates.map((candidate) => {
            const state = materials[candidate.upload.versionId]
            const summary = safeVersionSummary(state?.material)
            const materialReady = Boolean(summary) && !state?.loading && !state?.error
            return <ConflictSide
              key={candidate.upload.versionId}
              label="另一端保存的内容"
              versionId={candidate.upload.versionId}
              createdAt={candidate.upload.createdAt}
              summary={summary}
              loading={state?.loading ?? false}
              error={state?.error}
              tone="warn"
              action={<div className="text-right">
                <Button
                  size="sm"
                  variant="primary"
                  loading={busyVersionId === candidate.upload.versionId}
                  disabled={!materialReady || (Boolean(busyVersionId) && busyVersionId !== candidate.upload.versionId)}
                  onClick={() => onAdopt(candidate.upload.versionId)}
                >
                  采用这份内容
                </Button>
                {!materialReady && (
                  <p className="mt-1 text-[11px] text-text-3">
                    {state?.loading
                      ? '候选摘要读取中，暂不能采用。'
                      : '候选摘要未读取，暂不能采用；请刷新核对或打开版本查看。'}
                  </p>
                )}
              </div>}
            />
          })}
        </div>
      </div>
      <div className="mt-3 flex flex-wrap items-center justify-end gap-2">
        <p className="mr-auto text-[11px] text-text-3">取消不会修改任何版本；另一份内容仍保留在服务器。</p>
        <Button size="sm" variant="ghost" disabled={Boolean(busyVersionId)} onClick={onCancel}>稍后处理</Button>
      </div>
    </section>
  )
}

function ConflictSide({
  label,
  versionId,
  createdAt,
  summary,
  loading = false,
  error,
  tone,
  action,
}: {
  label: string
  versionId: string | null
  createdAt: string | null
  summary: SafeVersionSummary | null
  loading?: boolean
  error?: unknown
  tone: 'ok' | 'warn'
  action: ReactNode
}) {
  return (
    <article className="flex min-w-0 flex-col rounded-ctl border border-border bg-bg p-3">
      <div className="flex items-center gap-2">
        <StatusBadge tone={tone} dot={false}>{label}</StatusBadge>
        {createdAt && <time className="tnum ml-auto text-[11px] text-text-4" dateTime={createdAt}>{formatTime(createdAt)}</time>}
      </div>
      <h4 className="mt-2 truncate text-sm font-medium text-text-1">{summary?.title ?? '版本摘要暂不可用'}</h4>
      {loading ? <Skeleton className="mt-1 h-14" />
        : error ? <InlineAlert className="mt-1" tone="crit">{errorText(error)}</InlineAlert>
          : <p className="mt-1 line-clamp-3 min-h-10 whitespace-pre-wrap break-words text-xs leading-5 text-text-2">
            {summary?.excerpt || '此版本只包含材料或无法读取文字摘要；可先打开版本查看。'}
          </p>}
      <dl className="mt-2 grid gap-x-3 gap-y-0.5 text-[11px] leading-5 text-text-3 sm:grid-cols-[4.5rem_minmax(0,1fr)]">
        <dt>版本 ID</dt>
        <dd className="mono break-all text-text-2">{versionId ? versionShortId(versionId) : '—'}</dd>
        {summary && <>
          <dt>正文块</dt>
          <dd>{summary.markdownBlocks} 个 · 照片 {summary.photoBlocks} 张 · 手写 {summary.inkBlocks} 张</dd>
          <dt>录音</dt>
          <dd>{summary.recordings} 段</dd>
        </>}
      </dl>
      <div className="mt-3 flex justify-end">{action}</div>
    </article>
  )
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}
