import { useState } from 'react'
import { Button, Card, Checkbox, EmptyState, InlineAlert, Label, Skeleton, Textarea } from '@/components/ui'
import type { RecoveryItemView, RecoveryRequestView } from './types'

const retryable = (item: RecoveryItemView) => item.status === 'failed' || item.status === 'unknown' || item.status === 'queued'
const statusLabel: Record<RecoveryItemView['status'], string> = {
  succeeded: '已成功（不可重跑）',
  failed: '失败',
  unknown: '结果待核对',
  queued: '等待处理',
  running: '执行中（不可重跑）',
}

export interface RetryPanelProps {
  items: RecoveryItemView[]
  onRetry?: (request: RecoveryRequestView) => void | Promise<void>
  loading?: boolean
  error?: string | null
  readOnly?: boolean
  disabled?: boolean
  pageSize?: number
}

export function RetryPanel({
  items,
  onRetry,
  loading = false,
  error = null,
  readOnly = false,
  disabled = false,
  pageSize = 8,
}: RetryPanelProps) {
  const [selected, setSelected] = useState<string[]>([])
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [page, setPage] = useState(1)
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState('')
  const boundedPageSize = Math.max(1, pageSize)
  const totalPages = Math.max(1, Math.ceil(items.length / boundedPageSize))
  const currentPage = Math.min(page, totalPages)
  const visible = items.slice((currentPage - 1) * boundedPageSize, currentPage * boundedPageSize)
  const canSubmit = selected.length > 0 && reason.trim().length > 0 && confirmed &&
    !readOnly && !disabled && !busy && Boolean(onRetry)

  function toggle(item: RecoveryItemView, checked: boolean) {
    if (!retryable(item)) return
    setSelected((current) => checked ? [...current, item.id] : current.filter((id) => id !== item.id))
    setConfirmed(false)
  }

  async function submit() {
    if (!canSubmit || !onRetry) return
    setBusy(true)
    setActionError('')
    try {
      await onRetry({
        itemIds: selected,
        reason: reason.trim(),
        possibleDuplicateCharge: selected.some((id) => items.find((item) => item.id === id)?.status === 'unknown'),
        confirmed: true,
      })
      setSelected([])
      setReason('')
      setConfirmed(false)
    } catch (failure) {
      setActionError(`${failure instanceof Error ? failure.message : '重跑失败'}。旧结果与执行历史继续保留，请先核对，页面不会自动重发。`)
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <Skeleton className="h-40" />
  if (!items.length && !error) return <EmptyState size="sm" title="没有可恢复分片" description="成功结果不可重跑；失败或结果未知的分片会在核对后显示。" />

  return <Card className="min-w-0 space-y-3 p-4 sm:p-5">
    <div>
      <h2 className="text-sm font-semibold text-text-1">选择性重跑</h2>
      <p className="mt-1 break-words text-xs text-text-3">只允许选择失败、排队或待核对分片。成功结果、运行中分片和旧历史都不可修改。</p>
    </div>
    {error && <InlineAlert tone="crit" title="分片状态读取失败">{error}；不会自动选择或重跑任何分片。</InlineAlert>}
    <div className="space-y-2">{visible.map((item) => {
      const selectable = retryable(item)
      return <div key={item.id} className="min-w-0 rounded-ctl border border-border p-3">
        <label className={`flex min-w-0 items-start gap-2 ${selectable ? '' : 'opacity-60'}`}>
          <Checkbox
            checked={selected.includes(item.id)}
            onCheckedChange={(checked) => toggle(item, checked)}
            disabled={!selectable || busy || disabled}
            ariaLabel={`选择重跑：${item.label}`}
          />
          <span className="min-w-0">
            <span className="block break-words text-sm text-text-1">{item.label}</span>
            <span className="mt-0.5 block break-words text-xs text-text-3">{statusLabel[item.status]}</span>
            {item.resultPreview && <span className="mt-1 block break-words text-xs text-text-2">保留结果：{item.resultPreview}</span>}
            {item.failureLabel && <span className="mt-1 block break-words text-xs text-warn">{item.failureLabel}</span>}
          </span>
        </label>
      </div>
    })}</div>
    {items.length > boundedPageSize && <div className="flex flex-wrap justify-end gap-2">
      <Button size="sm" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>上一页分片</Button>
      <Button size="sm" disabled={currentPage >= totalPages} onClick={() => setPage(currentPage + 1)}>下一页分片</Button>
    </div>}
    {selected.length > 0 && <div className="space-y-3 rounded-ctl border border-border p-3">
      <div>
        <Label htmlFor="note-asr-rerun-reason">重跑原因</Label>
        <Textarea
          id="note-asr-rerun-reason"
          value={reason}
          maxLength={500}
          placeholder="例如：上次结果超时，已核对原音频与旧记录"
          disabled={busy || disabled || readOnly}
          onChange={(event) => setReason(event.target.value)}
        />
      </div>
      <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox checked={confirmed} onCheckedChange={setConfirmed} disabled={busy || disabled || readOnly} ariaLabel="确认选择性重跑并保留旧历史" />
        <span>确认只重跑所选分片；旧结果和历史保留。结果未知的分片可能已计费，重跑可能产生额外费用。</span>
      </label>
      <Button loading={busy} disabled={!canSubmit} onClick={() => void submit()}>确认重跑所选分片</Button>
    </div>}
    {actionError && <InlineAlert tone="crit" title="重跑未确认">{actionError}</InlineAlert>}
    {readOnly && <p className="text-xs text-text-3">只读模式不会发起重跑。</p>}
  </Card>
}
