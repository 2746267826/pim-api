import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Download, ImagePlus, RotateCcw, Save } from 'lucide-react'
import {
  Button,
  Card,
  ConfirmDialog,
  InlineAlert,
  Input,
  Label,
  PageHeader,
  Skeleton,
  StatusBadge,
} from '@/components/ui'
import {
  buildMaterialBundle,
  createMaterialId,
  hydrateParsedPhotos,
  parseMaterialVersion,
  type MaterialBundle,
  type MaterialDraftBlock,
  type NoteMaterialUpload,
  type NoteMaterialVersion,
  type ParsedNoteMaterial,
} from './material-model'
import type { NativeNotesClient } from './api'
import type { PromoteVersionOutcome } from './note-version-panel'
import { uploadMaterialBundle, type MaterialUploadProgress } from './upload-engine'
import { exportAndroidNotePackage } from './material-package'
import {
  ContinuousMaterialEditor,
  type ContinuousMaterialEditorHandle,
} from './continuous-material-editor'

export interface NoteEditorSeed {
  noteId: string
  title: string
  body: string
  blocks?: MaterialDraftBlock[]
  legacyNotice?: string
}

export type EndWithoutSaveReason = 'unchanged' | 'empty'

export interface NoteEditorPaneProps {
  ownerId: string
  serverBinding: string
  api: NativeNotesClient
  showVersionDetails: boolean
  versionId?: string
  seed?: NoteEditorSeed
  onDirtyChange?: (dirty: boolean) => void
  onSaved: (upload: NoteMaterialUpload, outcome: 'save' | 'end') => void
  onEnded: (reason: EndWithoutSaveReason) => void
  onBack: () => void
  promoteInFlight: boolean
  promotingVersionId: string
  onPromoteVersion: (versionId: string) => Promise<PromoteVersionOutcome>
}

interface LoadedEditorProps {
  noteId: string
  title: string
  blocks: MaterialDraftBlock[]
  revision: number
  noteCreatedAtMillis: number
  baseVersionId: string | null
  isCurrent: boolean
  preservedRecordings?: unknown[]
  preservedFiles?: ParsedNoteMaterial['manifestFiles']
  sourceInputJson?: string
  legacyNotice?: string
  serverBinding: string
  api: NativeNotesClient
  showVersionDetails: boolean
  onDirtyChange?: (dirty: boolean) => void
  onSaved: (upload: NoteMaterialUpload, outcome: 'save' | 'end') => void
  onEnded: (reason: EndWithoutSaveReason) => void
  onBack: () => void
  promoteInFlight: boolean
  promotingVersionId: string
  onPromoteVersion: (versionId: string) => Promise<PromoteVersionOutcome>
}

interface AcceptedEditorLoad {
  versionId: string
  material: NoteMaterialVersion
  hydrated: {
    parsed: ParsedNoteMaterial
    blocks: MaterialDraftBlock[]
  }
}

export function NoteEditorPane({
  ownerId,
  serverBinding,
  api,
  showVersionDetails,
  versionId,
  seed,
  onDirtyChange,
  onSaved,
  onEnded,
  onBack,
  promoteInFlight,
  promotingVersionId,
  onPromoteVersion,
}: NoteEditorPaneProps) {
  const material = useQuery({
    queryKey: ['native-note-material', ownerId, serverBinding, versionId],
    queryFn: ({ signal }) => api.version(versionId!, signal),
    enabled: Boolean(versionId),
  })
  const hydrated = useQuery({
    queryKey: ['native-note-material-hydrated', ownerId, serverBinding, versionId],
    queryFn: async ({ signal }) => {
      const loaded = material.data
      if (!loaded) throw new Error('笔记尚未载入')
      const parsed = parseMaterialVersion(loaded)
      const blocks = await hydrateParsedPhotos(
        parsed,
        loaded,
        (id, fileIndex) => api.fileBytes(id, fileIndex, signal),
      )
      return { parsed, blocks }
    },
    enabled: Boolean(material.data),
  })
  const dirtyRef = useRef(false)
  const [acceptedLoad, setAcceptedLoad] = useState<AcceptedEditorLoad | null>(null)

  useEffect(() => {
    if (!versionId || !material.data || !hydrated.data) return
    setAcceptedLoad((current) => {
      if (current?.versionId === versionId && dirtyRef.current) return current
      if (current?.versionId === versionId &&
          current.material === material.data && current.hydrated === hydrated.data) {
        return current
      }
      return { versionId, material: material.data, hydrated: hydrated.data }
    })
  }, [hydrated.data, material.data, versionId])

  const updateDirty = useCallback((dirty: boolean) => {
    dirtyRef.current = dirty
    onDirtyChange?.(dirty)
  }, [onDirtyChange])

  if (!versionId) {
    if (!seed) return <EmptyEditor />
    return <LoadedEditor
      key={`seed/${seed.noteId}`}
      noteId={seed.noteId}
      title={seed.title}
      blocks={seed.blocks ?? [{ id: createMaterialId(), kind: 'markdown', text: seed.body }]}
      revision={0}
      noteCreatedAtMillis={Date.now()}
      baseVersionId={null}
      isCurrent
      legacyNotice={seed.legacyNotice}
      serverBinding={serverBinding}
      api={api}
      showVersionDetails={showVersionDetails}
      onDirtyChange={updateDirty}
      onSaved={onSaved}
      onEnded={onEnded}
      onBack={onBack}
      promoteInFlight={promoteInFlight}
      promotingVersionId={promotingVersionId}
      onPromoteVersion={onPromoteVersion}
    />
  }

  const acceptedForVersion = acceptedLoad?.versionId === versionId ? acceptedLoad : null
  if (!acceptedForVersion) {
    if (material.isError) {
      return <LoadFailureCard
        title="笔记读取失败"
        detail={errorText(material.error)}
        retrying={material.isFetching}
        onRetry={() => void material.refetch()}
      />
    }
    if (hydrated.isError) {
      return <LoadFailureCard
        title={showVersionDetails ? '材料原字节核对失败' : '笔记内容读取失败'}
        detail={errorText(hydrated.error)}
        retrying={hydrated.isFetching}
        onRetry={() => void hydrated.refetch()}
      />
    }
    return <Card className="space-y-3 p-5"><Skeleton className="h-9 w-2/3" /><Skeleton className="h-56" /><p className="text-xs text-text-3">{showVersionDetails ? '正在核对固定材料、正文块和图片原字节…' : '正在载入笔记内容…'}</p></Card>
  }

  const parsed = acceptedForVersion.hydrated.parsed
  const refreshFailures = [
    material.isError ? `版本：${errorText(material.error)}` : '',
    hydrated.isError ? `正文材料：${errorText(hydrated.error)}` : '',
  ].filter(Boolean)

  async function retryRefresh() {
    if (material.isError) {
      const result = await material.refetch()
      if (result.isError) return
    }
    if (hydrated.isError) await hydrated.refetch()
  }

  return <>
    {refreshFailures.length > 0 && <InlineAlert className="mb-3" tone="crit" title="笔记刷新失败，当前草稿已保留">
      <p>{refreshFailures.join('；')}。正文、材料节点和未确认提交仍保留在本页，没有被替换。</p>
      <Button
        className="mt-2"
        size="sm"
        variant="secondary"
        loading={material.isFetching || hydrated.isFetching}
        onClick={() => void retryRefresh()}
      >
        就地重试
      </Button>
    </InlineAlert>}
    <LoadedEditor
      key={`${parsed.versionId}/${parsed.revision}`}
      noteId={parsed.noteId}
      title={parsed.title}
      blocks={acceptedForVersion.hydrated.blocks}
      revision={parsed.revision}
      noteCreatedAtMillis={parsed.createdAtMillis}
      baseVersionId={parsed.versionId}
      isCurrent={acceptedForVersion.material.upload.isCurrent}
      preservedRecordings={parsed.recordingEntries}
      preservedFiles={parsed.manifestFiles}
      sourceInputJson={parsed.sourceInputJson}
      serverBinding={serverBinding}
      api={api}
      showVersionDetails={showVersionDetails}
      onDirtyChange={updateDirty}
      onSaved={onSaved}
      onEnded={onEnded}
      onBack={onBack}
      promoteInFlight={promoteInFlight}
      promotingVersionId={promotingVersionId}
      onPromoteVersion={onPromoteVersion}
    />
  </>
}

function EmptyEditor() {
  return <Card className="min-h-[30rem] p-8">
    <div className="mx-auto flex max-w-sm flex-col items-center justify-center py-20 text-center">
      <span className="grid size-14 place-items-center rounded-full bg-surface-2 text-primary"><ImagePlus className="size-6" aria-hidden /></span>
      <h2 className="mt-4 text-lg font-semibold text-text-1">选一篇笔记或新建笔记</h2>
      <p className="mt-2 text-sm leading-6 text-text-3">写好标题和内容后保存。保存遇到问题时，当前页面中的内容会保留，可重试。</p>
    </div>
  </Card>
}

function LoadFailureCard({
  title,
  detail,
  retrying,
  onRetry,
}: {
  title: string
  detail: string
  retrying: boolean
  onRetry: () => void
}) {
  return <Card className="p-5">
    <InlineAlert tone="crit" title={title}>{detail}</InlineAlert>
    <div className="mt-3 flex items-center gap-2">
      <Button size="sm" variant="secondary" loading={retrying} onClick={onRetry}>就地重试</Button>
      <span className="text-xs text-text-3">当前编辑上下文仍保留；重试只重新读取这一项。</span>
    </div>
  </Card>
}

function LoadedEditor({
  noteId,
  title: initialTitle,
  blocks: initialBlocks,
  revision,
  noteCreatedAtMillis,
  baseVersionId,
  isCurrent,
  preservedRecordings = [],
  preservedFiles = [],
  sourceInputJson,
  legacyNotice,
  serverBinding,
  api,
  showVersionDetails,
  onDirtyChange,
  onSaved,
  onEnded,
  onBack,
  promoteInFlight,
  promotingVersionId,
  onPromoteVersion,
}: LoadedEditorProps) {
  const [title, setTitle] = useState(initialTitle)
  const [versionId, setVersionId] = useState(createMaterialId)
  const [frozen, setFrozen] = useState<MaterialBundle | null>(null)
  const [progress, setProgress] = useState<MaterialUploadProgress | null>(null)
  const [busy, setBusy] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [storageConsent, setStorageConsent] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [inkEditorOpen, setInkEditorOpen] = useState(false)
  const [inkPending, setInkPending] = useState(false)
  const [hasEditorContent, setHasEditorContent] = useState(initialBlocks.length > 0)
  const [endConfirmOpen, setEndConfirmOpen] = useState(false)
  const [stickyHeaderHeight, setStickyHeaderHeight] = useState(0)
  const stickyHeaderRef = useRef<HTMLDivElement>(null)
  const continuousEditorRef = useRef<ContinuousMaterialEditorHandle>(null)
  const uploadSessionRef = useRef<EditorUploadSession | null>(null)
  const editable = !busy && !exporting && !frozen && isCurrent
  const editableRef = useRef(editable)
  useEffect(() => {
    editableRef.current = editable
  }, [editable])
  const canRetryFrozen = Boolean(frozen) && isCurrent && !busy && !inkEditorOpen && !inkPending
  const canSubmit = hasEditorContent && storageConsent && !inkEditorOpen && !inkPending &&
    (editable || canRetryFrozen)

  useLayoutEffect(() => {
    const node = stickyHeaderRef.current
    if (!node) return
    const measure = () => {
      const height = Math.ceil(node.getBoundingClientRect().height)
      if (height <= 0) return
      setStickyHeaderHeight((current) => current === height ? current : height)
    }
    measure()
    const observer = typeof ResizeObserver === 'undefined'
      ? null
      : new ResizeObserver(measure)
    observer?.observe(node)
    window.addEventListener('resize', measure)
    return () => {
      observer?.disconnect()
      window.removeEventListener('resize', measure)
    }
  }, [])

  const stickyVariables = {
    '--note-sticky-top': '0px',
    '--note-sticky-header-height': `${stickyHeaderHeight || 72}px`,
  } as CSSProperties

  const requireUploadSession = useCallback(() => {
    const session = uploadSessionRef.current
    if (!session || session.noteId !== noteId || session.baseVersionId !== baseVersionId ||
        session.controller.signal.aborted) {
      const failure = new Error('当前笔记编辑会话已结束，已停止旧材料上传')
      failure.name = 'AbortError'
      throw failure
    }
    return session
  }, [baseVersionId, noteId])

  const assertUploadSessionActive = useCallback((session: EditorUploadSession) => {
    if (uploadSessionRef.current !== session || session.controller.signal.aborted) {
      const failure = new Error('当前笔记编辑会话已结束，已停止旧材料上传')
      failure.name = 'AbortError'
      throw failure
    }
  }, [])

  const isUploadSessionActive = (session: EditorUploadSession) =>
    uploadSessionRef.current === session && !session.controller.signal.aborted

  useEffect(() => {
    const controller = new AbortController()
    uploadSessionRef.current = { noteId, baseVersionId, controller }
    return () => {
      if (uploadSessionRef.current?.controller === controller) uploadSessionRef.current = null
      controller.abort()
    }
  }, [baseVersionId, noteId])

  useEffect(() => {
    onDirtyChange?.(dirty || inkPending)
  }, [dirty, inkPending, onDirtyChange])

  const lockedReason = !isCurrent
    ? '这是一份历史版本，仅供查看。请从当前版本继续编辑。'
    : frozen
      ? '保存尚未确认完成。重试会继续核对同一次保存，不会另建一份。'
      : ''

  async function save(outcome: 'save' | 'end' = 'save'): Promise<boolean> {
    if (busy || inkEditorOpen || inkPending || !isCurrent) return false
    if (!title.trim()) {
      setError('请先填写笔记标题，再保存或结束这次记录；当前草稿仍保留在本页。')
      return false
    }
    if (!storageConsent) {
      setError('请先在页内勾选“确认保存到当前服务器”，再保存或结束本次记录。')
      return false
    }
    let blocks: MaterialDraftBlock[]
    try {
      blocks = continuousEditorRef.current?.getBlocks() ?? []
    } catch (failure) {
      setError(`${errorText(failure)}。当前草稿仍保留在本页，未提交任何内容。`)
      return false
    }
    if (!blocks.length) {
      setError('正文还没有可保存的内容。')
      return false
    }
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return false
    }
    setBusy(true)
    setError('')
    setMessage('')
    try {
      let batch = frozen
      if (!batch) {
        batch = await buildMaterialBundle({
          versionId,
          noteId,
          baseVersionId,
          revision: revision + 1,
          title,
          noteCreatedAtMillis,
          createdAtMillis: Date.now(),
          blocks,
          recordings: preservedRecordings,
          preservedRecordings,
          preservedFiles,
          preserveSourceFiles: Boolean(baseVersionId),
          sourceInputJson,
        })
        if (!isUploadSessionActive(session)) return false
        setFrozen(batch)
        setDirty(true)
      }
      const upload = await uploadMaterialBundle(
        batch,
        api,
        {
          storageConsent,
          signal: session.controller.signal,
          assertActive: () => assertUploadSessionActive(session),
          cloneSourceVersionId: baseVersionId ?? undefined,
          expectedCurrentVersionId: baseVersionId,
        },
        setProgress,
      )
      assertUploadSessionActive(session)
      setMessage(upload.hasConflict
        ? '内容已完整保存，但另一端也保存了修改；两份内容均已保留，请选择采用哪一份。'
        : '正文已同步。')
      setDirty(false)
      onSaved(upload, outcome)
      return true
    } catch (failure) {
      if (!isUploadSessionActive(session)) return false
      setError(`${errorText(failure)}。本次提交状态已保留，可在本页重试。`)
      return false
    } finally {
      if (isUploadSessionActive(session)) setBusy(false)
    }
  }

  async function finishSession(): Promise<boolean> {
    if (busy || inkEditorOpen || inkPending || !isCurrent) return false
    if (!baseVersionId && !hasEditorContent && !frozen) {
      onEnded('empty')
      return true
    }
    if (!dirty && baseVersionId && !frozen) {
      onEnded('unchanged')
      return true
    }
    if (!title.trim()) {
      setError('请先填写笔记标题，再保存或结束这次记录；当前草稿仍保留在本页。')
      return false
    }
    if (!storageConsent) {
      setError('结束前需要先确认保存到当前服务器；当前正文仍保留在本页，未提交任何内容。')
      return false
    }
    return save('end')
  }

  async function exportPackage() {
    if (busy || exporting || inkEditorOpen || inkPending) return
    let blocks: MaterialDraftBlock[]
    try {
      blocks = continuousEditorRef.current?.getBlocks() ?? []
    } catch (failure) {
      setError(`${errorText(failure)}。当前草稿仍保留在本页，未导出任何内容。`)
      return
    }
    if (!blocks.length) {
      setError('正文还没有可导出的内容。')
      return
    }
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return
    }
    setExporting(true)
    setError('')
    try {
      const bundle = await buildMaterialBundle({
        versionId,
        noteId,
        baseVersionId,
        revision: revision + (dirty || !baseVersionId ? 1 : 0),
        title,
        noteCreatedAtMillis,
        createdAtMillis: Date.now(),
        blocks,
        recordings: preservedRecordings,
        preservedRecordings,
        preservedFiles,
        preserveSourceFiles: Boolean(baseVersionId),
        sourceInputJson,
      })
      assertUploadSessionActive(session)
      const archive = await exportAndroidNotePackage(bundle)
      assertUploadSessionActive(session)
      const url = URL.createObjectURL(new Blob([archive as BlobPart], { type: 'application/zip' }))
      const link = document.createElement('a')
      link.href = url
      link.download = `${safePackageName(title)}.zip`
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1_000)
      setMessage('已导出 Android schema-v1 笔记包，包含正文、原图、可编辑笔迹与 PNG 预览；不含录音。')
    } catch (failure) {
      if (isUploadSessionActive(session)) setError(`${errorText(failure)}。未导出不完整笔记包。`)
    } finally {
      if (isUploadSessionActive(session)) setExporting(false)
    }
  }

  async function downloadOriginalBundle() {
    if (!baseVersionId || downloading) return
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return
    }
    setDownloading(true)
    setError('')
    try {
      const download = await api.downloadBundle(baseVersionId, session.controller.signal)
      assertUploadSessionActive(session)
      const url = URL.createObjectURL(download.blob)
      try {
        assertUploadSessionActive(session)
        const link = document.createElement('a')
        link.href = url
        link.download = download.filename
        link.click()
      } finally {
        window.setTimeout(() => URL.revokeObjectURL(url), 1_000)
      }
      setMessage('已下载该固定版本的完整原始材料包。下载不修改材料，也不开放录音编辑。')
    } catch (failure) {
      if (isUploadSessionActive(session)) {
        setError(`${errorText(failure)}。未创建下载；本机草稿保持不变。`)
      }
    } finally {
      if (isUploadSessionActive(session)) setDownloading(false)
    }
  }

  async function promote() {
    if (!baseVersionId || busy || promoteInFlight) return
    setError('')
    setMessage('')
    try {
      const outcome = await onPromoteVersion(baseVersionId)
      if (outcome.status === 'adopted') {
        setMessage(outcome.message)
      } else {
        setError(outcome.message)
      }
    } catch (failure) {
      setError(`${errorText(failure)}。未确认本次选择是否成功，请刷新核对。`)
    }
  }

  function createBlankVersion() {
    if (busy) return
    setFrozen(null)
    setProgress(null)
    setVersionId(createMaterialId())
    setError('')
    setMessage('已放弃本页的提交尝试；部分未完成上传会由服务端按策略过期清理。再次保存会重新提交。')
  }

  const endsEmptyNewNote = !baseVersionId && !hasEditorContent && !frozen
  const endsSavedWithoutChanges = Boolean(baseVersionId) && !dirty && !frozen
  const endNeedsSave = !endsEmptyNewNote && !endsSavedWithoutChanges
  const endDescription = endsEmptyNewNote
    ? '当前没有可保存的正文，不会在服务器创建新笔记；标题和本页内容不会上传。'
    : endsSavedWithoutChanges
      ? '服务器上的当前版本会保持不变；结束不会创建新版本。'
      : '会先保存当前标题与连续正文，再结束本次编辑。保存失败时留在本页，当前草稿不会丢失。'

  return <>
  <Card className="relative overflow-visible" style={stickyVariables}>
    <div
      ref={stickyHeaderRef}
      data-testid="note-editor-sticky-header"
      className="sticky z-30 rounded-t-card border-b border-divider bg-bg/95 px-4 py-3 shadow-card backdrop-blur"
      style={{ top: 'var(--note-sticky-top, 0px)' }}
    >
      <PageHeader className="mb-0" actionsClassName="ml-auto justify-end" title={baseVersionId ? '继续写这篇笔记' : '新建笔记'} subtitle={baseVersionId ? '标题、正文、照片与手写共用唯一连续编辑上下文。' : '先写下内容，再决定是否保存到当前服务器。'} actions={
        <div className="flex flex-wrap items-center justify-end gap-2">
          <Button className="xl:hidden" size="sm" variant="ghost" aria-label="返回笔记列表" onClick={onBack}>
            <ArrowLeft className="size-4" aria-hidden />
          </Button>
          {dirty && <StatusBadge tone="warn" dot={false}>有未保存更改</StatusBadge>}
          <Button size="sm" variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void save('save')}>
            <Save className="size-4" aria-hidden />{busy ? '正在保存…' : frozen ? '重试同一提交' : '保存笔记'}
          </Button>
          <Button
            className="shadow-card"
            size="sm"
            variant="secondary"
            disabled={!isCurrent || busy || inkEditorOpen || inkPending}
            onClick={() => {
              setError('')
              setEndConfirmOpen(true)
            }}
          >
            结束
          </Button>
        </div>
      } />
    </div>

    <div className="mx-auto max-w-[76rem] space-y-4 p-4 sm:p-6">
      {legacyNotice && <InlineAlert tone="warn" title="旧闪念兼容副本">{legacyNotice}</InlineAlert>}
      {lockedReason && <InlineAlert tone={!isCurrent ? 'warn' : 'info'}>{lockedReason}</InlineAlert>}
      {showVersionDetails && (
        <details className="rounded-ctl border border-border bg-surface px-3 py-2 text-xs text-text-2">
          <summary className="cursor-pointer font-medium">本次保存回执 · 修订 {revision}</summary>
          <section className="mt-3" aria-labelledby="note-save-identity">
            <div className="flex flex-wrap items-center gap-2">
              <h2 id="note-save-identity" className="text-xs font-semibold text-text-1">本次保存回执</h2>
              <StatusBadge tone={isCurrent ? 'ok' : 'warn'} dot={false}>{isCurrent ? '可编辑' : '只读历史版本'}</StatusBadge>
            </div>
            <dl className="mt-2 grid gap-x-4 gap-y-1 text-[11px] leading-5 sm:grid-cols-[8rem_minmax(0,1fr)]">
              <dt className="text-text-4">笔记 ID</dt>
              <dd className="mono break-all text-text-2">{noteId}</dd>
              <dt className="text-text-4">当前载入版本 ID</dt>
              <dd className="mono break-all text-text-2">{baseVersionId ?? '尚未保存'}</dd>
              <dt className="text-text-4">本次保存编号</dt>
              <dd className="mono break-all text-text-2">{versionId}</dd>
              <dt className="text-text-4">本次保存来源版本</dt>
              <dd className="mono break-all text-text-2">{baseVersionId ?? '新建笔记'}</dd>
            </dl>
            <p className="mt-2 text-[11px] leading-5 text-text-3">
              保存编号在提交后由服务器固定为版本 ID；历史版本只读，不能覆盖当前版本。
            </p>
          </section>
        </details>
      )}
      <div>
        <Label htmlFor="native-note-title">笔记标题</Label>
        <Input id="native-note-title" aria-label="笔记标题" className="mt-1" maxLength={500} value={title}
          disabled={!editable} onChange={(event) => {
            setTitle(event.target.value)
            setDirty(true)
          }} />
      </div>

      <ContinuousMaterialEditor
        ref={continuousEditorRef}
        initialBlocks={initialBlocks}
        editable={editable}
        showVersionDetails={showVersionDetails}
        isSessionActive={() => {
          const session = uploadSessionRef.current
          return editableRef.current && session !== null && !session.controller.signal.aborted &&
            session.noteId === noteId && session.baseVersionId === baseVersionId
        }}
        onDirty={() => setDirty(true)}
        onContentStateChange={setHasEditorContent}
        onInkEditorChange={setInkEditorOpen}
        onInkPendingChange={setInkPending}
        onError={setError}
      />
      {baseVersionId && <InlineAlert tone="info">已有录音、照片和手写材料会完整保留。正文中的照片和手写可继续在原位置前后编辑；历史版本始终只读。</InlineAlert>}

      <section className="space-y-3 rounded-card border border-border bg-surface p-4" aria-labelledby="note-save-heading">
        <div>
          <h2 id="note-save-heading" className="text-sm font-semibold text-text-1">保存与同步</h2>
          <p className="mt-1 text-xs leading-5 text-text-3">草稿仅保存在当前页面内存中，离开或刷新前请保存到服务器。</p>
        </div>
        <label className="flex items-start gap-2 rounded-ctl border border-border bg-bg p-3 text-xs leading-5 text-text-2">
          <input
            type="checkbox"
            className="mt-0.5 size-4 shrink-0"
            aria-label="确认保存到当前服务器"
            checked={storageConsent}
            disabled={!editable || !isCurrent}
            onChange={(event) => setStorageConsent(event.target.checked)}
          />
          <span>
            <span className="block font-medium text-text-1">确认保存到当前服务器</span>
            我确认将本次笔记材料保存到 {serverBinding}。当前账号及该服务器管理员可读取。
          </span>
        </label>
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm" variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void save('save')}>
            <Save className="size-4" aria-hidden />{busy ? '正在保存…' : frozen ? '重试同一提交' : '保存笔记'}
          </Button>
          {dirty && <StatusBadge tone="warn" dot={false}>有未保存更改</StatusBadge>}
          {!dirty && baseVersionId && (
            <StatusBadge tone="ok" dot={false}>{showVersionDetails ? '已从服务器载入' : '无未保存更改'}</StatusBadge>
          )}
          {!dirty && !baseVersionId && <StatusBadge tone="neutral" dot={false}>尚未保存</StatusBadge>}
        </div>
        {progress && <div className="rounded-ctl border border-info-border bg-info-soft p-3 text-xs text-text-2">
          <p className="font-medium text-text-1">正在同步笔记</p>
          <p className="mt-1 break-all">
            {progress.completedParts} / {progress.totalParts}
            {showVersionDetails ? ` · ${progress.currentPath}` : ''}
          </p>
        </div>}
        {error && !endConfirmOpen && <InlineAlert tone="crit" title="未完成">{error}</InlineAlert>}
        {message && <InlineAlert tone={message.includes('选择采用哪一份') ? 'warn' : 'ok'}>{message}</InlineAlert>}
      </section>

      <details className="rounded-ctl border border-border px-3 py-2 text-xs leading-5 text-text-3">
        <summary className="cursor-pointer">{showVersionDetails ? '更多：历史材料、下载、导出与诊断' : '更多：下载、导出与诊断'}</summary>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {showVersionDetails && baseVersionId && !isCurrent && (
            <Button
              size="sm"
              loading={promoteInFlight && promotingVersionId === baseVersionId}
              disabled={promoteInFlight}
              onClick={() => void promote()}
            >
              明确设为当前版本
            </Button>
          )}
          {showVersionDetails && baseVersionId && <Button size="sm" variant="secondary" disabled={downloading} loading={downloading} onClick={() => void downloadOriginalBundle()}>
            <Download className="size-4" aria-hidden />下载完整原始材料包
          </Button>}
          <Button size="sm" variant="ghost" disabled={busy || exporting || inkEditorOpen || inkPending} loading={exporting} onClick={() => void exportPackage()}>
            <Download className="size-4" aria-hidden />导出 Android 笔记包
          </Button>
          {frozen && <Button size="sm" variant="ghost" disabled={busy} onClick={createBlankVersion}><RotateCcw className="size-4" aria-hidden />重新开始保存</Button>}
        </div>
        {showVersionDetails && baseVersionId && <p className="mt-2">原始材料包下载上限为 128 MiB。</p>}
        <p className="mt-2">正文、照片、手写和已有录音材料保持在同一顺序。</p>
      </details>
    </div>
  </Card>
  <ConfirmDialog
    open={endConfirmOpen}
    onOpenChange={setEndConfirmOpen}
    title="结束这次记录？"
    description={endDescription}
    feedback={error ? <InlineAlert tone="crit" title="结束未完成">{error}</InlineAlert> : undefined}
    confirmLabel={endNeedsSave ? '结束并保存' : '结束'}
    cancelLabel="继续编辑"
    loading={busy}
    onConfirm={finishSession}
  />
  </>
}

function safePackageName(value: string) {
  const normalized = value.trim().replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').replace(/[. ]+$/g, '').slice(0, 80)
  return normalized || 'PIM-note'
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}

interface EditorUploadSession {
  noteId: string
  baseVersionId: string | null
  controller: AbortController
}
