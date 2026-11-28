const DEVICE_ID_KEY = 'pim.notificationDeviceId'

export function notificationDeviceId(): string {
  const existing = localStorage.getItem(DEVICE_ID_KEY)
  if (existing) return existing
  const created =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : `device-${Date.now()}-${Math.random().toString(16).slice(2)}`
  localStorage.setItem(DEVICE_ID_KEY, created)
  return created
}
