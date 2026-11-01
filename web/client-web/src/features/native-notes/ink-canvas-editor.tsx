import { type PointerEvent as ReactPointerEvent, useMemo, useRef, useState } from 'react'
import { Check, Eraser, Undo2, X } from 'lucide-react'
import { Button, Card, InlineAlert } from '@/components/ui'

export interface InkPoint {
  x: number
  y: number
  pressure: number
  [key: string]: unknown
}

export interface InkStroke {
  points: InkPoint[]
  width: number
  [key: string]: unknown
}

export interface InkDocument extends Record<string, unknown> {
  width: number
  height: number
  strokes: InkStroke[]
}

interface InkCanvasEditorProps {
  initialInk?: Record<string, unknown>
  onCancel: () => void
  onSave: (ink: InkDocument, previewBytes: Uint8Array) => void
}

const DEFAULT_INK: InkDocument = { width: 320, height: 240, strokes: [] }
const MAX_STROKES = 10_000
const MAX_POINTS = 50_000
const MAX_PREVIEW_BYTES = 40 * 1024 * 1024

export function InkCanvasEditor({ initialInk, onCancel, onSave }: InkCanvasEditorProps) {
  const parsed = useMemo(() => parseInk(initialInk), [initialInk])
  const [ink, setInk] = useState(parsed.ink)
  const [activePoints, setActivePoints] = useState<InkPoint[]>([])
  const activePointsRef = useRef<InkPoint[] | null>(null)
  const [error, setError] = useState(parsed.error)
  const [saving, setSaving] = useState(false)

  function pointerPoint(event: ReactPointerEvent<SVGSVGElement>): InkPoint {
    const bounds = event.currentTarget.getBoundingClientRect()
    const renderWidth = bounds.width || ink.width
    const renderHeight = bounds.height || ink.height
    const pressure = Number.isFinite(event.pressure) && event.pressure > 0
      ? Math.min(1, event.pressure)
      : 1
    return {
      x: clamp((event.clientX - bounds.left) * ink.width / renderWidth, 0, ink.width),
      y: clamp((event.clientY - bounds.top) * ink.height / renderHeight, 0, ink.height),
      pressure,
    }
  }

  function beginStroke(event: ReactPointerEvent<SVGSVGElement>) {
    if (saving || parsed.error || !event.isPrimary) return
    event.preventDefault()
    event.currentTarget.setPointerCapture?.(event.pointerId)
    const points = [pointerPoint(event)]
    activePointsRef.current = points
    setActivePoints(points)
  }

  function continueStroke(event: ReactPointerEvent<SVGSVGElement>) {
    const points = activePointsRef.current
    if (!points || saving) return
    event.preventDefault()
    const next = [...points, pointerPoint(event)]
    activePointsRef.current = next
    setActivePoints(next)
  }

  function finishStroke() {
    const points = activePointsRef.current
    if (!points) return
    activePointsRef.current = null
    setActivePoints([])
    if (!points.length) return
    setInk((current) => ({
      ...current,
      strokes: [...current.strokes, { points, width: 2.5 }],
    }))
  }

  function cancelStroke() {
    activePointsRef.current = null
    setActivePoints([])
  }

  async function save() {
    if (saving || parsed.error) return
    setSaving(true)
    setError('')
    try {
      const validated = validateInk(ink)
      const preview = await renderInkPreview(validated)
      onSave(validated, preview)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : '手写预览生成失败')
    } finally {
      setSaving(false)
    }
  }

  const lastStroke = ink.strokes.at(-1)
  return <Card className="space-y-3 border-primary/30 p-4" data-testid="ink-canvas-editor">
    <div className="flex flex-wrap items-center gap-2">
      <div className="mr-auto">
        <h3 className="text-sm font-semibold text-text-1">手写卡片</h3>
        <p className="text-xs text-text-3">笔迹按 Android schema-v1 保存坐标、压力和画布尺寸。</p>
      </div>
      <Button size="sm" variant="ghost" disabled={!lastStroke || saving} onClick={() => {
        setInk((current) => ({ ...current, strokes: current.strokes.slice(0, -1) }))
      }} aria-label="撤销最近一笔"><Undo2 className="size-4" aria-hidden />撤销</Button>
      <Button size="sm" variant="ghost" disabled={!ink.strokes.length || saving} onClick={() => {
        setInk((current) => ({ ...current, strokes: [] }))
      }} aria-label="清空手写卡片"><Eraser className="size-4" aria-hidden />清空</Button>
    </div>

    <svg
      className="block w-full touch-none rounded-ctl border border-border bg-white"
      data-testid="ink-canvas"
      role="img"
      aria-label="手写画布"
      viewBox={`0 0 ${ink.width} ${ink.height}`}
      onPointerDown={beginStroke}
      onPointerMove={continueStroke}
      onPointerUp={finishStroke}
      onPointerCancel={cancelStroke}
      onLostPointerCapture={finishStroke}
    >
      <rect width={ink.width} height={ink.height} fill="#fff" />
      {ink.strokes.map((stroke, index) => <InkStrokeView key={index} stroke={stroke} />)}
      {activePoints.length > 0 && <InkStrokeView stroke={{ points: activePoints, width: 2.5 }} />}
    </svg>

    {parsed.error && <InlineAlert tone="crit" title="手写数据无法安全编辑">{parsed.error}</InlineAlert>}
    {error && !parsed.error && <InlineAlert tone="crit" title="未保存">{error}</InlineAlert>}
    <div className="flex flex-wrap justify-end gap-2">
      <Button size="sm" variant="ghost" disabled={saving} onClick={onCancel}><X className="size-4" aria-hidden />取消</Button>
      <Button size="sm" variant="primary" loading={saving} disabled={Boolean(parsed.error)} onClick={() => void save()}>
        <Check className="size-4" aria-hidden />保存手写
      </Button>
    </div>
  </Card>
}

function InkStrokeView({ stroke }: { stroke: InkStroke }) {
  const first = stroke.points[0]
  if (!first) return null
  if (stroke.points.length === 1) {
    return <circle cx={first.x} cy={first.y} r={stroke.width / 2} fill="#263b85" />
  }
  const path = stroke.points.map((point, index) => `${index ? 'L' : 'M'} ${point.x} ${point.y}`).join(' ')
  return <path d={path} fill="none" stroke="#263b85" strokeWidth={stroke.width} strokeLinecap="round" strokeLinejoin="round" />
}

function parseInk(value?: Record<string, unknown>): { ink: InkDocument; error: string } {
  if (!value) return { ink: { ...DEFAULT_INK, strokes: [] }, error: '' }
  try {
    return { ink: validateInk(value), error: '' }
  } catch (failure) {
    return {
      ink: { ...DEFAULT_INK, strokes: [] },
      error: failure instanceof Error ? failure.message : '手写数据格式无效',
    }
  }
}

function validateInk(value: Record<string, unknown>): InkDocument {
  const width = finiteNumber(value.width, '画布宽度')
  const height = finiteNumber(value.height, '画布高度')
  if (width < 80 || width > 2048 || height < 80 || height > 4096) {
    throw new Error('画布尺寸超出 schema-v1 范围')
  }
  if (!Array.isArray(value.strokes) || value.strokes.length > MAX_STROKES) {
    throw new Error('笔画数量无效')
  }
  let pointCount = 0
  const strokes = value.strokes.map((rawStroke): InkStroke => {
    if (!isRecord(rawStroke) || !Array.isArray(rawStroke.points) || rawStroke.points.length === 0) {
      throw new Error('笔画必须包含坐标点')
    }
    const strokeWidth = rawStroke.width === undefined ? 2.5 : finiteNumber(rawStroke.width, '笔画宽度')
    if (strokeWidth < 0.5 || strokeWidth > 24) throw new Error('笔画宽度超出 schema-v1 范围')
    pointCount += rawStroke.points.length
    if (pointCount > MAX_POINTS) throw new Error('笔迹点数超过 Web 编辑上限')
    const points = rawStroke.points.map((rawPoint): InkPoint => {
      if (!isRecord(rawPoint)) throw new Error('笔迹坐标点格式无效')
      const x = finiteNumber(rawPoint.x, '笔迹 X 坐标')
      const y = finiteNumber(rawPoint.y, '笔迹 Y 坐标')
      const pressure = rawPoint.pressure === undefined ? 1 : finiteNumber(rawPoint.pressure, '笔迹压力')
      if (x < 0 || x > width || y < 0 || y > height || pressure < 0 || pressure > 1) {
        throw new Error('笔迹坐标或压力超出 schema-v1 范围')
      }
      return { ...rawPoint, x, y, pressure }
    })
    return { ...rawStroke, points, width: strokeWidth }
  })
  return { ...value, width, height, strokes }
}

async function renderInkPreview(ink: InkDocument): Promise<Uint8Array> {
  const canvas = document.createElement('canvas')
  canvas.width = Math.ceil(ink.width)
  canvas.height = Math.ceil(ink.height)
  const context = canvas.getContext('2d')
  if (!context) throw new Error('浏览器无法创建手写预览画布')
  context.fillStyle = '#fff'
  context.fillRect(0, 0, canvas.width, canvas.height)
  context.strokeStyle = '#263b85'
  context.fillStyle = '#263b85'
  context.lineCap = 'round'
  context.lineJoin = 'round'
  for (const stroke of ink.strokes) {
    const first = stroke.points[0]!
    context.beginPath()
    context.lineWidth = stroke.width
    if (stroke.points.length === 1) {
      context.arc(first.x, first.y, stroke.width / 2, 0, Math.PI * 2)
      context.fill()
    } else {
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

function finiteNumber(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${label}无效`)
  return value
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.max(minimum, Math.min(maximum, value))
}
