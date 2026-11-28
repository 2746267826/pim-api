import { notificationApi } from './api'
import { notificationDeviceId } from './device'

const PENDING_KEY = 'pim.pendingPushRevocations'
const MAX_PENDING = 10

interface PendingRevocation {
  deviceId: string
  userId: string | null
}

function readPending(): PendingRevocation[] {
  try {
    const raw = localStorage.getItem(PENDING_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed
      .map((value): PendingRevocation | null => {
        if (typeof value === 'string' && value.length > 0) {
          // 旧版本只存 deviceId；保留可用性，但不假装知道所属账号。
          return { deviceId: value, userId: null }
        }
        if (typeof value !== 'object' || value === null) return null
        const record = value as Record<string, unknown>
        if (typeof record.deviceId !== 'string' || record.deviceId.length === 0) return null
        return {
          deviceId: record.deviceId,
          userId: typeof record.userId === 'string' && record.userId.length > 0
            ? record.userId
            : null,
        }
      })
      .filter((value): value is PendingRevocation => value !== null)
      .slice(0, MAX_PENDING)
  } catch {
    return []
  }
}

function writePending(entries: PendingRevocation[]): void {
  try {
    if (entries.length === 0) {
      localStorage.removeItem(PENDING_KEY)
      return
    }
    localStorage.setItem(PENDING_KEY, JSON.stringify(entries.slice(0, MAX_PENDING)))
  } catch {
    // 存储不可用时只能放弃重试标记，登出本身不受影响。
  }
}

function queuePending(deviceId: string, userId: string | null): void {
  const pending = readPending()
  if (pending.some((entry) => entry.deviceId === deviceId && entry.userId === userId)) return
  pending.push({ deviceId, userId })
  writePending(pending)
}

/** 待补撤销的设备标识（仅本机 deviceId 与所属账号，从不保存 endpoint 或令牌）。 */
export function pendingPushRevocations(): string[] {
  return readPending().map((entry) => entry.deviceId)
}

/**
 * 重试登出时未完成的订阅撤销。只按 deviceId 撤销，
 * 避免把 endpoint 明文落到本地存储；成功后清空对应标记。
 * 记录所属账号，避免同一设备在另一账号登录时撤销到错误账号。
 */
export async function flushPendingPushRevocations(activeUserId?: string): Promise<void> {
  const pending = readPending()
  if (pending.length === 0) return
  const remaining: PendingRevocation[] = []
  for (const entry of pending) {
    if (activeUserId !== undefined &&
        entry.userId !== null &&
        entry.userId !== activeUserId) {
      // 该撤销属于另一个账号：保留到原账号下次登录，不跨账号调用服务端。
      remaining.push(entry)
      continue
    }
    try {
      await notificationApi.revokeCurrent({ deviceId: entry.deviceId })
    } catch {
      remaining.push(entry)
    }
  }
  writePending(remaining)
}

/**
 * 浏览器登出时先解绑本机推送端点，再撤销服务端订阅。
 * 清理是尽力而为：任何一步失败都不能阻塞本地会话退出；
 * 服务端撤销失败时只记下本机 deviceId，下次登录后重试，不保存 endpoint/令牌。
 */
export async function revokeBrowserPushOnLogout(userId?: string | null): Promise<void> {
  const deviceId = notificationDeviceId()
  let revokeFailed = false
  // 在第一个 await 之前启动请求，让 apiFetch 先捕获当前认证会话。
  // 调用方紧接着会 clearTokens()；若等到 service worker 返回后再发请求，
  // 服务端撤销会因缺少已清理的令牌而必然 401。
  const revokeRequest = notificationApi
    .revokeCurrent({ deviceId })
    .catch(() => {
      revokeFailed = true
    })

  if (typeof navigator !== 'undefined' && 'serviceWorker' in navigator) {
    try {
      const registration = await navigator.serviceWorker.getRegistration()
      const subscription = await registration?.pushManager.getSubscription()
      await subscription?.unsubscribe()
    } catch {
      // 浏览器侧取消失败时仍等待服务端撤销请求。
    }
  }

  await revokeRequest
  if (revokeFailed) queuePending(deviceId, userId ?? null)
}
