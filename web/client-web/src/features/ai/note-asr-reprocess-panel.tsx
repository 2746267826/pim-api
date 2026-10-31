import { useEffect, useState } from 'react'
import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton, StatusBadge, Textarea } from '@/components/ui'
import { aiApi, type NoteAsrAttemptDetail, type NoteAsrReprocessRequest, type NoteAsrTask } from './api'
import { createAiRequestId } from './request-id'
import { actualCloudRoute, isActualCloudRoute } from './route-config-adapter'
import {
  clearNoteAsrReprocessIntent,
  loadNoteAsrReprocessIntent,
  saveNoteAsrReprocessIntent,
  type NoteAsrReprocessIntentState,
} from './note-asr-reprocess-intent'

const dateTime = (value: string) => new Date(value).toLocaleString()
const phaseLabel: Record<string, string> = {
  queued: '等待处理', running: '转写中', succeeded: '转写完成', needsattention: '有分片需处理',
  failed: '失败', stopped: '已停止', unknown: '结果待核对', cancelled: '已取消',
}
const label = (value: string) => phaseLabel[value.toLowerCase()] ?? value

export function NoteAsrReprocessPanel({ task, userId, serverBinding, onCreated }: {
  task: NoteAsrTask
  userId: string
  serverBinding: string
  onCreated?: (taskId: string) => void
}) {
  const qc = useQueryClient()
  const [offset, setOffset] = useState(0)
  const [targetId, setTargetId] = useState('last')
  const [kind, setKind] = useState<'manual' | 'quality'>('manual')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [intentState, setIntentState] = useState<NoteAsrReprocessIntentState>(() =>
    loadNoteAsrReprocessIntent(userId, serverBinding, task.taskId))
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [actionError, setActionError] = useState('')
  const config = useQuery({ queryKey: ['ai', userId, serverBinding, 'asr-route-config'], queryFn: ({ signal }) => aiApi.asrRouteConfig(signal) })
  // Public per-account destinations; ordinary owners never read admin model keys.
  const destinations = useQuery({
    queryKey: ['ai', userId, serverBinding, 'note-asr-destinations'],
    queryFn: ({ signal }) => aiApi.noteAsrDestinations(signal),
  })
  const history = useQuery({
    queryKey: ['ai', userId, serverBinding, 'reprocess-history', task.taskId, offset],
    queryFn: ({ signal }) => aiApi.asrReprocessHistory(task.taskId, userId, offset, 10, signal),
  })
  const attempts = useQuery({
    queryKey: ['ai', userId, serverBinding, 'asr-attempts', task.taskId],
    queryFn: ({ signal }) => aiApi.asrRoutingAttempts(task.taskId, signal),
  })

  const lastDestination = destinations.data?.items.find((item) => item.modelId === task.destination?.modelId) ?? null
  const intent = intentState.kind === 'ready' ? intentState.value : null
  const blockedIntent = intentState.kind === 'blocked' ? intentState.message : ''
  const selectedDestination = targetId === 'local'
    ? null
    : targetId === 'last'
      ? lastDestination
      : destinations.data?.items.find((item) => item.modelId === targetId) ?? null
  const selectedRoute = targetId === 'local' || (targetId === 'last' && task.route === 'local')
    ? 'local'
    : selectedDestination ? actualCloudRoute(selectedDestination.presetId) : ''
  const lastCloudInvalid = targetId === 'last' && isActualCloudRoute(task.route ?? '') && !lastDestination
  const targetReady = Boolean(config.data && selectedRoute)
  const canSubmit = Boolean(targetReady && reason.trim() && confirmed && !busy && !intent && !blockedIntent)

  useEffect(() => {
    setIntentState(loadNoteAsrReprocessIntent(userId, serverBinding, task.taskId))
    setTargetId('last')
    setKind('manual')
    setReason('')
    setConfirmed(false)
    setNotice('')
    setActionError('')
  }, [userId, serverBinding, task.taskId])

  function createIntent(): NoteAsrReprocessRequest | null {
    if (!config.data || !selectedRoute) return null
    const common = {
      clientTaskId: createAiRequestId(),
      expectedUserId: userId,
      expectedRevision: task.revision,
      reason: { kind, text: reason.trim() },
      route: {
        route: selectedRoute,
        expectedConfigRevision: config.data.revision,
        expectedConsentVersion: config.data.consentVersion,
        executionMode: 'realtime',
        confirmed: true,
        batchScopeConfirmed: false,
      },
    }
    return selectedRoute === 'local' || !selectedDestination
      ? common
      : {
        ...common,
        consent: {
          modelId: selectedDestination.modelId,
          modelRevision: selectedDestination.modelRevision,
          endpoint: selectedDestination.endpoint,
          presetId: selectedDestination.presetId,
          presetVersion: selectedDestination.presetVersion,
          confirmed: true,
        },
      }
  }

  async function submit(body: NoteAsrReprocessRequest) {
    setBusy(true)
    setNotice('')
    setActionError('')
    try {
      const result = await aiApi.asrReprocess(task.taskId, body)
      clearNoteAsrReprocessIntent(userId, serverBinding, task.taskId, body.clientTaskId)
      setIntentState({ kind: 'none' })
      setNotice(`已按原标识提交重处理；原任务与历史结果保持不变。新任务：${result.task.taskId}`)
      setConfirmed(false)
      setReason('')
      onCreated?.(result.task.taskId)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'reprocess-history', task.taskId] })
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'note-asr'] })
    } catch (error) {
      setActionError(`${error instanceof Error ? error.message : '重处理未确认'}。已冻结原提交标识，不会分配新任务、自动重试或重复计费。`)
    } finally {
      setBusy(false)
    }
  }

  async function rerun() {
    if (!canSubmit) return
    const body = createIntent()
    if (!body) return
    const storageError = saveNoteAsrReprocessIntent(userId, serverBinding, task.taskId, body)
    if (storageError) {
      setActionError(storageError)
      return
    }
    setIntentState({ kind: 'ready', value: body })
    await submit(body)
  }

  async function reconcileHistory() {
    if (!intent || busy) return
    setActionError('')
    setNotice('')
    await history.refetch()
    setNotice('已刷新不可变历史。请核对是否已有新任务；若没有，可按同一标识保存，不会分配新任务。')
  }

  const options = [
    {
      value: 'last',
      label: lastDestination
        ? `沿上次：${lastDestination.displayName}`
        : task.route === 'local' ? '沿上次：本机转写' : '沿上次：原云端目标（当前不可用）',
    },
    { value: 'local', label: '本机转写（不发送云端）' },
    ...(destinations.data?.items ?? []).map((item) => ({
      value: item.modelId,
      label: `${actualCloudRoute(item.presetId) === 'cloud-multimodal' ? '云端多模态' : '云端ASR'} · ${item.displayName}`,
    })),
  ]

  return <div className="min-w-0 space-y-3">
    <Card className="min-w-0 space-y-3 p-4 sm:p-5">
      <div>
        <h3 className="text-sm font-semibold text-text-1">执行尝试与停止</h3>
        <p className="mt-1 break-words text-xs text-text-3">停止只阻止后续分片继续发送；已发送或结果未知的分片不会自动重发或假定成功。</p>
      </div>
      <Attempts taskId={task.taskId} userId={userId} serverBinding={serverBinding} attempts={attempts} />
    </Card>

    <Card className="min-w-0 space-y-3 p-4 sm:p-5">
      <div>
        <h3 className="text-sm font-semibold text-text-1">重处理（显式确认）</h3>
        <p className="mt-1 break-words text-xs text-text-3">默认沿用上次执行位置。整份音频会重新发送，云端可能产生新费用；本机执行不发送云端。新执行以追加任务保留，不覆盖原任务、成功分片或历史。</p>
      </div>
      {config.isError || destinations.isError
        ? <InlineAlert tone="crit" title="执行目标读取失败">无法核对当前路由与共享目标；不会自动改用其他目标。</InlineAlert>
        : !config.data || destinations.isPending ? <Skeleton className="h-20" />
          : <>
            <div className="max-w-md"><Label htmlFor="note-asr-reprocess-target">执行位置</Label>
              <Select id="note-asr-reprocess-target" ariaLabel="执行位置" value={targetId} options={options}
                disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
                onValueChange={(value) => { setTargetId(value); setConfirmed(false); setNotice(''); setActionError('') }} /></div>
            {lastCloudInvalid && <InlineAlert tone="warn" title="上次云端目标不可用">
              原目标不在当前共享目录中。请明确选择其他目标；页面不会自动猜测或改用其他云端模型。
            </InlineAlert>}
            {!targetReady && !lastCloudInvalid && <InlineAlert tone="warn" title="没有可用执行位置">
              当前没有匹配的共享目标；本机转写仍可从“执行位置”明确选择，不会因缺少云密钥自动受阻止。
            </InlineAlert>}
            {selectedRoute === 'local' && <InlineAlert tone="info" title="本机重处理">
              任务在本机队列执行，不发送云端、不需要云端凭据；本机离线时保持排队。
            </InlineAlert>}
            {selectedRoute !== 'local' && selectedDestination && <div className="space-y-2 rounded-ctl border border-border p-3 text-xs text-text-2">
              <p className="break-words">目标：{selectedDestination.displayName} · {selectedDestination.model}</p>
              <p className="break-all font-mono text-text-3">{selectedDestination.endpoint}</p>
              <p>路由 {selectedRoute} · 预设 {selectedDestination.presetId} v{selectedDestination.presetVersion}。当前配置版本：{config.data.consentVersion}。</p>
            </div>}
          </>}
      <div className="max-w-sm"><Label htmlFor="note-asr-reprocess-kind">重处理原因</Label>
        <Select id="note-asr-reprocess-kind" ariaLabel="重处理原因" value={kind} disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
          options={[{ value: 'manual', label: '人工要求' }, { value: 'quality', label: '结果质量需要重做' }]}
          onValueChange={(value) => { setKind(value as 'manual' | 'quality'); setConfirmed(false) }} /></div>
      <div><Label htmlFor="note-asr-reprocess-reason">原因说明</Label>
        <Textarea id="note-asr-reprocess-reason" value={reason} maxLength={200} disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
          placeholder="例如：已核对原音频和旧结果，需要改用当前目标重新生成"
          onChange={(event) => { setReason(event.target.value); setConfirmed(false) }} /></div>
      {blockedIntent && <InlineAlert tone="crit" title="原重处理状态无法确认">{blockedIntent}</InlineAlert>}
      {intent ? <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs text-text-2">
        <p className="break-all">已冻结原提交标识：<span className="font-mono">{intent.clientTaskId}</span>。目标、原因与路由不可在核对前更改。</p>
        <p>原提交：{intent.route.route} · {intent.reason.kind} · {intent.reason.text}</p>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" loading={busy} onClick={() => void reconcileHistory()}>核对重处理历史</Button>
          <Button size="sm" variant="secondary" disabled={busy} onClick={() => void submit(intent)}>按原标识保存</Button>
        </div>
      </div> : <>
        <label className="flex items-start gap-2 text-xs text-text-2">
          <Checkbox ariaLabel={selectedRoute === 'local' ? '确认创建本机重处理任务' : '确认云端强制重处理并可能产生费用'}
            checked={confirmed} disabled={busy || !targetReady || Boolean(blockedIntent)} onCheckedChange={setConfirmed} />
          <span>{selectedRoute === 'local'
            ? '确认按当前本机配置创建重处理任务；本机不可用时只排队，不会自动切换云端。'
            : '确认将整份音频重新发送至上述云端目标，理解长等待与可能的新费用；旧任务和旧结果继续保留。'}</span>
        </label>
        <Button loading={busy} disabled={!canSubmit} onClick={() => void rerun()}>
          {selectedRoute === 'local' ? '确认创建本机重处理任务' : '确认创建云端重处理任务'}
        </Button>
      </>}
      {notice && <InlineAlert tone="ok">{notice}</InlineAlert>}
      {actionError && <InlineAlert tone="crit" title="重处理未确认">{actionError}</InlineAlert>}
    </Card>

    <Card className="min-w-0 space-y-3 p-4 sm:p-5">
      <div>
        <h3 className="text-sm font-semibold text-text-1">重处理历史（不可变）</h3>
        <p className="mt-1 break-words text-xs text-text-3">每次重处理都追加新任务与原因；旧执行、旧结果和失败记录均保留。</p>
      </div>
      {history.isError ? <InlineAlert tone="crit" title="重处理历史读取失败">请刷新核对；不会自动重发或清理旧记录。</InlineAlert>
        : history.isPending ? <Skeleton className="h-16" />
          : !history.data?.items.length ? <p className="text-sm text-text-3">还没有重处理记录。</p>
            : <div className="space-y-2">{history.data.items.map((item) => <div key={item.taskId}
              className="min-w-0 rounded-ctl border border-border p-3 text-xs text-text-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="break-all font-mono">{item.taskId}</span>
                <StatusBadge size="xs" dot={false} tone={item.phase.toLowerCase() === 'succeeded' ? 'ok' : 'warn'}>{label(item.phase)}</StatusBadge>
              </div>
              <p className="mt-1 break-words">{dateTime(item.createdAt)} · 路由 {item.route} · {item.executionMode}</p>
              <p className="mt-1 break-words">原因：{item.reason || '未提供'}</p>
            </div>)}</div>}
      {history.data && history.data.total > history.data.limit && <div className="flex flex-wrap justify-end gap-2">
        <Button size="sm" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 10))}>上一页历史</Button>
        <Button size="sm" disabled={offset + 10 >= history.data.total} onClick={() => setOffset(offset + 10)}>下一页历史</Button>
      </div>}
    </Card>
  </div>
}

function Attempts({ taskId, userId, serverBinding, attempts }: {
  taskId: string
  userId: string
  serverBinding: string
  attempts: UseQueryResult<NoteAsrAttemptDetail[]>
}) {
  const qc = useQueryClient()
  const [stopId, setStopId] = useState<string | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  if (attempts.isPending) return <Skeleton className="h-16" />
  if (attempts.isError) return <InlineAlert tone="crit" title="执行尝试读取失败">请刷新核对；不会自动停止或重发。</InlineAlert>
  if (!attempts.data?.length) return <p className="text-sm text-text-3">该任务还没有路由执行尝试。</p>
  return <div className="space-y-2">{attempts.data.map((attempt) => {
    const terminal = ['succeeded', 'failed', 'stopped', 'cancelled'].includes(attempt.state.toLowerCase())
    return <div key={attempt.attemptId} className="min-w-0 rounded-ctl border border-border p-3 text-xs text-text-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="break-all font-mono">{attempt.attemptId}</span>
        <StatusBadge size="xs" dot={false} tone={attempt.state.toLowerCase() === 'succeeded' ? 'ok' : terminal ? 'warn' : 'info'}>
          {label(attempt.state)}{attempt.stopRequested ? ' · 已请求停止' : ''}
        </StatusBadge>
      </div>
      <p className="mt-1 break-words">{attempt.route} · {attempt.completedSegments}/{attempt.expectedSegments} 片 · 失败 {attempt.failedSegments}</p>
      <p className="mt-1 break-all font-mono text-text-3">{attempt.endpoint ?? attempt.model ?? '目标未记录'}</p>
      {attempt.lastErrorMessage && <p className="mt-1 break-words text-warn">{attempt.lastErrorMessage}</p>}
      {!terminal && !attempt.stopRequested && <div className="mt-2">
        {stopId !== attempt.attemptId
          ? <Button size="sm" variant="secondary" onClick={() => { setStopId(attempt.attemptId); setConfirmed(false); setError('') }}>停止该执行</Button>
          : <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-2">
            <label className="flex items-start gap-2 text-text-2">
              <Checkbox checked={confirmed} onCheckedChange={setConfirmed} ariaLabel="确认停止后续分片" />
              <span>确认停止后续分片；已发送或结果未知的分片会先核对，不会自动重发。</span>
            </label>
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="danger-soft" loading={busy} disabled={!confirmed || busy}
                onClick={() => {
                  setBusy(true); setError('')
                  void aiApi.asrRoutingAttemptStop(attempt.attemptId)
                    .then(async () => {
                      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'asr-attempts', taskId] })
                      setStopId(null); setConfirmed(false)
                    })
                    .catch((failure: unknown) => setError(failure instanceof Error ? failure.message : '停止失败'))
                    .finally(() => setBusy(false))
                }}>确认停止</Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => { setStopId(null); setConfirmed(false) }}>返回</Button>
            </div>
          </div>}
      </div>}
      {error && stopId === attempt.attemptId && <p role="alert" className="mt-1 text-crit">{error}；状态未确认，请刷新核对，不会自动重试。</p>}
    </div>
  })}</div>
}
