import { useState } from 'react'
import { Button, Card, Checkbox, EmptyState, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import type { BatchJobView, BatchStatusView } from './types'

const statusMeta: Record<BatchStatusView, { label: string; tone: 'ok' | 'warn' | 'crit' | 'info' | 'neutral' }> = {
  validating: { label: '校验中', tone: 'info' },
  waiting: { label: '供应商排队中', tone: 'info' },
  running: { label: '运行中', tone: 'info' },
  partial: { label: '部分成功', tone: 'warn' },
  unknown: { label: 'UNKNOWN 待人工核对', tone: 'crit' },
  cancelling: { label: '取消请求已发送', tone: 'warn' },
  cancelled: { label: '已取消', tone: 'neutral' },
  completed: { label: '已完成', tone: 'ok' },
  failed: { label: '失败', tone: 'crit' },
  expired: { label: '已过期', tone: 'warn' },
}

export interface BatchMonitorPanelProps {
  jobs: BatchJobView[]
  onPoll?: (id: string) => void | Promise<void>
  onCancel?: (id: string) => void | Promise<void>
  loading?: boolean
  loadError?: string | null
  readOnly?: boolean
  disabled?: boolean
  pageSize?: number
}

export function BatchMonitorPanel({
  jobs,
  onPoll,
  onCancel,
  loading = false,
  loadError = null,
  readOnly = false,
  disabled = false,
  pageSize = 5,
}: BatchMonitorPanelProps) {
  const [page, setPage] = useState(1)
  const [cancelId, setCancelId] = useState<string | null>(null)
  const [cancelConfirmed, setCancelConfirmed] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [notice, setNotice] = useState('')
  const [actionError, setActionError] = useState('')
  const boundedPageSize = Math.max(1, pageSize)
  const totalPages = Math.max(1, Math.ceil(jobs.length / boundedPageSize))
  const currentPage = Math.min(page, totalPages)
  const visible = jobs.slice((currentPage - 1) * boundedPageSize, currentPage * boundedPageSize)

  async function poll(job: BatchJobView) {
    if (!onPoll || busyId || disabled || readOnly) return
    setBusyId(job.id)
    setNotice('')
    setActionError('')
    try {
      await onPoll(job.id)
      setNotice('已读取最新状态；页面只展示供应商确认后的状态。')
    } catch (error) {
      setActionError(`${error instanceof Error ? error.message : '查询失败'}。当前记录保持不变，不会自动新建或重发任务。`)
    } finally {
      setBusyId(null)
    }
  }

  async function cancel() {
    const job = jobs.find((value) => value.id === cancelId)
    if (!job || !onCancel || !cancelConfirmed || busyId || disabled || readOnly) return
    setBusyId(job.id)
    setActionError('')
    try {
      await onCancel(job.id)
      setNotice('取消请求已发送；这是异步操作，最终状态仍以供应商后续查询为准，页面不会直接标记完成或成功。')
      setCancelId(null)
      setCancelConfirmed(false)
    } catch (error) {
      setActionError(`${error instanceof Error ? error.message : '取消失败'}。任务状态保持不变，请刷新核对。`)
    } finally {
      setBusyId(null)
    }
  }

  if (loading) return <Skeleton className="h-48" />
  if (loadError) return <InlineAlert tone="crit" title="Batch任务加载失败">{loadError}；不会自动重建、重发或假定成功。</InlineAlert>
  if (!jobs.length) return <EmptyState size="sm" title="还没有Batch任务" description="Batch用于闲时批量推理，可能长时间等待，并仅按成功请求产生现金费用。" />

  return <Card className="min-w-0 space-y-3 p-4 sm:p-5">
    <div className="flex min-w-0 flex-wrap items-start justify-between gap-2">
      <div className="min-w-0">
        <h2 className="text-sm font-semibold text-text-1">MiMo Batch 长任务</h2>
        <p className="mt-1 break-words text-xs text-text-3">当前为开发契约预览，不是已发布用户能力；批量音频尚未获得真实付费验收，界面保持 NOT-VERIFIED。没有供应商任务标识时只做人工核对，禁止自动重建。</p>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {jobs.length > boundedPageSize && <span className="text-xs text-text-3">第 {currentPage} / {totalPages} 页</span>}
        <StatusBadge size="xs" tone="warn" dot={false}>音频能力 NOT-VERIFIED</StatusBadge>
      </div>
    </div>
    {notice && <InlineAlert tone="info" title="操作状态">{notice}</InlineAlert>}
    {actionError && <InlineAlert tone="crit" title="操作未确认">{actionError}</InlineAlert>}
    <div className="space-y-3">{visible.map((job) => {
      const meta = statusMeta[job.status]
      const unknownWithoutId = job.status === 'unknown' && !job.providerRequestId
      return <section key={job.id} className="min-w-0 rounded-ctl border border-border p-3" aria-label={`Batch任务 ${job.title}`}>
        <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
          <h3 className="min-w-0 break-words text-sm font-medium text-text-1">{job.title}</h3>
          <StatusBadge size="xs" dot={false} tone={meta.tone}>{meta.label}</StatusBadge>
        </div>
        <div className="mt-2 grid min-w-0 gap-1 text-xs text-text-3 sm:grid-cols-2">
          <p className="break-words">{job.createdAtLabel} · {job.destinationLabel}</p>
          <p className="break-words">完成窗口 {job.completionWindowLabel} · {job.cashEstimateLabel}</p>
          <p className="break-words">进度 {job.progressLabel}</p>
          <p className="break-words">成功 {job.completedItems ?? '未知'} / 失败 {job.failedItems ?? '未知'} / 总数 {job.totalItems ?? '未知'}</p>
          <p className="break-words">输入文件保留至 {job.inputExpiresAtLabel ?? '未知'}</p>
          <p className="break-words">Batch任务截止 {job.batchExpiresAtLabel ?? '未知'}</p>
        </div>
        {job.providerRequestId
          ? <p className="mt-2 break-all font-mono text-[11px] text-text-4">供应商任务标识：{job.providerRequestId}</p>
          : <InlineAlert tone="warn" title="没有可核对的供应商任务标识" className="mt-2">保持 UNKNOWN，请到MiMo平台按提交时间、文件与请求信息人工核对；不得自动创建新任务、重新上传或假定成功。</InlineAlert>}
        {job.status === 'partial' && <InlineAlert tone="warn" title="部分成功" className="mt-2">
          成功请求已经产生费用并保留结果；失败请求不会自动续跑或整体重发，需逐项核对后人工决定。
        </InlineAlert>}
        {unknownWithoutId && <p className="mt-2 break-words text-xs text-crit">当前页面只提供历史与证据，不会因缺少标识而生成新的付费任务。</p>}
        {job.outputAvailable && <p className="mt-2 text-xs text-ok">已取得输出文件，仍需核对 UNKNOWN/失败项后才能发布完整结果。</p>}
        {job.errorLabel && <p className="mt-2 break-words text-xs text-warn">{job.errorLabel}</p>}
        <div className="mt-3 flex flex-wrap gap-2">
          {job.providerRequestId && onPoll && <Button size="sm" loading={busyId === job.id} disabled={disabled || readOnly} onClick={() => void poll(job)}>立即查询状态</Button>}
          {onCancel && job.status !== 'cancelled' && job.status !== 'completed' && job.status !== 'partial' &&
            job.status !== 'failed' && job.status !== 'expired' &&
            (job.status !== 'unknown' || Boolean(job.providerRequestId)) &&
            <Button size="sm" variant="danger-soft" disabled={disabled || readOnly || busyId != null} onClick={() => {
              setCancelId(job.id)
              setCancelConfirmed(false)
              setNotice('')
            }}>请求取消</Button>}
        </div>
        {cancelId === job.id && <div className="mt-3 space-y-2 rounded-ctl border border-crit-border bg-crit-soft p-3">
          <p className="break-words text-xs text-crit">取消是异步请求；已成功请求仍可能计费并生成部分结果。确认后不代表任务已完成。</p>
          <label className="flex items-start gap-2 text-xs text-text-2">
            <Checkbox checked={cancelConfirmed} onCheckedChange={setCancelConfirmed} ariaLabel={`确认请求取消 ${job.title}`} disabled={busyId != null} />
            <span>确认请求供应商取消该任务，并理解最终状态仍需后续核对</span>
          </label>
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="danger" loading={busyId === job.id} disabled={!cancelConfirmed || busyId != null} onClick={() => void cancel()}>确认异步取消</Button>
            <Button size="sm" variant="ghost" disabled={busyId != null} onClick={() => { setCancelId(null); setCancelConfirmed(false) }}>返回</Button>
          </div>
        </div>}
      </section>
    })}</div>
    {jobs.length > boundedPageSize && <div className="flex flex-wrap justify-end gap-2">
      <Button size="sm" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>上一页Batch</Button>
      <Button size="sm" disabled={currentPage >= totalPages} onClick={() => setPage(currentPage + 1)}>下一页Batch</Button>
    </div>}
    {readOnly && <p className="text-xs text-text-3">只读模式不会轮询、取消或创建Batch任务。</p>}
  </Card>
}
