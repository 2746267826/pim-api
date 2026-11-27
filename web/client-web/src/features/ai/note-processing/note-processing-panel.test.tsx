// @vitest-environment jsdom
import { beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteProcessingPanel } from './note-processing-panel'
import type { NoteProcessingPlan, NoteProcessingStatus } from './api'

const api = vi.hoisted(() => ({
  status: vi.fn(),
  plan: vi.fn(),
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

const plan: NoteProcessingPlan = {
  noteId,
  versionId,
  purpose: 'native-note-processing',
  purposeText: '生成当前固定版本的转写与整理结果',
  materialScope: 'FullVersion',
  materialScopeText: '当前固定版本的全部材料（正文、图片、手写与录音）',
  costNotice: '当前固定版本的全部材料会按账号默认目标处理；云端转写与多模态整理可能按服务商用量产生费用。',
  possibleCharge: true,
  authorizationRevision: '77777777-7777-4777-8777-777777777777',
  standingAuthorizationRevision: null,
  standingAuthorizationValid: false,
  asrRequired: true,
  targets: [
    {
      kind: 'Asr',
      required: true,
      paid: true,
      modelId: '88888888-8888-4888-8888-888888888888',
      modelRevision: '99999999-9999-4999-8999-999999999999',
      modelName: '合成转写模型',
      model: 'synthetic-asr',
      endpoint: 'https://private-asr.invalid/v1/chat/completions',
      route: 'cloud-asr',
      executionMode: 'Realtime',
      presetId: 'mimo-asr',
      presetVersion: '1',
    },
    {
      kind: 'Derived',
      required: true,
      paid: true,
      modelId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      modelRevision: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      modelName: '合成整理模型',
      model: 'synthetic-text',
      endpoint: 'https://private-derived.invalid/v1/',
      route: 'cloud',
      executionMode: 'Realtime',
      presetId: null,
      presetVersion: null,
    },
  ],
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
  api.plan.mockResolvedValue(plan)
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
  expect(screen.queryByText(/private-asr\.invalid/)).toBeNull()
  expect(screen.queryByText(/private-derived\.invalid/)).toBeNull()
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
  expect(screen.getByText(/private-asr\.invalid/)).toBeTruthy()
  expect(screen.getByText(/private-derived\.invalid/)).toBeTruthy()
})

it('uses a generic action failure for ordinary users and keeps the backend error for admins', async () => {
  api.start.mockRejectedValue(new Error('MiMo provider start failure'))
  const ordinary = page(false)

  const ordinaryButton = await screen.findByRole('button', { name: '使用账号默认模型发起' })
  expect((ordinaryButton as HTMLButtonElement).disabled).toBe(true)
  await userEvent.click(screen.getByRole('checkbox', { name: /我已核对以上处理目标/ }))
  await userEvent.click(ordinaryButton)
  expect(await screen.findByText(/发起未确认，请刷新核对/)).toBeTruthy()
  expect(screen.queryByText(/MiMo provider start failure/)).toBeNull()
  ordinary.unmount()

  const admin = page(true)
  const adminButton = await screen.findByRole('button', { name: '使用账号默认模型发起' })
  await userEvent.click(await screen.findByRole('checkbox', { name: /我已核对以上处理目标/ }))
  await userEvent.click(adminButton)
  expect(await screen.findByText(/MiMo provider start failure/)).toBeTruthy()
  admin.unmount()
})

it('shows the target, scope, cost and revision before sending the exact consent snapshot', async () => {
  api.start.mockResolvedValue(processing)
  page(false)

  expect(await screen.findByText('发起前处理计划')).toBeTruthy()
  expect(screen.getByText(/材料范围：当前固定版本的全部材料/)).toBeTruthy()
  expect(screen.getAllByText(/可能按服务商用量产生费用/).length).toBeGreaterThan(0)
  expect(screen.getByText(new RegExp(plan.authorizationRevision))).toBeTruthy()
  expect(screen.getByText(/合成转写模型/)).toBeTruthy()
  expect(screen.getByText(/合成整理模型/)).toBeTruthy()

  const button = screen.getByRole('button', { name: '使用账号默认模型发起' }) as HTMLButtonElement
  expect(button.disabled).toBe(true)
  expect(api.start).not.toHaveBeenCalled()

  await userEvent.click(screen.getByRole('checkbox', { name: /我已核对以上处理目标/ }))
  expect(button.disabled).toBe(false)
  await userEvent.click(button)

  expect(api.start).toHaveBeenCalledOnce()
  expect(api.start).toHaveBeenCalledWith({
    noteId,
    versionId,
    consent: {
      purpose: plan.purpose,
      materialScope: plan.materialScope,
      costNotice: plan.costNotice,
      possibleChargeAcknowledged: true,
      confirmed: true,
      authorizationRevision: plan.authorizationRevision,
      standingAuthorizationRevision: null,
    },
  })
})

it('shows the persisted authorization snapshot for automatic processing', async () => {
  api.status.mockResolvedValue({
    ...processing,
    autoStarted: true,
    manualStartAvailable: false,
    pendingReason: null,
    authorization: {
      purpose: plan.purpose,
      purposeText: plan.purposeText,
      materialScope: plan.materialScope,
      materialScopeText: plan.materialScopeText,
      costNotice: plan.costNotice,
      possibleCharge: plan.possibleCharge,
      confirmed: true,
      possibleChargeAcknowledged: true,
      authorizationRevision: plan.authorizationRevision,
      standingAuthorizationRevision: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      source: 'standing',
      targets: plan.targets,
    },
  } satisfies NoteProcessingStatus)
  page(false)

  expect(await screen.findByRole('heading', { name: '已持久化的处理授权' })).toBeTruthy()
  expect(screen.getByText(/来源：账号持续授权/)).toBeTruthy()
  expect(screen.getByText(/材料范围：当前固定版本的全部材料/)).toBeTruthy()
  expect(screen.getByText(new RegExp(plan.authorizationRevision))).toBeTruthy()
  expect(screen.getByText(/合成转写模型/)).toBeTruthy()
  expect(screen.getByText(/合成整理模型/)).toBeTruthy()
  expect(screen.queryByText(/private-asr\.invalid/)).toBeNull()
  expect(screen.queryByText(/private-derived\.invalid/)).toBeNull()
})
