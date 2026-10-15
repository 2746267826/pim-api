import { useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, Bot, Download, FileDown, RefreshCw, ScrollText, Trash2 } from 'lucide-react'
import { ApiError, apiDelete, apiGet, apiPost, apiPut, apiUpload, fetchBlob } from '@/api/client'
import type { components } from '@/api/generated'
import { downloadCsv, downloadJson } from '@/lib/download'
import { formatTime } from '@/lib/datetime'

import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, Card, CardTitle, Chip, ConfirmDialog, Dialog, DialogBody, DialogClose, DialogContent, DialogFooter, DialogHeader, EmptyState, InlineAlert, Input, Label, PageHeader, Segmented, Select, Skeleton, StatusBadge, Switch } from '@/components/ui'
import { EventEditorDialog } from '@/features/calendar/components/event-editor-dialog'
import { useCalendars } from '@/features/calendar/queries'
import type { EventResponse } from '@/features/calendar/types'
import { cn } from '@/lib/utils'

/* ── /settings/data-reliability 数据可信度 ─────────────────── */

interface InspectionRule {
  code: string
  key: string
  name: string
  group: string
  groupLabel: string
  status: string
  statusLabel: string
  detail: string
  currentValueLabel: string | null
  threshold: string
  criterion: string
  rationale: string
  totalViolations: number
  /** 窗内违规（决定颜色）；旧名 newViolations 已下线（PR #361） */
  windowViolations: number
  /** 历史欠账（只计数，不参与颜色判定；含绿灯尺子的欠账） */
  historicalViolations: number
  /** 仅 S3 返回：本次取数实际覆盖的业务日数 */
  scanCoveredDays: number | null
  samples: string[]
}

interface InspectionReport {
  inspectedAtUtc: string
  redCount: number
  yellowCount: number
  greenCount: number
  /** ≡ windowViolations（总览违规只由窗内违规产生） */
  totalViolations: number
  /** 历史欠账合计（含绿灯尺子的欠账） */
  historicalViolations: number
  /** 考核线 = max(体检时刻 − 考核窗, 进程启动时刻) */
  assessmentStartUtc: string
  /** 生效考核窗时长（小时），默认 168（7 天） */
  assessmentWindowHours: number
  rules: InspectionRule[]
  message: string
}

export function DataReliabilityPage() {
  const qc = useQueryClient()
  const report = useQuery({
    queryKey: ['data-reliability', 'inspection'],
    queryFn: () => apiGet<InspectionReport>('/api/v1/data-reliability/inspection'),
  })
  const refresh = useMutation({
    mutationFn: () => apiPost<InspectionReport>('/api/v1/data-reliability/inspection/refresh', {}),
    onSuccess: (data) => {
      qc.setQueryData(['data-reliability', 'inspection'], data)
      notifySuccess('体检完成')
    },
  })
  const [detailRule, setDetailRule] = useState<InspectionRule | null>(null)

  const groups = useMemo(() => {
    const map = new Map<string, InspectionRule[]>()
    for (const r of report.data?.rules ?? []) {
      const list = map.get(r.groupLabel) ?? []
      list.push(r)
      map.set(r.groupLabel, list)
    }
    return [...map.entries()]
  }, [report.data])

  return (
    <div>
      <PageHeader
        title="数据可信度"
        subtitle={
          report.data
            ? `体检于 ${formatTime(report.data.inspectedAtUtc)} · 考核线 ${formatTime(report.data.assessmentStartUtc)} 起`
            : '13 条尺子数据体检（红黄绿只由窗内违规决定，历史欠账只计数）'
        }
        actions={
          <Button variant="primary" size="sm" loading={refresh.isPending} onClick={() => refresh.mutate()}>
            <RefreshCw className="size-4" aria-hidden /> 重新体检
          </Button>
        }
      />

      {report.data && (
        <div className="mb-4 grid grid-cols-2 gap-3 lg:grid-cols-4">
          <Tile label="红灯" value={report.data.redCount} tone="crit" />
          <Tile label="黄灯" value={report.data.yellowCount} tone="warn" />
          <Tile label="绿灯" value={report.data.greenCount} tone="ok" />
          <Tile
            label="窗内违规"
            value={report.data.totalViolations}
            tone={report.data.totalViolations > 0 ? 'warn' : 'ok'}
            hint={`历史欠账 ${report.data.historicalViolations} · 考核窗 ${report.data.assessmentWindowHours}h`}
          />
        </div>
      )}

      {report.isLoading ? (
        <div className="space-y-2">{Array.from({ length: 5 }, (_, i) => <Skeleton key={i} className="h-14" />)}</div>
      ) : (
        <div className="space-y-4">
          {groups.map(([groupLabel, rules]) => (
            <Card key={groupLabel} className="p-4">
              <CardTitle className="flex items-center gap-2">
                <span className="h-3.5 w-[3px] shrink-0 rounded-full bg-primary" aria-hidden />
                {groupLabel}
              </CardTitle>
              <div className="mt-2 divide-y divide-divider">
                {rules.map((r) => (
                  <button
                    key={r.code}
                    type="button"
                    onClick={() => setDetailRule(r)}
                    className="flex w-full items-center gap-3 py-2 text-left outline-none hover:bg-surface"
                  >
                    <StatusBadge size="xs" tone={r.status === 'Red' || r.status === 'red' ? 'crit' : r.status === 'Yellow' || r.status === 'yellow' ? 'warn' : 'ok'} dot={false}>
                      {r.statusLabel || r.status}
                    </StatusBadge>
                    <span className="min-w-0 flex-1 truncate text-[13px] text-text-1">{r.name}</span>
                    <span className="tnum shrink-0 text-xs text-text-3">{r.currentValueLabel ?? '—'}</span>
                    <span className={cn('tnum shrink-0 text-xs', r.windowViolations > 0 ? 'text-warn' : 'text-text-4')}>
                      窗内 {r.windowViolations}
                      {r.historicalViolations > 0 && <span className="text-text-4"> / 欠账 {r.historicalViolations}</span>}
                    </span>
                  </button>
                ))}
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* 规则详情弹窗（含违规样例 + CSV 导出） */}
      <Dialog open={detailRule != null} onOpenChange={(o) => !o && setDetailRule(null)}>
        <DialogContent className="max-w-[560px]">
          <DialogHeader title={detailRule?.name ?? ''} description={detailRule ? `${detailRule.code} · 阈值：${detailRule.threshold}` : undefined} />
          <DialogBody className="space-y-3 text-[13px]">
            {detailRule && (
              <>
                <Section label="判据">{detailRule.criterion}</Section>
                <Section label="为什么这么定">{detailRule.rationale}</Section>
                <Section label="当前情况">
                  {detailRule.currentValueLabel ?? '—'} · 违规合计 {detailRule.totalViolations}
                  （窗内 {detailRule.windowViolations} / 历史欠账 {detailRule.historicalViolations}）
                  {detailRule.scanCoveredDays != null && ` · 本次覆盖 ${detailRule.scanCoveredDays} 个业务日`}
                </Section>
                <div>
                  <Label>违规样例（前 10 条）</Label>
                  <ul className="space-y-0.5 text-xs text-text-3">
                    {(detailRule.samples ?? []).slice(0, 10).map((s, i) => <li key={i} className="truncate">· {s}</li>)}
                    {(detailRule.samples?.length ?? 0) === 0 && <li>无样例</li>}
                  </ul>
                </div>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={async () => {
                    const res = await apiGet<{ items: { ruleCode: string; id: string; deviceId: string; occurredAtUtc: string; isNew: boolean; fields: Record<string, string> }[]; totalCount: number; truncated: boolean }>(
                      `/api/v1/data-reliability/rules/${encodeURIComponent(detailRule.code)}/violations?limit=2000`,
                    )
                    downloadCsv(
                      `${detailRule.code}-violations.csv`,
                      // isNew：true=窗内（考核线之后），false=历史欠账（PR #361）
                      res.items.map((v) => ({ 尺子: v.ruleCode, ID: v.id, 设备: v.deviceId, 时间: v.occurredAtUtc, 窗内: v.isNew ? '是' : '否', ...v.fields })),
                    )
                    notifySuccess(`已导出 ${res.items.length} 条${res.truncated ? '（已截断）' : ''}`)
                  }}
                >
                  <FileDown className="size-3.5" aria-hidden /> 导出全部违规 CSV
                </Button>
              </>
            )}
          </DialogBody>
          <div className="flex justify-end px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setDetailRule(null)}>关闭</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

function Tile({ label, value, tone, hint }: { label: string; value: number; tone: 'ok' | 'warn' | 'crit' | 'neutral'; hint?: string }) {
  const toneCls = { ok: 'text-ok', warn: 'text-warn', crit: 'text-crit', neutral: 'text-text-1' }[tone]
  return (
    <Card className="p-4">
      <div className="text-xs text-text-3">{label}</div>
      <div className={cn('tnum mt-1 text-2xl font-semibold', toneCls)}>{value}</div>
      {hint && <div className="text-[11px] text-text-4">{hint}</div>}
    </Card>
  )
}

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <Label>{label}</Label>
      <p className="text-text-2">{children}</p>
    </div>
  )
}

/* ── /settings/ai AI 设置 ─────────────────────────────────── */

interface AiStatus {
  enabled: boolean
  provider: string
  baseUrl: string
  defaultModel: string
  lastHealthCheckAt: string | null
  lastError: string | null
  recentSuccessfulCallAt: string | null
}

interface AiRequestItem {
  id: string
  startedAt: string
  module: string
  purpose: string
  model: string
  status: string
  totalTokens: number | null
  estimatedCost: number | null
  durationMs: number | null
}

export function AiSettingsPage() {
  const qc = useQueryClient()
  const status = useQuery({ queryKey: ['ai', 'status'], queryFn: () => apiGet<AiStatus>('/api/v1/ai/status'), refetchInterval: 60_000 })
  const usage = useQuery({
    queryKey: ['ai', 'usage'],
    queryFn: () => apiGet<{ requestCount: number; successCount: number; failureCount: number; totalTokens: number; estimatedCost: number }>('/api/v1/ai/usage/summary'),
    refetchInterval: 60_000,
  })
  const [page, setPage] = useState(1)
  const logs = useQuery({
    queryKey: ['ai', 'requests', page],
    queryFn: () => apiGet<{ items: AiRequestItem[]; totalCount: number; totalPages: number }>(`/api/v1/ai/requests?page=${page}&pageSize=20`),
    refetchInterval: 60_000,
  })
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null)

  const tone = status.data?.enabled ? (status.data.lastError ? 'warn' : 'ok') : 'neutral'

  return (
    <div>
      <PageHeader
        title="AI 设置"
        subtitle="LiteLLM 网关状态、用量与请求日志"
        actions={
          <>
            <Button
              variant="secondary"
              size="sm"
              onClick={async () => {
                try {
                  const res = await apiPost<{ status: string; responseText: string | null; userFacingError: string | null }>('/api/v1/ai/test', {})
                  notifySuccess(`测试结果：${res.status}`, res.responseText ?? res.userFacingError ?? undefined)
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '测试失败')
                }
              }}
            >
              <Bot className="size-4" aria-hidden /> 测试
            </Button>
            <Button
              variant="primary"
              size="sm"
              onClick={async () => {
                await apiPost('/api/v1/ai/health-check', {})
                notifySuccess('已触发健康检查')
                void qc.invalidateQueries({ queryKey: ['ai'] })
              }}
            >
              <Activity className="size-4" aria-hidden /> 健康检查
            </Button>
          </>
        }
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[340px_minmax(0,1fr)]">
        <div className="space-y-4">
          <Card className="p-4">
            <div className="flex items-center gap-2">
              <CardTitle>网关状态</CardTitle>
              <StatusBadge tone={tone} className="ml-auto">{status.data?.enabled ? (status.data.lastError ? '有错误' : '正常') : '未启用'}</StatusBadge>
            </div>
            <div className="mt-2 space-y-1 text-xs text-text-3">
              <div>提供方：{status.data?.provider ?? '—'}</div>
              <div className="mono truncate">{status.data?.baseUrl ?? '—'}</div>
              <div>默认模型：{status.data?.defaultModel ?? '—'}</div>
              <div>最近检查：{status.data?.lastHealthCheckAt ? formatTime(status.data.lastHealthCheckAt) : '—'}</div>
              {status.data?.lastError && <div className="text-crit">{status.data.lastError}</div>}
            </div>
          </Card>

          <Card className="p-4">
            <CardTitle>用量总览</CardTitle>
            <div className="mt-3 grid grid-cols-2 gap-2 text-center">
              <MiniTile label="请求数" value={usage.data?.requestCount ?? '—'} />
              <MiniTile label="成功率" value={usage.data ? `${Math.round((usage.data.successCount / Math.max(1, usage.data.requestCount)) * 100)}%` : '—'} />
              <MiniTile label="总 token" value={usage.data?.totalTokens?.toLocaleString() ?? '—'} />
              <MiniTile label="估算成本" value={usage.data?.estimatedCost != null ? usage.data.estimatedCost.toFixed(4) : '—'} />
            </div>
          </Card>
        </div>

        <Card className="p-4">
          <div className="flex items-center gap-2">
            <CardTitle>请求日志</CardTitle>
            <span className="tnum ml-auto text-xs text-text-4">共 {logs.data?.totalCount ?? 0} 条</span>
          </div>
          <div className="mt-2 overflow-x-auto">
            {logs.isLoading ? (
              <Skeleton className="h-40" />
            ) : (logs.data?.items.length ?? 0) === 0 ? (
              <EmptyState size="sm" title="暂无 AI 请求" />
            ) : (
              <table className="w-full text-[13px]">
                <thead>
                  <tr className="bg-surface text-left text-xs text-text-3">
                    <th className="px-3 py-2 font-medium">时间</th>
                    <th className="px-3 py-2 font-medium">模块/用途</th>
                    <th className="px-3 py-2 font-medium">模型</th>
                    <th className="px-3 py-2 font-medium">状态</th>
                    <th className="px-3 py-2 text-right font-medium">token</th>
                    <th className="px-3 py-2 text-right font-medium">耗时</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-divider">
                  {logs.data!.items.map((l) => (
                    <tr key={l.id} className="cursor-pointer transition-colors hover:bg-surface" onClick={() => void apiGet(`/api/v1/ai/requests/${l.id}`).then((d) => setDetail(d as Record<string, unknown>))}>
                      <td className="tnum px-3 py-1.5 text-text-3">{formatTime(l.startedAt)}</td>
                      <td className="px-3 py-1.5 text-text-2">{l.module} / {l.purpose}</td>
                      <td className="mono px-3 py-1.5 text-xs text-text-3">{l.model}</td>
                      <td className="px-3 py-1.5">
                        <StatusBadge tone={l.status === 'Succeeded' ? 'ok' : l.status === 'Blocked' ? 'warn' : 'crit'} dot={false}>{l.status}</StatusBadge>
                      </td>
                      <td className="tnum px-3 py-1.5 text-right text-text-3">{l.totalTokens ?? '—'}</td>
                      <td className="tnum px-3 py-1.5 text-right text-text-3">{l.durationMs != null ? `${l.durationMs}ms` : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
          {(logs.data?.totalPages ?? 0) > 1 && (
            <div className="mt-3 flex items-center justify-end gap-2 text-[13px] text-text-3">
              <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
              <span className="tnum">{page} / {logs.data!.totalPages}</span>
              <Button variant="secondary" size="sm" disabled={page >= (logs.data?.totalPages ?? 1)} onClick={() => setPage((p) => p + 1)}>下一页</Button>
            </div>
          )}
        </Card>
      </div>

      <Dialog open={detail != null} onOpenChange={(o) => !o && setDetail(null)}>
        <DialogContent className="max-w-[640px]">
          <DialogHeader title="请求详情" />
          <DialogBody>
            <pre className="mono max-h-[60vh] overflow-auto rounded-ctl bg-surface p-3 text-xs text-text-2">
              {JSON.stringify(detail, null, 2)}
            </pre>
          </DialogBody>
          <div className="flex justify-end px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setDetail(null)}>关闭</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

function MiniTile({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-ctl bg-surface py-2">
      <div className="tnum text-lg font-semibold text-text-1">{value}</div>
      <div className="text-[10px] text-text-4">{label}</div>
    </div>
  )
}

/* ── /settings/mcp MCP 设置 ───────────────────────────────── */

interface McpActivityEntry {
  timestamp: string
  clientName: string
  toolName: string
  statusCode: number
  durationMs: number
  argumentsSummary: string
  ownerUserId?: string
}

interface McpClient {
  id: string
  name: string
  status: 'active' | 'revoked'
  tokenPrefix: string
  permissions: { read?: Record<string, boolean>; write?: Record<string, boolean> }
  createdAt: string
  lastSeenAt: string | null
  callCount: number
  writeCallCount: number
  lastTool: string | null
  online: boolean
}

export function McpSettingsPage() {
  const qc = useQueryClient()
  /* 固定 10 秒轮询（规格） */
  const clients = useQuery({ queryKey: ['mcp', 'clients'], queryFn: () => apiGet<McpClient[]>('/api/v1/mcp/clients'), refetchInterval: 10_000 })
  const activity = useQuery({
    queryKey: ['mcp', 'activity'],
    queryFn: () => apiGet<McpActivityEntry[]>('/api/v1/mcp/activity'),
    refetchInterval: 10_000,
  })
  const catalog = useQuery({
    queryKey: ['mcp', 'catalog'],
    queryFn: () => apiGet<{ read: { name: string; group: string; description: string }[]; write: { name: string; group: string; description: string }[] }>('/api/v1/mcp/catalog'),
    staleTime: Infinity,
  })
  const [newOpen, setNewOpen] = useState(false)
  const [newName, setNewName] = useState('')
  const [createdToken, setCreatedToken] = useState<{ name: string; token: string } | null>(null)
  const [permEditing, setPermEditing] = useState<McpClient | null>(null)
  const [logEntry, setLogEntry] = useState<McpActivityEntry | null>(null)

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['mcp'] })

  return (
    <div>
      <PageHeader
        title="MCP 设置"
        subtitle="客户端、令牌与工具权限（列表与流水每 10 秒轮询）"
        actions={
          <Button variant="primary" size="sm" onClick={() => setNewOpen(true)}>
            <ScrollText className="size-4" aria-hidden /> 新建客户端
          </Button>
        }
      />

      <div className="space-y-4">
        <Card className="overflow-x-auto p-4">
          <CardTitle>客户端（{clients.data?.length ?? 0}）</CardTitle>
          <table className="mt-2 w-full text-[13px]">
            <thead>
              <tr className="bg-surface text-left text-xs text-text-3">
                <th className="px-3 py-2 font-medium">名称</th>
                <th className="px-3 py-2 font-medium">token 前缀</th>
                <th className="px-3 py-2 font-medium">状态</th>
                <th className="px-3 py-2 text-right font-medium">调用</th>
                <th className="px-3 py-2 font-medium">最近工具</th>
                <th className="px-3 py-2 font-medium">操作</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-divider">
              {(clients.data ?? []).map((c) => (
                <tr key={c.id}>
                  <td className="px-3 py-2 text-text-1">{c.name}</td>
                  <td className="mono px-3 py-2 text-xs text-text-3">{c.tokenPrefix}…</td>
                  <td className="px-3 py-2">
                    <StatusBadge tone={c.status === 'revoked' ? 'neutral' : c.online ? 'ok' : 'warn'} dot={false}>
                      {c.status === 'revoked' ? '已吊销' : c.online ? '在线' : '离线'}
                    </StatusBadge>
                  </td>
                  <td className="tnum px-3 py-2 text-right text-text-3">
                    {c.callCount.toLocaleString()}
                    {c.writeCallCount > 0 && <span className="ml-1 text-[11px] text-text-4">（写 {c.writeCallCount}）</span>}
                  </td>
                  <td className="px-3 py-2 text-xs text-text-3">{c.lastTool ?? '—'}</td>
                  <td className="px-3 py-2">
                    <div className="flex gap-1">
                      <Button variant="ghost" size="sm" onClick={() => setPermEditing(c)}>权限</Button>
                      {c.status === 'active' && (
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={async () => {
                            if (!window.confirm(`吊销「${c.name}」的 token？`)) return
                            await apiPost(`/api/v1/mcp/clients/${c.id}/revoke`, {})
                            notifySuccess('已吊销')
                            invalidate()
                          }}
                        >
                          吊销
                        </Button>
                      )}
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-crit"
                        onClick={async () => {
                          if (!window.confirm(`删除客户端「${c.name}」？（不可恢复）`)) return
                          await apiDelete(`/api/v1/mcp/clients/${c.id}`)
                          notifySuccess('已删除')
                          invalidate()
                        }}
                      >
                        删除
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
              {(clients.data?.length ?? 0) === 0 && (
                <tr><td colSpan={6}><EmptyState size="sm" title="还没有 MCP 客户端" description="新建后把 token 配置到 Claude Code / Codex 等客户端。" /></td></tr>
              )}
            </tbody>
          </table>
        </Card>

        <Card className="p-4">
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>实时调用流水</CardTitle>
            <span className="tnum text-xs text-text-4">最近 {activity.data?.length ?? 0} 条（内存队列，最多 100 条）</span>
            <Button
              variant="secondary"
              size="sm"
              className="ml-auto"
              loading={activity.isFetching}
              onClick={() => void activity.refetch()}
            >
              <RefreshCw className="size-3.5" aria-hidden /> 刷新
            </Button>
          </div>
          <div className="mt-2 overflow-x-auto">
            {(activity.data?.length ?? 0) === 0 ? (
              <EmptyState size="sm" title="暂无调用记录" description="MCP 客户端调用工具后会出现这里（每 10 秒自动刷新）。" />
            ) : (
              <table className="w-full text-[12px]">
                <thead>
                  <tr className="bg-surface text-left text-xs text-text-3">
                    <th className="px-3 py-2 font-medium">时间</th>
                    <th className="px-3 py-2 font-medium">客户端</th>
                    <th className="px-3 py-2 font-medium">工具</th>
                    <th className="px-3 py-2 font-medium">状态</th>
                    <th className="px-3 py-2 text-right font-medium">耗时</th>
                    <th className="px-3 py-2 font-medium">参数</th>
                    <th className="px-3 py-2" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-divider">
                  {(activity.data ?? []).map((a, i) => (
                    <tr key={i} className="transition-colors hover:bg-surface">
                      <td className="tnum px-3 py-1.5 text-text-4">{formatTime(a.timestamp)}</td>
                      <td className="px-3 py-1.5 text-text-2">{a.clientName}</td>
                      <td className="mono px-3 py-1.5 text-text-1">{a.toolName}</td>
                      <td className="px-3 py-1.5">
                        <StatusBadge tone={a.statusCode === 200 ? 'ok' : 'crit'} dot={false}>{a.statusCode}</StatusBadge>
                      </td>
                      <td className="tnum px-3 py-1.5 text-right text-text-3">{a.durationMs}ms</td>
                      <td className="mono max-w-72 truncate px-3 py-1.5 text-[11px] text-text-3" title={a.argumentsSummary}>
                        {a.argumentsSummary || '—'}
                      </td>
                      <td className="px-3 py-1.5 text-right">
                        <Button variant="ghost" size="sm" onClick={() => setLogEntry(a)}>完整日志</Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
          <p className="mt-2 text-[11px] text-text-4">
            记录为进程内存队列（非持久化）：含时间、客户端、工具名、状态码、耗时与入参摘要（截断 120 字符）；
            完整入参 JSON 见每行「完整日志」。
          </p>
        </Card>
      </div>

      {/* 新建客户端抽屉（一次性 token） */}
      <Dialog open={newOpen} onOpenChange={setNewOpen}>
        <DialogContent className="max-w-[480px]">
          <DialogHeader title="新建 MCP 客户端" description="新客户端默认读权限全开、写权限全关" />
          <DialogBody className="space-y-3">
            <div>
              <Label htmlFor="mcp-name">名称</Label>
              <Input id="mcp-name" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="如 Claude Code" />
            </div>
            {createdToken && (
              <>
                <InlineAlertWarn title="Token 只显示这一次，请立即保存">
                  <span className="mono break-all">{createdToken.token}</span>
                </InlineAlertWarn>
                <div>
                  <Label>mcp.json 配置示例</Label>
                  <pre className="mono overflow-x-auto rounded-ctl bg-surface p-3 text-[11px] text-text-2">{JSON.stringify({
                    mcpServers: { pim: { type: 'http', url: `${window.location.origin}/mcp`, headers: { Authorization: `Bearer ${createdToken.token}` } } },
                  }, null, 2)}</pre>
                </div>
              </>
            )}
          </DialogBody>
          <div className="flex justify-end gap-2 px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => { setNewOpen(false); setCreatedToken(null); setNewName('') }}>关闭</Button>
            {!createdToken && (
              <Button
                variant="primary"
                size="sm"
                disabled={!newName.trim()}
                onClick={async () => {
                  try {
                    const res = await apiPost<{ token: string; client: { name: string } }>('/api/v1/mcp/clients', { name: newName.trim() })
                    setCreatedToken({ name: res.client.name, token: res.token })
                    await navigator.clipboard.writeText(res.token).catch(() => {})
                    notifySuccess('已创建并复制 token')
                    invalidate()
                  } catch (err) {
                    notifyError(err instanceof Error ? err.message : '创建失败')
                  }
                }}
              >
                创建
              </Button>
            )}
          </div>
        </DialogContent>
      </Dialog>

      {/* 完整调用日志 */}
      <Dialog open={logEntry != null} onOpenChange={(o) => !o && setLogEntry(null)}>
        <DialogContent className="max-w-[560px]">
          <DialogHeader
            title={logEntry?.toolName ?? ''}
            description={logEntry ? `${logEntry.clientName} · ${formatTime(logEntry.timestamp)}` : undefined}
          />
          <DialogBody className="space-y-3 text-[13px]">
            {logEntry && (
              <>
                <div className="grid grid-cols-2 gap-x-4 gap-y-1.5">
                  {[
                    ['状态码', String(logEntry.statusCode)],
                    ['耗时', `${logEntry.durationMs} ms`],
                    ['调用时间', formatTime(logEntry.timestamp)],
                    ['归属用户', logEntry.ownerUserId ?? '—'],
                  ].map(([k, v]) => (
                    <div key={k} className="flex gap-2">
                      <span className="w-20 shrink-0 text-text-3">{k}</span>
                      <span className="tnum min-w-0 flex-1 break-all text-text-1">{v}</span>
                    </div>
                  ))}
                </div>
                <div>
                  <Label>入参（argumentsSummary）</Label>
                  <pre className="mono max-h-56 overflow-auto rounded-ctl bg-surface p-3 text-[11px] text-text-2">
                    {formatArgsSummary(logEntry.argumentsSummary)}
                  </pre>
                </div>
                <InlineAlertNote>
                  服务端仅保留 120 字符内的入参摘要（内存队列）。需要更完整的调用审计请查看对应域的审计时间线。
                </InlineAlertNote>
              </>
            )}
          </DialogBody>
          <div className="flex justify-end gap-2 px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setLogEntry(null)}>关闭</Button>
          </div>
        </DialogContent>
      </Dialog>

      {/* 权限矩阵 */}
      {permEditing && catalog.data && (
        <McpPermissionDialog client={permEditing} catalog={catalog.data} onClose={() => setPermEditing(null)} onSaved={invalidate} />
      )}
    </div>
  )
}

function InlineAlertNote({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-ctl border border-info-border bg-info-soft px-3 py-2 text-[12px] text-info">{children}</div>
  )
}

/** 入参摘要美化：是 JSON 则缩进展示，否则原样 */
function formatArgsSummary(raw: string): string {
  if (!raw) return '（无参数）'
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

function InlineAlertWarn({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2.5 text-[13px] text-warn">
      <div className="font-medium">{title}</div>
      <div className="mt-1 text-text-2">{children}</div>
    </div>
  )
}

function McpPermissionDialog({
  client,
  catalog,
  onClose,
  onSaved,
}: {
  client: McpClient
  catalog: { read: { name: string; group: string }[]; write: { name: string; group: string }[] }
  onClose: () => void
  onSaved: () => void
}) {
  const [read, setRead] = useState<Record<string, boolean>>(() => ({ ...Object.fromEntries(catalog.read.map((t) => [t.name, true])), ...client.permissions.read }))
  const [write, setWrite] = useState<Record<string, boolean>>(() => ({ ...Object.fromEntries(catalog.write.map((t) => [t.name, false])), ...client.permissions.write }))
  const [tab, setTab] = useState<'read' | 'write'>('read')

  const tools = tab === 'read' ? catalog.read : catalog.write
  const state = tab === 'read' ? read : write
  const setState = tab === 'read' ? setRead : setWrite
  const groups = useMemo(() => [...new Set(tools.map((t) => t.group))], [tools])

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[620px]">
        <DialogHeader title={`工具权限：${client.name}`} description={`读 ${catalog.read.length} 个 / 写 ${catalog.write.length} 个工具`} />
        <DialogBody className="space-y-3">
          <div className="flex items-center gap-2">
            <Segmented
              value={tab}
              onValueChange={(v) => setTab(v)}
              options={[
                { value: 'read', label: `读（${catalog.read.length}）` },
                { value: 'write', label: `写（${catalog.write.length}）` },
              ]}
            />
            <Button variant="ghost" size="sm" className="ml-auto" onClick={() => setState(Object.fromEntries(tools.map((t) => [t.name, true])))}>全选</Button>
            <Button variant="ghost" size="sm" onClick={() => setState(Object.fromEntries(tools.map((t) => [t.name, false])))}>全不选</Button>
          </div>
          <div className="max-h-[45vh] space-y-3 overflow-y-auto">
            {groups.map((g) => (
              <div key={g}>
                <div className="mb-1 text-xs font-medium text-text-3">{g}</div>
                <div className="space-y-1">
                  {tools.filter((t) => t.group === g).map((t) => (
                    <label key={t.name} className="flex items-center gap-2 rounded-ctl px-2 py-1 hover:bg-surface">
                      <Switch checked={state[t.name] ?? false} onCheckedChange={(v) => setState((prev) => ({ ...prev, [t.name]: v }))} />
                      <span className="mono text-xs text-text-2">{t.name}</span>
                    </label>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </DialogBody>
        <div className="flex justify-end gap-2 px-5 py-3.5">
          <Button variant="secondary" size="sm" onClick={onClose}>取消</Button>
          <Button
            variant="primary"
            size="sm"
            onClick={async () => {
              try {
                await apiPut(`/api/v1/mcp/clients/${client.id}`, { permissions: { read, write } })
                notifySuccess('权限已保存')
                onSaved()
                onClose()
              } catch (err) {
                notifyError(err instanceof Error ? err.message : '保存失败')
              }
            }}
          >
            保存
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/* ── /settings/calendar-data 日程数据管理 ─────────────────── */

interface AdminEvent {
  id: string
  title: string
  calendarId: string
  dtStart: string
  dtEnd: string
  /** 契约字段名（PW-05）；旧数据可能是小写 rrule */
  rRule?: string | null
  rrule?: string | null
  isCancelled: boolean
  /*
   * 列表是服务端**展开后的 occurrence**：`id` 是派生合成 id（不在 events 表里），
   * `originalEventId` / `seriesMasterId` 才指向库中那一行存储事件。
   * 导出端点按存储事件 id 过滤，因此导出前必须回落到 originalEventId。
   */
  originalEventId?: string | null
  seriesMasterId?: string | null
}

export function CalendarDataManagerPage() {
  const qc = useQueryClient()
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const calendars = useQuery({ queryKey: ['calendar', 'calendars', 'all'], queryFn: () => apiGet<{ id: string; name: string; color: string; kind: string }[]>('/api/v1/calendar/calendars') })
  const events = useQuery({
    queryKey: ['calendar', 'admin-events', search, page],
    queryFn: () => apiGet<{ items: AdminEvent[]; totalCount: number; totalPages: number }>(`/api/v1/calendar/events?page=${page}&pageSize=50${search ? `&search=${encodeURIComponent(search)}` : ''}`),
    placeholderData: (prev) => prev,
  })
  const [importTarget, setImportTarget] = useState('')
  const [editingEvent, setEditingEvent] = useState<EventResponse | null>(null)
  const calendarBooks = useCalendars('calendar')
  const [deleteOpen, setDeleteOpen] = useState(false)
  const fileRef = useEffectImport()

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['calendar'] })

  async function exportIcs(ids?: string[]) {
    const q = ids?.length ? `?ids=${ids.join(',')}` : ''
    try {
      /*
       * PW-08：导出必须走统一传输——
       * 配置的 apiBase（apiUrl）、统一 Bearer、401 走共享单飞刷新并重放一次、
       * 非 2xx 抛错。只有真正拿到 2xx 的 ICS 内容才提示成功。
       */
      const blob = await fetchBlob(`/api/v1/calendar/export-ics${q}`, undefined, {
        refreshOn401: true,
      })
      if (blob.size === 0) throw new Error('导出内容为空')
      /*
       * 服务端对「命中 0 条」返回的是 HTTP 200 + 只有 VCALENDAR/VTIMEZONE 的空日历（约 195 B），
       * 不是 0 字节；因此必须按内容判空，不能只判 blob.size，否则空日历会被当成导出成功。
       */
      const icsText = await blob.text()
      if (!/BEGIN:VEVENT/.test(icsText)) {
        throw new Error('导出内容为空：没有可导出的事件')
      }
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'pim-events.ics'
      a.click()
      setTimeout(() => URL.revokeObjectURL(url), 500)
      notifySuccess('ICS 已导出')
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '导出失败')
    }
  }

  return (
    <div>
      <PageHeader
        title="日程数据管理"
        subtitle={`共 ${events.data?.totalCount ?? 0} 条事件`}
        actions={
          <>
            <Select
              className="h-8 w-36"
              value={importTarget}
              onValueChange={setImportTarget}
              options={[{ value: '', label: '自动选择日历' }, ...(calendars.data ?? []).filter((c) => c.kind === 'calendar').map((c) => ({ value: c.id, label: c.name }))]}
              ariaLabel="导入目标日历"
            />
            <Button variant="secondary" size="sm" onClick={() => fileRef.open()}>
              <FileDown className="size-4" aria-hidden /> 导入 ICS
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                // 选中集里可能有多条 occurrence 指向同一库中事件：映射到存储 id 后去重
                const items = events.data?.items ?? []
                const toStoredId = (id: string) =>
                  items.find((x) => x.id === id)?.originalEventId ?? id
                void exportIcs(
                  selected.size ? [...new Set([...selected].map(toStoredId))] : undefined,
                )
              }}
            >
              <Download className="size-4" aria-hidden /> 导出{selected.size ? '选中' : '全部'}
            </Button>
            <Button variant="danger-soft" size="sm" disabled={selected.size === 0} onClick={() => setDeleteOpen(true)}>
              <Trash2 className="size-4" aria-hidden /> 删除选中
            </Button>
            <input
              ref={fileRef.ref}
              type="file"
              accept=".ics,text/calendar"
              className="hidden"
              onChange={async (e) => {
                const file = e.target.files?.[0]
                if (!file) return
                const form = new FormData()
                form.append('file', file)
                if (importTarget) form.append('calendarId', importTarget)
                try {
                  /*
                   * PW-08：导入同样走统一传输（apiUpload 会补 Bearer、401 刷新重放、
                   * 非 2xx / 非 JSON 抛错）。失败绝不能弹「导入完成」。
                   */
                  const result = await apiUpload<{ imported?: number; skipped?: number }>(
                    '/api/v1/calendar/import-ics',
                    form,
                  )
                  notifySuccess(
                    `导入完成：成功 ${result?.imported ?? 0}，跳过 ${result?.skipped ?? 0}`,
                  )
                  invalidate()
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '导入失败')
                }
                e.target.value = ''
              }}
            />
          </>
        }
      />

      <div className="mb-3 flex items-center gap-2">
        <Input className="w-56" placeholder="搜索标题…" value={search} onChange={(e) => { setSearch(e.target.value); setPage(1) }} />
        {(events.data?.totalPages ?? 0) > 1 && (
          <div className="ml-auto flex items-center gap-2 text-[13px] text-text-3">
            <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
            <span className="tnum">{page} / {events.data!.totalPages}</span>
            <Button variant="secondary" size="sm" disabled={page >= events.data!.totalPages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
          </div>
        )}
      </div>

      <Card className="overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead>
            <tr className="bg-surface text-left text-xs text-text-3">
              <th className="w-9 px-3 py-2">
                <input
                  type="checkbox"
                  aria-label="全选"
                  className="size-3.5 accent-primary"
                  checked={selected.size === (events.data?.items.length ?? 0) && selected.size > 0}
                  onChange={(e) => setSelected(e.target.checked ? new Set((events.data?.items ?? []).map((x) => x.id)) : new Set())}
                />
              </th>
              <th className="px-2 py-2 font-medium">标题</th>
              <th className="px-2 py-2 font-medium">日历</th>
              <th className="px-2 py-2 font-medium">起止</th>
              <th className="px-2 py-2 font-medium">重复</th>
              <th className="px-2 py-2" />
            </tr>
          </thead>
          <tbody className="divide-y divide-divider">
            {(events.data?.items ?? []).map((e) => {
              const cal = calendars.data?.find((c) => c.id === e.calendarId)
              return (
                <tr key={e.id} className={cn('transition-colors hover:bg-surface', selected.has(e.id) && 'bg-primary-soft/60')}>
                  <td className="px-3 py-1.5">
                    <input
                      type="checkbox"
                      aria-label={`选择 ${e.title}`}
                      className="size-3.5 accent-primary"
                      checked={selected.has(e.id)}
                      onChange={(ev) =>
                        setSelected((prev) => {
                          const next = new Set(prev)
                          if (ev.target.checked) next.add(e.id)
                          else next.delete(e.id)
                          return next
                        })
                      }
                    />
                  </td>
                  <td className="px-2 py-1.5">
                    <button
                      type="button"
                      onClick={() => setEditingEvent(e as unknown as EventResponse)}
                      className="max-w-full truncate text-left text-text-1 outline-none hover:text-primary hover:underline"
                      title="点击打开完整编辑面板"
                    >
                      {e.title}
                    </button>
                    {e.isCancelled && <StatusBadge tone="neutral" dot={false} className="ml-2">已取消</StatusBadge>}
                  </td>
                  <td className="px-2 py-1.5">
                    <span className="inline-flex items-center gap-1.5 text-text-3">
                      <span className="size-2 rounded-full" style={{ backgroundColor: cal?.color ?? '#94A3B8' }} aria-hidden />
                      {cal?.name ?? '—'}
                    </span>
                  </td>
                  <td className="tnum px-2 py-1.5 text-text-3">
                    {new Date(e.dtStart).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}
                    {' ~ '}
                    {new Date(e.dtEnd).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}
                  </td>
                  <td className="px-2 py-1.5 text-xs text-text-3">{e.rRule || e.rrule ? '↻ 重复' : '—'}</td>
                  <td className="px-2 py-1.5 text-right">
                    <Button variant="ghost" size="sm" onClick={() => setEditingEvent(e as unknown as EventResponse)}>
                      编辑
                    </Button>
                  </td>
                </tr>
              )
            })}
            {(events.data?.items.length ?? 0) === 0 && (
              <tr><td colSpan={6}><EmptyState size="sm" title="没有匹配的事件" /></td></tr>
            )}
          </tbody>
        </table>
      </Card>

      <EventEditorDialog
        open={editingEvent != null}
        onOpenChange={(o) => !o && setEditingEvent(null)}
        calendars={(calendarBooks.data ?? []).filter((c) => c.kind === 'calendar')}
        event={editingEvent}
      />

      <ConfirmDialog
        open={deleteOpen}
        onOpenChange={setDeleteOpen}
        tone="danger"
        title={`删除 ${selected.size} 个事件？`}
        description="软删除进入回收站，可在回收站恢复。"
        impact={{ summary: `将删除 ${selected.size} 个日程事件` }}
        confirmLabel="删除"
        onConfirm={async () => {
          await apiPost('/api/v1/calendar/events/batch-delete', { ids: [...selected] })
          notifySuccess(`已删除 ${selected.size} 个事件`)
          setSelected(new Set())
          invalidate()
        }}
      />
    </div>
  )
}

function useEffectImport() {
  const ref = { current: null as HTMLInputElement | null }
  return {
    ref: (el: HTMLInputElement | null) => {
      ref.current = el
    },
    open: () => ref.current?.click(),
  }
}

/* ── /settings/recycle-bin 回收站 ──────────────────────────── */

interface RecycleItem {
  id: string
  type: string
  title: string
  deletedAt: string
  bookName: string | null
  start: string | null
  end: string | null
}

export function RecycleBinPage() {
  const qc = useQueryClient()
  const [type, setType] = useState('all')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [restoreTarget, setRestoreTarget] = useState<RecycleItem | null>(null)
  const [preview, setPreview] = useState<{ restoreCount: number; samples: string[]; conflicts: { reason: string; title: string }[]; canRestoreWithoutConflict: boolean } | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)

  const list = useQuery({
    queryKey: ['calendar', 'recycle-bin', type, search, page],
    queryFn: () => apiGet<{ items: RecycleItem[]; totalCount: number; totalPages: number }>(`/api/v1/calendar/recycle-bin?type=${type}&page=${page}&pageSize=50${search ? `&search=${encodeURIComponent(search)}` : ''}`),
    placeholderData: (prev) => prev,
  })

  const typeLabel: Record<string, string> = { calendar: '日历', 'task-book': '任务本', event: '日程', task: '任务' }

  async function openRestore(item: RecycleItem) {
    setRestoreTarget(item)
    setPreview(null)
    setPreviewLoading(true)
    try {
      const res = await apiPost<{ restoreCount: number; samples: { title: string }[]; conflicts: { reason: string; title: string }[]; canRestoreWithoutConflict: boolean }>(
        `/api/v1/calendar/recycle-bin/${item.type}/${item.id}/restore-preview`,
        {},
      )
      setPreview({ restoreCount: res.restoreCount, samples: res.samples.map((s) => s.title), conflicts: res.conflicts, canRestoreWithoutConflict: res.canRestoreWithoutConflict })
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '预览失败')
    } finally {
      setPreviewLoading(false)
    }
  }

  const canRestoreAsCopy = restoreTarget?.type === 'event' || restoreTarget?.type === 'task'

  return (
    <div>
      <PageHeader title="回收站" subtitle={`共 ${list.data?.totalCount ?? 0} 条`} />

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="flex gap-1.5">
          {['all', 'event', 'task', 'calendar', 'task-book'].map((t) => (
            <Chip key={t} active={type === t} onClick={() => { setType(t); setPage(1) }}>{t === 'all' ? '全部' : typeLabel[t]}</Chip>
          ))}
        </div>
        <Input className="ml-auto w-56" placeholder="搜索标题或所属本…" value={search} onChange={(e) => { setSearch(e.target.value); setPage(1) }} />
      </div>

      <Card className="overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead>
            <tr className="bg-surface text-left text-xs text-text-3">
              <th className="px-3 py-2 font-medium">类型</th>
              <th className="px-3 py-2 font-medium">标题</th>
              <th className="px-3 py-2 font-medium">原属</th>
              <th className="px-3 py-2 font-medium">删除时间</th>
              <th className="px-3 py-2" />
            </tr>
          </thead>
          <tbody className="divide-y divide-divider">
            {(list.data?.items ?? []).map((item) => (
              <tr key={`${item.type}-${item.id}`} className="transition-colors hover:bg-surface">
                <td className="px-3 py-1.5"><StatusBadge tone="neutral" dot={false}>{typeLabel[item.type] ?? item.type}</StatusBadge></td>
                <td className="px-3 py-1.5 text-text-1">
                  {item.title}
                  {item.start && <span className="tnum ml-2 text-[11px] text-text-4">{new Date(item.start).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}</span>}
                </td>
                <td className="px-3 py-1.5 text-text-3">{item.bookName ?? '—'}</td>
                <td className="tnum px-3 py-1.5 text-text-3">{formatTime(item.deletedAt)}</td>
                <td className="px-3 py-1.5 text-right">
                  <Button variant="ghost" size="sm" onClick={() => void openRestore(item)}>恢复…</Button>
                </td>
              </tr>
            ))}
            {(list.data?.items.length ?? 0) === 0 && <tr><td colSpan={5}><EmptyState size="sm" title="回收站是空的" /></td></tr>}
          </tbody>
        </table>
      </Card>

      {(list.data?.totalPages ?? 0) > 1 && (
        <div className="mt-3 flex items-center justify-end gap-2 text-[13px] text-text-3">
          <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          <span className="tnum">{page} / {list.data!.totalPages}</span>
          <Button variant="secondary" size="sm" disabled={page >= list.data!.totalPages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
        </div>
      )}

      {/* 恢复预览弹窗 */}
      <Dialog open={restoreTarget != null} onOpenChange={(o) => !o && setRestoreTarget(null)}>
        <DialogContent className="max-w-[520px]">
          <DialogHeader title="恢复预览" description={restoreTarget?.title} />
          <DialogBody className="space-y-3">
            {previewLoading ? (
              <Skeleton className="h-24" />
            ) : preview ? (
              <>
                <div className={cn('rounded-ctl border px-3 py-2.5 text-[13px]', preview.canRestoreWithoutConflict ? 'border-ok-border bg-ok-soft text-ok' : 'border-warn-border bg-warn-soft text-warn')}>
                  {preview.canRestoreWithoutConflict ? `可直接恢复：将恢复 ${preview.restoreCount} 条` : `存在 ${preview.conflicts.length} 个冲突，需恢复为副本`}
                </div>
                {preview.samples.length > 0 && (
                  <div>
                    <Label>恢复样例</Label>
                    <ul className="space-y-0.5 text-xs text-text-3">
                      {preview.samples.slice(0, 5).map((s, i) => <li key={i}>· {s}</li>)}
                    </ul>
                  </div>
                )}
                {preview.conflicts.length > 0 && (
                  <div>
                    <Label>冲突明细</Label>
                    <ul className="space-y-0.5 text-xs text-warn">
                      {preview.conflicts.map((c, i) => <li key={i}>· {c.reason}：{c.title}</li>)}
                    </ul>
                  </div>
                )}
              </>
            ) : (
              <p className="text-[13px] text-text-4">预览失败</p>
            )}
          </DialogBody>
          <div className="flex justify-end gap-2 px-5 py-3.5">
            <Button variant="secondary" size="sm" onClick={() => setRestoreTarget(null)}>取消</Button>
            {canRestoreAsCopy && (
              <Button
                variant="secondary"
                size="sm"
                disabled={previewLoading || !restoreTarget}
                onClick={async () => {
                  await apiPost(`/api/v1/calendar/recycle-bin/${restoreTarget!.type}/${restoreTarget!.id}/restore`, { restoreAsCopy: true })
                  notifySuccess('已恢复为副本')
                  setRestoreTarget(null)
                  void qc.invalidateQueries({ queryKey: ['calendar'] })
                }}
              >
                恢复为副本
              </Button>
            )}
            <Button
              variant="primary"
              size="sm"
              disabled={previewLoading || !preview?.canRestoreWithoutConflict}
              onClick={async () => {
                await apiPost(`/api/v1/calendar/recycle-bin/${restoreTarget!.type}/${restoreTarget!.id}/restore`, { restoreAsCopy: false })
                notifySuccess('已恢复')
                setRestoreTarget(null)
                void qc.invalidateQueries({ queryKey: ['calendar'] })
              }}
            >
              恢复
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

/* ── /settings/pc-data PC 明细查询 ────────────────────────── */

interface PcDetailRecord {
  recordType: string
  start: string
  end: string | null
  deviceId: string
  appName: string | null
  displayName: string | null
  categoryName: string | null
  title: string | null
  keyPresses: number | null
  totalClicks: number | null
  domain: string | null
  url: string | null
}

export function PcDetailQueryPage() {
  const [filters, setFilters] = useState({
    dateFrom: '', dateTo: '', dimension: 'day', view: 'interpreted', eventType: '',
    deviceId: '', appName: '', categoryName: '', keyName: '', domain: '', title: '', url: '', sortDir: 'desc',
  })
  const [page, setPage] = useState(1)
  const query = useQuery({
    queryKey: ['pc', 'detail', filters, page],
    queryFn: () => {
      const q = new URLSearchParams({ page: String(page), pageSize: '50' })
      for (const [k, v] of Object.entries(filters)) if (v) q.set(k, v)
      return apiGet<{ items: PcDetailRecord[]; totalCount: number; totalPages: number }>(`/api/v1/pc/detail?${q}`)
    },
    placeholderData: (prev) => prev,
  })

  const rows = query.data?.items ?? []

  return (
    <div>
      <PageHeader
        title="PC 明细查询"
        subtitle={`共 ${query.data?.totalCount ?? 0} 条原始记录`}
        actions={
          <>
            <Button variant="secondary" size="sm" disabled={rows.length === 0} onClick={() => {
              downloadCsv('pc-detail.csv', rows as unknown as Record<string, unknown>[])
              notifySuccess('CSV 已导出（带 BOM）')
            }}>
              <Download className="size-4" aria-hidden /> 导出 CSV
            </Button>
            <Button variant="secondary" size="sm" disabled={rows.length === 0} onClick={() => {
              downloadJson('pc-detail.json', JSON.stringify(rows, null, 2))
              notifySuccess('JSON 已导出')
            }}>
              <Download className="size-4" aria-hidden /> 导出 JSON
            </Button>
          </>
        }
      />

      {/* 14 字段筛选格 */}
      <Card className="mb-3 p-3">
        <div className="grid grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-6">
          <Field label="起始日"><Input type="date" className="h-8" value={filters.dateFrom} onChange={(e) => setFilters((f) => ({ ...f, dateFrom: e.target.value }))} /></Field>
          <Field label="结束日"><Input type="date" className="h-8" value={filters.dateTo} onChange={(e) => setFilters((f) => ({ ...f, dateTo: e.target.value }))} /></Field>
          <Field label="维度">
            <Select className="h-8" value={filters.dimension} onValueChange={(v) => setFilters((f) => ({ ...f, dimension: v }))} options={[{ value: 'hour', label: '时' }, { value: 'day', label: '日' }, { value: 'month', label: '月' }, { value: 'year', label: '年' }]} />
          </Field>
          <Field label="视图">
            <Select className="h-8" value={filters.view} onValueChange={(v) => setFilters((f) => ({ ...f, view: v }))} options={[{ value: 'interpreted', label: '解释视图' }, { value: 'raw', label: '原始视图' }]} />
          </Field>
          <Field label="事件类型">
            <Select className="h-8" value={filters.eventType} onValueChange={(v) => setFilters((f) => ({ ...f, eventType: v }))} options={[
              { value: '', label: '全部' }, { value: 'window', label: 'window' }, { value: 'afk', label: 'afk' }, { value: 'web-page', label: 'web-page' },
              { value: 'web', label: 'web' }, { value: 'input-minute', label: 'input-minute' }, { value: 'app-input', label: 'app-input' }, { value: 'key-input', label: 'key-input' },
            ]} />
          </Field>
          <Field label="排序">
            <Select className="h-8" value={filters.sortDir} onValueChange={(v) => setFilters((f) => ({ ...f, sortDir: v }))} options={[{ value: 'desc', label: '时间倒序' }, { value: 'asc', label: '时间正序' }]} />
          </Field>
          <Field label="设备"><Input className="h-8" value={filters.deviceId} onChange={(e) => setFilters((f) => ({ ...f, deviceId: e.target.value }))} /></Field>
          <Field label="应用"><Input className="h-8" value={filters.appName} onChange={(e) => setFilters((f) => ({ ...f, appName: e.target.value }))} /></Field>
          <Field label="分类"><Input className="h-8" value={filters.categoryName} onChange={(e) => setFilters((f) => ({ ...f, categoryName: e.target.value }))} /></Field>
          <Field label="按键"><Input className="h-8" value={filters.keyName} onChange={(e) => setFilters((f) => ({ ...f, keyName: e.target.value }))} /></Field>
          <Field label="域名"><Input className="h-8" value={filters.domain} onChange={(e) => setFilters((f) => ({ ...f, domain: e.target.value }))} /></Field>
          <Field label="标题"><Input className="h-8" value={filters.title} onChange={(e) => setFilters((f) => ({ ...f, title: e.target.value }))} /></Field>
          <Field label="URL"><Input className="h-8" value={filters.url} onChange={(e) => setFilters((f) => ({ ...f, url: e.target.value }))} /></Field>
        </div>
      </Card>

      <Card className="overflow-x-auto">
        <table className="w-full text-[12px]">
          <thead>
            <tr className="bg-surface text-left text-xs text-text-3">
              <th className="px-3 py-2 font-medium">类型</th>
              <th className="px-3 py-2 font-medium">起止</th>
              <th className="px-3 py-2 font-medium">设备</th>
              <th className="px-3 py-2 font-medium">应用/分类</th>
              <th className="px-3 py-2 font-medium">明细</th>
              <th className="px-3 py-2 text-right font-medium">按键/点击</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-divider">
            {query.isLoading ? (
              <tr><td colSpan={6}><Skeleton className="m-3 h-24" /></td></tr>
            ) : rows.length === 0 ? (
              <tr><td colSpan={6}><EmptyState size="sm" title="没有匹配的记录" description="调整筛选条件或日期范围。" /></td></tr>
            ) : (
              rows.map((r, i) => (
                <tr key={i} className="transition-colors hover:bg-surface">
                  <td className="px-3 py-1.5"><span className="mono text-[11px] text-text-3">{r.recordType}</span></td>
                  <td className="tnum px-3 py-1.5 text-text-3">
                    {new Date(r.start).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}
                    {r.end && ` → ${new Date(r.end).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}`}
                  </td>
                  <td className="mono px-3 py-1.5 text-[11px] text-text-4">{r.deviceId}</td>
                  <td className="px-3 py-1.5 text-text-2">
                    {r.displayName ?? r.appName ?? '—'}
                    {r.categoryName && <span className="ml-1.5 text-[11px] text-text-4">{r.categoryName}</span>}
                  </td>
                  <td className="max-w-72 truncate px-3 py-1.5 text-text-3" title={r.title ?? r.domain ?? r.url ?? ''}>
                    {r.title ?? r.domain ?? r.url ?? '—'}
                  </td>
                  <td className="tnum px-3 py-1.5 text-right text-text-3">
                    {r.keyPresses != null || r.totalClicks != null ? `${r.keyPresses ?? 0} / ${r.totalClicks ?? 0}` : '—'}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </Card>

      {(query.data?.totalPages ?? 0) > 1 && (
        <div className="mt-3 flex items-center justify-end gap-2 text-[13px] text-text-3">
          <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          <span className="tnum">{page} / {query.data!.totalPages}</span>
          <Button variant="secondary" size="sm" disabled={page >= query.data!.totalPages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
        </div>
      )}
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <span className="mb-0.5 block text-[10px] text-text-4">{label}</span>
      {children}
    </div>
  )
}

/* ── /settings/users 用户管理 ─────────────────────────────── */

/** 响应/请求形状全部取自刷新的契约类型，不再手写 interface（AC-15.2） */
type AdminUser = components['schemas']['AdminUserDto']
type CreateUserBody = components['schemas']['CreateUserRequest']

/** 客户端预校验提示（就地提示，不发请求） */
const CREATE_HINT = {
  username: '请输入用户名',
  email: '请输入正确的邮箱',
  password: '初始密码至少 8 位',
} as const

const RESET_HINT = {
  password: '新密码至少 8 位',
} as const

export function AdminUsersPage() {
  const qc = useQueryClient()
  const me = useQuery({ queryKey: ['auth', 'me'], queryFn: () => apiGet<{ id: string }>('/api/v1/auth/me') })
  const users = useQuery({ queryKey: ['admin', 'users'], queryFn: () => apiGet<AdminUser[]>('/api/v1/admin/users') })
  const [error, setError] = useState<string | null>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [resetTarget, setResetTarget] = useState<AdminUser | null>(null)

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['admin', 'users'] })

  async function run(fn: () => Promise<unknown>, okMessage: string) {
    setError(null)
    try {
      await fn()
      notifySuccess(okMessage)
      invalidate()
    } catch (err) {
      // 与建号/重置/改密统一口径（验收意见 R-3 的同类第 4 处，由本轮独立 review 指出）：
      // `TypeError: Failed to fetch` 也是 Error，只认 ApiError，其余走中文兜底。
      const msg = err instanceof ApiError ? err.message || '操作失败' : '操作失败'
      setError(msg)
      notifyError(msg)
    }
  }

  return (
    <div>
      <PageHeader
        title="用户管理"
        subtitle={`共 ${users.data?.length ?? 0} 个用户（仅管理员可见）`}
        actions={
          <Button variant="primary" onClick={() => setCreateOpen(true)}>
            新建用户
          </Button>
        }
      />
      {error && (
        <div className="mb-3 rounded-ctl border border-crit-border bg-crit-soft px-3 py-2 text-[13px] text-crit">{error}</div>
      )}
      <div className="space-y-2">
        {users.isLoading
          ? Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-16" />)
          : (users.data ?? []).map((u) => (
              <Card key={u.id} className="flex flex-wrap items-center gap-3 p-4">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate text-sm font-medium text-text-1">{u.displayName || u.username}</span>
                    {u.id === me.data?.id && <StatusBadge tone="info" dot={false}>我</StatusBadge>}
                    <StatusBadge tone={u.role === 'admin' ? 'warn' : 'neutral'} dot={false}>{u.role === 'admin' ? '管理员' : '普通用户'}</StatusBadge>
                    {!u.isActive && <StatusBadge tone="crit" dot={false}>已停用</StatusBadge>}
                  </div>
                  <div className="mt-0.5 text-xs text-text-4">
                    {u.username} · {u.email} · 注册于 {formatTime(u.createdAt)}
                  </div>
                </div>
                <div className="flex gap-1.5">
                  <Button variant="secondary" size="sm" onClick={() => setResetTarget(u)}>
                    重置密码
                  </Button>
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={async () => {
                      if (!window.confirm(u.role === 'admin' ? `将「${u.username}」降为普通用户？` : `将「${u.username}」设为管理员？`)) return
                      await run(() => apiPost(`/api/v1/admin/users/${u.id}/role`, { role: u.role === 'admin' ? 'user' : 'admin' }), '角色已更新')
                    }}
                  >
                    {u.role === 'admin' ? '移除管理员' : '设为管理员'}
                  </Button>
                  <Button
                    variant={u.isActive ? 'danger-soft' : 'secondary'}
                    size="sm"
                    onClick={async () => {
                      if (!window.confirm(u.isActive ? `停用「${u.username}」？停用后无法登录。` : `启用「${u.username}」？`)) return
                      await run(() => apiPost(`/api/v1/admin/users/${u.id}/status`, { isActive: !u.isActive }), u.isActive ? '已停用' : '已启用')
                    }}
                  >
                    {u.isActive ? '停用' : '启用'}
                  </Button>
                </div>
              </Card>
            ))}
      </div>

      <CreateUserDialog open={createOpen} onOpenChange={setCreateOpen} />
      {resetTarget && (
        <ResetPasswordDialog
          target={resetTarget}
          onOpenChange={(open) => {
            if (!open) setResetTarget(null)
          }}
        />
      )}
    </div>
  )
}

/**
 * 新建用户（REQ-12）。居中弹窗；客户端先做预校验（不发请求），
 * 服务端报错时保持打开并显示服务端 message 原文；提交进行中不可关闭、防重复提交。
 */
function CreateUserDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const qc = useQueryClient()
  const [username, setUsername] = useState('')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<'user' | 'admin'>('user')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  /*
   * 逻辑层防重入：`loading` 只把按钮置 disabled，同一 tick 内的第二次点击
   * 仍可能进入 submit（React state 还没提交）。用 ref 立即置位，配合 state 控制 UI。
   */
  const inFlight = useRef(false)

  function resetForm() {
    setUsername('')
    setEmail('')
    setDisplayName('')
    setPassword('')
    setRole('user')
    setError(null)
  }

  async function submit() {
    if (inFlight.current) return
    if (!username.trim()) return setError(CREATE_HINT.username)
    if (!email.trim() || !email.includes('@')) return setError(CREATE_HINT.email)
    if (password.length < 8) return setError(CREATE_HINT.password)

    setError(null)
    inFlight.current = true
    setSubmitting(true)
    try {
      const body: CreateUserBody = {
        username: username.trim(),
        email: email.trim(),
        password,
        role,
      }
      if (displayName.trim()) body.displayName = displayName.trim()
      await apiPost<AdminUser>('/api/v1/admin/users', body)
      notifySuccess('用户已创建')
      void qc.invalidateQueries({ queryKey: ['admin', 'users'] })
      resetForm()
      onOpenChange(false)
    } catch (err) {
      /*
       * 失败反馈 = 弹窗内错误文本 + 顶部提示，不得静默失败（界面文案与交互要点 §1）；
       * 服务端 message 原文（如「用户名已存在」/「邮箱已存在」），弹窗保持打开、内容保留。
       *
       * 只认 ApiError：`TypeError: Failed to fetch` 之类也是 Error，
       * 会把英文原文直接贴给用户（验收意见 R-3）。非 ApiError 一律中文兜底。
       */
      const message = err instanceof ApiError ? err.message || '创建失败，请稍后重试' : '创建失败，请稍后重试'
      setError(message)
      notifyError(message)
    } finally {
      inFlight.current = false
      setSubmitting(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        // 提交进行中不可关（Esc / 遮罩 / 取消按钮都会被这一道挡住）
        if (submitting) return
        // 关闭即清空：不留上一次填过的用户名/邮箱，尤其不能把**初始密码**留在表单里
        if (!next) resetForm()
        onOpenChange(next)
      }}
    >
      <DialogContent>
        <DialogHeader title="新建用户" />
        <DialogBody className="space-y-4">
          {error && <InlineAlert tone="crit">{error}</InlineAlert>}
          <div>
            <Label htmlFor="create-username">用户名</Label>
            <Input id="create-username" value={username} onChange={(e) => setUsername(e.target.value)} />
          </div>
          <div>
            <Label htmlFor="create-email">邮箱</Label>
            <Input id="create-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
          <div>
            <Label htmlFor="create-display">显示名</Label>
            <Input
              id="create-display"
              placeholder="可留空，默认与用户名相同"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="create-password">初始密码</Label>
            <Input
              id="create-password"
              type="password"
              autoComplete="new-password"
              placeholder="至少 8 位"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>
          <div>
            <Label id="create-role-label">角色</Label>
            <div role="group" aria-labelledby="create-role-label">
              <Segmented<'user' | 'admin'>
                className="w-full [&>button]:flex-1"
                value={role}
                onValueChange={setRole}
                options={[
                  { value: 'user', label: '普通用户' },
                  { value: 'admin', label: '管理员' },
                ]}
              />
            </div>
          </div>
        </DialogBody>
        <DialogFooter>
          <DialogClose asChild>
            <Button variant="secondary">取消</Button>
          </DialogClose>
          <Button variant="primary" loading={submitting} onClick={submit}>
            创建
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/**
 * 重置密码（REQ-13）。重置没有「当前设备」概念：服务端会让该用户所有设备重新登录，
 * 因此后果说明必须逐字给出；对自己重置时文案与其他用户完全一致（不带特殊措辞）。
 */
function ResetPasswordDialog({
  target,
  onOpenChange,
}: {
  target: AdminUser
  onOpenChange: (open: boolean) => void
}) {
  const qc = useQueryClient()
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const inFlight = useRef(false)

  async function submit() {
    if (inFlight.current) return
    if (password.length < 8) return setError(RESET_HINT.password)

    setError(null)
    inFlight.current = true
    setSubmitting(true)
    try {
      // 请求体形状取自契约类型（AC-15.2）：字段名写错会被 tsc 拦下
      const body: components['schemas']['ResetPasswordRequest'] = { newPassword: password }
      await apiPost<AdminUser>(`/api/v1/admin/users/${target.id}/password`, body)
      notifySuccess('密码已重置')
      void qc.invalidateQueries({ queryKey: ['admin', 'users'] })
      onOpenChange(false)
    } catch (err) {
      // 同建号：弹窗内文本 + 顶部提示（§1 失败反馈）；非 ApiError 走中文兜底（R-3）
      const message = err instanceof ApiError ? err.message || '重置失败，请稍后重试' : '重置失败，请稍后重试'
      setError(message)
      notifyError(message)
    } finally {
      inFlight.current = false
      setSubmitting(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (submitting) return
        onOpenChange(next)
      }}
    >
      <DialogContent>
        <DialogHeader title="重置密码" description="重置后该用户在所有设备都需要重新登录。" />
        <DialogBody className="space-y-4">
          {error && <InlineAlert tone="crit">{error}</InlineAlert>}
          <div>
            <Label htmlFor="reset-password">新密码</Label>
            <Input
              id="reset-password"
              type="password"
              autoComplete="new-password"
              placeholder="至少 8 位"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>
        </DialogBody>
        <DialogFooter>
          <DialogClose asChild>
            <Button variant="secondary">取消</Button>
          </DialogClose>
          <Button variant="primary" loading={submitting} onClick={submit}>
            重置
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/* ── /settings/sync 旧别名（重定向由路由处理） ─────────────── */
export function SyncSettingsRedirectPage() {
  return (
    <EmptyState
      icon={Activity}
      title="同步设置已并入 Microsoft 账户"
      action={<Link to="/settings/microsoft?tab=outlook" className="text-primary hover:underline">前往 Microsoft 账户 →</Link>}
    />
  )
}
