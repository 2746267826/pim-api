import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton, Textarea } from '@/components/ui'
import { aiApi, type NoteAsrBatchJob, type NoteAsrBatchRetryFailedRequest, type NoteAsrTask } from './api'
import {
  clearNoteAsrBatchFailedIntent,
  loadNoteAsrBatchFailedIntent,
  saveNoteAsrBatchFailedIntent,
  type NoteAsrBatchFailedIntent,
  type NoteAsrBatchIntentState,
} from './note-asr-batch-intent'
import { BatchMonitorPanel, type BatchJobView, type BatchStatusView } from './note-asr-routing'
import { createAiRequestId } from './request-id'

const dateTime = (value: string | null | undefined) => value ? new Date(value).toLocaleString() : '未知'

function batchStatus(job: NoteAsrBatchJob): BatchStatusView {
  switch (job.status.toUpperCase()) {
    case 'VALIDATING': return 'validating'
    case 'CREATED': case 'QUEUED': return 'waiting'
    case 'IN_PROGRESS': case 'RUNNING': return 'running'
    case 'COMPLETED':
      return job.outputImported && !job.lastErrorCode && !((job.failedItems ?? 0) > 0)
        ? 'completed' : 'partial'
    case 'FAILED': return 'failed'
    case 'EXPIRED': return 'expired'
    case 'CANCELLING': return 'cancelling'
    case 'CANCELLED': return 'cancelled'
    default: return 'unknown'
  }
}

function toView(job: NoteAsrBatchJob): BatchJobView {
  return {
    id: job.id,
    title: `Batch ${job.model}`,
    status: batchStatus(job),
    createdAtLabel: dateTime(job.createdAt),
    destinationLabel: job.baseUrl,
    completionWindowLabel: job.completionWindow,
    cashEstimateLabel: '仅成功请求按 Token 消耗现金；未执行项不计费',
    progressLabel: `${job.completedItems ?? 0}/${job.totalItems ?? 0} 项`,
    totalItems: job.totalItems,
    completedItems: job.completedItems,
    failedItems: job.failedItems,
    providerRequestId: job.batchId,
    inputExpiresAtLabel: dateTime(job.inputExpireAt),
    batchExpiresAtLabel: dateTime(job.batchExpiresAt),
    outputAvailable: Boolean(job.outputFileId),
    errorLabel: job.lastErrorMessage ?? undefined,
  }
}

const retryableTerminal = (job: NoteAsrBatchJob) => {
  if (!job.batchId || !['Failed', 'Expired', 'Cancelled', 'Completed'].includes(job.status)) return false
  return job.status !== 'Completed' || (job.failedItems ?? 0) > 0 || Boolean(job.lastErrorCode)
}

/** Owner-visible Batch monitor and failed-only retry. It never reads admin stores or raw JSONL. */
export function NoteAsrOwnerBatches({ task, userId, serverBinding, onCreated }: {
  task: NoteAsrTask
  userId: string
  serverBinding: string
  onCreated?: (task: NoteAsrTask) => void
}) {
  const qc = useQueryClient()
  const [pollError, setPollError] = useState('')
  const jobs = useQuery({
    queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'],
    queryFn: ({ signal }) => aiApi.asrBatchJobs(signal),
    refetchInterval: 15_000,
  })
  const taskJobs = useMemo(() => (jobs.data ?? []).filter((job) => job.taskId === task.taskId), [jobs.data, task.taskId])
  const views = useMemo(() => taskJobs.map(toView), [taskJobs])
  const eligible = useMemo(() => taskJobs.filter(retryableTerminal), [taskJobs])

  async function poll(id: string) {
    setPollError('')
    try {
      await aiApi.asrBatchPoll(id)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'] })
    } catch (error) {
      setPollError(error instanceof Error ? error.message : '查询失败')
      throw error
    }
  }

  async function cancel(id: string) {
    setPollError('')
    try {
      await aiApi.asrBatchCancel(id)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'] })
    } catch (error) {
      setPollError(error instanceof Error ? error.message : '取消失败')
      throw error
    }
  }

  return <div className="min-w-0 space-y-3">
    {pollError && <InlineAlert tone="crit" title="Batch 操作未确认">{pollError}；记录保持不变，不会自动重建或假报成功。</InlineAlert>}
    <BatchMonitorPanel jobs={views} loading={jobs.isPending}
      loadError={jobs.isError ? 'Batch 任务读取失败；不会自动重建或重发' : null}
      onPoll={poll} onCancel={cancel} />
    {eligible.length > 0 && <BatchFailedRetry task={task} jobs={eligible} userId={userId}
      serverBinding={serverBinding} onCreated={onCreated} />}
  </div>
}

function BatchFailedRetry({ task, jobs, userId, serverBinding, onCreated }: {
  task: NoteAsrTask
  jobs: NoteAsrBatchJob[]
  userId: string
  serverBinding: string
  onCreated?: (task: NoteAsrTask) => void
}) {
  const qc = useQueryClient()
  const [jobId, setJobId] = useState('')
  const [kind, setKind] = useState<'manual' | 'quality'>('manual')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const job = jobs.find((value) => value.id === jobId) ?? null
  const [intentState, setIntentState] = useState<NoteAsrBatchIntentState<NoteAsrBatchFailedIntent>>({ kind: 'none' })
  const intent = intentState.kind === 'ready' ? intentState.value : null
  const blockedIntent = intentState.kind === 'blocked' ? intentState.message : ''
  const config = useQuery({
    queryKey: ['ai', userId, serverBinding, 'asr-route-config'],
    queryFn: ({ signal }) => aiApi.asrRouteConfig(signal),
  })
  const destinations = useQuery({
    queryKey: ['ai', userId, serverBinding, 'note-asr-destinations'],
    queryFn: ({ signal }) => aiApi.noteAsrDestinations(signal),
  })
  const destination = destinations.data?.items.find((value) =>
    value.modelId === task.destination?.modelId ||
    (!task.destination && value.model === job?.model)) ?? null
  const canConfirm = Boolean(job && destination && config.data && reason.trim() && !busy && !intent && !blockedIntent)
  const canSubmit = Boolean(job && destination && config.data && reason.trim() && confirmed && !busy &&
    !intent && !blockedIntent)

  useEffect(() => {
    const next = jobs[0]?.id ?? ''
    setJobId(next)
    setIntentState(next ? loadNoteAsrBatchFailedIntent(userId, serverBinding, next) : { kind: 'none' })
    setKind('manual')
    setReason('')
    setConfirmed(false)
    setNotice('')
    setError('')
  }, [jobs, serverBinding, userId])

  function selectJob(next: string) {
    setJobId(next)
    setIntentState(next ? loadNoteAsrBatchFailedIntent(userId, serverBinding, next) : { kind: 'none' })
    setConfirmed(false)
    setNotice('')
    setError('')
  }

  function createIntent(): NoteAsrBatchFailedIntent | null {
    if (!job || !destination || !config.data) return null
    const request: NoteAsrBatchRetryFailedRequest = {
      clientTaskId: createAiRequestId(),
      sourceJobId: job.id,
      expectedUserId: userId,
      route: {
        route: 'cloud-multimodal',
        expectedConfigRevision: config.data.revision,
        expectedConsentVersion: config.data.consentVersion,
        executionMode: 'batch',
        batchScopeConfirmed: true,
        confirmed: true,
      },
      target: {
        modelId: destination.modelId,
        modelRevision: destination.modelRevision,
        consentRef: job.consentRef,
        confirmed: true,
        batchScopeConfirmed: true,
        completionWindow: job.completionWindow,
      },
      reason: { kind, text: reason.trim() },
    }
    return { kind: 'failed', expectedUserId: userId, sourceTaskId: task.taskId, request }
  }

  async function submit(frozen: NoteAsrBatchFailedIntent) {
    setBusy(true)
    setNotice('')
    setError('')
    try {
      const result = await aiApi.asrBatchRetryFailed(frozen.request.sourceJobId, frozen.request)
      clearNoteAsrBatchFailedIntent(userId, serverBinding, frozen.request.sourceJobId, frozen.request.clientTaskId)
      setIntentState({ kind: 'none' })
      setConfirmed(false)
      setNotice(`失败项子任务已按原标识提交：${result.task.taskId}。成功分片和旧历史保留，只重新发送失败或缺失项。`)
      onCreated?.(result.task)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'] })
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'note-asr'] })
    } catch (failure) {
      setError(`${failure instanceof Error ? failure.message : '失败项 Batch 重试未确认'}。已冻结原提交标识，不会新建子任务、自动重试或整体重发。`)
    } finally {
      setBusy(false)
    }
  }

  async function rerun() {
    if (!canSubmit) return
    const frozen = createIntent()
    if (!frozen) return
    const storageError = saveNoteAsrBatchFailedIntent(userId, serverBinding, frozen.request.sourceJobId, frozen)
    if (storageError) {
      setError(storageError)
      return
    }
    setIntentState({ kind: 'ready', value: frozen })
    await submit(frozen)
  }

  async function reconcile() {
    if (!intent || busy) return
    setBusy(true)
    setNotice('')
    setError('')
    try {
      const [child, latest] = await Promise.all([
        aiApi.noteAsrTaskOrNull(intent.request.clientTaskId),
        aiApi.asrBatchJobs(),
      ])
      const job = latest.find((value) => value.taskId === intent.request.clientTaskId && value.batchId)
      if (child && job) {
        clearNoteAsrBatchFailedIntent(userId, serverBinding, intent.request.sourceJobId, intent.request.clientTaskId)
        setIntentState({ kind: 'none' })
        setNotice(`已找到失败项子任务及其 Batch 标识，没有重新发送。子任务：${child.taskId}`)
        onCreated?.(child)
        await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-batch-jobs'] })
      } else {
        setNotice('尚未同时确认子任务与 Batch 标识。保留原提交标识，不会重新分配或自动重发。')
      }
    } catch (failure) {
      setError(`${failure instanceof Error ? failure.message : '核对失败'}。保留原提交标识，请刷新后重试。`)
    } finally {
      setBusy(false)
    }
  }

  if (config.isPending || destinations.isPending) return <Skeleton className="h-36" />
  return <Card className="min-w-0 space-y-3 p-4 sm:p-5">
    <div>
      <h3 className="text-sm font-semibold text-text-1">失败项 Batch 重试</h3>
      <p className="mt-1 break-words text-xs text-text-3">只提交原 Batch 的失败或缺失分片。成功分片、旧任务、custom id 和历史记录均保留；不会整体重发。</p>
    </div>
    {config.isError || destinations.isError
      ? <InlineAlert tone="crit" title="目标读取失败">无法核对当前路由或共享目标；不会自动改用其他执行位置。</InlineAlert>
      : <>
        <div className="max-w-md"><Label htmlFor="note-asr-batch-retry-job">原 Batch 任务</Label>
          <Select id="note-asr-batch-retry-job" ariaLabel="原 Batch 任务" value={jobId}
            disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
            options={jobs.map((value) => ({
              value: value.id,
              label: `${dateTime(value.createdAt)} · 失败 ${value.failedItems ?? '未知'} / ${value.totalItems ?? '未知'} · ${value.status}`,
            }))}
            onValueChange={selectJob} /></div>
        {!destination && <InlineAlert tone="warn" title="原 Batch 目标当前不可用">
          必须在当前共享目录中精确匹配原模型修订；不会猜测或改用其他付费目标。
        </InlineAlert>}
        {destination && config.data && <div className="rounded-ctl border border-border p-3 text-xs leading-5 text-text-2">
          <p className="break-words">{destination.displayName} · {destination.model}</p>
          <p className="break-all font-mono">{destination.endpoint}</p>
          <p>失败项会按当前配置版本 {config.data.consentVersion} 重新确认；Batch 可能长等待并按成功 Token 消耗现金。</p>
        </div>}
        <div className="max-w-sm"><Label htmlFor="note-asr-batch-retry-kind">重试原因</Label>
          <Select id="note-asr-batch-retry-kind" ariaLabel="重试原因" value={kind}
            options={[{ value: 'manual', label: '人工核对失败项' }, { value: 'quality', label: '部分结果质量需要重做' }]}
            disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
            onValueChange={(value) => { setKind(value as 'manual' | 'quality'); setConfirmed(false) }} /></div>
        <div><Label htmlFor="note-asr-batch-retry-reason">原因说明</Label>
          <Textarea id="note-asr-batch-retry-reason" value={reason} maxLength={200}
            disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
            placeholder="例如：已核对原 Batch 错误文件，只恢复失败分片"
            onChange={(event) => { setReason(event.target.value); setConfirmed(false) }} /></div>
      </>}
    {blockedIntent && <InlineAlert tone="crit" title="原失败项提交状态无法确认">{blockedIntent}</InlineAlert>}
    {intent ? <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs text-text-2">
      <p className="break-all">已冻结失败项子任务标识：<span className="font-mono">{intent.request.clientTaskId}</span>。原 Batch 与原因不可静默更改。</p>
      <p>原 Batch：<span className="font-mono">{intent.request.sourceJobId}</span> · {intent.request.reason.kind} · {intent.request.reason.text}</p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" loading={busy} onClick={() => void reconcile()}>核对子任务与 Batch 标识</Button>
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => void submit(intent)}>按原标识重新提交失败项</Button>
      </div>
    </div> : <>
      <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox ariaLabel="确认只重试Batch失败分片并产生现金费用" checked={confirmed}
          disabled={!canConfirm} onCheckedChange={setConfirmed} />
        <span>确认只重新提交失败或缺失项，理解闲时长等待与可能的新现金费用；成功项不会重发。</span>
      </label>
      <Button variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void rerun()}>创建失败项 Batch 子任务</Button>
    </>}
    {notice && <InlineAlert tone="ok">{notice}</InlineAlert>}
    {error && <InlineAlert tone="crit" title="失败项 Batch 重试未确认">{error}</InlineAlert>}
  </Card>
}
