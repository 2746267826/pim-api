import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Camera, Download, ImagePlus, PenLine, Plus, RotateCcw, Save, Trash2 } from 'lucide-react'
import { Button, Card, InlineAlert, Input, Label, PageHeader, Skeleton, StatusBadge, Textarea } from '@/components/ui'
import {
  buildMaterialBundle,
  createMaterialId,
  hydrateParsedPhotos,
  parseMaterialVersion,
  type MaterialBundle,
  type MaterialDraftBlock,
  type NoteMaterialUpload,
  type ParsedNoteMaterial,
  type SupportedPhotoMime,
} from './material-model'
import type { NativeNotesClient, NoteLibrarySource } from './api'
import { uploadMaterialBundle, type MaterialUploadProgress } from './upload-engine'
import { InkCanvasEditor } from './ink-canvas-editor'
import { exportAndroidNotePackage } from './material-package'

export interface NoteEditorSeed {
  noteId: string
  title: string
  body: string
  blocks?: MaterialDraftBlock[]
  legacyNotice?: string
}

export interface NoteEditorPaneProps {
  ownerId: string
  serverBinding: string
  api: NativeNotesClient
  versionId?: string
  seed?: NoteEditorSeed
  onDirtyChange?: (dirty: boolean) => void
  onSaved: (upload: NoteMaterialUpload) => void
}

interface LoadedEditorProps {
  noteId: string
  title: string
  blocks: MaterialDraftBlock[]
  revision: number
  noteCreatedAtMillis: number
  baseVersionId: string | null
  recordings: number
  isCurrent: boolean
  currentVersionId: string | null
  preservedRecordings?: unknown[]
  preservedFiles?: ParsedNoteMaterial['manifestFiles']
  sourceInputJson?: string
  legacyNotice?: string
  serverBinding: string
  api: NativeNotesClient
  onDirtyChange?: (dirty: boolean) => void
  onSaved: (upload: NoteMaterialUpload) => void
}

export function NoteEditorPane({
  ownerId,
  serverBinding,
  api,
  versionId,
  seed,
  onDirtyChange,
  onSaved,
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
      recordings={0}
      isCurrent
      currentVersionId={null}
      legacyNotice={seed.legacyNotice}
      serverBinding={serverBinding}
      api={api}
      onDirtyChange={onDirtyChange}
      onSaved={onSaved}
    />
  }

  if (material.isPending || hydrated.isPending) {
    return <Card className="space-y-3 p-5"><Skeleton className="h-9 w-2/3" /><Skeleton className="h-56" /><p className="text-xs text-text-3">正在核对固定材料、正文块和图片原字节…</p></Card>
  }
  if (material.isError) {
    return <Card className="p-5"><InlineAlert tone="crit" title="笔记读取失败">{errorText(material.error)}</InlineAlert></Card>
  }
  if (hydrated.isError || !hydrated.data) {
    return <Card className="p-5"><InlineAlert tone="crit" title="材料原字节核对失败">{errorText(hydrated.error)}</InlineAlert></Card>
  }

  const parsed = hydrated.data.parsed
  return <LoadedEditor
    key={`${parsed.versionId}/${parsed.revision}`}
    noteId={parsed.noteId}
    title={parsed.title}
    blocks={hydrated.data.blocks}
    revision={parsed.revision}
    noteCreatedAtMillis={parsed.createdAtMillis}
    baseVersionId={parsed.versionId}
    recordings={parsed.recordings}
    isCurrent={material.data.upload.isCurrent}
    currentVersionId={material.data.upload.currentVersionId ?? null}
    preservedRecordings={parsed.recordingEntries}
    preservedFiles={parsed.manifestFiles}
    sourceInputJson={parsed.sourceInputJson}
    serverBinding={serverBinding}
    api={api}
    onDirtyChange={onDirtyChange}
    onSaved={onSaved}
  />
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

function LoadedEditor({
  noteId,
  title: initialTitle,
  blocks: initialBlocks,
  revision,
  noteCreatedAtMillis,
  baseVersionId,
  recordings,
  isCurrent,
  currentVersionId,
  preservedRecordings = [],
  preservedFiles = [],
  sourceInputJson,
  legacyNotice,
  serverBinding,
  api,
  onDirtyChange,
  onSaved,
  onProcessing,
}: LoadedEditorProps) {
  const [title, setTitle] = useState(initialTitle)
  const [blocks, setBlocks] = useState<MaterialDraftBlock[]>(initialBlocks)
  const [versionId, setVersionId] = useState(createMaterialId)
  const [frozen, setFrozen] = useState<MaterialBundle | null>(null)
  const [progress, setProgress] = useState<MaterialUploadProgress | null>(null)
  const [busy, setBusy] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [downloading, setDownloading] = useState(false)
  const [promoting, setPromoting] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const [storageConsent, setStorageConsent] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [inkPending, setInkPending] = useState(false)
  const [inkEditor, setInkEditor] = useState<{ id: string; ink: Record<string, unknown>; sessionId: string } | null>(null)
  const inkEditorRef = useRef<{ id: string; ink: Record<string, unknown>; sessionId: string } | null>(null)
  const inkSessionIdRef = useRef<string | null>(null)
  const uploadSessionRef = useRef<EditorUploadSession | null>(null)
  const editable = !busy && !exporting && !promoting && !frozen && isCurrent
  const editableRef = useRef(editable)
  useEffect(() => {
    editableRef.current = editable
  }, [editable])
  const canSubmit = editable && blocks.length > 0 && storageConsent && !inkEditor && !inkPending

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

  function isInkSessionActive(sessionId: string) {
    const session = uploadSessionRef.current
    return inkSessionIdRef.current === sessionId && inkEditorRef.current?.sessionId === sessionId &&
      editableRef.current && session !== null && !session.controller.signal.aborted &&
      session.noteId === noteId && session.baseVersionId === baseVersionId
  }

  function openInkEditor(id: string, ink: Record<string, unknown>) {
    const sessionId = createMaterialId()
    inkSessionIdRef.current = sessionId
    const next = { id, ink, sessionId }
    inkEditorRef.current = next
    setInkEditor(next)
    setInkPending(false)
  }

  function closeInkEditor(sessionId: string) {
    if (inkSessionIdRef.current !== sessionId) return
    inkSessionIdRef.current = null
    inkEditorRef.current = null
    setInkEditor(null)
    setInkPending(false)
  }

  useEffect(() => {
    const controller = new AbortController()
    uploadSessionRef.current = { noteId, baseVersionId, controller }
    return () => {
      inkSessionIdRef.current = null
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

  async function save() {
    if (busy || inkEditor || inkPending || !isCurrent || !blocks.length || !storageConsent) return
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return
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
        if (!isUploadSessionActive(session)) return
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
        ? '新版本已完整保存，但另一端同时更新了当前版本；两份材料均保留，请明确选择当前版本。'
        : '新版本已完整保存，并成为当前材料版本。')
      setDirty(false)
      onSaved(upload)
    } catch (failure) {
      if (!isUploadSessionActive(session)) return
      setError(`${errorText(failure)}。材料和版本标识已保留，可在本页重试。`)
    } finally {
      if (isUploadSessionActive(session)) setBusy(false)
    }
  }

  async function exportPackage() {
    if (busy || exporting || inkEditor || inkPending) return
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
    if (!baseVersionId || busy || promoting) return
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return
    }
    setPromoting(true)
    setError('')
    try {
      const upload = await api.promote(baseVersionId, currentVersionId, session.controller.signal)
      assertUploadSessionActive(session)
      setMessage('已明确将此版本设为当前版本。')
      onSaved(upload)
    } catch (failure) {
      if (!isUploadSessionActive(session)) return
      setError(`${errorText(failure)}。当前选用版本没有改变。`)
    } finally {
      if (isUploadSessionActive(session)) setPromoting(false)
    }
  }

  async function addPhotos(files: FileList | null) {
    if (!files?.length || !editable) return
    let session: EditorUploadSession
    try {
      session = requireUploadSession()
    } catch {
      return
    }
    setError('')
    const accepted: MaterialDraftBlock[] = []
    for (const file of [...files]) {
      if (file.type !== 'image/jpeg' && file.type !== 'image/png' && file.type !== 'image/webp') {
        setError('Web 材料当前只接受 JPEG/PNG/WebP 照片。')
        continue
      }
      if (file.size > 32 * 1024 * 1024) {
        setError(`照片 ${file.name} 超过 32 MiB，未加入材料。`)
        continue
      }
      accepted.push({
        kind: 'photo',
        id: createMaterialId(),
        assetId: createMaterialId(),
        mimeType: file.type as SupportedPhotoMime,
        bytes: new Uint8Array(await file.arrayBuffer()),
      })
    }
    if (!isUploadSessionActive(session)) return
    if (accepted.length) {
      setBlocks((current) => [...current, ...accepted])
      setDirty(true)
    }
  }

  function editMarkdown(id: string, text: string) {
    if (!editable) return
    setBlocks((current) => current.map((block) => block.id === id && block.kind === 'markdown'
      ? { ...block, text }
      : block))
    setDirty(true)
  }

  function createBlankVersion() {
    if (busy) return
    setFrozen(null)
    setProgress(null)
    setVersionId(createMaterialId())
    setError('')
    setMessage('已放弃本页冻结的提交尝试；部分未完成上传会由服务端按策略过期清理。再次保存会使用新的版本标识。')
  }

  return <Card className="overflow-hidden">
    <div className="border-b border-divider bg-[linear-gradient(120deg,var(--color-surface),var(--color-bg))] px-5 py-4">
      <PageHeader className="mb-0" title={baseVersionId ? '继续写这篇笔记' : '新建笔记'} subtitle={baseVersionId ? '标题、正文、照片与手写内容按原顺序编辑。' : '先写下内容，再决定是否保存到当前服务器。'} actions={
        <div className="flex items-center gap-2">
          {dirty && <StatusBadge tone="warn" dot={false}>有未保存更改</StatusBadge>}
          <Button size="sm" variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void save()}>
            <Save className="size-4" aria-hidden />{busy ? '正在保存…' : '保存笔记'}
          </Button>
        </div>
      } />
    </div>

    <div className="space-y-4 p-5">
      {legacyNotice && <InlineAlert tone="warn" title="旧闪念兼容副本">{legacyNotice}</InlineAlert>}
      {lockedReason && <InlineAlert tone={!isCurrent ? 'warn' : 'info'}>{lockedReason}</InlineAlert>}
      <div>
        <Label htmlFor="native-note-title">笔记标题</Label>
        <Input id="native-note-title" aria-label="笔记标题" className="mt-1" maxLength={500} value={title}
          disabled={!editable} onChange={(event) => {
            setTitle(event.target.value)
            setDirty(true)
          }} />
      </div>

      <div className="space-y-3">
        {baseVersionId && <InlineAlert tone="info">已有录音、照片和手写材料会完整保留。当前支持续写正文及追加照片/手写；已有照片或手写原件不能替换或删除。</InlineAlert>}
        {inkEditor && <InkCanvasEditor
          key={inkEditor.sessionId}
          initialInk={inkEditor.ink}
          disabled={!editable}
          isSessionActive={() => isInkSessionActive(inkEditor.sessionId)}
          onPendingChange={(pending) => {
            if (inkSessionIdRef.current === inkEditor.sessionId) setInkPending(pending)
          }}
          onDirty={() => {
            if (isInkSessionActive(inkEditor.sessionId)) setDirty(true)
          }}
          onCancel={() => closeInkEditor(inkEditor.sessionId)}
          onSave={(ink, bytes) => {
            if (!isInkSessionActive(inkEditor.sessionId)) return
            const inkBlockId = inkEditor.id
            const next = { id: inkBlockId, kind: 'ink' as const, ink, bytes }
            setBlocks((current) => current.some((block) => block.kind === 'ink' && block.id === inkBlockId)
              ? current.map((block) => block.kind === 'ink' && block.id === inkBlockId ? next : block)
              : [...current, next])
            closeInkEditor(inkEditor.sessionId)
            setDirty(true)
          }}
        />}
        {blocks.map((block, index) => block.kind === 'markdown'
          ? <div key={block.id} className="rounded-ctl border border-border bg-bg p-3">
            <div className="mb-2 flex items-center justify-between gap-2">
              <Label htmlFor={`native-note-block-${block.id}`}>正文块 {index + 1}</Label>
              <span className="text-[11px] text-text-4">Markdown · {block.text.length.toLocaleString()} 字符</span>
            </div>
            <Textarea id={`native-note-block-${block.id}`} aria-label={`正文块 ${index + 1}`} className="min-h-64 font-sans text-sm leading-6"
              value={block.text} disabled={!editable} onChange={(event) => editMarkdown(block.id, event.target.value)} />
          </div>
          : <MaterialImageBlock key={block.id} block={block} index={index} editable={editable && !('sha256' in block)}
            onEdit={block.kind === 'ink' && !('sha256' in block) ? () => openInkEditor(block.id, block.ink) : undefined}
            onRemove={block.kind === 'photo' && !('sha256' in block)
              ? () => {
                setBlocks((current) => current.filter((item) => item.id !== block.id))
                setDirty(true)
              }
              : undefined} />)}
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-ctl bg-surface p-3">
        <Button size="sm" disabled={!editable} onClick={() => {
          setBlocks((current) => [
            ...current,
            { id: createMaterialId(), kind: 'markdown', text: '' },
          ])
          setDirty(true)
        }}><Plus className="size-4" aria-hidden />添加正文段</Button>
        <Button size="sm" disabled={!editable || Boolean(inkEditor)} onClick={() => openInkEditor(
          createMaterialId(),
          { width: 320, height: 240, strokes: [] },
        )}><PenLine className="size-4" aria-hidden />添加手写卡片</Button>
        <label className={fileLabelClass(editable)}>
          <ImagePlus className="size-4" aria-hidden />添加照片
          <input className="hidden" type="file" aria-label="添加照片" accept="image/jpeg,image/png,image/webp" multiple disabled={!editable}
            onChange={(event) => { void addPhotos(event.target.files); event.currentTarget.value = '' }} />
        </label>
        <label className={fileLabelClass(editable)}>
          <Camera className="size-4" aria-hidden />拍照加入
          <input className="hidden" type="file" aria-label="拍照加入笔记" accept="image/jpeg,image/png,image/webp" capture="environment" disabled={!editable}
            onChange={(event) => { void addPhotos(event.target.files); event.currentTarget.value = '' }} />
        </label>
      </div>

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
          <Button size="sm" variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void save()}>
            <Save className="size-4" aria-hidden />{busy ? '正在保存…' : frozen ? '重试同一提交' : '保存笔记'}
          </Button>
          {dirty && <StatusBadge tone="warn" dot={false}>有未保存更改</StatusBadge>}
          {!dirty && baseVersionId && <StatusBadge tone="ok" dot={false}>已从服务器载入</StatusBadge>}
          {!dirty && !baseVersionId && <StatusBadge tone="neutral" dot={false}>尚未保存</StatusBadge>}
        </div>
        {progress && <div className="rounded-ctl border border-info-border bg-info-soft p-3 text-xs text-text-2">
          <p className="font-medium text-text-1">正在同步笔记</p>
          <p className="mt-1 break-all">{progress.completedParts} / {progress.totalParts} · {progress.currentPath}</p>
        </div>}
        {error && <InlineAlert tone="crit" title="未完成">{error}</InlineAlert>}
        {message && <InlineAlert tone={message.includes('同时更新') ? 'warn' : 'ok'}>{message}</InlineAlert>}
      </section>

      <details className="rounded-ctl border border-border px-3 py-2 text-xs leading-5 text-text-3">
        <summary className="cursor-pointer">更多：历史材料、下载、导出与诊断</summary>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {baseVersionId && !isCurrent && <Button size="sm" loading={promoting} onClick={() => void promote()}>明确设为当前版本</Button>}
          {baseVersionId && <Button size="sm" variant="secondary" disabled={downloading} loading={downloading} onClick={() => void downloadOriginalBundle()}>
            <Download className="size-4" aria-hidden />下载完整原始材料包
          </Button>}
          <Button size="sm" variant="ghost" disabled={busy || exporting || Boolean(inkEditor) || inkPending} loading={exporting} onClick={() => void exportPackage()}>
            <Download className="size-4" aria-hidden />导出 Android 笔记包
          </Button>
          {frozen && <Button size="sm" variant="ghost" disabled={busy} onClick={createBlankVersion}><RotateCcw className="size-4" aria-hidden />重新开始保存</Button>}
        </div>
        {baseVersionId && <p className="mt-2">原始材料包下载上限为 128 MiB。</p>}
        <div className="mt-3 space-y-1 rounded-ctl bg-surface p-3 font-mono">
          <p className="break-all">笔记 ID：{noteId}</p>
          <p className="break-all">本次保存编号：{versionId}</p>
          <p className="break-all">来源版本：{baseVersionId ?? '新建笔记'}</p>
          <p>正文、照片、手写和已有录音材料保持在同一顺序。</p>
        </div>
      </details>
    </div>
  </Card>
}

function MaterialImageBlock({
  block,
  index,
  editable,
  onEdit,
  onRemove,
}: {
  block: Extract<MaterialDraftBlock, { kind: 'photo' | 'ink' }>
  index: number
  editable: boolean
  onEdit?: () => void
  onRemove?: () => void
}) {
  return <div className="overflow-hidden rounded-ctl border border-border bg-bg">
    <PreviewImage bytes={block.bytes} mimeType={block.kind === 'photo' ? block.mimeType : 'image/png'}
      alt={block.kind === 'photo' ? `正文照片 ${index + 1}` : `手写卡片 ${index + 1}`} />
    <div className="flex flex-wrap items-center gap-2 border-t border-divider px-3 py-2">
      <div>
        <p className="text-xs font-medium text-text-1">{block.kind === 'photo' ? `照片 ${index + 1}` : `手写卡片 ${index + 1}`}</p>
        <p className="break-all text-[11px] text-text-4">{block.kind === 'photo' ? block.assetId : '原始向量、尺寸与身份随 note.json 保留'}</p>
      </div>
      <StatusBadge className="ml-auto" tone={block.kind === 'photo' ? 'info' : 'neutral'} dot={false}>
        {block.kind === 'photo' ? '照片原字节' : '可编辑向量'}
      </StatusBadge>
      {onEdit && <Button size="sm" variant="ghost" disabled={!editable} onClick={onEdit} aria-label={`编辑手写卡片 ${index + 1}`}>
        <PenLine className="size-3.5" aria-hidden />编辑
      </Button>}
      {onRemove && <Button size="sm" variant="danger-soft" disabled={!editable} onClick={onRemove} aria-label={`移除照片 ${index + 1}`}>
        <Trash2 className="size-3.5" aria-hidden />
      </Button>}
    </div>
  </div>
}

function PreviewImage({ bytes, mimeType, alt }: { bytes: Uint8Array; mimeType: string; alt: string }) {
  const [url, setUrl] = useState('')
  useEffect(() => {
    const next = URL.createObjectURL(new Blob([bytes as BlobPart], { type: mimeType }))
    setUrl(next)
    return () => URL.revokeObjectURL(next)
  }, [bytes, mimeType])
  return url
    ? <img src={url} alt={alt} className="max-h-72 w-full object-contain bg-surface" />
    : <div className="h-40 bg-surface" aria-hidden />
}

function shortId(value: string) {
  return value.length > 12 ? `${value.slice(0, 8)}…` : value
}

function safePackageName(value: string) {
  const normalized = value.trim().replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').replace(/[. ]+$/g, '').slice(0, 80)
  return normalized || 'PIM-note'
}

function fileLabelClass(enabled: boolean) {
  return `inline-flex h-8 cursor-pointer items-center justify-center gap-1.5 rounded-ctl border border-border bg-bg px-3 text-[13px] font-medium text-text-1 hover:border-border-strong hover:bg-surface ${enabled ? '' : 'pointer-events-none opacity-50'}`
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}

interface EditorUploadSession {
  noteId: string
  baseVersionId: string | null
  controller: AbortController
}
