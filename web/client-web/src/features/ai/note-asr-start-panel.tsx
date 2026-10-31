import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton, StatusBadge } from '@/components/ui'
import { aiApi, type NoteAsrStart, type NoteAsrTask } from './api'
import { createAiRequestId } from './request-id'
import {
  clearNoteAsrStartIntent,
  loadNoteAsrStartIntent,
  saveNoteAsrStartIntent,
  type NoteAsrStartIntentState,
} from './note-asr-start-intent'
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
  const [language, setLanguage] = useState('auto')
  const [confirmed, setConfirmed] = useState(false)
  const [intentState, setIntentState] = useState<NoteAsrStartIntentState>(() => loadNoteAsrStartIntent(userId, serverBinding, versionId))
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
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
  const blockedIntent = intentState.kind === 'blocked' ? intentState.message : ''
  const canSubmit = Boolean(targetReady && confirmed && !busy && !intent && !blockedIntent)
  const languageOptions = (selectedDestination?.languages ?? ['auto', 'zh', 'en'])
    .map((value) => ({ value, label: languageNames[value] ?? value }))
  const cloudInvalid = Boolean(config.data?.cloudModelId) && !savedDestination

  useEffect(() => {
    setIntentState(loadNoteAsrStartIntent(userId, serverBinding, versionId))
    setRoute('default')
    setLanguage('auto')
    setConfirmed(false)
    setNotice('')
    setError('')
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

  async function start() {
    if (!canSubmit) return
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
      <Select id="note-asr-start-route" ariaLabel="执行位置" value={route} disabled={busy || Boolean(intent) || Boolean(blockedIntent)}
        options={[
          { value: 'default', label: effectiveRoute === 'cloud' ? '按当前默认路由（云端）' : '按当前默认路由（本机）' },
          { value: 'local', label: '本机转写' },
          { value: 'cloud', label: '云端转写' },
        ]}
        onValueChange={(value) => { setRoute(value as 'default' | 'local' | 'cloud'); setConfirmed(false); setNotice(''); setError('') }} />
    </div>
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
    </div> : <>
      <label className="flex items-start gap-2 text-xs text-text-2">
        <Checkbox ariaLabel="确认创建录音转写任务" checked={confirmed} disabled={!targetReady || busy || Boolean(blockedIntent)}
          onCheckedChange={setConfirmed} />
        <span>{effectiveRoute === 'local'
          ? '确认按当前本机配置创建转写任务；本机不可用时只排队，不会自动切换到云端'
          : '确认将当前版本录音发送至上述云端目标，理解可能的新费用与长等待'}</span>
      </label>
      <Button variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void start()}>
        {effectiveRoute === 'cloud' && cloudInvalid
          ? '云端目标失效'
          : effectiveRoute === 'local' ? '创建本机排队任务' : '确认创建云端任务'}
      </Button>
    </>}
    {effectiveRoute === 'cloud' && savedDestination && !intent && <StatusBadge size="xs" dot={false} tone="warn">云端出域 · 可能计费</StatusBadge>}
    {effectiveRoute === 'local' && !intent && <StatusBadge size="xs" dot={false} tone={localConfigured ? 'ok' : 'warn'}>{localConfigured ? '本机队列' : '未配置本机模型'}</StatusBadge>}
    {notice && <InlineAlert tone="ok">{notice}</InlineAlert>}
    {error && <InlineAlert tone="crit" title="任务创建未确认">{error}</InlineAlert>}
  </Card>
}
