// @vitest-environment jsdom
/*
 * REQ-14：设置中心「账号安全」卡与修改密码界面（WO-AUTH-REGISTER-20261014 阶段二）。
 *
 * 判定依据：AC-14.2 ~ AC-14.6 与 references/界面文案与交互要点.md §4 / §4.1。
 * §4.1 是本文件最关键的一条：`refreshToken` 必须在**提交时**从存储现取，
 * 不得在组件挂载时读一次缓存进 state —— 401 单次刷新会轮换令牌，缓存值会变成
 * 服务端已撤销的旧令牌，触发 AC-5.9 的 40065。
 *
 * 本文件只含测试，不含产品实现。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { ApiError } from '@/api/client'

const { postMock, notifySuccessMock, getRefreshTokenMock, clearTokensMock } = vi.hoisted(() => ({
  postMock: vi.fn(),
  notifySuccessMock: vi.fn(),
  getRefreshTokenMock: vi.fn(),
  clearTokensMock: vi.fn(),
}))

/** 存储里的「当前设备」refresh token：可在测试中途被"轮换" */
let currentRefreshToken = 'STALE-token'

const authState = { hostChannel: 'browser' as 'browser' | 'host' }

vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => authState,
}))

vi.mock('@/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    apiPost: postMock,
    // 现取语义由产品代码决定：这里只是把「存储里的当前值」暴露出来
    getRefreshToken: () => {
      getRefreshTokenMock()
      return currentRefreshToken
    },
    clearTokens: clearTokensMock,
  }
})

vi.mock('@/lib/notify', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/notify')>()
  return { ...actual, notifySuccess: notifySuccessMock, notifyError: vi.fn() }
})

import { AccountSecurityPage } from './account-security-page'

function field(label: string) {
  return screen.getByLabelText(label)
}

/* 同 admin-users 用例：置值用 fireEvent.change 提速，断言不变；点击仍走 userEvent */
async function fillForm(current: string, next: string, confirm = next) {
  fireEvent.change(field('当前密码'), { target: { value: current } })
  fireEvent.change(field('新密码'), { target: { value: next } })
  fireEvent.change(field('确认新密码'), { target: { value: confirm } })
}

describe('REQ-14 账号安全：修改密码', () => {
  beforeEach(() => {
    postMock.mockReset()
    notifySuccessMock.mockReset()
    getRefreshTokenMock.mockReset()
    clearTokensMock.mockReset()
    currentRefreshToken = 'STALE-token'
    authState.hostChannel = 'browser'
  })

  afterEach(() => cleanup())

  it('AC-14.2 三项字段与主按钮齐备，且都是密码类型', () => {
    render(<AccountSecurityPage />)

    expect(field('当前密码').getAttribute('type')).toBe('password')
    expect(field('新密码').getAttribute('type')).toBe('password')
    expect(field('确认新密码').getAttribute('type')).toBe('password')
    expect(field('新密码').getAttribute('placeholder')).toBe('至少 8 位')
    expect(screen.getByRole('button', { name: '修改密码' })).toBeTruthy()
  })

  it('AC-14.2 + §4.1 提交时现取 refreshToken（挂载后令牌已轮换，必须提交新值）', async () => {
    postMock.mockResolvedValueOnce('密码已修改')
    render(<AccountSecurityPage />)

    // 挂载完成后，"存储里"的令牌被 401 单次刷新轮换掉了
    currentRefreshToken = 'FRESH-token'
    await fillForm('old-password', 'new-password-1')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
    const [path, body] = postMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(path).toBe('/api/v1/auth/password')
    expect(body.currentPassword).toBe('old-password')
    expect(body.newPassword).toBe('new-password-1')
    // 关键：必须是提交那一刻从存储现取的值，不能是挂载时缓存的旧值
    expect(body.refreshToken).toBe('FRESH-token')
  })

  it('AC-14.2 提交成功后显示成功提示、表单清空、当前设备保持登录', async () => {
    postMock.mockResolvedValueOnce('密码已修改')
    render(<AccountSecurityPage />)
    await fillForm('old-password', 'new-password-1')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    await waitFor(() => expect(notifySuccessMock).toHaveBeenCalledWith('密码已修改'))
    await waitFor(() => {
      expect((field('当前密码') as HTMLInputElement).value).toBe('')
      expect((field('新密码') as HTMLInputElement).value).toBe('')
      expect((field('确认新密码') as HTMLInputElement).value).toBe('')
    })
    /*
     * 当前设备保持登录：不能只凭"表单还在"——清除令牌并跳转的实现也可能让表单短暂存在。
     * 这里直接断言**令牌没有被清掉**（clearTokens 未被调用），这是"仍然登录"的可观察前提。
     */
    expect(clearTokensMock).not.toHaveBeenCalled()
    expect(getRefreshTokenMock).toHaveBeenCalled()
    expect(screen.getByLabelText('当前密码')).toBeTruthy()
  })

  it('AC-14.3 当前密码错误时显示服务端文案、停留原页、已填内容不清空', async () => {
    postMock.mockRejectedValueOnce(new ApiError('当前密码不正确', 40063, 400))
    render(<AccountSecurityPage />)
    await fillForm('wrong-password', 'new-password-1')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    expect(await screen.findByText('当前密码不正确')).toBeTruthy()
    expect((field('当前密码') as HTMLInputElement).value).toBe('wrong-password')
    expect((field('新密码') as HTMLInputElement).value).toBe('new-password-1')
    expect(notifySuccessMock).not.toHaveBeenCalled()
  })

  it('AC-14.4 本地校验：新密码不足 8 位 → 提示且不发请求', async () => {
    render(<AccountSecurityPage />)
    await fillForm('old-password', 'short')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    expect(await screen.findByText('新密码至少 8 位')).toBeTruthy()
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-14.4 本地校验：两次新密码不一致 → 提示且不发请求', async () => {
    render(<AccountSecurityPage />)
    await fillForm('old-password', 'new-password-1', 'new-password-2')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    expect(await screen.findByText('两次输入的新密码不一致')).toBeTruthy()
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-14.4 本地校验：新密码与当前密码相同 → 提示且不发请求', async () => {
    render(<AccountSecurityPage />)
    await fillForm('same-password', 'same-password')

    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    expect(await screen.findByText('新密码不能与当前密码相同')).toBeTruthy()
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-14.4 三项均非空是前提：留空也拦下且不发请求', async () => {
    render(<AccountSecurityPage />)
    await userEvent.click(screen.getByRole('button', { name: '修改密码' }))

    await waitFor(() => expect(screen.getByLabelText('当前密码')).toBeTruthy())
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-14.6 host 通道下不渲染可提交表单、明确提示走桌面客户端，且不发请求', async () => {
    authState.hostChannel = 'host'
    render(<AccountSecurityPage />)

    /*
     * AC-14.6 允许「隐藏入口」或「明确提示走桌面客户端」两种做法，本实现选择后者。
     * 因此断言收紧为：**不渲染可提交表单**（避免用户提交空 refreshToken）+ 出现明确提示。
     * 只断言"有提示"是不够的——提示与可提交表单同时在也会通过（review 指出）。
     */
    expect(screen.queryByLabelText('当前密码')).toBeNull()
    expect(screen.queryByRole('button', { name: '修改密码' })).toBeNull()
    // 用 queryAllByText：提示可能出现在标题与说明两处，单个 queryByText 会因多匹配抛错
    expect(screen.queryAllByText(/桌面客户端/).length).toBeGreaterThan(0)

    // 无论哪种，都不得因为提交空 refreshToken 而发出请求
    expect(postMock).not.toHaveBeenCalled()
    expect(getRefreshTokenMock).not.toHaveBeenCalled()
  })
})
