import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import { Play, RefreshCw, ShieldCheck, Square } from 'lucide-react'
import { Button, Card, EmptyState, Label, PageHeader, Select, Skeleton, StatusBadge, Textarea } from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { formatTime } from '@/lib/datetime'
import { aiApi, type NoteAsrTask } from '../api'
import { createAiRequestId } from '../request-id'
import { NoteAsrStartPanel } from '../note-asr-start-panel'
import { NoteAsrOwnerTasks } from '../note-asr-owner-tasks'
import {
  noteDerivedApi,
  type NoteDerivedArtifact,
  type NoteDerivedDestination,
  type NoteDerivedLimits,
  type NoteDerivedSource,
  type NoteDerivedSteps,
  type NoteDerivedTask,
  type NoteDerivedVersion,
  type RetryNoteDerived,
  type SaveNoteDerivedManual,
  type StartNoteDerived,
} from './api'
import {
  canAdoptSelectedVersion,
  canonicalServerBinding,
  clearStoredIntent,
  createStoredDraft,
  editStoredDraft,
  loadStoredDraft,
  persistStoredDraft,
  reserveAndPersistIntent,
  type NoteDerivedArtifactKind,
  type NoteDerivedDraftScope,
  type StoredNoteDerivedDraft,
} from './draft-storage'
import {
  beginManualSave,
  isVersionHead,
  resolveManualSave,
  type ManualSaveSnapshot,
} from './draft-protection'
import { NoteTaskSuggestionsPanel } from './note-task-suggestions'

const phaseNames: Record<string, string> = {
  Queued: '等待处理',
  Running: '处理中',
  Succeeded: '已完成',
  NeedsAttention: '有步骤需处理',
  Stopped: '已停止',
  Blocked: '输入有缺口',
  Failed: '失败',
}
const stepNames: Record<string, string> = {
  ImageEvidence: '图片证据',
  RecordSynthesis: '完整记录整理',
  AnalysisSynthesis: '独立分析',
}
const kindNames: Record<NoteDerivedArtifactKind, string> = {
  CompleteRecord: '完整记录',
  IndependentAnalysis: '独立分析',
}
const authorNames = { Model: '模型生成', User: '人工修订' } as const
const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请刷新核对'
const time = (value: string) => formatTime(value)

function tone(value: string) {
  if (value === 'Succeeded') return 'ok' as const
  if (value === 'Queued' || value === 'Running') return 'info' as const
  if (value === 'Blocked' || value === 'Failed') return 'crit' as const
  return 'warn' as const
}

function State({ value }: { value: string }) {
  return <StatusBadge dot={false} tone={tone(value)}>{phaseNames[value] ?? value}</StatusBadge>
}

function SectionTitle({ number, title, description }: { number: string; title: string; description: string }) {
  return <div className="flex items-start gap-3">
    <span className="grid size-7 shrink-0 place-items-center rounded-full bg-primary/10 text-xs font-semibold text-primary">{number}</span>
    <div className="min-w-0"><h2 className="text-sm font-semibold text-text-1">{title}</h2><p className="mt-1 text-xs leading-5 text-text-3">{description}</p></div>
  </div>
}

function bindingScope(ownerId: string, serverBinding: string, artifactId: string, sourceVersionId: string, kind: NoteDerivedArtifactKind): NoteDerivedDraftScope {
  return { ownerId, serverBinding, artifactId, sourceVersionId, kind }
}

function loadLocalRecord(scope: NoteDerivedDraftScope): { record: StoredNoteDerivedDraft; error: string | null } {
  const loaded = loadStoredDraft(scope)
  if (!loaded.ok) return { record: createStoredDraft(scope), error: loaded.error.message }
  return { record: loaded.value ?? createStoredDraft(scope), error: null }
}

export function NoteDerivedPage() {
  const { user } = useAuth()
  const [serverBinding, setServerBinding] = useState(canonicalServerBinding)
  useEffect(() => {
    const update = () => setServerBinding(canonicalServerBinding())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])
  const ownerId = user?.id ?? ''
  return <Workspace key={`${ownerId}/${serverBinding}`} ownerId={ownerId} serverBinding={serverBinding} role={user?.role} />
}

function Workspace({ ownerId, serverBinding, role }: { ownerId: string; serverBinding: string; role?: string }) {
  const qc = useQueryClient()
  const [searchParams, setSearchParams] = useSearchParams()
  const asrSection = useRef<HTMLElement>(null)
  const organizeSection = useRef<HTMLElement>(null)
  const [sourcePage, setSourcePage] = useState(0)
  const [sourceVersionId, setSourceVersionId] = useState(() => searchParams.get('version') ?? searchParams.get('versionId') ?? '')
  const [asrTaskId, setAsrTaskId] = useState('')
  const [taskId, setTaskId] = useState('')
  const [message, setMessage] = useState('')
  const sources = useQuery({ queryKey: ['note-derived', ownerId, serverBinding, 'sources', sourcePage], queryFn: ({ signal }) => noteDerivedApi.sources(sourcePage, signal) })
  const destinations = useQuery({ queryKey: ['note-derived', ownerId, serverBinding, 'destinations'], queryFn: ({ signal }) => noteDerivedApi.destinations(signal) })
  const limits = useQuery({ queryKey: ['note-derived', ownerId, serverBinding, 'limits'], queryFn: ({ signal }) => noteDerivedApi.limits(signal) })
  const source = sources.data?.items.find((item) => item.versionId === sourceVersionId)
  const requestedNoteId = searchParams.get('noteId') ?? ''
  const requestedAction = searchParams.get('action')
  useEffect(() => {
    if (!sourceVersionId && requestedNoteId && sources.data) {
      const matchingNoteVersions = sources.data.items.filter((item) => item.noteId === requestedNoteId)
      const preferredVersion = matchingNoteVersions.find((item) => item.isCurrent) ?? matchingNoteVersions[0]
      if (preferredVersion) {
        setSourceVersionId(preferredVersion.versionId)
        setSearchParams((current) => {
          const next = new URLSearchParams(current)
          next.set('version', preferredVersion.versionId)
          return next
        }, { replace: true })
        return
      }
    }
    const searchingForLinkedSource = Boolean(sourceVersionId || requestedNoteId)
    const sourceFound = source || (requestedNoteId && sources.data?.items.some((item) => item.noteId === requestedNoteId))
    if (searchingForLinkedSource && sources.data && !sourceFound && sourcePage + sources.data.items.length < sources.data.total) {
      setSourcePage((page) => page + 20)
    }
  }, [requestedNoteId, source, sourcePage, sourceVersionId, sources.data, setSearchParams])
  useEffect(() => {
    if (!source || (requestedAction !== 'transcribe' && requestedAction !== 'organize')) return
    const target = requestedAction === 'transcribe' && source.recordings > 0 ? asrSection.current : organizeSection.current
    if (!target) return
    target.scrollIntoView({ block: 'start' })
    target.focus({ preventScroll: true })
    setSearchParams((current) => {
      const next = new URLSearchParams(current)
      next.delete('action')
      return next
    }, { replace: true })
  }, [requestedAction, setSearchParams, source])
  const tasks = useQuery({
    queryKey: ['note-derived', ownerId, serverBinding, 'tasks', source?.noteId ?? 'all'],
    queryFn: ({ signal }) => noteDerivedApi.tasks(source?.noteId ?? null, signal),
    refetchInterval: (query) => query.state.data?.some((item) => item.phase === 'Running' || item.phase === 'Queued') ? 5000 : false,
  })
  const asrTasks = useQuery({
    queryKey: ['note-derived', ownerId, serverBinding, 'asr-tasks', source?.versionId],
    queryFn: ({ signal }) => aiApi.noteAsrTasks(signal),
    enabled: Boolean(source && source.recordings > 0),
  })
  const selectedTask = tasks.data?.find((item) => item.taskId === taskId)
  const noteId = source?.noteId ?? selectedTask?.noteId ?? ''
  const artifacts = useQuery({
    queryKey: ['note-derived', ownerId, serverBinding, 'artifacts', noteId],
    queryFn: ({ signal }) => noteDerivedApi.artifacts(noteId, signal),
    enabled: Boolean(noteId),
    refetchInterval: (query) => query.state.data?.some((item) => item.versions.some((version) => !version.isHead)) ? 5000 : false,
  })
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding] })
  }
  const refreshArtifacts = () => {
    void qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding, 'artifacts'] })
  }
  const returnParams = new URLSearchParams(searchParams)
  if (sourceVersionId) returnParams.set('version', sourceVersionId)
  returnParams.delete('versionId')
  returnParams.delete('action')
  const returnQuery = returnParams.toString()

  return <div className="space-y-5">
      <PageHeader title="继续处理这篇笔记" subtitle="先完成录音转写，再整理成完整记录和分析。" actions={<>
      <Link className="inline-flex h-8 items-center justify-center rounded-ctl px-3 text-[13px] font-medium text-text-2 transition-colors hover:bg-surface hover:text-text-1 focus-visible:outline-2 focus-visible:outline-primary-ring" to={`/native-notes${returnQuery ? `?${returnQuery}` : ''}`}>返回所选笔记</Link>
      <Button variant="secondary" size="sm" onClick={refresh}><RefreshCw className="size-4" aria-hidden />刷新</Button>
    </>} />
    <details className="rounded-ctl border border-info-border bg-info-soft px-4 py-3 text-sm text-text-2">
      <summary className="flex cursor-pointer items-center gap-2 font-medium"><ShieldCheck className="size-4 shrink-0 text-info" aria-hidden />隐私与处理说明</summary>
      <div className="mt-2 space-y-1 pl-6 text-xs leading-5 text-text-3">
        <p>打开页面不会自动调用模型；只有明确确认后才会创建任务。提交意图会保存在本机以便核对。</p>
        <p>{role === 'admin' ? '管理员可在设置 → AI 与 ASR 维护共享目标；本页只使用启用的安全目标，不显示凭据。' : '普通账号只能使用管理员启用的共享目标，不能查看或配置模型凭据。'}</p>
      </div>
    </details>

    <div className="space-y-5">
        <Card className="space-y-4 p-5">
          <SectionTitle number="1" title="选择笔记材料" description="从所选笔记的固定版本继续；零录音版本可直接整理，录音版本先完成转写。" />
          {sources.isPending ? <Skeleton className="h-16" /> : sources.isError ? <EmptyState title="材料版本加载失败" action={<Button onClick={() => void sources.refetch()}>重试</Button>} />
            : !sources.data?.items.length ? <EmptyState title="没有已上传材料" description="请先在客户端提交完整笔记材料版本。" />
              : <>
                <div><Label htmlFor="note-derived-source">材料版本</Label>
                  <Select id="note-derived-source" ariaLabel="材料版本" value={sourceVersionId} className="mt-1 w-full"
                    options={sources.data.items.map((item) => ({
                      value: item.versionId,
                      label: `${item.title || '未命名笔记'} · ${time(item.createdAt)}${item.isCurrent ? ' · 当前版' : ' · 历史版'}${item.hasConflict ? ' · 冲突保全' : ''}`,
                    }))}
                    onValueChange={(value) => {
                      setSourceVersionId(value)
                      setSearchParams((current) => {
                        const next = new URLSearchParams(current)
                        next.set('version', value)
                        return next
                      }, { replace: true })
                      setAsrTaskId('')
                      setTaskId('')
                      setMessage('')
                    }} /></div>
                {source && <p className="text-xs leading-5 text-text-3">{source.recordings === 0 ? '零录音版本' : `${source.recordings} 次录音`} · 有效录音 {Math.floor(source.totalSamples / source.sampleRate / 60)}:{String(Math.floor(source.totalSamples / source.sampleRate) % 60).padStart(2, '0')} · 全部原录音保留</p>}
                {sources.data.total > 20 && <div className="flex gap-2"><Button size="sm" disabled={sourcePage === 0} onClick={() => setSourcePage((page) => page - 20)}>上一页材料</Button><Button size="sm" disabled={sourcePage + 20 >= sources.data.total} onClick={() => setSourcePage((page) => page + 20)}>下一页材料</Button></div>}
              </>}
        </Card>

        {source && source.recordings > 0 && <section ref={asrSection} tabIndex={-1} aria-label="录音转写" className="space-y-3 rounded-ctl border border-primary/20 bg-primary/5 p-4 outline-none sm:p-5">
          <div><h2 className="text-base font-semibold text-text-1">1 · 先完成录音转写</h2><p className="mt-1 text-sm text-text-3">选好本次录音转写后再生成笔记整理结果；已有成功转写可以直接复用。</p></div>
          <NoteAsrStartPanel key={`asr-${source.versionId}`} userId={ownerId} serverBinding={serverBinding} versionId={source.versionId}
            onCreated={(task) => {
              setAsrTaskId(task.taskId)
              setMessage('转写任务已保存；可离开此页，稍后从所选笔记继续。')
              void qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding, 'asr-tasks', source.versionId] })
            }} />
          <NoteAsrOwnerTasks userId={ownerId} serverBinding={serverBinding} versionId={source.versionId}
            selectedTaskId={asrTaskId} onSelect={setAsrTaskId} />
        </section>}
        {source && <section ref={organizeSection} tabIndex={-1} aria-label="整理生成" className="space-y-3 rounded-ctl border border-border p-4 outline-none sm:p-5">
          <div><h2 className="text-base font-semibold text-text-1">{source.recordings > 0 ? '2 · 整理并生成' : '1 · 整理并生成'}</h2><p className="mt-1 text-sm text-text-3">生成完整记录与独立分析；原笔记和已选版本保持不变。</p></div>
          {source.recordings > 0 && <p className="rounded-ctl bg-surface p-3 text-sm text-text-2">仅使用当前固定版本已成功的转写。若转写仍在处理中，可稍后返回；不会自动启动生成。</p>}
          <StartTaskCard key={source.versionId} ownerId={ownerId} serverBinding={serverBinding} role={role} source={source}
          destinations={destinations.data?.items ?? []} destinationsError={destinations.isError ? errorText(destinations.error) : ''}
          asrTasks={asrTasks.data ?? []} asrLoading={asrTasks.isPending} limits={limits.data}
          onCreated={(task) => {
            setTaskId(task.taskId)
            setMessage('派生任务已创建，后续生成不会自动覆盖当前选用版本。')
            refresh()
          }} />
        </section>}

        <Card className="space-y-3 p-5">
          <div className="flex flex-wrap items-center justify-between gap-2"><div><h2 className="text-sm font-semibold text-text-1">处理进度</h2><p className="mt-1 text-xs text-text-3">任务保存在服务器，可离开后返回；旧任务与结果不会被新稿自动替换。</p></div><Button size="sm" variant="ghost" onClick={() => void tasks.refetch()}>刷新任务</Button></div>
          {tasks.isPending ? <Skeleton className="h-16" /> : tasks.isError ? <p role="alert" className="text-sm text-text-2">任务加载失败：{errorText(tasks.error)}</p>
            : !tasks.data?.length ? <p className="text-sm text-text-3">还没有派生任务。</p>
              : <div className="space-y-2">{tasks.data.map((item) => <button key={item.taskId} type="button" onClick={() => setTaskId(item.taskId)}
                className="flex w-full flex-wrap items-center justify-between gap-2 rounded-ctl border border-border p-3 text-left text-sm hover:bg-surface">
                <span className="min-w-0"><span className="block truncate">{time(item.createdAt)} · {item.coverage.complete ? '材料覆盖完整' : '有材料缺口'}</span><span className="mt-1 block text-xs text-text-3">ASR {item.coverage.completedTranscriptSegments}/{item.coverage.transcriptSegments} 片 · 图片 {item.coverage.completedPhotos + item.coverage.completedInkPreviews}/{item.coverage.photos + item.coverage.inkPreviews}</span></span>
                <State value={item.phase} /></button>)}</div>}
        </Card>

        {taskId && <TaskDetailCard key={taskId} taskId={taskId} ownerId={ownerId} serverBinding={serverBinding} onChanged={refresh} />}
    </div>

    {message && <p role="status" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {noteId && <div className="grid gap-5 lg:grid-cols-2">
      {(['CompleteRecord', 'IndependentAnalysis'] as const).map((kind) => {
        const artifact = artifacts.data?.find((item) => item.kind === kind)
        if (!artifact) return <Card key={kind} className="p-5"><EmptyState title={kindNames[kind]} description="任务生成完成后，候选版本会出现在这里；需要明确选择后才成为当前选用版本。" /></Card>
        return <ArtifactCard key={`${artifact.artifactId}/${artifact.revision}`} artifact={artifact} ownerId={ownerId} serverBinding={serverBinding} onChanged={refreshArtifacts} />
      })}
    </div>}
  </div>
}

function StartTaskCard({ ownerId, serverBinding, role, source, destinations, destinationsError, asrTasks, asrLoading, limits, onCreated }: {
  ownerId: string
  serverBinding: string
  role?: string
  source: NoteDerivedSource
  destinations: NoteDerivedDestination[]
  destinationsError: string
  asrTasks: NoteAsrTask[]
  asrLoading: boolean
  limits: NoteDerivedLimits | undefined
  onCreated: (task: NoteDerivedTask) => void
}) {
  const scope = bindingScope(ownerId, serverBinding, 'task-create', source.versionId, 'CompleteRecord')
  const initial = loadLocalRecord(scope)
  const [stored, setStored] = useState(initial.record)
  const [storageError, setStorageError] = useState(initial.error)
  const [destinationId, setDestinationId] = useState('')
  const [asrTaskId, setAsrTaskId] = useState('')
  const [consentedTo, setConsentedTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const destination = destinations.find((item) => destinationKey(item) === destinationId)
  const completedAsr = asrTasks.filter((task) => task.versionId === source.versionId && task.phase === 'Succeeded')
  const needsAsr = source.recordings > 0
  const approval = [source.versionId, destinationId, needsAsr ? asrTaskId : 'zero-recording'].join('/')
  const ready = Boolean(destination) && (!needsAsr || Boolean(asrTaskId)) && !stored.pending
  const pending = stored.pending

  async function persistIntent(intentId: string, body: StartNoteDerived) {
    const reserved = reserveAndPersistIntent(stored, 'create', intentId, {}, JSON.stringify(body))
    if (!reserved.ok) {
      setStorageError(reserved.error.message)
      return null
    }
    setStored(reserved.value.record)
    setStorageError(null)
    return reserved.value
  }

  async function start() {
    if (!destination || busy || consentedTo !== approval || stored.pending) return
    const body: StartNoteDerived = {
      taskId: createAiRequestId(),
      expectedUserId: ownerId,
      versionId: source.versionId,
      asrTaskId: needsAsr ? asrTaskId : null,
      consent: { modelId: destination.modelId, modelRevision: destination.modelRevision, endpoint: destination.endpoint, confirmed: true },
    }
    const reserved = await persistIntent(body.taskId, body)
    if (!reserved) return
    setBusy(true); setMessage('')
    try {
      const task = await noteDerivedApi.start(body)
      const cleared = clearStoredIntent(reserved.record, body.taskId)
      if (cleared.ok) setStored(cleared.value)
      onCreated(task)
    } catch (error) {
      setMessage(errorText(error) + '。提交意图已保存，请用同一标识核对或重新提交，不要新建另一个任务。')
    } finally {
      setBusy(false)
    }
  }

  async function reconcile() {
    if (!pending?.requestJson || busy) return
    let body: StartNoteDerived
    try { body = JSON.parse(pending.requestJson) as StartNoteDerived } catch { setStorageError('原提交意图无法解析，请保留本机记录并联系维护者核对。'); return }
    setBusy(true); setMessage('')
    try {
      const task = await noteDerivedApi.start(body)
      const cleared = clearStoredIntent(stored, pending.intentId)
      if (cleared.ok) setStored(cleared.value)
      onCreated(task)
    } catch (error) {
      setMessage(errorText(error) + '。仍保留原任务标识，没有重新分配。')
    } finally {
      setBusy(false)
    }
  }

  return <Card className="space-y-4 p-5">
    <SectionTitle number={source.recordings > 0 ? '2' : '1'} title="生成目标与同意" description="普通账号只消费管理员已启用的安全目标；不读取管理员模型设置或凭据。" />
    {destinationsError && <p role="alert" className="text-sm text-text-2">安全目标加载失败：{destinationsError}</p>}
    <div className="grid gap-3 md:grid-cols-2">
      <div><Label htmlFor="note-derived-model">本次生成目标</Label><Select id="note-derived-model" ariaLabel="本次生成目标" value={destinationId} className="mt-1 w-full"
        options={[{ value: '', label: destinations.length ? '请选择安全目标' : '暂无可用安全目标' }, ...destinations.map((item) => ({ value: destinationKey(item), label: item.displayName }))]}
        onValueChange={(value) => { setDestinationId(value); setConsentedTo('') }} disabled={busy || Boolean(stored.pending)} /></div>
      {needsAsr && <div><Label htmlFor="note-derived-asr">同版本已完成 ASR</Label><Select id="note-derived-asr" ariaLabel="同版本已完成 ASR" value={asrTaskId} className="mt-1 w-full"
        options={[{ value: '', label: asrLoading ? '正在读取 ASR 任务' : completedAsr.length ? '请选择已成功的 ASR 任务' : '没有已完整成功的 ASR 任务' }, ...completedAsr.map((task) => ({ value: task.taskId, label: `${time(task.createdAt)} · ${task.completedSegments}/${task.totalSegments} 片` }))]}
        onValueChange={(value) => { setAsrTaskId(value); setConsentedTo('') }} disabled={busy || Boolean(stored.pending)} /></div>}
    </div>
    {!destinations.length && <p role="status" className="rounded-ctl bg-surface p-3 text-xs leading-5 text-text-3">暂无可用的共享生成目标。{role === 'admin' ? '请先在“设置 → AI 与 ASR”启用安全目标。' : '普通账号不能查看或配置模型凭据，请联系管理员启用共享目标。'}</p>}
    {needsAsr && !asrLoading && completedAsr.length === 0 && <p role="status" className="rounded-ctl bg-surface p-3 text-xs leading-5 text-text-3">{role === 'admin' ? '该版本还没有完整成功的 ASR。请先在“设置 → AI 与 ASR”完成配置与 ASR 任务后再生成。' : '该版本还没有完整成功的 ASR。请使用客户端完成录音转写；共享 ASR 目标由管理员预先启用。'}</p>}
    {!needsAsr && <p className="rounded-ctl bg-surface p-3 text-xs leading-5 text-text-3">该版本为零录音材料，不需要绑定 ASR 任务；正文、照片和手写图仍会由服务端完整核对。</p>}
    <div role="group" aria-label="本次生成目标、材料范围与费用" className="space-y-1 rounded-ctl border border-border p-3 text-xs leading-5 text-text-2">
      <p>{destination ? `本次目标：${destination.displayName} · ${destination.model}` : '本次目标：尚未选择可用的共享生成目标'}</p>
      <p>发送材料：固定版本的完整正文、照片和手写渲染图{needsAsr ? '，以及已核对成功的 ASR 转写' : ''}。</p>
      <p className="font-medium text-warn">创建后可能产生服务商费用；页面不会自动创建或自动付费。</p>
      {destination && <><p>能力：{destination.capabilities.join(' / ') || '由服务端判定'}</p><details className="text-text-3"><summary className="cursor-pointer">处理详情：服务地址</summary><p className="mt-1 break-all font-mono">{destination.endpoint}</p></details></>}
    </div>
    <label className="flex items-start gap-2 rounded-ctl border border-border p-3 text-xs leading-5 text-text-2">
      <input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary" checked={consentedTo === approval && approvedKey(source.versionId, destinationId, asrTaskId, needsAsr) === approval} disabled={!ready || busy}
        onChange={(event) => setConsentedTo(event.target.checked ? approval : '')} />
      <span>确认将上述材料发送至上方所选生成目标，并理解可能产生服务商费用。目标或材料版本变化后需要重新确认。</span>
    </label>
    {storageError && <p role="alert" className="rounded-ctl bg-warn-soft p-3 text-xs leading-5 text-warn">{storageError}</p>}
    {pending && <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-text-2"><p>已有创建意图 {pending.intentId}，还没有确认服务端结果。刷新或丢响应时必须用同一标识核对。</p><Button size="sm" onClick={() => void reconcile()} disabled={busy || !pending.requestJson}>核对原提交</Button></div>}
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    <div className="flex flex-wrap gap-2"><Button variant="primary" loading={busy} disabled={!ready || consentedTo !== approval} onClick={() => void start()}><Play className="size-4" aria-hidden />创建派生任务</Button>
      {limits && <span className="self-center text-xs text-text-3">单步证据最多 {limits.maxEvidenceCharsPerStep.toLocaleString()} 字符 · 最多 {limits.maxVisionCalls} 次图片调用</span>}</div>
  </Card>
}

function TaskDetailCard({ taskId, ownerId, serverBinding, onChanged }: { taskId: string; ownerId: string; serverBinding: string; onChanged: () => void }) {
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const task = useQuery({ queryKey: ['note-derived', ownerId, serverBinding, 'task', taskId], queryFn: ({ signal }) => noteDerivedApi.task(taskId, signal), refetchInterval: 5000 })
  const steps = useQuery({ queryKey: ['note-derived', ownerId, serverBinding, 'steps', taskId], queryFn: ({ signal }) => noteDerivedApi.steps(taskId, 0, signal), refetchInterval: 5000 })
  if (task.isPending) return <Card className="p-5"><Skeleton className="h-28" /></Card>
  if (task.isError || !task.data) return <Card className="p-5"><EmptyState title="任务读取失败" description={errorText(task.error)} action={<Button onClick={() => void task.refetch()}>重试</Button>} /></Card>
  const value = task.data
  async function stop() {
    setBusy(true); setMessage('')
    try { await noteDerivedApi.stop(taskId); await qc.invalidateQueries({ queryKey: ['note-derived', ownerId, serverBinding] }); onChanged() }
    catch (error) { setMessage(errorText(error)) }
    finally { setBusy(false) }
  }
  return <Card className="space-y-4 p-5">
    <div className="flex flex-wrap items-start justify-between gap-2"><div><h2 className="text-sm font-semibold text-text-1">当前任务进度</h2><p className="mt-1 text-xs text-text-3">{time(value.createdAt)} · 任务已保存，可离开后返回</p></div><State value={value.phase} /></div>
    <details className="text-xs leading-5 text-text-3"><summary className="cursor-pointer">处理详情</summary><div className="mt-2 space-y-3 rounded-ctl bg-surface p-3"><div className="space-y-1 font-mono"><p className="break-all">任务 ID：{value.taskId}</p><p className="break-all">材料来源版本：{value.sourceVersionId}</p><p className="break-all">输入摘要：{value.inputSha256}</p></div>
    <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
      <Metric label="正文块" value={String(value.coverage.textBlocks)} />
      <Metric label="ASR 片段" value={`${value.coverage.completedTranscriptSegments}/${value.coverage.transcriptSegments}`} />
      <Metric label="照片" value={`${value.coverage.completedPhotos}/${value.coverage.photos}`} />
      <Metric label="手写图" value={`${value.coverage.completedInkPreviews}/${value.coverage.inkPreviews}`} />
    </div>
    </div></details>
    {!value.coverage.complete && <div className="rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-text-2"><p className="font-medium">当前不能发布“完整”结果</p><p>材料覆盖仍有缺口；生成任务可以继续保持失败状态，不能被页面改写成完整。</p></div>}
    {value.issues.length > 0 && <div className="space-y-2"><h3 className="text-xs font-medium text-text-2">覆盖缺口</h3>{value.issues.map((issue, index) => <div key={`${issue.code}/${issue.stepKey}/${index}`} className="rounded-ctl border border-border p-3 text-xs leading-5 text-text-2"><p className="font-medium">{issue.message}</p><p className="mt-1 break-all text-text-3">{issue.code}{issue.sourcePath ? ` · ${issue.sourcePath}` : ''}{issue.stepKey ? ` · ${issue.stepKey}` : ''}</p></div>)}</div>}
    {!value.backgroundProcessingAvailable && <p className="text-xs text-text-3">服务器后台处理当前不可用；任务已保存，不会伪造完成状态。</p>}
    <div className="flex flex-wrap gap-2"><Button size="sm" variant="secondary" disabled={busy || value.stopRequested || value.phase === 'Succeeded'} onClick={() => void stop()}><Square className="size-3.5" aria-hidden />停止后续步骤</Button><Button size="sm" variant="ghost" onClick={() => { void task.refetch(); void steps.refetch() }}><RefreshCw className="size-3.5" aria-hidden />刷新步骤</Button></div>
    <details className="text-xs text-text-3"><summary className="cursor-pointer">步骤、尝试与 endpoint 详情</summary>
    {steps.isPending ? <Skeleton className="h-28" /> : steps.isError ? <p role="alert" className="text-sm text-text-2">步骤读取失败：{errorText(steps.error)}</p>
      : !steps.data?.items.length ? <p className="text-sm text-text-3">当前没有可显示的步骤。</p>
        : <div className="space-y-2">{steps.data.items.map((step) => <div key={step.key} className="space-y-2 rounded-ctl border border-border p-3">
          <div className="flex flex-wrap items-center justify-between gap-2"><div><p className="text-sm text-text-1">{stepNames[step.kind] ?? step.kind}</p><p className="mt-1 break-all text-xs text-text-3">{step.key}</p></div><State value={step.status} /></div>
          {step.attempts.length > 0 && <div className="space-y-1">{step.attempts.map((attempt, index) => <p key={attempt.requestId} className="break-all text-xs text-text-3">第 {index + 1} 次 · {attempt.status}{attempt.errorCode ? ` · ${attempt.errorCode}` : ''}{attempt.errorMessage ? ` · ${attempt.errorMessage}` : ''}{attempt.logId ? ` · 调用 ${attempt.logId}` : ''}</p>)}</div>}
        </div>)}</div>}
    </details>
    <RetryControls key={`${value.taskId}/${value.sourceVersionId}`} task={value} steps={steps.data} ownerId={ownerId} serverBinding={serverBinding} onChanged={onChanged} />
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
  </Card>
}

function RetryControls({ task, steps, ownerId, serverBinding, onChanged }: { task: NoteDerivedTask; steps: NoteDerivedSteps | undefined; ownerId: string; serverBinding: string; onChanged: () => void }) {
  const scope = bindingScope(ownerId, serverBinding, `task-retry:${task.taskId}`, task.sourceVersionId, 'CompleteRecord')
  const initial = loadLocalRecord(scope)
  const [stored, setStored] = useState(initial.record)
  const [selected, setSelected] = useState<string[]>([])
  const [ack, setAck] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState(initial.error ?? '')
  const pending = stored.pending
  const candidates = steps?.items.filter((step) => step.status !== 'Succeeded' && step.status !== 'Running') ?? []
  const approval = `${task.revision}/${selected.join(',')}/${ack}`

  async function submit(body: RetryNoteDerived) {
    const reserved = reserveAndPersistIntent(stored, 'retry', body.retryId, {}, JSON.stringify(body))
    if (!reserved.ok) { setMessage(reserved.error.message); return null }
    setStored(reserved.value.record)
    setMessage('')
    return reserved.value
  }
  async function retry() {
    if (busy || !selected.length || !ack || pending) return
    const body: RetryNoteDerived = { retryId: createAiRequestId(), expectedUserId: ownerId, expectedRevision: task.revision, stepKeys: selected, consent: task.destination, acknowledgePossibleDuplicateCharge: true }
    const reserved = await submit(body)
    if (!reserved) return
    setBusy(true)
    try {
      await noteDerivedApi.retry(task.taskId, body)
      const cleared = clearStoredIntent(reserved.record, body.retryId)
      if (cleared.ok) setStored(cleared.value)
      setSelected([]); setAck(false); onChanged()
    } catch (error) { setMessage(errorText(error) + '。恢复意图已保存，必须用同一标识核对。') }
    finally { setBusy(false) }
  }
  async function replay() {
    if (!pending?.requestJson || busy) return
    let body: RetryNoteDerived
    try { body = JSON.parse(pending.requestJson) as RetryNoteDerived } catch { setMessage('原恢复意图无法解析，未重新分配。'); return }
    setBusy(true)
    try {
      await noteDerivedApi.retry(task.taskId, body)
      const cleared = clearStoredIntent(stored, pending.intentId)
      if (cleared.ok) setStored(cleared.value)
      onChanged()
    } catch (error) { setMessage(errorText(error) + '。仍保留原恢复标识。') }
    finally { setBusy(false) }
  }
  if (!candidates.length && !pending) return null
  return <div className="space-y-3 rounded-ctl border border-warn-border p-3">
    <h3 className="text-xs font-medium text-text-2">显式恢复</h3>
    {!pending && candidates.map((step) => <label key={step.key} className="flex items-center gap-2 text-xs text-text-2"><input type="checkbox" checked={selected.includes(step.key)}
      onChange={(event) => setSelected((current) => event.target.checked ? [...current, step.key] : current.filter((key) => key !== step.key))} disabled={busy} />{stepNames[step.kind] ?? step.kind} · {step.key}</label>)}
    {pending && <div className="space-y-2 text-xs leading-5 text-text-2"><p>已有恢复意图 {pending.intentId}，不会分配新标识。</p><Button size="sm" disabled={busy || !pending.requestJson} onClick={() => void replay()}>核对原恢复提交</Button></div>}
    {!pending && selected.length > 0 && <><label className="flex items-start gap-2 text-xs leading-5 text-text-2"><input type="checkbox" checked={ack} onChange={(event) => setAck(event.target.checked)} /><span>确认恢复 {selected.length} 个步骤，理解未知结果可能重复计费；成功步骤不重跑。</span></label>
      <Button size="sm" disabled={busy || !ack || approval === ''} onClick={() => void retry()}>恢复所选步骤</Button></>}
    {message && <p role="alert" className="text-xs leading-5 text-text-2">{message}</p>}
  </div>
}

function ArtifactCard({ artifact, ownerId, serverBinding, onChanged }: { artifact: NoteDerivedArtifact; ownerId: string; serverBinding: string; onChanged: () => void }) {
  const [selectedVersionId, setSelectedVersionId] = useState(artifact.headVersionId ?? artifact.versions[0]?.versionId ?? '')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const version = useQuery({
    queryKey: ['note-derived', ownerId, serverBinding, 'version', artifact.artifactId, selectedVersionId],
    queryFn: ({ signal }) => noteDerivedApi.version(artifact.artifactId, selectedVersionId, signal),
    enabled: Boolean(selectedVersionId),
  })
  const selectedSummary = artifact.versions.find((item) => item.versionId === selectedVersionId)

  async function selectVersion() {
    if (!selectedVersionId || busy) return
    setBusy(true); setMessage('')
    try {
      await noteDerivedApi.select(artifact.artifactId, { expectedHeadRevision: artifact.revision, versionId: selectedVersionId })
      if (version.data) {
        const scope = bindingScope(ownerId, serverBinding, artifact.artifactId, version.data.sourceVersionId, artifact.kind)
        const loaded = loadStoredDraft(scope)
        if (!loaded.ok) setMessage(loaded.error.message)
        else if (!loaded.value) {
          const reset = createStoredDraft(scope, version.data.contentMarkdown, version.data.versionId)
          const persisted = persistStoredDraft(reset, { allowBaseChange: true })
          if (!persisted.ok) setMessage(persisted.error.message)
        } else if (canAdoptSelectedVersion(loaded.value, version.data.versionId)) {
          setMessage('服务端版本已设为当前；本机草稿已基于该版本，未改写本机内容。')
        } else if (!loaded.value.dirty && !loaded.value.pending) {
          const reset: StoredNoteDerivedDraft = {
            ...loaded.value,
            draft: {
              ...loaded.value.draft,
              text: version.data.contentMarkdown,
              savedText: version.data.contentMarkdown,
              baseToken: version.data.versionId,
              editRevision: loaded.value.draft.editRevision + 1,
            },
            dirty: false,
          }
          const persisted = persistStoredDraft(reset, { allowBaseChange: true })
          if (!persisted.ok) setMessage(persisted.error.message)
        } else {
          setMessage(`服务端版本已设为当前；本机草稿基于 ${loaded.value.draft.baseToken || '未记录版本'}，已保留，未被选择操作覆盖。`)
        }
      }
      onChanged()
    } catch (error) { setMessage(errorText(error)) }
    finally { setBusy(false) }
  }

  return <Card className="space-y-4 p-5">
    <div className="flex flex-wrap items-start justify-between gap-3"><div><h2 className="text-base font-semibold text-text-1">{kindNames[artifact.kind]}</h2><p className="mt-1 text-xs leading-5 text-text-3">正文优先显示；选择新版本或人工修订都不会覆盖旧正文。</p></div><StatusBadge tone={artifact.headVersionId ? 'ok' : 'warn'} dot={false}>{artifact.headVersionId ? '已选用版本' : '等待明确选择'}</StatusBadge></div>
    <div className="text-xs text-text-3">当前查看：{selectedSummary ? `${authorNames[selectedSummary.author]} · ${time(selectedSummary.createdAt)}` : '尚无版本'}</div>
    {selectedSummary && <details className="text-xs leading-5 text-text-3"><summary className="cursor-pointer">高级选项：版本历史与处理详情</summary><div className="mt-2 space-y-3 rounded-ctl bg-surface p-3"><p>模型生成版本先作为候选；需要时可明确切换选用版本，保留人工稿和历史记录。</p><div className="space-y-2"><Label htmlFor={`artifact-version-${artifact.artifactId}`}>版本历史</Label><Select id={`artifact-version-${artifact.artifactId}`} ariaLabel={`${kindNames[artifact.kind]}版本历史`} value={selectedVersionId} className="w-full"
      options={artifact.versions.length ? artifact.versions.map((item) => ({ value: item.versionId, label: `${authorNames[item.author]} · ${time(item.createdAt)}${item.isHead ? ' · 已选用' : ''}${item.hasConflict ? ' · 冲突候选' : ''}` })) : [{ value: '', label: '还没有可读取版本' }]}
      onValueChange={setSelectedVersionId} disabled={busy || !artifact.versions.length} /></div>
    <div className="flex flex-wrap items-center gap-2"><Button size="sm" variant="secondary" disabled={busy || !selectedVersionId || selectedSummary.isHead} onClick={() => void selectVersion()}>明确设为当前版本</Button><Button size="sm" variant="ghost" disabled={!selectedVersionId} onClick={() => void version.refetch()}><RefreshCw className="size-3.5" aria-hidden />刷新正文</Button></div>
    <div className="space-y-1 font-mono"><p className="break-all">结果 ID：{artifact.artifactId}</p><p className="break-all">版本 ID：{selectedSummary.versionId}</p><p className="break-all">材料来源版本：{selectedSummary.sourceVersionId}</p>{selectedSummary.generationTaskId && <p className="break-all">生成任务：{selectedSummary.generationTaskId}</p>}</div></div></details>}
    {version.isPending ? <Skeleton className="h-40" /> : version.isError ? <p role="alert" className="text-sm text-text-2">版本读取失败：{errorText(version.error)}</p>
      : version.data ? <ArtifactEditor key={`${artifact.revision}/${version.data.versionId}`} artifact={artifact} version={version.data} ownerId={ownerId} serverBinding={serverBinding} onChanged={onChanged} /> : null}
    {version.data && <NoteTaskSuggestionsPanel
      key={`task-suggestions-${artifact.artifactId}/${version.data.versionId}`}
      userId={ownerId} serverBinding={serverBinding} noteId={artifact.noteId}
      artifactId={artifact.artifactId} versionId={version.data.versionId} />}
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
  </Card>
}

function ArtifactEditor({ artifact, version, ownerId, serverBinding, onChanged }: { artifact: NoteDerivedArtifact; version: NoteDerivedVersion; ownerId: string; serverBinding: string; onChanged: () => void }) {
  const scope = bindingScope(ownerId, serverBinding, artifact.artifactId, version.sourceVersionId, artifact.kind)
  const initial = loadLocalRecord(scope)
  const [stored, setStored] = useState(() => initial.record.draft.text || initial.record.pending ? initial.record : { ...initial.record, draft: createStoredDraft(scope, version.contentMarkdown, version.versionId).draft, dirty: false })
  const [error, setError] = useState(initial.error ?? '')
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const latest = useRef(stored)
  const pending = stored.pending
  const canSave = artifact.headVersionId === version.versionId && !pending && !busy

  function commit(record: StoredNoteDerivedDraft) {
    latest.current = record
    setStored(record)
  }

  function edit(text: string) {
    const next = editStoredDraft(stored, text)
    if (!next.ok) { setError(next.error.message); return }
    const persisted = persistStoredDraft(next.value)
    if (persisted.ok) {
      commit(persisted.value)
      setError('')
    } else {
      commit(next.value)
      setError(persisted.error.message)
    }
  }

  async function saveNew() {
    if (!canSave || !artifact.headVersionId) return
    const body: SaveNoteDerivedManual = { revisionId: createAiRequestId(), expectedHeadRevision: artifact.revision, expectedHeadVersionId: artifact.headVersionId, contentMarkdown: stored.draft.text }
    const started = beginManualSave(stored.draft)
    const reserved = reserveAndPersistIntent({ ...stored, draft: started.state }, 'manual-save', body.revisionId, {}, JSON.stringify(body))
    if (!reserved.ok) { setError(reserved.error.message); return }
    commit(reserved.value.record); setBusy(true); setMessage('')
    try {
      const mutation = await noteDerivedApi.saveManual(artifact.artifactId, body)
      const receipt = reconcileReceipt(latest.current, started.snapshot, mutation, body, body.revisionId)
      commit(receipt.record); setError(receipt.error); setMessage(receipt.message)
      onChanged()
    } catch (failure) { setMessage(errorText(failure) + '。人工保存意图已保存，刷新后必须用同一标识重新提交核对。') }
    finally { setBusy(false) }
  }

  async function replay() {
    if (!pending?.requestJson || busy) return
    let body: SaveNoteDerivedManual
    try { body = JSON.parse(pending.requestJson) as SaveNoteDerivedManual } catch { setError('原人工保存意图无法解析；没有重新分配标识。'); return }
    const snapshot: ManualSaveSnapshot = {
      sequence: stored.draft.saveSequence,
      editRevision: pending.submittedEditRevision ?? stored.draft.editRevision,
      baseToken: pending.submittedBaseToken ?? stored.draft.baseToken,
      text: pending.submittedText ?? body.contentMarkdown,
    }
    setBusy(true)
    try {
      const mutation = await noteDerivedApi.saveManual(artifact.artifactId, body)
      const receipt = reconcileReceipt(latest.current, snapshot, mutation, body, pending.intentId)
      commit(receipt.record); setError(receipt.error); setMessage(receipt.message)
      onChanged()
    } catch (failure) { setMessage(errorText(failure) + '。仍保留原人工保存标识。') }
    finally { setBusy(false) }
  }

  function reconcileReceipt(
    current: StoredNoteDerivedDraft,
    snapshot: ManualSaveSnapshot,
    mutation: { artifact: NoteDerivedArtifact; versionId: string },
    body: SaveNoteDerivedManual,
    intentId: string,
  ) {
    const headApplied = isVersionHead(mutation.artifact.headVersionId, mutation.versionId)
    const resolution = resolveManualSave(current.draft, snapshot, { kind: 'saved', headToken: mutation.versionId })
    const cleared = clearStoredIntent(current, intentId)
    const base = cleared.ok ? cleared.value : current
    let next: StoredNoteDerivedDraft
    let nextMessage: string
    if (resolution.status === 'edited-during-save' || resolution.status === 'superseded') {
      next = base
      nextMessage = '保存回执已核对；保存期间的新输入仍保留，旧回执没有覆盖本机草稿。'
    } else {
      const baseToken = headApplied ? mutation.versionId : (mutation.artifact.headVersionId ?? current.draft.baseToken)
      next = {
        ...base,
        draft: { ...base.draft, savedText: body.contentMarkdown, baseToken },
        dirty: false,
      }
      nextMessage = headApplied
        ? '人工修订已保存并成为当前版本。'
        : '候选已保存，当前选用未改变；请比较后明确选择。'
    }
    const persisted = persistStoredDraft(next, { allowBaseChange: true, allowPendingChange: true })
    return {
      record: persisted.ok ? persisted.value : next,
      error: persisted.ok ? '' : persisted.error.message,
      message: nextMessage,
    }
  }

  return <div className="space-y-3 rounded-ctl border border-border p-3">
    <div className="flex flex-wrap items-center justify-between gap-2"><div><h3 className="text-sm font-medium text-text-1">完整文本编辑</h3><p className="mt-1 text-xs text-text-3">当前查看：{authorNames[version.author]} · {time(version.createdAt)} · {version.isHead ? '当前选用版本' : '候选版本'}</p></div><StatusBadge tone={stored.dirty ? 'warn' : 'neutral'} dot={false}>{stored.dirty ? '有未保存修改' : '已保存到本机'}</StatusBadge></div>
    <details className="text-xs leading-5 text-text-3"><summary className="cursor-pointer">本机草稿核对信息</summary><div className="mt-2 space-y-1 rounded-ctl bg-surface p-3 font-mono"><p className="break-all">本机草稿基于：{stored.draft.baseToken || '未绑定版本'}</p><p className="break-all">结果 ID：{artifact.artifactId}</p><p className="break-all">材料来源版本：{version.sourceVersionId}</p></div></details>
    <details className="text-xs text-text-3"><summary className="cursor-pointer">查看所选版本服务端原文</summary><pre className="mt-2 max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-ctl bg-surface p-3 text-text-2">{version.contentMarkdown}</pre></details>
    <Textarea aria-label={`${kindNames[artifact.kind]}正文`} className="min-h-72 font-mono text-xs" value={stored.draft.text} onChange={(event) => edit(event.target.value)} />
    {error && <p role="alert" className="rounded-ctl bg-warn-soft p-3 text-xs leading-5 text-warn">{error}</p>}
    {message && <p role="status" className="rounded-ctl bg-surface p-3 text-xs leading-5 text-text-2">{message}</p>}
    {pending && <div className="space-y-2 rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-text-2"><p>人工保存意图 {pending.intentId} 已保存，但服务端结果未确认。</p><Button size="sm" disabled={busy || !pending.requestJson} onClick={() => void replay()}>用原标识核对</Button></div>}
    <div className="flex flex-wrap items-center gap-2"><Button size="sm" variant="primary" loading={busy} disabled={!canSave} onClick={() => void saveNew()}>保存为人工修订</Button><span className="text-xs text-text-3">{!artifact.headVersionId ? '请先选择一个版本作为当前选用版本。' : version.versionId !== artifact.headVersionId ? '所选版本不是当前选用版本，请先明确选择。' : '保存冲突会保留候选，不覆盖当前选用版本。'}</span></div>
  </div>
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="rounded-ctl bg-surface p-3"><p className="text-xs text-text-3">{label}</p><p className="mt-1 text-sm font-medium text-text-1">{value}</p></div>
}

function destinationKey(destination: NoteDerivedDestination) {
  return `${destination.modelId}/${destination.modelRevision}/${destination.endpoint}`
}

function approvedKey(versionId: string, destinationId: string, asrTaskId: string, needsAsr: boolean) {
  return [versionId, destinationId, needsAsr ? asrTaskId : 'zero-recording'].join('/')
}
