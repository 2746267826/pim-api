/**
 * #280 快速记录双悬浮按钮重合的回归护栏。
 *
 * 需求（已与用户确认）：
 *  1. 移除旧的蓝色悬浮按钮（QuickNoteFloatingButton）及其旧编辑面板
 *     （QuickNoteFloatingPanel），消除 /quick-notes 页右下角两个 + 号按钮重叠；
 *  2. 保留黑色按钮及其菜单 / 新建-编辑流程；
 *  3. 「写闪念」打开的编辑卡片（NoteDialog）支持拖动，排版与功能不变；
 *  4. 全局悬浮能力保留：每个页面仍有且只有一个快速记录入口；
 *  5. 迁移不造成功能缺项（草稿保留、位置记忆随迁移保留）。
 *
 * 本用例同时做源码结构断言与真实渲染断言 —— 纯源码断言挡不住「两个按钮同时挂载」，
 * 所以关键几条用 react-dom/server 真正渲染组件树来验证。
 */
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

function test(name: string, run: () => void) { run(); }

const read = (relative: string) => readFileSync(path.join(process.cwd(), relative), 'utf8');
const exists = (relative: string) => existsSync(path.join(process.cwd(), relative));

test('#280 旧蓝色悬浮按钮与旧编辑面板已删除', () => {
  assert.equal(
    exists('src/client-web/src/components/quick-notes/QuickNoteFloatingButton.tsx'),
    false,
    'QuickNoteFloatingButton 应被删除（它正是重叠的蓝色 + 按钮）',
  );
  assert.equal(
    exists('src/client-web/src/components/quick-notes/QuickNoteFloatingPanel.tsx'),
    false,
    'QuickNoteFloatingPanel 应被删除（旧编辑面板）',
  );
});

test('#280 全站布局不再挂载旧蓝色按钮，但仍有且只有一个全局快速记录入口', () => {
  const layout = read('src/client-web/src/layout/AppLayout.tsx');

  assert.equal(
    layout.includes('QuickNoteFloatingButton'),
    false,
    'AppLayout 不应再引用旧蓝色按钮组件',
  );
  assert.equal(
    layout.includes('QuickNoteFloatingPanel'),
    false,
    'AppLayout 不应再引用旧编辑面板组件',
  );

  // 全局入口仍存在（由新实现承载，且只有一个）
  const globalEntryMatches = layout.match(/<QuickNoteFloatingEntry\b/g) ?? [];
  assert.equal(
    globalEntryMatches.length,
    1,
    `AppLayout 应恰好挂载一个全局快速记录入口，实际 ${globalEntryMatches.length} 个`,
  );
});

test('#280 全局入口与页面内黑色按钮不会同时出现（真实渲染验证无重叠）', () => {
  const { QuickNoteFloatingEntry, PAGE_FAB_PATHS } = require('../../src/client-web/src/components/quick-notes/QuickNoteFloatingEntry');
  const { MemoryRouter } = requireFromClient('react-router-dom');

  // 页面内已有黑色 FAB 的路径（/quick-notes）不应再渲染全局入口，
  // 否则两个按钮会在右下角重叠 —— 这正是 issue 的现象。
  assert.ok(
    Array.isArray(PAGE_FAB_PATHS) || PAGE_FAB_PATHS instanceof Set,
    'PAGE_FAB_PATHS 应导出用于判断「本页已有自己的 FAB」',
  );

  // #300 起入口内部使用 useNavigate（菜单跳转），静态渲染需要 Router 上下文。
  const inRouter = (pathname: string) => renderToStaticMarkup(
    React.createElement(
      MemoryRouter,
      { initialEntries: [pathname] },
      React.createElement(QuickNoteFloatingEntry, { pathname }),
    ),
  );

  const onQuickNotes = inRouter('/quick-notes');
  const onToday = inRouter('/today');

  const countFab = (html: string) => (html.match(/aria-label="打开快速记录"/g) ?? []).length;

  assert.equal(countFab(onQuickNotes), 0, '/quick-notes 页不应再渲染全局入口（该页有自己的黑色按钮）');
  assert.equal(countFab(onToday), 1, '/today 等其他页面应恰好有一个全局入口');

  // 蓝色按钮的两个特征都不应再出现
  const allHtml = onQuickNotes + onToday;
  assert.equal(/bg-blue-600|bg-blue-500/.test(allHtml), false, '不应再出现蓝色按钮样式');
});

test('#280 编辑卡片支持拖动，且排版与功能标记保持不变', () => {
  const dialog = read('src/client-web/src/components/quick-notes/QuickNoteDialog.tsx');

  // 拖动能力
  assert.ok(dialog.includes('onPointerDown'), '编辑卡片应支持指针按下开始拖动');
  assert.ok(dialog.includes('onPointerMove'), '编辑卡片应支持拖动移动');
  assert.ok(dialog.includes('setPointerCapture'), '拖动应捕获指针，避免移出窗口丢失');

  // 排版与功能保持不变的锚点
  for (const kept of [
    '写闪念',
    '编辑记录',
    '记录分类：',
    '附件',
    '已归档',
    '标记处理',
    '支持 Markdown 语法',
  ]) {
    assert.ok(dialog.includes(kept), `编辑卡片应保留「${kept}」`);
  }
});

test('#280 迁移不造成功能缺项：草稿保留与位置记忆仍存在', () => {
  const state = read('src/client-web/src/components/quick-notes/quickNoteFloatingState.ts');

  assert.ok(state.includes('QUICK_NOTE_DRAFT_KEY'), '草稿 key 应保留');
  assert.ok(state.includes('QUICK_NOTE_PANEL_POSITION_KEY'), '位置记忆 key 应保留');
  assert.ok(state.includes('clampPanelPosition'), '位置夹取工具应保留');

  // #300：旧面板（QuickNoteGlobalPanel）已删除，全局入口改用「快速记录」页同款编辑卡片
  // （QuickNoteDialog，懒加载），草稿与位置记忆能力随之迁移到该卡片。
  const entry = read('src/client-web/src/components/quick-notes/QuickNoteFloatingEntry.tsx');
  const dialog = read('src/client-web/src/components/quick-notes/QuickNoteDialog.tsx');
  const entryAndDialog = entry + '\n' + dialog;

  assert.ok(entry.includes('QuickNoteDialog'), '全局入口应复用新版编辑卡片');
  assert.ok(entryAndDialog.includes('loadPanelPosition'), '全局入口应保留位置记忆能力');
  assert.ok(entryAndDialog.includes('savePanelPosition'), '全局入口应保留位置持久化能力');
});

test('#280 快速记录页仍有黑色按钮及其菜单（写闪念 / 建任务 / 排日程）', () => {
  const page = read('src/client-web/src/pages/QuickNotesPage.tsx');

  assert.ok(page.includes('bg-zinc-900'), '页面内黑色按钮应保留');
  for (const item of ['写闪念', '建任务', '排日程']) {
    assert.ok(page.includes(item), `黑色按钮菜单应保留「${item}」`);
  }
  assert.equal(/bg-blue-600/.test(page), false, '页面内不应再有蓝色悬浮按钮样式');
});

console.log('quickNoteFabTests passed');
