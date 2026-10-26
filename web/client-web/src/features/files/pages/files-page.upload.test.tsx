// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { FileItem, FileProvider } from '../types'
import { STORAGE_KEYS } from '@/lib/storage'

const api = vi.hoisted(() => ({
  providers: vi.fn(), listItems: vi.fn(), syncStatus: vi.fn(), search: vi.fn(),
  uploadSimple: vi.fn(), createUploadSession: vi.fn(), completeUploadSession: vi.fn(), setDefault: vi.fn(),
}))
vi.mock('../api', () => ({ filesApi: api }))
vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))

import { FilesPage } from './files-page'
import { uploadEngine } from '../upload-engine'

const account = (id: string, provider: 'onedrive' | 'google'): FileProvider => ({
  id, provider, accountName: id, isDefault: id === 'account-a', status: 'connected',
  clientId: null, driveId: null, accountId: null, syncStatus: 'idle', syncedItemCount: 0,
  lastSyncAt: null, lastError: null, tokenExpiresAt: null,
})
const accounts = [account('account-a', 'onedrive'), account('account-b', 'google')]
let stored: FileItem[]
let client: QueryClient

function item(providerId: string, path: string, size: number): FileItem {
  return {
    id: crypto.randomUUID(), providerId, externalFileId: 'fixture-id', parentExternalFileId: null,
    path, name: path.split('/').at(-1)!, itemType: 'file', mimeType: 'text/plain', size,
    etag: null, contentHash: null, createdAt: new Date().toISOString(), modifiedAt: new Date().toISOString(),
    syncedAt: new Date().toISOString(), indexStatus: 'indexed',
  }
}

async function openPage(ready = true) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: 0 } } })
  render(<QueryClientProvider client={client}><FilesPage /></QueryClientProvider>)
  if (ready) await waitFor(() => expect((screen.getByRole('button', { name: '上传文件' }) as HTMLButtonElement).disabled).toBe(false))
}

function picker() { return screen.getByLabelText('选择上传文件') as HTMLInputElement }
function queue() { return screen.getByRole('region', { name: '传输队列' }) }

describe('文件上传用户闭环（真实页面、查询缓存和上传引擎；仅 API 为隔离 fixture）', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
    uploadEngine.clearFinished()
    stored = []
    api.providers.mockResolvedValue(accounts)
    api.syncStatus.mockResolvedValue({ syncStatus: 'idle', syncedItemCount: 0 })
    api.listItems.mockImplementation(async ({ providerId, path, type }) => {
      const items = type === 'folder' ? [] : stored.filter((i) => i.providerId === providerId && i.path.slice(0, i.path.lastIndexOf('/')) === (path === '/' ? '' : path))
      return { result: { items, totalCount: items.length, totalPages: 1, page: 1, pageSize: 100 } }
    })
    api.uploadSimple.mockImplementation(async (providerId: string, path: string, file: File) => {
      const result = item(providerId, path, file.size)
      stored.push(result)
      return result
    })
  })
  afterEach(() => { cleanup(); client?.clear() })

  it('没有第三方云盘时可直接在内置存储上传', async () => {
    api.providers.mockResolvedValue([{ ...account('builtin', 'onedrive'), provider: 'local', accountName: '内置存储（服务器）', isDefault: true }])
    await openPage()
    expect(screen.getByText('服务器持久化存储 · 无需连接云盘')).toBeTruthy()
    fireEvent.change(picker(), { target: { files: [new File(['本地附件'], 'note.txt', { type: 'text/plain' })] } })
    await waitFor(() => expect(api.uploadSimple).toHaveBeenCalled())
    expect(api.uploadSimple.mock.calls.at(-1)?.[0]).toBe('builtin')
    await waitFor(() => expect(screen.getByText('note.txt')).toBeTruthy())
  })

  it('默认云盘失效时保留选择，只有手动切换后才允许上传到内置盘', async () => {
    api.providers.mockResolvedValue([
      { ...account('account-a', 'onedrive'), status: 'expired' },
      { ...account('builtin', 'onedrive'), provider: 'local', isDefault: false },
    ])
    await openPage(false)
    await screen.findByRole('alert')
    expect((screen.getByRole('button', { name: '上传文件' }) as HTMLButtonElement).disabled).toBe(true)
    expect(api.uploadSimple).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText('选择网盘账号'), { target: { value: 'builtin' } })
    await waitFor(() => expect((screen.getByRole('button', { name: '上传文件' }) as HTMLButtonElement).disabled).toBe(false))
    expect(api.uploadSimple).not.toHaveBeenCalled()
  })

  it('可见按钮打开可多选的文件选择器', async () => {
    await openPage()
    const input = picker()
    expect(input.type).toBe('file')
    expect(input.multiple).toBe(true)
    const click = vi.spyOn(input, 'click')
    await userEvent.click(screen.getByRole('button', { name: '上传文件' }))
    expect(click).toHaveBeenCalledOnce()
  })

  it('多文件传到当前账号和目录，完成后无需手动刷新就能看见文件', async () => {
    localStorage.setItem(STORAGE_KEYS.fileBrowserMemory, JSON.stringify({ path: '/收件箱' }))
    await openPage()
    const files = [new File(['first'], 'first.txt'), new File(['second'], 'second.txt')]
    await userEvent.upload(picker(), files)
    await waitFor(() => expect(api.uploadSimple).toHaveBeenCalledTimes(2))
    expect(api.uploadSimple).toHaveBeenNthCalledWith(1, 'account-a', '/收件箱/first.txt', files[0])
    expect(api.uploadSimple).toHaveBeenNthCalledWith(2, 'account-a', '/收件箱/second.txt', files[1])
    await screen.findByRole('button', { name: 'first.txt' })
    await screen.findByRole('button', { name: 'second.txt' })
    expect(within(queue()).getAllByText('已完成')).toHaveLength(2)
  })

  it('切换账号后不会继续上传到旧账号或旧目录，同一文件可以再次选择', async () => {
    localStorage.setItem(STORAGE_KEYS.fileBrowserMemory, JSON.stringify({ path: '/旧目录' }))
    await openPage()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: '选择网盘账号' }), 'account-b')
    const file = new File(['repeat'], 'repeat.txt')
    await userEvent.upload(picker(), file)
    await screen.findByRole('button', { name: 'repeat.txt' })
    await userEvent.upload(picker(), file)
    await waitFor(() => expect(api.uploadSimple).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(within(queue()).getAllByText('已完成')).toHaveLength(2))
    for (const args of api.uploadSimple.mock.calls) expect(args).toEqual(['account-b', '/repeat.txt', file])
  })

  it('搜索中上传仍落当前目录，并退出搜索以展示真实完成结果', async () => {
    localStorage.setItem(STORAGE_KEYS.fileBrowserMemory, JSON.stringify({ path: '/收件箱', searchScope: 'all' }))
    api.search.mockResolvedValue({ items: [], totalCount: 0, totalPages: 1 })
    await openPage()
    const search = screen.getByPlaceholderText('搜索文件…') as HTMLInputElement
    await userEvent.type(search, 'unrelated')
    await waitFor(() => expect(api.search).toHaveBeenCalledWith('unrelated', 1, undefined))
    const file = new File(['visible'], 'visible.txt')
    await userEvent.upload(picker(), file)
    expect(search.value).toBe('')
    expect(api.uploadSimple).toHaveBeenCalledWith('account-a', '/收件箱/visible.txt', file)
    await screen.findByRole('button', { name: 'visible.txt' })
  })

  it('失败不显示完成也不伪造列表条目，重试成功后才刷新列表', async () => {
    api.uploadSimple.mockRejectedValueOnce(new Error('fixture: 网盘拒绝写入'))
    await openPage()
    await userEvent.upload(picker(), new File(['retry'], 'retry.txt'))
    await within(queue()).findByText('失败')
    expect(within(queue()).queryByText('已完成')).toBeNull()
    expect(screen.queryByRole('button', { name: 'retry.txt' })).toBeNull()
    await userEvent.click(within(queue()).getByRole('button', { name: '重试' }))
    await screen.findByRole('button', { name: 'retry.txt' })
    await within(queue()).findByText('已完成')
  })

  it('超过 2GB 的文件由既有引擎拒绝，不发上传请求', async () => {
    await openPage()
    const file = new File(['small fixture'], 'too-large.txt')
    Object.defineProperty(file, 'size', { value: 2 * 1024 ** 3 + 1 })
    await userEvent.upload(picker(), file)
    await within(queue()).findByText('已拒绝')
    expect(api.uploadSimple).not.toHaveBeenCalled()
    expect(api.createUploadSession).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'too-large.txt' })).toBeNull()
  })

  it('来源加载异常时禁用上传并提供重试，避免误导用户必须连接云盘', async () => {
    api.providers.mockResolvedValue([])
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><FilesPage /></QueryClientProvider>)
    await screen.findByText('暂时无法加载文件来源')
    expect((screen.getByRole('button', { name: '上传文件' }) as HTMLButtonElement).disabled).toBe(true)
    expect(screen.getByRole('button', { name: '重试' })).toBeTruthy()
    expect(api.uploadSimple).not.toHaveBeenCalled()
  })

  it('拖放和文件粘贴仍然进入同一上传队列，普通文本粘贴不被拦截', async () => {
    await openPage()
    const region = screen.getByRole('region', { name: '文件列表，可拖放或粘贴文件上传' })
    expect(region.tabIndex).toBe(0)
    const dropped = new File(['drop'], 'drop.txt')
    fireEvent.drop(region, { dataTransfer: { files: [dropped] } })
    await screen.findByRole('button', { name: 'drop.txt' })
    const pasted = new File(['paste'], 'paste.txt')
    expect(fireEvent.paste(region, { clipboardData: { files: [pasted] } })).toBe(false)
    await screen.findByRole('button', { name: 'paste.txt' })
    expect(fireEvent.paste(region, { clipboardData: { files: [] } })).toBe(true)
  })
})
