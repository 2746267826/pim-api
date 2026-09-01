import browser from 'webextension-polyfill'
import type { HeartbeatData } from './background/client'

function watchKey<T>(key: string, cb: (value: T) => void | Promise<void>) {
  const listener = (changes: browser.Storage.StorageAreaOnChangedChangesType) => {
    if (!(key in changes)) return
    cb(changes[key].newValue as T)
  }
  browser.storage.local.onChanged.addListener(listener)
  return () => browser.storage.local.onChanged.removeListener(listener)
}

async function waitForKey<T>(key: string, desiredValue: T, timeoutMs = 30000): Promise<void> {
  const value = await browser.storage.local.get(key).then((r) => r[key])
  if (value === desiredValue) return
  return new Promise<void>((resolve, reject) => {
    let done = false
    const timer = setTimeout(() => {
      if (done) return
      done = true
      unsubscribe()
      reject(new Error(`waitForKey timeout: ${key} did not become ${String(desiredValue)} within ${timeoutMs}ms`))
    }, timeoutMs)

    const unsubscribe = watchKey<T>(key, (value) => {
      if (done) return
      if (value !== desiredValue) return
      done = true
      clearTimeout(timer)
      resolve()
      unsubscribe()
    })
  })
}

// -- Enabled switch ----------------------------------------------------------

type Enabled = boolean
export const waitForEnabled = () => waitForKey<Enabled>('enabled', true)
export const getEnabled = (): Promise<Enabled> =>
  browser.storage.local.get('enabled').then((r) => {
    // default to true if never set, so fresh installs are active
    if (r.enabled === undefined) return true
    return Boolean(r.enabled)
  })
export const setEnabled = (enabled: Enabled) => browser.storage.local.set({ enabled })

// -- Base URL (PIM client) ---------------------------------------------------

type BaseUrl = string
export const getBaseUrl = (): Promise<BaseUrl | undefined> =>
  browser.storage.local.get('baseUrl').then((r) => r.baseUrl as string | undefined)
export const setBaseUrl = (baseUrl: BaseUrl) => browser.storage.local.set({ baseUrl })

// -- Last heartbeat data (dedup) ---------------------------------------------

export const getHeartbeatData = (): Promise<HeartbeatData | undefined> =>
  browser.storage.local.get('heartbeatData').then((r) => r.heartbeatData as HeartbeatData | undefined)
export const setHeartbeatData = (heartbeatData: HeartbeatData) =>
  browser.storage.local.set({ heartbeatData })

// -- Persistent instance id (unique per extension install) --------------------

export const getStoredInstanceId = (): Promise<string | undefined> =>
  browser.storage.local.get('instanceId').then((r) => r.instanceId as string | undefined)
export const setStoredInstanceId = (instanceId: string) => browser.storage.local.set({ instanceId })

// -- Browser name cache (used by helpers.getBrowser) -------------------------

type BrowserName = string
type StorageData = { [key: string]: unknown }
export const getBrowserName = (): Promise<BrowserName | undefined> =>
  browser.storage.local.get('browserName').then((data: StorageData) => data.browserName as string | undefined)
export const setBrowserName = (browserName: BrowserName) =>
  browser.storage.local.set({ browserName })
