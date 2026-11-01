import { useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Button, Card, EmptyState, Label, Select, Skeleton, StatusBadge } from '@/components/ui'
import { formatTime } from '@/lib/datetime'
import { aiApi, type Model, type ModelSettings, type NoteAsrStart, type NoteAsrTask } from './api'
import { createAiRequestId } from './request-id'
import { NoteAsrReprocessPanel } from './note-asr-reprocess-panel'
import { canonicalServerBinding } from './note-derived/draft-storage'

const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请刷新核对'
const names: Record<string, string> = { Queued: '等待处理', Running: '转写中', Succeeded: '转写完成', NeedsAttention: '有分片需处理', Stopped: '已停止',
  Failed: '失败', Blocked: '未发送', Cancelled: '已取消', Incomplete: '结果未完整确认', TimedOut: '超时', Unknown: '结果待核对' }
const time = (samples: number, rate = 16000) => `${Math.floor(samples / rate / 60)}:${String(Math.floor(samples / rate) % 60).padStart(2, '0')}`
function State({ value }: { value: string }) {
  return <StatusBadge dot={false} tone={value === 'Succeeded' ? 'ok' : ['Queued', 'Running'].includes(value) ? 'info' : 'warn'}>{names[value] ?? value}</StatusBadge>
}

export function NoteAsrTasks({ userId, models, settings, showDetail }: { userId: string; models: Model[]; settings: ModelSettings; showDetail: (id: string) => void }) {
  const qc = useQueryClient(), prefix = ['ai', userId, 'note-asr']
  const [sourcePage, setSourcePage] = useState(0), [versionId, setVersionId] = useState(''), [modelId, setModelId] = useState(''), [language, setLanguage] = useState('auto')
  const [approved, setApproved] = useState(''), [intent, setIntent] = useState<NoteAsrStart | null>(null), [selected, setSelected] = useState('')
  const [message, setMessage] = useState(''), [busy, setBusy] = useState(false)
  const sending = useRef(false)
  const sources = useQuery({ queryKey: [...prefix, 'sources', sourcePage], queryFn: ({ signal }) => aiApi.noteAsrSources(sourcePage, signal) })
  const tasks = useQuery({ queryKey: [...prefix, 'tasks'], queryFn: ({ signal }) => aiApi.noteAsrTasks(signal), refetchInterval: 5000 })
  const presets = useQuery({ queryKey: ['ai', userId, 'asr-presets'], queryFn: ({ signal }) => aiApi.asrPresets(signal) })
  const model = models.find((m) => m.id === (modelId || settings.transcriptionModelId))
  const preset = presets.data?.find((p) => p.id === model?.asrPresetId)
  const endpoint = model && preset ? model.baseUrl + preset.operationPath : ''
  const source = sources.data?.items.find((s) => s.versionId === versionId)
  const versionTasks = useMemo(() => versionId ? (tasks.data ?? []).filter((task) => task.versionId === versionId) : [], [tasks.data, versionId])
  const approvalKey = [versionId, model?.id, model?.revision, preset?.id, preset?.version, endpoint, language].join('/')
  const ready = settings.enabled && model?.enabled && preset && source && source.totalSamples > 0
  async function start() {
    if (sending.current || !ready || !model || !preset || approved !== approvalKey || intent) return
    const body: NoteAsrStart = { taskId: createAiRequestId(), expectedUserId: userId, versionId, language,
      consent: { modelId: model.id, modelRevision: model.revision, presetId: preset.id, presetVersion: preset.version, endpoint, confirmed: true } }
    sending.current = true; setBusy(true); setMessage(''); setIntent(body)
    try { const task = await aiApi.noteAsrStart(body); setSelected(task.taskId); setIntent(null); setApproved(''); await qc.invalidateQueries({ queryKey: prefix }) }
    catch (error) { setMessage(errorText(error) + '。任务可能已经保存，请先核对，页面不会自动新建或重新发送。') }
    finally { sending.current = false; setBusy(false) }
  }
  async function reconcileIntent() {
    if (!intent || busy) return
    setBusy(true)
    try { const task = await aiApi.noteAsrTask(intent.taskId); setSelected(task.taskId); setIntent(null); setMessage('已找到原任务，没有重新发送。'); await qc.invalidateQueries({ queryKey: prefix }) }
    catch (error) { setMessage(errorText(error) + '。可以使用原标识再次保存任务，服务端会核对已有任务。') }
    finally { setBusy(false) }
  }
  return <div className="space-y-4">
    <Card className="space-y-4 p-5"><div><h2 className="text-sm font-semibold text-text-1">固定笔记录音转写</h2>
      <p className="mt-1 text-xs text-text-3">选择已上传的材料版本，后台逐片转写。关闭页面后继续，已完成分片保留；编辑原笔记不会改变本次材料。Android 客户端可正式上传材料，Web 本机预览仍不能上传；材料接收后可前往“笔记处理”生成完整记录与独立分析。</p></div>
      {sources.isPending ? <Skeleton className="h-20" /> : sources.isError ? <EmptyState title="材料版本加载失败" action={<Button onClick={() => void sources.refetch()}>重试</Button>} />
        : !sources.data?.items.length ? <EmptyState title="没有已上传材料" description="Android 客户端可正式上传完整笔记材料；Web 本机预览仍不能上传。材料接收后请前往“笔记处理”。" action={<Button size="sm" variant="secondary" onClick={() => { window.location.assign('/native-notes') }}>前往笔记处理</Button>} />
          : <><Label htmlFor="note-asr-source">固定材料版本</Label><Select id="note-asr-source" ariaLabel="固定材料版本" value={versionId} disabled={busy || !!intent}
            options={sources.data.items.map((s) => ({ value: s.versionId, label: `${s.title || '未命名笔记'} · ${formatTime(s.createdAt)}${s.isCurrent ? ' · 当前版' : ' · 历史版'}${s.hasConflict ? ' · 冲突保全' : ''}` }))}
            onValueChange={(value) => { setVersionId(value); setApproved('') }} />
            {source && <p className="text-xs text-text-3">{source.recordings} 次录音 · 有效录音 {time(source.totalSamples, source.sampleRate)} · 暂停不计时。全部原录音保留。</p>}</>}
      {versionId && versionTasks.length > 0 && <div className="space-y-2 rounded-ctl border border-border p-3">
        <p className="text-xs font-medium text-text-2">该版本已有 {versionTasks.length} 个转写任务，历史任务与结果保留。</p>
        {versionTasks.map((task) => <button key={task.taskId} type="button"
          className="flex w-full flex-wrap items-center justify-between gap-2 rounded-ctl bg-surface px-3 py-2 text-left text-xs" onClick={() => setSelected(task.taskId)}>
          <span>历史任务 {formatTime(task.createdAt)} · {task.completedSegments}/{task.totalSegments} 片</span><State value={task.phase} /></button>)}
        <p className="text-xs text-text-3">再次开始会新建任务并可能产生服务商费用；不会覆盖或修改历史结果。</p>
      </div>}
      {sources.data && sources.data.total > 20 && <div className="flex gap-2"><Button size="sm" disabled={sourcePage === 0 || busy || !!intent} onClick={() => { setSourcePage((p) => p - 20); setVersionId('') }}>上一页材料</Button>
        <Button size="sm" disabled={sourcePage + 20 >= sources.data.total || busy || !!intent} onClick={() => { setSourcePage((p) => p + 20); setVersionId('') }}>下一页材料</Button></div>}
      <div><Label htmlFor="note-asr-model">长录音ASR模型</Label><Select id="note-asr-model" ariaLabel="长录音ASR模型" value={modelId} disabled={busy || !!intent}
        options={[{ value: '', label: '使用默认ASR模型' }, ...models.filter((m) => m.enabled && m.asrPresetId && m.capabilities.includes('Transcription')).map((m) => ({ value: m.id, label: m.name }))]}
        onValueChange={(value) => { setModelId(value); setApproved(''); setLanguage('auto') }} /></div>
      <div><Label htmlFor="note-asr-language">长录音识别语言</Label><Select id="note-asr-language" ariaLabel="长录音识别语言" value={language} disabled={busy || !!intent || !preset}
        options={(preset?.languages ?? ['auto']).map((value) => ({ value, label: ({ auto: '自动识别', zh: '中文', en: '英语' } as Record<string, string>)[value] ?? value }))}
        onValueChange={(value) => { setLanguage(value); setApproved('') }} /></div>
      {model && preset && <div className="space-y-2 rounded-ctl border border-border p-3 text-xs text-text-2"><p>发送至：{model.name} · {model.model} · {preset.name} v{preset.version}</p><p className="break-all font-mono">{endpoint}</p>
        <p>全部录音按最长一分钟拆分，逐片可能产生服务商费用。配置变化后会停止发送，需重新核对。</p>
        <label className="flex items-start gap-2"><input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary" checked={approved === approvalKey} disabled={!ready || busy || !!intent}
          onChange={(event) => setApproved(event.target.checked ? approvalKey : '')} /><span>确认将该固定版本的全部录音发送至上述模型</span></label></div>}
      <div className="flex flex-wrap gap-2"><Button variant="primary" loading={busy} disabled={!ready || approved !== approvalKey || !!intent} onClick={() => void start()}>开始后台转写</Button>
        {intent && <><Button disabled={busy} onClick={() => void reconcileIntent()}>核对已提交任务</Button><Button disabled={busy} variant="secondary" onClick={async () => {
          setBusy(true); try { const task = await aiApi.noteAsrStart(intent); setSelected(task.taskId); setIntent(null); setMessage('原任务已核对保存'); await qc.invalidateQueries({ queryKey: prefix }) }
          catch (error) { setMessage(errorText(error)) } finally { setBusy(false) }
        }}>使用原标识保存</Button></>}</div>
      {message && <p role="alert" className="text-sm text-text-2">{message}</p>}
    </Card>
    <Card className="space-y-3 p-5"><div className="flex items-center justify-between gap-3"><h2 className="text-sm font-semibold text-text-1">已保存的转写任务</h2><Button size="sm" variant="ghost" onClick={() => void qc.invalidateQueries({ queryKey: prefix })}>刷新任务</Button></div>
      {tasks.isPending ? <Skeleton className="h-20" /> : tasks.isError ? <p role="alert">任务加载失败，请刷新核对。</p> : !tasks.data?.length ? <p className="text-sm text-text-3">还没有转写任务。</p>
        : <div className="space-y-2">{tasks.data.map((task) => <button key={task.taskId} type="button" className="flex w-full flex-wrap items-center justify-between gap-2 rounded-ctl border border-border p-3 text-left text-sm" onClick={() => setSelected(task.taskId)}>
          <span>{formatTime(task.createdAt)} · {task.completedSegments}/{task.totalSegments} 片</span><State value={task.phase} /></button>)}</div>}
    </Card>
    {selected && <TaskDetail key={selected} id={selected} userId={userId} serverBinding={canonicalServerBinding()} prefix={prefix} showDetail={showDetail} />}
  </div>
}

export function TaskDetail({ id, userId, serverBinding, prefix, showDetail }: {
  id: string
  userId: string
  serverBinding: string
  prefix: string[]
  showDetail?: (id: string) => void
}) {
  const qc = useQueryClient(), [offset, setOffset] = useState(0), [selected, setSelected] = useState<number[]>([]), [approved, setApproved] = useState(''), [busy, setBusy] = useState(false), [message, setMessage] = useState('')
  const task = useQuery({ queryKey: [...prefix, id], queryFn: ({ signal }) => aiApi.noteAsrTask(id, signal), refetchInterval: 5000 })
  const segments = useQuery({ queryKey: [...prefix, id, 'segments', offset], queryFn: ({ signal }) => aiApi.noteAsrSegments(id, offset, signal), refetchInterval: 5000 })
  const approvalKey = `${task.data?.revision}/${selected.join(',')}`
  async function change(action: (task: NoteAsrTask) => Promise<NoteAsrTask>) {
    if (!task.data || busy) return
    setBusy(true); setMessage('')
    try { await action(task.data); setSelected([]); setApproved(''); await qc.invalidateQueries({ queryKey: prefix }) }
    catch (error) { setMessage(errorText(error) + '。请先刷新核对，恢复操作不会自动重发。') }
    finally { setBusy(false) }
  }
  return <Card className="space-y-4 p-5"><h2 className="text-sm font-semibold text-text-1">逐片进度与恢复</h2>
    {!task.data ? task.isError ? <p role="alert">任务读取失败，请刷新核对。</p> : <Skeleton className="h-20" /> : <>
      <div className="flex flex-wrap items-center justify-between gap-2"><State value={task.data.phase} /><span className="text-xs text-text-3">{task.data.completedSegments}/{task.data.totalSegments} 片 · 已覆盖 {time(task.data.completedSamples, task.data.sampleRate)} / {time(task.data.totalSamples, task.data.sampleRate)}</span></div>
      <progress aria-label="成功转写覆盖进度" className="h-2 w-full accent-primary" max={task.data.totalSamples} value={task.data.completedSamples} />
      {!task.data.backgroundProcessingAvailable && <p role="status" className="text-sm text-text-2">服务器未启用后台调度，任务已保存；启用后才会继续处理。</p>}
      <p className="break-all text-xs text-text-3">本任务发送目标：{task.data.destination?.endpoint ?? '本机执行 · 未发送云端'}</p>
      <p className="text-xs text-text-3">停止后不再发送下一片，当前已发送分片会先核对结果。未知结果不会自动重试；成功分片不会重跑。</p>
      {!task.data.stopRequested && task.data.phase !== 'Succeeded' && <Button size="sm" variant="secondary" disabled={busy} onClick={() => void change(() => aiApi.noteAsrStop(id))}>停止后续转写</Button>}
      {segments.isError && <p role="alert">分片结果加载失败，请刷新核对。</p>}
      <div className="space-y-3">{segments.data?.items.map((part) => {
        const retryable = !['Succeeded', 'Running'].includes(part.status)
        return <div key={part.index} className="space-y-2 rounded-ctl border border-border p-3"><div className="flex flex-wrap items-center justify-between gap-2"><span className="text-sm">第 {part.index + 1} 片 · {time(part.timelineOffsetSamples)}–{time(part.timelineOffsetSamples + part.samples)}</span><State value={part.status} /></div>
          <p className="text-xs text-text-3">第 {part.recordingSegment} 段 · 原始分块 {part.chunkIndex} · {part.attempts.length} 次记录</p>
          {part.transcript != null && <pre aria-label={`第${part.index + 1}片转写`} className="max-h-64 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{part.transcript || '空转写，请试听原音频核对。'}</pre>}
          {part.attempts.map((attempt, index) => <div key={attempt.requestId} className="flex flex-wrap items-center gap-2 text-xs text-text-3"><span>第 {index + 1} 次 · {names[attempt.status] ?? attempt.status}{attempt.errorMessage ? ` · ${attempt.errorMessage}` : ''}</span>
            {attempt.logId && showDetail && <Button size="sm" variant="ghost" onClick={() => showDetail(attempt.logId!)}>查看分片调用记录</Button>}</div>)}
          {retryable && <label className="flex items-center gap-2 text-xs text-text-2"><input type="checkbox" aria-label={`恢复第${part.index + 1}片`} checked={selected.includes(part.index)} disabled={busy}
            onChange={(event) => { setSelected((s) => event.target.checked ? [...s, part.index] : s.filter((i) => i !== part.index)); setApproved('') }} />选择恢复该片</label>}
        </div>
      })}</div>
      {segments.data && segments.data.total > 10 && <div className="flex gap-2"><Button size="sm" disabled={offset === 0} onClick={() => setOffset((p) => p - 10)}>上一页分片</Button><Button size="sm" disabled={offset + 10 >= segments.data.total} onClick={() => setOffset((p) => p + 10)}>下一页分片</Button></div>}
      {selected.length > 0 && <div className="space-y-3 rounded-ctl border border-border p-3"><p className="break-all text-xs text-text-2">
        {task.data.destination
          ? `恢复 ${selected.length} 片，仍发送至：${task.data.destination.endpoint} · ${task.data.destination.presetId} v${task.data.destination.presetVersion}。原调用记录和成功转写保留。`
          : `恢复 ${selected.length} 片，仍在本机执行；不需要云端凭据，本机不可用时只排队。原调用记录和成功转写保留。`}
      </p>
        <label className="flex items-start gap-2 text-xs text-text-2"><input type="checkbox" aria-label="确认分片恢复和可能重复计费" checked={approved === approvalKey} onChange={(event) => setApproved(event.target.checked ? approvalKey : '')} /><span>已核对原目标和记录，确认恢复，理解未知结果的重试可能重复计费</span></label>
        <Button disabled={busy || approved !== approvalKey} onClick={() => void change((value) => aiApi.noteAsrRetry(id, { retryId: createAiRequestId(), expectedUserId: userId, expectedRevision: value.revision,
          segmentIndexes: selected, consent: value.destination ?? null, acknowledgePossibleDuplicateCharge: true }))}>恢复所选分片</Button></div>}
      <NoteAsrReprocessPanel task={task.data} userId={userId} serverBinding={serverBinding} />
      {message && <p role="alert" className="text-sm text-text-2">{message}</p>}
    </>}
  </Card>
}
