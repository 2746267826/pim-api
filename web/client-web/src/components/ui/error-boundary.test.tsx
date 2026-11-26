// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'

const reportWebError = vi.hoisted(() => vi.fn())
vi.mock('@/features/diagnostics/queue', () => ({ reportWebError }))

import { ErrorBoundary } from './error-boundary'

describe('ErrorBoundary diagnostics reporting', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    vi.restoreAllMocks()
  })

  it('reports a sanitized render error and does not show token text', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    function Broken(): never {
      throw new Error('Authorization: Bearer abcdefghijklmnop')
    }

    render(
      <ErrorBoundary level="page">
        <Broken />
      </ErrorBoundary>,
    )

    expect(reportWebError).toHaveBeenCalledTimes(1)
    expect(screen.queryByText(/abcdefghijklmnop/)).toBeNull()
    expect(screen.getByText(/\[已脱敏\]/)).toBeTruthy()
  })
})
