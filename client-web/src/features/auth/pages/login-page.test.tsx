// @vitest-environment jsdom
/*
 * REQ-11：登录页移除注册模式（WO-AUTH-REGISTER-20261014 阶段二）。
 *
 * 判定依据（工单原文 + references/界面文案与交互要点.md §5）：
 *   - 页面只呈现登录表单（用户名、密码、提交按钮）；
 *   - 不存在「登录/注册」模式切换控件、不存在邮箱输入框、不存在显示名输入框、
 *     不存在指向注册的链接或提示；
 *   - 必须保持不变：内联错误提示、登录成功后的原路由回跳、host 通道既有说明文案。
 *
 * 本文件只含测试，不含产品实现。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { ApiError } from '@/api/client'
import { LoginPage } from './login-page'

const { loginMock, navigateMock } = vi.hoisted(() => ({
  loginMock: vi.fn<(username: string, password: string) => Promise<void>>(),
  navigateMock: vi.fn(),
}))

/** useAuth 的可控替身：只替换会话上下文，不重复验证 AuthProvider 内部逻辑 */
type AuthStub = {
  user: { id: string; username: string; displayName: string | null; role: string } | null
  hostChannel: 'browser' | 'host'
  login: typeof loginMock
  retryBoot: () => void
}

const authState: AuthStub = {
  user: null,
  hostChannel: 'browser',
  login: loginMock,
  retryBoot: vi.fn(),
}

vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => authState,
}))

vi.mock('react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
  Navigate: ({ to }: { to: string }) => <div data-testid="redirect">{to}</div>,
  useNavigate: () => navigateMock,
  useLocation: () => ({ state: { from: '/today' } }),
}))


describe('REQ-11 登录页只有登录，没有注册入口', () => {
  beforeEach(() => {
    authState.user = null
    authState.hostChannel = 'browser'
    loginMock.mockReset()
    navigateMock.mockReset()
  })

  afterEach(() => cleanup())

  it('AC-11.1 只呈现登录表单：不存在注册切换控件 / 邮箱框 / 显示名框', () => {
    render(<LoginPage />)

    // 登录表单三件套在
    expect(screen.getByLabelText('用户名或邮箱')).toBeTruthy()
    expect(screen.getByLabelText('密码')).toBeTruthy()
    expect(screen.getByRole('button', { name: '登录' })).toBeTruthy()

    /*
     * 模式切换控件不在。注意不能用 getByRole('button', { name: '注册' })：
     * 既有的 Segmented 建在 Radix ToggleGroup 上（role=tablist/tab），不是 button ——
     * 那样写会"找不到就通过"，是假绿。这里改用两路硬断言：
     * 页面上不存在文本恰为「注册」的元素，也不存在任何模式切换容器。
     */
    expect(screen.queryByText('注册')).toBeNull()
    expect(document.querySelector('[role="tablist"]')).toBeNull()
    expect(document.querySelector('[role="radiogroup"]')).toBeNull()
    expect(document.querySelector('[role="tab"]')).toBeNull()

    // 注册专属字段不在
    expect(document.querySelector('input[type="email"]')).toBeNull()
    expect(screen.queryByLabelText('邮箱')).toBeNull()
    expect(screen.queryByLabelText('显示名（可选）')).toBeNull()
    expect(screen.queryByLabelText('显示名')).toBeNull()

    // 页面内不存在指向注册的链接或提示
    expect(screen.queryByRole('link', { name: /注册/ })).toBeNull()
  })

  it('AC-11.3 登录失败仍显示内联错误提示，且不跳转', async () => {
    loginMock.mockRejectedValueOnce(new ApiError('用户名或密码错误', 40001, 401))
    render(<LoginPage />)

    await userEvent.type(screen.getByLabelText('用户名或邮箱'), 'alice')
    await userEvent.type(screen.getByLabelText('密码'), 'wrong-password')
    await userEvent.click(screen.getByRole('button', { name: '登录' }))

    expect(await screen.findByText('用户名或密码错误')).toBeTruthy()
    expect(navigateMock).not.toHaveBeenCalled()
  })

  it('AC-11.2 登录成功后仍回跳原目标路由', async () => {
    loginMock.mockResolvedValueOnce(undefined)
    render(<LoginPage />)

    await userEvent.type(screen.getByLabelText('用户名或邮箱'), 'alice')
    await userEvent.type(screen.getByLabelText('密码'), 'password123')
    await userEvent.click(screen.getByRole('button', { name: '登录' }))

    await waitFor(() => expect(loginMock).toHaveBeenCalledWith('alice', 'password123'))
    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith('/today', { replace: true }))
  })

  it('AC-11.6 host 通道下既有说明文案仍在，且不出现注册入口', () => {
    authState.hostChannel = 'host'
    render(<LoginPage />)

    expect(screen.getByText('请在桌面客户端登录')).toBeTruthy()
    expect(
      screen.getByText('嵌入页面不提供账号密码登录或注册。请使用桌面客户端的登录窗口登录，然后点重试。'),
    ).toBeTruthy()
    // 说明文案里出现「注册」二字是既有措辞（必须保持不变），但不得有可点的注册入口
    expect(screen.queryByText('注册')).toBeNull()
    expect(document.querySelector('[role="tablist"]')).toBeNull()
    expect(screen.queryByRole('link', { name: /注册/ })).toBeNull()
    // host 通道下连登录表单也不该出现
    expect(screen.queryByLabelText('用户名或邮箱')).toBeNull()
    expect(screen.queryByLabelText('密码')).toBeNull()
  })
})
