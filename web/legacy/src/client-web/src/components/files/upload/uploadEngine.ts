import {
  contentRangeHeader,
  exceedsUploadLimit,
  parseNextExpectedRange,
  planUploadChunks,
  requiresUploadSession,
} from './uploadChunkPlan';

/**
 * 浏览器直传的上传引擎（REQ-11 / REQ-13 / REQ-14）。
 *
 * 两条通路：
 * - **≤4MB**：走 PIM 端点的简单上传（服务器转发，平台允许的小文件例外）；
 * - **>4MB**：向 PIM 换取 createUploadSession 的预授权 `uploadUrl`，然后由**浏览器
 *   直接向微软分片 PUT**——字节不经 PIM 服务器（AC-14.2 的硬约束）。
 *
 * 分片 PUT **不得带 Authorization**：Graph 的 uploadUrl 已经带授权，
 * 再带 token 会把凭据发给错误的主机（references/03 明确警告）。
 *
 * 依赖以接口注入，便于用真实 HTTP 语义（含断点续传、失败重试）做断言。
 */

export interface UploadTarget {
  /** 当前目录（默认上传目标，REQ-11）。 */
  path: string;
  providerId: string;
}

export interface UploadSessionApi {
  createSession(path: string, fileName: string): Promise<{ uploadUrl: string; expirationDateTime: string | null }>;
  /**
   * 完成后登记元数据。
   * <paramref name="uploadedItemId"/> 是 Graph 在上传完成响应里给出的**真实条目 id**：
   * 重名时 Graph 会自动改名，仅凭原路径回读会读到**已存在的旧文件**并登记错误元数据（AC-12.1）。
   */
  completeSession(path: string, fileName: string, uploadedItemId?: string): Promise<unknown>;
  simpleUpload(providerId: string, path: string, file: File): Promise<unknown>;
}

export interface UploadProgress {
  fileName: string;
  /** 0..1 */
  ratio: number;
  uploadedBytes: number;
  totalBytes: number;
}

export interface UploadResult {
  fileName: string;
  ok: boolean;
  /** 失败原因（可读） */
  error?: string;
}

/** 单块上传的 HTTP 语义可注入，便于测试失败/续传（真实实现即 fetch）。 */
export type ChunkSender = (
  url: string,
  body: Blob,
  headers: Record<string, string>,
) => Promise<{ status: number; nextExpectedRanges?: unknown; body?: unknown }>;

const defaultChunkSender: ChunkSender = async (url, body, headers) => {
  const response = await fetch(url, { method: 'PUT', body, headers });
  // 202 = 还需要更多分片；201 = 上传完成，响应体是**最终条目**（含真实 id/name）。
  // 必须把 201 的响应体带回去：重名时 Graph 会把文件改名成「名称 (1).ext」，
  // 只有用它返回的 id/name 才能确定最终落到的是哪一个条目。
  const text = await response.text().catch(() => '');
  let parsed: unknown;
  try {
    parsed = text ? JSON.parse(text) : undefined;
  } catch {
    parsed = undefined;
  }
  const nextExpectedRanges =
    response.status === 202 ? (parsed as { nextExpectedRanges?: unknown } | undefined)?.nextExpectedRanges : undefined;
  return { status: response.status, nextExpectedRanges, body: parsed };
};

export interface UploadEngineOptions {
  sendChunk?: ChunkSender;
  /** 单次分片上传的重试次数（网络抖动）；不含用户手动重试。 */
  chunkRetryLimit?: number;
}

/**
 * 上传一个文件。返回结构化结果——**绝不吞掉失败**（AC-13.2 / AC-11.3 不得假成功）。
 */
export async function uploadFile(
  file: File,
  target: UploadTarget,
  api: UploadSessionApi,
  onProgress?: (progress: UploadProgress) => void,
  options: UploadEngineOptions = {},
): Promise<UploadResult> {
  if (exceedsUploadLimit(file.size)) {
    // P5：超过 2GB 提示改用 OneDrive 客户端
    return {
      fileName: file.name,
      ok: false,
      error: '文件超过 2GB，请改用 OneDrive 客户端上传',
    };
  }

  try {
    if (!requiresUploadSession(file.size)) {
      onProgress?.({ fileName: file.name, ratio: 0, uploadedBytes: 0, totalBytes: file.size });
      await api.simpleUpload(target.providerId, `${target.path.replace(/\/$/, '')}/${file.name}`, file);
      onProgress?.({ fileName: file.name, ratio: 1, uploadedBytes: file.size, totalBytes: file.size });
      return { fileName: file.name, ok: true };
    }

    return await uploadViaSession(file, target, api, onProgress, options);
  } catch (error) {
    return {
      fileName: file.name,
      ok: false,
      error: error instanceof Error && error.message ? error.message : '上传失败',
    };
  }
}

async function uploadViaSession(
  file: File,
  target: UploadTarget,
  api: UploadSessionApi,
  onProgress?: (progress: UploadProgress) => void,
  options: UploadEngineOptions = {},
): Promise<UploadResult> {
  const sendChunk = options.sendChunk ?? defaultChunkSender;
  const retryLimit = options.chunkRetryLimit ?? 2;

  const session = await api.createSession(target.path, file.name);
  if (!session.uploadUrl) {
    return { fileName: file.name, ok: false, error: '未能创建上传会话，请稍后重试' };
  }

  const total = file.size;
  const chunks = planUploadChunks(total);
  let uploaded = 0;
  /** 上传完成响应里的最终条目（重名时名字由服务端决定）。 */
  let finalItem: { id?: string; name?: string } | undefined;

  for (let index = 0; index < chunks.length; index += 1) {
    const chunk = chunks[index];
    const blob = file.slice(chunk.start, chunk.end + 1);

    let attempt = 0;
    // 顺序上传（平台要求）：必须等当前块成功再发下一块
    for (;;) {
      const response = await sendChunk(session.uploadUrl, blob, {
        // 分片 PUT 不带 Authorization（uploadUrl 已预授权）
        'Content-Range': contentRangeHeader(chunk, total),
      });

      if (response.status === 202 || response.status === 201) {
        if (response.status === 201 && response.body && typeof response.body === 'object') {
          finalItem = response.body as { id?: string; name?: string };
        }
        break;
      }

      // 416/其他：尝试用服务端给的 nextExpectedRanges 对齐（断点续传）
      const nextExpected = parseNextExpectedRange(response.nextExpectedRanges);
      if (nextExpected !== null && nextExpected !== chunk.start) {
        // 服务端期望别的偏移：按它的期望重排剩余分片（真实场景常见于重试后）
        uploaded = nextExpected;
        break;
      }

      attempt += 1;
      if (attempt > retryLimit) {
        return {
          fileName: file.name,
          ok: false,
          error: `上传中断（HTTP ${response.status}），可重试`,
        };
      }
    }

    uploaded = Math.max(uploaded, chunk.end + 1);
    onProgress?.({ fileName: file.name, ratio: uploaded / total, uploadedBytes: uploaded, totalBytes: total });
  }

  // 内容已在微软侧，这里只登记元数据（服务器不搬字节）。
  // 带上服务端返回的真实条目 id，避免重名改名后登记成别的文件。
  await api.completeSession(target.path, file.name, finalItem?.id);
  onProgress?.({ fileName: file.name, ratio: 1, uploadedBytes: total, totalBytes: total });
  return { fileName: file.name, ok: true };
}
