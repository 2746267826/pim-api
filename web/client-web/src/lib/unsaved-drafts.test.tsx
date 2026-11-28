// @vitest-environment jsdom
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import {
  listUnsavedDrafts,
  registerUnsavedDraft,
  resetUnsavedDraftsForTests,
  useUnsavedDraft,
  useUnsavedDrafts,
} from './unsaved-drafts'

function useHarness(active: boolean, title: string) {
  useUnsavedDraft(active, {
    id: 'note:1',
    kind: 'note',
    title,
    detail: '正文尚未保存',
    saveHint: '返回笔记页保存',
    saveHref: '/native-notes',
  })
  return useUnsavedDrafts()
}

afterEach(() => {
  cleanup()
  resetUnsavedDraftsForTests()
})

describe('未保存草稿保护登记', () => {
  it('激活时登记，内容变化时更新，失活或卸载后注销', async () => {
    const hook = renderHook(({ active, title }) => useHarness(active, title), {
      initialProps: { active: true, title: '笔记 A' },
    })

    await waitFor(() => expect(hook.result.current).toHaveLength(1))
    expect(hook.result.current[0]).toMatchObject({ id: 'note:1', title: '笔记 A' })

    act(() => hook.rerender({ active: true, title: '笔记 B' }))
    await waitFor(() => expect(hook.result.current[0]?.title).toBe('笔记 B'))

    act(() => hook.rerender({ active: false, title: '笔记 B' }))
    await waitFor(() => expect(hook.result.current).toHaveLength(0))
  })

  it('重复注销不会误删同 id 的新登记', async () => {
    const first = registerUnsavedDraft({
      id: 'quick-note:draft',
      kind: 'quick-note',
      title: '旧草稿',
      detail: '旧内容',
      saveHint: '保存',
    })
    const second = registerUnsavedDraft({
      id: 'quick-note:draft',
      kind: 'quick-note',
      title: '新草稿',
      detail: '新内容',
      saveHint: '保存',
    })

    first()
    expect(listUnsavedDrafts()).toHaveLength(1)
    expect(listUnsavedDrafts()[0]?.title).toBe('新草稿')

    second()
    expect(listUnsavedDrafts()).toHaveLength(0)
  })
})
