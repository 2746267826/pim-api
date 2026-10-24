import { describe, expect, it } from 'vitest';
import {
  MAX_UPLOAD_BYTES,
  SIMPLE_UPLOAD_LIMIT,
  UPLOAD_CHUNK_ALIGNMENT,
  UPLOAD_CHUNK_MAX,
  UPLOAD_CHUNK_SIZE,
  contentRangeHeader,
  exceedsUploadLimit,
  isChunkCompliant,
  parseNextExpectedRange,
  planUploadChunks,
  requiresUploadSession,
} from '../uploadChunkPlan';

/**
 * REQ-14 / V4：分片规则是**平台硬约束**，算错会在真实上传时表现为
 * 「传完了但文件不完整」——最难排查的一类问题，因此逐条固定。
 */
describe('uploadChunkPlan', () => {
  it('默认块大小 10MiB 且是 320KiB 的整数倍', () => {
    expect(UPLOAD_CHUNK_SIZE).toBe(10 * 1024 * 1024);
    expect(UPLOAD_CHUNK_SIZE % UPLOAD_CHUNK_ALIGNMENT).toBe(0);
    expect(UPLOAD_CHUNK_SIZE).toBeLessThanOrEqual(UPLOAD_CHUNK_MAX);
  });

  it('500MB 文件：全部非末块都是 320KiB 整数倍且 ≤60MiB，偏移连续无缝', () => {
    const total = 500 * 1024 * 1024;
    const chunks = planUploadChunks(total);

    expect(chunks.length).toBeGreaterThan(1);
    expect(chunks.reduce((sum, c) => sum + c.length, 0)).toBe(total);

    let offset = 0;
    chunks.forEach((chunk, index) => {
      const isLast = index === chunks.length - 1;
      expect(chunk.start).toBe(offset);
      expect(isChunkCompliant(chunk, isLast)).toBe(true);
      if (!isLast) {
        expect(chunk.length % UPLOAD_CHUNK_ALIGNMENT).toBe(0);
      }
      expect(chunk.length).toBeLessThanOrEqual(UPLOAD_CHUNK_MAX);
      offset = chunk.end + 1;
    });
    expect(offset).toBe(total);
  });

  it('末块按实际剩余收尾（允许不是整数倍）', () => {
    const total = UPLOAD_CHUNK_SIZE * 2 + 12345;
    const chunks = planUploadChunks(total);

    expect(chunks).toHaveLength(3);
    expect(chunks[2].length).toBe(12345);
    expect(isChunkCompliant(chunks[2], true)).toBe(true);
    // 末块不合规的「非整数倍」是被允许的
    expect(chunks[2].length % UPLOAD_CHUNK_ALIGNMENT).not.toBe(0);
  });

  it('恰好整块时不留空分片', () => {
    const total = UPLOAD_CHUNK_SIZE * 3;
    const chunks = planUploadChunks(total);

    expect(chunks).toHaveLength(3);
    expect(chunks.every(c => c.length === UPLOAD_CHUNK_SIZE)).toBe(true);
  });

  it('0 字节不产生分片；负值/非法值同样不产生', () => {
    expect(planUploadChunks(0)).toEqual([]);
    expect(planUploadChunks(-1)).toEqual([]);
    expect(planUploadChunks(Number.NaN)).toEqual([]);
  });

  it('请求的块大小超过硬上限时被夹到 60MiB', () => {
    const chunks = planUploadChunks(UPLOAD_CHUNK_MAX * 2, UPLOAD_CHUNK_MAX * 5);
    expect(chunks.every(c => c.length <= UPLOAD_CHUNK_MAX)).toBe(true);
  });

  it('Content-Range 头格式符合 Graph 要求', () => {
    const chunks = planUploadChunks(UPLOAD_CHUNK_SIZE * 2 + 10);
    expect(contentRangeHeader(chunks[0], UPLOAD_CHUNK_SIZE * 2 + 10))
      .toBe(`bytes 0-${UPLOAD_CHUNK_SIZE - 1}/${UPLOAD_CHUNK_SIZE * 2 + 10}`);
    expect(contentRangeHeader(chunks[2], UPLOAD_CHUNK_SIZE * 2 + 10))
      .toBe(`bytes ${UPLOAD_CHUNK_SIZE * 2}-${UPLOAD_CHUNK_SIZE * 2 + 9}/${UPLOAD_CHUNK_SIZE * 2 + 10}`);
  });

  it('>4MB 走上传会话，≤4MB 走简单上传（平台分界）', () => {
    expect(requiresUploadSession(SIMPLE_UPLOAD_LIMIT)).toBe(false);
    expect(requiresUploadSession(SIMPLE_UPLOAD_LIMIT + 1)).toBe(true);
    expect(requiresUploadSession(500 * 1024 * 1024)).toBe(true);
  });

  it('P5：>2GB 判定为超限（提示改用 OneDrive 客户端）', () => {
    expect(exceedsUploadLimit(MAX_UPLOAD_BYTES)).toBe(false);
    expect(exceedsUploadLimit(MAX_UPLOAD_BYTES + 1)).toBe(true);
  });

  describe('断点续传的 nextExpectedRanges', () => {
    it('单区间返回起始偏移', () => {
      expect(parseNextExpectedRange(['12345-'])).toBe(12345);
    });

    it('多区间取第一个', () => {
      expect(parseNextExpectedRange(['100-', '200-300'])).toBe(100);
    });

    it('闭区间也能解析（只取起点）', () => {
      expect(parseNextExpectedRange(['5242880-10485759'])).toBe(5242880);
    });

    it('空/非法输入返回 null（调用方据此走「从头开始」而不是瞎续传）', () => {
      expect(parseNextExpectedRange([])).toBeNull();
      expect(parseNextExpectedRange(undefined)).toBeNull();
      expect(parseNextExpectedRange(null)).toBeNull();
      expect(parseNextExpectedRange(['abc'])).toBeNull();
      expect(parseNextExpectedRange([123])).toBeNull();
    });
  });
});
