import browser from 'webextension-polyfill'
import deepEqual from 'deep-equal'
import * as punycode from 'punycode.js'
import { getActiveWindowTab, getTab, getTabs } from './helpers'
import { sendHeartbeat } from './client'
import type { HeartbeatData } from './client'
import { getEnabled, getHeartbeatData, setHeartbeatData } from '../storage'
import config from '../config'

function decodeURL(url: string): string {
  try {
    const parsed = new URL(url)
    if (!parsed.hostname.includes('xn--')) {
      return url
    }

    const decodedHost = punycode.toUnicode(parsed.hostname)
    const userinfo =
      parsed.username === '' ? '' : `${parsed.username}${parsed.password === '' ? '' : `:${parsed.password}`}@`
    const port = parsed.port === '' ? '' : `:${parsed.port}`
    return `${parsed.protocol}//${userinfo}${decodedHost}${port}${parsed.pathname}${parsed.search}${parsed.hash}`
  } catch (e) {
    console.error('Error decoding URL:', e)
    return url
  }
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

  const { url, title, audible, incognito } = tab
  const data: HeartbeatData = {
    url: decodeURL(url),
    title,
    audible: audible ?? false,
    incognito: incognito ?? false,
    tabCount,
  }

  const previousData = await getHeartbeatData()
  if (previousData && deepEqual(previousData, data)) {
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
  const activeWindowTab = await getActiveWindowTab()
  if (!activeWindowTab) return
  const tabs = await getTabs()
  console.debug('Sending heartbeat for alarm', activeWindowTab.url)
  await heartbeat(activeWindowTab, tabs.length)
}

export const tabActivatedListener = async (activeInfo: browser.Tabs.OnActivatedActiveInfoType) => {
  const tab = await getTab(activeInfo.tabId)
  const tabs = await getTabs()
  console.debug('Sending heartbeat for tab activation', tab.url)
  await heartbeat(tab, tabs.length)
}
