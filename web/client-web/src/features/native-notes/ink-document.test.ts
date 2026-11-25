// @vitest-environment node
import { describe, expect, it } from 'vitest'
import {
  BLUE_COLOR,
  LEGACY_INK_COLOR,
  RED_COLOR,
  eraseInk,
  parseInkDocument,
  replaceInkStrokes,
  type InkDocument,
} from './ink-document'

const STROKE_1 = '11111111-1111-4111-8111-111111111111'
const STROKE_2 = '22222222-2222-4222-8222-222222222222'

function styleDocument(): InkDocument {
  return {
    width: 320,
    height: 240,
    rootExtension: { keep: true },
    strokeIds: [STROKE_1],
    strokeStyles: [{
      color: RED_COLOR,
      width: 4,
      futureStyle: { source: 'android', keep: true },
    }],
    strokes: [{
      color: RED_COLOR,
      width: 4,
      strokeExtension: 'keep-stroke',
      points: [
        { x: 20, y: 100, pressure: 0.2, pointExtension: 'keep-start' },
        { x: 300, y: 100, pressure: 0.8, pointExtension: 'keep-end' },
      ],
    }],
  }
}

describe('D04 ink document', () => {
  it('preserves unknown root, stroke, point, and strokeStyles fields while normalizing defaults', () => {
    const parsed = parseInkDocument({
      width: 320,
      height: 240,
      rootExtension: { keep: true },
      strokeIds: [STROKE_1, STROKE_2],
      strokeStyles: [
        {
          color: '#B8373C',
          width: 1.8,
          styleExtension: { source: 'android', keep: true },
        },
        {
          color: BLUE_COLOR,
          width: 6,
          futureStyle: 'keep-second',
        },
      ],
      strokes: [
        {
          color: '#B8373C',
          width: 1.8,
          strokeExtension: 'keep-first',
          points: [{
            x: 20,
            y: 30,
            pressure: 0.4,
            pointExtension: { keep: true },
          }],
        },
        {
          color: BLUE_COLOR,
          width: 6,
          points: [{ x: 50, y: 60, pressure: 0.7 }],
        },
      ],
    })

    expect(parsed.rootExtension).toEqual({ keep: true })
    expect(parsed.strokes[0]).toMatchObject({
      color: RED_COLOR,
      width: 1.8,
      strokeExtension: 'keep-first',
      points: [{ pointExtension: { keep: true } }],
    })
    expect(parsed.strokeStyles).toEqual([
      {
        color: RED_COLOR,
        width: 1.8,
        styleExtension: { source: 'android', keep: true },
      },
      {
        color: BLUE_COLOR,
        width: 6,
        futureStyle: 'keep-second',
      },
    ])
  })

  it('normalizes a legacy snapshot while preserving unknown fields', () => {
    const parsed = parseInkDocument({
      width: 320,
      height: 240,
      rootExtension: 'keep-root',
      strokes: [{
        strokeExtension: 'keep-stroke',
        points: [{
          x: 12,
          y: 18,
          pointExtension: 'keep-point',
        }],
      }],
    })

    expect(parsed.rootExtension).toBe('keep-root')
    expect(parsed.strokeIds).toEqual([expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    )])
    expect(parsed.strokes[0]).toMatchObject({
      color: LEGACY_INK_COLOR,
      width: 2.5,
      strokeExtension: 'keep-stroke',
      points: [{ x: 12, y: 18, pressure: 1, pointExtension: 'keep-point' }],
    })
    expect(parsed.strokeStyles).toEqual([{ color: LEGACY_INK_COLOR, width: 2.5 }])
  })

  it('retains style fields when replacing or appending strokes', () => {
    const ink = styleDocument()
    const appended = {
      color: BLUE_COLOR,
      width: 2.2,
      points: [{ x: 40, y: 80, pressure: 0.5 }],
    }

    const next = replaceInkStrokes(
      ink,
      [...ink.strokes, appended],
      [STROKE_1, STROKE_2],
    )

    expect(next.strokeStyles).toEqual([
      {
        color: RED_COLOR,
        width: 4,
        futureStyle: { source: 'android', keep: true },
      },
      { color: BLUE_COLOR, width: 2.2 },
    ])
  })

  it('retains style fields on both sides of a partial erase', () => {
    const ink = styleDocument()
    const result = eraseInk(ink, 'partial', [{ x: 160, y: 100, pressure: 1 }])

    expect(result.changed).toBe(true)
    expect(result.ink.strokes).toHaveLength(2)
    expect(result.ink.strokes[0]?.points[0]).toMatchObject({ pointExtension: 'keep-start' })
    expect(result.ink.strokes[0]?.points.at(-1)).toMatchObject({ pointExtension: 'keep-start' })
    expect(result.ink.strokes[1]?.points[0]).toMatchObject({ pointExtension: 'keep-start' })
    expect(result.ink.strokes[1]?.points.at(-1)).toMatchObject({ pointExtension: 'keep-end' })
    expect(result.ink.strokeStyles).toHaveLength(2)
    expect(result.ink.strokeStyles).toEqual([
      {
        color: RED_COLOR,
        width: 4,
        futureStyle: { source: 'android', keep: true },
      },
      {
        color: RED_COLOR,
        width: 4,
        futureStyle: { source: 'android', keep: true },
      },
    ])
  })

  it('retains style extensions when an existing stroke changes color and width', () => {
    const ink = styleDocument()
    const next = replaceInkStrokes(
      ink,
      [{ ...ink.strokes[0]!, color: BLUE_COLOR, width: 3 }],
      [STROKE_1],
    )

    expect(next.strokeStyles).toEqual([{
      color: BLUE_COLOR,
      width: 3,
      futureStyle: { source: 'android', keep: true },
    }])
  })

  it.each([
    {
      name: 'duplicate normalized IDs',
      value: {
        width: 320,
        height: 240,
        strokeIds: [STROKE_1, STROKE_1.toUpperCase()],
        strokes: [
          { points: [{ x: 1, y: 1 }] },
          { points: [{ x: 2, y: 2 }] },
        ],
      },
      message: '笔迹身份',
    },
    {
      name: 'invalid ID',
      value: {
        width: 320,
        height: 240,
        strokeIds: ['not-a-uuid'],
        strokes: [{ points: [{ x: 1, y: 1 }] }],
      },
      message: '笔迹身份',
    },
    {
      name: 'stroke ID count mismatch',
      value: {
        width: 320,
        height: 240,
        strokeIds: [STROKE_1],
        strokes: [{ points: [{ x: 1, y: 1 }] }, { points: [{ x: 2, y: 2 }] }],
      },
      message: '数量必须与笔画一致',
    },
    {
      name: 'style count mismatch',
      value: {
        width: 320,
        height: 240,
        strokeStyles: [{ color: RED_COLOR, width: 2 }],
        strokes: [{ points: [{ x: 1, y: 1 }] }, { points: [{ x: 2, y: 2 }] }],
      },
      message: '数量必须与笔画一致',
    },
    {
      name: 'invalid inline color',
      value: {
        width: 320,
        height: 240,
        strokes: [{ color: 'red', points: [{ x: 1, y: 1 }] }],
      },
      message: '笔迹颜色无效',
    },
    {
      name: 'invalid style color',
      value: {
        width: 320,
        height: 240,
        strokeStyles: [{ color: '#12345', width: 2 }],
        strokes: [{ points: [{ x: 1, y: 1 }] }],
      },
      message: '笔迹颜色无效',
    },
    {
      name: 'inline and style color conflict',
      value: {
        width: 320,
        height: 240,
        strokeStyles: [{ color: RED_COLOR, width: 2 }],
        strokes: [{ color: BLUE_COLOR, points: [{ x: 1, y: 1 }] }],
      },
      message: '笔迹颜色与样式数组不一致',
    },
    {
      name: 'inline and style width conflict',
      value: {
        width: 320,
        height: 240,
        strokeStyles: [{ color: RED_COLOR, width: 2 }],
        strokes: [{ width: 3, points: [{ x: 1, y: 1 }] }],
      },
      message: '笔画宽度与样式数组不一致',
    },
    {
      name: 'empty points',
      value: {
        width: 320,
        height: 240,
        strokes: [{ points: [] }],
      },
      message: '笔画必须包含坐标点',
    },
    {
      name: 'negative x coordinate',
      value: {
        width: 320,
        height: 240,
        strokes: [{ points: [{ x: -1, y: 1 }] }],
      },
      message: '笔迹坐标或压力超出 schema-v1 范围',
    },
    {
      name: 'y coordinate beyond canvas',
      value: {
        width: 320,
        height: 240,
        strokes: [{ points: [{ x: 1, y: 241 }] }],
      },
      message: '笔迹坐标或压力超出 schema-v1 范围',
    },
    {
      name: 'negative pressure',
      value: {
        width: 320,
        height: 240,
        strokes: [{ points: [{ x: 1, y: 1, pressure: -0.1 }] }],
      },
      message: '笔迹坐标或压力超出 schema-v1 范围',
    },
    {
      name: 'stroke width below schema range',
      value: {
        width: 320,
        height: 240,
        strokes: [{ width: 0.4, points: [{ x: 1, y: 1 }] }],
      },
      message: '笔画宽度超出 schema-v1 范围',
    },
    {
      name: 'padded color',
      value: {
        width: 320,
        height: 240,
        strokes: [{ color: ` ${RED_COLOR} `, points: [{ x: 1, y: 1 }] }],
      },
      message: '笔迹颜色无效',
    },
  ])('rejects $name', ({ value, message }) => {
    expect(() => parseInkDocument(value)).toThrow(message)
  })
})
