// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, configure, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'

const mocks = vi.hoisted(() => ({
  enqueue: vi.fn(),
  flush: vi.fn(() => Promise.resolve()),
  clearPending: vi.fn(),
  overview: vi.fn(),
  exportZip: vi.fn(),
  downloadBlob: vi.fn(),
  queueState: {
    configured: true,
    pendingCount: 2,
    sentCount: 7,
    droppedCount: 1,
    flushing: false,
    nextAttemptAtMs: Date.parse('2026-11-25T12:01:00Z'),
    lastError: null as string | null,
  },
}))

vi.mock('../api', () => ({
  diagnosticsApi: {
    overview: mocks.overview,
    exportZip: mocks.exportZip,
  },
}))

vi.mock('../queue', () => ({
  diagnosticQueue: {
    subscribe: () => () => undefined,
    getSnapshot: () => mocks.queueState,
    enqueue: mocks.enqueue,
    flush: mocks.flush,
    clearPending: mocks.clearPending,
  },
}))

vi.mock('@/lib/download', () => ({
  downloadBlob: mocks.downloadBlob,
}))

import { DiagnosticsPage } from './diagnostics-page'

configure({ asyncUtilTimeout: 10_000 })

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <DiagnosticsPage />
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

describe('DiagnosticsPage', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders device-isolated events, queue state and submits a manual report', async () => {
    mocks.overview.mockResolvedValue({
      totalCount: 2,
      last24HoursCount: 1,
      devices: [
        { deviceId: 'android-1', platform: 'android', eventCount: 1, lastEventAtUtc: '2026-11-25T12:00:00Z' },
        { deviceId: 'web-1', platform: 'web', eventCount: 1, lastEventAtUtc: '2026-11-25T12:00:00Z' },
      ],
      items: [
        {
          id: '11111111-1111-4111-8111-111111111111',
          deviceId: 'android-1',
          platform: 'android',
          kind: 'crash',
          severity: 'fatal',
          occurredAtUtc: '2026-11-25T11:59:00Z',
          receivedAtUtc: '2026-11-25T12:00:00Z',
          appVersion: '1.0.0',
          osVersion: 'Android 15',
          message: '笔记页崩溃',
        },
        {
          id: '22222222-2222-4222-8222-222222222222',
          deviceId: 'web-1',
          platform: 'web',
          kind: 'manual',
          severity: 'warning',
          occurredAtUtc: '2026-11-25T11:58:00Z',
          receivedAtUtc: '2026-11-25T12:00:00Z',
          appVersion: '0.1.0',
          osVersion: 'Web',
          message: '搜索结果为空',
        },
      ],
    })
    mocks.enqueue.mockReturnValue({ clientEventId: 'manual-1' })

    renderPage()

    expect(await screen.findByText('笔记页崩溃')).toBeTruthy()
    expect(screen.getByText('搜索结果为空')).toBeTruthy()
    expect(screen.getByText('Android · android-1')).toBeTruthy()
    expect(screen.getByText('7')).toBeTruthy()

    fireEvent.change(screen.getByLabelText('问题描述'), {
      target: { value: '导出按钮无响应' },
    })
    fireEvent.click(screen.getByRole('button', { name: '提交诊断报告' }))

    await waitFor(() => {
      expect(mocks.enqueue).toHaveBeenCalledWith(expect.objectContaining({
        kind: 'manual',
        severity: 'warning',
        message: '导出按钮无响应',
      }))
    })
    expect(mocks.flush).toHaveBeenCalled()
    expect(screen.getByText('已加入本机持久队列；离线时会自动退避重试。')).toBeTruthy()
  }, 20_000)

  it('supports cancelling an export and retrying after failure', async () => {
    mocks.overview.mockResolvedValue({
      totalCount: 0,
      last24HoursCount: 0,
      devices: [],
      items: [],
    })
    mocks.exportZip
      .mockImplementationOnce((_params, signal: AbortSignal) => new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))
      }))
      .mockResolvedValueOnce(new Blob(['zip'], { type: 'application/zip' }))

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '一键导出 ZIP' }))
    fireEvent.click(await screen.findByRole('button', { name: '取消导出' }))

    expect(await screen.findByText('导出已取消。')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: '重试导出' }))

    expect(await screen.findByText('诊断 ZIP 已生成并开始下载。')).toBeTruthy()
    expect(mocks.downloadBlob).toHaveBeenCalledTimes(1)
  }, 20_000)

  it('filters and exports by platform plus device id when the id is shared', async () => {
    mocks.overview.mockResolvedValue({
      totalCount: 0,
      last24HoursCount: 0,
      devices: [
        { deviceId: 'shared-id', platform: 'android', eventCount: 1, lastEventAtUtc: '2026-11-25T12:00:00Z' },
        { deviceId: 'shared-id', platform: 'web', eventCount: 1, lastEventAtUtc: '2026-11-25T12:00:00Z' },
      ],
      items: [],
    })
    mocks.exportZip.mockResolvedValue(new Blob(['zip'], { type: 'application/zip' }))

    renderPage()
    await screen.findByRole('option', { name: /Web · shared-id/ })
    fireEvent.change(await screen.findByLabelText('设备'), {
      target: { value: 'web:shared-id' },
    })

    await waitFor(() => {
      expect(mocks.overview).toHaveBeenLastCalledWith(
        expect.objectContaining({ deviceId: 'shared-id', platform: 'web', limit: 100 }),
        expect.anything(),
      )
    })

    fireEvent.click(screen.getByRole('button', { name: '一键导出 ZIP' }))
    await waitFor(() => {
      expect(mocks.exportZip).toHaveBeenCalledWith(
        expect.objectContaining({ deviceId: 'shared-id', platform: 'web', days: 7 }),
        expect.anything(),
      )
    })
  }, 20_000)
})
