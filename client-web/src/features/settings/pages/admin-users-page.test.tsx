// @vitest-environment jsdom
/*
 * REQ-12「新建用户」与 REQ-13「重置密码」（WO-AUTH-REGISTER-20261014 阶段二）。
 *
 * 判定依据：工单 docs/工单-主文档.md 的 AC-12.x / AC-13.x，以及
 * references/界面文案与交互要点.md §2 / §3 的**逐字文案**。
 *
 * 只替换数据层（react-query / api client / 通知），不重复验证 HTTP 拼装与后端语义；
 * 服务端的唯一性、会话撤销等已在阶段一（pim-api）覆盖。
 *
 * 本文件只含测试，不含产品实现。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

import { ApiError } from '@/api/client'

const { getMock, postMock, invalidateMock, notifySuccessMock, notifyErrorMock, state } = vi.hoisted(() => ({
  getMock: vi.fn(),
  postMock: vi.fn(),
  invalidateMock: vi.fn(),
  notifySuccessMock: vi.fn(),
  notifyErrorMock: vi.fn(),
  /* 列表内容放在可变状态里：创建成功后真的多一个用户，避免"只断言 invalidate"的弱证据 */
  state: { users: [] as Array<Record<string, unknown>> },
}))

/* 既有用户列表：一名管理员（我）+ 一名普通用户，覆盖「自定义行」与「对自己重置」 */
const USERS = [
  {
    id: 'me-admin',
    username: 'root',
    email: 'root@example.com',
    displayName: '管理员',
    role: 'admin',
    isActive: true,
    createdAt: '2026-10-01T00:00:00+00:00',
  },
  {
    id: 'u-member',
    username: 'member',
    email: 'member@example.com',
    displayName: null,
    role: 'user',
    isActive: true,
    createdAt: '2026-10-02T00:00:00+00:00',
  },
]

vi.mock('@tanstack/react-query', () => ({
  useQuery: ({ queryKey }: { queryKey: string[] }) =>
    queryKey.includes('me') ? { data: { id: 'me-admin' } } : { data: state.users, isLoading: false },
  useQueryClient: () => ({ invalidateQueries: invalidateMock }),
}))

vi.mock('@/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return { ...actual, apiGet: getMock, apiPost: postMock }
})

vi.mock('@/lib/notify', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/notify')>()
  return { ...actual, notifySuccess: notifySuccessMock, notifyError: notifyErrorMock }
})

/* 该文件从 settings-pages 聚合模块导出，顺带把日历依赖挡掉，保持用例隔离 */
vi.mock('@/features/calendar/queries', () => ({ useCalendars: () => ({ data: [] }) }))
vi.mock('@/features/calendar/components/event-editor-dialog', () => ({ EventEditorDialog: () => null }))
vi.mock('react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
}))

import { AdminUsersPage } from './settings-pages'

/** 打开「新建用户」弹窗 */
async function openCreateDialog() {
  await userEvent.click(screen.getByRole('button', { name: '新建用户' }))
  return await screen.findByRole('dialog')
}

/** 在弹窗内按可见标签填字段（Label 是真实 <label htmlFor>，用 getByLabelText 定位） */
function field(label: string) {
  return screen.getByLabelText(label)
}

/*
 * 表单取值用 fireEvent.change 直接置值，而不是逐字 userEvent.type：
 * 逐字输入在负载高的机器上（本仓多会话共用）会让单条用例逼近 5s 默认超时，
 * 属于测试自身的脆弱。**断言一条没变**——校验/请求计数/弹窗状态照样要过；
 * 真正的交互（点击、Esc、连点）仍然走 userEvent，保留真实事件语义。
 */
async function fillCreateForm(opts: { username?: string; email?: string; password?: string } = {}) {
  const { username = 'newbie', email = 'newbie@example.com', password = 'password123' } = opts
  fireEvent.change(field('用户名'), { target: { value: username } })
  fireEvent.change(field('邮箱'), { target: { value: email } })
  fireEvent.change(field('初始密码'), { target: { value: password } })
}

describe('REQ-12 用户管理页「新建用户」', () => {
  beforeEach(() => {
    state.users = [...USERS]
    getMock.mockReset().mockResolvedValue(state.users)
    postMock.mockReset()
    invalidateMock.mockReset()
    notifySuccessMock.mockReset()
    notifyErrorMock.mockReset()
  })

  afterEach(() => cleanup())

  it('AC-12.1 页面存在「新建用户」按钮，点击后出现居中弹窗（不是右侧抽屉）', async () => {
    render(<AdminUsersPage />)

    const trigger = screen.getByRole('button', { name: '新建用户' })
    expect(trigger).toBeTruthy()

    const dialog = await openCreateDialog()

    // 居中弹窗的形态证据：Radix Dialog 的 role=dialog + 内容容器带居中定位类
    const content = document.querySelector('.pim-dialog-content')
    expect(content).not.toBeNull()
    expect(content?.className).toContain('top-1/2')
    expect(content?.className).toContain('left-1/2')
    // 不得出现右侧抽屉（既有 Drawer 的内容容器类名）
    expect(document.querySelector('.pim-drawer-content')).toBeNull()
    expect(within(dialog).getByText('新建用户')).toBeTruthy()
  })

  it('AC-12.2 弹窗字段与默认值：用户名/邮箱/显示名/初始密码/角色，默认「普通用户」', async () => {
    render(<AdminUsersPage />)
    const dialog = await openCreateDialog()

    expect(field('用户名')).toBeTruthy()
    expect(field('邮箱')).toBeTruthy()
    expect(field('初始密码')).toBeTruthy()
    expect(field('角色')).toBeTruthy()

    // 显示名可空，且有指定占位文案
    expect(screen.getByLabelText(/显示名/).getAttribute('placeholder')).toBe('可留空，默认与用户名相同')
    // 初始密码是密码类型，且占位为「至少 8 位」
    expect(field('初始密码').getAttribute('type')).toBe('password')
    expect(field('初始密码').getAttribute('placeholder')).toBe('至少 8 位')

    // 角色两个选项都在（限定在弹窗内：列表行的角色徽标文案与之相同）。
    // 默认选中「普通用户」由 AC-12.3 的请求体 role=user 证明。
    expect(within(dialog).getByText('普通用户')).toBeTruthy()
    expect(within(dialog).getByText('管理员')).toBeTruthy()

    expect(screen.getByRole('button', { name: '创建' })).toBeTruthy()
    expect(screen.getByRole('button', { name: '取消' })).toBeTruthy()
  })

  it('AC-12.3 提交成功：关闭弹窗、刷新列表、显示成功提示', async () => {
    postMock.mockImplementationOnce(async (_path: string, body: Record<string, unknown>) => {
      // 服务端真的建出了用户：列表数据随之变化，随后的重渲染才会显示它
      state.users = [...state.users, { ...body, id: 'u-new', isActive: true, createdAt: '2026-10-03T00:00:00+00:00' }]
      return { id: 'u-new', username: body.username, role: body.role }
    })
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
    const [path, body] = postMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(path).toBe('/api/v1/admin/users')
    expect(body.username).toBe('newbie')
    expect(body.email).toBe('newbie@example.com')
    expect(body.password).toBe('password123')
    // 默认角色必须是普通用户，不得默认创建管理员
    expect(body.role).toBe('user')

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(notifySuccessMock).toHaveBeenCalledWith('用户已创建')
    expect(invalidateMock).toHaveBeenCalledWith({ queryKey: ['admin', 'users'] })
    // 关键：列表里**真的**出现了新用户（无需手动刷新），而不是只看"刷新被调用过"
    expect(await screen.findByText('newbie')).toBeTruthy()
  })

  it('AC-12.4 / AC-12.5 服务端报错时弹窗保持打开、内容保留、显示服务端文案', async () => {
    postMock.mockRejectedValueOnce(new ApiError('用户名已存在', 1003, 409))
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm({ username: 'taken', email: 'taken@example.com' })

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    expect(await screen.findByText('用户名已存在')).toBeTruthy()
    // 失败必须同时有顶部提示（§1：不得静默失败）
    expect(notifyErrorMock).toHaveBeenCalledWith('用户名已存在')
    // 弹窗仍开着
    expect(screen.getByRole('dialog')).toBeTruthy()
    // 已填内容不丢
    expect((field('用户名') as HTMLInputElement).value).toBe('taken')
    expect((field('邮箱') as HTMLInputElement).value).toBe('taken@example.com')
    expect(notifySuccessMock).not.toHaveBeenCalled()
  })

  it('AC-12.6 提交进行中按钮禁用；连点两次只发一次请求', async () => {
    let resolvePost: (v: unknown) => void = () => {}
    postMock.mockImplementationOnce(() => new Promise((resolve) => (resolvePost = resolve)))
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    const submit = screen.getByRole('button', { name: /创建/ })
    await userEvent.click(submit)

    await waitFor(() => expect(submit.hasAttribute('disabled')).toBe(true))
    await userEvent.click(submit)
    expect(postMock).toHaveBeenCalledTimes(1)

    resolvePost({ id: 'u-new' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(postMock).toHaveBeenCalledTimes(1)
  })

  /*
   * AC-12.6 反面：连续三次点击只发一次请求。
   *
   * 故障对照结论（已实测）：把 submit 里的 ref 防重入守卫去掉后，本条用例**依然通过**——
   * 因为 `fireEvent` 会经 act() 冲刷 state，第一次点击后按钮已 disabled，后两次点击被 DOM 拦下。
   * 也就是说本用例证明的是「连点不会重复提交」，**不**能证明 ref 守卫的必要性；
   * ref 属额外保险（若将来按钮不再 disabled 仍能挡住），不作为本 AC 的独立证据。
   */
  it('AC-12.6 连续三次点击「创建」也只发一次请求', async () => {
    let resolvePost: (v: unknown) => void = () => {}
    postMock.mockImplementationOnce(() => new Promise((resolve) => (resolvePost = resolve)))
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    const submit = screen.getByRole('button', { name: /创建/ })
    // 不 await、不等待 disabled：模拟 React 尚未提交 state 时的连续两次点击
    fireEvent.click(submit)
    fireEvent.click(submit)
    fireEvent.click(submit)

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
    resolvePost({ id: 'u-new' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(postMock).toHaveBeenCalledTimes(1)
  })

  it('AC-12.7 初始密码输入框是密码类型，不明文回显', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await userEvent.type(field('初始密码'), 'password123')

    const input = field('初始密码') as HTMLInputElement
    expect(input.getAttribute('type')).toBe('password')
    expect(input.value).toBe('password123')
  })

  it('AC-12.8 用 Esc 关闭弹窗后不产生任何用户（不发请求）', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    await userEvent.keyboard('{Escape}')

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(postMock).not.toHaveBeenCalled()
  })

  /*
   * AC-12.2 客户端预校验：拆成三条独立用例（每条只走一轮"填表 → 提交 → 计数"）。
   * 合并成一条时要在同一用例里跑 4 轮，机器负载高时会撞 5s 默认超时——
   * 那是测试自身的脆弱，不是产品行为，拆开后每条都远低于超时且失败定位更准。
   */
  it('AC-12.2 预校验：用户名为空 → 就地拦截，不发请求', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm({ username: '' })

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(screen.getByText('请输入用户名')).toBeTruthy())
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-12.2 预校验：邮箱缺 @ → 就地拦截，不发请求', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm({ email: 'not-an-email' })

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(screen.getByText('请输入正确的邮箱')).toBeTruthy())
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-12.2 预校验：初始密码不足 8 位 → 就地拦截，不发请求', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm({ password: 'short' })

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(screen.getByText('初始密码至少 8 位')).toBeTruthy())
    expect(postMock).not.toHaveBeenCalled()
  })

  it('关闭弹窗后重新打开是空表单，不残留上一次的初始密码', async () => {
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm({ username: 'draft-user', email: 'draft@example.com', password: 'draft-pass-1' })

    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    const reopened = await openCreateDialog()
    expect((within(reopened).getByLabelText('用户名') as HTMLInputElement).value).toBe('')
    expect((within(reopened).getByLabelText('邮箱') as HTMLInputElement).value).toBe('')
    expect((within(reopened).getByLabelText('初始密码') as HTMLInputElement).value).toBe('')
    expect(postMock).not.toHaveBeenCalled()
  })

  it('AC-12.2 对照：同样的表单填对后会发出请求（证明拦截来自校验，不是按钮坏了）', async () => {
    postMock.mockResolvedValueOnce({ id: 'u-new' })
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
  })

  it('AC-12.2 角色切换到「管理员」时，请求体 role=admin', async () => {
    postMock.mockResolvedValueOnce({ id: 'u-new' })
    render(<AdminUsersPage />)
    await openCreateDialog()
    await fillCreateForm()

    await userEvent.click(within(screen.getByRole('dialog')).getByText('管理员'))
    await userEvent.click(screen.getByRole('button', { name: '创建' }))

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
    const [, body] = postMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(body.role).toBe('admin')
  })
})

describe('REQ-13 用户管理页「重置密码」', () => {
  beforeEach(() => {
    state.users = [...USERS]
    getMock.mockReset().mockResolvedValue(state.users)
    postMock.mockReset()
    invalidateMock.mockReset()
    notifySuccessMock.mockReset()
    notifyErrorMock.mockReset()
  })

  afterEach(() => cleanup())

  it('AC-13.1 / AC-13.2 每行都有「重置密码」入口，弹窗含逐字后果说明', async () => {
    render(<AdminUsersPage />)

    const resetButtons = screen.getAllByRole('button', { name: '重置密码' })
    expect(resetButtons.length).toBe(USERS.length)

    await userEvent.click(resetButtons[1])
    expect(await screen.findByRole('dialog')).toBeTruthy()
    expect(screen.getByText('重置后该用户在所有设备都需要重新登录。')).toBeTruthy()
    expect(screen.getByRole('button', { name: '重置' })).toBeTruthy()
    expect(screen.getByRole('button', { name: '取消' })).toBeTruthy()
  })

  it('AC-13.1 提交成功后显示提示并刷新列表', async () => {
    postMock.mockResolvedValueOnce({ id: 'u-member' })
    render(<AdminUsersPage />)
    await userEvent.click(screen.getAllByRole('button', { name: '重置密码' })[1])
    await screen.findByRole('dialog')

    await userEvent.type(field('新密码'), 'brand-new-pass')
    await userEvent.click(screen.getByRole('button', { name: '重置' }))

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
    const [path, body] = postMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(path).toBe('/api/v1/admin/users/u-member/password')
    expect(body.newPassword).toBe('brand-new-pass')

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(notifySuccessMock).toHaveBeenCalledWith('密码已重置')
  })

  it('AC-13.3 新密码不足 8 位时前端阻止提交且不发请求', async () => {
    render(<AdminUsersPage />)
    await userEvent.click(screen.getAllByRole('button', { name: '重置密码' })[1])
    await screen.findByRole('dialog')

    await userEvent.type(field('新密码'), 'short')
    await userEvent.click(screen.getByRole('button', { name: '重置' }))

    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy())
    expect(postMock).not.toHaveBeenCalled()

    // 修正后发得出去（证明是校验拦下的）
    await userEvent.clear(field('新密码'))
    await userEvent.type(field('新密码'), 'long-enough-pass')
    await userEvent.click(screen.getByRole('button', { name: '重置' }))
    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1))
  })

  it('AC-13.4 服务端返回错误时在弹窗内显示服务端文案，弹窗不关闭', async () => {
    postMock.mockRejectedValueOnce(new ApiError('用户不存在', 40040, 404))
    render(<AdminUsersPage />)
    await userEvent.click(screen.getAllByRole('button', { name: '重置密码' })[1])
    await screen.findByRole('dialog')

    await userEvent.type(field('新密码'), 'brand-new-pass')
    await userEvent.click(screen.getByRole('button', { name: '重置' }))

    expect(await screen.findByText('用户不存在')).toBeTruthy()
    expect(notifyErrorMock).toHaveBeenCalledWith('用户不存在')
    expect(screen.getByRole('dialog')).toBeTruthy()
  })

  it('AC-13.5 对自己重置时的确认与后果说明与其他用户完全一致（无特殊文案）', async () => {
    render(<AdminUsersPage />)

    // 第 0 行是「我」；第 1 行是普通用户
    const selfDialog = await (async () => {
      await userEvent.click(screen.getAllByRole('button', { name: '重置密码' })[0])
      return await screen.findByRole('dialog')
    })()
    const selfHtml = selfDialog.textContent
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    const otherDialog = await (async () => {
      await userEvent.click(screen.getAllByRole('button', { name: '重置密码' })[1])
      return await screen.findByRole('dialog')
    })()

    expect(selfHtml).toBe(otherDialog.textContent)
    expect(screen.getByText('重置后该用户在所有设备都需要重新登录。')).toBeTruthy()
  })
})
