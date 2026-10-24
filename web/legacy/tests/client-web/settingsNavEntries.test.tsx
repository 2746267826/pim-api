import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { NAV_ITEMS, SETTINGS_SECTION_ITEMS, PAGE_TITLE_ITEMS } from '../../src/client-web/src/layout/navItems';
import SettingsPage from '../../src/client-web/src/pages/SettingsPage';
import { AuthProvider } from '../../src/client-web/src/auth/AuthContext';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const { MemoryRouter } = requireFromClient('react-router-dom') as typeof import('react-router-dom');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

// AboutPimCard → useVersionInfo 会取 vite 注入的 __APP_VERSION__ / import.meta.env；
// tsx(CJS) 下 import.meta.env 不可用，这里补上 vite define 的全局常量。
(globalThis as unknown as { __APP_VERSION__: string }).__APP_VERSION__ = '0.0.0-test';
(globalThis as unknown as { __GIT_SHA__: string }).__GIT_SHA__ = 'testsha';

// AuthProvider → api/client 的 loadTokens 会读 localStorage；node 环境下补一个最小实现。
if (!('localStorage' in globalThis)) {
  const store = new Map<string, string>();
  (globalThis as unknown as { localStorage: Storage }).localStorage = {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => { store.set(key, String(value)); },
    removeItem: (key: string) => { store.delete(key); },
    clear: () => { store.clear(); },
    key: (index: number) => [...store.keys()][index] ?? null,
    get length() { return store.size; },
  } as Storage;
}

function test(name: string, run: () => void) { run(); }

const read = (relative: string) => readFileSync(path.join(process.cwd(), relative), 'utf8');

/** 移入「设置」页的四个板块：页面与路由不变，只是入口位置改变。 */
const MOVED = [
  { label: '展览馆', to: '/exhibition' },
  { label: '状态信息', to: '/status' },
  { label: '设备管理', to: '/devices' },
  { label: '应用知识库', to: '/app-knowledge-base' },
];

test('#279 左侧主导航不再包含这四个板块', () => {
  const paths = NAV_ITEMS.map(item => item.path);
  for (const { to, label } of MOVED) {
    assert.equal(paths.includes(to), false, `主导航不应再出现 ${label}（${to}）`);
  }
});

test('#279 这四个页面仍注册在路由表中（功能与 URL 不变）', () => {
  const appLayout = read('src/client-web/src/layout/AppLayout.tsx');
  for (const { to } of MOVED) {
    assert.ok(
      appLayout.includes(`path="${to}"`),
      `AppLayout 必须保留路由 ${to}`,
    );
  }
});

test('#279 设置页渲染出四个入口卡片，且链接指向原页面', () => {
  const html = renderToStaticMarkup(
    React.createElement(
      MemoryRouter,
      null,
      // SettingsPage 的入口可见性依赖 useAuth（管理员卡片），必须包在 AuthProvider 内。
      React.createElement(AuthProvider, null, React.createElement(SettingsPage)),
    ),
  );

  for (const { label, to } of MOVED) {
    assert.ok(html.includes(label), `设置页应出现入口「${label}」`);
    assert.ok(html.includes(`href="${to}"`), `「${label}」应指向 ${to}`);
  }
});

test('#279 移入设置页的板块仍提供页头标题（标题不再回退为默认值）', () => {
  // AppLayout 用 NAV_ITEMS 查当前页标题；条目移出主导航后必须仍有标题来源，
  // 否则 /status 等页面头部会退化成「个人中枢」。
  const titles = new Map(PAGE_TITLE_ITEMS.map(item => [item.path, item.label]));
  for (const { label, to } of MOVED) {
    assert.equal(titles.get(to), label, `页头标题查找表应包含 ${to} -> ${label}`);
  }

  assert.ok(
    read('src/client-web/src/layout/AppLayout.tsx').includes('PAGE_TITLE_ITEMS'),
    'AppLayout 的标题查找应改用 PAGE_TITLE_ITEMS',
  );
});

test('#279 设置页原有入口与可见性逻辑保持不变', () => {
  const settingsSource = read('src/client-web/src/pages/SettingsPage.tsx');
  for (const kept of ['/settings/data-reliability', '/settings/calendar-data', '/settings/recycle-bin',
    '/settings/pc-data', '/settings/sync', '/settings/ai', '/settings/mcp', '/settings/users']) {
    assert.ok(settingsSource.includes(kept), `设置页应保留原入口 ${kept}`);
  }
  assert.ok(settingsSource.includes("role === 'admin'"), '管理员可见性逻辑应保持不变');
});

test('#279 移入设置页的四个板块不与主导航条目重复', () => {
  const navPaths = new Set(NAV_ITEMS.map(item => item.path));
  for (const item of SETTINGS_SECTION_ITEMS) {
    assert.equal(navPaths.has(item.path), false, `${item.path} 不应同时出现在主导航`);
  }
});

console.log('settingsNavEntries tests passed');
