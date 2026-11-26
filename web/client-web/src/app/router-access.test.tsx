// @vitest-environment jsdom
import { isValidElement } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, type RouteObject } from 'react-router'
import { router } from './router'
import { RequireAdmin } from './guards'
import { navGroupsForRole } from './nav'

const authState = vi.hoisted(() => ({
  booting: false,
  user: null as null | { id: string; username: string; displayName: string | null; role: string },
}))
vi.mock('@/features/auth/auth-context', () => ({ useAuth: () => authState }))

afterEach(() => {
  cleanup()
  authState.user = null
})

function findRoutes(
  routes: RouteObject[],
  target: string,
  ancestors: RouteObject[] = [],
): { route: RouteObject; ancestors: RouteObject[] }[] {
  const matches: { route: RouteObject; ancestors: RouteObject[] }[] = []
  for (const route of routes) {
    if (route.path === target) matches.push({ route, ancestors })
    matches.push(...findRoutes(route.children ?? [], target, [...ancestors, route]))
  }
  return matches
}

describe('application route access', () => {
  it('puts the data center behind the admin-only guard', () => {
    const found = findRoutes(router.routes, 'data-center')
    expect(found).toHaveLength(1)
    expect(found[0]?.route.element).toBeTruthy()
    expect(found[0]?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(true)
  })

  it('puts the audit timeline behind the admin-only guard', () => {
    const found = findRoutes(router.routes, 'audit/:objectType/:objectId')
    expect(found).toHaveLength(1)
    expect(found[0]?.route.element).toBeTruthy()
    expect(found[0]?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(true)
  })

  it('keeps the ordinary-user recycle bin outside the admin-only guard', () => {
    const found = findRoutes(router.routes, 'settings/recycle-bin')
    expect(found).toHaveLength(1)
    expect(found[0]?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(false)
  })

  it('still keeps admin settings behind the admin guard', () => {
    const found = findRoutes(router.routes, 'settings/ai')
    expect(found).toHaveLength(1)
    expect(found[0]?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(true)
  })

  it('hides the data center navigation from ordinary users and keeps it for admins', () => {
    const ordinary = navGroupsForRole('user').find((group) => group.label === '治理')
    expect(ordinary?.items.some((item) => item.to === '/data-center')).toBe(false)

    const admin = navGroupsForRole('admin').find((group) => group.label === '治理')
    expect(admin?.items).toContainEqual(expect.objectContaining({ to: '/data-center', label: '数据中心' }))
  })

  it('renders the explicit admin gate for an ordinary user who reaches a guarded route', () => {
    authState.user = {
      id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      username: 'ordinary',
      displayName: null,
      role: 'user',
    }

    render(<MemoryRouter><RequireAdmin /></MemoryRouter>)

    expect(screen.getByText('需要管理员权限')).toBeTruthy()
  })

  it('fails closed when the admin route is rendered without a confirmed user', () => {
    render(<MemoryRouter><RequireAdmin /></MemoryRouter>)

    expect(screen.getByText('需要管理员权限')).toBeTruthy()
    expect(screen.getByText(/未确认管理员登录状态/)).toBeTruthy()
  })

  it('puts global search in the regular work navigation without an admin guard', () => {
    const found = findRoutes(router.routes, 'search')
    expect(found).toHaveLength(1)
    expect(found[0]?.route.element).toBeTruthy()
    expect(found[0]?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(false)
    const work = navGroupsForRole('user').find((group) => group.label === '工作')
    expect(work?.items).toContainEqual(expect.objectContaining({ to: '/search', label: '搜索' }))
  })
})
