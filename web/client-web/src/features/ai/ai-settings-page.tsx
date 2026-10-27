import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, RefreshCw } from 'lucide-react'
import { useAuth } from '@/features/auth/auth-context'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatTime } from '@/lib/datetime'
import { Button, Card, Chip, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, EmptyState, Input, Label, PageHeader, Segmented, Select, Skeleton, StatusBadge, Switch, Textarea } from '@/components/ui'
import { aiApi, type CallRequest, type CallResult, type Model, type ModelSettings, type SaveSettings } from './api'
import { createAiRequestId } from './request-id'
import { capabilities, capabilityNames, ModelEditor } from './model-editor'
import { CallImages, VisionCall } from './vision-call'

const states = { Succeeded: '成功', Failed: '失败', Blocked: '未执行', TimedOut: '超时', FailedValidation: '结构不符', Running: '进行中', Cancelled: '已取消', Incomplete: '未完整/未确认' }
function State({ value }: { value: string }) {
  return <StatusBadge dot={false} tone={value === 'Succeeded' ? 'ok' : value === 'Running' ? 'info' : ['Blocked', 'Cancelled', 'Incomplete'].includes(value) ? 'warn' : 'crit'}>{states[value as keyof typeof states] ?? value}</StatusBadge>
}
const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请稍后重试'

export function AiSettingsPage() {
  const { user } = useAuth()
  if (!user || user.role !== 'admin') return <><PageHeader title="AI 设置" /><EmptyState title="需要管理员权限" description="模型配置与调用记录由管理员管理。" /></>
  return <Workspace key={user.id} userId={user.id} />
}

function Workspace({ userId }: { userId: string }) {
  const qc = useQueryClient()
  const prefix = ['ai', userId]
  const [tab, setTab] = useState<'models' | 'history' | 'usage'>('models')
  const [callMode, setCallMode] = useState<'text' | 'vision'>('text')
  const [editing, setEditing] = useState<Model | null | undefined>(undefined)
  const [deleting, setDeleting] = useState<Model | null>(null)
  const [busy, setBusy] = useState(false)
  const [detailId, setDetailId] = useState<string | null>(null)
  const models = useQuery({ queryKey: [...prefix, 'models'], queryFn: ({ signal }) => aiApi.models(signal) })
  const settings = useQuery({ queryKey: [...prefix, 'settings'], queryFn: ({ signal }) => aiApi.settings(signal) })
  const refresh = () => qc.invalidateQueries({ queryKey: prefix })
  useEffect(() => () => {
    void qc.cancelQueries({ queryKey: ['ai', userId] }).then(() => qc.removeQueries({ queryKey: ['ai', userId] }))
  }, [qc, userId])
  async function patchSettings(patch: Partial<SaveSettings>) {
    if (!settings.data) return
    setBusy(true)
    try {
      const saved = await aiApi.saveSettings({ enabled: settings.data.enabled, textModelId: settings.data.textModelId,
        visionModelId: settings.data.visionModelId, transcriptionModelId: settings.data.transcriptionModelId,
        expectedRevision: settings.data.revision, ...patch })
      qc.setQueryData([...prefix, 'settings'], saved); notifySuccess('模型设置已保存')
    } catch (error) { notifyError(errorText(error)); void refresh() }
    finally { setBusy(false) }
  }
  return <div className="space-y-5">
    <PageHeader title="AI 设置" subtitle="模型设置 · 实际调用记录 · 统计" actions={<Button variant="secondary" size="sm" onClick={() => void refresh()}><RefreshCw className="size-4" aria-hidden />刷新</Button>} />
    <Segmented value={tab} onValueChange={setTab} options={[{ value: 'models', label: '模型设置' }, { value: 'history', label: '调用记录' }, { value: 'usage', label: '统计' }]} />
    <div hidden={tab !== 'models'} className="space-y-4">
      {(models.isError || settings.isError) && <p role="alert" className="text-sm text-text-2">设置刷新失败，请刷新核对；已开始的调用使用原配置。</p>}
      {(models.isError || settings.isError) && (!models.data || !settings.data) ? <Card className="p-5"><EmptyState title="模型设置加载失败" description={errorText(models.error ?? settings.error)} action={<Button onClick={() => void refresh()}>重试</Button>} /></Card>
        : !models.data || !settings.data ? <Skeleton className="h-48" /> : <>
          <Card className="space-y-4 p-5">
            <div className="flex items-center justify-between gap-4"><div><h2 className="text-sm font-semibold text-text-1">SDK 直连</h2><p className="mt-1 text-xs text-text-3">此开关控制新直连调用；旧环境链路保留原有配置。</p></div>
              <div className="flex items-center gap-2"><Label htmlFor="ai-sdk-enabled">启用 SDK 直连</Label><Switch id="ai-sdk-enabled" checked={settings.data.enabled} disabled={busy} onCheckedChange={(enabled) => void patchSettings({ enabled })} /></div></div>
            <div className="grid gap-3 md:grid-cols-3">{capabilities.map((cap) => {
              const field = cap === 'Text' ? 'textModelId' : cap === 'Vision' ? 'visionModelId' : 'transcriptionModelId'
              const chosen = settings.data![field]
              const options = models.data!.filter((m) => m.capabilities.includes(cap) && (m.enabled || m.id === chosen))
              return <div key={cap}><Label htmlFor={`ai-default-${cap}`}>默认{capabilityNames[cap]}模型</Label><Select id={`ai-default-${cap}`} ariaLabel={`默认${capabilityNames[cap]}模型`} value={chosen ?? ''} disabled={busy}
                options={[{ value: '', label: '未选择' }, ...options.map((m) => ({ value: m.id, label: m.name + (m.enabled ? '' : '（已停用）') }))]}
                onValueChange={(value) => void patchSettings({ [field]: value || null })} /></div>
            })}</div>
            <p className="text-xs text-text-3">已开始的调用使用固定配置。视觉支持PNG/JPEG多图调用；语音转写当前仅可配置，调用能力待接入。</p>
          </Card>
          <Card className="p-5"><div className="mb-4 flex items-center justify-between gap-3"><h2 className="text-sm font-semibold text-text-1">模型配置（{models.data.length}）</h2><Button size="sm" variant="primary" onClick={() => setEditing(null)}><Plus className="size-4" aria-hidden />添加模型</Button></div>
            {models.data.length === 0 ? <EmptyState title="添加第一个模型" description="保存服务地址、模型标识和凭据，即可启用文字调用。" />
              : <div className="grid gap-3 lg:grid-cols-2">{models.data.map((model) => <div key={model.id} className="min-w-0 space-y-2 rounded-ctl border border-border p-4">
                <div className="flex flex-wrap items-center gap-2"><span className="font-medium text-text-1">{model.name}</span><StatusBadge tone={model.enabled ? 'ok' : 'neutral'} dot={false}>{model.enabled ? '已启用' : '已停用'}</StatusBadge></div>
                <p className="break-all font-mono text-xs text-text-2">{model.model}</p><p className="break-all text-xs text-text-3">{model.baseUrl}</p>
                <div className="flex flex-wrap gap-1">{model.capabilities.map((cap) => <Chip key={cap}>{capabilityNames[cap]}</Chip>)}</div>
                <div className="flex flex-wrap items-center justify-between gap-2"><span className="text-xs text-text-3">凭据{model.hasApiKey ? '已保存' : '未设置'} · {model.timeoutSeconds}秒 · {model.maxOutputTokens}输出上限</span><div className="flex gap-1"><Button size="sm" variant="ghost" onClick={() => setEditing(model)} aria-label={`编辑 ${model.name}`}>编辑</Button><Button size="sm" variant="ghost" onClick={() => setDeleting(model)} aria-label={`删除 ${model.name}`}>删除</Button></div></div>
              </div>)}</div>}
          </Card>
          <Segmented value={callMode} onValueChange={setCallMode} options={[{ value: 'text', label: '文字调用' }, { value: 'vision', label: '图片与正文' }]} />
          <div hidden={callMode !== 'text'}><TextCall userId={userId} models={models.data} settings={settings.data} changed={() => void refresh()} showDetail={setDetailId} /></div>
          <div hidden={callMode !== 'vision'}><VisionCall userId={userId} models={models.data} settings={settings.data} changed={() => void refresh()} showDetail={setDetailId} renderStatus={(status) => <State value={status} />} /></div>
          <LegacyInfo prefix={prefix} />
        </>}
    </div>
    {tab === 'history' && <History prefix={prefix} showDetail={setDetailId} />}
    {tab === 'usage' && <Statistics prefix={prefix} />}
    {editing !== undefined && <ModelEditor key={editing?.id ?? 'new'} model={editing} close={() => setEditing(undefined)} save={async (body) => { const result = await aiApi.saveModel(editing?.id, body); await refresh(); notifySuccess('模型已保存'); return result }} />}
    <Dialog open={deleting != null} onOpenChange={(open) => { if (!open && !busy) setDeleting(null) }}><DialogContent showClose={!busy}>
      <DialogHeader title="删除模型配置" description="已有调用记录保留；默认模型需先切换或清空。" /><DialogBody><p className="text-sm text-text-2">确认删除“{deleting?.name}”？</p></DialogBody>
      <DialogFooter><Button variant="secondary" disabled={busy} onClick={() => setDeleting(null)}>取消</Button><Button variant="primary" loading={busy} onClick={() => {
        if (!deleting) return
        setBusy(true); void aiApi.removeModel(deleting).then(async () => { setDeleting(null); await refresh(); notifySuccess('模型配置已删除，调用记录保留') }).catch((e: unknown) => notifyError(errorText(e))).finally(() => setBusy(false))
      }}>删除配置</Button></DialogFooter>
    </DialogContent></Dialog>
    {detailId && <Detail id={detailId} prefix={prefix} close={() => setDetailId(null)} />}
  </div>
}

function TextCall({ userId, models, settings, changed, showDetail }: { userId: string; models: Model[]; settings: ModelSettings; changed: () => void; showDetail: (id: string) => void }) {
  const [selected, setSelected] = useState('')
  const [system, setSystem] = useState('')
  const [prompt, setPrompt] = useState('')
  const [pending, setPending] = useState(false)
  const [submitted, setSubmitted] = useState<CallRequest | null>(null)
  const [result, setResult] = useState<CallResult | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
  const selectedModel = models.find((m) => m.id === (selected || settings.textModelId))
  const ready = settings.enabled && selectedModel?.enabled && selectedModel.capabilities.includes('Text')
  return <Card className="space-y-4 p-5">
    <div><h2 className="text-sm font-semibold text-text-1">文字调用</h2><p className="mt-1 text-xs text-text-3">按实际输入调用所选模型，保存系统提示、正文、回复及统计。每次新调用可能产生服务商费用。</p></div>
    <div><Label htmlFor="ai-call-model">本次模型</Label><Select id="ai-call-model" ariaLabel="本次模型" value={selected} options={[{ value: '', label: '使用默认文本模型' }, ...models.filter((m) => m.capabilities.includes('Text') && m.enabled).map((m) => ({ value: m.id, label: m.name }))]} onValueChange={setSelected} disabled={pending} /></div>
    {!ready && <p role="status" className="text-sm text-text-3">请先启用 SDK 直连，并选择已启用的文本模型。</p>}
    <div><Label htmlFor="ai-call-system">系统提示（可选）</Label><Textarea id="ai-call-system" value={system} maxLength={32000} rows={3} onChange={(e) => setSystem(e.target.value)} /></div>
    <div><Label htmlFor="ai-call-prompt">正文与上下文</Label><Textarea id="ai-call-prompt" value={prompt} maxLength={200000} rows={6} onChange={(e) => setPrompt(e.target.value)} placeholder="填写本次实际发送给模型的内容" /><p className="mt-1 text-xs text-text-3">总输入 {system.length + prompt.length} / 200000 字符；按原输入保留。当前为单次非流式调用。</p></div>
    <div className="flex flex-wrap gap-2">
      <Button variant="primary" loading={pending} disabled={!ready || !prompt.trim() || system.length + prompt.length > 200000 || submitted !== null} onClick={() => {
        const body: CallRequest = { clientRequestId: createAiRequestId(), expectedUserId: userId, prompt, systemPrompt: system || null, modelId: selected || null }
        const controller = new AbortController(); abort.current = controller; setSubmitted(body); setResult(null); setMessage(null); setPending(true)
        void aiApi.call(body, controller.signal).then(setResult).catch((error: unknown) => setMessage(controller.signal.aborted ? '已请求取消，请在调用记录中核对状态；上游是否结束以其记录为准。' : errorText(error) + '；请先核对本次记录，页面不会自动重发。'))
          .finally(() => { setPending(false); changed() })
      }}>发送并记录</Button>
      {pending && <Button variant="secondary" onClick={() => abort.current?.abort()}>取消等待</Button>}
      {submitted && !pending && <><Button variant="secondary" onClick={() => {
        void aiApi.lookup(submitted.clientRequestId).then((value) => { setResult(value); setMessage(null); changed() }).catch((e: unknown) => setMessage(errorText(e)))
      }}>核对本次状态</Button><Button variant="ghost" onClick={() => { setSubmitted(null); setResult(null); setMessage(null) }}>新建一次调用</Button></>}
    </div>
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {result && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2"><State value={result.status} /><Button variant="ghost" size="sm" onClick={() => showDetail(result.logId)}>查看本次完整记录</Button></div>
      {result.errorMessage && <p role="alert" className="text-sm text-text-2">{result.errorMessage}</p>}
      {result.responseText != null && <pre aria-label="实际模型回复" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{result.responseText}</pre>}
      {result.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新执行模型调用。</p>}
    </div>}
  </Card>
}

function History({ prefix, showDetail }: { prefix: string[]; showDetail: (id: string) => void }) {
  const [page, setPage] = useState(1); const [status, setStatus] = useState(''); const [model, setModel] = useState(''); const [filter, setFilter] = useState('')
  const rows = useQuery({ queryKey: [...prefix, 'history', page, status, filter], queryFn: ({ signal }) => aiApi.history(page, status, filter, signal),
    refetchInterval: (query) => query.state.data?.items.some((r) => r.status === 'Running') ? 3000 : false })
  return <Card className="space-y-4 p-5"><div><h2 className="text-sm font-semibold text-text-1">调用记录</h2><p className="mt-1 text-xs text-text-3">SDK 直连与旧链路记录统一查询；点击记录查看实际消息、回复和配置快照。</p></div>
    <div className="flex flex-wrap items-end gap-3"><div className="w-44"><Label htmlFor="ai-log-status">状态</Label><Select id="ai-log-status" ariaLabel="记录状态" value={status} options={[{ value: '', label: '全部状态' }, ...Object.entries(states).map(([value, label]) => ({ value, label }))]} onValueChange={(v) => { setStatus(v); setPage(1) }} /></div>
      <form className="flex min-w-0 flex-1 gap-2" onSubmit={(e) => { e.preventDefault(); setFilter(model); setPage(1) }}><Input aria-label="按模型标识筛选" value={model} onChange={(e) => setModel(e.target.value)} placeholder="模型标识（精确匹配）" /><Button type="submit" variant="secondary">筛选</Button></form></div>
    {rows.isLoading ? <Skeleton className="h-40" /> : rows.isError ? <EmptyState title="记录读取失败" description={errorText(rows.error)} action={<Button onClick={() => void rows.refetch()}>重试</Button>} /> : !rows.data?.items.length ? <EmptyState title="暂无调用记录" description="完成一次文字调用后，会在这里显示实际请求与回复。" />
      : <div className="space-y-2">{rows.data.items.map((row) => <button key={row.id} type="button" className="flex w-full flex-wrap items-center justify-between gap-3 rounded-ctl border border-border p-3 text-left hover:bg-surface" onClick={() => showDetail(row.id)}>
        <div className="min-w-0"><p className="truncate text-sm font-medium text-text-1">{row.modelProfileName ?? row.model}</p><p className="mt-1 text-xs text-text-3">{formatTime(row.startedAt)} · {row.purpose === 'ai.vision-call' ? 'SDK 视觉' : row.purpose === 'ai.text-call' ? 'SDK 直连' : row.module + ' / ' + row.purpose}</p></div>
        <div className="flex flex-wrap items-center gap-3 text-xs text-text-3"><State value={row.status} /><span>{row.totalTokens == null ? 'Token 未上报' : `${row.totalTokens} token`}</span><span>{row.durationMs == null ? '—' : `${row.durationMs}ms`}</span></div>
      </button>)}</div>}
    <div className="flex items-center justify-between gap-3 text-xs text-text-3"><span>{rows.data?.totalCount ?? 0} 条记录</span><div className="flex items-center gap-2"><Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage((v) => v - 1)}>上一页</Button><span>{page} / {Math.max(1, rows.data?.totalPages ?? 1)}</span><Button size="sm" variant="secondary" disabled={page >= (rows.data?.totalPages ?? 1)} onClick={() => setPage((v) => v + 1)}>下一页</Button></div></div>
  </Card>
}

function Statistics({ prefix }: { prefix: string[] }) {
  const usage = useQuery({ queryKey: [...prefix, 'usage'], queryFn: ({ signal }) => aiApi.usage(signal),
    refetchInterval: (query) => (query.state.data?.runningCount ?? 0) > 0 ? 3000 : false })
  if (usage.isLoading) return <Skeleton className="h-48" />
  if (usage.isError || !usage.data) return <EmptyState title="统计读取失败" description={errorText(usage.error)} action={<Button onClick={() => void usage.refetch()}>重试</Button>} />
  const u = usage.data; const finished = u.requestCount - (u.runningCount ?? 0)
  return <div className="space-y-4"><div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
    {[['记录数', u.requestCount], ['记录成功率（已结束）', finished > 0 ? `${(u.successCount / finished * 100).toFixed(1)}%` : '—'], ['输入 Token（已上报）', u.promptTokens], ['输出 Token（已上报）', u.completionTokens], ['总 Token（已上报）', u.totalTokens], ['平均耗时', u.averageDurationMs == null ? '—' : `${Math.round(u.averageDurationMs)}ms`]].map(([label, value]) => <Card key={String(label)} className="p-4"><p className="text-xs text-text-3">{label}</p><p className="mt-2 text-xl font-semibold text-text-1">{value}</p></Card>)}
  </div><p className="text-xs text-text-3">进行中 {u.runningCount ?? 0} 条，已结束未成功 {u.failureCount} 条。Token 汇总仅含服务商上报数据，{u.unknownTokenUsageCount ?? 0} 条未完整上报；费用{(u.knownCostRequestCount ?? 0) === 0 ? '未提供，不能按零费用解释' : '按每条记录的币种查看，不跨币种合计'}。</p>
    <Card className="space-y-3 p-5"><h2 className="text-sm font-semibold text-text-1">按模型标识</h2>{u.byModel.length === 0 ? <EmptyState title="暂无统计" /> : u.byModel.map((group) => <div key={group.groupKey} className="flex flex-wrap justify-between gap-2 border-b border-divider py-2 text-sm"><span className="break-all text-text-1">{group.groupKey}</span><span className="text-text-3">{group.requestCount} 条 · 成功 {group.successCount} · 进行中 {group.runningCount ?? 0} · {group.totalTokens} 已上报 token</span></div>)}</Card>
  </div>
}

function Detail({ id, prefix, close }: { id: string; prefix: string[]; close: () => void }) {
  const data = useQuery({ queryKey: [...prefix, 'detail', id], queryFn: ({ signal }) => aiApi.detail(id, signal), refetchInterval: (q) => q.state.data?.status === 'Running' ? 3000 : false })
  const row = data.data
  return <Dialog open onOpenChange={(open) => { if (!open) close() }}><DialogContent className="max-w-[760px]"><DialogHeader title="调用详情" description={row ? `${row.modelProfileName ?? row.model} · ${formatTime(row.startedAt)}` : '正在读取记录'} />
    <DialogBody className="space-y-4">{data.isLoading ? <Skeleton className="h-40" /> : data.isError || !row ? <EmptyState title="详情读取失败" description={errorText(data.error)} action={<Button onClick={() => void data.refetch()}>重试</Button>} /> : <>
      <div className="flex flex-wrap items-center gap-3"><State value={row.status} /><Chip>{row.purpose === 'ai.vision-call' ? 'SDK 视觉调用' : row.purpose === 'ai.text-call' ? 'SDK 调用记录' : '旧链路记录'}</Chip><span className="text-xs text-text-3">结束原因：{row.finishReason ?? '未提供'} · Token：{row.usage?.totalTokens ?? '未上报'}</span></div>
      <p className="text-xs text-text-3">输入 Token {row.usage?.promptTokens ?? '未上报'} · 输出 Token {row.usage?.completionTokens ?? '未上报'} · 耗时 {row.durationMs == null ? '未记录' : `${row.durationMs}ms`} · 费用 {row.usage?.estimatedCost == null ? '未知' : `${row.usage.estimatedCost} ${row.usage.currency ?? '（币种未提供）'}（估算）`}</p>
      {row.errorMessage && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{row.errorMessage}</p>}
      {row.contentRedacted && <p className="text-xs text-text-3">本条内容中出现的认证凭据已遮盖，并作此标记。</p>}
      <div><Label htmlFor="ai-record-messages">实际请求消息／上下文</Label><Textarea id="ai-record-messages" className="font-mono text-xs" readOnly rows={8} value={pretty(row.requestMessagesJson)} /></div>
      <div><Label htmlFor="ai-record-reply">实际回复</Label><Textarea id="ai-record-reply" readOnly rows={8} value={row.responseText ?? '未收到普通文本回复；可查看下方SDK/错误快照。'} /></div>
      {row.purpose === 'ai.vision-call' && <><p className="text-xs text-text-3">SDK消息中的图片按指纹引用到下方材料，正文和回复按原内容保留；未执行或准备失败的记录不代表图片已发送。</p><CallImages logId={id} owner={row.userId} viewer={prefix[1]!} running={row.status === 'Running'} /></>}
      <details className="text-xs text-text-3"><summary className="cursor-pointer">配置与 SDK／错误结果快照</summary><div className="mt-3 space-y-2"><p className="break-all">{row.modelEndpoint ?? '旧链路未记录地址'} · 模型 {row.model}</p><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words">{pretty(row.requestPayloadJson)}</pre><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words">{pretty(row.responseRawJson)}</pre><p>请求标识：{row.clientRequestId ?? '旧记录未提供'}</p></div></details>
    </>}</DialogBody><DialogFooter><Button variant="secondary" onClick={close}>关闭</Button></DialogFooter>
  </DialogContent></Dialog>
}
function pretty(value: string) { try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value } }

function LegacyInfo({ prefix }: { prefix: string[] }) {
  const [open, setOpen] = useState(false)
  const data = useQuery({ queryKey: [...prefix, 'legacy'], queryFn: ({ signal }) => aiApi.legacy(signal), enabled: open })
  return <div><Button variant="ghost" size="sm" onClick={() => setOpen((v) => !v)}>{open ? '收起' : '查看'}旧环境链路信息</Button>{open && <Card className="mt-2 space-y-2 p-4 text-xs text-text-3">
    <p>尚未迁移的原有功能继续沿用环境配置，调用记录与直连统一统计。</p>
    {data.data ? <><p>旧链路：{data.data.enabled ? '已启用' : '未启用'} · {data.data.provider} · {data.data.defaultModel}</p><p className="break-all">{data.data.baseUrl}</p></> : data.isError ? <p>{errorText(data.error)}</p> : <p>正在读取…</p>}
  </Card>}</div>
}
