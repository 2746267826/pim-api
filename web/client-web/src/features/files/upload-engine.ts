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
import { STORAGE_KEYS, getJSON, remove, setJSON } from '@/lib/storage'
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
  /** 完成时的清理纪元；合并时丢弃早于当前纪元的记录。 */
  clearEpoch?: number
}

type Listener = (items: TransferItem[]) => void

const CHUNK_MAX_RETRY = 2

function readPersistedHistory(): PersistedTransfer[] {
  return getJSON<PersistedTransfer[]>(STORAGE_KEYS.transferHistory, [])
}

function readClearEpoch(): number {
  const value = getJSON<number>(STORAGE_KEYS.transferHistoryClearEpoch, 0)
  return Number.isSafeInteger(value) && value >= 0 ? value : 0
}

export class UploadEngine {
  private queue: TransferItem[] = []
  private running = false
  private listeners = new Set<Listener>()
  /** 每个任务完成时的清理纪元，避免旧实例把已清理记录重新写回。 */
  private completionEpochs = new Map<string, number>()

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
    const epoch = readClearEpoch()
    return readPersistedHistory().filter((item) => (item.clearEpoch ?? 0) >= epoch)
  }

  historyCount(): number {
    return this.historyList().length
  }

  /** 只清理普通文件传输历史；进行中的上传队列和内存中的 File 继续保留。 */
  clearHistory(): number {
    const cleared = this.historyList().length
    const before = this.queue.length
    const epoch = readClearEpoch() + 1
    setJSON(STORAGE_KEYS.transferHistoryClearEpoch, epoch)
    for (const item of this.queue) {
      if (item.status === 'done' || item.status === 'failed' || item.status === 'rejected') {
        this.completionEpochs.delete(item.id)
      }
    }
    this.queue = this.queue.filter((item) => item.status === 'queued' || item.status === 'uploading')
    remove(STORAGE_KEYS.transferHistory)
    if (cleared > 0 || this.queue.length !== before) this.emit()
    return cleared
  }

  private persist() {
    const epoch = readClearEpoch()
    const done = this.queue
      .filter((t) => (t.status === 'done' || t.status === 'failed' || t.status === 'rejected') &&
        (this.completionEpochs.get(t.id) ?? 0) >= epoch)
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
        clearEpoch: this.completionEpochs.get(t.id) ?? epoch,
      }))
    // 写前重读：另一标签页可能刚清理历史或新增了完成记录。
    // 只合并当前清理纪元及之后的记录，旧纪元记录不会被重新写回。
    const merged = new Map<string, PersistedTransfer>()
    for (const item of done) merged.set(item.id, item)
    for (const item of readPersistedHistory()) {
      if ((item.clearEpoch ?? 0) >= epoch && !merged.has(item.id)) merged.set(item.id, item)
    }
    const next = [...merged.values()]
      .sort((left, right) => right.startedAt - left.startedAt)
      .slice(0, 100)
    if (next.length) setJSON(STORAGE_KEYS.transferHistory, next)
    else remove(STORAGE_KEYS.transferHistory)
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
      this.completionEpochs.set(item.id, readClearEpoch())
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
    for (const item of this.queue) {
      if (item.status !== 'queued' && item.status !== 'uploading') this.completionEpochs.delete(item.id)
    }
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
        this.completionEpochs.set(next.id, readClearEpoch())
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

  /** Provider 会话分片上传：云盘直传或内置存储；PUT 只使用返回的资源授权 URL。 */
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
          // 不带账号 Authorization；只向服务端返回的临时会话地址发送分片。
          headers: { 'Content-Range': contentRangeHeader(item.fileSize, chunk), 'Content-Type': 'application/octet-stream' },
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
