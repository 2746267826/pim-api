import { useMemo, useState } from 'react'
import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { Card, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import { aiApi, type NoteAsrNode } from './api'
import { BatchMonitorPanel } from './note-asr-routing'
import { RouteConfigEditor } from './note-asr-routing'
import type { BatchJobView, BatchStatusView } from './note-asr-routing/types'
import { isActualCloudRoute, toRouteConfigSave } from './route-config-adapter'

const dateTime = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString() : '未知'

const batchStatus = (job: { status: string; statusRaw?: string | null; batchId?: string | null; outputImported: boolean; failedItems?: number | null; lastErrorCode?: string | null }): BatchStatusView => {
  switch (job.status.toUpperCase()) {
    case 'VALIDATING': return 'validating'
    case 'CREATED': case 'QUEUED': return 'waiting'
    case 'IN_PROGRESS': case 'RUNNING': return 'running'
    case 'COMPLETED': return job.outputImported && !job.lastErrorCode && !((job.failedItems ?? 0) > 0) ? 'completed' : 'partial'
    case 'FAILED': return 'failed'
    case 'EXPIRED': return 'expired'
    case 'CANCELLING': return 'cancelling'
    case 'CANCELLED': return 'cancelled'
    default: return 'unknown'
  }
}

export function NoteAsrRoutingView({ userId }: { userId: string }) {
  const qc = useQueryClient()
  const [pollError, setPollError] = useState('')
  const config = useQuery({ queryKey: ['ai', userId, 'asr-route-config'], queryFn: ({ signal }) => aiApi.asrRouteConfig(signal) })
  const batches = useQuery({ queryKey: ['ai', userId, 'asr-batch-jobs'], queryFn: ({ signal }) => aiApi.asrBatchJobs(signal), refetchInterval: 15000 })
  const models = useQuery({ queryKey: ['ai', userId, 'models'], queryFn: ({ signal }) => aiApi.models(signal) })
  const nodes = useQuery({ queryKey: ['ai', userId, 'asr-nodes'], queryFn: ({ signal }) => aiApi.asrRoutingNodes(signal), refetchInterval: 20000 })

  const localModels = useMemo(() => {
    const installed = new Map<string, { id: string; label: string; revision: string; enabled: boolean }>()
    for (const node of nodes.data ?? []) {
      const savedMatch = node.modelId === config.data?.localModelId &&
        node.modelRevision === config.data?.localModelRevision
      const existing = installed.get(node.modelId)
      if (savedMatch || !existing) installed.set(node.modelId, {
        id: node.modelId,
        label: `${node.modelId} · ${node.nodeIdExternal || '本机节点'} · ${node.modelRevision}`,
        revision: node.modelRevision,
        enabled: node.enabled && node.modelVerified,
      })
    }
    const saved = config.data?.localModelId
    const currentConfig = config.data
    if (saved && !nodes.data?.some((node) => node.modelId === saved && node.modelRevision === config.data?.localModelRevision)) {
      installed.set(saved, {
        id: currentConfig!.localModelId!,
        label: `${currentConfig!.localModelId!} · 已保存配置（当前节点未提供匹配版本）`,
        revision: currentConfig!.localModelRevision ?? '',
        enabled: true,
      })
    }
    return [...installed.values()]
  }, [config.data?.localModelId, config.data?.localModelRevision, nodes.data])
  const cloudModels = useMemo(() => (models.data ?? [])
    .filter((model) => model.enabled && model.capabilities.includes('Transcription'))
    .map((model) => ({ id: model.id, label: model.name, revision: model.revision, enabled: model.enabled, cloudConfigured: true, presetId: model.asrPresetId ?? null })), [models.data])

  const jobs: BatchJobView[] = useMemo(() => (batches.data ?? []).map((job) => {
    const status = batchStatus(job)
    return {
      id: job.id,
      title: `Batch ${job.model}`,
      status,
      createdAtLabel: dateTime(job.createdAt),
      destinationLabel: job.baseUrl,
      completionWindowLabel: job.completionWindow,
      cashEstimateLabel: '成功请求按Token计费，未执行部分不计费',
      progressLabel: `${job.completedItems ?? 0}/${job.totalItems ?? 0} 项`,
      totalItems: job.totalItems,
      completedItems: job.completedItems,
      failedItems: job.failedItems,
      providerRequestId: job.batchId,
      inputExpiresAtLabel: dateTime(job.inputExpireAt),
      batchExpiresAtLabel: dateTime(job.batchExpiresAt),
      outputAvailable: job.outputFileId !== null,
      errorLabel: job.lastErrorMessage ?? undefined,
    }
  }), [batches.data])

  async function save(intent: Parameters<typeof toRouteConfigSave>[0]) {
    if (!config.data) return
    const cloudId = intent.cloudModelId === undefined ? config.data.cloudModelId ?? null : intent.cloudModelId
    const localId = intent.localModelId === undefined ? config.data.localModelId ?? null : intent.localModelId
    const selectedCloud = cloudModels.find((model) => model.id === cloudId) ?? null
    const selectedLocal = localModels.find((model) => model.id === localId) ?? null
    try {
      await aiApi.asrRouteConfigSave(toRouteConfigSave(intent, config.data, selectedCloud, selectedLocal))
      await qc.invalidateQueries({ queryKey: ['ai', userId, 'asr-route-config'] })
    } catch (error) {
      throw error instanceof Error ? error : new Error('保存失败')
    }
  }

  async function poll(id: string) {
    setPollError('')
    try {
      await aiApi.asrBatchPoll(id)
      await qc.invalidateQueries({ queryKey: ['ai', userId, 'asr-batch-jobs'] })
    } catch (error) {
      setPollError(error instanceof Error ? error.message : '查询失败')
    }
  }

  async function cancel(id: string) {
    setPollError('')
    try {
      await aiApi.asrBatchCancel(id)
      await qc.invalidateQueries({ queryKey: ['ai', userId, 'asr-batch-jobs'] })
    } catch (error) {
      setPollError(error instanceof Error ? error.message : '取消失败')
    }
  }

  return <div className="min-w-0 space-y-4">
    {config.isPending && <Card className="p-5"><Skeleton className="h-56" /></Card>}
    {config.data && <RouteConfigEditor
      config={{
        revision: config.data.revision,
        defaultRouteId: isActualCloudRoute(config.data.defaultRoute) ? 'cloud' : 'local',
        localModelId: config.data.localModelId ?? null,
        localModelRevision: config.data.localModelRevision ?? null,
        cloudConfigured: config.data.cloudConfigured,
        cloudModelId: config.data.cloudModelId ?? null,
        consentVersion: config.data.consentVersion,
        updatedAtLabel: dateTime(config.data.updatedAt),
      }}
      accountScope={userId}
      loading={config.isFetching && !config.data}
      loadError={config.isError ? '路由配置读取失败' : null}
      onReload={() => void config.refetch()}
      localModels={localModels}
      cloudModels={cloudModels}
      onSave={save}
    />}
    {pollError && <InlineAlert tone="crit" title="Batch操作失败">{pollError}；任务记录保持不变，不会自动重建。</InlineAlert>}
    <BatchMonitorPanel jobs={jobs} onPoll={poll} onCancel={cancel} loading={batches.isPending}
      loadError={batches.isError ? 'Batch任务读取失败' : null} />
    <Card className="space-y-2 p-5">
      <h2 className="text-sm font-semibold text-text-1">本机节点</h2>
      <NodeList nodes={nodes} />
    </Card>
  </div>
}

function NodeList({ nodes }: { nodes: UseQueryResult<NoteAsrNode[]> }) {
  if (nodes.isPending) return <Skeleton className="h-20" />
  if (nodes.isError) return <InlineAlert tone="crit" title="节点状态读取失败">请刷新核对；不会自动注册或修改任何节点。</InlineAlert>
  if (!nodes.data?.length) return <p className="text-sm text-text-3">还没有注册的本机转写节点。</p>
  return <div className="space-y-2">{nodes.data.map((node) => <div key={node.nodeId}
    className="flex min-w-0 flex-wrap items-center justify-between gap-2 rounded-ctl border border-border p-3 text-xs">
    <span className="min-w-0 break-all font-mono">{node.nodeIdExternal || node.nodeId}</span>
    <span className="flex flex-wrap items-center gap-2">
      <StatusBadge size="xs" dot={false} tone={node.enabled && node.claimable ? 'ok' : 'warn'}>
        {node.enabled && node.claimable ? '可认领' : '不可认领'}
      </StatusBadge>
      <span className="text-text-3">心跳 {dateTime(node.lastHeartbeatAt)} · 已验证模型 {node.modelVerified ? '是' : '否'}</span>
    </span>
  </div>)}</div>
}
