// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const filesApiMock = vi.hoisted(() => ({
  uploadSimple: vi.fn(),
  createUploadSession: vi.fn(),
  completeUploadSession: vi.fn(),
}))

vi.mock('./api', () => ({ filesApi: filesApiMock }))

import { uploadEngine } from './upload-engine'

async function waitFor(check: () => boolean, timeoutMillis = 3_000) {
  const started = Date.now()
  while (!check()) {
    if (Date.now() - started > timeoutMillis) throw new Error('condition not met before timeout')
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
}

function bigFile(name = 'big.bin') {
  return new File([new Uint8Array(5 * 1024 * 1024)], name, { type: 'application/octet-stream' })
}

describe('云盘预授权直传传输门禁（Q04）', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
    uploadEngine.clearFinished()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('远程 HTTP 预授权地址在发送分片前停止上传，且日志不泄露签名参数', async () => {
    const warning = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    filesApiMock.createUploadSession.mockResolvedValue({
      uploadUrl: 'http://upload.example.com/session?sig=SECRET-SIGNATURE',
      expirationDateTime: null,
      path: '/',
      fileName: 'big.bin',
    })

    uploadEngine.enqueue({ providerId: 'account-a', file: bigFile(), targetPath: '/' })
    await waitFor(() => uploadEngine.snapshot()[0]?.status === 'failed')

    const item = uploadEngine.snapshot()[0]
    expect(fetchMock).not.toHaveBeenCalled()
    expect(filesApiMock.completeUploadSession).not.toHaveBeenCalled()
    expect(item.error).toContain('HTTPS')
    expect(item.error).not.toContain('SECRET-SIGNATURE')
    const logs = JSON.stringify(warning.mock.calls)
    expect(logs).toContain('upload.example.com')
    expect(logs).not.toContain('SECRET-SIGNATURE')
  })

  it('HTTPS 预授权地址继续分片上传且不携带 PIM Authorization', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      new Response(JSON.stringify({ id: 'drive-item-1' }), {
        status: 201,
        headers: { 'content-type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)
    filesApiMock.createUploadSession.mockResolvedValue({
      uploadUrl: 'https://upload.example.com/session?sig=SECRET-SIGNATURE',
      expirationDateTime: null,
      path: '/',
      fileName: 'big.bin',
    })
    filesApiMock.completeUploadSession.mockResolvedValue({})

    uploadEngine.enqueue({ providerId: 'account-a', file: bigFile(), targetPath: '/' })
    await waitFor(() => uploadEngine.snapshot()[0]?.status === 'done')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]![0]).toBe('https://upload.example.com/session?sig=SECRET-SIGNATURE')
    const init = fetchMock.mock.calls[0]![1] as RequestInit
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined()
    expect(filesApiMock.completeUploadSession).toHaveBeenCalledWith(
      '/',
      'big.bin',
      'account-a',
      'drive-item-1',
    )
  })
})
