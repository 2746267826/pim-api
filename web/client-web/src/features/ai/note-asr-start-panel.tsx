import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton, StatusBadge } from '@/components/ui'
import {
  aiApi,
  type NoteAsrStart,
  type NoteAsrTask,
} from './api'
import { createAiRequestId } from './request-id'
import {
  clearNoteAsrStartIntent,
  loadNoteAsrStartIntent,
  saveNoteAsrStartIntent,
  type NoteAsrStartIntentState,
} from './note-asr-start-intent'
import {
  clearNoteAsrBatchInitialIntent,
  loadNoteAsrBatchInitialIntent,
  saveNoteAsrBatchInitialIntent,
  type NoteAsrBatchInitialIntent,
  type NoteAsrBatchIntentState,
} from './note-asr-batch-intent'
import { actualCloudRoute, isActualCloudRoute } from './route-config-adapter'

const languageNames: Record<string, string> = { auto: '自动识别', zh: '中文', en: '英语' }

/**
 * Ordinary-user ASR start entry. It consumes only the public destination
 * catalog and per-account routing config; it never reads admin model settings.
 */
export function NoteAsrStartPanel({ userId, serverBinding, versionId, onCreated }: {
  userId: string
  serverBinding: string
  versionId: string
  onCreated?: (task: NoteAsrTask) => void
}) {
  const qc = useQueryClient()
  const [route, setRoute] = useState<'default' | 'local' | 'cloud'>('default')
  const [executionMode, setExecutionMode] = useState<'realtime' | 'batch'>('realtime')
  const [language, setLanguage] = useState('auto')
  const [confirmed, setConfirmed] = useState(false)
  const [batchConfirmed, setBatchConfirmed] = useState(false)
  const [intentState, setIntentState] = useState<NoteAsrStartIntentState>(() => loadNoteAsrStartIntent(userId, serverBinding, versionId))
  const [batchIntentState, setBatchIntentState] = useState<NoteAsrBatchIntentState<NoteAsrBatchInitialIntent>>(
    () => loadNoteAsrBatchInitialIntent(userId, serverBinding, versionId),
  )
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [batchConflict, setBatchConflict] = useState(false)
  const destinations = useQuery({
    queryKey: ['ai', userId, serverBinding, 'note-asr-destinations'],
    queryFn: ({ signal }) => aiApi.noteAsrDestinations(signal),
  })
  const config = useQuery({
    queryKey: ['ai', userId, serverBinding, 'asr-route-config'],
    queryFn: ({ signal }) => aiApi.asrRouteConfig(signal),
  })
  const savedDestination = destinations.data?.items.find((item) => item.modelId === config.data?.cloudModelId) ?? null
  const selectedDestination = savedDestination
  const effectiveRoute = route === 'default'
    ? config.data && isActualCloudRoute(config.data.defaultRoute) ? 'cloud' : 'local'
    : route
  const localConfigured = Boolean(config.data?.localModelId)
  const targetReady = effectiveRoute === 'cloud' ? Boolean(config.data && savedDestination) : localConfigured
  const intent = intentState.kind === 'ready' ? intentState.value : null
  const batchIntent = batchIntentState.kind === 'ready' ? batchIntentState.value : null
  const blockedIntent = intentState.kind === 'blocked'
    ? intentState.message
    : batchIntentState.kind === 'blocked' ? batchIntentState.message : ''
  const supportsBatch = effectiveRoute === 'cloud' && savedDestination?.presetId === 'mimo-multimodal'
  const selectedExecutionMode = supportsBatch ? executionMode : 'realtime'
  const batchReady = selectedExecutionMode === 'batch'
  const canSubmit = Boolean(
    targetReady && confirmed && (!batchReady || batchConfirmed) && !busy && !intent && !batchIntent && !blockedIntent,
  )
  const languageOptions = (selectedDestination?.languages ?? ['auto', 'zh', 'en'])
    .map((value) => ({ value, label: languageNames[value] ?? value }))
  const cloudInvalid = Boolean(config.data?.cloudModelId) && !savedDestination

  useEffect(() => {
    setIntentState(loadNoteAsrStartIntent(userId, serverBinding, versionId))
    setBatchIntentState(loadNoteAsrBatchInitialIntent(userId, serverBinding, versionId))
    setRoute('default')
    setExecutionMode('realtime')
    setLanguage('auto')
    setConfirmed(false)
    setBatchConfirmed(false)
    setNotice('')
    setError('')
    setBatchConflict(false)
  }, [userId, serverBinding, versionId])

  function freezeIntent() {
    if (!config.data) return null
    const destination = effectiveRoute === 'cloud' ? selectedDestination : null
    if (effectiveRoute === 'cloud' && !destination) return null
    const routeConsent = {
      route: destination ? actualCloudRoute(destination.presetId) : 'local',
      configRevision: config.data.revision,
      consentVersion: config.data.consentVersion,
      executionMode: 'realtime',
      batchScopeConfirmed: false,
      confirmed: true,
    }
    if (!destination) {
      return { taskId: createAiRequestId(), expectedUserId: userId, versionId, language, consent: null, route: routeConsent } satisfies NoteAsrStart
    }
    return {
      taskId: createAiRequestId(),
      expectedUserId: userId,
      versionId,
      language,
      consent: {
        modelId: destination.modelId,
        modelRevision: destination.modelRevision,
        endpoint: destination.endpoint,
        presetId: destination.presetId,
        presetVersion: destination.presetVersion,
        confirmed: true,
      },
      route: routeConsent,
    } satisfies NoteAsrStart
  }

  async function submit(body: NoteAsrStart) {
    setBusy(true)
    setNotice('')
    setError('')
    try {
      const task = await aiApi.noteAsrStart(body)
      clearNoteAsrStartIntent(userId, serverBinding, versionId, body.taskId)
      setIntentState({ kind: 'none' })
      setConfirmed(false)
      setNotice(body.consent
        ? '已创建新的云端转写任务，可能产生服务商费用；任务与旧历史分别保留。'
        : '已保存本机排队转写任务；本机不可用时保持排队，不会自动改用云端，也不产生云端费用。')
      onCreated?.(task)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'note-asr'] })
    } catch (failure) {
      setError(`${failure instanceof Error ? failure.message : '创建任务失败'}。任务可能已经保存，请先用原任务标识核对；页面不会分配新任务或自动重试。`)
    } finally {
      setBusy(false)
    }
  }

  function freezeBatchIntent(): NoteAsrBatchInitialIntent | null {
    if (!config.data || !savedDestination) return null
    const taskId = createAiRequestId()
    const routeConsent = {
      route: actualCloudRoute(savedDestination.presetId),
      configRevision: config.data.revision,
      consentVersion: config.data.consentVersion,
      executionMode: 'batch',
      batchScopeConfirmed: true,
      confirmed: true,
    }
    return {
      kind: 'initial',
      phase: 'prepare',
      taskId,
      expectedUserId: userId,
      versionId,
      start: {
        taskId,
        expectedUserId: userId,
        versionId,
        language,
        consent: {
          modelId: savedDestination.modelId,
          modelRevision: savedDestination.modelRevision,
          endpoint: savedDestination.endpoint,
          presetId: savedDestination.presetId,
          presetVersion: savedDestination.presetVersion,
          confirmed: true,
        },
        route: routeConsent,
      },
      route: {
        route: actualCloudRoute(savedDestination.presetId),
        expectedConfigRevision: config.data.revision,
        expectedConsentVersion: config.data.consentVersion,
        executionMode: 'batch',
        batchScopeConfirmed: true,
        confirmed: true,
      },
      target: {
        modelId: savedDestination.modelId,
        modelRevision: savedDestination.modelRevision,
        consentRef: config.data.consentVersion,
        confirmed: true,
        batchScopeConfirmed: true,
        completionWindow: '24h',
      },
      reason: { kind: 'manual', text: '初次批量转写' },
    }
  }

  async function submitBatchIntent(frozen: NoteAsrBatchInitialIntent) {
    setBusy(true)
    setNotice('')
    setError('')
    setBatchConflict(false)
    try {
      let current = frozen
      if (current.phase === 'prepare') {
        const prepared = await aiApi.noteAsrStart(current.start)
        onCreated?.(prepared)
        const submitted: NoteAsrBatchInitialIntent = {
          kind: 'initial',
          phase: 'submit',
          taskId: current.taskId,
          expectedUserId: current.expectedUserId,
          versionId: current.versionId,
          start: current.start,
          request: {
            clientTaskId: current.taskId,
            sourceTaskId: current.taskId,
            expectedSourceRevision: prepared.revision,
            expectedUserId: current.expectedUserId,
            route: current.route,
            target: current.target,
            reason: current.reason,
          },
        }
        const storageError = saveNoteAsrBatchInitialIntent(userId, serverBinding, versionId, submitted)
        if (storageError) {
          setBatchIntentState({ kind: 'ready', value: current })
          setError(`${storageError} 已创建的 Batch 准备任务保持原标识 ${current.taskId}，没有继续提交。`)
          return
        }
        current = submitted
        setBatchIntentState({ kind: 'ready', value: submitted })
      }
      if (current.phase !== 'submit') throw new Error('Batch 提交意图阶段无效')
      const result = await aiApi.asrBatchFromTask(current.taskId, current.request)
      clearNoteAsrBatchInitialIntent(userId, serverBinding, versionId, current.taskId)
      setBatchIntentState({ kind: 'none' })
      setConfirmed(false)
      setBatchConfirmed(false)
      setNotice(`Batch 开发契约已按同一任务标识提交，进入可长达 ${result.job.completionWindow} 的闲时等待；按成功请求消耗现金，失败项不会自动重跑。`)
      onCreated?.(result.task)
      await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'note-asr'] })
    } catch (failure) {
      const code = typeof failure === 'object' && failure && 'code' in failure
        ? Number((failure as { code?: unknown }).code)
        : -1
      setBatchConflict(code === 40901)
      setError(`${failure instanceof Error ? failure.message : 'Batch 提交未确认'}。已冻结同一任务标识，不会自动改用实时执行、创建新任务或重复提交。`)
    } finally {
      setBusy(false)
    }
  }

  async function start() {
    if (!canSubmit) return
    if (batchReady) {
      const frozen = freezeBatchIntent()
      if (!frozen) return
      const storageError = saveNoteAsrBatchInitialIntent(userId, serverBinding, versionId, frozen)
      if (storageError) {
        setError(storageError)
        return
      }
      setBatchIntentState({ kind: 'ready', value: frozen })
      await submitBatchIntent(frozen)
      return
    }
    const body = freezeIntent()
    if (!body) return
    const storageError = saveNoteAsrStartIntent(userId, serverBinding, versionId, body)
    if (storageError) {
      setError(storageError)
      return
    }
    setIntentState({ kind: 'ready', value: body })
    await submit(body)
  }

  async function reconcile() {
    if (!intent || busy) return
    setBusy(true)
    setError('')
    setNotice('')
    try {
      const task = await aiApi.noteAsrTaskOrNull(intent.taskId)
      if (task) {
        clearNoteAsrStartIntent(userId, serverBinding, versionId, intent.taskId)
        setIntentState({ kind: 'none' })
        setNotice('已找到原任务，没有重新发送。')
        onCreated?.(task)
        await qc.invalidateQueries({ queryKey: ['ai', userId, serverBinding, 'note-asr'] })
      } else {
        setNotice('服务端未找到原任务；可以按同一标识重新保存，不会生成新任务。')
      }
    } catch (failure) {
      setError(`${failure instanceof Error ? failure.message : '核对失败'}。保留原任务标识，请刷新后重试。`)
    } finally {
      setBusy(false)
    }
  }

  async function reconcileBatch() {
    if (!batchIntent || busy) return
    setBusy(true)
    setError('')
    setNotice('')
    try {
      const task = await aiApi.noteAsrTaskOrNull(batchIntent.taskId)
      if (!task) {
        setNotice('服务端尚未找到该准备任务。保留原标识；再次提交只会核对同一标识，不会分配新任务。')
        return
      }
      if (batchIntent.phase === 'prepare') {
        const submitted: NoteAsrBatchInitialIntent = {
          kind: 'initial',
          phase: 'submit',
          taskId: batchIntent.taskId,
          expectedUserId: batchIntent.expectedUserId,
          versionId: batchIntent.versionId,
          start: batchIntent.start,
          request: {
            clientTaskId: batchIntent.taskId,
            sourceTaskId: batchIntent.taskId,
            expectedSourceRevision: task.revision,
            expectedUserId: batchIntent.expectedUserId,
            route: batchIntent.route,
            target: batchIntent.target,
            reason: batchIntent.reason,
          },
        }
        const storageError = saveNoteAsrBatchInitialIntent(userId, serverBinding, versionId, submitted)
        if (storageError) {
          setError(storageError)
          return
        }
        setBatchIntentState({ kind: 'ready', value: submitted })
        setNotice('已找到同一准备任务并补齐冻结版本；没有自动提交 Batch。确认后可继续使用同一标识提交。')
      } else {
        setNotice('已找到同一准备任务；没有自动重复提交 Batch。')
      }
      onCreated?.(task)
    } catch (failure) {
      setError(`${failure instanceof Error ? failure.message : '核对失败'}。保留原任务标识，请刷新后重试。`)
    } finally {
      setBusy(false)
    }
  }

  if (config.isError || destinations.isError) return <InlineAlert tone="crit" title="转写目标读取失败">
    无法核对当前路由或共享目标；不会自动回退到其他执行位置。
  </InlineAlert>
  if (!config.data || destinations.isPending) return <Skeleton className="h-32" />

  return <Card className="min-w-0 space-y-3 p-4 sm:p-5">
    <div>
      <h3 className="text-sm font-semibold text-text-1">启动录音转写</h3>
      <p className="mt-1 break-words text-xs text-text-3">只为当前固定材料版本创建新任务。本机不可用时保持排队且不会自动改用云端；云端执行必须单独确认出域与费用。旧任务和结果不会覆盖。</p>
    </div>
    <div><Label htmlFor="note-asr-start-route">执行位置</Label>
        <Select id="note-asr-start-route" ariaLabel="执行位置" value={route} disabled={busy || Boolean(intent) || Boolean(batchIntent) || Boolean(blockedIntent)}
        options={[
          { value: 'default', label: effectiveRoute === 'cloud' ? '按当前默认路由（云端）' : '按当前默认路由（本机）' },
          { value: 'local', label: '本机转写' },
          { value: 'cloud', label: '云端转写' },
        ]}
        onValueChange={(value) => {
          setRoute(value as 'default' | 'local' | 'cloud')
          setExecutionMode('realtime')
          setConfirmed(false)
          setBatchConfirmed(false)
          setNotice('')
          setError('')
        }} />
    </div>
    {supportsBatch && <div className="max-w-sm"><Label htmlFor="note-asr-start-execution-mode">执行方式</Label>
      <Select id="note-asr-start-execution-mode" ariaLabel="执行方式" value={executionMode}
        disabled={busy || Boolean(intent) || Boolean(batchIntent) || Boolean(blockedIntent)}
        options={[{ value: 'realtime', label: '实时云端转写' }, { value: 'batch', label: 'MiMo Batch 闲时批量（开发预览）' }]}
        onValueChange={(value) => {
          setExecutionMode(value as 'realtime' | 'batch')
          setConfirmed(false)
          setBatchConfirmed(false)
          setNotice('')
          setError('')
        }} />
      <p className="mt-1 text-xs leading-5 text-text-3">Batch 只提交已冻结的多模态任务，可能等待数小时；没有供应商任务标识时保持 UNKNOWN 并人工核对。</p>
    </div>}
    {effectiveRoute === 'local' && (localConfigured
      ? <InlineAlert tone="info" title="本机执行">
        任务保存后在本机队列执行，不发送云端、不需要云端凭据；本机离线时保持排队，不会自动切换云端。
      </InlineAlert>
      : <InlineAlert tone="warn" title="没有本机模型">
        当前账号尚未保存本机 ASR 模型；请选择云端目标，或由本机客户端注册模型后重新开始。
      </InlineAlert>)}
    {effectiveRoute === 'cloud' && (savedDestination
      ? <div className="space-y-2 rounded-ctl border border-border p-3 text-xs text-text-2">
        <p className="break-words">目标：{savedDestination.displayName} · {savedDestination.model}</p>
        <p className="break-all font-mono text-text-3">{savedDestination.endpoint}</p>
        <p>预设 {savedDestination.presetId} v{savedDestination.presetVersion}。音频将离开本机并产生可能的服务商费用。</p>
      </div>
      : <InlineAlert tone="warn" title={cloudInvalid ? '已保存云端目标失效' : '云端尚未配置'}>
        {cloudInvalid ? '保存的云端模型不在当前共享目录中；本页不会改用其他目标。' : '管理员尚未提供可用云端目标；不会自动回退到本机。'}
      </InlineAlert>)}
    {blockedIntent && <InlineAlert tone="crit" title="原提交状态无法确认">{blockedIntent}</InlineAlert>}
    <div className="max-w-sm"><Label htmlFor="note-asr-start-language">识别语言</Label>
      <Select id="note-asr-start-language" ariaLabel="识别语言" value={language} options={languageOptions}
        disabled={busy || Boolean(intent) || Boolean(blockedIntent)} onValueChange={(value) => { setLanguage(value); setConfirmed(false) }} /></div>
    {intent ? <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs text-text-2">
      <p className="break-all">已冻结原任务标识：<span className="font-mono">{intent.taskId}</span>。目标与原因不可在核对前静默更改。</p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" loading={busy} onClick={() => void reconcile()}>核对原任务</Button>
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => void submit(intent)}>按原标识保存</Button>
      </div>
    </div> : batchIntent ? <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs text-text-2">
      <p className="break-all">已冻结 Batch 任务标识：<span className="font-mono">{batchIntent.taskId}</span>。准备成功不会清空标识；目标、整批范围与 24h 窗口在核对前不可更改。</p>
      <p>{batchIntent.phase === 'prepare' ? '阶段：等待核对/创建准备任务（尚未提交 Batch）' : '阶段：已准备，等待/核对 Batch 提交'}</p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" loading={busy} onClick={() => void reconcileBatch()}>核对原 Batch 任务</Button>
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => void submitBatchIntent(batchIntent)}>
          {batchIntent.phase === 'prepare' ? '按原标识准备并提交 Batch' : '按原标识重新提交 Batch'}
        </Button>
      </div>
    </div> : <>
      <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox ariaLabel="确认创建录音转写任务" checked={confirmed} disabled={!targetReady || busy || Boolean(blockedIntent)}
          onCheckedChange={(value) => { setConfirmed(value); if (!value) setBatchConfirmed(false) }} />
        <span>{batchReady
          ? '确认将固定版本录音发送至上述 MiMo 多模态目标作为整批请求，理解闲时调度、长等待、可能仅部分成功与现金费用'
          : effectiveRoute === 'local'
          ? '确认按当前本机配置创建转写任务；本机不可用时只排队，不会自动切换到云端'
          : '确认将当前版本录音发送至上述云端目标，理解可能的新费用与长等待'}</span>
      </label>
      {batchReady && <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox ariaLabel="确认Batch整批范围与现金计费" checked={batchConfirmed} disabled={!confirmed || busy || Boolean(blockedIntent)}
          onCheckedChange={setBatchConfirmed} />
        <span>确认整批分片范围、24 小时完成窗口与按成功 Token 消耗现金；失败项不会自动续跑或整体重发。</span>
      </label>}
      <Button variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void start()}>
        {batchReady
          ? batchIntent ? '使用原标识继续 Batch' : '准备并提交 MiMo Batch'
          : effectiveRoute === 'cloud' && cloudInvalid
          ? '云端目标失效'
          : effectiveRoute === 'local' ? '创建本机排队任务' : '确认创建云端任务'}
      </Button>
    </>}
    {effectiveRoute === 'cloud' && savedDestination && !intent && !batchIntent && <StatusBadge size="xs" dot={false} tone="warn">云端出域 · 可能计费</StatusBadge>}
    {batchReady && !intent && !batchIntent && <StatusBadge size="xs" dot={false} tone="warn">Batch 开发预览 · NOT-VERIFIED</StatusBadge>}
    {effectiveRoute === 'local' && !intent && !batchIntent && <StatusBadge size="xs" dot={false} tone={localConfigured ? 'ok' : 'warn'}>{localConfigured ? '本机队列' : '未配置本机模型'}</StatusBadge>}
    {notice && <InlineAlert tone="ok">{notice}</InlineAlert>}
    {error && <InlineAlert tone="crit" title={batchConflict ? 'Batch 能力未开放或状态冲突' : '任务创建未确认'}>{error}</InlineAlert>}
  </Card>
}
