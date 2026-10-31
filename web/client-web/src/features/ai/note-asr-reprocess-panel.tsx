import { useState } from 'react'
import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton, StatusBadge, Textarea } from '@/components/ui'
import { aiApi, type NoteAsrAttemptDetail, type NoteAsrTask } from './api'
import { createAiRequestId } from './request-id'
import { actualCloudRoute } from './route-config-adapter'

const dateTime = (value: string) => new Date(value).toLocaleString()
const phaseLabel: Record<string, string> = {
  queued: '等待处理', running: '转写中', succeeded: '转写完成', needsattention: '有分片需处理',
  failed: '失败', stopped: '已停止', unknown: '结果待核对', cancelled: '已取消',
}
const label = (value: string) => phaseLabel[value.toLowerCase()] ?? value

export function NoteAsrReprocessPanel({ task, userId, onCreated }: {
  task: NoteAsrTask
  userId: string
  onCreated?: (taskId: string) => void
}) {
  const qc = useQueryClient()
  const [offset, setOffset] = useState(0)
  const [kind, setKind] = useState<'manual' | 'quality'>('manual')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [actionError, setActionError] = useState('')
  const config = useQuery({ queryKey: ['ai', userId, 'asr-route-config'], queryFn: ({ signal }) => aiApi.asrRouteConfig(signal) })
  const models = useQuery({ queryKey: ['ai', userId, 'models'], queryFn: ({ signal }) => aiApi.models(signal) })
  const presets = useQuery({ queryKey: ['ai-asr-presets'], queryFn: ({ signal }) => aiApi.asrPresets(signal) })
  const history = useQuery({
    queryKey: ['ai', userId, 'reprocess-history', task.taskId, offset],
    queryFn: ({ signal }) => aiApi.asrReprocessHistory(task.taskId, userId, offset, 10, signal),
  })
  const attempts = useQuery({
    queryKey: ['ai', userId, 'asr-attempts', task.taskId],
    queryFn: ({ signal }) => aiApi.asrRoutingAttempts(task.taskId, signal),
  })

  const cloudModel = models.data?.find((model) => model.id === config.data?.cloudModelId && model.enabled)
  const preset = presets.data?.find((value) => value.id === cloudModel?.asrPresetId)
  const targetReady = Boolean(config.data && cloudModel && preset && cloudModel.hasApiKey)
  const canSubmit = Boolean(targetReady && reason.trim() && confirmed && !busy)

  async function rerun() {
    if (!canSubmit || !config.data || !cloudModel || !preset) return
    setBusy(true)
    setNotice('')
    setActionError('')
    try {
      const result = await aiApi.asrReprocess(task.taskId, {
        clientTaskId: createAiRequestId(),
        expectedUserId: userId,
        expectedRevision: task.revision,
        reason: { kind, text: reason.trim() },
        route: {
          route: actualCloudRoute(cloudModel.asrPresetId),
          expectedConfigRevision: config.data.revision,
          expectedConsentVersion: config.data.consentVersion,
          executionMode: 'realtime',
          confirmed: true,
          batchScopeConfirmed: false,
        },
        consent: {
          modelId: cloudModel.id,
          modelRevision: cloudModel.revision,
          endpoint: cloudModel.baseUrl + preset.operationPath,
          presetId: preset.id,
          presetVersion: preset.version,
          confirmed: true,
        },
      })
      setNotice(`已创建新的云端重处理任务；原任务与历史结果保持不变。新任务：${result.task.taskId}`)
      setConfirmed(false)
      setReason('')
      onCreated?.(result.task.taskId)
      await qc.invalidateQueries({ queryKey: ['ai', userId, 'reprocess-history', task.taskId] })
      await qc.invalidateQueries({ queryKey: ['ai', userId, 'note-asr'] })
    } catch (error) {
      setActionError(`${error instanceof Error ? error.message : '重处理失败'}。原任务与旧结果仍保留；页面不会自动重试或重复计费。`)
    } finally {
      setBusy(false)
    }
  }

  return <div className="min-w-0 space-y-3">
    <Card className="min-w-0 space-y-3 p-4 sm:p-5">
      <div>
        <h3 className="text-sm font-semibold text-text-1">执行尝试与停止</h3>
        <p className="mt-1 break-words text-xs text-text-3">停止只阻止后续分片继续发送；已发送或结果未知的分片不会自动重发或假定成功。</p>
      </div>
      <Attempts taskId={task.taskId} userId={userId} attempts={attempts} />
    </Card>

    <Card className="min-w-0 space-y-3 p-4 sm:p-5">
      <div>
        <h3 className="text-sm font-semibold text-text-1">云端强制重处理（显式确认）</h3>
        <p className="mt-1 break-words text-xs text-text-3">整份音频会重新发送到当前默认云端模型，可能产生新费用；新的执行以追加任务保留，不覆盖原任务、分片成功结果或历史。</p>
      </div>
      {config.isError || models.isError || presets.isError
        ? <InlineAlert tone="crit" title="云端目标读取失败">无法核对当前云端模型、预设与配置版本；不会自动改用其他目标。</InlineAlert>
        : !config.data || models.isPending || presets.isPending ? <Skeleton className="h-20" />
          : !targetReady ? <InlineAlert tone="warn" title="没有可用的云端目标">请先在 ASR 配置中保存已启用、带凭据的云端模型与预设；本机模式不会自动切换云端。</InlineAlert>
            : <div className="space-y-2 rounded-ctl border border-border p-3 text-xs text-text-2">
              <p className="break-words">目标：{cloudModel!.name} · {cloudModel!.model} · {preset!.name} v{preset!.version}</p>
              <p className="break-all font-mono text-text-3">{cloudModel!.baseUrl + preset!.operationPath}</p>
              <p>当前配置版本：{config.data.consentVersion}。执行模式：实时逐片；Batch 不在此强制重跑入口自动启用。</p>
            </div>}
      <div className="max-w-sm"><Label htmlFor="note-asr-reprocess-kind">重处理原因</Label>
        <Select id="note-asr-reprocess-kind" ariaLabel="重处理原因" value={kind} disabled={busy}
          options={[{ value: 'manual', label: '人工要求' }, { value: 'quality', label: '结果质量需要重做' }]}
          onValueChange={(value) => setKind(value as 'manual' | 'quality')} /></div>
      <div><Label htmlFor="note-asr-reprocess-reason">原因说明</Label>
        <Textarea id="note-asr-reprocess-reason" value={reason} maxLength={200} disabled={busy}
          placeholder="例如：已核对原音频和旧结果，需要改用当前云端模型重新生成"
          onChange={(event) => { setReason(event.target.value); setConfirmed(false) }} /></div>
      <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox ariaLabel="确认云端强制重处理并可能产生费用" checked={confirmed} disabled={busy || !targetReady}
          onCheckedChange={setConfirmed} />
        <span>确认将整份音频重新发送至上述云端目标，理解长等待与可能的新费用；旧任务和旧结果继续保留。</span>
      </label>
      <Button loading={busy} disabled={!canSubmit} onClick={() => void rerun()}>确认创建云端重处理任务</Button>
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

function Attempts({ taskId, userId, attempts }: {
  taskId: string
  userId: string
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
                      await qc.invalidateQueries({ queryKey: ['ai', userId, 'asr-attempts', taskId] })
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
