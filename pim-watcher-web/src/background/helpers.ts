import browser from 'webextension-polyfill'
import { getBrowserName, getStoredInstanceId, setBrowserName, setStoredInstanceId } from '../storage'

export const getTab = (id: number) => browser.tabs.get(id)

export const getTabs = (query: browser.Tabs.QueryQueryInfoType = {}) => browser.tabs.query(query)

export const getActiveWindowTab = async (): Promise<browser.Tabs.Tab | undefined> => {
  const tabs = await getTabs({
    active: true,
    currentWindow: true,
  })

  if (tabs.length > 0) {
    return tabs[0]
  }

  const allTabs = await getTabs({
    active: true,
  })

  if (allTabs.length > 0) {
    return allTabs[0]
  }

  return undefined
}

export function emitNotification(title: string, message: string) {
  browser.notifications.create({
    type: 'basic',
    iconUrl: browser.runtime.getURL('logo-128.png'),
    title,
    message,
  })
}

export const getBrowser = async (): Promise<string> => {
  const storedName = await getBrowserName()
  if (storedName) {
    return storedName
  }

  const browserName = detectBrowser()

  await setBrowserName(browserName)
  return browserName
}

export const getBrowserType = (): string => {
  const ua = navigator.userAgent
  // Edge UA includes "Chrome"/"Safari" tokens, so it must be checked first.
  if (ua.includes('Edg')) return 'edge'
  if (ua.includes('Firefox')) return 'firefox'
  // Chrome UA also includes "Safari", so Chrome must win over Safari.
  if (ua.includes('Chrome')) return 'chrome'
  if (ua.includes('Safari')) return 'safari'
  return 'other'
}

// Memoized in-flight promise so concurrent first-time callers (initial heartbeat
// + alarm) share the same generated id instead of racing on storage read/write.
let instanceIdPromise: Promise<string> | null = null

// Returns a stable, per-install unique id. Stored in storage.local so it
// survives browser/extension restarts and differs across profiles/browsers.
export function getInstanceId(): Promise<string> {
  instanceIdPromise ??= resolveInstanceId()
  return instanceIdPromise
}

async function resolveInstanceId(): Promise<string> {
  const existing = await getStoredInstanceId()
  if (existing) return existing

  const id = `${browser.runtime.id}_${crypto.randomUUID()}`
  try {
    await setStoredInstanceId(id)
  } catch (err) {
    console.error('Failed to persist instance id:', err)
  }
  return id
}

export const detectBrowser = () => {
  const nav = navigator as unknown as { brave?: { isBrave: () => boolean }; userAgent: string }
  if (nav.brave?.isBrave?.()) {
    return 'brave'
  } else if (navigator.userAgent.includes('Opera') || navigator.userAgent.includes('OPR')) {
    return 'opera'
  } else if (navigator.userAgent.includes('Firefox')) {
    return 'firefox'
  } else if (navigator.userAgent.includes('Chrome')) {
    return 'chrome'
  } else if (navigator.userAgent.includes('Safari')) {
    return 'safari'
  } else {
    return 'unknown'
  }
}
