import browser from 'webextension-polyfill'
import deepEqual from 'deep-equal'
import * as punycode from 'punycode.js'
import { getActiveWindowTab, getBrowserType, getInstanceId, getTab, getTabs } from './helpers'
import { sendHeartbeat } from './client'
import type { HeartbeatData } from './client'
import { getEnabled, getHeartbeatData, setHeartbeatData } from '../storage'
import config from '../config'
import { PIM_BASE_URL } from './client'

function decodeURL(url: string): string {
  try {
    const parsed = new URL(url)
    // Strip credentials to avoid leaking passwords via heartbeat
    // We keep username for debugging but mask password
    let hostname = parsed.hostname
    if (hostname.includes('xn--')) {
      hostname = punycode.toUnicode(hostname)
    }
    const port = parsed.port === '' ? '' : `:${parsed.port}`
    // Do not include userinfo/password in heartbeat payload
    return `${parsed.protocol}//${hostname}${port}${parsed.pathname}${parsed.search}${parsed.hash}`
  } catch (e) {
    console.error('Error decoding URL:', e)
    return url
  }
}

function isTrackableUrl(url: string): boolean {
  // Only track http/https; skip internal schemes and PIM itself to avoid self-loop
  if (url.startsWith(PIM_BASE_URL)) return false
  return url.startsWith('http://') || url.startsWith('https://')
}

function formatHeartbeatLogData(data: HeartbeatData) {
  return Object.entries(data)
    .map(([key, value]) => {
      const formattedValue =
        typeof value === 'string' ? JSON.stringify(value) : value === undefined ? 'undefined' : JSON.stringify(value)
      return `${key}=${formattedValue}`
    })
    .join(', ')
}

async function heartbeat(tab: browser.Tabs.Tab | undefined, tabCount: number) {
  const enabled = await getEnabled()
  if (!enabled) {
    console.warn('Ignoring heartbeat because extension is disabled')
    return
  }

  if (!tab) {
    console.warn('Ignoring heartbeat because no active tab was found')
    return
  }

  if (!tab.url || !tab.title) {
    console.warn('Ignoring heartbeat because tab is missing URL or title')
    return
  }

  if (!isTrackableUrl(tab.url)) {
    console.debug('Ignoring heartbeat for non-http url:', tab.url)
    return
  }

  const { url, title, audible, incognito } = tab
  const data: HeartbeatData = {
    url: decodeURL(url),
    title,
    audible: audible ?? false,
    incognito: incognito ?? false,
    tabCount,
    browser: getBrowserType(),
    instanceId: await getInstanceId(),
  }

  const previousData = await getHeartbeatData()
  if (previousData && deepEqual(previousData, data, { strict: true })) {
    console.debug('Skipping heartbeat, data unchanged:', formatHeartbeatLogData(data))
    return
  }

  console.debug(`Sending heartbeat: ${formatHeartbeatLogData(data)}`)
  const ok = await sendHeartbeat(data)
  if (ok) {
    await setHeartbeatData(data)
  } else {
    console.warn('Heartbeat send failed, will retry on next alarm')
  }
}

export const sendInitialHeartbeat = async () => {
  const activeWindowTab = await getActiveWindowTab()
  const tabs = await getTabs()
  console.debug('Sending initial heartbeat', activeWindowTab?.url)
  await heartbeat(activeWindowTab, tabs.length)
}

export const heartbeatAlarmListener = async (alarm: browser.Alarms.Alarm) => {
  if (alarm.name !== config.heartbeat.alarmName) return
  try {
    const activeWindowTab = await getActiveWindowTab()
    if (!activeWindowTab) return
    const tabs = await getTabs()
    console.debug('Sending heartbeat for alarm', activeWindowTab.url)
    await heartbeat(activeWindowTab, tabs.length)
  } catch (err) {
    console.warn('heartbeatAlarmListener failed:', err)
  }
}

export const tabActivatedListener = async (activeInfo: browser.Tabs.OnActivatedActiveInfoType) => {
  try {
    const tab = await getTab(activeInfo.tabId)
    const tabs = await getTabs()
    console.debug('Sending heartbeat for tab activation', tab?.url)
    await heartbeat(tab, tabs.length)
  } catch (err) {
    console.warn('tabActivatedListener failed:', err)
  }
}
