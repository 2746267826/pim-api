import browser from 'webextension-polyfill'
import config from '../config'
import { heartbeatAlarmListener, sendInitialHeartbeat, tabActivatedListener } from './heartbeat'
import { waitForPimClient } from './client'
import { emitNotification } from './helpers'
import { setBaseUrl, setEnabled, getEnabled } from '../storage'

const PIM_BASE_URL = 'http://localhost:15601'

console.info('PIM Browser Watcher starting...')

// Ensure enabled defaults to true on first install
browser.runtime.onInstalled.addListener(async () => {
  const enabled = await getEnabled()
  // getEnabled returns true by default, but ensure persisted
  await setEnabled(enabled)
  console.info('Extension installed, enabled:', enabled)
})

// Try to reach PIM client before starting heartbeats
async function initPimConnection() {
  const connected = await waitForPimClient()
  if (!connected) {
    console.error('PIM client not found on port 15601')
    try {
      emitNotification('PIM client not found', 'Please ensure PIM daemon is running (localhost:15601)')
    } catch (e) {
      console.error('Failed to show notification:', e)
    }
  } else {
    console.info('PIM client reachable at', PIM_BASE_URL)
  }
  return connected
}

console.debug('Creating alarms and tab listeners')
browser.alarms.create(config.heartbeat.alarmName, {
  periodInMinutes: Math.max(1, Math.floor(config.heartbeat.intervalInSeconds / 60)),
})
browser.alarms.onAlarm.addListener((alarm) => {
  void heartbeatAlarmListener(alarm)
})
browser.tabs.onActivated.addListener((activeInfo) => {
  void tabActivatedListener(activeInfo)
})

console.debug('Setting base url and sending initial heartbeat')
void (async () => {
  await setBaseUrl(PIM_BASE_URL)
  await initPimConnection()
  // Small delay to let storage settle
  await sendInitialHeartbeat()
  console.info('PIM Browser Watcher started successfully')
})().catch((err) => console.error('Failed to initialize extension:', err))

/**
 * Keep the service worker alive using Offscreen API to prevent Chrome's termination.
 */
async function setupOffscreen() {
  const _chrome = (globalThis as unknown as { chrome?: { offscreen?: { hasDocument: () => Promise<boolean>; createDocument: (opts: unknown) => Promise<void> } } }).chrome
  if (typeof _chrome === 'undefined' || !_chrome.offscreen) return

  if (await _chrome.offscreen.hasDocument()) return

  try {
    await _chrome.offscreen.createDocument({
      url: 'src/offscreen.html',
      reasons: ['BLOBS'],
      justification: 'Keep service worker alive for heartbeat packets',
    })
  } catch (e) {
    console.error('Failed to create offscreen document:', e)
  }
}

browser.runtime.onMessage.addListener((message: unknown) => {
  if (typeof message === 'object' && message !== null && (message as { type?: string }).type === 'KEEP_ALIVE') {
    return Promise.resolve({ status: 'ok' })
  }
  return undefined
})

// Initialize on startup and installation
browser.runtime.onStartup.addListener(() => {
  void setupOffscreen()
})
browser.runtime.onInstalled.addListener(() => {
  void setupOffscreen()
})

void setupOffscreen()
