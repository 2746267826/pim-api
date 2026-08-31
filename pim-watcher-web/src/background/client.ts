// PIM Browser Watcher - 替代 aw-client 的直连实现
export const PIM_BASE_URL = 'http://localhost:15601'

const FETCH_TIMEOUT_MS = 5000

async function fetchWithTimeout(input: RequestInfo, init: RequestInit = {}, timeoutMs = FETCH_TIMEOUT_MS): Promise<Response> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    return await fetch(input, { ...init, signal: controller.signal })
  } finally {
    clearTimeout(timer)
  }
}

export interface HeartbeatData {
  url: string;
  title: string;
  audible: boolean;
  incognito: boolean;
  tabCount: number;
}

export async function sendHeartbeat(data: HeartbeatData): Promise<boolean> {
  try {
    const resp = await fetchWithTimeout(`${PIM_BASE_URL}/browser/heartbeat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ...data,
        timestamp: new Date().toISOString(),
      }),
    })
    return resp.ok
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      console.warn('Heartbeat timeout to PIM')
    } else {
      console.error('Failed to send heartbeat to PIM:', err)
    }
    return false
  }
}

export async function ping(): Promise<boolean> {
  try {
    const resp = await fetchWithTimeout(`${PIM_BASE_URL}/browser/ping`, {}, 3000)
    return resp.ok
  } catch {
    return false
  }
}

export async function waitForPimClient(maxRetries = 10): Promise<boolean> {
  for (let i = 0; i < maxRetries; i++) {
    if (await ping()) return true;
    await new Promise(r => setTimeout(r, 3000));
  }
  return false;
}
