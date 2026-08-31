// PIM Browser Watcher - 替代 aw-client 的直连实现
const PIM_BASE_URL = 'http://localhost:15601';

export interface HeartbeatData {
  url: string;
  title: string;
  audible: boolean;
  incognito: boolean;
  tabCount: number;
}

export async function sendHeartbeat(data: HeartbeatData): Promise<boolean> {
  try {
    const resp = await fetch(`${PIM_BASE_URL}/browser/heartbeat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ...data,
        timestamp: new Date().toISOString(),
      }),
    });
    return resp.ok;
  } catch (err) {
    console.error('Failed to send heartbeat to PIM:', err);
    return false;
  }
}

export async function ping(): Promise<boolean> {
  try {
    const resp = await fetch(`${PIM_BASE_URL}/browser/ping`);
    return resp.ok;
  } catch {
    return false;
  }
}

export async function waitForPimClient(maxRetries = 10): Promise<boolean> {
  for (let i = 0; i < maxRetries; i++) {
    if (await ping()) return true;
    await new Promise(r => setTimeout(r, 3000));
  }
  return false;
}
