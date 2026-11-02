/**
 * In-memory credential-session generation.
 *
 * Tokens live in storage, but a token value alone cannot fence an async refresh:
 * an account or server switch can happen while the old refresh request is in
 * flight. This in-memory counter changes on local credential writes, logout, and
 * server switches. The client also snapshots the opaque browser token pair, so a
 * cross-tab credential replacement is rejected before it can mutate the current
 * session. It contains no token or user data and is never logged.
 */
import { STORAGE_KEYS } from './storage'

let revision = 0

const sessionStorageKeys = new Set<string>([
  STORAGE_KEYS.accessToken,
  STORAGE_KEYS.refreshToken,
  STORAGE_KEYS.apiBase,
])

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => {
    if (event.key === null || sessionStorageKeys.has(event.key)) {
      markAuthSessionChanged()
    }
  })
}

export function authSessionRevision(): number {
  return revision
}

export function markAuthSessionChanged(): void {
  revision += 1
}
