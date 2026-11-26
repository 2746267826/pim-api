// @vitest-environment jsdom
import { beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteProcessingPanel } from './note-processing-panel'
import type { NoteProcessingStatus } from './api'

const api = vi.hoisted(() => ({
  status: vi.fn(),
  start: vi.fn(),
}))
vi.mock('./api', () => ({ noteProcessingApi: api }))

const ownerId = '11111111-1111-4111-8111-111111111111'
const noteId = '22222222-2222-4222-8222-222222222222'
const versionId = '33333333-3333-4333-8333-333333333333'
const processing: NoteProcessingStatus = {
  processingId: '44444444-4444-4444-8444-444444444444',
  noteId,
  versionId,
  state: 'VendorOverallState' as NoteProcessingStatus['state'],
  autoProcessingEnabled: true,
  autoStarted: false,
  manualStartAvailable: true,
  incrementalScopeSupported: false,
  pendingReason: 'MiMo provider pending diagnostic',
  asr: {
    kind: 'Asr',
    state: 'VendorChainState' as NoteProcessingStatus['asr']['state'],
    taskId: '55555555-5555-4555-8555-555555555555',
    taskCreated: false,
    revision: null,
    phase: 'VendorPhase',
    totalItems: 1,
    completedItems: 0,
    failedItems: 1,
    hasResult: false,
    errors: ['MiMo raw ASR error'],
    waitingReason: 'provider waiting diagnostic',
  },
  derived: {
    kind: 'Derived',
    state: 'WaitingForAsr',
    taskId: '66666666-6666-4666-8666-666666666666',
    taskCreated: false,
    revision: null,
    phase: 'VendorDerivedPhase',
    totalItems: 1,
    completedItems: 0,
    failedItems: 0,
    hasResult: false,
    errors: [],
    waitingReason: 'MiMo provider derived wait',
  },
  artifacts: [],
  issues: [{ code: 'internal_code', message: 'backend issue diagnostic', task: 'Asr' }],
  createdAt: '2026-11-25T00:00:00Z',
  updatedAt: '2026-11-25T00:01:00Z',
  scope: 'FullVersion',
}

function page(showVersionDetails = false) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}>
    <NoteProcessingPanel ownerId={ownerId} serverBinding="https://synthetic.invalid"
      noteId={noteId} versionId={versionId} showVersionDetails={showVersionDetails} />
  </QueryClientProvider>)
}

beforeEach(() => {
  cleanup()
  vi.clearAllMocks()
  api.status.mockResolvedValue(processing)
})

it('hides raw processing state, backend reasons and diagnostics from ordinary users', async () => {
  page(false)

  expect(await screen.findByRole('heading', { name: '服务器笔记处理' })).toBeTruthy()
  expect(screen.getAllByText('处理状态待核对').length).toBeGreaterThan(0)
  expect(screen.getByText('处理尚未发起，请核对材料是否完整或稍后手动重试；页面不会自动重发。')).toBeTruthy()
  expect(screen.getAllByText('任务尚未开始，请刷新核对').length).toBeGreaterThan(0)
  expect(screen.getByText('部分材料或处理项尚未完成，请刷新核对；页面不会自动重发。')).toBeTruthy()
  expect(screen.getByText('处理未完整完成，请刷新核对；页面不会自动重发。')).toBeTruthy()
  expect(screen.queryByText(/MiMo/)).toBeNull()
  expect(screen.queryByText(/provider/)).toBeNull()
  expect(screen.queryByText(/Vendor/)).toBeNull()
  expect(screen.queryByText(/backend issue diagnostic/)).toBeNull()
})

it('keeps raw processing state, backend reasons and diagnostics for admins', async () => {
  page(true)

  expect(await screen.findByText('VendorOverallState')).toBeTruthy()
  expect(screen.getByText('VendorChainState')).toBeTruthy()
  expect(screen.getByText('MiMo provider pending diagnostic')).toBeTruthy()
  expect(screen.getByText('provider waiting diagnostic')).toBeTruthy()
  expect(screen.getByText('MiMo provider derived wait')).toBeTruthy()
  expect(screen.getByText('MiMo raw ASR error')).toBeTruthy()
  expect(screen.getByText('backend issue diagnostic')).toBeTruthy()
})

it('uses a generic action failure for ordinary users and keeps the backend error for admins', async () => {
  api.start.mockRejectedValue(new Error('MiMo provider start failure'))
  const ordinary = page(false)

  await userEvent.click(await screen.findByRole('button', { name: '使用账号默认模型发起' }))
  expect(await screen.findByText(/发起未确认，请刷新核对/)).toBeTruthy()
  expect(screen.queryByText(/MiMo provider start failure/)).toBeNull()
  ordinary.unmount()

  const admin = page(true)
  await userEvent.click(await screen.findByRole('button', { name: '使用账号默认模型发起' }))
  expect(await screen.findByText(/MiMo provider start failure/)).toBeTruthy()
  admin.unmount()
})
