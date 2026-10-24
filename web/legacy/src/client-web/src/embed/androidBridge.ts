export interface BridgeMessage {
  version: 1;
  id: string;
  type: 'token.request' | 'token.refresh' | 'native.state.request' | 'page.report';
  failedAccessToken?: string;
  report?: {
    hasServerData?: boolean;
    generatedAt?: string | null;
    error?: string | null;
  };
}

export interface BridgeResponse {
  version: 1;
  id: string;
  ok: boolean;
  accessToken?: string;
  nativeState?: Record<string, unknown>;
  errorCode?: string;
  message?: string;
}

export interface NativeState {
  collectionMode?: boolean;
  triggerReason?: string;
  nextLocationAt?: string;
  pending?: number;
  uploading?: boolean;
  confirmed?: number;
  rejected?: number;
  lastSuccessAt?: string;
  nextAttemptAt?: string;
}

interface PimAndroidBridge {
  postMessage(raw: string): void;
  onmessage: ((event: MessageEvent) => void) | null;
}

function getBridge(win: Window): PimAndroidBridge {
  const b = (win as unknown as Record<string, unknown>).pimAndroid;
  if (!b || typeof (b as Record<string, unknown>).postMessage !== 'function') {
    throw new Error('bridge_unavailable');
  }
  return b as PimAndroidBridge;
}

export class AndroidBridgeClient {
  private accessToken: string | null = null;
  private pendingToken: Promise<string> | null = null;
  private pendingRefresh: Promise<string | null> | null = null;
  private pendingMap = new Map<string, {
    resolve: (res: BridgeResponse) => void;
    reject: (err: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  }>();
  private idCounter = 0;
  private win: Window;
  private timeoutMs: number;
  private _prevOnMessage: ((event: MessageEvent) => void) | null = null;
  private _attached = false;

  constructor(win?: Window, timeoutMs = 30000) {
    this.win = win ?? window;
    this.timeoutMs = timeoutMs;
    try {
      const bridge = getBridge(this.win);
      this._prevOnMessage = bridge.onmessage;
      bridge.onmessage = this._onMessage;
      this._attached = true;
    } catch {
      // bridge_unavailable: will attach on first _send()
    }
  }

  private _ensureAttached(): void {
    if (this._attached) return;
    try {
      const bridge = getBridge(this.win);
      this._prevOnMessage = bridge.onmessage;
      bridge.onmessage = this._onMessage;
      this._attached = true;
    } catch {
      // still unavailable; _send will throw bridge_unavailable
    }
  }

  private _nextId(): string {
    this.idCounter++;
    return `ab_${this.idCounter}_${Date.now()}`;
  }

  private _send(msg: BridgeMessage): void {
    this._ensureAttached();
    getBridge(this.win).postMessage(JSON.stringify(msg));
  }

  private _onMessage = (event: MessageEvent): void => {
    const raw = typeof event.data === 'string' ? event.data : null;
    if (!raw) return;
    let res: BridgeResponse;
    try {
      res = JSON.parse(raw);
    } catch {
      return;
    }
    if (res.version !== 1 || !res.id) return;

    const pending = this.pendingMap.get(res.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.pendingMap.delete(res.id);
    pending.resolve(res);
  };

  private _request(
    type: BridgeMessage['type'],
    extra: Record<string, unknown> = {},
  ): Promise<BridgeResponse> {
    const id = this._nextId();
    return new Promise<BridgeResponse>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pendingMap.delete(id);
        const err = new Error('bridge_timeout');
        (err as Error & { code?: string }).code = 'bridge_timeout';
        reject(err);
      }, this.timeoutMs);
      this.pendingMap.set(id, { resolve, reject, timer });
      try {
        this._send({ version: 1, id, type, ...extra } as BridgeMessage);
      } catch (err) {
        clearTimeout(timer);
        this.pendingMap.delete(id);
        reject(err);
      }
    });
  }

  async requestToken(): Promise<string> {
    if (this.accessToken) return this.accessToken;
    if (this.pendingToken) return this.pendingToken;

    this.pendingToken = (async () => {
      try {
        const res = await this._request('token.request');
        if (!res.ok || !res.accessToken) {
          const err = new Error(res.message || '登录已过期');
          (err as Error & { code?: string }).code = res.errorCode || 'login_expired';
          throw err;
        }
        this.accessToken = res.accessToken;
        return res.accessToken;
      } finally {
        this.pendingToken = null;
      }
    })();

    return this.pendingToken;
  }

  async refreshToken(failedAccessToken?: string): Promise<string | null> {
    if (this.pendingRefresh) return this.pendingRefresh;

    this.pendingRefresh = (async () => {
      try {
        const res = await this._request(
          'token.refresh',
          failedAccessToken ? { failedAccessToken } : {},
        );
        if (!res.ok || !res.accessToken) {
          this.accessToken = null;
          return null;
        }
        this.accessToken = res.accessToken;
        return res.accessToken;
      } finally {
        this.pendingRefresh = null;
      }
    })();

    return this.pendingRefresh;
  }

  async requestNativeState(): Promise<NativeState> {
    const res = await this._request('native.state.request');
    if (!res.ok) {
      const err = new Error(res.message || 'native_state_unavailable');
      (err as Error & { code?: string }).code = res.errorCode || '';
      throw err;
    }
    return (res.nativeState || {}) as NativeState;
  }

  async sendPageReport(report: BridgeMessage['report']): Promise<void> {
    const res = await this._request('page.report', { report });
    if (!res.ok) {
      const err = new Error(res.message || 'page_report_failed');
      (err as Error & { code?: string }).code = res.errorCode || '';
      throw err;
    }
  }

  getAccessToken(): string | null { return this.accessToken; }
  setAccessToken(token: string | null) { this.accessToken = token; }

  destroy() {
    try {
      const bridge = getBridge(this.win);
      if (bridge.onmessage === this._onMessage) {
        bridge.onmessage = this._prevOnMessage;
      }
    } catch { /* bridge already gone */ }
    for (const [, p] of this.pendingMap) {
      clearTimeout(p.timer);
      p.reject(new Error('bridge_destroyed'));
    }
    this.pendingMap.clear();
  }
}
