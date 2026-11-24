// @vitest-environment jsdom
import { isValidElement } from 'react'
import { describe, expect, it } from 'vitest'
import type { RouteObject } from 'react-router'
import { router } from './router'
import { RequireAdmin } from './guards'

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
})
