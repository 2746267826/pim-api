// @vitest-environment jsdom
import { useRef, useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import {
  ContinuousMaterialEditor,
  type ContinuousMaterialEditorHandle,
  projectMaterialMarkdown,
  serializeMaterialBlocks,
} from './continuous-material-editor'
import type { MaterialDraftBlock } from './material-model'
import { installProseMirrorLayoutStubs } from '@/test-utils/prosemirror-layout'

const markdownA = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const photoId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const markdownB = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const inkId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'

const blocks: MaterialDraftBlock[] = [
  {
    id: markdownA,
    kind: 'markdown',
    text: '# 第一段\n\n照片之前',
    source: { vendor: { keep: true }, text: '# 第一段\n\n照片之前' },
  },
  {
    id: photoId,
    kind: 'photo',
    assetId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
    mimeType: 'image/png',
    bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1]),
    source: { photoExtension: 'keep' },
  },
  {
    id: markdownB,
    kind: 'markdown',
    text: '照片之后 **继续写**',
    source: { vendor: 'second-block' },
  },
  {
    id: inkId,
    kind: 'ink',
    ink: { width: 320, height: 240, strokes: [] },
    bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 2]),
    source: { inkExtension: { keep: true } },
  },
]

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

beforeEach(() => {
  installProseMirrorLayoutStubs()
})

function Harness({ initial = blocks, onDirty = () => undefined }: {
  initial?: MaterialDraftBlock[]
  onDirty?: () => void
}) {
  const ref = useRef<ContinuousMaterialEditorHandle>(null)
  const [read, setRead] = useState<MaterialDraftBlock[]>([])
  return <div>
    <ContinuousMaterialEditor
      ref={ref}
      initialBlocks={initial}
      editable
      showVersionDetails
      isSessionActive={() => true}
      onDirty={onDirty}
      onContentStateChange={() => undefined}
      onInkEditorChange={() => undefined}
      onInkPendingChange={() => undefined}
      onError={() => undefined}
    />
    <button type="button" onClick={() => setRead(ref.current?.getBlocks() ?? [])}>读取正文原稿</button>
    <pre data-testid="read-blocks">{JSON.stringify(read)}</pre>
  </div>
}

describe('continuous native-note material editor', () => {
  it('keeps one continuous document and round-trips markdown, photo and ink identities in order', async () => {
    Object.defineProperties(URL, {
      createObjectURL: { configurable: true, value: vi.fn(() => 'blob:continuous-material') },
      revokeObjectURL: { configurable: true, value: vi.fn() },
    })
    render(<Harness />)

    expect(await screen.findByLabelText('笔记正文')).toBeTruthy()
    expect(screen.queryByLabelText('正文块 1')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: '读取正文原稿' }))
    await waitFor(() => expect(screen.getByTestId('read-blocks').textContent).not.toBe('[]'))

    const result = JSON.parse(screen.getByTestId('read-blocks').textContent ?? '[]') as MaterialDraftBlock[]
    expect(result.map((block) => [block.kind, block.id])).toEqual([
      ['markdown', markdownA],
      ['photo', photoId],
      ['markdown', markdownB],
      ['ink', inkId],
    ])
    expect(result[0]).toMatchObject({ source: { vendor: { keep: true } } })
    expect(result[1]).toMatchObject({ source: { photoExtension: 'keep' } })
    expect(result[2]).toMatchObject({ source: { vendor: 'second-block' } })
    expect(result[3]).toMatchObject({ source: { inkExtension: { keep: true } } })
    expect(result[0]?.kind === 'markdown' ? result[0].text : '').toContain('照片之前')
    expect(result[0]?.kind === 'markdown' ? result[0].text : '').toContain('# 第一段')
    expect(result[2]?.kind === 'markdown' ? result[2].text : '').toContain('照片之后')
    expect(result[2]?.kind === 'markdown' ? result[2].text : '').toContain('**继续写**')
  })

  it('keeps the same reading order in the continuous editor and the Markdown projection', async () => {
    Object.defineProperties(URL, {
      createObjectURL: { configurable: true, value: vi.fn(() => 'blob:continuous-material') },
      revokeObjectURL: { configurable: true, value: vi.fn() },
    })
    render(<Harness />)

    const body = await screen.findByLabelText('笔记正文')
    const text = body.textContent ?? ''
    expect(text.indexOf('照片之前')).toBeLessThan(text.indexOf('照片之后'))
    expect(body.querySelector(`[data-pim-markdown-block-id="${markdownA}"]`)).toBeTruthy()
    expect(body.querySelector(`[data-pim-markdown-block-id="${markdownB}"]`)).toBeTruthy()
    expect(body.querySelector(`[data-pim-material-id="${photoId}"]`)).toBeTruthy()
    expect(body.querySelector(`[data-pim-material-id="${inkId}"]`)).toBeTruthy()

    const projection = projectMaterialMarkdown(blocks)
    expect(projection.indexOf('照片之前')).toBeLessThan(projection.indexOf('assets/'))
    expect(projection.indexOf('assets/')).toBeLessThan(projection.indexOf('照片之后'))
  })

  it('keeps nested Markdown intact when a material boundary splits a list', async () => {
    Object.defineProperties(URL, {
      createObjectURL: { configurable: true, value: vi.fn(() => 'blob:continuous-material') },
      revokeObjectURL: { configurable: true, value: vi.fn() },
    })
    render(<Harness initial={[{
      id: markdownA,
      kind: 'markdown',
      text: '- 上层条目\n  - 嵌套 **重点**\n\n> 引用段落',
    }]} />)

    const body = await screen.findByLabelText('笔记正文')
    body.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}')
    const photo = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 4])], 'nested.png', { type: 'image/png' })
    await userEvent.upload(screen.getByLabelText('添加照片'), photo)
    await waitFor(() =>
      expect(body.querySelectorAll('[data-material-block-id]')).toHaveLength(1))

    await userEvent.click(screen.getByRole('button', { name: '读取正文原稿' }))
    await waitFor(() => expect(screen.getByTestId('read-blocks').textContent).not.toBe('[]'))
    const result = JSON.parse(screen.getByTestId('read-blocks').textContent ?? '[]') as MaterialDraftBlock[]

    expect(result.filter((block) => block.kind === 'photo')).toHaveLength(1)
    expect(new Set(result.map((block) => block.id)).size).toBe(result.length)
    expect(result.filter((block) => block.kind === 'markdown').map((block) =>
      block.kind === 'markdown' ? block.text : '').join('\n')).toContain('嵌套 **重点**')
    expect(result.filter((block) => block.kind === 'markdown').map((block) =>
      block.kind === 'markdown' ? block.text : '').join('\n')).toContain('引用段落')
  })

  it('rejects missing or duplicate block identities before an incomplete submission', () => {
    const markdownNode = (id: string) => ({
      type: { name: 'markdownBlock' },
      attrs: { blockId: id },
      content: { toJSON: () => ({ type: 'paragraph' }) },
    })
    const materialNode = (id: string) => ({
      type: { name: 'nativeNoteMaterial' },
      attrs: { blockId: id, kind: 'photo' },
    })
    const editorFor = (nodes: Array<ReturnType<typeof markdownNode> | ReturnType<typeof materialNode>>) => ({
      state: {
        doc: {
          forEach: (visit: (node: unknown, offset: number) => void) =>
            nodes.forEach((node, offset) => visit(node, offset)),
        },
      },
      storage: { markdown: { serializer: { serialize: () => '' } } },
    }) as unknown as Parameters<typeof serializeMaterialBlocks>[0]
    const noMaterial = () => undefined

    expect(() => serializeMaterialBlocks(
      editorFor([materialNode('missing')]),
      noMaterial,
      new Map(),
      new Map(),
      new Set(),
      new Map(),
    )).toThrow(/未能从编辑上下文还原/)

    expect(() => serializeMaterialBlocks(
      editorFor([markdownNode('duplicate'), markdownNode('duplicate')]),
      noMaterial,
      new Map([['duplicate', '原稿']]),
      new Map([['duplicate', '{}']]),
      new Set(['duplicate']),
      new Map(),
    )).toThrow(/正文块标识重复/)
  })

  it('does not emit a dirty change when saved source is loaded into the editor', async () => {
    Object.defineProperties(URL, {
      createObjectURL: { configurable: true, value: vi.fn(() => 'blob:continuous-material') },
      revokeObjectURL: { configurable: true, value: vi.fn() },
    })
    const onDirty = vi.fn()
    render(<Harness onDirty={onDirty} />)

    expect(await screen.findByLabelText('笔记正文')).toBeTruthy()
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(onDirty).not.toHaveBeenCalled()
  })

  it('splits the markdown container at the caret and keeps the right side with a new identity', async () => {
    Object.defineProperties(URL, {
      createObjectURL: { configurable: true, value: vi.fn(() => 'blob:continuous-material') },
      revokeObjectURL: { configurable: true, value: vi.fn() },
    })
    render(<Harness />)

    const body = await screen.findByLabelText('笔记正文')
    body.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}{ArrowRight}')
    const photo = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47, 3])], 'middle.png', { type: 'image/png' })
    await userEvent.upload(screen.getByLabelText('添加照片'), photo)
    await waitFor(() =>
      expect(body.querySelectorAll('[data-material-block-id]')).toHaveLength(3))

    await userEvent.click(screen.getByRole('button', { name: '读取正文原稿' }))
    await waitFor(() => expect(screen.getByTestId('read-blocks').textContent).not.toBe('[]'))
    const result = JSON.parse(screen.getByTestId('read-blocks').textContent ?? '[]') as MaterialDraftBlock[]

    expect(result.map((block) => block.kind)).toEqual([
      'markdown',
      'photo',
      'markdown',
      'photo',
      'markdown',
      'ink',
    ])
    expect(result[0]?.id).toBe(markdownA)
    const splitMarkdown = result[2]
    expect(splitMarkdown?.kind).toBe('markdown')
    expect(splitMarkdown?.id).not.toBe(markdownA)
    expect(result[3]?.id).toBe(photoId)
    expect(result[4]?.id).toBe(markdownB)
    expect(result[5]?.id).toBe(inkId)
  })
})
