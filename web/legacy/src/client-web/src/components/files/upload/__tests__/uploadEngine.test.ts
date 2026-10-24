import { describe, expect, it, vi } from 'vitest';
import { uploadFile, type UploadSessionApi, type ChunkSender } from '../uploadEngine';
import { SIMPLE_UPLOAD_LIMIT, UPLOAD_CHUNK_SIZE } from '../uploadChunkPlan';

/**
 * REQ-11 / REQ-13 / REQ-14：上传引擎。
 *
 * 关键是**真实交互语义**：分片顺序、Content-Range 正确、字节不经服务器、
 * 失败必须如实上报（不得假成功）、超限给出明确原因。
 */
function makeFile(name: string, size: number): File {
  // jsdom 的 File 支持 slice；内容用确定性的字节填充
  const bytes = new Uint8Array(size);
  for (let i = 0; i < size; i += 1) bytes[i] = i % 251;
  return new File([bytes], name, { type: 'application/octet-stream' });
}

/** 记录每次分片 PUT 的形状，并按真实语义回应。 */
function makeSender(options: { failOnCall?: number; expectedRanges?: unknown } = {}) {
  const calls: { url: string; size: number; headers: Record<string, string> }[] = [];
  let call = 0;
  const sender: ChunkSender = async (url, body, headers) => {
    calls.push({ url, size: body.size, headers });
    call += 1;
    if (options.failOnCall === call) {
      return { status: 503 };
    }
    return { status: 202, nextExpectedRanges: options.expectedRanges };
  };
  return { sender, calls };
}

function makeApi(overrides: Partial<UploadSessionApi> = {}) {
  const api: UploadSessionApi = {
    createSession: vi.fn(async () => ({ uploadUrl: 'https://upload.example.com/s1', expirationDateTime: null })),
    completeSession: vi.fn(async () => ({ ok: true })),
    simpleUpload: vi.fn(async () => ({ ok: true })),
    ...overrides,
  };
  return api;
}

describe('uploadEngine / REQ-11 入口与目标目录', () => {
  it('AC-11.1 ≤4MB 走简单上传，目标是当前目录', async () => {
    const api = makeApi();
    const file = makeFile('note.txt', 1024);

    const result = await uploadFile(file, { path: '/文档', providerId: 'p1' }, api);

    expect(result.ok).toBe(true);
    expect(api.simpleUpload).toHaveBeenCalledWith('p1', '/文档/note.txt', file);
    expect(api.createSession).not.toHaveBeenCalled();
  });

  it('根目录上传不会产生双斜杠路径', async () => {
    const api = makeApi();
    await uploadFile(makeFile('a.txt', 10), { path: '/', providerId: 'p1' }, api);

    expect(api.simpleUpload).toHaveBeenCalledWith('p1', '/a.txt', expect.anything());
  });

  it('AC-11.3 反面：超限给出明确原因，不得假成功', async () => {
    const api = makeApi();
    // 超限判定发生在切片之前，只需 size/name —— 真去分配 3GB 会拖垮测试进程
    const file = { name: 'huge.bin', size: 3 * 1024 * 1024 * 1024 } as unknown as File;

    const result = await uploadFile(file, { path: '/', providerId: 'p1' }, api);

    expect(result.ok).toBe(false);
    expect(result.error).toContain('2GB');
    expect(api.simpleUpload).not.toHaveBeenCalled();
    expect(api.createSession).not.toHaveBeenCalled();
  });
});

describe('uploadEngine / REQ-14 浏览器直传', () => {
  it('AC-14.2 >4MB 走上传会话，分片直接发往 uploadUrl 且**不带 Authorization**', async () => {
    const api = makeApi();
    const { sender, calls } = makeSender();
    const file = makeFile('big.bin', UPLOAD_CHUNK_SIZE * 2 + 100);

    const result = await uploadFile(file, { path: '/文档', providerId: 'p1' }, api, undefined, { sendChunk: sender });

    expect(result.ok).toBe(true);
    expect(api.createSession).toHaveBeenCalledWith('/文档', 'big.bin');
    expect(calls).toHaveLength(3);
    // 所有分片都发往微软的 uploadUrl
    expect(calls.every(c => c.url === 'https://upload.example.com/s1')).toBe(true);
    // 关键：分片 PUT 不带 Authorization（uploadUrl 已预授权）
    expect(calls.every(c => !('Authorization' in c.headers))).toBe(true);
    // 简单上传未被使用（字节不经服务器）
    expect(api.simpleUpload).not.toHaveBeenCalled();
  });

  it('Content-Range 按顺序、连续、首尾正确', async () => {
    const api = makeApi();
    const { sender, calls } = makeSender();
    const total = UPLOAD_CHUNK_SIZE * 2 + 100;
    const file = makeFile('big.bin', total);

    await uploadFile(file, { path: '/', providerId: 'p1' }, api, undefined, { sendChunk: sender });

    expect(calls[0].headers['Content-Range']).toBe(`bytes 0-${UPLOAD_CHUNK_SIZE - 1}/${total}`);
    expect(calls[1].headers['Content-Range']).toBe(`bytes ${UPLOAD_CHUNK_SIZE}-${UPLOAD_CHUNK_SIZE * 2 - 1}/${total}`);
    expect(calls[2].headers['Content-Range']).toBe(`bytes ${UPLOAD_CHUNK_SIZE * 2}-${total - 1}/${total}`);
    expect(calls.map(c => c.size)).toEqual([UPLOAD_CHUNK_SIZE, UPLOAD_CHUNK_SIZE, 100]);
  });

  it('AC-13.1 进度随分片推进，最终到 100%', async () => {
    const api = makeApi();
    const { sender } = makeSender();
    const progress: number[] = [];

    await uploadFile(
      makeFile('big.bin', UPLOAD_CHUNK_SIZE * 2),
      { path: '/', providerId: 'p1' },
      api,
      p => progress.push(p.ratio),
      { sendChunk: sender },
    );

    expect(progress.length).toBeGreaterThanOrEqual(3);
    expect(progress[progress.length - 1]).toBe(1);
    // 单调不减
    for (let i = 1; i < progress.length; i += 1) {
      expect(progress[i]).toBeGreaterThanOrEqual(progress[i - 1]);
    }
  });

  it('完成后登记元数据（服务器只登记结果，不接收字节）', async () => {
    const api = makeApi();
    const { sender } = makeSender();

    await uploadFile(makeFile('big.bin', UPLOAD_CHUNK_SIZE), { path: '/文档', providerId: 'p1' }, api, undefined, { sendChunk: sender });

    // 第 3 个参数是服务端返回的真实条目 id（本例 201 未带 body，故为 undefined）
    expect(api.completeSession).toHaveBeenCalledWith('/文档', 'big.bin', undefined);
  });

  it('AC-12.1 重名改名后按**服务端返回的真实 id**登记，而不是原路径', async () => {
    const api = makeApi();
    // 上传完成（201）时 Graph 返回最终条目：名字已被改成「报告 (1).pdf」
    const sender: ChunkSender = async () => ({
      status: 201,
      body: { id: 'renamed-item-9', name: '报告 (1).pdf' },
    });

    const result = await uploadFile(
      makeFile('报告.pdf', UPLOAD_CHUNK_SIZE),
      { path: '/文档', providerId: 'p1' },
      api,
      undefined,
      { sendChunk: sender },
    );

    expect(result.ok).toBe(true);
    // 必须把服务端 id 传给登记接口，否则会按原路径读到已存在的旧文件
    expect(api.completeSession).toHaveBeenCalledWith('/文档', '报告.pdf', 'renamed-item-9');
  });

  it('AC-13.2 分片持续失败时如实返回失败与可重试原因（不假成功）', async () => {
    const api = makeApi();
    // 持续失败：每一块都失败，耗尽重试预算后必须如实上报
    const sender: ChunkSender = async () => ({ status: 503 });
    const progress: number[] = [];

    const result = await uploadFile(
      makeFile('big.bin', UPLOAD_CHUNK_SIZE),
      { path: '/', providerId: 'p1' },
      api,
      p => progress.push(p.ratio),
      { sendChunk: sender, chunkRetryLimit: 1 },
    );

    expect(result.ok).toBe(false);
    expect(result.error).toContain('可重试');
    // 失败时不得登记为完成（否则会留下半截文件伪装成功）
    expect(api.completeSession).not.toHaveBeenCalled();
  });

  it('网络抖动后重试成功（重试预算内）', async () => {
    const api = makeApi();
    let call = 0;
    const sender: ChunkSender = async () => {
      call += 1;
      // 第一次失败，后续成功
      return call === 1 ? { status: 503 } : { status: 201 };
    };

    const result = await uploadFile(
      makeFile('big.bin', UPLOAD_CHUNK_SIZE),
      { path: '/', providerId: 'p1' },
      api,
      undefined,
      { sendChunk: sender, chunkRetryLimit: 2 },
    );

    expect(result.ok).toBe(true);
    expect(api.completeSession).toHaveBeenCalled();
  });

  it('创建会话失败时给出可读原因', async () => {
    const api = makeApi({ createSession: vi.fn(async () => ({ uploadUrl: '', expirationDateTime: null })) });

    const result = await uploadFile(
      makeFile('big.bin', UPLOAD_CHUNK_SIZE),
      { path: '/', providerId: 'p1' },
      api,
      undefined,
      { sendChunk: makeSender().sender },
    );

    expect(result.ok).toBe(false);
    expect(result.error).toContain('上传会话');
  });

  it('恰好 4MB 仍走简单上传（平台分界是「超过 4MB」）', async () => {
    const api = makeApi();
    await uploadFile(makeFile('edge.bin', SIMPLE_UPLOAD_LIMIT), { path: '/', providerId: 'p1' }, api);
    expect(api.simpleUpload).toHaveBeenCalled();
    expect(api.createSession).not.toHaveBeenCalled();
  });

  it('4MB + 1 字节走上传会话', async () => {
    const api = makeApi();
    const { sender } = makeSender();
    await uploadFile(
      makeFile('edge.bin', SIMPLE_UPLOAD_LIMIT + 1),
      { path: '/', providerId: 'p1' },
      api,
      undefined,
      { sendChunk: sender },
    );
    expect(api.createSession).toHaveBeenCalled();
    expect(api.simpleUpload).not.toHaveBeenCalled();
  });
});
