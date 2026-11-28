import { useState } from 'react'
import { Link } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { HardDrive, ShieldCheck } from 'lucide-react'
import {
  Button,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  InlineAlert,
  StatusBadge,
} from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { useObjectUrlCacheEpoch } from '@/lib/object-url-cache'
import { useUnsavedDrafts } from '@/lib/unsaved-drafts'
import { cn } from '@/lib/utils'
import {
  clearTemporaryWebCache,
  snapshotTemporaryCache,
  type TemporaryCacheClearResult,
} from './cache-cleanup'
import { belongsToOtherServer, ordinaryDraftProcessingHref, serverLabel } from './display'

/**
 * 常驻入口：设置页是独立路由，离开编辑器后内存草稿会注销；
 * 侧栏按钮在弹窗中打开清理面板，当前页面（笔记编辑器、文件预览等）保持挂载，
 * 因此这些界面里的未保存编辑仍能被真实看到并受到清理前确认保护。
 */
export function StorageCacheLauncher({
  compact = false,
  className,
}: {
  compact?: boolean
  className?: string
}) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button
        type="button"
        aria-label="存储与缓存"
        title="存储与缓存：清理临时缓存"
        onClick={() => setOpen(true)}
        className={cn(
          'rounded-ctl text-text-3 transition-colors outline-none hover:bg-surface-2 hover:text-text-1',
          compact
            ? 'grid size-8 place-items-center'
            : 'flex h-8 w-full items-center gap-1.5 px-1 text-xs',
          className,
        )}
      >
        <HardDrive className="size-4 shrink-0" aria-hidden />
        {!compact && <span>存储与缓存</span>}
      </button>
      <StorageCacheDialog open={open} onOpenChange={setOpen} />
    </>
  )
}

export function StorageCacheDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const { user } = useAuth()
  const ownerId = user?.id ?? ''
  const canViewVersionDrafts = user?.role === 'admin'
  const queryClient = useQueryClient()
  useUnsavedDrafts()
  useObjectUrlCacheEpoch()
  const [preserveConfirmed, setPreserveConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TemporaryCacheClearResult | null>(null)
  const [error, setError] = useState('')

  const snapshot = snapshotTemporaryCache(queryClient, ownerId)
  const protectedCount = snapshot.unsavedDrafts.length + snapshot.aiDrafts.length
  const clearableCount = snapshot.queryEntries + snapshot.thumbnailEntries + snapshot.playerEntries + snapshot.transferHistoryEntries

  function handleOpenChange(next: boolean) {
    if (next) {
      onOpenChange(true)
      return
    }
    setPreserveConfirmed(false)
    setError('')
    setResult(null)
    onOpenChange(false)
  }

  async function clear() {
    setBusy(true)
    setError('')
    try {
      const cleared = await clearTemporaryWebCache(queryClient, ownerId)
      setResult(cleared)
      notifySuccess('临时缓存已清理', `查询 ${cleared.queryEntries} 条 · 缩略图 ${cleared.thumbnailEntries} 项 · 播放器 ${cleared.playerEntries} 项 · 传输历史 ${cleared.transferHistoryEntries} 条`)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : '清理临时缓存失败，请稍后重试')
      notifyError('清理临时缓存失败')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent className="max-w-[640px]">
        <DialogHeader
          title="存储与缓存"
          description="只清理查询、缩略图、播放器和普通文件传输历史；不会删除未保存编辑、草稿、令牌或服务器配置。"
        />
        <DialogBody className="space-y-4">
          <p className="flex flex-wrap items-center gap-1.5 text-xs text-text-3">
            当前服务器：<span className="mono break-all text-text-2">{serverLabel(snapshot.serverBinding)}</span>
          </p>

          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {[
              ['查询缓存', snapshot.queryEntries, '条'],
              ['图片缩略图', snapshot.thumbnailEntries, '项'],
              ['播放器缓存', snapshot.playerEntries, '项'],
              ['传输历史', snapshot.transferHistoryEntries, '条'],
            ].map(([label, count, unit]) => (
              <div key={String(label)} className="rounded-ctl border border-border bg-surface px-3 py-2">
                <div className="text-[11px] text-text-3">{label}</div>
                <div className="mt-0.5 text-sm font-semibold text-text-1 tnum">{count} {unit}</div>
              </div>
            ))}
          </div>
          <p className="text-xs leading-5 text-text-3">
            共 {clearableCount} 项可清理；
            {snapshot.inUseQueryEntries > 0 && `当前页面使用中的 ${snapshot.inUseQueryEntries} 条查询会保留；`}
            使用中的缩略图/播放器对象 URL 会保留到组件释放后再撤销；清理后页面会按需重新读取服务器。
          </p>

          <section aria-labelledby="storage-dialog-protected-heading">
            <div className="mb-2 flex flex-wrap items-center gap-2">
              <ShieldCheck className="size-4 text-ok" aria-hidden />
              <h2 id="storage-dialog-protected-heading" className="text-sm font-semibold text-text-1">受保护内容</h2>
              <StatusBadge tone={protectedCount > 0 ? 'warn' : 'ok'} dot={false}>
                {protectedCount > 0 ? `${protectedCount} 项需要留意` : '当前无未保存内容'}
              </StatusBadge>
            </div>
            <p className="text-xs leading-5 text-text-3">
              只列出当前账号、当前仍挂载页面中的未保存编辑与本机 AI 草稿。其他账号的 AI 草稿不会显示，也不会被清理。
            </p>
            {protectedCount === 0 ? (
              <p className="mt-2 rounded-ctl bg-surface px-3 py-2 text-[13px] text-text-2">
                没有检测到当前账号的未保存编辑或本机 AI 草稿。
              </p>
            ) : (
              <ul className="mt-2 max-h-64 space-y-2 overflow-y-auto">
                {snapshot.unsavedDrafts.map((draft) => (
                  <li key={draft.id} className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2 text-[13px]">
                    <div className="flex flex-wrap items-center gap-2">
                      <StatusBadge tone="warn" dot={false}>未保存编辑</StatusBadge>
                      <span className="font-medium text-text-1">{draft.title}</span>
                    </div>
                    <p className="mt-1 text-text-2">{draft.detail}</p>
                    <p className="mt-1 text-xs text-text-3">{draft.saveHint}</p>
                    {draft.saveHref && (
                      <Link
                        className="mt-1 inline-block text-xs text-primary hover:underline"
                        to={draft.saveHref}
                        onClick={() => handleOpenChange(false)}
                      >
                        去保存
                      </Link>
                    )}
                  </li>
                ))}
                {snapshot.aiDrafts.map((draft) => {
                  const otherServer = belongsToOtherServer(draft.binding.serverBinding, snapshot.serverBinding)
                  return (
                    <li key={draft.key} className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2 text-[13px]">
                      <div className="flex flex-wrap items-center gap-2">
                        <StatusBadge tone="warn" dot={false}>AI 草稿</StatusBadge>
                        <span className="font-medium text-text-1">
                          {draft.binding.kind === 'IndependentAnalysis' ? '独立分析' : '完整记录'}
                        </span>
                        {draft.pending && <StatusBadge tone="info" dot={false}>有待核对提交</StatusBadge>}
                        {otherServer && <StatusBadge tone="neutral" dot={false}>其他服务器</StatusBadge>}
                      </div>
                      {canViewVersionDrafts ? (
                        <>
                          <p className="mt-1 break-all text-xs text-text-3">
                            来源版本 {draft.binding.sourceVersionId || '未知'} · {draft.updatedAt ? formatTime(draft.updatedAt) : '记录损坏'}
                          </p>
                          <p className="mt-1 break-all text-xs text-text-3">草稿服务器：{serverLabel(draft.binding.serverBinding)}</p>
                          <p className="mt-1 text-xs text-text-3">
                            {otherServer
                              ? '此草稿属于其他服务器；请先切换到该服务器，再从笔记处理页核对保存。'
                              : '打开笔记处理页核对并保存人工修改；AI 草稿不会随临时缓存清理删除。'}
                          </p>
                        </>
                      ) : (
                        <p className="mt-1 text-xs leading-5 text-text-3">
                          {otherServer
                            ? '此草稿属于其他服务器；请先切换到该服务器，再从笔记处理页核对保存。'
                            : draft.binding.noteId
                              ? 'AI 草稿已保存在本机。普通账号不显示历史材料版本；将打开关联笔记的当前内容与对应整理结果版本。'
                              : '这份旧草稿缺少笔记定位信息；请从处理页手动选择当前笔记核对并保存，草稿不会被清理。'}
                        </p>
                      )}
                      {!otherServer && (canViewVersionDrafts
                        ? draft.binding.sourceVersionId && (
                          <Link
                            className="mt-1 inline-block text-xs text-primary hover:underline"
                            to={`/native-notes/processing?version=${encodeURIComponent(draft.binding.sourceVersionId)}`}
                            onClick={() => handleOpenChange(false)}
                          >
                            去保存
                          </Link>
                        )
                        : (
                          <Link
                            className="mt-1 inline-block text-xs text-primary hover:underline"
                            to={ordinaryDraftProcessingHref(draft)}
                            onClick={() => handleOpenChange(false)}
                          >
                            {draft.binding.noteId ? '去处理页核对' : '去处理页手动核对'}
                          </Link>
                        ))}
                    </li>
                  )
                })}
              </ul>
            )}
          </section>

          {protectedCount > 0 && (
            <label className="flex items-start gap-2 rounded-ctl border border-border bg-bg px-3 py-2.5 text-xs leading-5 text-text-2">
              <Checkbox
                className="mt-0.5"
                checked={preserveConfirmed}
                onCheckedChange={setPreserveConfirmed}
                ariaLabel="确认保留未保存编辑与 AI 草稿"
              />
              <span>我已知晓以上内容不会被清理，并选择只清理临时缓存。</span>
            </label>
          )}

          {result && (
            <div className="rounded-ctl border border-border bg-surface px-3 py-2.5 text-xs leading-5 text-text-3" role="status">
              <p className="font-medium text-text-1">上次清理回执</p>
              <p className="mt-1">
                已清理查询缓存 {result.queryEntries} 条、缩略图 {result.thumbnailEntries} 项、播放器缓存 {result.playerEntries} 项、传输历史 {result.transferHistoryEntries} 条。
              </p>
              <p className="mt-1">清理时保留未保存编辑/草稿 {result.preservedUnsavedDrafts} 项、AI 草稿 {result.preservedAiDrafts} 份。</p>
            </div>
          )}

          {error && <InlineAlert tone="crit" title="清理未完成">{error}</InlineAlert>}
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={() => handleOpenChange(false)} disabled={busy}>关闭</Button>
          <Button
            variant="primary"
            loading={busy}
            disabled={protectedCount > 0 && !preserveConfirmed}
            onClick={() => void clear()}
          >
            只清理临时缓存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
