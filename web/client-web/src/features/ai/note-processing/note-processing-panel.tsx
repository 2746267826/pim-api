import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Play, RefreshCw } from 'lucide-react'
import { Button, Card, Chip, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatTime } from '@/lib/datetime'
import { noteProcessingApi, type NoteProcessingChain, type NoteProcessingStatus } from './api'

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

function ChainCard({ chain }: { chain: NoteProcessingChain }) {
  const title = chain.kind === 'Asr' ? 'ASR 转写' : '多模态整理'
  return <div className="min-w-0 rounded-ctl border border-border bg-bg p-3">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <span className="text-sm font-medium text-text-1">{title}</span>
      <StatusBadge dot={false} tone={chainTone(chain.state)}>{chainLabels[chain.state] ?? chain.state}</StatusBadge>
    </div>
    <p className="mt-2 text-xs text-text-3">
      {chain.state === 'NotApplicable'
        ? '该固定版本没有录音，不创建空 ASR 任务。'
        : chain.taskCreated
          ? `${chain.completedItems} / ${chain.totalItems} 项完成${chain.failedItems ? ` · ${chain.failedItems} 项未完成` : ''}`
          : chain.waitingReason ?? '任务尚未创建'}
    </p>
    {chain.errors.length > 0 && <ul className="mt-2 space-y-1 text-xs leading-5 text-crit">
      {chain.errors.map((error, index) => <li key={`${chain.kind}-${index}`}>{error}</li>)}
    </ul>}
  </div>
}

export function NoteProcessingPanel({ ownerId, serverBinding, noteId, versionId, onChanged }: {
  ownerId: string
  serverBinding: string
  noteId: string
  versionId: string
  onChanged?: () => void
}) {
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
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

  async function start() {
    setBusy(true)
    setActionError('')
    try {
      const next = await noteProcessingApi.start({ noteId, versionId })
      qc.setQueryData(queryKey, next)
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['ai', ownerId, serverBinding] }),
        qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding] }),
      ])
      notifySuccess('处理任务已按账号默认目标持久化；后续由后台任务执行')
      onChanged?.()
    } catch (error) {
      const message = errorText(error)
      setActionError(message)
      notifyError(message)
    } finally {
      setBusy(false)
    }
  }

  if (status.isPending) return <Skeleton className="h-44" />
  if (status.isError || !status.data) {
    return <InlineAlert tone="crit" title="处理状态读取失败">
      {errorText(status.error)}
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
          <StatusBadge dot={false} tone={overallTone(value.state)}>{overallLabels[value.state] ?? value.state}</StatusBadge>
          <Chip>{value.autoProcessingEnabled ? '自动处理已开启' : '自动处理已关闭'}</Chip>
        </div>
        <p className="mt-1 text-xs leading-5 text-text-3">
          固定版本的全材料处理状态；两份结果均保留原稿，失败不会自动重发。
          {value.autoStarted ? ' 本次由材料完成事件自动发起。' : ''}
        </p>
      </div>
      <div className="flex flex-wrap gap-2">
        {value.manualStartAvailable && <Button size="sm" variant="primary" loading={busy} onClick={() => void start()}>
          <Play className="size-4" aria-hidden />使用账号默认模型发起
        </Button>}
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => void status.refetch()}>
          <RefreshCw className="size-4" aria-hidden />刷新状态
        </Button>
      </div>
    </div>

    {value.pendingReason && <InlineAlert tone="warn" title="尚未发起">{value.pendingReason}</InlineAlert>}
    {actionError && <InlineAlert tone="crit" title="发起未完成">{actionError}</InlineAlert>}

    <div className="grid gap-3 md:grid-cols-2">
      <ChainCard chain={value.asr} />
      <ChainCard chain={value.derived} />
    </div>

    {value.artifacts.length > 0 && <div className="rounded-ctl border border-border p-3">
      <p className="text-xs font-medium text-text-2">现有结果</p>
      <div className="mt-2 flex flex-wrap gap-2">
        {value.artifacts.map((artifact) => <Chip key={artifact.artifactId}>
          {kindLabels[artifact.kind] ?? artifact.kind} · {artifact.headVersionId ? '已有选用版本' : '仅生成待选版本'}
        </Chip>)}
      </div>
    </div>}

    {value.issues.length > 0 && <ul className="space-y-1 text-xs leading-5 text-text-2">
      {value.issues.map((issue, index) => <li key={`${issue.code}-${index}`}>{issue.message}</li>)}
    </ul>}

    <p className="text-xs leading-5 text-text-3">
      当前仅覆盖该固定版本的全量材料
      {value.incrementalScopeSupported ? '；增量范围已支持。' : '；后续增量处理范围尚未实现，不会伪装成全量已覆盖。'}
      {value.updatedAt ? ` · 更新于 ${formatTime(value.updatedAt)}` : ''}
    </p>
  </Card>
}

export type { NoteProcessingStatus }
