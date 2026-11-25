export interface InkPoint {
  x: number
  y: number
  pressure: number
  [key: string]: unknown
}

export interface InkStrokeStyle {
  color: string
  width: number
  [key: string]: unknown
}

export interface InkStroke extends InkStrokeStyle {
  points: InkPoint[]
  [key: string]: unknown
}

export interface InkDocument extends Record<string, unknown> {
  width: number
  height: number
  strokes: InkStroke[]
  strokeIds: string[]
  strokeStyles: InkStrokeStyle[]
}

export type InkEraserMode = 'partial' | 'whole'

export const LEGACY_INK_COLOR = '#263b85'
export const INK_COLOR = '#252b35'
export const RED_COLOR = '#b8373c'
export const BLUE_COLOR = '#245fba'
export const GREEN_COLOR = '#216e4e'
export const THIN_WIDTH = 1.8
export const MEDIUM_WIDTH = 3.4
export const THICK_WIDTH = 6
export const DEFAULT_STROKE_WIDTH = 2.5
export const MAX_STROKES = 10_000
export const MAX_POINTS = 50_000
export const ERASER_RADIUS = 12

const EPSILON = 1e-9
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const COLOR_PATTERN = /^#[0-9a-f]{6}$/i

export function parseInkDocument(value: Record<string, unknown>): InkDocument {
  const width = finiteNumber(value.width, '画布宽度')
  const height = finiteNumber(value.height, '画布高度')
  if (width < 80 || width > 2048 || height < 80 || height > 4096) {
    throw new Error('画布尺寸超出 schema-v1 范围')
  }
  if (!Array.isArray(value.strokes) || value.strokes.length > MAX_STROKES) {
    throw new Error('笔画数量无效')
  }

  const rawStrokeIds = value.strokeIds
  if (rawStrokeIds !== undefined && (!Array.isArray(rawStrokeIds) || rawStrokeIds.length !== value.strokes.length)) {
    throw new Error('笔迹身份无效：数量必须与笔画一致')
  }
  const rawStrokeStyles = value.strokeStyles
  if (rawStrokeStyles !== undefined && (!Array.isArray(rawStrokeStyles) || rawStrokeStyles.length !== value.strokes.length)) {
    throw new Error('笔迹样式无效：数量必须与笔画一致')
  }

  let pointCount = 0
  const strokeStyles: InkStrokeStyle[] = []
  const strokes = value.strokes.map((rawStroke, index): InkStroke => {
    if (!isRecord(rawStroke) || !Array.isArray(rawStroke.points) || rawStroke.points.length === 0) {
      throw new Error('笔画必须包含坐标点')
    }
    const inlineWidth = rawStroke.width === undefined
      ? undefined
      : finiteNumber(rawStroke.width, '笔画宽度')
    if (inlineWidth !== undefined && (inlineWidth < 0.5 || inlineWidth > 24)) {
      throw new Error('笔画宽度超出 schema-v1 范围')
    }
    const inlineColor = rawStroke.color === undefined
      ? undefined
      : normalizeColor(rawStroke.color)

    let strokeWidth = inlineWidth ?? DEFAULT_STROKE_WIDTH
    let strokeColor = inlineColor ?? LEGACY_INK_COLOR
    let strokeStyle: InkStrokeStyle | undefined
    if (rawStrokeStyles !== undefined) {
      const rawStyle = rawStrokeStyles[index]
      if (!isRecord(rawStyle)) throw new Error('笔迹样式必须与笔画一一对应')
      const styleColor = normalizeColor(rawStyle.color)
      const styleWidth = finiteNumber(rawStyle.width, '笔画宽度')
      if (styleWidth < 0.5 || styleWidth > 24) throw new Error('笔画宽度超出 schema-v1 范围')
      if (inlineColor !== undefined && inlineColor !== styleColor) {
        throw new Error('笔迹颜色与样式数组不一致')
      }
      if (inlineWidth !== undefined && Math.abs(inlineWidth - styleWidth) > EPSILON) {
        throw new Error('笔画宽度与样式数组不一致')
      }
      strokeColor = styleColor
      strokeWidth = styleWidth
      strokeStyle = { ...rawStyle, color: styleColor, width: styleWidth }
    }
    strokeStyles.push(strokeStyle ?? { color: strokeColor, width: strokeWidth })

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
    return { ...rawStroke, points, width: strokeWidth, color: strokeColor }
  })

  const usedIds = new Set<string>()
  const strokeIds = strokes.map((_, index): string => {
    if (rawStrokeIds === undefined) {
      let id = createStrokeId()
      while (usedIds.has(id)) id = createStrokeId()
      usedIds.add(id)
      return id
    }
    const rawId = rawStrokeIds[index]
    if (typeof rawId !== 'string' || !UUID_PATTERN.test(rawId)) {
      throw new Error('笔迹身份无效：必须与笔画一一对应且不可重复')
    }
    const id = rawId.toLowerCase()
    if (usedIds.has(id)) throw new Error('笔迹身份无效：必须与笔画一一对应且不可重复')
    usedIds.add(id)
    return id
  })

  return {
    ...value,
    width,
    height,
    strokes,
    strokeIds,
    strokeStyles,
  }
}

export function replaceInkStrokes(
  ink: InkDocument,
  strokes: InkStroke[],
  strokeIds: string[],
  strokeStyles?: readonly InkStrokeStyle[],
): InkDocument {
  if (strokeIds.length !== strokes.length) {
    throw new Error('笔迹身份无效：数量必须与笔画一致')
  }
  const nextStyles = strokeStyles ?? strokes.map((stroke, index) => {
    const existing = ink.strokeStyles[index]
    if (existing && existing.color === stroke.color && Math.abs(existing.width - stroke.width) <= EPSILON) {
      return { ...existing, color: stroke.color, width: stroke.width }
    }
    return { color: stroke.color, width: stroke.width }
  })
  if (nextStyles.length !== strokes.length) {
    throw new Error('笔迹样式无效：数量必须与笔画一致')
  }
  return {
    ...ink,
    strokes,
    strokeIds,
    strokeStyles: [...nextStyles],
  }
}

export function eraseInk(
  ink: InkDocument,
  mode: InkEraserMode,
  eraserPoints: InkPoint[],
  radius = ERASER_RADIUS,
): { ink: InkDocument; changed: boolean } {
  if (!eraserPoints.length) return { ink, changed: false }
  const nextStrokes: InkStroke[] = []
  const nextIds: string[] = []
  const nextStyles: InkStrokeStyle[] = []
  let changed = false
  const eraserSegments = pathSegments(eraserPoints)
  const usedIds = new Set(ink.strokeIds.map((id) => id.toLowerCase()))

  ink.strokes.forEach((stroke, strokeIndex) => {
    let strokeId = ink.strokeIds[strokeIndex]
    if (!strokeId) {
      do {
        strokeId = createStrokeId()
      } while (usedIds.has(strokeId))
      usedIds.add(strokeId)
    }
    const hitRadius = radius + stroke.width / 2
    const strokeStyle = ink.strokeStyles[strokeIndex] ?? { color: stroke.color, width: stroke.width }
    if (mode === 'whole') {
      if (!strokeTouchesPath(stroke, eraserSegments, hitRadius)) {
        nextStrokes.push(stroke)
        nextIds.push(strokeId)
        nextStyles.push(strokeStyle)
      } else {
        changed = true
      }
      return
    }

    const pieces = splitStroke(stroke, eraserSegments, hitRadius)
    if (pieces.length === 1 && samePoints(pieces[0]!, stroke.points)) {
      nextStrokes.push(stroke)
      nextIds.push(strokeId)
      nextStyles.push(strokeStyle)
      return
    }
    changed = true
    pieces.forEach((points, pieceIndex) => {
      nextStrokes.push({ ...stroke, points })
      nextIds.push(pieceIndex === 0 ? strokeId : deriveUniqueStrokeId(strokeId, pieceIndex, usedIds))
      nextStyles.push(strokeStyle)
    })
  })

  if (!changed) return { ink, changed: false }
  return { ink: replaceInkStrokes(ink, nextStrokes, nextIds, nextStyles), changed: true }
}

export function createStrokeId(): string {
  const crypto = globalThis.crypto
  if (typeof crypto?.randomUUID === 'function') return crypto.randomUUID().toLowerCase()
  if (typeof crypto?.getRandomValues === 'function') {
    const bytes = crypto.getRandomValues(new Uint8Array(16))
    bytes[6] = (bytes[6]! & 0x0f) | 0x40
    bytes[8] = (bytes[8]! & 0x3f) | 0x80
    return uuidFromBytes(bytes)
  }
  const bytes = new Uint8Array(16)
  for (let index = 0; index < bytes.length; index++) bytes[index] = Math.floor(Math.random() * 256)
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  return uuidFromBytes(bytes)
}

function splitStroke(
  stroke: InkStroke,
  eraserSegments: EraserSegment[],
  radius: number,
): InkPoint[][] {
  const points = stroke.points
  if (points.length === 1) {
    return points.some((point) => eraserSegments.some(([start, end]) =>
      distanceSquaredToSegment(point, start, end) <= radius * radius))
      ? []
      : [[points[0]!]]
  }

  const pieces: InkPoint[][] = []
  let current: InkPoint[] = []
  for (let index = 1; index < points.length; index++) {
    const start = points[index - 1]!
    const end = points[index]!
    const erased = erasableIntervals(start, end, eraserSegments, radius)
    const kept = complementIntervals(erased)
    for (const [from, to] of kept) {
      if (to - from <= EPSILON) continue
      const first = interpolatePoint(start, end, from)
      const last = interpolatePoint(start, end, to)
      if (!current.length) {
        current = [first]
      } else if (!samePosition(current.at(-1)!, first)) {
        pieces.push(current)
        current = [first]
      }
      appendPosition(current, last)
      if (to < 1 - EPSILON) {
        pieces.push(current)
        current = []
      }
    }
  }
  if (current.length) pieces.push(current)
  return pieces
}

type EraserSegment = [InkPoint, InkPoint]

function strokeTouchesPath(stroke: InkStroke, eraserSegments: EraserSegment[], radius: number) {
  if (stroke.points.length === 1) {
    return eraserSegments.some(([start, end]) =>
      distanceSquaredToSegment(stroke.points[0]!, start, end) <= radius * radius)
  }
  for (let index = 1; index < stroke.points.length; index++) {
    if (erasableIntervals(stroke.points[index - 1]!, stroke.points[index]!, eraserSegments, radius).length) {
      return true
    }
  }
  return false
}

function erasableIntervals(
  start: InkPoint,
  end: InkPoint,
  eraserSegments: EraserSegment[],
  radius: number,
): Array<[number, number]> {
  if (radius <= 0) return []
  let intervals: Array<[number, number]> = []
  for (const [eraserStart, eraserEnd] of eraserSegments) {
    const next = segmentCapsuleIntervals(start, end, eraserStart, eraserEnd, radius)
    if (next.length) intervals = mergeIntervals([...intervals, ...next])
  }
  return intervals
}

function segmentCapsuleIntervals(
  start: InkPoint,
  end: InkPoint,
  eraserStart: InkPoint,
  eraserEnd: InkPoint,
  radius: number,
): Array<[number, number]> {
  const vx = end.x - start.x
  const vy = end.y - start.y
  const lengthSquared = vx * vx + vy * vy
  if (lengthSquared <= EPSILON) {
    return distanceSquaredToSegment(start, eraserStart, eraserEnd) <= radius * radius ? [[0, 1]] : []
  }

  const wx = eraserEnd.x - eraserStart.x
  const wy = eraserEnd.y - eraserStart.y
  const eraserLengthSquared = wx * wx + wy * wy
  if (eraserLengthSquared <= EPSILON) {
    const interval = circleInterval(start, end, eraserStart, radius)
    return interval ? [interval] : []
  }

  const projectionStart = (start.x - eraserStart.x) * wx + (start.y - eraserStart.y) * wy
  const projectionSpeed = vx * wx + vy * wy
  const breakpoints = new Set<number>([0, 1])
  if (Math.abs(projectionSpeed) > EPSILON) {
    const atStart = -projectionStart / projectionSpeed
    const atEnd = (eraserLengthSquared - projectionStart) / projectionSpeed
    if (atStart > EPSILON && atStart < 1 - EPSILON) breakpoints.add(atStart)
    if (atEnd > EPSILON && atEnd < 1 - EPSILON) breakpoints.add(atEnd)
  }
  const sorted = [...breakpoints].sort((a, b) => a - b)
  const intervals: Array<[number, number]> = []

  for (let index = 1; index < sorted.length; index++) {
    const from = sorted[index - 1]!
    const to = sorted[index]!
    if (to - from <= EPSILON) continue
    const middle = (from + to) / 2
    const projection = projectionStart + projectionSpeed * middle
    let interval: [number, number] | null
    if (projection <= 0) {
      interval = circleInterval(start, end, eraserStart, radius)
    } else if (projection >= eraserLengthSquared) {
      interval = circleInterval(start, end, eraserEnd, radius)
    } else {
      interval = infiniteLineInterval(start, end, eraserStart, wx, wy, eraserLengthSquared, radius)
    }
    if (interval) {
      const clipped = intersectInterval(interval, [from, to])
      if (clipped) intervals.push(clipped)
    }
  }
  return mergeIntervals(intervals)
}

function circleInterval(start: InkPoint, end: InkPoint, center: InkPoint, radius: number): [number, number] | null {
  const vx = end.x - start.x
  const vy = end.y - start.y
  const dx = start.x - center.x
  const dy = start.y - center.y
  const a = vx * vx + vy * vy
  const b = 2 * (dx * vx + dy * vy)
  const c = dx * dx + dy * dy - radius * radius
  if (a <= EPSILON) return c <= 0 ? [0, 1] : null
  const discriminant = b * b - 4 * a * c
  if (discriminant < 0) return null
  const root = Math.sqrt(discriminant)
  const first = (-b - root) / (2 * a)
  const second = (-b + root) / (2 * a)
  return intersectInterval([Math.min(first, second), Math.max(first, second)], [0, 1])
}

function infiniteLineInterval(
  start: InkPoint,
  end: InkPoint,
  eraserStart: InkPoint,
  wx: number,
  wy: number,
  eraserLengthSquared: number,
  radius: number,
): [number, number] | null {
  const startCross = wx * (start.y - eraserStart.y) - wy * (start.x - eraserStart.x)
  const speedCross = wx * (end.y - start.y) - wy * (end.x - start.x)
  const limit = radius * Math.sqrt(eraserLengthSquared)
  if (Math.abs(speedCross) <= EPSILON) {
    return Math.abs(startCross) <= limit ? [0, 1] : null
  }
  const first = (-limit - startCross) / speedCross
  const second = (limit - startCross) / speedCross
  return [Math.min(first, second), Math.max(first, second)]
}

function intersectInterval(
  first: [number, number],
  second: [number, number],
): [number, number] | null {
  const from = Math.max(first[0], second[0])
  const to = Math.min(first[1], second[1])
  return to - from > EPSILON ? [from, to] : null
}

function mergeIntervals(intervals: Array<[number, number]>): Array<[number, number]> {
  if (!intervals.length) return []
  const sorted = intervals
    .map(([from, to]): [number, number] => [Math.max(0, from), Math.min(1, to)])
    .filter(([from, to]) => to - from > EPSILON)
    .sort((a, b) => a[0] - b[0])
  if (!sorted.length) return []
  const merged: Array<[number, number]> = [sorted[0]!]
  for (const interval of sorted.slice(1)) {
    const last = merged[merged.length - 1]!
    if (interval[0] <= last[1] + EPSILON) {
      last[1] = Math.max(last[1], interval[1])
    } else {
      merged.push(interval)
    }
  }
  return merged
}

function complementIntervals(intervals: Array<[number, number]>): Array<[number, number]> {
  if (!intervals.length) return [[0, 1]]
  const kept: Array<[number, number]> = []
  let cursor = 0
  for (const [from, to] of intervals) {
    if (from > cursor + EPSILON) kept.push([cursor, from])
    cursor = Math.max(cursor, to)
  }
  if (cursor < 1 - EPSILON) kept.push([cursor, 1])
  return kept
}

function pathSegments(points: InkPoint[]): EraserSegment[] {
  if (!points.length) return []
  if (points.length === 1) return [[points[0]!, points[0]!]]
  const segments: EraserSegment[] = []
  for (let index = 1; index < points.length; index++) {
    segments.push([points[index - 1]!, points[index]!])
  }
  return segments
}

function distanceSquaredToSegment(point: InkPoint, start: InkPoint, end: InkPoint) {
  const dx = end.x - start.x
  const dy = end.y - start.y
  const lengthSquared = dx * dx + dy * dy
  if (lengthSquared <= EPSILON) {
    return (point.x - start.x) ** 2 + (point.y - start.y) ** 2
  }
  const projection = clamp(
    ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared,
    0,
    1,
  )
  return (point.x - (start.x + projection * dx)) ** 2 +
    (point.y - (start.y + projection * dy)) ** 2
}

function interpolatePoint(start: InkPoint, end: InkPoint, amount: number): InkPoint {
  if (amount <= EPSILON) return start
  if (amount >= 1 - EPSILON) return end
  return {
    x: start.x + (end.x - start.x) * amount,
    y: start.y + (end.y - start.y) * amount,
    pressure: start.pressure + (end.pressure - start.pressure) * amount,
  }
}

function appendPosition(points: InkPoint[], next: InkPoint) {
  const last = points.at(-1)
  if (last && samePosition(last, next)) return
  points.push(next)
}

function samePosition(first: InkPoint, second: InkPoint) {
  return Math.abs(first.x - second.x) <= EPSILON && Math.abs(first.y - second.y) <= EPSILON
}

function samePoints(first: InkPoint[], second: InkPoint[]) {
  return first.length === second.length && first.every((point, index) => {
    const other = second[index]!
    return samePosition(point, other) &&
      Math.abs(point.pressure - other.pressure) <= EPSILON
  })
}

function normalizeColor(value: unknown): string {
  if (typeof value !== 'string' || !COLOR_PATTERN.test(value.trim())) {
    throw new Error('笔迹颜色无效：必须使用 #RRGGBB')
  }
  return value.trim().toLowerCase()
}

function finiteNumber(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${label}无效`)
  return value
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function deriveStrokeId(sourceId: string, pieceIndex: number): string {
  const bytes = new Uint8Array(16)
  const seed = `${sourceId}:${pieceIndex}`
  for (let block = 0; block < 4; block++) {
    let hash = 0x811c9dc5
    for (let index = 0; index < seed.length; index++) {
      hash ^= seed.charCodeAt(index) + block * 0x9e3779b9
      hash = Math.imul(hash, 0x01000193)
    }
    hash ^= hash >>> 16
    for (let byte = 0; byte < 4; byte++) {
      bytes[block * 4 + byte] = (hash >>> (byte * 8)) & 0xff
    }
  }
  bytes[6] = (bytes[6]! & 0x0f) | 0x50
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  return uuidFromBytes(bytes)
}

function deriveUniqueStrokeId(sourceId: string, initialSalt: number, usedIds: Set<string>): string {
  let salt = initialSalt
  let candidate = deriveStrokeId(sourceId, salt)
  while (usedIds.has(candidate)) {
    salt += 1
    candidate = deriveStrokeId(sourceId, salt)
  }
  usedIds.add(candidate)
  return candidate
}

function uuidFromBytes(bytes: Uint8Array): string {
  const hex = [...bytes].map((value) => value.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.max(minimum, Math.min(maximum, value))
}
