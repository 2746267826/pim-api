import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Play, RefreshCw } from 'lucide-react'
import { ApiError } from '@/api/client'
import { Button, Card, Checkbox, Chip, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatTime } from '@/lib/datetime'
import {
  noteProcessingApi,
  type NoteProcessingChain,
  type NoteProcessingPlan,
  type NoteProcessingStatus,
} from './api'

const overallLabels: Record<string, string> = {
  WaitingForMaterial: '等待材料',
  PendingProcessing: '待发起处理',
  Processing: '处理中',
  PartiallyCompleted: '部分完成',
  Completed: '处理完成',
  NeedsAttention: '需要处理或阻断',
}

const chainLabels: Record<string, string> = {
  NotApplicable: '不适用',
  WaitingForMaterial: '等待材料',
  WaitingForAsr: '等待 ASR',
  PendingStart: '待发起',
  Queued: '排队中',
  Running: '处理中',
  Succeeded: '已完成',
  NeedsAttention: '需要处理',
  Stopped: '已停止',
}

const kindLabels: Record<string, string> = {
  CompleteRecord: '完整记录',
  IndependentAnalysis: '独立分析',
}

const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请刷新核对'
const failureText = (error: unknown, showVersionDetails: boolean, fallback: string) =>
  showVersionDetails && error instanceof Error && error.message.trim() ? error.message : fallback
const overallLabel = (value: string, showVersionDetails: boolean) =>
  overallLabels[value] ?? (showVersionDetails ? value : '处理状态待核对')
const chainLabel = (value: string, showVersionDetails: boolean) =>
  chainLabels[value] ?? (showVersionDetails ? value : '处理状态待核对')
const waitingText = (value: string | null | undefined, showVersionDetails: boolean, fallback: string) =>
  showVersionDetails && value ? value : fallback
const targetKindLabel = (kind: string) => kind === 'Asr' ? '语音转写' : '多模态整理'
const targetRouteLabel = (route: string | null | undefined) =>
  route === 'local' ? '本机转写' : route === 'cloud-asr' || route === 'cloud-multimodal'
    ? '云端模型' : '账号默认目标'
type ProcessingTarget = NoteProcessingPlan['targets'][number]
const targetLabel = (target: ProcessingTarget) =>
  target.modelName || target.model || targetRouteLabel(target.route)

function overallTone(value: string) {
  if (value === 'Completed') return 'ok' as const
  if (value === 'Processing') return 'info' as const
  if (value === 'NeedsAttention') return 'crit' as const
  return 'warn' as const
}

function chainTone(value: string) {
  if (value === 'Succeeded') return 'ok' as const
  if (value === 'Running' || value === 'Queued') return 'info' as const
  if (value === 'NeedsAttention' || value === 'Stopped') return 'crit' as const
  return 'neutral' as const
}

function ChainCard({ chain, showVersionDetails }: { chain: NoteProcessingChain; showVersionDetails: boolean }) {
  const title = chain.kind === 'Asr' ? 'ASR 转写' : '多模态整理'
  return <div className="min-w-0 rounded-ctl border border-border bg-bg p-3">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <span className="text-sm font-medium text-text-1">{title}</span>
      <StatusBadge dot={false} tone={chainTone(chain.state)}>{chainLabel(chain.state, showVersionDetails)}</StatusBadge>
    </div>
    <p className="mt-2 text-xs text-text-3">
      {chain.state === 'NotApplicable'
        ? showVersionDetails ? '该固定版本没有录音，不创建空 ASR 任务。' : '当前内容没有录音，无需创建转写任务。'
        : chain.taskCreated
          ? `${chain.completedItems} / ${chain.totalItems} 项完成${chain.failedItems ? ` · ${chain.failedItems} 项未完成` : ''}`
          : waitingText(chain.waitingReason, showVersionDetails, '任务尚未开始，请刷新核对')}
    </p>
    {chain.errors.length > 0 && (showVersionDetails
      ? <ul className="mt-2 space-y-1 text-xs leading-5 text-crit">
          {chain.errors.map((error, index) => <li key={`${chain.kind}-${index}`}>{error}</li>)}
        </ul>
      : <p className="mt-2 text-xs leading-5 text-warn">处理未完整完成，请刷新核对；页面不会自动重发。</p>)}
  </div>
}

function ProcessingTargets({ targets, showVersionDetails }: {
  targets: ProcessingTarget[]
  showVersionDetails: boolean
}) {
  return <ul className="space-y-2">
    {targets.map((target) => <li key={target.kind}
      className="rounded-ctl border border-border bg-surface p-2 text-xs leading-5 text-text-2">
      <span className="font-medium text-text-1">{targetKindLabel(target.kind)}：</span>
      {targetLabel(target)}
      {showVersionDetails && target.endpoint && <span className="mt-1 block break-all font-mono text-text-3">
        {target.endpoint}
        {target.presetId ? ` · ${target.presetId}/${target.presetVersion ?? ''}` : ''}
        {target.executionMode ? ` · ${target.executionMode}` : ''}
      </span>}
      <span className="mt-1 block text-text-3">
        {target.paid ? '可能按服务商用量产生费用' : '本机执行，本次不产生云端费用'}
      </span>
    </li>)}
  </ul>
}

function ProcessingPlan({ plan, showVersionDetails, confirmed, onConfirmedChange }: {
  plan: NoteProcessingPlan
  showVersionDetails: boolean
  confirmed: boolean
  onConfirmedChange: (value: boolean) => void
}) {
  return <section className="space-y-3 rounded-ctl border border-border bg-bg p-3" aria-label="发起前处理计划">
    <div>
      <h3 className="text-sm font-medium text-text-1">发起前处理计划</h3>
      <p className="mt-1 text-xs leading-5 text-text-3">{plan.purposeText}</p>
    </div>
    <p className="text-xs leading-5 text-text-2">
      <span className="font-medium text-text-1">材料范围：</span>
      {plan.materialScopeText}
    </p>
    <ProcessingTargets targets={plan.targets} showVersionDetails={showVersionDetails} />
    <p className="rounded-ctl bg-warn-soft p-2 text-xs leading-5 text-warn">
      {plan.costNotice}
    </p>
    <p className="break-all font-mono text-[11px] leading-5 text-text-3">
      处理授权 revision：{plan.authorizationRevision}
      {plan.standingAuthorizationRevision
        ? ` · 持续授权 revision：${plan.standingAuthorizationRevision}`
        : ' · 持续授权：未开启或不适用'}
    </p>
    <label className="flex items-start gap-2 text-xs leading-5 text-text-2">
      <Checkbox checked={confirmed} onCheckedChange={onConfirmedChange}
        ariaLabel="我已核对处理目标、材料范围与费用提示" className="mt-0.5" />
      <span>我已核对以上处理目标、材料范围与费用提示；本次确认会随任务一起持久化。</span>
    </label>
  </section>
}

function ProcessingAuthorization({ authorization, showVersionDetails }: {
  authorization: NonNullable<NoteProcessingStatus['authorization']>
  showVersionDetails: boolean
}) {
  return <section className="space-y-3 rounded-ctl border border-border bg-bg p-3"
    aria-label="已持久化的处理授权">
    <div>
      <h3 className="text-sm font-medium text-text-1">已持久化的处理授权</h3>
      <p className="mt-1 text-xs leading-5 text-text-3">
        来源：{authorization.source === 'standing' ? '账号持续授权' : '本次手动确认'}
        ；授权已随任务记录保存。
      </p>
    </div>
    <p className="text-xs leading-5 text-text-2">
      <span className="font-medium text-text-1">材料范围：</span>
      {authorization.materialScopeText}
    </p>
    <ProcessingTargets targets={authorization.targets} showVersionDetails={showVersionDetails} />
    <p className="rounded-ctl bg-warn-soft p-2 text-xs leading-5 text-warn">
      {authorization.costNotice}
    </p>
    <p className="break-all font-mono text-[11px] leading-5 text-text-3">
      处理授权 revision：{authorization.authorizationRevision}
      {authorization.standingAuthorizationRevision
        ? ` · 持续授权 revision：${authorization.standingAuthorizationRevision}`
        : ''}
    </p>
  </section>
}

export function NoteProcessingPanel({ ownerId, serverBinding, noteId, versionId, showVersionDetails, onChanged }: {
  ownerId: string
  serverBinding: string
  noteId: string
  versionId: string
  showVersionDetails: boolean
  onChanged?: () => void
}) {
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
  const [confirmedRevision, setConfirmedRevision] = useState<string | null>(null)
  const [actionError, setActionError] = useState('')
  const queryKey = ['note-processing', ownerId, serverBinding, noteId, versionId]
  const status = useQuery({
    queryKey,
    queryFn: ({ signal }) => noteProcessingApi.status(noteId, versionId, signal),
    refetchInterval: (query) => {
      const value = query.state.data?.state
      return value === 'Processing' || value === 'PartiallyCompleted' ? 5000 : false
    },
  })
  const planQuery = useQuery({
    queryKey: [...queryKey, 'plan'],
    queryFn: ({ signal }) => noteProcessingApi.plan(noteId, versionId, signal),
    enabled: status.data?.manualStartAvailable === true,
  })
  const consentConfirmed = Boolean(planQuery.data &&
    confirmedRevision === planQuery.data.authorizationRevision)

  async function start() {
    const plan = planQuery.data
    if (!plan || !consentConfirmed) {
      setActionError('请先核对处理目标、材料范围与费用提示')
      return
    }
    setBusy(true)
    setActionError('')
    try {
      const next = await noteProcessingApi.start({
        noteId,
        versionId,
        consent: {
          purpose: plan.purpose,
          materialScope: plan.materialScope,
          costNotice: plan.costNotice,
          possibleChargeAcknowledged: true,
          confirmed: true,
          authorizationRevision: plan.authorizationRevision,
          standingAuthorizationRevision: plan.standingAuthorizationRevision ?? null,
        },
      })
      qc.setQueryData(queryKey, next)
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['ai', ownerId, serverBinding] }),
        qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding] }),
      ])
      notifySuccess('处理任务已按已核对的目标持久化；后续由后台任务执行')
      onChanged?.()
    } catch (error) {
      const message = failureText(error, showVersionDetails, '发起未确认，请刷新核对')
      setActionError(message)
      notifyError(message)
      if (error instanceof ApiError && (error.status === 400 || error.status === 409)) {
        setConfirmedRevision(null)
        await planQuery.refetch()
      }
    } finally {
      setBusy(false)
    }
  }

  if (status.isPending) return <Skeleton className="h-44" />
  if (status.isError || !status.data) {
    return <InlineAlert tone="crit" title="处理状态读取失败">
      {showVersionDetails ? errorText(status.error) : '处理状态暂时无法读取，请重试；页面不会自动重发。'}
      <Button size="sm" variant="ghost" onClick={() => void status.refetch()}>
        <RefreshCw className="size-4" aria-hidden />重试
      </Button>
    </InlineAlert>
  }
  const value = status.data
  return <Card className="space-y-4 p-5" aria-live="polite">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="text-sm font-semibold text-text-1">服务器笔记处理</h2>
          <StatusBadge dot={false} tone={overallTone(value.state)}>{overallLabel(value.state, showVersionDetails)}</StatusBadge>
          <Chip>{value.autoProcessingEnabled ? '自动处理已开启' : '自动处理已关闭'}</Chip>
        </div>
        <p className="mt-1 text-xs leading-5 text-text-3">
          {showVersionDetails ? '固定版本的全材料处理状态' : '当前内容的处理状态'}；两份结果均保留原稿，失败不会自动重发。
          {value.autoStarted ? ' 本次由材料完成事件自动发起。' : ''}
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        {value.manualStartAvailable && <Button size="sm" variant="primary" loading={busy}
          disabled={!planQuery.data || !consentConfirmed} onClick={() => void start()}>
          <Play className="size-4" aria-hidden />使用账号默认模型发起
        </Button>}
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => void status.refetch()}>
          <RefreshCw className="size-4" aria-hidden />刷新状态
        </Button>
      </div>
    </div>

    {value.manualStartAvailable && <div className="space-y-2">
      {planQuery.isPending && <Skeleton className="h-28" />}
      {planQuery.isError && <InlineAlert tone="crit" title="处理计划读取失败">
        {showVersionDetails ? errorText(planQuery.error) : '暂时无法核对本次处理目标；请刷新后重试。本页不会自动重新提交，自动处理只会在账号持续授权有效时由材料完成事件发起。'}
        <Button size="sm" variant="ghost" onClick={() => void planQuery.refetch()}>重试</Button>
      </InlineAlert>}
      {planQuery.data && <ProcessingPlan plan={planQuery.data} showVersionDetails={showVersionDetails}
        confirmed={consentConfirmed}
        onConfirmedChange={(value) => setConfirmedRevision(value
          ? planQuery.data?.authorizationRevision ?? null
          : null)} />}
    </div>}

    {value.authorization && !value.manualStartAvailable &&
      <ProcessingAuthorization authorization={value.authorization}
        showVersionDetails={showVersionDetails} />}

    {value.pendingReason && <InlineAlert tone="warn" title="尚未发起">
      {waitingText(value.pendingReason, showVersionDetails, '处理尚未发起，请核对材料是否完整或稍后手动重试；页面不会自动重发。')}
    </InlineAlert>}
    {actionError && <InlineAlert tone="crit" title="发起未完成">{actionError}</InlineAlert>}

    <div className="grid gap-3 md:grid-cols-2">
      <ChainCard chain={value.asr} showVersionDetails={showVersionDetails} />
      <ChainCard chain={value.derived} showVersionDetails={showVersionDetails} />
    </div>

    {value.artifacts.length > 0 && <div className="rounded-ctl border border-border p-3">
      <p className="text-xs font-medium text-text-2">现有结果</p>
      <div className="mt-2 flex flex-wrap gap-2">
        {value.artifacts.map((artifact) => <Chip key={artifact.artifactId}>
          {kindLabels[artifact.kind] ?? artifact.kind} · {artifact.headVersionId
            ? showVersionDetails ? '已有选用版本' : '已采用'
            : showVersionDetails ? '仅生成待选版本' : '有新结果待采用'}
        </Chip>)}
      </div>
    </div>}

    {value.issues.length > 0 && (showVersionDetails
      ? <ul className="space-y-1 text-xs leading-5 text-text-2">
          {value.issues.map((issue, index) => <li key={`${issue.code}-${index}`}>{issue.message}</li>)}
        </ul>
      : <p className="text-xs leading-5 text-warn">部分材料或处理项尚未完成，请刷新核对；页面不会自动重发。</p>)}

    <p className="text-xs leading-5 text-text-3">
      {showVersionDetails ? '当前仅覆盖该固定版本的全量材料' : '当前仅覆盖这篇笔记的全量材料'}
      {value.incrementalScopeSupported ? '；增量范围已支持。' : '；后续增量处理范围尚未实现，不会伪装成全量已覆盖。'}
      {value.updatedAt ? ` · 更新于 ${formatTime(value.updatedAt)}` : ''}
    </p>
  </Card>
}

export type { NoteProcessingStatus }
