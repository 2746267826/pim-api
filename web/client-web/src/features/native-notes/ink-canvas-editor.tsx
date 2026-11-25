import {
  type PointerEvent as ReactPointerEvent,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import { Check, Eraser, PenLine, Undo2, X } from 'lucide-react'
import { Button, Card, InlineAlert } from '@/components/ui'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import {
  BLUE_COLOR,
  ERASER_RADIUS,
  GREEN_COLOR,
  INK_COLOR,
  MEDIUM_WIDTH,
  RED_COLOR,
  THICK_WIDTH,
  THIN_WIDTH,
  createStrokeId,
  eraseInk,
  parseInkDocument,
  replaceInkStrokes,
  type InkDocument,
  type InkEraserMode,
  type InkPoint,
  type InkStroke,
  type InkStrokeStyle,
} from './ink-document'

export type { InkDocument, InkPoint, InkStroke } from './ink-document'

interface InkCanvasEditorProps {
  initialInk?: Record<string, unknown>
  disabled?: boolean
  isSessionActive?: () => boolean
  onPendingChange?: (pending: boolean) => void
  onDirty?: () => void
  onCancel: () => void
  onSave: (ink: InkDocument, previewBytes: Uint8Array) => void
}

interface BrushPreference extends InkStrokeStyle {}

interface ViewBox {
  x: number
  y: number
  width: number
  height: number
}

type InkTool = 'brush' | 'eraser'

type ActiveGesture =
  | {
    kind: 'draw'
    pointerId: number
    points: InkPoint[]
    style: InkStrokeStyle
  }
  | {
    kind: 'erase'
    pointerId: number
    mode: InkEraserMode
    baseInk: InkDocument
    points: InkPoint[]
    changed: boolean
  }
  | {
    kind: 'pan'
    pointerId: number
    startClientX: number
    startClientY: number
    startViewBox: ViewBox
    scaleX: number
    scaleY: number
  }

const DEFAULT_INK: InkDocument = {
  width: 320,
  height: 240,
  strokes: [],
  strokeIds: [],
  strokeStyles: [],
}
const MAX_PREVIEW_BYTES = 40 * 1024 * 1024
const MIN_ZOOM_WIDTH = 48
const MAX_ZOOM_MULTIPLE = 4

const BRUSH_COLORS = [
  { label: '墨色', color: INK_COLOR },
  { label: '红', color: RED_COLOR },
  { label: '蓝', color: BLUE_COLOR },
  { label: '绿', color: GREEN_COLOR },
] as const

const BRUSH_WIDTHS = [
  { label: '细', width: THIN_WIDTH },
  { label: '中', width: MEDIUM_WIDTH },
  { label: '粗', width: THICK_WIDTH },
] as const

const DEFAULT_BRUSH: BrushPreference = { color: INK_COLOR, width: MEDIUM_WIDTH }

export function InkCanvasEditor({
  initialInk,
  disabled = false,
  isSessionActive = () => true,
  onPendingChange,
  onDirty,
  onCancel,
  onSave,
}: InkCanvasEditorProps) {
  const parsed = useMemo(() => parseInk(initialInk), [initialInk])
  const [ink, setInkState] = useState<InkDocument>(parsed.ink)
  const inkRef = useRef(ink)
  const [initialUndo, setInitialUndo] = useState<InkDocument | null>(() => {
    if (!parsed.ink.strokes.length) return null
    return replaceInkStrokes(
      parsed.ink,
      parsed.ink.strokes.slice(0, -1),
      parsed.ink.strokeIds.slice(0, -1),
    )
  })
  const [undoStack, setUndoStack] = useState<InkDocument[]>([])
  const editedRef = useRef(false)
  const [viewBox, setViewBox] = useState<ViewBox>(() => ({
    x: 0,
    y: 0,
    width: parsed.ink.width,
    height: parsed.ink.height,
  }))
  const [activePoints, setActivePoints] = useState<InkPoint[]>([])
  const [eraserPoint, setEraserPoint] = useState<InkPoint | null>(null)
  const [gestureActive, setGestureActive] = useState(false)
  const gestureRef = useRef<ActiveGesture | null>(null)
  const [tool, setTool] = useState<InkTool>('brush')
  const [eraserMode, setEraserMode] = useState<InkEraserMode>('partial')
  const [brush, setBrush] = useState<BrushPreference>(readBrushPreference)
  const brushRef = useRef(brush)
  const [brushPanelOpen, setBrushPanelOpen] = useState(false)
  const [eraserPanelOpen, setEraserPanelOpen] = useState(false)
  const [error, setError] = useState(parsed.error)
  const [saving, setSaving] = useState(false)
  const svgRef = useRef<SVGSVGElement | null>(null)
  const mountedRef = useRef(true)
  const disabledRef = useRef(disabled)
  const sessionActiveRef = useRef(isSessionActive)
  const pendingChangeRef = useRef(onPendingChange)

  useLayoutEffect(() => {
    inkRef.current = ink
  }, [ink])

  useLayoutEffect(() => {
    brushRef.current = brush
  }, [brush])

  useLayoutEffect(() => {
    disabledRef.current = disabled
    sessionActiveRef.current = isSessionActive
    pendingChangeRef.current = onPendingChange
  }, [disabled, isSessionActive, onPendingChange])

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      pendingChangeRef.current?.(false)
    }
  }, [])

  useEffect(() => {
    const node = svgRef.current
    if (!node) return
    const handler = (event: WheelEvent) => {
      if (!mountedRef.current || disabledRef.current || !sessionActiveRef.current() || saving) return
      event.preventDefault()
      const factor = Math.exp(event.deltaY * 0.0015)
      const bounds = node.getBoundingClientRect()
      setViewBox((current) => {
        const nextWidth = clamp(
          current.width * factor,
          Math.min(MIN_ZOOM_WIDTH, ink.width),
          ink.width * MAX_ZOOM_MULTIPLE,
        )
        const nextHeight = nextWidth * current.height / current.width
        const renderWidth = bounds.width || current.width
        const renderHeight = bounds.height || current.height
        const centerX = current.x + (event.clientX - bounds.left) * current.width / renderWidth
        const centerY = current.y + (event.clientY - bounds.top) * current.height / renderHeight
        return {
          x: centerX - (centerX - current.x) * nextWidth / current.width,
          y: centerY - (centerY - current.y) * nextHeight / current.height,
          width: nextWidth,
          height: nextHeight,
        }
      })
    }
    node.addEventListener('wheel', handler, { passive: false })
    return () => node.removeEventListener('wheel', handler)
  }, [ink.width, saving])

  useEffect(() => {
    if (!brushPanelOpen && !eraserPanelOpen) return
    const closePanels = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      setBrushPanelOpen(false)
      setEraserPanelOpen(false)
    }
    window.addEventListener('keydown', closePanels)
    return () => window.removeEventListener('keydown', closePanels)
  }, [brushPanelOpen, eraserPanelOpen])

  function sessionIsActive() {
    return mountedRef.current && !disabledRef.current && sessionActiveRef.current()
  }

  function canEdit() {
    return sessionIsActive() && !saving && !parsed.error
  }

  function commitInk(next: InkDocument) {
    inkRef.current = next
    setInkState(next)
  }

  function recordUndo(previous: InkDocument) {
    if (!editedRef.current) {
      editedRef.current = true
      setInitialUndo(null)
      setUndoStack([previous])
      return
    }
    setUndoStack((current) => [...current, previous])
  }

  function pointerPoint(
    event: ReactPointerEvent<SVGSVGElement>,
    currentViewBox: ViewBox = viewBox,
  ): InkPoint {
    const bounds = event.currentTarget.getBoundingClientRect()
    const renderWidth = bounds.width || currentViewBox.width
    const renderHeight = bounds.height || currentViewBox.height
    const pressure = Number.isFinite(event.pressure) && event.pressure > 0
      ? Math.min(1, event.pressure)
      : 1
    return {
      x: clamp(
        currentViewBox.x + (event.clientX - bounds.left) * currentViewBox.width / renderWidth,
        0,
        ink.width,
      ),
      y: clamp(
        currentViewBox.y + (event.clientY - bounds.top) * currentViewBox.height / renderHeight,
        0,
        ink.height,
      ),
      pressure,
    }
  }

  function beginDraw(event: ReactPointerEvent<SVGSVGElement>) {
    const points = [pointerPoint(event)]
    gestureRef.current = {
      kind: 'draw',
      pointerId: event.pointerId,
      points,
      style: { ...brushRef.current },
    }
    setGestureActive(true)
    setActivePoints(points)
    pendingChangeRef.current?.(true)
  }

  function beginErase(event: ReactPointerEvent<SVGSVGElement>) {
    const point = pointerPoint(event)
    const baseInk = inkRef.current
    const result = eraseInk(baseInk, eraserMode, [point])
    gestureRef.current = {
      kind: 'erase',
      pointerId: event.pointerId,
      mode: eraserMode,
      baseInk,
      points: [point],
      changed: result.changed,
    }
    if (result.changed) commitInk(result.ink)
    setGestureActive(true)
    setEraserPoint(point)
    pendingChangeRef.current?.(true)
  }

  function beginPan(event: ReactPointerEvent<SVGSVGElement>) {
    const bounds = event.currentTarget.getBoundingClientRect()
    gestureRef.current = {
      kind: 'pan',
      pointerId: event.pointerId,
      startClientX: event.clientX,
      startClientY: event.clientY,
      startViewBox: viewBox,
      scaleX: bounds.width ? viewBox.width / bounds.width : 1,
      scaleY: bounds.height ? viewBox.height / bounds.height : 1,
    }
    setGestureActive(true)
  }

  function beginGesture(event: ReactPointerEvent<SVGSVGElement>) {
    if (!canEdit() || gestureRef.current) return
    setBrushPanelOpen(false)
    setEraserPanelOpen(false)
    if (event.button === 1) {
      event.preventDefault()
      event.currentTarget.setPointerCapture?.(event.pointerId)
      beginPan(event)
      return
    }
    if (event.button !== 0 || !event.isPrimary) return
    event.preventDefault()
    event.currentTarget.setPointerCapture?.(event.pointerId)
    if (tool === 'eraser') beginErase(event)
    else beginDraw(event)
  }

  function continueGesture(event: ReactPointerEvent<SVGSVGElement>) {
    const gesture = gestureRef.current
    if (!gesture || gesture.pointerId !== event.pointerId) return
    if (gesture.kind === 'pan') {
      event.preventDefault()
      const next: ViewBox = {
        ...gesture.startViewBox,
        x: gesture.startViewBox.x - (event.clientX - gesture.startClientX) * gesture.scaleX,
        y: gesture.startViewBox.y - (event.clientY - gesture.startClientY) * gesture.scaleY,
      }
      setViewBox(next)
      return
    }
    if (!canEdit()) return
    event.preventDefault()
    const point = pointerPoint(event)
    if (gesture.kind === 'draw') {
      const points = [...gesture.points, point]
      gesture.points = points
      setActivePoints(points)
      return
    }
    gesture.points = [...gesture.points, point]
    setEraserPoint(point)
    const result = eraseInk(gesture.baseInk, gesture.mode, gesture.points)
    if (result.changed) {
      gesture.changed = true
      commitInk(result.ink)
    }
  }

  function finishGesture(event?: ReactPointerEvent<SVGSVGElement>) {
    const gesture = gestureRef.current
    if (!gesture || (event && event.pointerId !== gesture.pointerId)) return
    gestureRef.current = null
    setGestureActive(false)
    setActivePoints([])
    setEraserPoint(null)
    if (gesture.kind === 'draw' || gesture.kind === 'erase') {
      pendingChangeRef.current?.(false)
    }

    if (gesture.kind === 'draw') {
      if (!gesture.points.length || !sessionIsActive()) return
      const previous = inkRef.current
      const next = replaceInkStrokes(
        previous,
        [...previous.strokes, { points: gesture.points, color: gesture.style.color, width: gesture.style.width }],
        [...previous.strokeIds, createStrokeId()],
      )
      recordUndo(previous)
      commitInk(next)
      onDirty?.()
      return
    }

    if (gesture.kind === 'erase') {
      if (!gesture.changed) return
      if (!sessionIsActive()) {
        commitInk(gesture.baseInk)
        return
      }
      recordUndo(gesture.baseInk)
      onDirty?.()
    }
  }

  function cancelGesture(event: ReactPointerEvent<SVGSVGElement>) {
    const gesture = gestureRef.current
    if (!gesture || gesture.pointerId !== event.pointerId) return
    if (gesture.kind === 'erase') {
      finishGesture(event)
      return
    }
    gestureRef.current = null
    setGestureActive(false)
    setActivePoints([])
    setEraserPoint(null)
    if (gesture.kind === 'draw') pendingChangeRef.current?.(false)
  }

  function undo() {
    if (!canEdit() || gestureRef.current) return
    const previous = undoStack.at(-1) ?? initialUndo
    if (!previous) return
    if (undoStack.length) setUndoStack((current) => current.slice(0, -1))
    else setInitialUndo(null)
    commitInk(previous)
    onDirty?.()
  }

  function clearInk() {
    if (!inkRef.current.strokes.length || !canEdit() || gestureRef.current) return
    const previous = inkRef.current
    recordUndo(previous)
    commitInk(replaceInkStrokes(previous, [], []))
    onDirty?.()
  }

  function updateBrush(next: BrushPreference) {
    const preference = { color: next.color, width: next.width }
    setBrush(preference)
    setJSON(STORAGE_KEYS.inkBrushPreference, preference)
  }

  async function save() {
    if (!canEdit() || gestureRef.current) return
    setSaving(true)
    setError('')
    try {
      const validated = parseInkDocument(inkRef.current)
      const preview = await renderInkPreview(validated)
      if (!sessionIsActive()) return
      onSave(validated, preview)
    } catch (failure) {
      if (sessionIsActive()) setError(failure instanceof Error ? failure.message : '手写预览生成失败')
    } finally {
      if (mountedRef.current) setSaving(false)
    }
  }

  const activeSession = !disabled && isSessionActive()
  const canUseUndo = (undoStack.length > 0 || Boolean(initialUndo)) && activeSession && !saving && !gestureActive
  const currentBrush = BRUSH_COLORS.find((item) => item.color === brush.color)
  return <Card className="relative space-y-3 border-primary/30 p-4" data-testid="ink-canvas-editor" aria-disabled={disabled}>
    <div className="flex flex-wrap items-center gap-2">
      <div className="mr-auto">
        <h3 className="text-sm font-semibold text-text-1">手写卡片</h3>
        <p className="text-xs text-text-3">逐笔保存颜色、粗细、坐标与压力；中键平移，滚轮缩放。</p>
      </div>

      <div className="relative">
        <Button
          size="sm"
          variant={tool === 'brush' ? 'secondary' : 'ghost'}
          disabled={!activeSession || saving || Boolean(parsed.error) || gestureActive}
          aria-pressed={tool === 'brush'}
          aria-expanded={brushPanelOpen}
          onClick={() => {
            setTool('brush')
            setBrushPanelOpen((open) => !open)
            setEraserPanelOpen(false)
          }}
        >
          <span className="size-2.5 rounded-full border border-black/10" style={{ backgroundColor: brush.color }} aria-hidden />
          <PenLine className="size-4" aria-hidden />画笔
        </Button>
        {brushPanelOpen && <div
          role="dialog"
          aria-label="画笔设置"
          className="absolute right-0 top-10 z-20 w-72 space-y-3 rounded-ctl border border-border bg-bg p-3 shadow-card"
        >
          <div>
            <p className="mb-1.5 text-xs font-medium text-text-2">墨色</p>
            <div className="grid grid-cols-4 gap-1.5">
              {BRUSH_COLORS.map((option) => <Button
                key={option.color}
                size="sm"
                variant={brush.color === option.color ? 'secondary' : 'ghost'}
                className="px-1.5"
                aria-label={option.label}
                aria-pressed={brush.color === option.color}
                onClick={() => updateBrush({ ...brush, color: option.color })}
              >
                <span className="size-3 rounded-full border border-black/10" style={{ backgroundColor: option.color }} aria-hidden />
                <span aria-hidden>{option.label}</span>
              </Button>)}
            </div>
          </div>
          <div>
            <p className="mb-1.5 text-xs font-medium text-text-2">粗细</p>
            <div className="grid grid-cols-3 gap-1.5">
              {BRUSH_WIDTHS.map((option) => <Button
                key={option.width}
                size="sm"
                variant={brush.width === option.width ? 'secondary' : 'ghost'}
                aria-label={option.label}
                aria-pressed={brush.width === option.width}
                onClick={() => updateBrush({ ...brush, width: option.width })}
              >
                {option.label}
              </Button>)}
            </div>
          </div>
          <div className="flex items-center justify-between gap-2 border-t border-border pt-2">
            <span className="flex items-center gap-1.5 text-[11px] text-text-3">
              当前：{currentBrush?.label ?? '自定义'} · {brush.width}
            </span>
            <Button size="sm" variant="primary" onClick={() => setBrushPanelOpen(false)}>完成设置</Button>
          </div>
        </div>}
      </div>

      <div className="relative">
        <Button
          size="sm"
          variant={tool === 'eraser' ? 'secondary' : 'ghost'}
          disabled={!activeSession || saving || Boolean(parsed.error) || gestureActive}
          aria-pressed={tool === 'eraser'}
          aria-expanded={eraserPanelOpen}
          onClick={() => {
            setTool('eraser')
            setEraserPanelOpen((open) => !open)
            setBrushPanelOpen(false)
          }}
        >
          <Eraser className="size-4" aria-hidden />橡皮
        </Button>
        {eraserPanelOpen && <div
          role="dialog"
          aria-label="橡皮设置"
          className="absolute right-0 top-10 z-20 w-56 space-y-1.5 rounded-ctl border border-border bg-bg p-2 shadow-card"
        >
          <Button
            size="sm"
            variant={eraserMode === 'partial' ? 'secondary' : 'ghost'}
            className="w-full justify-start"
            aria-pressed={eraserMode === 'partial'}
            onClick={() => {
              setTool('eraser')
              setEraserMode('partial')
            }}
          >
            局部擦除
          </Button>
          <Button
            size="sm"
            variant={eraserMode === 'whole' ? 'secondary' : 'ghost'}
            className="w-full justify-start"
            aria-pressed={eraserMode === 'whole'}
            onClick={() => {
              setTool('eraser')
              setEraserMode('whole')
            }}
          >
            整笔擦除
          </Button>
          <p className="px-1 pt-1 text-[11px] leading-4 text-text-3">
            局部擦除切断笔迹并保留两侧；整笔擦除命中即删除。
          </p>
        </div>}
      </div>

      <Button
        size="sm"
        variant="ghost"
        disabled={!canUseUndo}
        onClick={undo}
        aria-label="撤销最近一笔"
      >
        <Undo2 className="size-4" aria-hidden />撤销
      </Button>
      <Button
        size="sm"
        variant="ghost"
        disabled={!ink.strokes.length || !activeSession || saving || gestureActive}
        onClick={clearInk}
        aria-label="清空手写卡片"
      >
        <Eraser className="size-4" aria-hidden />清空
      </Button>
    </div>

    <svg
      ref={svgRef}
      className="block w-full touch-none rounded-ctl border border-border bg-white"
      data-testid="ink-canvas"
      role="img"
      aria-label="手写画布"
      aria-disabled={disabled}
      viewBox={`${viewBox.x} ${viewBox.y} ${viewBox.width} ${viewBox.height}`}
      style={{ pointerEvents: disabled ? 'none' : undefined, cursor: 'crosshair' }}
      onPointerDown={beginGesture}
      onPointerMove={continueGesture}
      onPointerUp={finishGesture}
      onPointerCancel={cancelGesture}
      onLostPointerCapture={finishGesture}
    >
      <rect width={ink.width} height={ink.height} fill="#fff" />
      {ink.strokes.map((stroke, index) => <InkStrokeView
        key={ink.strokeIds[index] ?? index}
        stroke={stroke}
        strokeId={ink.strokeIds[index]}
      />)}
      {activePoints.length > 0 && <InkStrokeView
        stroke={{ points: activePoints, color: brush.color, width: brush.width }}
      />}
      {eraserPoint && <circle
        cx={eraserPoint.x}
        cy={eraserPoint.y}
        r={ERASER_RADIUS}
        fill="none"
        stroke="#64748b"
        strokeWidth={Math.max(1, viewBox.width / ink.width)}
        strokeDasharray="4 3"
        pointerEvents="none"
      />}
    </svg>

    <div className="flex flex-wrap items-center gap-2 text-[11px] text-text-3">
      <span>{ink.strokes.length} 笔</span>
      <span aria-hidden>·</span>
      <span>中键拖动平移，滚轮缩放</span>
      <Button
        size="sm"
        variant="ghost"
        className="ml-auto"
        disabled={viewBox.x === 0 && viewBox.y === 0 && viewBox.width === ink.width && viewBox.height === ink.height}
        onClick={() => setViewBox({ x: 0, y: 0, width: ink.width, height: ink.height })}
      >
        复位视图
      </Button>
    </div>

    {parsed.error && <InlineAlert tone="crit" title="手写数据无法安全编辑">{parsed.error}</InlineAlert>}
    {error && !parsed.error && <InlineAlert tone="crit" title="未保存">{error}</InlineAlert>}
    <div className="flex flex-wrap justify-end gap-2">
      <Button size="sm" variant="ghost" disabled={!activeSession || gestureActive} onClick={() => {
        if (sessionIsActive()) onCancel()
      }}><X className="size-4" aria-hidden />取消</Button>
      <Button
        size="sm"
        variant="primary"
        loading={saving}
        disabled={Boolean(parsed.error) || !activeSession || gestureActive}
        onClick={() => void save()}
      >
        <Check className="size-4" aria-hidden />保存手写
      </Button>
    </div>
  </Card>
}

function InkStrokeView({ stroke, strokeId }: { stroke: InkStroke; strokeId?: string }) {
  const first = stroke.points[0]
  if (!first) return null
  if (stroke.points.length === 1) {
    return <circle
      data-stroke-id={strokeId}
      cx={first.x}
      cy={first.y}
      r={stroke.width / 2}
      fill={stroke.color}
    />
  }
  const path = stroke.points.map((point, index) => `${index ? 'L' : 'M'} ${point.x} ${point.y}`).join(' ')
  return <path
    data-stroke-id={strokeId}
    d={path}
    fill="none"
    stroke={stroke.color}
    strokeWidth={stroke.width}
    strokeLinecap="round"
    strokeLinejoin="round"
  />
}

function parseInk(value?: Record<string, unknown>): { ink: InkDocument; error: string } {
  if (!value) return { ink: { ...DEFAULT_INK, strokes: [], strokeIds: [], strokeStyles: [] }, error: '' }
  try {
    return { ink: parseInkDocument(value), error: '' }
  } catch (failure) {
    return {
      ink: { ...DEFAULT_INK, strokes: [], strokeIds: [], strokeStyles: [] },
      error: failure instanceof Error ? failure.message : '手写数据格式无效',
    }
  }
}

async function renderInkPreview(ink: InkDocument): Promise<Uint8Array> {
  const canvas = document.createElement('canvas')
  canvas.width = Math.ceil(ink.width)
  canvas.height = Math.ceil(ink.height)
  const context = canvas.getContext('2d')
  if (!context) throw new Error('浏览器无法创建手写预览画布')
  context.fillStyle = '#fff'
  context.fillRect(0, 0, canvas.width, canvas.height)
  context.lineCap = 'round'
  context.lineJoin = 'round'
  for (const stroke of ink.strokes) {
    const first = stroke.points[0]!
    context.beginPath()
    context.lineWidth = stroke.width
    if (stroke.points.length === 1) {
      context.fillStyle = stroke.color
      context.arc(first.x, first.y, stroke.width / 2, 0, Math.PI * 2)
      context.fill()
    } else {
      context.strokeStyle = stroke.color
      context.moveTo(first.x, first.y)
      for (const point of stroke.points.slice(1)) context.lineTo(point.x, point.y)
      context.stroke()
    }
  }
  const blob = await new Promise<Blob>((resolve, reject) => {
    canvas.toBlob((result) => result ? resolve(result) : reject(new Error('PNG 手写预览生成失败')), 'image/png')
  })
  if (!blob.size || blob.size > MAX_PREVIEW_BYTES) throw new Error('手写预览为空或超过 40 MiB')
  return new Uint8Array(await blob.arrayBuffer())
}

function readBrushPreference(): BrushPreference {
  const saved = getJSON<unknown>(STORAGE_KEYS.inkBrushPreference, null)
  if (!isRecord(saved)) return { ...DEFAULT_BRUSH }
  const color = BRUSH_COLORS.find((option) => option.color === saved.color)?.color
  const width = BRUSH_WIDTHS.find((option) => option.width === saved.width)?.width
  if (!color || width === undefined) return { ...DEFAULT_BRUSH }
  return { color, width }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.max(minimum, Math.min(maximum, value))
}
