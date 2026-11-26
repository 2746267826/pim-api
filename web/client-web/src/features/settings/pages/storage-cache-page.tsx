import { useState } from 'react'
import { Link } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import {
  FileClock,
  HardDrive,
  Image,
  Play,
  RefreshCw,
  Server,
  ShieldCheck,
  Trash2,
} from 'lucide-react'
import {
  Button,
  Card,
  CardSubtitle,
  CardTitle,
  Checkbox,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  InlineAlert,
  PageHeader,
  StatusBadge,
} from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { useObjectUrlCacheEpoch } from '@/lib/object-url-cache'
import { useUnsavedDrafts } from '@/lib/unsaved-drafts'
import {
  clearTemporaryWebCache,
  snapshotTemporaryCache,
  type TemporaryCacheClearResult,
} from '../storage-cache/cache-cleanup'
import { belongsToOtherServer, serverLabel } from '../storage-cache/display'

function CountCard({
  icon: Icon,
  title,
  count,
  unit,
  description,
}: {
  icon: typeof HardDrive
  title: string
  count: number
  unit: string
  description: string
}) {
  return (
    <Card className="p-4">
      <div className="flex items-center gap-2">
        <span className="grid size-8 shrink-0 place-items-center rounded-ctl bg-surface-2 text-text-2">
          <Icon className="size-4" aria-hidden />
        </span>
        <CardTitle>{title}</CardTitle>
        <StatusBadge className="ml-auto" tone={count > 0 ? 'info' : 'neutral'} dot={false}>
          {count} {unit}
        </StatusBadge>
      </div>
      <CardSubtitle className="mt-2">{description}</CardSubtitle>
    </Card>
  )
}

export function StorageCachePage() {
  const { user } = useAuth()
  const ownerId = user?.id ?? ''
  const queryClient = useQueryClient()
  useUnsavedDrafts()
  useObjectUrlCacheEpoch()
  const [, setRefreshToken] = useState(0)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [preserveConfirmed, setPreserveConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<TemporaryCacheClearResult | null>(null)
  const [error, setError] = useState('')

  const snapshot = snapshotTemporaryCache(queryClient, ownerId)
  const protectedCount = snapshot.unsavedDrafts.length + snapshot.aiDrafts.length
  const clearableCount = snapshot.queryEntries + snapshot.thumbnailEntries + snapshot.playerEntries + snapshot.transferHistoryEntries

  function refresh() {
    setRefreshToken((value) => value + 1)
    setError('')
  }

  function openCleanup() {
    setPreserveConfirmed(false)
    setError('')
    setDialogOpen(true)
  }

  async function clear() {
    setBusy(true)
    setError('')
    try {
      const cleared = await clearTemporaryWebCache(queryClient, ownerId)
      setResult(cleared)
      setDialogOpen(false)
      setRefreshToken((value) => value + 1)
      notifySuccess('临时缓存已清理', `查询 ${cleared.queryEntries} 条 · 缩略图 ${cleared.thumbnailEntries} 项 · 播放器 ${cleared.playerEntries} 项 · 传输历史 ${cleared.transferHistoryEntries} 条`)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : '清理临时缓存失败，请稍后重试')
      notifyError('清理临时缓存失败')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div>
      <PageHeader
        title="存储与缓存"
        subtitle="清理 Web 临时缓存；不会删除未保存编辑、草稿、笔记材料索引、登录状态或服务器配置"
        actions={
          <Button variant="secondary" size="sm" onClick={refresh}>
            <RefreshCw className="size-4" aria-hidden /> 重新统计
          </Button>
        }
      />

      <div className="max-w-[920px] space-y-4">
        <InlineAlert tone="info">
          Web 只清理浏览器临时缓存。Android 与 Windows 的本机材料清理、冲突保留和引用保护不在本页范围；笔记材料仍以服务器为唯一事实源。
        </InlineAlert>

        <p className="flex flex-wrap items-center gap-1.5 text-xs text-text-3">
          <Server className="size-3.5 shrink-0" aria-hidden />
          当前服务器：<span className="mono break-all text-text-2">{serverLabel(snapshot.serverBinding)}</span>
        </p>

        <section aria-labelledby="cache-clearable-heading">
          <div className="mb-2 flex items-end justify-between gap-3">
            <div>
              <h2 id="cache-clearable-heading" className="text-sm font-semibold text-text-1">临时缓存</h2>
              <p className="mt-1 text-xs text-text-3">共 {clearableCount} 项可清理；清理后页面会按需重新读取服务器。</p>
            </div>
            <Button variant="primary" size="sm" onClick={openCleanup}>
              <Trash2 className="size-4" aria-hidden /> 清理本机缓存
            </Button>
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <CountCard
              icon={RefreshCw}
              title="查询缓存"
              count={snapshot.queryEntries}
              unit="条"
              description="页面内存中的服务器查询结果；清理后需要时重新读取。"
            />
            <CountCard
              icon={Image}
              title="图片缩略图"
              count={snapshot.thumbnailEntries}
              unit="项"
              description="文件列表缩略图的对象 URL 缓存；不包含笔记照片草稿。"
            />
            <CountCard
              icon={Play}
              title="播放器缓存"
              count={snapshot.playerEntries}
              unit="项"
              description="已读取音频的对象 URL 缓存；不包含待上传录音。"
            />
            <CountCard
              icon={FileClock}
              title="普通文件传输历史"
              count={snapshot.transferHistoryEntries}
              unit="条"
              description="文件上传完成/失败记录；进行中的上传队列保留。"
            />
          </div>
        </section>

        <section aria-labelledby="cache-protected-heading">
          <div className="mb-2 flex items-center gap-2">
            <ShieldCheck className="size-4 text-ok" aria-hidden />
            <h2 id="cache-protected-heading" className="text-sm font-semibold text-text-1">受保护内容</h2>
            <StatusBadge tone={protectedCount > 0 ? 'warn' : 'ok'} dot={false}>
              {protectedCount > 0 ? `${protectedCount} 项需要留意` : '当前无未保存内容'}
            </StatusBadge>
          </div>
          <Card className="space-y-3 p-4">
            <p className="text-xs leading-5 text-text-3">
              临时缓存清理不会删除以下内容。这里只列出当前账号、当前仍挂载页面中的未保存编辑与本机 AI 草稿；清理前会列出对象和保存入口，并要求明确确认保留。
            </p>
            {snapshot.unsavedDrafts.length === 0 && snapshot.aiDrafts.length === 0 ? (
              <p className="rounded-ctl bg-surface px-3 py-2 text-[13px] text-text-2">没有检测到页面内未保存编辑或本机 AI 草稿。</p>
            ) : (
              <ul className="space-y-2">
                {snapshot.unsavedDrafts.map((draft) => (
                  <li key={draft.id} className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2 text-[13px]">
                    <div className="flex flex-wrap items-center gap-2">
                      <StatusBadge tone="warn" dot={false}>未保存编辑</StatusBadge>
                      <span className="font-medium text-text-1">{draft.title}</span>
                    </div>
                    <p className="mt-1 text-text-2">{draft.detail}</p>
                    <p className="mt-1 text-xs text-text-3">{draft.saveHint}</p>
                    {draft.saveHref && <Link className="mt-1 inline-block text-xs text-primary hover:underline" to={draft.saveHref}>去保存</Link>}
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
                      <p className="mt-1 break-all text-xs text-text-3">
                        来源版本 {draft.binding.sourceVersionId || '未知'} · {draft.updatedAt ? formatTime(draft.updatedAt) : '记录损坏'}
                      </p>
                      <p className="mt-1 break-all text-xs text-text-3">草稿服务器：{serverLabel(draft.binding.serverBinding)}</p>
                      <p className="mt-1 text-xs text-text-3">
                        {otherServer
                          ? '此草稿属于其他服务器；请先切换到该服务器，再从笔记处理页核对保存。'
                          : '打开笔记处理页核对并保存人工修改；AI 草稿不会随临时缓存清理删除。'}
                      </p>
                      {draft.binding.sourceVersionId && !otherServer && (
                        <Link
                          className="mt-1 inline-block text-xs text-primary hover:underline"
                          to={`/native-notes/processing?version=${encodeURIComponent(draft.binding.sourceVersionId)}`}
                        >
                          去保存
                        </Link>
                      )}
                    </li>
                  )
                })}
              </ul>
            )}
            <div className="rounded-ctl bg-surface px-3 py-2 text-xs leading-5 text-text-3">
              始终保留：笔记正文、手写、笔记照片与图片草稿、AI 草稿、本机持久笔记材料索引、文件传输队列、登录令牌、服务器地址和账号身份。
            </div>
          </Card>
        </section>

        {result && (
          <Card className="p-4" role="status">
            <div className="flex flex-wrap items-center gap-2">
              <CardTitle>上次清理回执</CardTitle>
              <StatusBadge tone="ok" dot={false}>已完成</StatusBadge>
              <span className="ml-auto text-xs text-text-3">{formatTime(result.clearedAt)}</span>
            </div>
            <p className="mt-2 text-[13px] text-text-2">
              已清理查询缓存 {result.queryEntries} 条、缩略图 {result.thumbnailEntries} 项、播放器缓存 {result.playerEntries} 项、传输历史 {result.transferHistoryEntries} 条。
            </p>
            <p className="mt-1 text-xs text-text-3">
              清理时保留未保存编辑/草稿 {result.preservedUnsavedDrafts} 项、AI 草稿 {result.preservedAiDrafts} 份。第二次执行不会重复撤销已清理对象。
            </p>
            <p className="mt-1 break-all text-xs text-text-3">清理时服务器：{serverLabel(result.serverBinding)}</p>
            {result.queryEntries + result.thumbnailEntries + result.playerEntries + result.transferHistoryEntries === 0 && (
              <p className="mt-1 text-xs text-text-3">这次没有可清理的临时缓存；当前页面需要的查询会按需重新建立。</p>
            )}
          </Card>
        )}

        {error && <InlineAlert tone="crit" title="清理未完成">{error}</InlineAlert>}
      </div>

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent className="max-w-[560px]">
          <DialogHeader
            title="清理本机临时缓存？"
            description="只清理查询、缩略图、播放器和普通文件传输历史；未保存内容与 AI 草稿不会被删除。"
          />
          <DialogBody className="space-y-3">
            <div className="rounded-ctl border border-border bg-surface px-3 py-2.5 text-[13px] text-text-2">
              将清理查询缓存 {snapshot.queryEntries} 条、缩略图 {snapshot.thumbnailEntries} 项、播放器缓存 {snapshot.playerEntries} 项、传输历史 {snapshot.transferHistoryEntries} 条。
            </div>
            {protectedCount > 0 ? (
              <>
                <div className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2.5 text-[13px] leading-5 text-text-2">
                  检测到 {snapshot.unsavedDrafts.length} 项未保存编辑/草稿和 {snapshot.aiDrafts.length} 份 AI 草稿。请先保存，或确认以下内容继续保留在当前页面/本机。
                </div>
                <ul className="max-h-52 space-y-2 overflow-y-auto">
                  {snapshot.unsavedDrafts.map((draft) => (
                    <li key={draft.id} className="rounded-ctl border border-border px-3 py-2 text-xs">
                      <div className="font-medium text-text-1">{draft.title}</div>
                      <div className="mt-1 text-text-3">{draft.detail}</div>
                      <div className="mt-1 text-text-3">{draft.saveHint}</div>
                      {draft.saveHref && <Link className="mt-1 inline-block text-primary hover:underline" to={draft.saveHref} onClick={() => setDialogOpen(false)}>去保存</Link>}
                    </li>
                  ))}
                  {snapshot.aiDrafts.map((draft) => {
                    const otherServer = belongsToOtherServer(draft.binding.serverBinding, snapshot.serverBinding)
                    return (
                      <li key={draft.key} className="rounded-ctl border border-border px-3 py-2 text-xs">
                        <div className="font-medium text-text-1">
                          AI 草稿 · {draft.binding.kind === 'IndependentAnalysis' ? '独立分析' : '完整记录'}
                          {otherServer && <span className="ml-1 text-text-3">（其他服务器）</span>}
                        </div>
                        <div className="mt-1 break-all text-text-3">来源版本 {draft.binding.sourceVersionId || '未知'}</div>
                        <div className="mt-1 break-all text-text-3">草稿服务器：{serverLabel(draft.binding.serverBinding)}</div>
                        {otherServer ? (
                          <div className="mt-1 text-text-3">请先切换到该服务器，再从笔记处理页保存。</div>
                        ) : draft.binding.sourceVersionId && (
                          <Link
                            className="mt-1 inline-block text-primary hover:underline"
                            to={`/native-notes/processing?version=${encodeURIComponent(draft.binding.sourceVersionId)}`}
                            onClick={() => setDialogOpen(false)}
                          >
                            去保存
                          </Link>
                        )}
                      </li>
                    )
                  })}
                </ul>
                <label className="flex items-start gap-2 rounded-ctl border border-border bg-bg px-3 py-2.5 text-xs leading-5 text-text-2">
                  <Checkbox
                    className="mt-0.5"
                    checked={preserveConfirmed}
                    onCheckedChange={setPreserveConfirmed}
                    ariaLabel="确认保留未保存编辑与 AI 草稿"
                  />
                  <span>我已知晓以上内容不会被清理，并选择只清理临时缓存。</span>
                </label>
              </>
            ) : (
              <p className="text-xs leading-5 text-text-3">
                没有检测到未保存编辑或 AI 草稿。登录状态、服务器配置和笔记材料索引不在清理范围。
              </p>
            )}
          </DialogBody>
          <DialogFooter>
            <Button variant="secondary" onClick={() => setDialogOpen(false)} disabled={busy}>取消</Button>
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
    </div>
  )
}
