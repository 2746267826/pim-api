import { renderHook, waitFor } from '@testing-library/react'
import { test, expect, vi } from 'vitest'
import { useVersionInfo } from './useVersionInfo'

test('hasUpdate 仅比 N', async () => {
  (globalThis as any).fetch = vi.fn().mockResolvedValue({ ok:true, json: async()=>({ version:'2026.08.100', latestVersion:'2026.08.101', checkedAt:new Date().toISOString(), error:null, capabilities:[] }) }) as any
  const { result } = renderHook(()=>useVersionInfo())
  await waitFor(()=> expect(result.current.hasUpdate).toBe(true))
  expect(result.current.localVersion).toBeDefined()
})

test('同 N 忽略后缀判无更新', async () => {
  (globalThis as any).fetch = vi.fn().mockResolvedValue({ ok:true, json: async()=>({ version:'2026.08.12+android.1', latestVersion:'2026.08.12-pr.5+abc', checkedAt:null, error:null, capabilities:[] }) }) as any
  const { result } = renderHook(()=>useVersionInfo())
  await waitFor(()=> expect(result.current.serverVersion).toBe('2026.08.12+android.1'))
  expect(result.current.hasUpdate).toBe(false)
})
