import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ClipboardCheck, ListChecks, RefreshCw, ShieldCheck } from 'lucide-react'
import {
  Button,
  Card,
  Checkbox,
  EmptyState,
  InlineAlert,
  Input,
  Label,
  Select,
  Skeleton,
  StatusBadge,
  Textarea,
  TwoStepButton,
} from '@/components/ui'
import type { Tone } from '@/components/ui'
import { noteDerivedApi, type NoteDerivedDestination } from '../api'
import { createAiRequestId } from '../../request-id'
import {
  noteTaskSuggestionsApi,
  type NoteTaskSuggestion,
  type NoteTaskSuggestionExtraction,
  type StartNoteTaskSuggestionExtraction,
  type UpdateNoteTaskSuggestion,
} from './api'
import {
  clearConfirmIntent,
  clearExtractionIntent,
  loadConfirmIntent,
  loadExtractionId,
  loadExtractionIntent,
  saveConfirmIntent,
  saveExtractionId,
  saveExtractionIntent,
  type ExtractionIntentState,
} from './intent-storage'
import { fromLocalInputValue, toLocalInputValue } from '@/lib/datetime'

const statusNames: Record<string, string> = {
  Draft: '待确认',
  Confirmed: '已创建任务',
  Dismissed: '已放弃',
}
const extractionNames: Record<string, string> = {
  Queued: '排队中',
  Running: '提取中',
  Succeeded: '提取完成',
  Failed: '提取失败',
}
const priorityOptions = [
  { value: '1', label: '低' },
  { value: '5', label: '中' },
  { value: '9', label: '高' },
]

const statusTone = (status: string): Tone => status === 'Confirmed' ? 'ok'
  : status === 'Dismissed' ? 'neutral' : 'warn'
const extractionTone = (status: string): Tone => status === 'Succeeded' ? 'ok'
  : status === 'Failed' ? 'crit' : status === 'Draft' ? 'warn' : 'info'
const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败，请刷新核对'
const destinationKey = (value: NoteDerivedDestination) =>
  `${value.modelId}/${value.modelRevision}/${value.endpoint}`

/**
 * Owner-scoped note task suggestion lifecycle. It only reads the shared note
 * destination catalogue and never queries admin model settings or credentials.
 */
export function NoteTaskSuggestionsPanel({
  userId,
  serverBinding,
  noteId,
  artifactId,
  versionId,
  className,
  onTaskCreated,
}: {
  userId: string
  serverBinding: string
  noteId: string
  artifactId: string
  versionId: string
  className?: string
  onTaskCreated?: (suggestion: NoteTaskSuggestion) => void
}) {
  const qc = useQueryClient()
  const [destinationId, setDestinationId] = useState('')
  const [chargeConfirmed, setChargeConfirmed] = useState(false)
  const [intentState, setIntentState] = useState<ExtractionIntentState>({ kind: 'none' })
  const [extractionId, setExtractionId] = useState(() => loadExtractionId(userId, serverBinding, versionId))
  const [busy, setBusy] = useState('')
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')

  const version = useQuery({
    queryKey: ['note-derived', userId, serverBinding, 'task-suggestion-source', artifactId, versionId],
    queryFn: ({ signal }) => noteDerivedApi.version(artifactId, versionId, signal),
  })
  const destinations = useQuery({
    queryKey: ['note-derived', userId, serverBinding, 'destinations'],
    queryFn: ({ signal }) => noteDerivedApi.destinations(signal),
  })
  const suggestions = useQuery({
    queryKey: ['note-task-suggestions', userId, serverBinding, noteId],
    queryFn: ({ signal }) => noteTaskSuggestionsApi.list(noteId, signal),
    refetchInterval: (query) =>
      query.state.data?.items.some((item) => item.status === 'Draft') ? 15_000 : false,
  })
  const extraction = useQuery({
    queryKey: ['note-task-suggestion-extraction', userId, serverBinding, extractionId],
    queryFn: ({ signal }) => noteTaskSuggestionsApi.extraction(extractionId, signal),
    enabled: Boolean(extractionId),
    refetchInterval: (query) =>
      query.state.data?.status === 'Queued' || query.state.data?.status === 'Running' ? 3_000 : false,
  })

  const textDestinations = useMemo(() =>
    (destinations.data?.items ?? []).filter((item) => item.capabilities.includes('Text')),
  [destinations.data])
  const selectedDestination = textDestinations.find((item) => destinationKey(item) === destinationId) ?? null
  const versionItems = suggestions.data?.items
    .filter((item) => item.versionId === versionId)
    .sort((left, right) => left.ordinal - right.ordinal) ?? []
  const draftCount = versionItems.filter((item) => item.status === 'Draft').length
  const activeExtraction = extraction.data &&
    (extraction.data.status === 'Queued' || extraction.data.status === 'Running')
  const intent = intentState.kind === 'ready' ? intentState.value : null
  const intentBlocked = intentState.kind === 'blocked' ? intentState.message : ''

  useEffect(() => {
    setDestinationId('')
    setChargeConfirmed(false)
    setIntentState({ kind: 'none' })
    setExtractionId(loadExtractionId(userId, serverBinding, versionId))
    setBusy('')
    setNotice('')
    setError('')
  }, [userId, serverBinding, versionId])

  useEffect(() => {
    if (!version.data) return
    setIntentState(loadExtractionIntent(
      userId, serverBinding, noteId, artifactId, versionId, version.data.contentSha256))
  }, [artifactId, noteId, serverBinding, userId, version.data, versionId])

  const extractionStatus = extraction.data?.status
  useEffect(() => {
    if (extractionStatus !== 'Succeeded') return
    setNotice(`已固定本次笔记版本并生成 ${extraction.data?.suggestionCount ?? 0} 条建议。请逐条核对后再创建任务。`)
    void qc.invalidateQueries({ queryKey: ['note-task-suggestions', userId, serverBinding, noteId] })
  }, [extraction.data?.suggestionCount, extractionStatus, noteId, qc, serverBinding, userId])

  const sourceReady = Boolean(version.data)
  const canExtract = Boolean(
    sourceReady && selectedDestination && chargeConfirmed && !busy &&
    !activeExtraction && !intentBlocked,
  )

  function freezeExtraction(): StartNoteTaskSuggestionExtraction | null {
    if (!version.data || !selectedDestination) return null
    return {
      requestId: createAiRequestId(),
      expectedUserId: userId,
      noteId,
      artifactId,
      versionId,
      expectedContentSha256: version.data.contentSha256,
      consent: {
        modelId: selectedDestination.modelId,
        modelRevision: selectedDestination.modelRevision,
        endpoint: selectedDestination.endpoint,
        confirmed: true,
      },
      acknowledgePossibleCharge: true,
    }
  }

  async function submitExtraction(body: StartNoteTaskSuggestionExtraction) {
    setBusy('extract')
    setNotice('')
    setError('')
    try {
      const created = await noteTaskSuggestionsApi.startExtraction(body)
      if (version.data) {
        clearExtractionIntent(userId, serverBinding, noteId, artifactId, versionId,
          version.data.contentSha256, body.requestId)
      }
      saveExtractionId(userId, serverBinding, versionId, created.id)
      setIntentState({ kind: 'none' })
      setExtractionId(created.id)
      setChargeConfirmed(false)
      setNotice('任务提取已受理。固定正文快照和服务商目标已保存；页面会轮询真实状态，不会在本机自动改用另一模型。')
    } catch (failure) {
      setError(`${errorText(failure)}。已冻结请求标识；请使用原提取请求核对，不会自动创建第二次付费请求。`)
    } finally {
      setBusy('')
    }
  }

  async function beginExtraction() {
    if (busy || activeExtraction || intentBlocked) return
    const frozen = intent ?? freezeExtraction()
    if (!frozen) return
    if (!intent) {
      if (!chargeConfirmed) return
      const storageError = saveExtractionIntent(userId, serverBinding, versionId, frozen)
      if (storageError) {
        setError(storageError)
        return
      }
      setIntentState({ kind: 'ready', value: frozen })
    }
    await submitExtraction(frozen)
  }

  async function refreshAll() {
    await Promise.all([
      suggestions.refetch(),
      extractionId ? extraction.refetch() : Promise.resolve(),
      destinations.refetch(),
    ])
  }

  return <Card className={`space-y-4 p-5 ${className ?? ''}`}>
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="flex items-start gap-3">
        <span className="grid size-8 shrink-0 place-items-center rounded-full bg-primary/10 text-primary">
          <ListChecks className="size-4" aria-hidden />
        </span>
        <div>
          <h2 className="text-sm font-semibold text-text-1">从笔记提取任务建议</h2>
          <p className="mt-1 max-w-2xl text-xs leading-5 text-text-3">
            只从当前固定版本的正文提取；来源、内容摘要、文本模型和地址会在发送前冻结。建议不会自动变成任务。
          </p>
        </div>
      </div>
      <Button size="sm" variant="ghost" loading={suggestions.isFetching || extraction.isFetching}
        onClick={() => void refreshAll()}>
        <RefreshCw className="size-4" aria-hidden />刷新建议
      </Button>
    </div>

    <div className="rounded-ctl border border-info-border bg-info-soft px-3 py-2.5 text-xs leading-5 text-text-2">
      <div className="flex items-start gap-2">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-info" aria-hidden />
        <p>提取会调用管理员已启用的共享文本目标，可能产生 Token 费用。确认创建时会复用现有任务系统，同时校验日历、任务本和账号归属。</p>
      </div>
    </div>

    {(version.isError || destinations.isError || suggestions.isError) &&
      <InlineAlert tone="crit" title="任务建议数据未完整加载">
        {errorText(version.error ?? destinations.error ?? suggestions.error)}。已加载内容保持不变，不会自动新建请求。
      </InlineAlert>}
    {error && <InlineAlert tone="crit" title="任务建议操作未确认">{error}</InlineAlert>}
    {notice && <InlineAlert tone="ok" title="任务建议">{notice}</InlineAlert>}
    {intentBlocked && <InlineAlert tone="warn" title="提取入口已锁定">{intentBlocked}</InlineAlert>}

    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(260px,.7fr)]">
      <div className="space-y-2">
        <Label htmlFor="note-task-suggestion-destination">文本模型目标</Label>
        {destinations.isPending || version.isPending
          ? <Skeleton className="h-9" />
          : !sourceReady
            ? <InlineAlert tone="warn">当前材料版本没有可核对的派生正文摘要，暂时不能提取任务建议。</InlineAlert>
            : !textDestinations.length
              ? <InlineAlert tone="warn">暂无可用的共享文本模型目标。普通账号不能读取或配置管理员凭据。</InlineAlert>
              : <Select id="note-task-suggestion-destination" ariaLabel="文本模型目标"
                  value={destinationId} onValueChange={setDestinationId}
                  disabled={Boolean(intent) || Boolean(intentBlocked) || Boolean(activeExtraction)}
                  placeholder="选择本次提取目标"
                  options={textDestinations.map((item) => ({
                    value: destinationKey(item),
                    label: `${item.displayName} · ${item.model}`,
                  }))} className="w-full" />}
        {version.data && <p className="break-all font-mono text-[11px] leading-5 text-text-4">
          固定内容 SHA-256：{version.data.contentSha256}
        </p>}
      </div>
      <div className="space-y-2">
        <Label>发送范围</Label>
        <div className="flex items-start gap-2 rounded-ctl border border-border p-3">
          <Checkbox id="note-task-suggestion-charge" checked={chargeConfirmed}
            disabled={!selectedDestination || Boolean(activeExtraction) ||
              Boolean(intent) || Boolean(intentBlocked)}
            onCheckedChange={setChargeConfirmed} />
          <label htmlFor="note-task-suggestion-charge" className="text-xs leading-5 text-text-2">
            确认将固定版本完整正文发送到所选文本模型，并确认整批可能产生现金费用。
          </label>
        </div>
        <Button className="w-full" variant="primary"
          loading={busy === 'extract'}
          disabled={intent
            ? Boolean(activeExtraction) || Boolean(intentBlocked)
            : !canExtract}
          onClick={() => void beginExtraction()}>
          <ClipboardCheck className="size-4" aria-hidden />
          {intent ? '按原提取标识重试' : activeExtraction ? '提取处理中' : '确认并提取任务建议'}
        </Button>
      </div>
    </div>

    {extraction.data && <ExtractionState extraction={extraction.data} />}
    {extraction.isError && <InlineAlert tone="warn" title="提取状态读取失败">
      {errorText(extraction.error)}。现有建议仍可读取；不会自动重新发送模型请求。
    </InlineAlert>}

    <div className="space-y-3 border-t border-border pt-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-semibold text-text-1">本版本建议</h3>
          <p className="mt-1 text-xs text-text-3">共 {versionItems.length} 条，待处理 {draftCount} 条；已创建任务与放弃记录保留历史。</p>
        </div>
        <StatusBadge tone={draftCount ? 'warn' : 'neutral'}>{draftCount} 条待确认</StatusBadge>
      </div>
      {suggestions.isPending
        ? <Skeleton className="h-24" />
        : !versionItems.length
          ? <EmptyState size="sm" title="还没有任务建议" description="明确确认费用后提取；模型只会生成候选，不会直接写入任务。" />
          : <div className="space-y-3">{versionItems.map((suggestion) => (
              <SuggestionRow key={`${suggestion.id}/${suggestion.revision}`}
                suggestion={suggestion} userId={userId} serverBinding={serverBinding}
                onChanged={async (message, created) => {
                  setNotice(message)
                  if (created) onTaskCreated?.(created)
                  await suggestions.refetch()
                }} />
            ))}</div>}
    </div>
  </Card>
}

function ExtractionState({ extraction }: { extraction: NoteTaskSuggestionExtraction }) {
  return <div className="flex flex-wrap items-center justify-between gap-2 rounded-ctl border border-border bg-surface px-3 py-2.5 text-xs text-text-2">
    <span>
      <span className="font-medium">提取记录</span> · 尝试 {extraction.attemptCount} 次 ·
      固定提示 {extraction.promptVersion}
    </span>
    <span className="flex items-center gap-2">
      {extraction.errorCode && <span className="font-mono text-[11px] text-crit">{extraction.errorCode}</span>}
      <StatusBadge tone={extractionTone(extraction.status)}>{extractionNames[extraction.status] ?? extraction.status}</StatusBadge>
    </span>
    {extraction.errorMessage && <span className="w-full text-crit">{extraction.errorMessage}</span>}
  </div>
}

function SuggestionRow({
  suggestion,
  userId,
  serverBinding,
  onChanged,
}: {
  suggestion: NoteTaskSuggestion
  userId: string
  serverBinding: string
  onChanged: (message: string, created?: NoteTaskSuggestion) => Promise<void>
}) {
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState(suggestion.title)
  const [description, setDescription] = useState(suggestion.description ?? '')
  const [priority, setPriority] = useState(String(suggestion.priority))
  const [due, setDue] = useState(toLocalInputValue(suggestion.due))
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const confirmIntent = loadConfirmIntent(
    userId, serverBinding, suggestion.id, suggestion.revision)
  const frozenConfirm = confirmIntent.kind === 'ready' && confirmIntent.value.revision === suggestion.revision
    ? confirmIntent.value : null
  const confirmBlocked = confirmIntent.kind === 'blocked' ? confirmIntent.message : ''
  const confirmLocked = Boolean(frozenConfirm) || Boolean(confirmBlocked)
  const draft = suggestion.status === 'Draft'

  useEffect(() => {
    setTitle(suggestion.title)
    setDescription(suggestion.description ?? '')
    setPriority(String(suggestion.priority))
    setDue(toLocalInputValue(suggestion.due))
    setEditing(false)
    setError('')
  }, [suggestion.revision, suggestion.title, suggestion.description, suggestion.priority, suggestion.due])

  async function save() {
    if (!title.trim()) {
      setError('任务标题不能为空。')
      return
    }
    const body: UpdateNoteTaskSuggestion = {
      expectedRevision: suggestion.revision,
      title: title.trim(),
      description: description.trim() || null,
      priority: Number(priority),
      due: fromLocalInputValue(due),
      taskBookId: suggestion.taskBookId ?? null,
      calendarId: suggestion.calendarId ?? null,
    }
    setBusy('save')
    setError('')
    try {
      const updated = await noteTaskSuggestionsApi.update(suggestion.id, body)
      if (confirmIntent.kind === 'ready') {
        clearConfirmIntent(userId, serverBinding, suggestion.id, confirmIntent.value.requestId)
      }
      setEditing(false)
      await onChanged(`建议已按原修订保存；后续确认将使用新修订 ${updated.revision}。`)
    } catch (failure) {
      setError(`${errorText(failure)}。服务器记录未在本机改写，请刷新后重新核对修订。`)
    } finally {
      setBusy('')
    }
  }

  async function dismiss() {
    setBusy('dismiss')
    setError('')
    try {
      await noteTaskSuggestionsApi.dismiss(suggestion.id, { expectedRevision: suggestion.revision })
      await onChanged('建议已放弃；不会创建任务，历史记录保留。')
    } catch (failure) {
      setError(`${errorText(failure)}。页面不会把失败当成功；请刷新核对当前状态。`)
    } finally {
      setBusy('')
    }
  }

  async function confirm() {
    if (confirmIntent.kind === 'blocked') {
      setError(confirmIntent.message)
      return
    }
    let requestId = frozenConfirm?.requestId ?? ''
    if (!requestId) {
      requestId = createAiRequestId()
      const storageError = saveConfirmIntent(
        userId, serverBinding, suggestion.id, suggestion.revision, requestId)
      if (storageError) {
        setError(storageError)
        return
      }
    }
    setBusy('confirm')
    setError('')
    try {
      const confirmed = await noteTaskSuggestionsApi.confirm(suggestion.id, {
        requestId,
        expectedRevision: suggestion.revision,
      })
      clearConfirmIntent(userId, serverBinding, suggestion.id, requestId)
      await onChanged(
        confirmed.calendarId
          ? `已在既有任务系统创建任务 ${confirmed.taskId}，目标日历保持不变。`
          : `已在既有任务系统创建收件箱任务 ${confirmed.taskId}。`,
        confirmed)
    } catch (failure) {
      setError(`${errorText(failure)}。确认请求标识已冻结；重试只复用 ${requestId}，不会新建第二个任务。`)
    } finally {
      setBusy('')
    }
  }

  return <div className="rounded-ctl border border-border p-3">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0 flex-1">
        {editing
          ? <div className="space-y-3">
              <div>
                <Label htmlFor={`task-suggestion-title-${suggestion.id}`}>任务标题</Label>
                <Input id={`task-suggestion-title-${suggestion.id}`} value={title}
                  maxLength={255} onChange={(event) => setTitle(event.target.value)} />
              </div>
              <div>
                <Label htmlFor={`task-suggestion-description-${suggestion.id}`}>任务说明</Label>
                <Textarea id={`task-suggestion-description-${suggestion.id}`} value={description}
                  maxLength={20000} onChange={(event) => setDescription(event.target.value)} />
              </div>
              <div className="grid gap-3 sm:grid-cols-2">
                <div>
                  <Label htmlFor={`task-suggestion-priority-${suggestion.id}`}>优先级</Label>
                  <Select id={`task-suggestion-priority-${suggestion.id}`} ariaLabel="优先级"
                    value={priority} onValueChange={setPriority} options={priorityOptions} className="w-full" />
                </div>
                <div>
                  <Label htmlFor={`task-suggestion-due-${suggestion.id}`}>截止时间（可选）</Label>
                  <Input id={`task-suggestion-due-${suggestion.id}`} type="datetime-local"
                    value={due} onChange={(event) => setDue(event.target.value)} />
                </div>
              </div>
            </div>
          : <>
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium text-text-1">{suggestion.title}</span>
                <StatusBadge tone={statusTone(suggestion.status)}>{statusNames[suggestion.status] ?? suggestion.status}</StatusBadge>
                <StatusBadge tone="neutral" size="xs">优先级 {priorityLabel(suggestion.priority)}</StatusBadge>
                {suggestion.due && <span className="text-xs text-text-3">截止 {toLocalInputValue(suggestion.due).replace('T', ' ')}</span>}
              </div>
              {suggestion.description && <p className="mt-1 whitespace-pre-wrap text-xs leading-5 text-text-2">{suggestion.description}</p>}
              {suggestion.taskId && <p className="mt-1 break-all font-mono text-[11px] text-text-4">任务 ID：{suggestion.taskId}</p>}
              <p className="mt-1 text-[11px] text-text-4">
                来源摘要 {suggestion.sourceContentSha256.slice(0, 12)}… · 修订 {suggestion.revision.slice(0, 8)}…
              </p>
            </>}
      </div>
      {!editing && draft && <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={() => setEditing(true)} disabled={Boolean(busy) || confirmLocked}>编辑</Button>
        <TwoStepButton size="sm" variant="danger-soft" disabled={Boolean(busy) || confirmLocked}
          onConfirm={dismiss} armLabel="再次点击确认放弃">放弃</TwoStepButton>
        <TwoStepButton size="sm" variant="primary"
          disabled={Boolean(busy) || Boolean(confirmBlocked)}
          onConfirm={confirm} armLabel={frozenConfirm ? '按原请求再次确认' : '再次点击创建任务'}>
          {frozenConfirm ? '重试未确认的创建' : '确认创建任务'}
        </TwoStepButton>
      </div>}
    </div>
    {editing && <div className="mt-3 flex flex-wrap justify-end gap-2">
      <Button size="sm" variant="ghost" disabled={Boolean(busy)} onClick={() => setEditing(false)}>取消</Button>
      <Button size="sm" variant="primary" loading={busy === 'save'} onClick={() => void save()}>保存修订</Button>
    </div>}
    {confirmBlocked && <InlineAlert tone="warn" className="mt-3">{confirmBlocked}</InlineAlert>}
    {error && <InlineAlert tone="crit" className="mt-3">{error}</InlineAlert>}
  </div>
}

function priorityLabel(value: number) {
  return value === 1 ? '低' : value === 9 ? '高' : '中'
}
