// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { InkCanvasEditor, type InkDocument } from './ink-canvas-editor'

const LEGACY_INK = '#263b85'
const INK = '#252b35'
const RED = '#b8373c'
const BLUE = '#245fba'
const GREEN = '#216e4e'
const PREFERENCE_KEY = 'pim.inkBrushPreference'
const STROKE_1 = '11111111-1111-4111-8111-111111111111'
const STROKE_2 = '22222222-2222-4222-8222-222222222222'

let strokeStyles: string[] = []
let currentStrokeStyle = ''
let currentFillStyle = ''
const context = {
  arc: vi.fn(),
  beginPath: vi.fn(),
  fill: vi.fn(),
  fillRect: vi.fn(),
  lineTo: vi.fn(),
  moveTo: vi.fn(),
  stroke: vi.fn(),
  get strokeStyle() {
    return currentStrokeStyle
  },
  set strokeStyle(value: string) {
    currentStrokeStyle = value
    strokeStyles.push(value)
  },
  get fillStyle() {
    return currentFillStyle
  },
  set fillStyle(value: string) {
    currentFillStyle = value
  },
  lineCap: 'butt',
  lineJoin: 'miter',
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  localStorage.clear()
  strokeStyles = []
  currentStrokeStyle = ''
  currentFillStyle = ''
  context.arc.mockClear()
  context.beginPath.mockClear()
  context.fill.mockClear()
  context.fillRect.mockClear()
  context.lineTo.mockClear()
  context.moveTo.mockClear()
  context.stroke.mockClear()
  document.documentElement.removeAttribute('style')
})

function stubPngCanvas() {
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback) => {
    callback(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }))
  })
}

function drawStroke(
  canvas: HTMLElement,
  pointerId: number,
  points: Array<{ x: number; y: number; pressure?: number }>,
  pointerType = 'pen',
) {
  const first = points[0]!
  fireEvent.pointerDown(canvas, {
    pointerId,
    pointerType,
    isPrimary: true,
    button: 0,
    buttons: 1,
    clientX: first.x,
    clientY: first.y,
    pressure: first.pressure ?? 1,
  })
  for (const point of points.slice(1)) {
    fireEvent.pointerMove(canvas, {
      pointerId,
      pointerType,
      isPrimary: true,
      button: 0,
      buttons: 1,
      clientX: point.x,
      clientY: point.y,
      pressure: point.pressure ?? 1,
    })
  }
  fireEvent.pointerUp(canvas, { pointerId, pointerType, isPrimary: true, button: 0, buttons: 0 })
}

async function clickSave() {
  await userEvent.click(screen.getByRole('button', { name: '保存手写' }))
}

describe('InkCanvasEditor', () => {
  it('records schema-v1 coordinates and pressure and creates a PNG preview', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const onPendingChange = vi.fn()
    const onDirty = vi.fn()
    render(<InkCanvasEditor onCancel={vi.fn()} onSave={onSave} onPendingChange={onPendingChange} onDirty={onDirty} />)
    const canvas = screen.getByTestId('ink-canvas')

    drawStroke(canvas, 3, [
      { x: 20, y: 30, pressure: 0.35 },
      { x: 80, y: 90, pressure: 0.75 },
    ])
    expect(onPendingChange.mock.calls).toEqual([[true], [false]])
    expect(onDirty).toHaveBeenCalledOnce()
    const path = canvas.querySelector('path')
    expect(path?.getAttribute('stroke-width')).toBe('3.4')
    expect(path?.getAttribute('stroke')).toBe(INK)
    await clickSave()

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink, preview] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.width).toBe(320)
    expect(ink.height).toBe(240)
    expect(ink.strokes[0]).toMatchObject({ width: 3.4, color: INK })
    expect(ink.strokeIds).toHaveLength(1)
    expect(ink.strokes[0]?.points.map(({ x, y }) => ({ x, y }))).toEqual([
      { x: 20, y: 30 },
      { x: 80, y: 90 },
    ])
    expect(ink.strokes[0]?.points[0]?.pressure).toBeCloseTo(0.35, 5)
    expect(ink.strokes[0]?.points[1]?.pressure).toBeCloseTo(0.75, 5)
    expect(preview).toEqual(new Uint8Array([0x89, 0x50, 0x4e, 0x47]))
    expect(context.stroke).toHaveBeenCalledOnce()
    expect(strokeStyles).toContain(INK)
  })

  it('edits existing vectors without dropping extension fields and supports undo', async () => {
    stubPngCanvas()
    const initialInk = {
      width: 400,
      height: 300,
      strokeIds: [STROKE_1, STROKE_2],
      strokes: [
        { width: 3, points: [{ x: 40, y: 50, pressure: 0.6 }] },
        { width: 4, points: [{ x: 100, y: 120, pressure: 0.8 }] },
      ],
      platformExtension: { source: 'android-v1' },
    }
    const onSave = vi.fn()
    render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)

    await userEvent.click(screen.getByRole('button', { name: '撤销最近一笔' }))
    await clickSave()

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink).toMatchObject({
      width: 400,
      height: 300,
      platformExtension: { source: 'android-v1' },
      strokeIds: [STROKE_1],
      strokes: [{ width: 3, color: LEGACY_INK, points: [{ x: 40, y: 50, pressure: 0.6 }] }],
    })
  })

  it('offers floating ink/red/blue/green and thin/medium/thick brushes and remembers the device choice', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokes: [{ width: 2.5, points: [{ x: 10, y: 10, pressure: 0.4 }] }],
    }
    const first = render(
      <InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />,
    )

    await userEvent.click(screen.getByRole('button', { name: '画笔' }))
    const dialog = screen.getByRole('dialog', { name: '画笔设置' })
    for (const name of ['墨色', '红', '蓝', '绿', '细', '中', '粗']) {
      expect(screen.getByRole('button', { name })).toBeTruthy()
    }
    expect(screen.getByRole('button', { name: '中' }).getAttribute('aria-pressed')).toBe('true')
    await userEvent.click(screen.getByRole('button', { name: '红' }))
    await userEvent.click(screen.getByRole('button', { name: '粗' }))
    expect(screen.getByRole('button', { name: '红' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: '粗' }).getAttribute('aria-pressed')).toBe('true')
    expect(dialog).toBeTruthy()
    expect(JSON.parse(localStorage.getItem(PREFERENCE_KEY) ?? 'null')).toEqual({ color: RED, width: 6 })
    await userEvent.click(screen.getByRole('button', { name: '完成设置' }))

    drawStroke(screen.getByTestId('ink-canvas'), 21, [
      { x: 30, y: 40 },
      { x: 120, y: 130 },
    ], 'mouse')
    await clickSave()
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [firstInk] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(firstInk.strokes[0]).toMatchObject({ width: 2.5, color: LEGACY_INK })
    expect(firstInk.strokes[1]).toMatchObject({ width: 6, color: RED })

    first.unmount()
    const secondSave = vi.fn()
    render(<InkCanvasEditor initialInk={firstInk} onCancel={vi.fn()} onSave={secondSave} />)
    await userEvent.click(screen.getByRole('button', { name: '画笔' }))
    expect(screen.getByRole('button', { name: '红' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: '粗' }).getAttribute('aria-pressed')).toBe('true')
    await userEvent.click(screen.getByRole('button', { name: '完成设置' }))
    drawStroke(screen.getByTestId('ink-canvas'), 22, [
      { x: 50, y: 60 },
      { x: 150, y: 160 },
    ], 'touch')
    await clickSave()
    await waitFor(() => expect(secondSave).toHaveBeenCalledOnce())
    const [secondInk] = secondSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(secondInk.strokes.slice(0, 2)).toEqual(firstInk.strokes.slice(0, 2))
    expect(secondInk.strokes[2]).toMatchObject({ width: 6, color: RED })
    expect(secondInk.strokeIds.slice(0, 2)).toEqual(firstInk.strokeIds)
    expect(secondInk.strokeIds[2]).not.toBe(firstInk.strokeIds[1])
  })

  it('keeps per-stroke styles and identities through theme changes, save-reopen, and PNG rendering', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokeIds: [STROKE_1, STROKE_2],
      strokes: [
        { color: RED, width: 1.8, points: [{ x: 20, y: 30, pressure: 0.2 }, { x: 100, y: 110, pressure: 0.8 }] },
        { color: BLUE, width: 6, points: [{ x: 200, y: 40, pressure: 0.5 }, { x: 280, y: 200, pressure: 0.5 }] },
      ],
      androidExtension: { keep: true },
    }
    const first = render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)
    const canvas = screen.getByTestId('ink-canvas')

    document.documentElement.style.setProperty('--color-primary', '#ff00ff')
    expect([...canvas.querySelectorAll('path')].map((path) => path.getAttribute('stroke'))).toEqual([RED, BLUE])
    await clickSave()
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [saved] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(saved).toMatchObject({ androidExtension: { keep: true } })
    expect(saved.strokeIds).toEqual([STROKE_1, STROKE_2])
    expect(saved.strokes.map(({ color, width }) => ({ color, width }))).toEqual([
      { color: RED, width: 1.8 },
      { color: BLUE, width: 6 },
    ])
    expect(strokeStyles.filter((color) => color === RED || color === BLUE)).toEqual([RED, BLUE])

    first.unmount()
    const reopenedSave = vi.fn()
    render(<InkCanvasEditor initialInk={saved} onCancel={vi.fn()} onSave={reopenedSave} />)
    await clickSave()
    await waitFor(() => expect(reopenedSave).toHaveBeenCalledOnce())
    const [reopened] = reopenedSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(reopened.strokeIds).toEqual([STROKE_1, STROKE_2])
    expect(reopened.strokes.map(({ color, width }) => ({ color, width }))).toEqual([
      { color: RED, width: 1.8 },
      { color: BLUE, width: 6 },
    ])
  })

  it('uses the partial eraser by default and cuts only the erased span while retaining both sides', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokeIds: [STROKE_1, STROKE_2],
      strokes: [
        { color: RED, width: 6, points: [{ x: 20, y: 100, pressure: 0.2 }, { x: 300, y: 100, pressure: 0.9 }] },
        { color: BLUE, width: 1.8, points: [{ x: 20, y: 200, pressure: 0.4 }, { x: 300, y: 200, pressure: 0.4 }] },
      ],
    }
    render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)

    await userEvent.click(screen.getByRole('button', { name: '橡皮' }))
    expect(screen.getByRole('button', { name: '局部擦除' }).getAttribute('aria-pressed')).toBe('true')
    await userEvent.click(screen.getByRole('button', { name: '局部擦除' }))
    drawStroke(screen.getByTestId('ink-canvas'), 31, [
      { x: 160, y: 80 },
      { x: 160, y: 100 },
      { x: 160, y: 120 },
    ], 'mouse')
    await clickSave()

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.strokes).toHaveLength(3)
    const [left, right, other] = ink.strokes
    expect(left.points[0]).toMatchObject({ x: 20, y: 100 })
    expect(left.points.at(-1)?.x).toBeGreaterThan(130)
    expect(left.points.at(-1)?.x).toBeLessThan(160)
    expect(right.points[0]?.x).toBeGreaterThan(160)
    expect(right.points[0]?.x).toBeLessThan(190)
    expect(right.points.at(-1)).toMatchObject({ x: 300, y: 100 })
    expect(left.points.every((point) => point.y === 100)).toBe(true)
    expect(right.points.every((point) => point.y === 100)).toBe(true)
    expect(left).toMatchObject({ color: RED, width: 6 })
    expect(right).toMatchObject({ color: RED, width: 6 })
    expect(other).toMatchObject({ color: BLUE, width: 1.8 })
    expect(ink.strokeIds[0]).toBe(STROKE_1)
    expect(ink.strokeIds[1]).not.toBe(STROKE_1)
    expect(ink.strokeIds[1]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(ink.strokeIds[2]).toBe(STROKE_2)
  })

  it('deletes a touched stroke in whole-stroke mode and preserves untouched strokes', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokeIds: [STROKE_1, STROKE_2],
      strokes: [
        { color: RED, width: 6, points: [{ x: 20, y: 100 }, { x: 300, y: 100 }] },
        { color: BLUE, width: 1.8, points: [{ x: 20, y: 200 }, { x: 300, y: 200 }] },
      ],
    }
    render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)

    await userEvent.click(screen.getByRole('button', { name: '橡皮' }))
    await userEvent.click(screen.getByRole('button', { name: '整笔擦除' }))
    drawStroke(screen.getByTestId('ink-canvas'), 32, [
      { x: 150, y: 90 },
      { x: 170, y: 110 },
    ], 'mouse')
    await clickSave()

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.strokes).toEqual([expect.objectContaining({ color: BLUE, width: 1.8 })])
    expect(ink.strokeIds).toEqual([STROKE_2])
  })

  it('groups one erase drag into one undo step', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokeIds: [STROKE_1],
      strokes: [{ color: GREEN, width: 3.4, points: [{ x: 20, y: 100 }, { x: 300, y: 100 }] }],
    }
    render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)

    await userEvent.click(screen.getByRole('button', { name: '橡皮' }))
    await userEvent.click(screen.getByRole('button', { name: '局部擦除' }))
    drawStroke(screen.getByTestId('ink-canvas'), 33, [
      { x: 160, y: 80 },
      { x: 160, y: 100 },
      { x: 160, y: 120 },
    ], 'mouse')
    expect(screen.getByTestId('ink-canvas').querySelectorAll('path')).toHaveLength(2)

    await userEvent.click(screen.getByRole('button', { name: '撤销最近一笔' }))
    expect(screen.getByTestId('ink-canvas').querySelectorAll('path')).toHaveLength(1)
    expect((screen.getByRole('button', { name: '撤销最近一笔' }) as HTMLButtonElement).disabled).toBe(true)
    await clickSave()

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.strokeIds).toEqual([STROKE_1])
    expect(ink.strokes).toEqual([
      expect.objectContaining({
        color: GREEN,
        width: 3.4,
        points: [{ x: 20, y: 100, pressure: 1 }, { x: 300, y: 100, pressure: 1 }],
      }),
    ])
  })

  it('preserves an in-progress erasure on pointer cancel and restores it with one undo', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    const onPendingChange = vi.fn()
    const initialInk = {
      width: 320,
      height: 240,
      strokeIds: [STROKE_1],
      strokes: [{ color: RED, width: 3.4, points: [{ x: 20, y: 100 }, { x: 300, y: 100 }] }],
    }
    render(<InkCanvasEditor
      initialInk={initialInk}
      onCancel={vi.fn()}
      onSave={onSave}
      onPendingChange={onPendingChange}
    />)

    await userEvent.click(screen.getByRole('button', { name: '橡皮' }))
    await userEvent.click(screen.getByRole('button', { name: '局部擦除' }))
    const canvas = screen.getByTestId('ink-canvas')
    fireEvent.pointerDown(canvas, {
      pointerId: 34, pointerType: 'touch', isPrimary: true, button: 0, buttons: 1, clientX: 160, clientY: 80,
    })
    fireEvent.pointerMove(canvas, {
      pointerId: 34, pointerType: 'touch', isPrimary: true, button: 0, buttons: 1, clientX: 160, clientY: 120,
    })
    fireEvent.pointerCancel(canvas, {
      pointerId: 34, pointerType: 'touch', isPrimary: true, button: 0, buttons: 0,
    })
    expect(canvas.querySelectorAll('path')).toHaveLength(2)
    expect(onPendingChange.mock.calls).toEqual([[true], [false]])

    await userEvent.click(screen.getByRole('button', { name: '撤销最近一笔' }))
    await clickSave()
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.strokeIds).toEqual([STROKE_1])
    expect(ink.strokes).toHaveLength(1)
  })

  it('supports middle-button panning and wheel zoom without marking ink dirty', async () => {
    const onDirty = vi.fn()
    const onPendingChange = vi.fn()
    render(<InkCanvasEditor onCancel={vi.fn()} onSave={vi.fn()} onDirty={onDirty} onPendingChange={onPendingChange} />)
    const canvas = screen.getByTestId('ink-canvas')
    expect(canvas.getAttribute('viewBox')).toBe('0 0 320 240')

    fireEvent.pointerDown(canvas, {
      pointerId: 41, pointerType: 'mouse', isPrimary: true, button: 1, buttons: 4, clientX: 100, clientY: 80,
    })
    fireEvent.pointerMove(canvas, {
      pointerId: 41, pointerType: 'mouse', isPrimary: true, button: 1, buttons: 4, clientX: 70, clientY: 60,
    })
    fireEvent.pointerUp(canvas, {
      pointerId: 41, pointerType: 'mouse', isPrimary: true, button: 1, buttons: 0, clientX: 70, clientY: 60,
    })
    expect(canvas.getAttribute('viewBox')).not.toBe('0 0 320 240')
    const panned = canvas.getAttribute('viewBox')

    fireEvent.wheel(canvas, { deltaY: -120, clientX: 80, clientY: 80 })
    expect(canvas.getAttribute('viewBox')).not.toBe(panned)
    expect(Number(canvas.getAttribute('viewBox')?.split(' ')[2])).toBeLessThan(320)
    expect(onDirty).not.toHaveBeenCalled()
    expect(onPendingChange).not.toHaveBeenCalled()
  })

  it('refuses an invalid stroke identity snapshot instead of rewriting it', () => {
    const onSave = vi.fn()
    render(<InkCanvasEditor
      initialInk={{
        width: 320,
        height: 240,
        strokeIds: [STROKE_1],
        strokes: [
          { points: [{ x: 20, y: 30 }] },
          { points: [{ x: 40, y: 50 }] },
        ],
      }}
      onCancel={vi.fn()}
      onSave={onSave}
    />)

    expect(screen.getByText(/笔迹身份/)).toBeTruthy()
    expect((screen.getByRole('button', { name: '保存手写' }) as HTMLButtonElement).disabled).toBe(true)
    expect(onSave).not.toHaveBeenCalled()
  })

  it('refuses out-of-range schema-v1 coordinates instead of rewriting the source', () => {
    const onSave = vi.fn()
    render(<InkCanvasEditor
      initialInk={{ width: 320, height: 240, strokes: [{ points: [{ x: 400, y: 20 }] }] }}
      onCancel={vi.fn()}
      onSave={onSave}
    />)

    expect(screen.getByText('笔迹坐标或压力超出 schema-v1 范围')).toBeTruthy()
    expect((screen.getByRole('button', { name: '保存手写' }) as HTMLButtonElement).disabled).toBe(true)
    expect(onSave).not.toHaveBeenCalled()
  })

  it('does not accept pointer changes or save while its edit session is disabled', async () => {
    const onSave = vi.fn()
    const onDirty = vi.fn()
    const onCancel = vi.fn()
    const initialInk = { width: 320, height: 240, strokes: [{ width: 2, points: [{ x: 10, y: 10 }] }] }
    const { rerender } = render(<InkCanvasEditor initialInk={initialInk} disabled onCancel={onCancel} onSave={onSave} onDirty={onDirty} />)
    const canvas = screen.getByTestId('ink-canvas')
    fireEvent.pointerDown(canvas, { pointerId: 9, isPrimary: true, clientX: 20, clientY: 20 })
    expect(onDirty).not.toHaveBeenCalled()
    expect((screen.getByRole('button', { name: '保存手写' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '撤销最近一笔' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '清空手写卡片' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '取消' }) as HTMLButtonElement).disabled).toBe(true)

    rerender(<InkCanvasEditor onCancel={onCancel} onSave={onSave} onDirty={onDirty} />)
    const activeCanvas = screen.getByTestId('ink-canvas')
    fireEvent.pointerDown(activeCanvas, { pointerId: 10, isPrimary: true, clientX: 20, clientY: 20 })
    fireEvent.pointerUp(activeCanvas, { pointerId: 10, isPrimary: true })
    rerender(<InkCanvasEditor disabled onCancel={onCancel} onSave={onSave} onDirty={onDirty} />)
    expect((screen.getByRole('button', { name: '撤销最近一笔' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '清空手写卡片' }) as HTMLButtonElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: '取消' }) as HTMLButtonElement).disabled).toBe(true)
    expect(onSave).not.toHaveBeenCalled()
    expect(onDirty).toHaveBeenCalledOnce()
    expect(onCancel).not.toHaveBeenCalled()
  })

  it('drops an asynchronous preview result after the editor session closes', async () => {
    let finishPreview!: (blob: Blob | null) => void
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
    const toBlob = vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback) => {
      finishPreview = callback
    })
    let active = true
    const onSave = vi.fn()
    const { rerender } = render(<InkCanvasEditor isSessionActive={() => active} onCancel={vi.fn()} onSave={onSave} />)

    await clickSave()
    await waitFor(() => expect(toBlob).toHaveBeenCalledOnce())
    active = false
    rerender(<InkCanvasEditor isSessionActive={() => active} disabled onCancel={vi.fn()} onSave={onSave} />)
    finishPreview(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }))
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(onSave).not.toHaveBeenCalled()
  })
})
