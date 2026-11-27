import { getAccessToken } from '@/api/client'
import { notificationApi } from './api'
import { notificationDeviceId } from './device'

/**
 * 浏览器登出时先解绑本机推送端点，再撤销服务端订阅。
 * 清理是尽力而为：任何一步失败都不能阻塞本地会话退出。
 */
export async function revokeBrowserPushOnLogout(): Promise<void> {
  const token = getAccessToken()
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return

  let endpoint: string | undefined
  try {
    const registration = await navigator.serviceWorker.getRegistration()
    const subscription = await registration?.pushManager.getSubscription()
    endpoint = subscription?.endpoint
    await subscription?.unsubscribe()
  } catch {
    // 浏览器侧取消失败时仍尝试撤销服务端记录。
  }
  if (!endpoint) return

  try {
    await notificationApi.revokeCurrent(
      { deviceId: notificationDeviceId(), endpoint },
      token ? { headers: { Authorization: `Bearer ${token}` } } : undefined,
    )
  } catch {
    // 服务端撤销失败时由后续登录的设备列表提示用户手动处理。
  }
}
