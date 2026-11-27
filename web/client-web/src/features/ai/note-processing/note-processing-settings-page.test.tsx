// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { NoteProcessingSettingsPage } from './note-processing-settings-page'
import type { NoteProcessingPreferences } from './api'

const api = vi.hoisted(() => ({
  preferences: vi.fn(),
  savePreferences: vi.fn(),
}))
vi.mock('./api', () => ({ noteProcessingApi: api }))
vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => ({ user: { id: '11111111-1111-4111-8111-111111111111', role: 'admin' } }),
}))
vi.mock('@/lib/notify', () => ({
  notifyError: vi.fn(),
  notifySuccess: vi.fn(),
}))

const revision = '22222222-2222-4222-8222-222222222222'
const standingRevision = '33333333-3333-4333-8333-333333333333'
const targetRevision = '66666666-6666-4666-8666-666666666666'
const preferences: NoteProcessingPreferences = {
  revision,
  autoProcessingEnabled: false,
  standingAuthorizationRevision: null,
  standingAuthorizationTargetRevision: null,
  purpose: 'native-note-processing',
  purposeText: '生成当前固定版本的转写与整理结果',
  materialScope: 'FullVersion',
  materialScopeText: '当前固定版本的全部材料（正文、图片、手写与录音）',
  costNotice: '当前固定版本的全部材料会按账号默认目标处理；云端转写与多模态整理可能按服务商用量产生费用。',
  possibleChargeAcknowledged: false,
  standingAuthorizationValid: false,
  currentDefaultTargetRevision: targetRevision,
  defaultTargets: [
    '转写：cloud-asr；合成转写模型 (synthetic-asr) revision 99999999-9999-4999-8999-999999999999；preset mimo-asr/1；endpoint 已纳入目标策略',
    '纯文本整理：合成整理模型 (synthetic-text) revision bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb；endpoint 已纳入目标策略',
  ],
}

function page() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  return render(<QueryClientProvider client={client}><NoteProcessingSettingsPage /></QueryClientProvider>)
}

beforeEach(() => {
  vi.clearAllMocks()
  api.preferences.mockResolvedValue(preferences)
})

afterEach(cleanup)

it('does not enable automatic paid processing without an explicit standing authorization', async () => {
  page()

  expect(await screen.findByText(/当前没有有效持续授权/)).toBeTruthy()
  expect(screen.getByText(/没有有效授权或默认目标不可用时材料保持待发起/)).toBeTruthy()
  const toggle = screen.getByRole('switch', { name: '结束后自动处理笔记' })
  await userEvent.click(toggle)

  expect(await screen.findByRole('heading', { name: '确认持续授权' })).toBeTruthy()
  expect(screen.getByText(/合成转写模型/)).toBeTruthy()
  expect(screen.getByText(/mimo-asr\/1/)).toBeTruthy()
  expect(screen.getAllByText(new RegExp(targetRevision)).length).toBeGreaterThan(0)
  expect(api.savePreferences).not.toHaveBeenCalled()
  const confirm = screen.getByRole('button', { name: '确认并开启自动处理' }) as HTMLButtonElement
  expect(confirm.disabled).toBe(true)

  await userEvent.click(screen.getByRole('checkbox', { name: '确认持续授权、材料范围与可能费用' }))
  expect(confirm.disabled).toBe(false)
  api.savePreferences.mockResolvedValue({
    ...preferences,
    revision: '44444444-4444-4444-8444-444444444444',
    autoProcessingEnabled: true,
    standingAuthorizationRevision: standingRevision,
    standingAuthorizationTargetRevision: targetRevision,
    possibleChargeAcknowledged: true,
    standingAuthorizationValid: true,
  })
  await userEvent.click(confirm)

  expect(api.savePreferences).toHaveBeenCalledWith({
    expectedRevision: revision,
    autoProcessingEnabled: true,
    confirmStandingAuthorization: true,
    acknowledgePossibleCharge: true,
    defaultTargetRevision: targetRevision,
  })
})

it('clears the standing authorization when automatic processing is turned off', async () => {
  api.preferences.mockResolvedValue({
    ...preferences,
    autoProcessingEnabled: true,
    standingAuthorizationRevision: standingRevision,
    standingAuthorizationTargetRevision: targetRevision,
    possibleChargeAcknowledged: true,
    standingAuthorizationValid: true,
  })
  api.savePreferences.mockResolvedValue({
    ...preferences,
    revision: '55555555-5555-4555-8555-555555555555',
    autoProcessingEnabled: false,
  })
  page()

  expect(await screen.findByText(/已有有效持续授权/)).toBeTruthy()
  await userEvent.click(screen.getByRole('switch', { name: '结束后自动处理笔记' }))

  expect(api.savePreferences).toHaveBeenCalledWith({
    expectedRevision: revision,
    autoProcessingEnabled: false,
    confirmStandingAuthorization: false,
    acknowledgePossibleCharge: false,
  })
})

it('offers a standing authorization repair for a legacy enabled setting', async () => {
  api.preferences.mockResolvedValue({
    ...preferences,
    autoProcessingEnabled: true,
  })
  page()

  expect(await screen.findByText(/当前设置不会创建新的付费任务/)).toBeTruthy()
  await userEvent.click(screen.getByRole('button', { name: '补充持续授权' }))

  expect(await screen.findByRole('heading', { name: '确认持续授权' })).toBeTruthy()
  expect(api.savePreferences).not.toHaveBeenCalled()
})
