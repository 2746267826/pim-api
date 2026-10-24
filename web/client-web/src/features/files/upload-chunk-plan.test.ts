import { describe, expect, it } from 'vitest'
import {
  CHUNK_ALIGNMENT,
  MAX_UPLOAD_BYTES,
  SIMPLE_UPLOAD_LIMIT,
  contentRangeHeader,
  nextOffsetFromRanges,
  pickChannel,
  planChunks,
  replanFrom,
} from './upload-chunk-plan'

const MB = 1024 * 1024

describe('pickChannel（双通道阈值）', () => {
  it('≤4MB → simple', () => {
    expect(pickChannel(0)).toBe('simple')
    expect(pickChannel(SIMPLE_UPLOAD_LIMIT)).toBe('simple')
  })
  it('4MB~2GB → session', () => {
    expect(pickChannel(SIMPLE_UPLOAD_LIMIT + 1)).toBe('session')
    expect(pickChannel(MAX_UPLOAD_BYTES)).toBe('session')
  })
  it('>2GB → rejected', () => {
    expect(pickChannel(MAX_UPLOAD_BYTES + 1)).toBe('rejected')
  })
})

describe('planChunks（10MiB 分片 + 末块余数）', () => {
  it('25MiB → 3 块（10/10/5）', () => {
    const chunks = planChunks(25 * MB)
    expect(chunks.map((c) => c.size)).toEqual([10 * MB, 10 * MB, 5 * MB])
    expect(chunks[2]!.end).toBe(25 * MB - 1)
  })

  it('分片连续无空洞（拼起来正好等于总长）', () => {
    const total = 37 * MB + 12345
    const chunks = planChunks(total)
    let cursor = 0
    for (const c of chunks) {
      expect(c.start).toBe(cursor)
      cursor = c.end + 1
    }
    expect(cursor).toBe(total)
  })

  it('非末块 320KiB 对齐', () => {
    // 用 1MiB 分片（非 320KiB 整数倍）验证对齐逻辑
    const chunks = planChunks(3 * MB, MB)
    for (const c of chunks.slice(0, -1)) {
      expect(c.size % CHUNK_ALIGNMENT).toBe(0)
    }
  })

  it('非法分片大小抛错、0 字节返回空', () => {
    expect(() => planChunks(100, 61 * MB)).toThrow()
    expect(planChunks(0)).toEqual([])
  })
})

describe('Content-Range 与续传', () => {
  it('header 形如 bytes 0-10485759/26214400', () => {
    const chunks = planChunks(25 * MB)
    expect(contentRangeHeader(25 * MB, chunks[0]!)).toBe(`bytes 0-${10 * MB - 1}/${25 * MB}`)
  })

  it('nextExpectedRanges → 下一个偏移；到末尾返回 null', () => {
    expect(nextOffsetFromRanges(['10485760-'], 25 * MB)).toBe(10 * MB)
    expect(nextOffsetFromRanges(['26214400-'], 25 * MB)).toBeNull()
    expect(nextOffsetFromRanges(undefined, 25 * MB)).toBeNull()
  })

  it('replanFrom 从断点重排（起点正确且总量补齐）', () => {
    const total = 25 * MB
    const rest = replanFrom(total, 10 * MB)
    expect(rest[0]!.start).toBe(10 * MB)
    const sum = rest.reduce((acc, c) => acc + c.size, 0)
    expect(sum).toBe(total - 10 * MB)
    expect(rest[rest.length - 1]!.end).toBe(total - 1)
  })
})
