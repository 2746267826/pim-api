/*
 * 双通道上传引擎（03 §4）：
 * - 串行队列（一次一个文件）、逐文件进度、失败重试、历史持久化 localStorage
 * - ≤4MB：multipart 直传（服务器中继）
 * - >4MB：Graph 上传会话；浏览器直 PUT 分片（不带 Authorization 头）
 *   · 202 → 读 nextExpectedRanges 对齐下一分片
 *   · 201 → 完成，响应体为最终条目（取 id 回填 uploadedItemId）
 *   · 每分片最多重试 2 次
 * - >2GB：直接拒绝
 */
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import { filesApi } from './api'
import {
  contentRangeHeader,
  formatBytes,
  nextOffsetFromRanges,
  pickChannel,
  replanFrom,
  type ChunkRange,
} from './upload-chunk-plan'

export type TransferStatus = 'queued' | 'uploading' | 'done' | 'failed' | 'rejected'

export interface TransferItem {
  id: string
  providerId: string
  fileName: string
  fileSize: number
  targetPath: string
  /** 上传后的完整路径 */
  fullPath: string
  status: TransferStatus
  /** 0–1 */
  progress: number
  error?: string
  startedAt: number
  finishedAt?: number
  file: File
}

interface PersistedTransfer {
  id: string
  fileName: string
  fileSize: number
  targetPath: string
  fullPath: string
  status: TransferStatus
  error?: string
  startedAt: number
  finishedAt?: number
}

type Listener = (items: TransferItem[]) => void

const CHUNK_MAX_RETRY = 2

export class UploadEngine {
  private queue: TransferItem[] = []
  private running = false
  private listeners = new Set<Listener>()
  private history: PersistedTransfer[] = getJSON<PersistedTransfer[]>(STORAGE_KEYS.transferHistory, [])

  subscribe(fn: Listener): () => void {
    this.listeners.add(fn)
    fn(this.snapshot())
    return () => this.listeners.delete(fn)
  }

  private emit() {
    const snap = this.snapshot()
    for (const fn of this.listeners) fn(snap)
  }

  snapshot(): TransferItem[] {
    return [...this.queue].sort((a, b) => b.startedAt - a.startedAt)
  }

  historyList(): PersistedTransfer[] {
    return this.history
  }

  private persist() {
    const done = this.queue
      .filter((t) => t.status === 'done' || t.status === 'failed' || t.status === 'rejected')
      .map<PersistedTransfer>((t) => ({
        id: t.id,
        fileName: t.fileName,
        fileSize: t.fileSize,
        targetPath: t.targetPath,
        fullPath: t.fullPath,
        status: t.status,
        error: t.error,
        startedAt: t.startedAt,
        finishedAt: t.finishedAt,
      }))
    // 队列中未完成的也保留（页面刷新后仅作历史展示）
    this.history = [...done, ...this.history].slice(0, 100)
    setJSON(STORAGE_KEYS.transferHistory, this.history)
  }

  /** 入队（唯一入口；>2GB 直接标 rejected） */
  enqueue(input: { providerId: string; file: File; targetPath: string }) {
    const { file, targetPath } = input
    const dir = targetPath.endsWith('/') ? targetPath : `${targetPath}/`
    const item: TransferItem = {
      id: crypto.randomUUID(),
      providerId: input.providerId,
      fileName: file.name,
      fileSize: file.size,
      targetPath,
      fullPath: `${dir}${file.name}`,
      status: 'queued',
      progress: 0,
      startedAt: Date.now(),
      file,
    }
    if (pickChannel(file.size) === 'rejected') {
      item.status = 'rejected'
      item.error = `文件 ${formatBytes(file.size)} 超过 2GB 上限`
      item.finishedAt = Date.now()
    }
    this.queue.push(item)
    this.emit()
    void this.drain()
  }

  retry(id: string) {
    const item = this.queue.find((t) => t.id === id)
    if (!item || item.status === 'rejected') return
    item.status = 'queued'
    item.progress = 0
    item.error = undefined
    this.emit()
    void this.drain()
  }

  clearFinished() {
    this.queue = this.queue.filter((t) => t.status === 'queued' || t.status === 'uploading')
    this.persist()
    this.emit()
  }

  /* ── 串行执行 ── */
  private async drain() {
    if (this.running) return
    this.running = true
    try {
      for (;;) {
        const next = this.queue.find((t) => t.status === 'queued')
        if (!next) break
        next.status = 'uploading'
        this.emit()
        try {
          await this.uploadOne(next)
          next.status = 'done'
          next.progress = 1
        } catch (err) {
          next.status = 'failed'
          next.error = err instanceof Error ? err.message : String(err)
        }
        next.finishedAt = Date.now()
        this.persist()
        this.emit()
      }
    } finally {
      this.running = false
    }
  }

  private async uploadOne(item: TransferItem) {
    if (pickChannel(item.fileSize) === 'simple') {
      await filesApi.uploadSimple(item.providerId, item.fullPath, item.file)
      item.progress = 1
      this.emit()
      return
    }
    await this.uploadBySession(item)
  }

  /** Graph 会话分片直传（字节不经过 PIM 服务器；分片 PUT 不带认证头） */
  private async uploadBySession(item: TransferItem) {
    const session = await filesApi.createUploadSession(item.targetPath, item.fileName, item.providerId)
    let offset = 0
    let uploadedItemId: string | undefined

    while (offset < item.fileSize) {
      const chunks = replanFrom(item.fileSize, offset)
      const chunk: ChunkRange | undefined = chunks[0]
      if (!chunk) break
      const blob = item.file.slice(chunk.start, chunk.end + 1)

      let attempt = 0
      let handled = false
      for (;;) {
        const res = await fetch(session.uploadUrl, {
          method: 'PUT',
          // 刻意不带 Authorization：Graph 预授权 URL 免鉴权，带认证头会被拒
          headers: { 'Content-Range': contentRangeHeader(item.fileSize, chunk) },
          body: blob,
        })
        if (res.status === 201 || res.status === 200) {
          const body = (await res.json().catch(() => null)) as { id?: string } | null
          uploadedItemId = body?.id
          offset = chunk.end + 1
          handled = true
          break
        }
        if (res.status === 202) {
          const body = (await res.json().catch(() => null)) as { nextExpectedRanges?: string[] } | null
          const next = nextOffsetFromRanges(body?.nextExpectedRanges, item.fileSize)
          offset = next ?? chunk.end + 1
          handled = true
          break
        }
        attempt++
        if (attempt > CHUNK_MAX_RETRY) {
          throw new Error(`分片上传失败（HTTP ${res.status}，已重试 ${CHUNK_MAX_RETRY} 次）`)
        }
      }
      if (!handled) throw new Error('分片上传异常终止')
      item.progress = Math.min(0.99, offset / item.fileSize)
      this.emit()
    }

    await filesApi.completeUploadSession(item.targetPath, item.fileName, item.providerId, uploadedItemId)
    item.progress = 1
    this.emit()
  }
}

export const uploadEngine = new UploadEngine()
