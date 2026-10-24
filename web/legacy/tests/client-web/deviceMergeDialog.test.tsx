import assert from 'node:assert/strict';
import { describe, it, before, beforeEach, afterEach } from 'node:test';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import type { DeviceListItem } from '../../src/client-web/src/api/mobile';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

const React = requireFromWeb('react') as typeof import('react');
const { act } = React;
const { createRoot } = requireFromWeb('react-dom/client') as typeof import('react-dom/client');

let QueryClient: typeof import('@tanstack/react-query').QueryClient;
let QueryClientProvider: typeof import('@tanstack/react-query').QueryClientProvider;
let MergeConfirmDialog: typeof import('../../src/client-web/src/pages/DeviceManagementPage').MergeConfirmDialog;

let container: HTMLElement;
let root: ReturnType<typeof createRoot>;
let dom: InstanceType<typeof JSDOM>;
let originalFetch: typeof globalThis.fetch;

/** issue #232 的真实形态：6 台设备同名，只有第一台是当前活跃设备。 */
const ACTIVE = 'android-a5b98c2e27c8c280';
const STALE_A = 'android-c8afbd44c95ad974';
const STALE_B = 'android-86e569f5435a0dd7';

function device(overrides: Partial<DeviceListItem> & { deviceId: string }): DeviceListItem {
  return {
    displayName: 'OPPO PLG110',
    brand: 'OPPO',
    model: 'PLG110',
    osVersion: '15',
    appVersion: '2026.09.504',
    registeredAtUtc: '2026-07-07T10:42:16Z',
    lastSeenAtUtc: '2026-07-07T10:42:16Z',
    isOnline: false,
    sessionCount: 0,
    eventCount: 0,
    locationCount: 0,
    summaryCount: 0,
    earliest: null,
    latest: null,
    storageEstimateKb: 0,
    syncStatus: 'disconnected',
    dataQuality: 'normal',
    storagePressure: 'normal',
    ...overrides,
  };
}

const DEVICES: DeviceListItem[] = [
  device({
    deviceId: ACTIVE,
    lastSeenAtUtc: '2026-09-13T09:48:00Z',
    isOnline: true,
    sessionCount: 63302,
    eventCount: 256447,
    locationCount: 5938,
    summaryCount: 99695,
  }),
  device({ deviceId: STALE_A, lastSeenAtUtc: '2026-07-11T05:19:02Z', sessionCount: 19990, eventCount: 79501, locationCount: 275, summaryCount: 16188 }),
  device({ deviceId: STALE_B, lastSeenAtUtc: '2026-07-09T03:24:38Z', sessionCount: 18444, eventCount: 72170, locationCount: 2, summaryCount: 14681 }),
];

const SOURCE_TOTAL = (19990 + 79501 + 275 + 16188) + (18444 + 72170 + 2 + 14681);

const previewCalls: Array<{ sourceDeviceIds: string[]; targetDeviceId: string }> = [];

function installFetch() {
  previewCalls.length = 0;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/mobile/devices/merge/preview')) {
      const body = JSON.parse(String(init?.body ?? '{}')) as {
        sourceDeviceIds: string[];
        targetDeviceId: string;
      };
      previewCalls.push(body);
      const items = [...body.sourceDeviceIds, body.targetDeviceId].map(deviceId => ({
        deviceId,
        dataCount: deviceId === ACTIVE ? 100 : deviceId === STALE_A ? 200 : 300,
      }));
      return new Response(
        JSON.stringify({ code: 0, message: 'OK', data: { items, total: items.reduce((sum, item) => sum + item.dataCount, 0) } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }
    return new Response(JSON.stringify({ code: 0, message: 'OK', data: null }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as typeof globalThis.fetch;
}

async function settle(times = 6) {
  for (let i = 0; i < times; i++) {
    await act(async () => {
      await new Promise(resolve => setTimeout(resolve, 0));
    });
  }
}

function render(mergeSel: string[], onClose: () => void = () => {}) {
  // gcTime 必须是 0：默认 5 分钟会在 root.unmount() 之后留下一个仍然活跃的定时器，
  // Node 事件循环不退出，单条测试文件会白等 5 分钟（CI web job 同样受影响）。
  // query 与 mutation 都有各自的 gcTime，两处都要关掉。
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false, gcTime: 0 },
    },
  });
  act(() => {
    root.render(
      React.createElement(
        QueryClientProvider,
        { client: queryClient },
        React.createElement(MergeConfirmDialog, { mergeSel, devices: DEVICES, onClose }),
      ),
    );
  });
  return queryClient;
}

function radioInputs(): HTMLInputElement[] {
  return Array.from(container.querySelectorAll('input[type="radio"]'));
}

before(async () => {
  dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  globalThis.window = dom.window as unknown as Window & typeof globalThis;
  globalThis.document = dom.window.document;
  globalThis.Node = dom.window.Node;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.HTMLInputElement = dom.window.HTMLInputElement;
  globalThis.HTMLDocument = dom.window.HTMLDocument;
  globalThis.DOMParser = dom.window.DOMParser;
  globalThis.React = React;
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

  const reactQueryEsmUrl = requireFromWeb.resolve('@tanstack/react-query').replace(/\.cjs$/, '.js');
  const reactQuery = (await import(pathToFileURL(reactQueryEsmUrl).href)) as typeof import('@tanstack/react-query');
  QueryClient = reactQuery.QueryClient;
  QueryClientProvider = reactQuery.QueryClientProvider;

  const mod = await import('../../src/client-web/src/pages/DeviceManagementPage');
  MergeConfirmDialog = mod.MergeConfirmDialog;
});

beforeEach(() => {
  originalFetch = globalThis.fetch;
  installFetch();
  // React root 不能重复挂载：每个用例都要新建容器与 root。
  container = dom.window.document.createElement('div');
  dom.window.document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => {
    root.unmount();
  });
  container.remove();
  globalThis.fetch = originalFetch;
});

describe('合并设备弹窗（issue #232）', () => {
  it('默认保留最近活跃的那台，并把它标成保留、其余标成将被移除', async () => {
    render([ACTIVE, STALE_A, STALE_B]);
    await settle();

    const radios = radioInputs();
    assert.equal(radios.length, 3, '每台勾选设备都应有一个「保留哪台」选项');
    assert.equal(radios.filter(input => input.checked).length, 1, '必须恰好默认选中一台');
    assert.equal(radios.find(input => input.checked)?.value, ACTIVE, '默认应为最近活跃的设备');

    // 默认目标确定后应自动拉一次预览，源设备不含目标设备
    assert.equal(previewCalls.length, 1);
    assert.deepEqual(previewCalls[0].targetDeviceId, ACTIVE);
    assert.deepEqual([...previewCalls[0].sourceDeviceIds].sort(), [STALE_A, STALE_B].sort());
  });

  it('同名设备的每个选项都带短码、最后活跃、记录数与活跃标记', async () => {
    render([ACTIVE, STALE_A, STALE_B]);
    await settle();

    const text = container.textContent ?? '';
    assert.ok(text.includes('…c8c280'), '活跃设备需要展示 ID 短码');
    assert.ok(text.includes('…5ad974'), '历史设备 A 需要展示 ID 短码');
    assert.ok(text.includes('…5a0dd7'), '历史设备 B 需要展示 ID 短码');
    assert.ok(text.includes('当前活跃'), '需要标出当前活跃设备');
    assert.ok(text.includes('425382 条记录'), '需要展示每台设备的记录数');
    assert.ok(/最后活跃/.test(text), '需要展示最后活跃时间');
    assert.ok(
      text.includes('勾选的 3 台设备都会参与合并'),
      '需要说明勾选集合与保留设备的关系',
    );
  });

  it('预览按设备列出将被并入的记录数，合计不含被保留设备自身', async () => {
    render([ACTIVE, STALE_A, STALE_B]);
    await settle();

    const text = container.textContent ?? '';
    assert.ok(text.includes('将并入 500 条记录'), `合计应为两台源设备的 200+300，实际渲染：${text}`);
    assert.ok(text.includes('200 条'), '历史设备 A 的明细缺失');
    assert.ok(text.includes('300 条'), '历史设备 B 的明细缺失');
    assert.ok(text.includes('保留'), '需要标明哪台被保留');
    assert.ok(text.includes('并入后移除'), '需要标明哪些设备会被移除');
  });

  it('改选目标设备后按新的源设备集合重新预览', async () => {
    render([ACTIVE, STALE_A, STALE_B]);
    await settle();

    const staleRadio = radioInputs().find(input => input.value === STALE_A);
    assert.ok(staleRadio, '历史设备应可作为保留设备');
    await act(async () => {
      staleRadio!.click();
    });
    await settle();

    assert.equal(previewCalls.length, 2, '改选目标后必须重新预览');
    assert.equal(previewCalls[1].targetDeviceId, STALE_A);
    assert.deepEqual([...previewCalls[1].sourceDeviceIds].sort(), [ACTIVE, STALE_B].sort());
    const text = container.textContent ?? '';
    assert.ok(text.includes('将并入 400 条记录'), `合计应为 100+300，实际渲染：${text}`);
  });

  it('勾选 2 台设备时也给出可区分的选项与明细', async () => {
    render([ACTIVE, STALE_A]);
    await settle();

    assert.equal(radioInputs().length, 2);
    const text = container.textContent ?? '';
    assert.ok(text.includes('勾选的 2 台设备都会参与合并'));
    assert.ok(text.includes('将并入 200 条记录'));
  });

  it('设备列表刷新导致勾选设备消失时给出提示并禁用确认合并', async () => {
    // mergeSel 里有一台设备已经不在列表里（后台刷新后设备被删/被合并）
    render([ACTIVE, STALE_A, 'android-vanished000000']);
    await settle();

    assert.equal(radioInputs().length, 2, '只渲染仍然存在的设备');
    const text = container.textContent ?? '';
    assert.ok(text.includes('部分勾选设备已不在列表中'), '需要明确提示，而不是只把按钮置灰');
    const confirm = Array.from(container.querySelectorAll('button'))
      .find(button => button.textContent?.includes('确认合并'));
    assert.ok(confirm, '确认合并按钮应存在');
    assert.equal(confirm!.disabled, true, '勾选集合失效时不允许确认合并');
    assert.equal(previewCalls.length, 0, '勾选集合失效时不应发起预览');
  });

  it('合并进行中禁止关闭弹窗', async () => {
    let closeCalls = 0;
    let resolveMerge: (() => void) | null = null;
    render([ACTIVE, STALE_A], () => { closeCalls += 1; });
    await settle();

    // 让合并请求挂起，模拟「合并中...」
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/mobile/devices/merge')) {
        await new Promise<void>(resolve => { resolveMerge = resolve; });
        return new Response(JSON.stringify({ code: 0, message: 'OK', data: 'merged' }), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify({ code: 0, message: 'OK', data: null }), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      });
    }) as typeof globalThis.fetch;

    const confirm = Array.from(container.querySelectorAll('button'))
      .find(button => button.textContent?.includes('确认合并'));
    assert.ok(confirm, '确认合并按钮应存在');
    await act(async () => {
      confirm!.click();
    });
    await settle(2);

    assert.ok(container.textContent?.includes('合并中'), '应进入合并中状态');

    const backdrop = container.firstElementChild as HTMLElement;
    await act(async () => {
      backdrop.click();
    });
    const closeButton = container.querySelector('button[aria-label="关闭"]') as HTMLButtonElement;
    assert.equal(closeButton.disabled, true, '合并进行中关闭按钮必须是 disabled 状态');
    const cancelButton = Array.from(container.querySelectorAll('button'))
      .find(button => button.textContent?.includes('取消'));
    assert.equal(cancelButton?.disabled, true, '合并进行中取消按钮必须是 disabled 状态');
    await act(async () => {
      closeButton.click();
    });
    assert.equal(closeCalls, 0, '合并进行中不允许关闭弹窗');

    await act(async () => {
      resolveMerge?.();
    });
    await settle();
    assert.equal(closeCalls, 1, '合并成功后应关闭弹窗');
  });
});
