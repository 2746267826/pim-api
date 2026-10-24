// @vitest-environment jsdom
/*
 * REQ-14（AC-14.1 / AC-14.5）：设置中心「账号安全」卡对**所有已登录用户**可见。
 *
 * 这是工单点名「最容易做偏」的一条：卡若被放进管理员专属分组，
 * 普通用户就看不到入口，Q2「用户可自行更换密码」直接失效。
 *
 * 本文件只含测试，不含产品实现。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'

const authState = { user: { id: 'u1', username: 'someone', displayName: null, role: 'user' } as { role: string } | null }

vi.mock('@/features/auth/auth-context', () => ({
  useAuth: () => authState,
}))

vi.mock('@/api/version', () => ({
  useVersionInfo: () => ({ data: undefined }),
  APP_VERSION: '0.0.0-test',
}))

vi.mock('@/lib/apiBase', () => ({ getApiBase: () => '' }))

vi.mock('react-router', () => ({
  Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a>,
}))

import { SettingsHubPage } from './settings-hub-page'

/** 返回承载该文案的分组标题（设置中心里卡片都包在 <section><h2>分组名</h2> 内） */
function sectionOf(label: string): string | null {
  const el = screen.getByText(label)
  return el.closest('section')?.querySelector('h2')?.textContent ?? null
}

describe('REQ-14 设置中心「账号安全」卡', () => {
  afterEach(() => cleanup())

  it('AC-14.1 普通用户（role=user）也能看到「账号安全」卡，且卡片文案逐字一致', () => {
    authState.user = { role: 'user' }
    render(<SettingsHubPage />)

    expect(screen.getByText('账号安全')).toBeTruthy()
    expect(screen.getByText('修改登录密码')).toBeTruthy()

    // 必须可点开（是一个指向该页面的链接，而不是「建设中」那种不可点的占位卡）
    const link = screen.getByText('账号安全').closest('a')
    expect(link).not.toBeNull()
    expect(link?.getAttribute('href')).toBe('/settings/account')
  })

  it('AC-14.1 账号安全卡不与管理员专属分组「组织 / 用户管理」同组', () => {
    authState.user = { role: 'admin' }
    render(<SettingsHubPage />)

    const securitySection = sectionOf('账号安全')
    const usersSection = sectionOf('用户管理')
    expect(securitySection).not.toBeNull()
    expect(usersSection).toBe('组织')
    expect(securitySection).not.toBe(usersSection)
  })

  it('AC-14.5 既有分组与卡片不被破坏：连接 / 智能与自动化 / 数据 仍在', () => {
    authState.user = { role: 'user' }
    render(<SettingsHubPage />)

    expect(sectionOf('服务器')).toBe('连接')
    expect(sectionOf('Microsoft 账户')).toBe('连接')
    expect(sectionOf('MCP 连接')).toBe('智能与自动化')
    expect(sectionOf('数据可信度')).toBe('数据')
    expect(sectionOf('回收站')).toBe('数据')

    // 普通用户不该看到管理员专属入口
    expect(screen.queryByText('用户管理')).toBeNull()
  })

  it('AC-14.5 管理员仍能看到「组织」分组里的「用户管理」入口', () => {
    authState.user = { role: 'admin' }
    render(<SettingsHubPage />)

    expect(sectionOf('用户管理')).toBe('组织')
    expect(screen.getByText('账号安全')).toBeTruthy()
  })
})
