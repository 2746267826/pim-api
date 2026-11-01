// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { InkCanvasEditor, type InkDocument } from './ink-canvas-editor'

const context = {
  arc: vi.fn(),
  beginPath: vi.fn(),
  fill: vi.fn(),
  fillRect: vi.fn(),
  lineTo: vi.fn(),
  moveTo: vi.fn(),
  stroke: vi.fn(),
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

function stubPngCanvas() {
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback) => {
    callback(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }))
  })
}

describe('InkCanvasEditor', () => {
  it('records schema-v1 coordinates and pressure and creates a PNG preview', async () => {
    stubPngCanvas()
    const onSave = vi.fn()
    render(<InkCanvasEditor onCancel={vi.fn()} onSave={onSave} />)
    const canvas = screen.getByTestId('ink-canvas')

    fireEvent.pointerDown(canvas, {
      pointerId: 3,
      pointerType: 'pen',
      isPrimary: true,
      clientX: 20,
      clientY: 30,
      pressure: 0.35,
    })
    fireEvent.pointerMove(canvas, {
      pointerId: 3,
      pointerType: 'pen',
      isPrimary: true,
      clientX: 80,
      clientY: 90,
      pressure: 0.75,
    })
    fireEvent.pointerUp(canvas, { pointerId: 3, pointerType: 'pen', isPrimary: true })
    await userEvent.click(screen.getByRole('button', { name: '保存手写' }))

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink, preview] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink.width).toBe(320)
    expect(ink.height).toBe(240)
    expect(ink.strokes[0]?.width).toBe(2.5)
    expect(ink.strokes[0]?.points.map(({ x, y }) => ({ x, y }))).toEqual([
      { x: 20, y: 30 },
      { x: 80, y: 90 },
    ])
    expect(ink.strokes[0]?.points[0]?.pressure).toBeCloseTo(0.35, 5)
    expect(ink.strokes[0]?.points[1]?.pressure).toBeCloseTo(0.75, 5)
    expect(preview).toEqual(new Uint8Array([0x89, 0x50, 0x4e, 0x47]))
    expect(context.stroke).toHaveBeenCalledOnce()
  })

  it('edits existing vectors without dropping extension fields and supports undo', async () => {
    stubPngCanvas()
    const initialInk = {
      width: 400,
      height: 300,
      strokes: [
        { width: 3, points: [{ x: 40, y: 50, pressure: 0.6 }] },
        { width: 4, points: [{ x: 100, y: 120, pressure: 0.8 }] },
      ],
      platformExtension: { source: 'android-v1' },
    }
    const onSave = vi.fn()
    render(<InkCanvasEditor initialInk={initialInk} onCancel={vi.fn()} onSave={onSave} />)

    await userEvent.click(screen.getByRole('button', { name: '撤销最近一笔' }))
    await userEvent.click(screen.getByRole('button', { name: '保存手写' }))

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce())
    const [ink] = onSave.mock.calls[0] as [InkDocument, Uint8Array]
    expect(ink).toMatchObject({
      width: 400,
      height: 300,
      platformExtension: { source: 'android-v1' },
      strokes: [{ width: 3, points: [{ x: 40, y: 50, pressure: 0.6 }] }],
    })
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
})
