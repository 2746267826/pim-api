// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import type { RecycleBinItem } from './api'

const api = vi.hoisted(() => ({
  list: vi.fn(),
  restore: vi.fn(),
  purge: vi.fn(),
}))

vi.mock('./api', () => ({ recycleBinApi: api }))
vi.mock('@/lib/notify', () => ({ notifySuccess: vi.fn(), notifyError: vi.fn() }))

import { RecycleBinPage } from './recycle-bin-page'

const note: RecycleBinItem = {
  id: '11111111-1111-4111-8111-111111111111',
  domain: 'note',
  kind: 'note',
  title: '课堂笔记',
  deletedAt: '2026-11-25T02:00:00Z',
  source: 'builtin',
  location: null,
  sizeBytes: null,
  restorable: true,
  sourcePending: false,
  sourceMessage: null,
}
const localFile: RecycleBinItem = {
  id: '22222222-2222-4222-8222-222222222222',
  domain: 'file',
  kind: 'file',
  title: '内置报告.txt',
  deletedAt: '2026-11-25T02:01:00Z',
  source: 'builtin',
  location: '/内置报告.txt',
  sizeBytes: 12,
  restorable: true,
  sourcePending: false,
  sourceMessage: null,
}
const cloudFile: RecycleBinItem = {
  id: '33333333-3333-4333-8333-333333333333',
  domain: 'file',
  kind: 'file',
  title: 'OneDrive 合同.docx',
  deletedAt: '2026-11-25T02:02:00Z',
  source: 'onedrive',
  location: '/合同.docx',
  sizeBytes: 20,
  restorable: false,
  sourcePending: true,
  sourceMessage: 'OneDrive 的恢复与永久清理需由外部来源确认',
}
const task: RecycleBinItem = {
  id: '44444444-4444-4444-8444-444444444444',
  domain: 'task',
  kind: 'task',
  title: '已删任务',
  deletedAt: '2026-11-25T02:03:00Z',
  source: 'manual',
  location: '工作',
  sizeBytes: null,
  restorable: true,
  sourcePending: false,
  sourceMessage: null,
}
const calendar: RecycleBinItem = {
  id: '55555555-5555-4555-8555-555555555555',
  domain: 'calendar',
  kind: 'event',
  title: '已删日程',
  deletedAt: '2026-11-25T02:04:00Z',
  source: 'manual',
  location: '日历',
  sizeBytes: null,
  restorable: true,
  sourcePending: false,
  sourceMessage: null,
}

function page(items: RecycleBinItem[]) {
  return {
    items,
    groups: [
      { domain: 'note', totalCount: items.filter((item) => item.domain === 'note').length },
      { domain: 'file', totalCount: items.filter((item) => item.domain === 'file').length },
      { domain: 'task', totalCount: items.filter((item) => item.domain === 'task').length },
      { domain: 'calendar', totalCount: items.filter((item) => item.domain === 'calendar').length },
    ],
    totalCount: items.length,
    page: 1,
    pageSize: 50,
    totalPages: 1,
  }
}

function receipt(
  action: string,
  entries: { item: RecycleBinItem; status: string; message?: string; index: number }[],
) {
  return {
    receiptId: '99999999-9999-4999-8999-999999999999',
    operationId: '88888888-8888-4888-8888-888888888888',
    action,
    succeeded: entries.filter((entry) => entry.status === 'success').length,
    failed: entries.filter((entry) => entry.status === 'failed').length,
    sourcePending: entries.filter((entry) => entry.status === 'source_pending').length,
    items: entries.map((entry) => ({
      index: entry.index,
      domain: entry.item.domain,
      id: entry.item.id,
      title: entry.item.title,
      status: entry.status,
      action,
      source: entry.item.source,
      message: entry.message ?? null,
      errorCode: entry.status === 'failed' ? 40901 : null,
    })),
  }
}

let client: QueryClient

function renderPage() {
  client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  return render(
    <QueryClientProvider client={client}>
      <RecycleBinPage />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  cleanup()
  client?.clear()
})

describe('统一回收站页面', () => {
  it('按笔记/文件/任务/日历四域分组，并标明外部来源待处理', async () => {
    api.list.mockResolvedValue(page([note, localFile, cloudFile, task, calendar]))
    renderPage()

    expect(await screen.findByRole('heading', { name: '笔记' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: '文件' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: '任务' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: '日历' })).toBeTruthy()
    expect(screen.getByText('课堂笔记')).toBeTruthy()
    expect(screen.getByText('已删任务')).toBeTruthy()
    expect(screen.getByText('已删日程')).toBeTruthy()
    expect(screen.getAllByText('来源待处理').length).toBeGreaterThan(0)
    expect(screen.getByText('OneDrive 的恢复与永久清理需由外部来源确认')).toBeTruthy()
  })

  it('跨域多选后展示逐项回执，只重试未成功项且不重复提交成功项', async () => {
    api.list.mockResolvedValue(page([note, cloudFile, task]))
    api.restore
      .mockResolvedValueOnce(receipt('restore', [
        { item: note, status: 'success', message: '笔记已恢复', index: 0 },
        { item: cloudFile, status: 'source_pending', message: 'OneDrive 待处理', index: 1 },
        { item: task, status: 'failed', message: '请先恢复所属任务本', index: 2 },
      ]))
      .mockResolvedValueOnce(receipt('restore', [
        { item: cloudFile, status: 'source_pending', message: '仍在等待 OneDrive', index: 0 },
        { item: task, status: 'failed', message: '仍失败', index: 1 },
      ]))
    renderPage()

    await userEvent.click(await screen.findByLabelText('选择 课堂笔记'))
    await userEvent.click(screen.getByLabelText('选择 OneDrive 合同.docx'))
    await userEvent.click(screen.getByLabelText('选择 已删任务'))
    await userEvent.click(screen.getByRole('button', { name: '恢复选中' }))

    const receiptCard = await screen.findByLabelText('服务器批次回执')
    expect(within(receiptCard).getByText('成功 1 · 失败 1 · 来源待处理 1')).toBeTruthy()
    expect(within(receiptCard).getByText('笔记已恢复')).toBeTruthy()
    expect(api.restore).toHaveBeenNthCalledWith(1, [
      { domain: 'note', id: note.id, expectedDeletedAt: note.deletedAt },
      { domain: 'file', id: cloudFile.id, expectedDeletedAt: cloudFile.deletedAt },
      { domain: 'task', id: task.id, expectedDeletedAt: task.deletedAt },
    ], false)
    await waitFor(() => expect(screen.getByText('已选 2 项')).toBeTruthy())

    await userEvent.click(screen.getByRole('button', { name: '重试未成功项（2）' }))
    await waitFor(() => expect(api.restore).toHaveBeenCalledTimes(2))
    expect(api.restore.mock.calls[1][0]).toEqual([
      { domain: 'file', id: cloudFile.id, expectedDeletedAt: cloudFile.deletedAt },
      { domain: 'task', id: task.id, expectedDeletedAt: task.deletedAt },
    ])
  })

  it('永久清理必须输入确认词，并按服务器回执保留来源待处理', async () => {
    api.list.mockResolvedValue(page([localFile, cloudFile]))
    api.purge.mockResolvedValue(receipt('purge', [
      { item: localFile, status: 'success', message: '内置文件已永久删除', index: 0 },
      { item: cloudFile, status: 'source_pending', message: 'Google 待处理', index: 1 },
    ]))
    renderPage()

    await userEvent.click(await screen.findByLabelText('选择 内置报告.txt'))
    await userEvent.click(screen.getByLabelText('选择 OneDrive 合同.docx'))
    await userEvent.click(screen.getByRole('button', { name: '永久删除选中' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: '永久删除' })).toHaveProperty('disabled', true)
    await userEvent.type(within(dialog).getByRole('textbox'), '永久删除')
    await userEvent.click(within(dialog).getByRole('button', { name: '永久删除' }))

    await waitFor(() => expect(api.purge).toHaveBeenCalledTimes(1))
    expect(api.purge.mock.calls[0][0]).toEqual([
      { domain: 'file', id: localFile.id, expectedDeletedAt: localFile.deletedAt },
      { domain: 'file', id: cloudFile.id, expectedDeletedAt: cloudFile.deletedAt },
    ])
    expect(await screen.findByText('成功 1 · 失败 0 · 来源待处理 1')).toBeTruthy()
  })

  it('网络断开时显示“未确认”，不把任何项目在本地标记为成功', async () => {
    api.list.mockResolvedValue(page([note]))
    api.restore.mockRejectedValue(new Error('Failed to fetch'))
    renderPage()

    await userEvent.click(await screen.findByLabelText('选择 课堂笔记'))
    await userEvent.click(screen.getByRole('button', { name: '恢复选中' }))

    expect(await screen.findByText('结果未确认')).toBeTruthy()
    expect(screen.getByText(/不会把任何项目标记为成功/)).toBeTruthy()
    expect(screen.queryByLabelText('服务器批次回执')).toBeNull()
    expect(screen.getByText('已选 1 项')).toBeTruthy()
  })
})
