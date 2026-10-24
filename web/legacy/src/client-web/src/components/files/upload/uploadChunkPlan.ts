/**
 * 浏览器直传的分片计划（REQ-14 / V4）。
 *
 * 平台硬约束（references/03-平台能力依据.md，Microsoft Learn `driveItem: createUploadSession`）：
 * - 分片大小必须是 **320 KiB（327,680 字节）的整数倍**；
 * - 单块硬上限 **60 MiB**；推荐 5–10 MiB；
 * - 分片必须**顺序**上传；
 * - 简单上传（PUT /content）仅 ≤4MB，超过必须走上传会话。
 *
 * 抽成纯函数是为了让这些规则可以被直接断言——分片算错会在真实上传时表现为
 * 「传完了但文件不完整」，属于最难排查的一类问题。
 */

/** 320 KiB：分片大小必须整除它。 */
export const UPLOAD_CHUNK_ALIGNMENT = 320 * 1024;

/** 单块硬上限 60 MiB。 */
export const UPLOAD_CHUNK_MAX = 60 * 1024 * 1024;

/** 推荐块大小 10 MiB（320KiB 的整数倍，且远低于硬上限）。 */
export const UPLOAD_CHUNK_SIZE = 10 * 1024 * 1024;

/** 超过该大小必须走上传会话（简单上传上限，Graph 平台限制）。 */
export const SIMPLE_UPLOAD_LIMIT = 4 * 1024 * 1024;

export interface UploadChunk {
  /** 起始字节偏移 */
  start: number;
  /** 结束字节偏移（含） */
  end: number;
  /** 该分片字节数 */
  length: number;
}

/**
 * 把文件切成合规分片。最后一块按实际剩余大小收尾（允许不是 320KiB 整数倍）。
 * 0 字节文件不产生分片（应由调用方走简单上传）。
 */
export function planUploadChunks(totalBytes: number, chunkSize: number = UPLOAD_CHUNK_SIZE): UploadChunk[] {
  if (!Number.isFinite(totalBytes) || totalBytes <= 0) return [];
  if (!Number.isFinite(chunkSize) || chunkSize <= 0) {
    throw new Error('分片大小必须是正数');
  }

  const size = Math.min(chunkSize, UPLOAD_CHUNK_MAX);
  const chunks: UploadChunk[] = [];
  let start = 0;
  while (start < totalBytes) {
    const length = Math.min(size, totalBytes - start);
    chunks.push({ start, end: start + length - 1, length });
    start += length;
  }
  return chunks;
}

/** `Content-Range` 头（Graph 要求 `bytes start-end/total`）。 */
export function contentRangeHeader(chunk: UploadChunk, totalBytes: number): string {
  return `bytes ${chunk.start}-${chunk.end}/${totalBytes}`;
}

/** 是否必须走上传会话（>4MB，或调用方显式要求）。 */
export function requiresUploadSession(sizeBytes: number): boolean {
  return sizeBytes > SIMPLE_UPLOAD_LIMIT;
}

/** P5：单文件上限 2GB；超过提示改用 OneDrive 客户端。 */
export const MAX_UPLOAD_BYTES = 2 * 1024 * 1024 * 1024;

export function exceedsUploadLimit(sizeBytes: number): boolean {
  return sizeBytes > MAX_UPLOAD_BYTES;
}

/** 校验一块是否合规（供断点续传恢复后自检，防止用错偏移继续）。 */
export function isChunkCompliant(chunk: UploadChunk, isLast: boolean): boolean {
  if (chunk.length <= 0) return false;
  if (chunk.length > UPLOAD_CHUNK_MAX) return false;
  if (!isLast && chunk.length % UPLOAD_CHUNK_ALIGNMENT !== 0) return false;
  return chunk.end === chunk.start + chunk.length - 1;
}

/**
 * 解析 Graph 的 `nextExpectedRanges`（断点续传用）。
 * 形如 `["12345-", "67890-99999"]`；返回下一个应上传的起始偏移（无则 null）。
 */
export function parseNextExpectedRange(ranges: unknown): number | null {
  if (!Array.isArray(ranges) || ranges.length === 0) return null;
  const first = ranges.find(value => typeof value === 'string');
  if (typeof first !== 'string') return null;
  const dash = first.indexOf('-');
  const startPart = dash < 0 ? first : first.slice(0, dash);
  const parsed = Number.parseInt(startPart, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}
