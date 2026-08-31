import browser from 'webextension-polyfill'
import { getBrowserName, setBrowserName } from '../storage'

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
