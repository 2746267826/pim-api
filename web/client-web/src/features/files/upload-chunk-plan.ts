/*
 * 上传分片计划（03 §4 双通道引擎的纯逻辑部分，可单测）：
 * - ≤4MB：multipart 直传（服务器中转字节）
 * - >4MB（≤2GB）：Graph 上传会话，浏览器直 PUT 分片（不带认证头）
 *   · 分片 10MiB；单块硬上限 60MiB
 *   · 非末块须 320KiB 对齐
 *   · 202 = 继续（读 nextExpectedRanges 对齐）；201 = 完成
 *   · >2GB 客户端直接拒绝
 */

export const SIMPLE_UPLOAD_LIMIT = 4 * 1024 * 1024
export const MAX_UPLOAD_BYTES = 2 * 1024 * 1024 * 1024
export const UPLOAD_CHUNK_SIZE = 10 * 1024 * 1024
export const MAX_CHUNK_SIZE = 60 * 1024 * 1024
export const CHUNK_ALIGNMENT = 320 * 1024

export type UploadChannel = 'simple' | 'session' | 'rejected'

export interface ChunkRange {
  index: number
  start: number
  end: number // 含
  size: number
}

/** 通道选择（≤4MB 直传；>4MB 会话；>2GB 拒绝） */
export function pickChannel(size: number): UploadChannel {
  if (size > MAX_UPLOAD_BYTES) return 'rejected'
  if (size <= SIMPLE_UPLOAD_LIMIT) return 'simple'
  return 'session'
}

/** 分片计划：10MiB 步长，末块余数；非末块按 320KiB 对齐（末块不受限） */
export function planChunks(totalBytes: number, chunkSize = UPLOAD_CHUNK_SIZE): ChunkRange[] {
  if (chunkSize <= 0 || chunkSize > MAX_CHUNK_SIZE) {
    throw new Error(`分片大小非法：${chunkSize}（上限 ${MAX_CHUNK_SIZE}）`)
  }
  if (totalBytes <= 0) return []
  const chunks: ChunkRange[] = []
  let start = 0
  let index = 0
  while (start < totalBytes) {
    const isLast = start + chunkSize >= totalBytes
    let size = Math.min(chunkSize, totalBytes - start)
    if (!isLast) {
      // 非末块对齐（向下取整到对齐边界），并保证 > 0
      const aligned = Math.floor(size / CHUNK_ALIGNMENT) * CHUNK_ALIGNMENT
      size = aligned > 0 ? aligned : size
    }
    chunks.push({ index, start, end: start + size - 1, size })
    start += size
    index++
  }
  return chunks
}

/** Content-Range 头值（Graph 上传会话语义） */
export function contentRangeHeader(totalBytes: number, chunk: ChunkRange): string {
  return `bytes ${chunk.start}-${chunk.end}/${totalBytes}`
}

/** 解析 202 响应的 nextExpectedRanges → 下一个分片起点（无则该块完成） */
export function nextOffsetFromRanges(nextExpectedRanges: string[] | undefined, totalBytes: number): number | null {
  if (!nextExpectedRanges?.length) return null
  const first = nextExpectedRanges[0]
  const m = /^(\d+)/.exec(first)
  if (!m) return null
  const offset = Number(m[1])
  return offset >= totalBytes ? null : offset
}

/** 从指定偏移重排分片（断点续传） */
export function replanFrom(totalBytes: number, offset: number, chunkSize = UPLOAD_CHUNK_SIZE): ChunkRange[] {
  const rest = planChunks(totalBytes - offset, chunkSize)
  return rest.map((c, i) => ({ index: i, start: c.start + offset, end: c.end + offset, size: c.size }))
}

/** 人类可读大小 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
