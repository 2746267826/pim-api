// @vitest-environment jsdom
/*
 * api#462：生成成功后把真实返回 upsert 进报告列表缓存。
 * 这样随后 invalidate 触发的 refetch 即使失败，刚生成的报告也仍能打开；
 * 同时保证选中 id 不在 refetch 前被过滤副作用清掉。
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'

const { generateMock } = vi.hoisted(() => ({ generateMock: vi.fn() }))

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  reportsApi: {
    list: vi.fn(),
    generate: generateMock,
    requestSuggestionAction: vi.fn(),
  },
}))

import { calendarExtraKeys, useGenerateReport } from './queries'
import type { ReportArtifact } from './types'

function report(id: string): ReportArtifact {
  return {
    id, kind: 'Project', projectId: 'p1', riskLevel: 'L0AutomaticArtifact',
    contentMarkdown: '# 项目报告', metricsJson: '{}', generatedAt: '2026-10-24T10:00:00Z', status: 'Active',
  }
}

describe('useGenerateReport 缓存 upsert', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('成功后立即把返回报告写入列表缓存（去重），后台 invalidate 不阻塞', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(calendarExtraKeys.reports, [report('old')])
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    )
    generateMock.mockResolvedValue(report('new'))
    const { result } = renderHook(() => useGenerateReport(), { wrapper })

    await result.current.mutateAsync({ kind: 'Project', date: '2026-10-24', projectId: 'p1' })

    await waitFor(() => {
      const cached = client.getQueryData<ReportArtifact[]>(calendarExtraKeys.reports)!
      expect(cached.map((r) => r.id)).toEqual(['new', 'old'])
    })
    expect(generateMock).toHaveBeenCalledWith({ kind: 'Project', date: '2026-10-24', projectId: 'p1' })
  })
})
