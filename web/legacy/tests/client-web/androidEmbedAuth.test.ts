/* eslint-disable @typescript-eslint/no-explicit-any, @typescript-eslint/no-require-imports */
import assert from 'node:assert/strict';

// ── helpers ────────────────────────────────────────────────────────
const _tests: { name: string; fn: () => void | Promise<void> }[] = [];

function test(name: string, fn: () => void | Promise<void>) {
  _tests.push({ name, fn });
}

async function _runAll(): Promise<void> {
  let exitCode = 0;
  for (const { name, fn } of _tests) {
    const restoreGlobals: Array<() => void> = [];
    for (const key of ['window', 'fetch', 'localStorage'] as const) {
      const desc = Object.getOwnPropertyDescriptor(globalThis, key);
      if (desc) {
        restoreGlobals.push(() => Object.defineProperty(globalThis, key, desc));
      } else {
        restoreGlobals.push(() => { delete (globalThis as Record<string, unknown>)[key]; });
      }
    }
    try {
      await fn();
      console.error(`PASS: ${name}`);
    } catch (err) {
      console.error(`FAIL: ${name}`);
      console.error(err);
      exitCode = 1;
    } finally {
      for (const restore of restoreGlobals) restore();
    }
  }
  if (exitCode !== 0) process.exit(exitCode);
}

interface MockWindow {
  pimAndroid: {
    postMessage(raw: string): void;
    onmessage: ((event: { data: string }) => void) | null;
  };
  addEventListener: (type: string, cb: (event: { data: string }) => void) => void;
  removeEventListener: (type: string, cb: (event: { data: string }) => void) => void;
  sendResponse(raw: string): void;
  getSent(): BridgeMessage[];
  clearSent(): void;
  sent: string[];
  getEventListeners(type: string): Array<(event: { data: string }) => void>;
}

import type { BridgeMessage } from '../../src/client-web/src/embed/androidBridge';
import { AndroidBridgeClient } from '../../src/client-web/src/embed/androidBridge';

function mockWindow(): MockWindow {
  const sent: string[] = [];
  const pimAndroid = {
    postMessage(raw: string) { sent.push(raw); },
    onmessage: null as ((event: { data: string }) => void) | null,
  };
  const listeners = new Map<string, Array<(event: { data: string }) => void>>();
  return {
    pimAndroid,
    addEventListener(type: string, cb: (event: { data: string }) => void) {
      let arr = listeners.get(type);
      if (!arr) { arr = []; listeners.set(type, arr); }
      arr.push(cb);
    },
    removeEventListener(type: string, cb: (event: { data: string }) => void) {
      const arr = listeners.get(type);
      if (arr) { const i = arr.indexOf(cb); if (i >= 0) arr.splice(i, 1); }
    },
    getEventListeners(type: string) { return listeners.get(type) ?? []; },
    sendResponse(raw: string) { pimAndroid.onmessage?.({ data: raw }); },
    getSent(): BridgeMessage[] { return sent.map(s => JSON.parse(s)); },
    clearSent() { sent.length = 0; },
    sent,
  };
}

// ────────────────────────────────────────────────────────────────────
//  Bridge client unit tests
// ────────────────────────────────────────────────────────────────────

test('requestToken: sends one postMessage and returns the access token', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  const tokenReq = bridge.requestToken();

  const msgs = win.getSent();
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].version, 1);
  assert.equal(msgs[0].type, 'token.request');
  assert.ok(typeof msgs[0].id === 'string' && msgs[0].id.length > 0);

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_abc',
  }));

  const token = await tokenReq;
  assert.equal(token, 'tok_abc');
  assert.equal(bridge.getAccessToken(), 'tok_abc');
});

test('two concurrent requestToken calls send only one postMessage', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);

  const req1 = bridge.requestToken();
  const req2 = bridge.requestToken();

  const msgs = win.getSent();
  assert.equal(msgs.length, 1, 'should send exactly one token.request');
  assert.equal(msgs[0].type, 'token.request');

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_concurrent',
  }));

  const [t1, t2] = await Promise.all([req1, req2]);
  assert.equal(t1, 'tok_concurrent');
  assert.equal(t2, 'tok_concurrent');
});

test('requestToken caches token for subsequent calls', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);

  const t1 = bridge.requestToken();
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: true, accessToken: 'tok_cached',
  }));
  assert.equal(await t1, 'tok_cached');
  assert.equal(win.getSent().length, 1);

  win.clearSent();
  const t2 = bridge.requestToken();
  assert.equal(await t2, 'tok_cached');
  assert.equal(win.getSent().length, 0, 'should not send another request when token is cached');
});

test('refreshToken sends token.refresh and returns new token', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_old');
  const refreshP = bridge.refreshToken('tok_old');

  const msgs = win.getSent();
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].type, 'token.refresh');
  assert.equal(msgs[0].failedAccessToken, 'tok_old');

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_new',
  }));

  const token = await refreshP;
  assert.equal(token, 'tok_new');
  assert.equal(bridge.getAccessToken(), 'tok_new');
});

test('two concurrent refreshToken calls share one bridge refresh request', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_old');

  const r1 = bridge.refreshToken();
  const r2 = bridge.refreshToken();

  const msgs = win.getSent();
  assert.equal(msgs.length, 1, 'should send exactly one token.refresh');

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_shared',
  }));

  const [t1, t2] = await Promise.all([r1, r2]);
  assert.equal(t1, 'tok_shared');
  assert.equal(t2, 'tok_shared');
});

test('refreshToken failure returns null and clears access token', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_expired');

  const refreshP = bridge.refreshToken('tok_expired');
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: false,
    errorCode: 'login_expired', message: '登录已过期',
  }));

  const result = await refreshP;
  assert.equal(result, null);
  assert.equal(bridge.getAccessToken(), null);
});

test('refreshToken after failure can succeed next time', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_fail');

  const failP = bridge.refreshToken('tok_fail');
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: false, errorCode: 'login_expired',
  }));
  assert.equal(await failP, null);

  win.clearSent();
  const okP = bridge.refreshToken();
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: true, accessToken: 'tok_recovered',
  }));
  assert.equal(await okP, 'tok_recovered');
});

test('bridge_unavailable when window.pimAndroid is missing', async () => {
  const winNoBridge = {
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  const bridge = new AndroidBridgeClient(winNoBridge as unknown as Window);

  await assert.rejects(
    () => bridge.requestToken(),
    (err: Error) => {
      assert.ok(err.message.includes('bridge_unavailable'), err.message);
      return true;
    },
  );
});

test('request id matching works with out-of-order replies', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_order');

  // Send refresh + native state concurrently (different types, not deduped)
  const r1 = bridge.refreshToken();
  const id1 = win.getSent()[0].id;
  win.clearSent();

  const r2 = bridge.requestNativeState();
  const id2 = win.getSent()[0].id;

  // Reply out of order: native state response arrives before refresh response
  win.sendResponse(JSON.stringify({
    version: 1, id: id2, ok: true, nativeState: { pending: 5 },
  }));
  win.sendResponse(JSON.stringify({
    version: 1, id: id1, ok: true, accessToken: 'tok_out_of_order',
  }));

  const [t1, state] = await Promise.all([r1, r2]);
  assert.equal(t1, 'tok_out_of_order');
  assert.deepEqual(state, { pending: 5 });
});

test('native state request uses correct protocol', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);

  const nativeP = bridge.requestNativeState();
  const msgs = win.getSent();
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].type, 'native.state.request');

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true,
    nativeState: {
      collectionMode: true,
      pending: 3,
      uploading: false,
    },
  }));

  const state = await nativeP;
  assert.deepEqual(state, {
    collectionMode: true,
    pending: 3,
    uploading: false,
  });
});

test('page report sends correct protocol fields', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);

  const reportData = {
    hasServerData: true,
    generatedAt: '2026-07-17T00:00:00Z',
    error: null,
  };

  const reportP = bridge.sendPageReport(reportData);
  const msgs = win.getSent();
  assert.equal(msgs.length, 1);
  assert.equal(msgs[0].type, 'page.report');
  assert.deepEqual(msgs[0].report, reportData);

  win.sendResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true,
  }));

  await reportP;
});

test('timeout rejects with bridge_timeout error code and message', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window, 50);

  await assert.rejects(
    () => bridge.requestToken(),
    (err: any) => {
      assert.equal(err.code, 'bridge_timeout');
      assert.equal(err.message, 'bridge_timeout');
      return true;
    },
  );
});

// ────────────────────────────────────────────────────────────────────
//  API client embed mode: localStorage never touched
// ────────────────────────────────────────────────────────────────────

test('embed mode loadTokens and setTokens do not touch localStorage', () => {
  const lsCalls: string[] = [];
  const lsStore = new Map<string, string>();
  const mockLS = {
    getItem: (k: string) => { lsCalls.push(`get:${k}`); return lsStore.get(k) ?? null; },
    setItem: (k: string, v: string) => { lsCalls.push(`set:${k}`); lsStore.set(k, v); },
    removeItem: (k: string) => { lsCalls.push(`remove:${k}`); lsStore.delete(k); },
    clear: () => { lsCalls.push('clear'); lsStore.clear(); },
    get length() { return lsStore.size; },
    key: (i: number) => [...lsStore.keys()][i] ?? null,
  };
  const origLS = (globalThis as any).localStorage;
  (globalThis as any).localStorage = mockLS;

  // Capture the original pathname and set embed mode
  const origLoc = (globalThis as any).window?.location;
  const mockLoc = { pathname: '/embed/android/today', href: '', origin: '', search: '' };
  if (!(globalThis as any).window) {
    (globalThis as any).window = { location: mockLoc };
  } else {
    (globalThis as any).window.location = mockLoc;
  }

  // Import client AFTER setting up embed mode
  delete require.cache[require.resolve('../../src/client-web/src/api/client')];
  const client = require('../../src/client-web/src/api/client');
  const embedCalls = lsCalls.length;
  assert.equal(embedCalls, 0, 'no localStorage calls during import in embed mode');

  // clearTokens should not touch localStorage
  client.clearTokens();
  const clearCalls = lsCalls.filter(c => c.startsWith('remove:'));
  assert.equal(clearCalls.length, 0, 'clearTokens should not call localStorage.removeItem in embed mode');

  // setTokens should not touch localStorage
  client.setTokens('tok_embed', 'rt_embed');
  const setCalls = lsCalls.filter(c => c.startsWith('set:'));
  assert.equal(setCalls.length, 0, 'setTokens should not call localStorage.setItem in embed mode');

  // Restore
  (globalThis as any).localStorage = origLS;
  if (origLoc) (globalThis as any).window.location = origLoc;
});

// ────────────────────────────────────────────────────────────────────
//  Desktop mode preservation
// ────────────────────────────────────────────────────────────────────

test('desktop mode setTokens writes to localStorage', () => {
  const lsCalls: string[] = [];
  const lsStore = new Map<string, string>();
  const mockLS = {
    getItem: (k: string) => { lsCalls.push(`get:${k}`); return lsStore.get(k) ?? null; },
    setItem: (k: string, v: string) => { lsCalls.push(`set:${k}`); lsStore.set(k, v); },
    removeItem: (k: string) => { lsCalls.push(`remove:${k}`); lsStore.delete(k); },
    clear: () => { lsCalls.push('clear'); lsStore.clear(); },
    get length() { return lsStore.size; },
    key: (i: number) => [...lsStore.keys()][i] ?? null,
  };
  const origLS = (globalThis as any).localStorage;
  (globalThis as any).localStorage = mockLS;

  // Set desktop mode path
  const origLoc = (globalThis as any).window?.location;
  const mockLoc = { pathname: '/today', href: '', origin: '', search: '' };
  if (!(globalThis as any).window) {
    (globalThis as any).window = { location: mockLoc };
  } else {
    (globalThis as any).window.location = mockLoc;
  }

  delete require.cache[require.resolve('../../src/client-web/src/api/client')];
  const client = require('../../src/client-web/src/api/client');

  client.setTokens('tok_desk', 'rt_desk');
  assert.ok(lsCalls.some(c => c === 'set:accessToken'), 'should set accessToken in localStorage');
  assert.ok(lsCalls.some(c => c === 'set:refreshToken'), 'should set refreshToken in localStorage');

  // Restore
  (globalThis as any).localStorage = origLS;
  if (origLoc) (globalThis as any).window.location = origLoc;
});

test('desktop mode 401 without refreshToken does not call /auth/refresh', async () => {
  const origWin = (globalThis as any).window;
  const origFetch = (globalThis as any).fetch;
  const origLS = (globalThis as any).localStorage;

  // Desktop: non-embed path
  (globalThis as any).window = {
    location: { pathname: '/today', href: '', origin: '', search: '' },
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  (globalThis as any).localStorage = {
    getItem: () => null, setItem: () => {}, removeItem: () => {},
    clear: () => {}, get length() { return 0; }, key: () => null,
  };

  const fetchCalls: string[] = [];
  (globalThis as any).fetch = (url: string) => {
    fetchCalls.push(url);
    return Promise.resolve(new Response(JSON.stringify({ message: 'Unauthorized' }), {
      status: 401, headers: { 'Content-Type': 'application/json' },
    }));
  };

  delete require.cache[require.resolve('../../src/client-web/src/api/client')];
  const client = require('../../src/client-web/src/api/client');

  // No tokens loaded → no refreshToken → should NOT call /auth/refresh
  await assert.rejects(
    () => client.apiGet('/today'),
    (err: Error) => err.message.includes('登录已过期'),
  );

  assert.equal(fetchCalls.length, 1, 'exactly one fetch call');
  assert.equal(fetchCalls[0], '/api/v1/today', 'only the original API call');
  assert.ok(!fetchCalls.some(u => u.includes('/auth/refresh')), 'no auth refresh');

  (globalThis as any).window = origWin;
  (globalThis as any).fetch = origFetch;
  (globalThis as any).localStorage = origLS;
});

test('desktop mode with refreshToken calls /auth/refresh on 401 then retries', async () => {
  const origWin = (globalThis as any).window;
  const origFetch = (globalThis as any).fetch;
  const origLS = (globalThis as any).localStorage;

  (globalThis as any).window = {
    location: { pathname: '/today', href: '', origin: '', search: '' },
    addEventListener: () => {},
    removeEventListener: () => {},
  };

  const lsStore = new Map<string, string>([
    ['accessToken', 'tok_desk_old'],
    ['refreshToken', 'rt_desk'],
  ]);
  (globalThis as any).localStorage = {
    getItem: (k: string) => lsStore.get(k) ?? null,
    setItem: (k: string, v: string) => { lsStore.set(k, v); },
    removeItem: (k: string) => { lsStore.delete(k); },
    clear: () => lsStore.clear(),
    get length() { return lsStore.size; },
    key: (i: number) => [...lsStore.keys()][i] ?? null,
  };

  const fetchCalls: { url: string; method: string; body?: string }[] = [];
  (globalThis as any).fetch = (url: string, opts: RequestInit = {}) => {
    const call = { url, method: (opts.method as string) || 'GET', body: opts.body as string | undefined };
    fetchCalls.push(call);

    if (url === '/api/v1/auth/refresh') {
      return Promise.resolve(new Response(JSON.stringify({
        data: { accessToken: 'tok_desk_new', refreshToken: 'rt_desk_new' },
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }

    // First API call → 401, retry → 200
    const apiCalls = fetchCalls.filter(c => c.url === '/api/v1/today').length;
    if (apiCalls === 1) {
      return Promise.resolve(new Response(JSON.stringify({ message: 'expired' }), {
        status: 401, headers: { 'Content-Type': 'application/json' },
      }));
    }
    return Promise.resolve(new Response(JSON.stringify({ data: { id: 1 } }), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }));
  };

  delete require.cache[require.resolve('../../src/client-web/src/api/client')];
  const client = require('../../src/client-web/src/api/client');
  client.loadTokens();

  const result = await client.apiGet('/today');
  assert.deepEqual(result, { data: { id: 1 } });

  // Verify refresh flow
  const refreshCall = fetchCalls.find(c => c.url === '/api/v1/auth/refresh');
  assert.ok(refreshCall, '/auth/refresh was called');
  assert.equal(refreshCall!.method, 'POST');
  const reqBody = JSON.parse(refreshCall!.body || '{}');
  assert.equal(reqBody.refreshToken, 'rt_desk');

  // Token was updated
  assert.equal(lsStore.get('accessToken'), 'tok_desk_new');

  (globalThis as any).window = origWin;
  (globalThis as any).fetch = origFetch;
  (globalThis as any).localStorage = origLS;
});

// ────────────────────────────────────────────────────────────────────
//  Embed 401 → refresh → retry (bridge-level integration)
// ────────────────────────────────────────────────────────────────────

test('embed 401 triggers single refresh via bridge with concurrent dedup', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_start');

  const r1 = bridge.refreshToken('tok_start');
  const r2 = bridge.refreshToken('tok_start');

  // Only one message sent
  assert.equal(win.getSent().length, 1);
  assert.equal(win.getSent()[0].type, 'token.refresh');

  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: true, accessToken: 'tok_refreshed',
  }));

  const [t1, t2] = await Promise.all([r1, r2]);
  assert.equal(t1, 'tok_refreshed');
  assert.equal(t2, 'tok_refreshed');
  assert.equal(bridge.getAccessToken(), 'tok_refreshed');
});

test('refresh failure: login expired and no further retry', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);
  bridge.setAccessToken('tok_good');

  const r1 = bridge.refreshToken('tok_good');
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: false,
    errorCode: 'login_expired', message: '登录已过期',
  }));

  const result = await r1;
  assert.equal(result, null, 'should return null on refresh failure');
  assert.equal(bridge.getAccessToken(), null, 'should clear access token');

  // A subsequent requestToken should start fresh (no cached token)
  win.clearSent();
  assert.equal(bridge.getAccessToken(), null);
});

// ────────────────────────────────────────────────────────────────────
//  Embed integration: apiGet → bridge.requestToken → Bearer fetch
// ────────────────────────────────────────────────────────────────────

function embedTestFix() {
  const origWin = (globalThis as any).window;
  const origFetch = (globalThis as any).fetch;
  const origLS = (globalThis as any).localStorage;

  const sent: string[] = [];
  const fetchCalls: { url: string; method: string; headers: Record<string, string> }[] = [];
  let fetchHandler: ((url: string, opts: RequestInit) => { status: number; body?: unknown }) | null = null;
  let postMessageThrow: Error | null = null;

  const pimAndroid = {
    postMessage(raw: string) {
      if (postMessageThrow) throw postMessageThrow;
      sent.push(raw);
    },
    onmessage: null as ((e: { data: string }) => void) | null,
  };

  (globalThis as any).window = {
    pimAndroid,
    addEventListener(_: string, _cb: (e: { data: string }) => void) { /* no-op */ },
    removeEventListener(_: string, _cb: (e: { data: string }) => void) { /* no-op */ },
    location: { pathname: '/embed/android/today', href: '', origin: '', search: '' },
  };
  (globalThis as any).localStorage = {
    getItem: () => null, setItem: () => {}, removeItem: () => {},
    clear: () => {}, get length() { return 0; }, key: () => null,
  };
  (globalThis as any).fetch = (url: string, opts: RequestInit = {}) => {
    const hdrs: Record<string, string> = {};
    if (opts.headers) { new Headers(opts.headers).forEach((v, k) => { hdrs[k] = v; }); }
    fetchCalls.push({ url, method: (opts.method as string) || 'GET', headers: hdrs });
    if (fetchHandler) {
      const r = fetchHandler(url, opts);
      return Promise.resolve(new Response(JSON.stringify(r.body ?? null), {
        status: r.status, headers: { 'Content-Type': 'application/json' },
      }));
    }
    return Promise.resolve(new Response('{"data":[]}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
  };

  function sendBridgeResponse(raw: string) { pimAndroid.onmessage?.({ data: raw }); }
  function getSentBridgeMessages() { return sent.map(s => JSON.parse(s)); }
  function importClient() {
    delete require.cache[require.resolve('../../src/client-web/src/api/client')];
    return require('../../src/client-web/src/api/client');
  }
  function restore() {
    (globalThis as any).window = origWin;
    (globalThis as any).fetch = origFetch;
    (globalThis as any).localStorage = origLS;
  }

  return {
    sent, fetchCalls,
    setFetchHandler(h: typeof fetchHandler) { fetchHandler = h; },
    setPostMessageThrow(err: Error | null) { postMessageThrow = err; },
    sendBridgeResponse, getSentBridgeMessages, importClient, restore,
  };
}

function idle(): Promise<void> { return new Promise(r => setImmediate(r)); }

test('embed first apiGet: requests token then Bearer fetch', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();

  const resultP = client.apiGet('/today');
  await idle();

  // Should have sent a token.request
  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'sent one bridge message');
  assert.equal(msgs[0].type, 'token.request');

  // Respond with access token
  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_first',
  }));

  const result = await resultP;
  assert.deepEqual(result, { data: [] }, 'apiGet returns parsed JSON');

  // Verify fetch used Bearer
  assert.equal(fx.fetchCalls.length, 1, 'one fetch call');
  assert.equal(fx.fetchCalls[0].url, '/api/v1/today', 'path is /api/v1/today');
  assert.equal(fx.fetchCalls[0].headers['authorization'], 'Bearer tok_first');

  fx.restore();
});

test('embed two concurrent apiGet: single token.request', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();

  const p1 = client.apiGet('/a');
  const p2 = client.apiGet('/b');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'exactly one token.request for two concurrent apiGet');
  assert.equal(msgs[0].type, 'token.request');

  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_concurrent',
  }));

  const [r1, r2] = await Promise.all([p1, p2]);
  assert.deepEqual(r1, { data: [] });
  assert.deepEqual(r2, { data: [] });

  // Both fetches used the same token
  assert.ok(fx.fetchCalls.every(c => c.headers['authorization'] === 'Bearer tok_concurrent'));
  // Both fetches are to /api/v1/* URLs
  assert.equal(fx.fetchCalls[0].url, '/api/v1/a');
  assert.equal(fx.fetchCalls[1].url, '/api/v1/b');

  fx.restore();
});

test('embed 401 triggers bridge refresh and retries with new token', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  client.setTokens('tok_expired', '');

  let callNo = 0;
  fx.setFetchHandler(() => {
    callNo++;
    if (callNo === 1) return { status: 401, body: { message: 'expired' } };
    return { status: 200, body: { data: { ok: true } } };
  });

  const resultP = client.apiGet('/profile');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'sent one bridge message');
  assert.equal(msgs[0].type, 'token.refresh');
  assert.equal(msgs[0].failedAccessToken, 'tok_expired');

  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_refreshed',
  }));

  const result = await resultP;
  assert.deepEqual(result, { data: { ok: true } });

  // Retry used new token
  const retryFetch = fx.fetchCalls[fx.fetchCalls.length - 1];
  assert.equal(retryFetch.url, '/api/v1/profile');
  assert.equal(retryFetch.headers['authorization'], 'Bearer tok_refreshed');

  fx.restore();
});

test('embed two concurrent 401 calls share one refresh', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  client.setTokens('tok_shared', '');

  let callNo = 0;
  fx.setFetchHandler((url) => {
    callNo++;
    // Both initial calls get 401; retries get 200 on the 3rd/4th call
    if (callNo <= 2) return { status: 401, body: { message: 'expired' } };
    return { status: 200, body: { data: { url } } };
  });

  const p1 = client.apiGet('/a');
  const p2 = client.apiGet('/b');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'single token.refresh for two concurrent 401s');
  assert.equal(msgs[0].type, 'token.refresh');

  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_after_refresh',
  }));

  const [r1, r2] = await Promise.all([p1, p2]);
  assert.ok(r1 && r2, 'both requests succeed after shared refresh');

  // Exactly one refresh message total
  const refreshMsgs = fx.getSentBridgeMessages().filter(m => m.type === 'token.refresh');
  assert.equal(refreshMsgs.length, 1);

  // Both retries use the new token
  const refreshedFetches = fx.fetchCalls.filter(c => c.headers['authorization'] === 'Bearer tok_after_refresh');
  assert.equal(refreshedFetches.length, 2, 'both retried fetches use refreshed token');

  fx.restore();
});

test('embed refresh failure: clears token, calls onTokensChanged, throws Chinese error', async () => {
  const fx = embedTestFix();
  let authChanged = false;
  const client = fx.importClient();
  client.onTokensChanged(() => { authChanged = true; });
  client.setTokens('tok_bad', '');

  fx.setFetchHandler(() => ({ status: 401, body: { message: 'expired' } }));

  const resultP = client.apiGet('/secure');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: false,
    errorCode: 'login_expired', message: '登录已过期',
  }));

  await assert.rejects(
    () => resultP,
    (err: Error) => {
      assert.ok(err.message.includes('登录已过期'),
        `expected Chinese error message, got: ${err.message}`);
      return true;
    },
  );

  assert.ok(authChanged, 'onTokensChanged callback was invoked');

  fx.restore();
});

test('embed 401 with bridge postMessage throw: Chinese auth error, auth callback, no retry', async () => {
  const fx = embedTestFix();
  let authChanged = false;
  const client = fx.importClient();
  client.onTokensChanged(() => { authChanged = true; });
  client.setTokens('tok_bad', '');

  fx.setFetchHandler(() => ({ status: 401, body: { message: 'expired' } }));
  fx.setPostMessageThrow(new Error('bridge transport failed'));

  await assert.rejects(
    () => client.apiGet('/secure'),
    (err: any) => {
      assert.ok(err.message.includes('登录已过期'),
        `expected Chinese auth error without machine details, got: ${err.message}`);
      return true;
    },
  );

  assert.ok(authChanged, 'onTokensChanged callback was invoked');

  // Only original fetch; no retry fetch
  assert.equal(fx.fetchCalls.length, 1, 'exactly one fetch call (no retry)');
  assert.equal(fx.fetchCalls[0].url, '/api/v1/secure');

  // No bridge message sent (postMessage threw before sending)
  assert.equal(fx.sent.length, 0, 'no bridge message sent (transport failed)');

  fx.restore();
});

test('embed retry still 401: stops without second refresh', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  client.setTokens('tok_first', '');

  let refreshCount = 0;
  fx.setFetchHandler(() => {
    // First API call → 401
    if (refreshCount === 0) return { status: 401, body: { message: 'expired' } };
    // After refresh, still 401 (but don't count as another refresh attempt)
    return { status: 401, body: { message: 'still expired' } };
  });

  const resultP = client.apiGet('/doomed');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_second',
  }));
  refreshCount = 1; // Next fetch will also be 401

  await assert.rejects(
    () => resultP,
    (err: Error) => {
      assert.ok(err.message.includes('401') || err.message.includes('still expired'),
        `expected HTTP error, got: ${err.message}`);
      return true;
    },
  );

  // Only one refresh attempt despite still getting 401 after refresh
  const refreshMsgs = fx.getSentBridgeMessages().filter(m => m.type === 'token.refresh');
  assert.equal(refreshMsgs.length, 1, 'no second refresh attempt after retry still 401');

  fx.restore();
});

// ────────────────────────────────────────────────────────────────────
//  Regression: bridge uses pimAndroid.onmessage, not window events
// ────────────────────────────────────────────────────────────────────



test('bridge uses pimAndroid.onmessage not window message events', async () => {
  const win = mockWindow();
  const bridge = new AndroidBridgeClient(win as unknown as Window);

  // Bridge must NOT register any window 'message' listeners
  assert.equal(win.getEventListeners('message').length, 0,
    'bridge must not register window message event listeners');

  // Bridge response must still work through pimAndroid.onmessage
  const tokenP = bridge.requestToken();
  win.sendResponse(JSON.stringify({
    version: 1, id: win.getSent()[0].id, ok: true, accessToken: 'tok_onmsg',
  }));

  assert.equal(await tokenP, 'tok_onmsg');

  bridge.destroy();
});

// ────────────────────────────────────────────────────────────────────
//  Regression: embed → desktop → embed round trip sends token.request
// ────────────────────────────────────────────────────────────────────

test('embed -> desktop -> embed: second embed phase sends token.request (provenance reset)', async () => {
  const origWin = (globalThis as any).window;
  const origFetch = (globalThis as any).fetch;
  const origLS = (globalThis as any).localStorage;

  try {
    const sent: string[] = [];
    const fetchCalls: { url: string; headers: Record<string, string> }[] = [];
    let pimAndroidOnMsg: ((e: { data: string }) => void) | null = null;
    const pimAndroid = {
      postMessage(raw: string) { sent.push(raw); },
      set onmessage(cb: ((e: { data: string }) => void) | null) { pimAndroidOnMsg = cb; },
      get onmessage() { return pimAndroidOnMsg; },
    };

    // Phase 1: Cold embed
    (globalThis as any).window = {
      pimAndroid,
      addEventListener: () => {},
      removeEventListener: () => {},
      location: { pathname: '/embed/android/today', href: '', origin: '', search: '' },
    };
    (globalThis as any).localStorage = {
      getItem: () => null, setItem: () => {}, removeItem: () => {},
      clear: () => {}, get length() { return 0; }, key: () => null,
    };
    (globalThis as any).fetch = (url: string, opts: RequestInit = {}) => {
      const hdrs: Record<string, string> = {};
      if (opts.headers) { new Headers(opts.headers).forEach((v, k) => { hdrs[k] = v; }); }
      fetchCalls.push({ url, headers: hdrs });
      return Promise.resolve(new Response('{"data":[]}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    };

    delete require.cache[require.resolve('../../src/client-web/src/api/client')];
    const client = require('../../src/client-web/src/api/client');

    // First API call in embed mode -> requests native token
    const p1 = client.apiGet('/embed-data');
    await idle();

    const msgs1 = sent.map(s => JSON.parse(s));
    assert.equal(msgs1.length, 1, 'cold embed: one bridge message sent');
    assert.equal(msgs1[0].type, 'token.request');

    pimAndroidOnMsg!({ data: JSON.stringify({
      version: 1, id: msgs1[0].id, ok: true, accessToken: 'tok_native_1',
    }) });
    await p1;

    const embedFetch = fetchCalls.find(c => c.url === '/api/v1/embed-data');
    assert.ok(embedFetch, 'cold embed API call made');
    assert.equal(embedFetch!.headers['authorization'], 'Bearer tok_native_1');

    // Phase 2: Switch to desktop
    sent.length = 0;
    fetchCalls.length = 0;
    (globalThis as any).window.location.pathname = '/today';
    client.loadTokens();
    client.setTokens('tok_desk', 'rt_desk');

    // Phase 3: Switch back to embed (no re-import of module)
    sent.length = 0;
    fetchCalls.length = 0;
    (globalThis as any).window.location.pathname = '/embed/android/today';

    // Second API call in embed mode -> MUST send token.request
    const p2 = client.apiGet('/embed-again');
    await idle();

    assert.ok(sent.length > 0, 'return-to-embed: must send bridge message');
    const msg2 = JSON.parse(sent[0]);
    assert.equal(msg2.type, 'token.request', 'return-to-embed: must send token.request');

    pimAndroidOnMsg!({ data: JSON.stringify({
      version: 1, id: msg2.id, ok: true, accessToken: 'tok_native_2',
    }) });
    await p2;

    const retryFetch = fetchCalls.find(c => c.url === '/api/v1/embed-again');
    assert.ok(retryFetch, 'return-to-embed API call made');
    assert.equal(retryFetch!.headers['authorization'], 'Bearer tok_native_2',
      'must use native token from bridge, not desktop token');
  } finally {
    (globalThis as any).window = origWin;
    (globalThis as any).fetch = origFetch;
    (globalThis as any).localStorage = origLS;
  }
});

// ────────────────────────────────────────────────────────────────────
//  Regression: lazy attach — bridge injected after construction
// ────────────────────────────────────────────────────────────────────

test('constructor without bridge: inject pimAndroid later, requestToken succeeds via onmessage', async () => {
  const winNoBridge = {
    addEventListener: () => {},
    removeEventListener: () => {},
  } as any;

  const bridge = new AndroidBridgeClient(winNoBridge as unknown as Window, 100);

  // Now inject pimAndroid
  const sent: string[] = [];
  let onMsg: ((e: { data: string }) => void) | null = null;
  winNoBridge.pimAndroid = {
    postMessage(raw: string) { sent.push(raw); },
    set onmessage(cb: ((e: { data: string }) => void) | null) { onMsg = cb; },
    get onmessage() { return onMsg; },
  };

  const tokenP = bridge.requestToken();
  await idle();

  // Without fix, onMsg is still null (bridge never attached handler)
  assert.notEqual(onMsg, null,
    'bridge must attach onmessage handler before first send (lazy attach)');

  assert.equal(sent.length, 1, 'should send token.request after bridge becomes available');
  const msg = JSON.parse(sent[0]);
  assert.equal(msg.type, 'token.request');

  onMsg!({ data: JSON.stringify({
    version: 1, id: msg.id, ok: true, accessToken: 'tok_lazy',
  }) });

  const token = await tokenP;
  assert.equal(token, 'tok_lazy');
  assert.equal(bridge.getAccessToken(), 'tok_lazy');
});

test('destroy restores previous handler only if current handler is still this client', () => {
  const win = mockWindow();
  const prevHandler = win.pimAndroid.onmessage;

  const bridge = new AndroidBridgeClient(win as unknown as Window);
  assert.equal(win.pimAndroid.onmessage, (bridge as any)._onMessage,
    'bridge sets itself as handler');

  // Simulate another handler installed after bridge
  const otherHandler = () => {};
  win.pimAndroid.onmessage = otherHandler;

  bridge.destroy();

  // Should NOT restore to prevHandler because the current handler is otherHandler
  assert.equal(win.pimAndroid.onmessage, otherHandler,
    'should not overwrite a handler installed after bridge');
});

// ────────────────────────────────────────────────────────────────────
//  clearTokens race condition tests
// ────────────────────────────────────────────────────────────────────

test('embed clearTokens clears the initialized bridge token', async () => {
  const fx = embedTestFix();
  try {
    const client = fx.importClient();
    const bridge = await client.getEmbedBridgeClient();
    bridge!.setAccessToken('tok_stale');

    client.clearTokens();

    assert.equal(bridge!.getAccessToken(), null,
      'bridge token should be null after clearTokens');
  } finally {
    fx.restore();
  }
});

test('embed clearTokens invalidates an in-flight refresh result', async () => {
  const fx = embedTestFix();
  try {
    const client = fx.importClient();
    client.setTokens('tok_expired', '');

    let fetchCallCount = 0;
    fx.setFetchHandler(() => {
      fetchCallCount++;
      if (fetchCallCount === 1) return { status: 401, body: { message: 'expired' } };
      return { status: 200, body: { data: { ok: true } } };
    });

    const resultP = client.apiGet('/secure');
    await idle();

    const msgs = fx.getSentBridgeMessages();
    assert.equal(msgs.length, 1, 'one bridge message sent');
    assert.equal(msgs[0].type, 'token.refresh',
      'message type should be token.refresh');

    client.clearTokens();

    fx.sendBridgeResponse(JSON.stringify({
      version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_stale_refresh',
    }));

    await assert.rejects(
      () => resultP,
      (err: Error) => {
        assert.ok(err.message.includes('登录已过期'),
          `expected login expired error, got: ${err.message}`);
        return true;
      },
    );

    assert.equal(fetchCallCount, 1,
      'fetch should be called exactly once (no retry after clearTokens)');

    const bridge = await client.getEmbedBridgeClient();
    assert.equal(bridge!.getAccessToken(), null,
      'bridge token should be null after clearTokens');
  } finally {
    fx.restore();
  }
});

test('embed clearTokens keeps a stale refresh single-flight until it settles', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  const bridge = await client.getEmbedBridgeClient();
  assert.ok(bridge, 'bridge must exist for embed mode');

  let refreshCallCount = 0;
  const refreshResolvers: Array<(val: string | null) => void> = [];

  (bridge as any).refreshToken = function (_failedAccessToken?: string): Promise<string | null> {
    refreshCallCount++;
    return new Promise<string | null>((resolve) => {
      refreshResolvers.push(resolve);
    });
  };

  const urlCounts = new Map<string, number>();
  fx.setFetchHandler((url: string) => {
    const count = (urlCounts.get(url) || 0) + 1;
    urlCounts.set(url, count);
    if (count === 1) return { status: 401, body: { message: 'expired' } };
    return { status: 200, body: { data: {} } };
  });

  let pA: Promise<unknown> | undefined;
  let pB: Promise<unknown> | undefined;
  let pC: Promise<unknown> | undefined;

  try {
    client.setTokens('tok_a', '');
    pA = client.apiGet('/a');
    await idle();

    assert.equal(refreshCallCount, 1, 'call A triggers one refresh');
    assert.equal(refreshResolvers.length, 1, 'one resolver stored');

    client.clearTokens();
    client.setTokens('tok_b', '');
    pB = client.apiGet('/b');
    await idle();

    assert.equal(refreshCallCount, 1,
      'after clearTokens, B must share the stale refresh until it settles');

    const resolveA = refreshResolvers[0];
    resolveA('tok_stale_a');
    const staleResults = await Promise.allSettled([pA, pB]);
    assert.ok(staleResults.every(result => result.status === 'rejected'),
      'requests sharing the stale refresh must both reject');
    await idle();

    client.setTokens('tok_c', '');
    pC = client.apiGet('/c');
    await idle();

    assert.equal(refreshCallCount, 2,
      'a new refresh may start only after the stale single-flight settles');
    refreshResolvers[1]('tok_c_refreshed');
    await pC;
    assert.equal(urlCounts.get('/api/v1/c'), 2,
      'the post-boundary request retries once with the fresh token');
  } finally {
    for (const r of refreshResolvers) {
      try { r('tok_cleanup'); } catch { /* already resolved */ }
    }
    await Promise.allSettled([pA, pB, pC]);
    if (bridge) delete (bridge as any).refreshToken;
    fx.restore();
  }
});

test('embed clearTokens prevents later requests from adopting the old native refresh', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  client.setTokens('tok_old', '');

  const urlCounts = new Map<string, number>();
  fx.setFetchHandler((url: string) => {
    const count = (urlCounts.get(url) || 0) + 1;
    urlCounts.set(url, count);
    if (count === 1) return { status: 401, body: { message: 'expired' } };
    return { status: 200, body: { data: {} } };
  });

  const pA = client.apiGet('/a');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'one bridge message after first apiGet 401');
  assert.equal(msgs[0].type, 'token.refresh');

  client.clearTokens();
  client.setTokens('tok_new_session', '');

  const pB = client.apiGet('/b');
  await idle();

  assert.equal(fx.getSentBridgeMessages().length, 1,
    'clearTokens must not cause a second bridge refresh message');

  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_stale_native_refresh',
  }));

  const results = await Promise.allSettled([pA, pB]);

  for (const r of results) {
    assert.equal(r.status, 'rejected',
      'both requests must reject after clearTokens discards stale refresh');
    assert.ok(
      (r as PromiseRejectedResult).reason?.message?.includes('登录已过期'),
      `expected login expired error, got: ${(r as PromiseRejectedResult).reason?.message}`,
    );
  }

  assert.equal(fx.fetchCalls.length, 2, 'exactly two fetch calls (one per URL, no retry)');
  assert.equal(fx.fetchCalls[0].url, '/api/v1/a');
  assert.equal(fx.fetchCalls[1].url, '/api/v1/b');

  const bridge = await client.getEmbedBridgeClient();
  assert.equal(bridge!.getAccessToken(), null,
    'bridge access token must be null after stale refresh discarded');

  fx.restore();
});

test('embed clearTokens invalidates an in-flight initial token request', async () => {
  const fx = embedTestFix();
  const client = fx.importClient();
  const bridge = await client.getEmbedBridgeClient();
  assert.ok(bridge, 'bridge must exist for embed mode');

  fx.setFetchHandler((url: string, opts: RequestInit) => {
    const hdrs = new Headers(opts.headers);
    const auth = hdrs.get('authorization');
    if (auth === 'Bearer tok_stale_initial') return { status: 200, body: { data: {} } };
    return { status: 401, body: { message: 'expired' } };
  });

  const pA = client.apiGet('/a');
  await idle();

  const msgs = fx.getSentBridgeMessages();
  assert.equal(msgs.length, 1, 'one token.request');
  assert.equal(msgs[0].type, 'token.request');

  client.clearTokens();

  const pB = client.apiGet('/b');
  await idle();

  assert.equal(fx.getSentBridgeMessages().length, 1,
    'B must share the same single-flight token.request after clearTokens');

  fx.sendBridgeResponse(JSON.stringify({
    version: 1, id: msgs[0].id, ok: true, accessToken: 'tok_stale_initial',
  }));
  await idle();

  const refreshMsgs = fx.getSentBridgeMessages().filter(m => m.type === 'token.refresh');
  if (refreshMsgs.length > 0) {
    fx.sendBridgeResponse(JSON.stringify({
      version: 1, id: refreshMsgs[0].id, ok: false,
      errorCode: 'login_expired', message: '登录已过期',
    }));
  }

  const results = await Promise.allSettled([pA, pB]);

  for (const r of results) {
    assert.equal(r.status, 'rejected',
      'both requests must reject after clearTokens discards stale initial token');
    assert.ok(
      (r as PromiseRejectedResult).reason?.message?.includes('登录已过期'),
      `expected login expired error, got: ${(r as PromiseRejectedResult).reason?.message}`,
    );
  }

  for (const call of fx.fetchCalls) {
    if (call.url === '/api/v1/a' || call.url === '/api/v1/b') {
      assert.notEqual(call.headers['authorization'], 'Bearer tok_stale_initial',
        'fetch must not use the stale initial token after clearTokens');
    }
  }

  assert.equal(bridge!.getAccessToken(), null,
    'bridge access token must be null after clearTokens');

  fx.restore();
});

test('desktop clearTokens invalidates an in-flight refresh response', async () => {
  const origWin = (globalThis as any).window;
  const origFetch = (globalThis as any).fetch;
  const origLS = (globalThis as any).localStorage;

  try {
    (globalThis as any).window = {
      location: { pathname: '/today', href: '', origin: '', search: '' },
      addEventListener: () => {},
      removeEventListener: () => {},
    };

    const lsStore = new Map<string, string>([
      ['accessToken', 'tok_desktop_old'],
      ['refreshToken', 'rt_desktop_old'],
    ]);
    (globalThis as any).localStorage = {
      getItem: (k: string) => lsStore.get(k) ?? null,
      setItem: (k: string, v: string) => { lsStore.set(k, v); },
      removeItem: (k: string) => { lsStore.delete(k); },
      clear: () => lsStore.clear(),
      get length() { return lsStore.size; },
      key: (i: number) => [...lsStore.keys()][i] ?? null,
    };

    let refreshResolve!: (res: Response) => void;
    const refreshPromise = new Promise<Response>((resolve) => { refreshResolve = resolve; });

    let todayCallCount = 0;
    (globalThis as any).fetch = (url: string, _opts: RequestInit = {}) => {
      if (url === '/api/v1/auth/refresh') return refreshPromise;
      if (url === '/api/v1/today') {
        todayCallCount++;
        if (todayCallCount === 1) {
          return Promise.resolve(new Response(JSON.stringify({ message: 'Unauthorized' }), {
            status: 401, headers: { 'Content-Type': 'application/json' },
          }));
        }
        return Promise.resolve(new Response(JSON.stringify({ data: { ok: true } }), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        }));
      }
      return Promise.resolve(new Response('{}', { status: 404 }));
    };

    delete require.cache[require.resolve('../../src/client-web/src/api/client')];
    const client = require('../../src/client-web/src/api/client');
    client.loadTokens();

    const p = client.apiGet('/today');
    await idle();

    client.clearTokens();
    assert.equal(lsStore.has('accessToken'), false,
      'accessToken cleared from localStorage');
    assert.equal(lsStore.has('refreshToken'), false,
      'refreshToken cleared from localStorage');

    refreshResolve(new Response(JSON.stringify({
      data: { accessToken: 'tok_desktop_stale', refreshToken: 'rt_desktop_stale' },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    await assert.rejects(
      () => p,
      (err: Error) => {
        assert.ok(err.message.includes('登录已过期'),
          `expected login expired error, got: ${err.message}`);
        return true;
      },
    );

    assert.equal(todayCallCount, 1,
      '/today only called once (no retry after clearTokens)');

    assert.equal(lsStore.has('accessToken'), false,
      'localStorage accessToken must remain empty');
    assert.equal(lsStore.has('refreshToken'), false,
      'localStorage refreshToken must remain empty');
  } finally {
    (globalThis as any).window = origWin;
    (globalThis as any).fetch = origFetch;
    (globalThis as any).localStorage = origLS;
  }
});

_runAll().catch((err) => {
  console.error('FATAL: test runner failed', err);
  process.exitCode = 1;
});
