import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import {
  EditorContent,
  NodeViewWrapper,
  ReactNodeViewRenderer,
  useEditor,
  type NodeViewProps,
} from '@tiptap/react'
import {
  Node as TiptapNode,
  createNodeFromContent,
  type Editor,
} from '@tiptap/core'
import { Image } from '@tiptap/extension-image'
import {
  Fragment,
  type Attrs,
  type Node as ProseMirrorNode,
  type NodeType,
} from '@tiptap/pm/model'
import { TextSelection } from '@tiptap/pm/state'
import StarterKit from '@tiptap/starter-kit'
import { Markdown } from 'tiptap-markdown'
import {
  Bold,
  Camera,
  Eye,
  EyeOff,
  Heading2,
  ImagePlus,
  Italic,
  List,
  ListOrdered,
  PenLine,
  Quote,
  Trash2,
} from 'lucide-react'
import { Button, Card, Label, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'
import { InkCanvasEditor } from './ink-canvas-editor'
import {
  createMaterialId,
  type MaterialDraftBlock,
  type SupportedPhotoMime,
} from './material-model'

const PHOTO_MIMES = new Set<string>(['image/jpeg', 'image/png', 'image/webp'])
const MAX_PHOTO_BYTES = 32 * 1024 * 1024
const BODY_LABEL = '笔记正文'

const NoteDocument = TiptapNode.create({
  name: 'doc',
  topNode: true,
  content: '(markdownBlock | nativeNoteMaterial)+',
})

interface MaterialNodeOptions {
  resolveBlock: (blockId: string) => MaterialDraftBlock | undefined
  onEditInk: (blockId: string, revision: () => void) => void
  onChange: () => void
  showTechnicalDetails: () => boolean
}

interface EditorSelectionPoint {
  from: number
  to: number
}

interface ActiveInkEditor {
  blockId: string
  ink: Record<string, unknown>
  sessionId: string
  isNew: boolean
  insertionPoint: number
  revision?: () => void
}

interface MarkdownStorage {
  serializer: { serialize: (node: ProseMirrorNode) => string }
  parser: { parse: (markdown: string) => string }
}

export interface ContinuousMaterialEditorHandle {
  getBlocks: () => MaterialDraftBlock[]
  getMarkdownPreview: () => string
  focus: () => void
}

interface ContinuousMaterialEditorProps {
  initialBlocks: MaterialDraftBlock[]
  editable: boolean
  showVersionDetails: boolean
  isSessionActive: () => boolean
  onDirty: () => void
  onContentStateChange: (hasContent: boolean) => void
  onInkEditorChange: (open: boolean) => void
  onInkPendingChange: (pending: boolean) => void
  onError: (message: string) => void
}

const MarkdownBlock = TiptapNode.create({
  name: 'markdownBlock',
  group: 'block',
  content: 'block+',
  defining: true,
  isolating: true,
  addAttributes() {
    return {
      blockId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-pim-markdown-block-id'),
        renderHTML: (attributes) => ({ 'data-pim-markdown-block-id': attributes.blockId }),
      },
    }
  },
  parseHTML() {
    return [{ tag: 'div[data-pim-markdown-block-id]' }]
  },
  renderHTML({ HTMLAttributes }) {
    return ['div', { ...HTMLAttributes, class: 'native-note-markdown-block' }, 0]
  },
  addStorage() {
    return {
      markdown: {
        serialize(
          state: { renderContent: (node: ProseMirrorNode) => void },
          node: ProseMirrorNode,
        ) {
          state.renderContent(node)
        },
      },
    }
  },
})

const MaterialNode = TiptapNode.create<MaterialNodeOptions>({
  name: 'nativeNoteMaterial',
  group: 'block',
  atom: true,
  selectable: true,
  draggable: false,
  addOptions() {
    return {
      resolveBlock: () => undefined,
      onEditInk: () => undefined,
      onChange: () => undefined,
      showTechnicalDetails: () => false,
    }
  },
  addAttributes() {
    return {
      blockId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-pim-material-id'),
        renderHTML: (attributes) => ({ 'data-pim-material-id': attributes.blockId }),
      },
      kind: {
        default: 'photo',
        parseHTML: (element) => element.getAttribute('data-pim-material-kind') ?? 'photo',
        renderHTML: (attributes) => ({ 'data-pim-material-kind': attributes.kind }),
      },
      revision: {
        default: 0,
        rendered: false,
      },
    }
  },
  parseHTML() {
    return [{ tag: 'div[data-pim-material-id]' }]
  },
  renderHTML({ HTMLAttributes }) {
    return ['div', HTMLAttributes]
  },
  addNodeView() {
    return ReactNodeViewRenderer(MaterialNodeView)
  },
  addStorage() {
    return {
      markdown: {
        serialize() {
          // 材料链接由 buildMaterialBundle 按块顺序统一生成。
        },
      },
    }
  },
})

function MaterialNodeView({ node, extension, editor, deleteNode, updateAttributes, selected }: NodeViewProps) {
  const options = extension.options as MaterialNodeOptions
  const blockId = String(node.attrs.blockId ?? '')
  const block = options.resolveBlock(blockId)
  const imageUrl = useObjectUrl(block)
  const editable = editor.isEditable

  if (!block || block.kind === 'markdown') {
    return <NodeViewWrapper className="my-4 rounded-ctl border border-crit-border bg-crit-soft p-3 text-xs text-crit" contentEditable={false}>
      正文材料未载入，已停止静默丢弃。请刷新后重试。
    </NodeViewWrapper>
  }

  const isPersisted = 'sha256' in block
  return <NodeViewWrapper
    className={cn(
      'my-4 overflow-hidden rounded-ctl border bg-bg',
      selected ? 'border-primary ring-2 ring-primary-ring' : 'border-border',
    )}
    data-pim-material-id={block.id}
    data-pim-material-kind={block.kind}
    data-material-block-id={block.id}
    contentEditable={false}
  >
    {imageUrl
      ? <img
          src={imageUrl}
          alt={block.kind === 'photo' ? '正文照片' : '手写卡片'}
          className="mx-auto max-h-[min(58vh,34rem)] w-full object-contain"
        />
      : <div className="grid min-h-32 place-items-center bg-surface text-xs text-text-3">材料预览不可用，原稿仍保留</div>}
    <div className="flex flex-wrap items-center gap-2 border-t border-divider px-3 py-2">
      <div className="min-w-0">
        <p className="text-xs font-medium text-text-1">{block.kind === 'photo' ? '正文照片' : '手写卡片'}</p>
        {options.showTechnicalDetails() && (
          <p className="mono mt-0.5 break-all text-[10px] text-text-4">{block.id}</p>
        )}
      </div>
      <StatusBadge className="ml-auto" tone={block.kind === 'photo' ? 'info' : 'neutral'} dot={false}>
        {block.kind === 'photo' ? (isPersisted ? '原始照片' : '待保存照片') : (isPersisted ? '可编辑手写原稿' : '待保存手写')}
      </StatusBadge>
      {block.kind === 'ink' && (
        <Button
          size="sm"
          variant="ghost"
          aria-label="编辑手写卡片"
          disabled={!editable}
          onClick={() => options.onEditInk(block.id, () => updateAttributes({ revision: Date.now() }))}
        >
          <PenLine className="size-3.5" aria-hidden />编辑手写
        </Button>
      )}
      {block.kind === 'photo' && !isPersisted && (
        <Button
          size="sm"
          variant="danger-soft"
          aria-label="移除未保存照片"
          onClick={() => {
            options.onChange()
            deleteNode()
          }}
        >
          <Trash2 className="size-3.5" aria-hidden />
        </Button>
      )}
    </div>
  </NodeViewWrapper>
}

function useObjectUrl(block: MaterialDraftBlock | undefined) {
  const [url, setUrl] = useState('')
  useEffect(() => {
    if (!block || block.kind === 'markdown' || !block.bytes.length) {
      setUrl('')
      return
    }
    const next = URL.createObjectURL(new Blob([block.bytes as BlobPart], {
      type: block.kind === 'photo' ? block.mimeType : 'image/png',
    }))
    setUrl(next)
    return () => URL.revokeObjectURL(next)
  }, [block])
  return url
}

export const ContinuousMaterialEditor = forwardRef<
  ContinuousMaterialEditorHandle,
  ContinuousMaterialEditorProps
>(function ContinuousMaterialEditor({
  initialBlocks,
  editable,
  showVersionDetails,
  isSessionActive,
  onDirty,
  onContentStateChange,
  onInkEditorChange,
  onInkPendingChange,
  onError,
}: ContinuousMaterialEditorProps, ref) {
  const blocksByIdRef = useRef(new Map(initialBlocks.map((block) => [block.id, block])))
  const originalIdsRef = useRef(new Set(initialBlocks.map((block) => block.id)))
  const sourceByIdRef = useRef(new Map(initialBlocks.flatMap((block) =>
    block.kind === 'markdown' && block.source ? [[block.id, block.source] as const] : [])))
  const originalMarkdownRef = useRef(new Map(initialBlocks.flatMap((block) =>
    block.kind === 'markdown' ? [[block.id, block.text] as const] : [])))
  const initialContentRef = useRef(new Map<string, string>())
  const initialDocument = useMemo(() => initialBlocks, [initialBlocks])
  const editableRef = useRef(editable)
  const showTechnicalDetailsRef = useRef(showVersionDetails)
  const sessionActiveRef = useRef(isSessionActive)
  const dirtyCallbackRef = useRef(onDirty)
  const errorCallbackRef = useRef(onError)
  const contentStateCallbackRef = useRef(onContentStateChange)
  const inkEditorChangeRef = useRef(onInkEditorChange)
  const lastSelectionRef = useRef<EditorSelectionPoint>({ from: 1, to: 1 })
  const mountedRef = useRef(false)
  const [inkEditor, setInkEditor] = useState<ActiveInkEditor | null>(null)
  const [preview, setPreview] = useState<string | null>(null)

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])
  useEffect(() => {
    editableRef.current = editable
  }, [editable])
  useEffect(() => {
    showTechnicalDetailsRef.current = showVersionDetails
  }, [showVersionDetails])
  useEffect(() => {
    sessionActiveRef.current = isSessionActive
    dirtyCallbackRef.current = onDirty
    errorCallbackRef.current = onError
    contentStateCallbackRef.current = onContentStateChange
    inkEditorChangeRef.current = onInkEditorChange
  }, [isSessionActive, onDirty, onError, onContentStateChange, onInkEditorChange])
  useEffect(() => {
    inkEditorChangeRef.current(Boolean(inkEditor))
  }, [inkEditor])

  const editor = useEditor({
    extensions: [
      StarterKit.configure({ document: false, trailingNode: false }),
      NoteDocument,
      Image.configure({ inline: false, allowBase64: false }),
      Markdown.configure({ html: true, linkify: false, breaks: false }),
      MarkdownBlock,
      MaterialNode.configure({
        resolveBlock: (blockId: string) => blocksByIdRef.current.get(blockId),
        showTechnicalDetails: () => showTechnicalDetailsRef.current,
        onChange: () => dirtyCallbackRef.current(),
        onEditInk: (blockId: string, revision: () => void) => {
          const block = blocksByIdRef.current.get(blockId)
          if (!block || block.kind !== 'ink' || !editableRef.current) return
          const insertionPoint = Math.max(1, lastSelectionRef.current.from)
          setInkEditor({
            blockId,
            ink: block.ink,
            sessionId: createMaterialId(),
            isNew: false,
            insertionPoint,
            revision,
          })
        },
      }),
    ],
    content: '',
    editable,
    editorProps: {
      attributes: {
        class: cn(
          'native-note-prose min-h-[58vh] max-w-none px-1 pb-24 outline-none',
          'text-[15px] leading-7 text-text-1',
          '[&_h1]:mt-6 [&_h1]:mb-3 [&_h1]:text-2xl [&_h1]:leading-9 [&_h1]:font-semibold',
          '[&_h2]:mt-5 [&_h2]:mb-2 [&_h2]:text-xl [&_h2]:leading-8 [&_h2]:font-semibold',
          '[&_h3]:mt-4 [&_h3]:mb-2 [&_h3]:text-lg [&_h3]:font-semibold',
          '[&_p]:my-3 [&_ul]:my-3 [&_ul]:list-disc [&_ul]:pl-6 [&_ol]:my-3 [&_ol]:list-decimal [&_ol]:pl-6',
          '[&_blockquote]:my-4 [&_blockquote]:border-l-2 [&_blockquote]:border-primary [&_blockquote]:pl-4 [&_blockquote]:text-text-2',
          '[&_pre]:my-4 [&_pre]:overflow-x-auto [&_pre]:rounded-ctl [&_pre]:bg-surface-2 [&_pre]:p-3 [&_pre]:text-xs',
          '[&_code]:rounded [&_code]:bg-surface-2 [&_code]:px-1 [&_code]:text-xs',
          '[&_hr]:my-6 [&_hr]:border-divider',
          '[&_a]:text-primary [&_a]:underline',
          '[&_img]:my-3 [&_img]:max-h-[34rem] [&_img]:max-w-full [&_img]:rounded-ctl [&_img]:object-contain',
          '[&>.native-note-markdown-block:last-child]:min-h-[56vh]',
        ),
        'aria-label': BODY_LABEL,
        'aria-multiline': 'true',
        role: 'textbox',
        spellcheck: 'true',
        'data-testid': 'continuous-note-editor',
      },
    },
    onBeforeCreate: ({ editor: created }) => {
      created.options.content = buildMaterialDocument(created, initialDocument).toJSON()
    },
    onCreate: ({ editor: created }) => {
      initialContentRef.current = snapshotMarkdownBlocks(created)
      lastSelectionRef.current = selectionPoint(created)
      contentStateCallbackRef.current(documentHasContent(created))
    },
    onUpdate: ({ editor: updated }) => {
      dirtyCallbackRef.current()
      contentStateCallbackRef.current(documentHasContent(updated))
      lastSelectionRef.current = selectionPoint(updated)
    },
    onSelectionUpdate: ({ editor: updated }) => {
      lastSelectionRef.current = selectionPoint(updated)
    },
  })

  useEffect(() => {
    if (!editor || editor.options.editable === editable) return
    editor.setEditable(editable, false)
  }, [editable, editor])

  const getBlocks = useCallback(() => {
    if (!editor) return initialBlocks
    return serializeMaterialBlocks(
      editor,
      (blockId) => blocksByIdRef.current.get(blockId),
      originalMarkdownRef.current,
      initialContentRef.current,
      originalIdsRef.current,
      sourceByIdRef.current,
    )
  }, [editor, initialBlocks])

  useImperativeHandle(ref, () => ({
    getBlocks,
    getMarkdownPreview: () => projectMaterialMarkdown(getBlocks()),
    focus: () => editor?.commands.focus(),
  }), [editor, getBlocks])

  function restoreInsertionPoint() {
    if (!editor) return 1
    const point = Math.min(lastSelectionRef.current.from, Math.max(1, editor.state.doc.content.size))
    editor.commands.setTextSelection(point)
    return point
  }

  async function addPhotos(files: FileList | null) {
    if (!files?.length || !editable || !editor) return
    const insertionPoint = restoreInsertionPoint()
    const accepted: MaterialDraftBlock[] = []
    for (const file of [...files]) {
      if (!PHOTO_MIMES.has(file.type)) {
        errorCallbackRef.current('Web 材料当前只接受 JPEG/PNG/WebP 照片。')
        continue
      }
      if (file.size > MAX_PHOTO_BYTES) {
        errorCallbackRef.current(`照片 ${file.name} 超过 32 MiB，未加入材料。`)
        continue
      }
      accepted.push({
        kind: 'photo',
        id: createMaterialId(),
        assetId: createMaterialId(),
        mimeType: file.type as SupportedPhotoMime,
        bytes: new Uint8Array(await file.arrayBuffer()),
      })
      if (!sessionActiveRef.current()) return
    }
    if (!accepted.length) return
    for (const block of accepted) blocksByIdRef.current.set(block.id, block)
    insertMaterialBlocks(editor, accepted, insertionPoint)
  }

  function openNewInkEditor() {
    if (!editable || !editor) return
    const insertionPoint = restoreInsertionPoint()
    setInkEditor({
      blockId: createMaterialId(),
      ink: { width: 320, height: 240, strokes: [] },
      sessionId: createMaterialId(),
      isNew: true,
      insertionPoint,
    })
  }

  function saveInk(ink: Record<string, unknown>, bytes: Uint8Array) {
    if (!editor || !inkEditor) return
    if (!sessionActiveRef.current()) {
      setInkEditor(null)
      return
    }
    const previous = blocksByIdRef.current.get(inkEditor.blockId)
    const next: MaterialDraftBlock = {
      id: inkEditor.blockId,
      kind: 'ink',
      ink,
      bytes,
      ...(previous?.source ? { source: previous.source } : {}),
    }
    blocksByIdRef.current.set(next.id, next)
    if (inkEditor.isNew) {
      insertMaterialBlocks(editor, [next], inkEditor.insertionPoint)
    } else {
      inkEditor.revision?.()
      dirtyCallbackRef.current()
    }
    setInkEditor(null)
  }

  function togglePreview() {
    if (preview !== null) {
      setPreview(null)
      return
    }
    try {
      setPreview(projectMaterialMarkdown(getBlocks()))
    } catch (failure) {
      errorCallbackRef.current(
        `${failure instanceof Error ? failure.message : '正文序列化失败'}。当前草稿仍保留在本页，未生成预览。`,
      )
    }
  }

  return <div className="space-y-3">
    <div
      data-testid="note-format-toolbar"
      className="sticky z-10 -mx-1 flex flex-wrap items-center gap-1 rounded-ctl border border-border bg-bg/95 px-2 py-1.5 shadow-card backdrop-blur"
      style={{ top: 'calc(var(--note-sticky-top, 0px) + var(--note-sticky-header-height, 4.5rem))' }}
    >
      <span className="px-1 text-[11px] font-medium text-text-3">连续正文</span>
      <Button size="sm" variant="ghost" aria-label="加粗" disabled={!editable} onClick={() => editor?.chain().focus().toggleBold().run()}>
        <Bold className="size-4" aria-hidden />
      </Button>
      <Button size="sm" variant="ghost" aria-label="斜体" disabled={!editable} onClick={() => editor?.chain().focus().toggleItalic().run()}>
        <Italic className="size-4" aria-hidden />
      </Button>
      <Button size="sm" variant="ghost" aria-label="二级标题" disabled={!editable} onClick={() => editor?.chain().focus().toggleHeading({ level: 2 }).run()}>
        <Heading2 className="size-4" aria-hidden />
      </Button>
      <Button size="sm" variant="ghost" aria-label="无序列表" disabled={!editable} onClick={() => editor?.chain().focus().toggleBulletList().run()}>
        <List className="size-4" aria-hidden />
      </Button>
      <Button size="sm" variant="ghost" aria-label="有序列表" disabled={!editable} onClick={() => editor?.chain().focus().toggleOrderedList().run()}>
        <ListOrdered className="size-4" aria-hidden />
      </Button>
      <Button size="sm" variant="ghost" aria-label="引用" disabled={!editable} onClick={() => editor?.chain().focus().toggleBlockquote().run()}>
        <Quote className="size-4" aria-hidden />
      </Button>
      <span className="mx-1 h-5 w-px bg-divider" />
      <Button size="sm" variant="ghost" aria-label="添加手写卡片" disabled={!editable} onClick={openNewInkEditor}>
        <PenLine className="size-4" aria-hidden />手写
      </Button>
      <label className={toolbarFileClass(editable)}>
        <ImagePlus className="size-4" aria-hidden />照片
        <input
          className="hidden"
          type="file"
          aria-label="添加照片"
          accept="image/jpeg,image/png,image/webp"
          multiple
          disabled={!editable}
          onChange={(event) => {
            void addPhotos(event.currentTarget.files)
            event.currentTarget.value = ''
          }}
        />
      </label>
      <label className={toolbarFileClass(editable)}>
        <Camera className="size-4" aria-hidden />拍照
        <input
          className="hidden"
          type="file"
          aria-label="拍照加入笔记"
          accept="image/jpeg,image/png,image/webp"
          capture="environment"
          disabled={!editable}
          onChange={(event) => {
            void addPhotos(event.currentTarget.files)
            event.currentTarget.value = ''
          }}
        />
      </label>
      <Button className="ml-auto" size="sm" variant="ghost" onClick={togglePreview}>
        {preview === null ? <Eye className="size-4" aria-hidden /> : <EyeOff className="size-4" aria-hidden />}
        {preview === null ? 'Markdown 预览' : '收起预览'}
      </Button>
      <span className="hidden text-[11px] text-text-4 sm:inline">唯一编辑上下文</span>
    </div>

    {inkEditor && <Card className="space-y-3 border-primary/40 p-4">
      <div className="flex flex-wrap items-center gap-2">
        <div>
          <h3 className="text-sm font-semibold text-text-1">{inkEditor.isNew ? '插入手写卡片' : '继续编辑手写原稿'}</h3>
          <p className="mt-1 text-xs text-text-3">保存后卡片会留在当前光标位置；前后正文继续在同一正文上下文中编辑。</p>
        </div>
        <StatusBadge className="ml-auto" tone="info" dot={false}>原稿与预览成套保存</StatusBadge>
      </div>
      <InkCanvasEditor
        key={inkEditor.sessionId}
        initialInk={inkEditor.ink}
        disabled={!editable}
        isSessionActive={() => editableRef.current && sessionActiveRef.current() && inkEditor !== null}
        onPendingChange={onInkPendingChange}
        onDirty={() => dirtyCallbackRef.current()}
        onCancel={() => setInkEditor(null)}
        onSave={saveInk}
      />
    </Card>}

    <div className={cn('rounded-ctl border border-border bg-bg px-4 py-3', !editable && 'bg-surface/60')}>
      <EditorContent editor={editor} />
    </div>

    {preview !== null && <section className="rounded-ctl border border-border bg-surface p-4" aria-label="整篇 Markdown 预览">
      <div className="flex flex-wrap items-center gap-2">
        <Label>整篇 Markdown 预览</Label>
        <StatusBadge className="ml-auto" tone="neutral" dot={false}>只读投影</StatusBadge>
      </div>
      <pre className="mono mt-3 max-h-[48vh] overflow-auto whitespace-pre-wrap text-xs leading-6 text-text-2">{preview}</pre>
      <p className="mt-2 text-[11px] leading-5 text-text-3">预览按当前正文、照片和手写顺序生成，不替代服务器上的结构化原稿。</p>
    </section>}
  </div>
})

export function buildMaterialDocument(editor: Editor, blocks: MaterialDraftBlock[]): ProseMirrorNode {
  const schema = editor.schema
  const markdownStorage = (editor.storage as unknown as { markdown: Pick<MarkdownStorage, 'parser'> }).markdown
  const ordered: ProseMirrorNode[] = []
  for (const block of blocks) {
    if (block.kind === 'markdown') {
      const html = markdownStorage.parser.parse(block.text)
      const parsed = createNodeFromContent(html, schema, {
        slice: true,
        errorOnInvalidContent: true,
      })
      const content = parsed instanceof Fragment ? parsed : Fragment.from(parsed)
      ordered.push(schema.nodes.markdownBlock.create(
        { blockId: block.id },
        content.childCount ? content : Fragment.from(schema.nodes.paragraph.create()),
      ))
      continue
    }
    ordered.push(schema.nodes.nativeNoteMaterial.create({
      blockId: block.id,
      kind: block.kind,
    }))
  }
  if (!ordered.length) {
    ordered.push(schema.nodes.markdownBlock.create(
      { blockId: createMaterialId() },
      schema.nodes.paragraph.create(),
    ))
  } else if (ordered[ordered.length - 1]?.type.name !== MarkdownBlock.name) {
    ordered.push(schema.nodes.markdownBlock.create(
      { blockId: createMaterialId() },
      schema.nodes.paragraph.create(),
    ))
  }
  return schema.nodes.doc.create(null, ordered)
}

export function projectMaterialMarkdown(blocks: MaterialDraftBlock[]): string {
  return blocks.map((block) => {
    if (block.kind === 'markdown') return block.text
    if (block.kind === 'photo') {
      const extension = block.mimeType === 'image/jpeg' ? 'jpg' : block.mimeType === 'image/webp' ? 'webp' : 'png'
      return `![照片](assets/${block.assetId}.${extension})`
    }
    return `![手写卡片](assets/${block.id}.png)`
  }).join('\n\n')
}

export function serializeMaterialBlocks(
  editor: Editor,
  resolveBlock: (blockId: string) => MaterialDraftBlock | undefined,
  originalMarkdown: Map<string, string>,
  initialContent: Map<string, string>,
  originalIds: Set<string>,
  sourceById: Map<string, Record<string, unknown>>,
): MaterialDraftBlock[] {
  const markdownStorage = (editor.storage as unknown as { markdown: MarkdownStorage }).markdown
  const blocks: MaterialDraftBlock[] = []
  const seen = new Set<string>()

  editor.state.doc.forEach((node) => {
    if (node.type.name === MarkdownBlock.name) {
      const blockId = String(node.attrs.blockId ?? '')
      if (!blockId) throw new Error('正文块身份缺失，未提交保存')
      if (seen.has(blockId)) throw new Error('正文块标识重复，未提交保存')
      seen.add(blockId)
      const unchanged = initialContent.get(blockId) === JSON.stringify(node.content.toJSON())
      const text = unchanged
        ? originalMarkdown.get(blockId) ?? markdownStorage.serializer.serialize(node)
        : markdownStorage.serializer.serialize(node).trimEnd()
      if (!text.trim() && !originalIds.has(blockId)) return
      blocks.push({
        id: blockId,
        kind: 'markdown',
        text,
        ...(sourceById.has(blockId) ? { source: sourceById.get(blockId) } : {}),
      })
      return
    }
    if (node.type.name === MaterialNode.name) {
      const blockId = String(node.attrs.blockId ?? '')
      if (!blockId) throw new Error('正文材料身份缺失，未提交保存')
      if (seen.has(blockId)) throw new Error('正文块标识重复，未提交保存')
      seen.add(blockId)
      const block = resolveBlock(blockId)
      if (!block || block.kind === 'markdown') {
        throw new Error(`正文材料 ${blockId} 未能从编辑上下文还原，未提交保存`)
      }
      if (String(node.attrs.kind ?? '') !== block.kind) {
        throw new Error(`正文材料 ${blockId} 的类型与编辑上下文不一致，未提交保存`)
      }
      blocks.push(block)
      return
    }
    throw new Error('连续正文包含未归属段落，未提交保存')
  })

  if (!blocks.length) {
    blocks.push({ id: createMaterialId(), kind: 'markdown', text: '' })
  }
  return blocks
}

function snapshotMarkdownBlocks(editor: Editor): Map<string, string> {
  const result = new Map<string, string>()
  editor.state.doc.forEach((node) => {
    if (node.type.name !== MarkdownBlock.name) return
    const blockId = String(node.attrs.blockId ?? '')
    if (blockId) result.set(blockId, JSON.stringify(node.content.toJSON()))
  })
  return result
}

function insertMaterialBlocks(
  editor: Editor,
  blocks: MaterialDraftBlock[],
  insertionPoint: number,
) {
  const materials = blocks.filter((block): block is Exclude<MaterialDraftBlock, { kind: 'markdown' }> =>
    block.kind !== 'markdown')
  if (!materials.length) return

  const { state } = editor
  const requestPos = Math.min(
    Math.max(insertionPoint, 1),
    Math.max(1, state.doc.content.size),
  )
  const transaction = state.tr
  const materialNodes = materials.map((block) =>
    state.schema.nodes.nativeNoteMaterial.create({ blockId: block.id, kind: block.kind }))

  let insertionPos = requestPos
  let hasRightBlock = false
  const resolved = transaction.doc.resolve(requestPos)
  const containerDepth = findMarkdownContainerDepth(resolved)
  if (containerDepth !== null) {
    const newBlockId = createMaterialId()
    const splitDepth = Math.max(1, resolved.depth)
    const typesAfter: Array<{ type: NodeType; attrs?: Attrs } | null> = []
    typesAfter[0] = {
      type: state.schema.nodes.markdownBlock,
      attrs: { blockId: newBlockId },
    }
    transaction.split(requestPos, splitDepth, typesAfter)

    const originalId = String(resolved.node(containerDepth).attrs.blockId ?? '')
    const boundary = findSplitBoundary(transaction.doc, originalId, newBlockId)
    if (boundary) {
      const { leftStart, leftNode, rightNode } = boundary
      const leftEmpty = markdownContainerIsEmpty(leftNode)
      const rightEmpty = rightNode ? markdownContainerIsEmpty(rightNode) : true
      const leftEnd = leftStart + leftNode.nodeSize
      if (leftEmpty) {
        transaction.delete(leftStart, leftEnd)
        if (rightNode) {
          transaction.setNodeMarkup(leftStart, undefined, { blockId: originalId })
          insertionPos = leftStart
          hasRightBlock = true
        } else {
          const empty = createEmptyMarkdownBlock(state, originalId)
          transaction.insert(leftStart, empty)
          insertionPos = leftStart
          hasRightBlock = true
        }
      } else if (!rightNode || rightEmpty) {
        if (rightNode) transaction.delete(leftEnd, leftEnd + rightNode.nodeSize)
        insertionPos = leftEnd
      } else {
        insertionPos = leftEnd
        hasRightBlock = true
      }
    }
  }

  const materialSize = materialNodes.reduce((sum, node) => sum + node.nodeSize, 0)
  if (insertionPos < 0 || insertionPos > transaction.doc.content.size) {
    throw new Error('当前光标位置不在连续正文中，未插入材料')
  }
  transaction.insert(insertionPos, materialNodes)
  let selectionPos = insertionPos + materialSize
  if (!hasRightBlock) {
    const empty = createEmptyMarkdownBlock(state, createMaterialId())
    transaction.insert(selectionPos, empty)
  }
  selectionPos = Math.min(selectionPos + 2, transaction.doc.content.size)
  transaction.setSelection(TextSelection.near(transaction.doc.resolve(selectionPos), 1))
  editor.view.dispatch(transaction)
  editor.commands.focus()
}

function findMarkdownContainerDepth(resolved: ReturnType<Editor['state']['doc']['resolve']>): number | null {
  for (let depth = resolved.depth; depth >= 0; depth -= 1) {
    if (resolved.node(depth).type.name === MarkdownBlock.name) return depth
  }
  return null
}

function findSplitBoundary(
  doc: ProseMirrorNode,
  originalId: string,
  newId: string,
): { leftStart: number; leftNode: ProseMirrorNode; rightNode: ProseMirrorNode | null } | null {
  let leftStart = -1
  let leftNode: ProseMirrorNode | null = null
  let rightNode: ProseMirrorNode | null = null
  doc.forEach((node, offset) => {
    if (node.type.name !== MarkdownBlock.name) return
    const blockId = String(node.attrs.blockId ?? '')
    if (blockId === originalId && leftStart < 0) {
      leftStart = offset
      leftNode = node
    } else if (blockId === newId) {
      rightNode = node
    }
  })
  if (!leftNode) return null
  return { leftStart, leftNode, rightNode }
}

function markdownContainerIsEmpty(node: ProseMirrorNode): boolean {
  return node.textContent.trim() === '' &&
    node.childCount === 1 &&
    node.child(0).isTextblock &&
    node.child(0).content.size === 0
}

function createEmptyMarkdownBlock(state: Editor['state'], blockId: string): ProseMirrorNode {
  return state.schema.nodes.markdownBlock.create(
    { blockId },
    state.schema.nodes.paragraph.create(),
  )
}

function documentHasContent(editor: Editor): boolean {
  let hasContent = false
  editor.state.doc.forEach((node) => {
    if (node.type.name === MaterialNode.name) {
      hasContent = true
      return
    }
    if (node.type.name === MarkdownBlock.name && node.textContent.trim()) hasContent = true
  })
  return hasContent
}

function selectionPoint(editor: Editor): EditorSelectionPoint {
  return { from: editor.state.selection.from, to: editor.state.selection.to }
}

function toolbarFileClass(editable: boolean) {
  return cn(
    'inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-ctl px-2 text-xs font-medium text-text-2 transition-colors hover:bg-surface focus-within:outline-2 focus-within:outline-primary-ring',
    !editable && 'pointer-events-none opacity-50',
  )
}
