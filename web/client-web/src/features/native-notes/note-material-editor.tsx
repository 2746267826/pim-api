import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Camera, ImagePlus, Plus, RotateCcw, Save, Trash2 } from 'lucide-react'
import { Button, Card, InlineAlert, Input, Label, PageHeader, Skeleton, StatusBadge, Textarea } from '@/components/ui'
import {
  buildMaterialBundle,
  createMaterialId,
  hydrateParsedPhotos,
  parseMaterialVersion,
  type MaterialBundle,
  type MaterialDraftBlock,
  type NoteMaterialUpload,
  type SupportedPhotoMime,
} from './material-model'
import { nativeNotesApi, type NoteLibrarySource } from './api'
import { uploadMaterialBundle, type MaterialUploadProgress } from './upload-engine'

export interface NoteEditorSeed {
  noteId: string
  title: string
  body: string
  legacyNotice?: string
}

export interface NoteEditorPaneProps {
  versionId?: string
  seed?: NoteEditorSeed
  source?: NoteLibrarySource
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
  legacyNotice?: string
  onSaved: (upload: NoteMaterialUpload) => void
}

export function NoteEditorPane({ versionId, seed, source, onSaved }: NoteEditorPaneProps) {
  const material = useQuery({
    queryKey: ['native-note-material', versionId],
    queryFn: ({ signal }) => nativeNotesApi.version(versionId!, signal),
    enabled: Boolean(versionId),
  })
  const hydrated = useQuery({
    queryKey: ['native-note-material-hydrated', versionId],
    queryFn: async () => {
      if (!material.data) throw new Error('材料版本尚未载入')
      const parsed = parseMaterialVersion(material.data)
      const blocks = await hydrateParsedPhotos(
        parsed,
        material.data,
        (id, fileIndex) => nativeNotesApi.fileBytes(id, fileIndex),
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
      blocks={[{ id: createMaterialId(), kind: 'markdown', text: seed.body }]}
      revision={0}
      noteCreatedAtMillis={Date.now()}
      baseVersionId={null}
      recordings={0}
      isCurrent
      currentVersionId={null}
      legacyNotice={seed.legacyNotice}
      onSaved={onSaved}
    />
  }

  if (material.isPending || hydrated.isPending) {
    return <Card className="space-y-3 p-5"><Skeleton className="h-9 w-2/3" /><Skeleton className="h-56" /><p className="text-xs text-text-3">正在核对固定材料、正文块和图片原字节…</p></Card>
  }
  if (material.isError) {
    return <Card className="p-5"><InlineAlert tone="crit" title="材料版本读取失败">{errorText(material.error)}</InlineAlert></Card>
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
    recordings={source?.recordings ?? parsed.recordings}
    isCurrent={source?.isCurrent ?? material.data?.upload.isCurrent ?? false}
    currentVersionId={material.data?.upload.currentVersionId ?? null}
    onSaved={onSaved}
  />
}

function EmptyEditor() {
  return <Card className="min-h-[30rem] p-8">
    <div className="mx-auto flex max-w-sm flex-col items-center justify-center py-20 text-center">
      <span className="grid size-14 place-items-center rounded-full bg-surface-2 text-primary"><ImagePlus className="size-6" aria-hidden /></span>
      <h2 className="mt-4 text-lg font-semibold text-text-1">选择一篇笔记，或新建空白稿</h2>
      <p className="mt-2 text-sm leading-6 text-text-3">每篇新笔记由固定材料版本发布。正文、照片和手写预览会一起冻结；保存失败时不会自动换版本标识。</p>
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
  legacyNotice,
  onSaved,
}: LoadedEditorProps) {
  const [title, setTitle] = useState(initialTitle)
  const [blocks, setBlocks] = useState<MaterialDraftBlock[]>(initialBlocks)
  const [versionId, setVersionId] = useState(createMaterialId)
  const [frozen, setFrozen] = useState<MaterialBundle | null>(null)
  const [progress, setProgress] = useState<MaterialUploadProgress | null>(null)
  const [busy, setBusy] = useState(false)
  const [promoting, setPromoting] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const editable = !busy && !promoting && !frozen && isCurrent
  const canSubmit = !busy && !promoting && isCurrent && recordings === 0 && blocks.length > 0

  const lockedReason = recordings > 0
    ? '此版本包含录音。Web 端不能在没有服务端克隆协议的情况下重建并丢失音频，因此只允许查看；请使用手机或 Windows 在保留全部录音的前提下创建新版本。'
    : !isCurrent
      ? '当前打开的不是笔记的选用版本。请先将此版本明确设为当前版本，再进行编辑。'
      : frozen
        ? '本次材料批次已经冻结。重试会复用同一版本标识，不能修改正文或图片。'
        : ''

  async function save() {
    if (busy || !isCurrent || recordings > 0 || !blocks.length) return
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
          recordings: [],
        })
        setFrozen(batch)
      }
      const upload = await uploadMaterialBundle(batch, nativeNotesApi, setProgress)
      setMessage(upload.hasConflict
        ? '新版本已完整保存，但另一端同时更新了当前版本；两份材料均保留，请明确选择当前版本。'
        : '新版本已完整保存，并成为当前材料版本。')
      onSaved(upload)
    } catch (failure) {
      setError(`${errorText(failure)}。材料和版本标识已保留，可在本页重试。`)
    } finally {
      setBusy(false)
    }
  }

  async function promote() {
    if (!baseVersionId || busy || promoting) return
    setPromoting(true)
    setError('')
    try {
      const upload = await nativeNotesApi.promote(baseVersionId, currentVersionId)
      setMessage('已明确将此版本设为当前版本。')
      onSaved(upload)
    } catch (failure) {
      setError(`${errorText(failure)}。当前选用版本没有改变。`)
    } finally {
      setPromoting(false)
    }
  }

  async function addPhotos(files: FileList | null) {
    if (!files?.length || !editable) return
    setError('')
    const accepted: MaterialDraftBlock[] = []
    for (const file of [...files]) {
      if (file.type !== 'image/jpeg' && file.type !== 'image/png') {
        setError('Web 材料当前只接受 JPEG/PNG 照片。')
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
    if (accepted.length) setBlocks((current) => [...current, ...accepted])
  }

  function editMarkdown(id: string, text: string) {
    if (!editable) return
    setBlocks((current) => current.map((block) => block.id === id && block.kind === 'markdown'
      ? { ...block, text }
      : block))
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
      <PageHeader className="mb-0" title={baseVersionId ? '编辑固定材料' : '新建固定材料'} subtitle={baseVersionId ? `将基于版本 ${shortId(baseVersionId)} 创建第 ${revision + 1} 版；旧版本原样保留。` : '首次保存会创建新笔记和第一个固定材料版本。'} actions={
        <div className="flex flex-wrap items-center gap-2">
          {baseVersionId && !isCurrent && <Button size="sm" loading={promoting} onClick={() => void promote()}>设为当前版本</Button>}
          <Button size="sm" variant="primary" loading={busy} disabled={!canSubmit} onClick={() => void save()}>
            <Save className="size-4" aria-hidden />{frozen ? '重试同一冻结批次' : '保存新版本'}
          </Button>
        </div>
      } />
    </div>

    <div className="space-y-4 p-5">
      {legacyNotice && <InlineAlert tone="warn" title="旧闪念兼容副本">{legacyNotice}</InlineAlert>}
      {lockedReason && <InlineAlert tone={recordings > 0 || !isCurrent ? 'warn' : 'info'}>{lockedReason}</InlineAlert>}
      <div>
        <Label htmlFor="native-note-title">笔记标题</Label>
        <Input id="native-note-title" aria-label="笔记标题" className="mt-1" maxLength={500} value={title}
          disabled={!editable} onChange={(event) => setTitle(event.target.value)} />
      </div>

      <div className="space-y-3">
        {blocks.map((block, index) => block.kind === 'markdown'
          ? <div key={block.id} className="rounded-ctl border border-border bg-bg p-3">
            <div className="mb-2 flex items-center justify-between gap-2">
              <Label htmlFor={`native-note-block-${block.id}`}>正文块 {index + 1}</Label>
              <span className="text-[11px] text-text-4">Markdown · {block.text.length.toLocaleString()} 字符</span>
            </div>
            <Textarea id={`native-note-block-${block.id}`} aria-label={`正文块 ${index + 1}`} className="min-h-44 font-mono text-xs"
              value={block.text} disabled={!editable} onChange={(event) => editMarkdown(block.id, event.target.value)} />
          </div>
          : <MaterialImageBlock key={block.id} block={block} index={index} editable={editable}
            onRemove={block.kind === 'photo'
              ? () => setBlocks((current) => current.filter((item) => item.id !== block.id))
              : undefined} />)}
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-ctl bg-surface p-3">
        <Button size="sm" disabled={!editable} onClick={() => setBlocks((current) => [
          ...current,
          { id: createMaterialId(), kind: 'markdown', text: '' },
        ])}><Plus className="size-4" aria-hidden />添加正文段</Button>
        <label className={fileLabelClass(editable)}>
          <ImagePlus className="size-4" aria-hidden />添加照片
          <input className="hidden" type="file" aria-label="添加照片" accept="image/jpeg,image/png" multiple disabled={!editable}
            onChange={(event) => { void addPhotos(event.target.files); event.currentTarget.value = '' }} />
        </label>
        <label className={fileLabelClass(editable)}>
          <Camera className="size-4" aria-hidden />拍照加入
          <input className="hidden" type="file" aria-label="拍照加入笔记" accept="image/jpeg,image/png" capture="environment" disabled={!editable}
            onChange={(event) => { void addPhotos(event.target.files); event.currentTarget.value = '' }} />
        </label>
        {frozen && <Button size="sm" variant="ghost" disabled={busy} onClick={createBlankVersion}><RotateCcw className="size-4" aria-hidden />放弃冻结批次</Button>}
      </div>

      {progress && <div className="rounded-ctl border border-info-border bg-info-soft p-3 text-xs text-text-2">
        <p className="font-medium text-text-1">正在提交固定材料</p>
        <p className="mt-1 break-all">{progress.completedParts} / {progress.totalParts} 分块 · {progress.currentPath}</p>
      </div>}
      {error && <InlineAlert tone="crit" title="未完成">{error}</InlineAlert>}
      {message && <InlineAlert tone={message.includes('同时更新') ? 'warn' : 'ok'}>{message}</InlineAlert>}
      <details className="text-xs leading-5 text-text-3">
        <summary className="cursor-pointer">本次材料核对信息</summary>
        <div className="mt-2 space-y-1 rounded-ctl bg-surface p-3 font-mono">
          <p className="break-all">笔记 ID：{noteId}</p>
          <p className="break-all">新版本 ID：{versionId}</p>
          <p className="break-all">基础版本：{baseVersionId ?? '首次发布'}</p>
          <p>正文/图片/手写预览会作为一个不可变版本一起提交。</p>
        </div>
      </details>
    </div>
  </Card>
}

function MaterialImageBlock({
  block,
  index,
  editable,
  onRemove,
}: {
  block: Extract<MaterialDraftBlock, { kind: 'photo' | 'ink' }>
  index: number
  editable: boolean
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
        {block.kind === 'photo' ? '照片原字节' : 'Web 只读手写'}
      </StatusBadge>
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

function fileLabelClass(enabled: boolean) {
  return `inline-flex h-8 cursor-pointer items-center justify-center gap-1.5 rounded-ctl border border-border bg-bg px-3 text-[13px] font-medium text-text-1 hover:border-border-strong hover:bg-surface ${enabled ? '' : 'pointer-events-none opacity-50'}`
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}
