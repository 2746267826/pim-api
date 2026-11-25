// @vitest-environment jsdom
import { isValidElement } from 'react'
import { describe, expect, it } from 'vitest'
import type { RouteObject } from 'react-router'
import { router } from './router'
import { RequireAdmin } from './guards'
import { NAV_GROUPS } from './nav'

function findRoute(
  routes: RouteObject[],
  target: string,
  ancestors: RouteObject[] = [],
): { route: RouteObject; ancestors: RouteObject[] } | null {
  for (const route of routes) {
    if (route.path === target) return { route, ancestors }
    const nested = findRoute(route.children ?? [], target, [...ancestors, route])
    if (nested) return nested
  }
  return null
}

describe('application route access', () => {
  it('keeps the data center outside the admin-only guard so ordinary users can reach it', () => {
    const found = findRoute(router.routes, 'data-center')
    expect(found).not.toBeNull()
    expect(found?.route.element).toBeTruthy()
    expect(found?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(false)
  })

  it('still keeps admin settings behind the admin guard', () => {
    const found = findRoute(router.routes, 'settings/ai')
    expect(found).not.toBeNull()
    expect(found?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(true)
  })

  it('puts global search in the regular work navigation without an admin guard', () => {
    const found = findRoute(router.routes, 'search')
    expect(found).not.toBeNull()
    expect(found?.route.element).toBeTruthy()
    expect(found?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(false)
    const work = NAV_GROUPS.find((group) => group.label === '工作')
    expect(work?.items).toContainEqual(expect.objectContaining({ to: '/search', label: '搜索' }))
  })

  it('exposes the unified recycle bin to ordinary users from the real shell route', () => {
    const found = findRoute(router.routes, 'recycle-bin')
    expect(found).not.toBeNull()
    expect(found?.route.element).toBeTruthy()
    expect(found?.ancestors.some((route) =>
      isValidElement(route.element) && route.element.type === RequireAdmin)).toBe(false)
    const system = NAV_GROUPS.find((group) => group.label === '系统')
    expect(system?.items.find((item) => item.to === '/recycle-bin')?.label).toBe('回收站')
  })
})
