// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { STORAGE_KEYS, setJSON } from '@/lib/storage'

const filesApi = vi.hoisted(() => ({
  uploadSimple: vi.fn(() => new Promise(() => undefined)),
  createUploadSession: vi.fn(),
  completeUploadSession: vi.fn(),
}))

vi.mock('./api', () => ({ filesApi }))

import { UploadEngine } from './upload-engine'

const history = [
  {
    id: 'history-1',
    fileName: 'a.txt',
    fileSize: 1,
    targetPath: '/',
    fullPath: '/a.txt',
    status: 'done' as const,
    startedAt: 1,
    finishedAt: 2,
  },
  {
    id: 'history-2',
    fileName: 'b.txt',
    fileSize: 2,
    targetPath: '/',
    fullPath: '/b.txt',
    status: 'failed' as const,
    startedAt: 3,
    finishedAt: 4,
  },
]

beforeEach(() => {
  localStorage.clear()
  vi.clearAllMocks()
  filesApi.uploadSimple.mockImplementation(() => new Promise(() => undefined))
})

afterEach(() => {
  localStorage.clear()
})

describe('文件传输历史清理', () => {
  it('只删除普通历史，第二次清理返回 0 且不重复落盘', () => {
    setJSON(STORAGE_KEYS.transferHistory, history)
    const engine = new UploadEngine()

    expect(engine.historyCount()).toBe(2)
    expect(engine.clearHistory()).toBe(2)
    expect(engine.historyCount()).toBe(0)
    expect(localStorage.getItem(STORAGE_KEYS.transferHistory)).toBeNull()

    expect(engine.clearHistory()).toBe(0)
    expect(localStorage.getItem(STORAGE_KEYS.transferHistory)).toBeNull()
  })

  it('清理历史时保留仍在队列中的上传任务', () => {
    const engine = new UploadEngine()
    engine.enqueue({
      providerId: 'provider-1',
      file: new File(['content'], 'active.txt', { type: 'text/plain' }),
      targetPath: '/',
    })

    expect(engine.snapshot()).toHaveLength(1)
    expect(engine.snapshot()[0]?.status).toBe('uploading')

    expect(engine.clearHistory()).toBe(0)
    expect(engine.snapshot()).toHaveLength(1)
    expect(engine.snapshot()[0]?.status).toBe('uploading')
    expect(filesApi.uploadSimple).toHaveBeenCalledTimes(1)
  })

  it('清理后下一次上传只记录新任务，已清历史不会复活', async () => {
    setJSON(STORAGE_KEYS.transferHistory, history)
    filesApi.uploadSimple.mockResolvedValue(undefined)
    const engine = new UploadEngine()

    engine.enqueue({
      providerId: 'provider-1',
      file: new File(['first'], 'first.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(engine.snapshot()[0]?.status).toBe('done'))
    expect(engine.historyCount()).toBe(3)

    expect(engine.clearHistory()).toBe(3)
    expect(engine.snapshot()).toHaveLength(0)
    expect(localStorage.getItem(STORAGE_KEYS.transferHistory)).toBeNull()

    engine.enqueue({
      providerId: 'provider-1',
      file: new File(['second'], 'second.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(engine.snapshot()[0]?.status).toBe('done'))

    expect(engine.historyCount()).toBe(1)
    expect(engine.historyList().map((item) => item.fileName)).toEqual(['second.txt'])
  })

  it('另一实例清理历史后，本实例使用旧内存历史完成上传不会复活记录', async () => {
    setJSON(STORAGE_KEYS.transferHistory, history)
    filesApi.uploadSimple.mockResolvedValue(undefined)
    const stale = new UploadEngine()
    const cleaner = new UploadEngine()

    expect(cleaner.clearHistory()).toBe(2)
    expect(localStorage.getItem(STORAGE_KEYS.transferHistory)).toBeNull()

    stale.enqueue({
      providerId: 'provider-1',
      file: new File(['new'], 'new.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(stale.snapshot()[0]?.status).toBe('done'))

    expect(stale.historyList().map((item) => item.fileName)).toEqual(['new.txt'])
    expect(JSON.parse(localStorage.getItem(STORAGE_KEYS.transferHistory) ?? '[]')).toEqual([
      expect.objectContaining({ fileName: 'new.txt' }),
    ])
  })

  it('另一实例清理历史后，本实例队列中清理前完成的任务不会复活', async () => {
    filesApi.uploadSimple.mockResolvedValue(undefined)
    const stale = new UploadEngine()
    const cleaner = new UploadEngine()

    stale.enqueue({
      providerId: 'provider-1',
      file: new File(['old'], 'old.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(stale.snapshot()[0]?.status).toBe('done'))
    expect(cleaner.clearHistory()).toBe(1)

    stale.enqueue({
      providerId: 'provider-1',
      file: new File(['new'], 'new.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(stale.snapshot()[0]?.status).toBe('done'))

    expect(stale.historyList().map((item) => item.fileName)).toEqual(['new.txt'])
    expect(JSON.parse(localStorage.getItem(STORAGE_KEYS.transferHistory) ?? '[]')).toEqual([
      expect.objectContaining({ fileName: 'new.txt' }),
    ])
  })

  it('落盘前合并另一实例新增的历史，不会只用构造时读到的旧副本覆盖', async () => {
    filesApi.uploadSimple.mockResolvedValue(undefined)
    const engine = new UploadEngine()
    setJSON(STORAGE_KEYS.transferHistory, [{
      id: 'other-tab',
      fileName: 'other.txt',
      fileSize: 1,
      targetPath: '/',
      fullPath: '/other.txt',
      status: 'done',
      startedAt: 1,
      finishedAt: 2,
    }])

    engine.enqueue({
      providerId: 'provider-1',
      file: new File(['new'], 'new.txt', { type: 'text/plain' }),
      targetPath: '/',
    })
    await vi.waitFor(() => expect(engine.snapshot()[0]?.status).toBe('done'))

    expect(engine.historyList().map((item) => item.fileName).sort()).toEqual(['new.txt', 'other.txt'])
  })
})
