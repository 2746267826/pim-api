const BASE = '/api/v1';

let accessToken: string | null = null;
let refreshToken: string | null = null;
let onAuthChange: (() => void) | null = null;
let bridgeClient: import('../embed/androidBridge').AndroidBridgeClient | null | undefined = undefined;
let bridgeInitPromise: Promise<void> | null = null;
let initialTokenPromise: Promise<void> | null = null;
let refreshPromise: Promise<boolean> | null = null;
let tokenProvenance: 'embed' | 'desktop' | null = null;
let generation = 0;

function isEmbed(): boolean {
  try {
    return typeof window !== 'undefined'
      && typeof window.location !== 'undefined'
      && window.location.pathname.startsWith('/embed/android/');
  } catch { return false; }
}

async function getBridgeClient(): Promise<import('../embed/androidBridge').AndroidBridgeClient | null> {
  if (!isEmbed()) return null;
  if (bridgeClient !== undefined) return bridgeClient;
  if (!bridgeInitPromise) {
    bridgeInitPromise = (async () => {
      try {
        const { AndroidBridgeClient } = await import('../embed/androidBridge');
        bridgeClient = new AndroidBridgeClient();
      } catch { bridgeClient = null; }
    })();
  }
  await bridgeInitPromise;
  return bridgeClient!;
}

export function setTokens(access: string, refresh: string) {
  accessToken = access;
  refreshToken = refresh;
  tokenProvenance = isEmbed() ? 'embed' : 'desktop';
  if (!isEmbed()) {
    localStorage.setItem('accessToken', access);
    localStorage.setItem('refreshToken', refresh);
  }
}

export function loadTokens(): boolean {
  if (isEmbed()) {
    accessToken = null;
    refreshToken = null;
    tokenProvenance = null;
    return false;
  }
  accessToken = localStorage.getItem('accessToken');
  refreshToken = localStorage.getItem('refreshToken');
  tokenProvenance = accessToken ? 'desktop' : null;
  return !!accessToken;
}

export function clearTokens() {
  accessToken = null;
  refreshToken = null;
  tokenProvenance = null;
  generation++;
  bridgeClient?.setAccessToken(null);
  if (!isEmbed()) {
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
  }
}

export function onTokensChanged(cb: () => void) { onAuthChange = cb; }

export function getEmbedBridgeClient(): Promise<import('../embed/androidBridge').AndroidBridgeClient | null> {
  return getBridgeClient();
}

async function refreshAccessToken(): Promise<boolean> {
  if (refreshPromise) return refreshPromise;
  const savedGen = generation;
  const currentPromise = (async () => {
    if (isEmbed()) {
      try {
        const bridge = await getBridgeClient();
        if (!bridge) {
          if (generation === savedGen) {
            clearTokens();
            onAuthChange?.();
          }
          return false;
        }
        const newToken = await bridge.refreshToken(accessToken ?? undefined);
        if (generation !== savedGen) {
          bridge.setAccessToken(null);
          return false;
        }
        if (newToken) {
          accessToken = newToken;
          return true;
        }
      } catch {
        // bridge transport failure
      }
      if (generation === savedGen) {
        accessToken = null;
        clearTokens();
        onAuthChange?.();
      }
      return false;
    }
    // desktop: 复用同一 refreshPromise 避免并发 401 导致第二请求误登出
    if (!refreshToken) return false;
    const savedRefreshToken = refreshToken;
    try {
      const res = await fetch(`${BASE}/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: savedRefreshToken })
      });
      if (!res.ok) return false;
      const json = await res.json();
      const d = json.data;
      if (generation !== savedGen || refreshToken !== savedRefreshToken) {
        return false;
      }
      setTokens(d.accessToken, d.refreshToken);
      return true;
    } catch { return false; }
  })();
  refreshPromise = currentPromise;
  currentPromise.then(
    () => { if (refreshPromise === currentPromise) refreshPromise = null; },
    () => { if (refreshPromise === currentPromise) refreshPromise = null; },
  );
  return refreshPromise;
}

export async function apiGet<T>(path: string): Promise<T> {
  return authedFetch<T>(path);
}

export async function apiPost<T>(path: string, body?: unknown): Promise<T> {
  return authedFetch<T>(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined });
}

export async function apiPut<T>(path: string, body?: unknown): Promise<T> {
  return authedFetch<T>(path, { method: 'PUT', body: body ? JSON.stringify(body) : undefined });
}

export async function apiDelete<T>(path: string): Promise<T> {
  return authedFetch<T>(path, { method: 'DELETE' });
}

export async function apiUpload<T>(path: string, body: BodyInit): Promise<T> {
  return apiFetchRaw<T>(path, { method: 'POST', body });
}

export async function apiDownloadBlob(path: string): Promise<Blob> {
  const res = await apiFetchResponse(path);
  return res.blob();
}

function logApi(method: string, path: string, duration: number, status?: number) {
  const msg = `[API] ${method} ${path} → ${status || '???'} (${duration}ms)`;
  const dev = (import.meta as { env?: { DEV?: boolean } }).env?.DEV;
  if (dev) console.log(msg);
}

export async function authedFetch<T>(path: string, opts: RequestInit = {}, acceptedStatuses?: readonly number[]): Promise<T> {
  return apiFetchRaw<T>(path, opts, true, acceptedStatuses);
}

async function apiFetchRaw<T>(
  path: string,
  opts: RequestInit = {},
  includeJsonContentType = false,
  acceptedStatuses?: readonly number[],
): Promise<T> {
  const res = await apiFetchResponse(path, opts, includeJsonContentType, acceptedStatuses);

  if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T;
  const contentType = res.headers.get('content-type') || '';
  if (contentType.includes('application/json')) {
    return res.json() as Promise<T>;
  }
  // 非 JSON 响应：先读取文本，检测是否为 HTML（SPA fallback 常见）
  const text = await res.text();
  const trimmed = text.trim().toLowerCase();
  if (trimmed.startsWith('<!doctype') || trimmed.startsWith('<html') || contentType.includes('text/html')) {
    const preview = text.slice(0, 120).replace(/\s+/g, ' ');
    throw new Error(`接口 ${path} 返回了 HTML 而非 JSON（可能是路径错误或后端未启动）：${preview}`);
  }
  if (!text) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new Error(`接口 ${path} 返回了非 JSON 响应（Content-Type: ${contentType || 'unknown'}）：${text.slice(0, 200)}`);
  }
}

async function apiFetchResponse(
  path: string,
  opts: RequestInit = {},
  includeJsonContentType = false,
  acceptedStatuses?: readonly number[],
): Promise<Response> {
  const start = performance.now();
  const method = opts.method || 'GET';

  if (isEmbed() && tokenProvenance !== 'embed') {
    if (!initialTokenPromise) {
      const savedGen = generation;
      const currentPromise = (async () => {
        const bridge = await getBridgeClient();
        if (!bridge) return;
        if (generation !== savedGen) {
          bridge.setAccessToken(null);
          return;
        }
        accessToken = null;
        bridge.setAccessToken(null);
        const token = await bridge.requestToken();
        if (generation !== savedGen) {
          bridge.setAccessToken(null);
          return;
        }
        if (token) {
          accessToken = token;
          tokenProvenance = 'embed';
        }
      })();
      initialTokenPromise = currentPromise;
      currentPromise.then(
        () => { if (initialTokenPromise === currentPromise) initialTokenPromise = null; },
        () => { if (initialTokenPromise === currentPromise) initialTokenPromise = null; },
      );
    }
    await initialTokenPromise;
  }

  const headers = new Headers(opts.headers);
  if (includeJsonContentType && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);

  let res = await fetch(`${BASE}${path}`, { ...opts, headers });

  if (res.status === 401) {
    const ok = await refreshAccessToken();
    if (ok) {
      headers.set('Authorization', `Bearer ${accessToken}`);
      res = await fetch(`${BASE}${path}`, { ...opts, headers });
    } else {
      if (!isEmbed()) {
        clearTokens();
        onAuthChange?.();
      }
      const e = new Error('登录已过期，请重新登录');
      (e as unknown as { status?: number }).status = 401;
      throw e;
    }
  }

  const elapsed = Math.round(performance.now() - start);
  logApi(method, path, elapsed, res.status);

  if (!res.ok && !(acceptedStatuses?.includes(res.status))) {
    const contentType = res.headers.get('content-type') || '';
    let err: { message?: string; detail?: string; title?: string } = {};
    if (contentType.includes('application/json')) {
      err = await res.json().catch(() => ({} as { message?: string; detail?: string; title?: string }));
    } else {
      const text = await res.text().catch(() => '');
      const trimmed = text.trim().toLowerCase();
      if (trimmed.startsWith('<!doctype') || trimmed.startsWith('<html') || contentType.includes('text/html')) {
        const preview = text.slice(0, 120).replace(/\s+/g, ' ');
        const e = new Error(`接口 ${path} 返回了 HTML 而非 JSON（HTTP ${res.status}，可能是路径错误或代理未配置）：${preview}`);
        (e as unknown as { status?: number }).status = res.status;
        throw e;
      }
      try { err = JSON.parse(text); } catch { err = { message: text.slice(0, 200) || `HTTP ${res.status}` }; }
    }
    const msg = err.message || err.detail || err.title || `HTTP ${res.status}`;
    const e = new Error(msg);
    (e as unknown as { status?: number }).status = res.status;
    // 保留原始 detail 以便上层做更友好展示
    if (err.detail) (e as unknown as { detail?: string }).detail = err.detail;
    throw e;
  }

  return res;
}
