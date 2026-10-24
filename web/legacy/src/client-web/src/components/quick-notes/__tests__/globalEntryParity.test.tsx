/**
 * #300 回归护栏：全局「快速记录」入口与「快速记录」页行为必须一致。
 *
 * 现象（#280 落地后）：同一个黑色「+」按钮存在两种行为 ——
 *   - 除「快速记录」页外的所有页面：点击 → **直接弹出旧编辑面板**（无菜单）；
 *   - 「快速记录」页：点击 → 菜单（写闪念 / 建任务 / 排日程）→「写闪念」打开新编辑卡片。
 *
 * 需求（已与用户确认，见 docs/evidence/20260917/prototypes/quicknote-entry.html）：
 *   1. 所有页面「+」行为统一为与「快速记录」页一致：点击 → 菜单；
 *      写闪念 → 新版编辑卡片（可拖动、全量功能）；建任务 / 排日程 → 跳转对应页面；
 *   2. 旧样式编辑面板不再出现；
 *   3. 每个页面仍只有一个入口（/quick-notes 页不渲染全局入口）；
 *   4. 不造成功能缺项：草稿保留、位置记忆继续生效。
 *
 * 用真实渲染 + 真实点击验证交互，而不是只看源码字符串 —— 源码断言挡不住
 * 「按钮点下去弹的是旧面板」这类行为回归。
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, Fragment, type ReactNode } from 'react';

import { QuickNoteFloatingEntry } from '../QuickNoteFloatingEntry';
import {
  clearQuickNoteDraft,
  loadQuickNoteDraft,
  saveQuickNoteDraft,
} from '../quickNoteFloatingState';

// 编辑卡片包含 Markdown 编辑器（体积大、懒加载），渲染它需要 API 客户端可用。
const createQuickNoteMock = vi.fn().mockResolvedValue({ id: 'note-1' });
vi.mock('../../../api/quickNotes', () => ({
  createQuickNote: (...args: unknown[]) => createQuickNoteMock(...args),
  getQuickNote: vi.fn().mockResolvedValue(null),
  updateQuickNote: vi.fn().mockResolvedValue({ id: 'note-1' }),
  archiveQuickNote: vi.fn().mockResolvedValue(undefined),
  restoreQuickNote: vi.fn().mockResolvedValue(undefined),
  deleteQuickNote: vi.fn().mockResolvedValue(undefined),
  processQuickNote: vi.fn().mockResolvedValue(undefined),
  uploadQuickNoteAttachment: vi.fn().mockResolvedValue({ id: 'att-1' }),
  getQuickNotes: vi.fn().mockResolvedValue({ items: [], total: 0 }),
}));

afterEach(() => {
  cleanup();
  createQuickNoteMock.mockClear();
});

/** 用 MemoryRouter 提供真实的路由上下文（组件内的菜单用 useNavigate 跳转）。 */
/** 编辑卡片内部使用 react-query，必须提供 QueryClientProvider。 */
function withProviders(node: ReactNode, initialPath: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return createElement(
    QueryClientProvider,
    { client: queryClient },
    createElement(MemoryRouter, { initialEntries: [initialPath] }, node),
  );
}

function renderEntry(pathname: string) {
  return render(withProviders(createElement(QuickNoteFloatingEntry, { pathname }), pathname));
}

/** 打开菜单并等待菜单项渲染出来。 */
async function openMenu() {
  fireEvent.click(screen.getByLabelText('打开快速记录'));
  await waitFor(() => expect(screen.getByText('写闪念')).toBeTruthy());
}

describe('#300 全局快速记录入口与「快速记录」页行为一致', () => {
  it('点击「+」先弹出菜单（写闪念 / 建任务 / 排日程），而不是直接打开编辑面板', async () => {
    renderEntry('/today');

    expect(screen.queryByText('写闪念')).toBeNull();
    expect(screen.queryByRole('dialog')).toBeNull();

    await openMenu();

    expect(screen.getByText('建任务')).toBeTruthy();
    expect(screen.getByText('排日程')).toBeTruthy();
    // #300 的旧行为：点「+」直接弹编辑面板。现在必须只弹菜单。
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  // 编辑卡片懒加载 + MDX 编辑器挂载较重，给足超时（首次 transform 可达数秒）。
  it('菜单「写闪念」打开新版编辑卡片（含分类 / 附件 / Markdown 全量功能）', async () => {
    renderEntry('/today');
    await openMenu();

    fireEvent.click(screen.getByText('写闪念'));

    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy(), { timeout: 20000 });

    const dialogText = screen.getByRole('dialog').textContent ?? '';
    for (const marker of ['记录分类：', '附件', '支持 Markdown 语法']) {
      expect(dialogText).toContain(marker);
    }
  });

  it('旧样式编辑面板（QuickNoteGlobalPanel）不再出现', async () => {
    renderEntry('/today');
    await openMenu();

    // 旧面板的 aria-label 是「快速记录」，且没有分类 / 附件等新版功能。
    expect(screen.queryByLabelText('快速记录')).toBeNull();
  });

  it('菜单「建任务」跳转任务页，「排日程」跳转日历页', async () => {
    function Probe() {
      return createElement('div', { 'data-testid': 'probe' }, useLocation().pathname);
    }

    for (const [menuItem, expectedPath] of [
      ['建任务', '/tasks'],
      ['排日程', '/calendar'],
    ] as const) {
      cleanup();
      render(
        withProviders(
          createElement(
            Fragment,
            null,
            createElement(QuickNoteFloatingEntry, { pathname: '/today' }),
            createElement(Probe, null),
          ),
          '/today',
        ),
      );

      await openMenu();
      fireEvent.click(screen.getByText(menuItem));
      await waitFor(() => expect(screen.getByTestId('probe').textContent).toBe(expectedPath));
    }
  });

  it('每页仍只有一个入口：/quick-notes 页不渲染全局入口', () => {
    const onQuickNotes = renderEntry('/quick-notes');
    expect(
      onQuickNotes.container.querySelectorAll('[aria-label="打开快速记录"]').length,
    ).toBe(0);

    cleanup();
    const onToday = renderEntry('/today');
    expect(onToday.container.querySelectorAll('[aria-label="打开快速记录"]').length).toBe(1);
  });

  it('迁移不造成功能缺项：位置记忆仍在，且全局入口复用新编辑卡片', () => {
    // 相对本测试文件定位，避免依赖运行时的 cwd（tsx / vitest 的 cwd 不同）。
    const dir = path.dirname(fileURLToPath(import.meta.url));
    const read = (file: string) => readFileSync(path.join(dir, '..', file), 'utf8');

    const dialog = read('QuickNoteDialog.tsx');
    expect(dialog).toContain('loadPanelPosition');
    expect(dialog).toContain('savePanelPosition');

    // 全局入口复用「快速记录」页同款编辑卡片（而不是旧面板）。
    expect(read('QuickNoteFloatingEntry.tsx')).toContain('QuickNoteDialog');
  });

  // #300 需求 4「不造成功能缺项」：草稿必须真正保留。
  // 仅断言常量存在是不够的（review 发现旧面板删除后草稿能力实际丢失，
  // 而字符串断言仍通过 —— 假阳性）。这里验证真实恢复路径。
  it('重新打开卡片时恢复上次未保存的草稿（草稿能力真实生效）', async () => {
    const draft = '睡前想到的一个点子';
    localStorage.clear();
    localStorage.setItem('pim.quickNotes.floatingDraft', draft);

    renderEntry('/today');
    await openMenu();
    fireEvent.click(screen.getByText('写闪念'));
    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy(), { timeout: 20000 });

    // 草稿内容必须出现在编辑卡片里（旧 QuickNoteGlobalPanel 有该行为，统一入口后不得丢失）。
    await waitFor(() => {
      expect(screen.getByRole('dialog').textContent ?? '').toContain(draft);
    }, { timeout: 20000 });

    localStorage.clear();
  });

  it('草稿读写工具按约定工作：空内容清除草稿，提交后草稿不再恢复', () => {
    localStorage.clear();
    saveQuickNoteDraft('abc');
    expect(loadQuickNoteDraft()).toBe('abc');
    // 空内容 -> 清除键（避免用空草稿覆盖已有内容）
    saveQuickNoteDraft('');
    expect(loadQuickNoteDraft()).toBe('');
    saveQuickNoteDraft('def');
    clearQuickNoteDraft();
    expect(loadQuickNoteDraft()).toBe('');
    expect(localStorage.getItem('pim.quickNotes.floatingDraft')).toBeNull();
  });

  it('进入 /quick-notes 时收起全局菜单，离开后不会残留为打开状态', async () => {
    localStorage.clear();
    const { rerender } = render(
      withProviders(createElement(QuickNoteFloatingEntry, { pathname: '/today' }), '/today'),
    );
    await openMenu();

    // 切到排除路径：组件返回 null，但状态必须被重置（review 发现）。
    rerender(withProviders(createElement(QuickNoteFloatingEntry, { pathname: '/quick-notes' }), '/quick-notes'));
    expect(screen.queryByText('写闪念')).toBeNull();

    // 再切回来：菜单不应「自己弹开」。
    rerender(withProviders(createElement(QuickNoteFloatingEntry, { pathname: '/today' }), '/today'));
    expect(screen.queryByText('写闪念')).toBeNull();
  });

  // review 发现：全局入口补齐了菜单语义 / Escape，但「快速记录」页内的菜单没有，
  // 两个「统一」入口仍不一致。这里断言两侧具备同一套交互契约。
  it('两个入口的菜单语义与关闭方式一致（role / aria / Escape）', () => {
    // 本文件位于 src/client-web/src/components/quick-notes/__tests__/
    // → 上溯三级到 src/，再取 pages/ 与 components/
    const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
    const read = (file: string) => readFileSync(path.join(dir, file), 'utf8');
    const page = read('pages/QuickNotesPage.tsx');
    const entry = read('components/quick-notes/QuickNoteFloatingEntry.tsx');

    for (const [name, src] of [['快速记录页', page], ['全局入口', entry]] as const) {
      expect(src, `${name}菜单容器应有 role="menu"`).toContain('role="menu"');
      expect(src, `${name}菜单项应有 role="menuitem"`).toContain('role="menuitem"');
      expect(src, `${name}触发按钮应声明 aria-haspopup`).toContain('aria-haspopup="menu"');
      expect(src, `${name}触发按钮应暴露 aria-expanded`).toContain('aria-expanded');
      expect(src, `${name}应支持 Escape 关闭菜单`).toMatch(/key === 'Escape'/);
      expect(src, `${name}应有可访问名称`).toContain('aria-label="打开快速记录"');
    }

    // 两侧菜单项集合与顺序一致
    for (const label of ['写闪念', '建任务', '排日程']) {
      expect(page).toContain(label);
      expect(entry).toContain(label);
    }
  });

  // #300：全局悬浮入口创建时必须沿用旧的 web-floating 来源标识，
  // 否则「悬浮入口创建」与「快速记录页创建」在数据里无法区分（review 发现）。
  // 说明：MDX 编辑器不接受合成 input 事件，因此这里直接校验组件树传入卡片的
  // source 属性（渲染断言），而不是伪造一次保存。
  it('全局入口传给编辑卡片的来源是 web-floating，页面内仍是 web-page', () => {
    const dir = path.dirname(fileURLToPath(import.meta.url));
    const entry = readFileSync(path.join(dir, '..', 'QuickNoteFloatingEntry.tsx'), 'utf8');

    // 入口必须显式传入 web-floating（否则会退回卡片的 web-page 默认值）
    expect(entry).toMatch(/<LazyQuickNoteDialog[\s\S]*?source="web-floating"/);

    // 卡片默认值必须是 web-page（快速记录页沿用），并把 source 传给创建接口
    const dialog = readFileSync(path.join(dir, '..', 'QuickNoteDialog.tsx'), 'utf8');
    expect(dialog).toMatch(/source = 'web-page'/);
    expect(dialog).toMatch(/source,\s*\n\s*attachmentIds/);
  });

  // review 发现的时序风险：恢复与持久化在同一提交内依次执行，持久化 effect 会先拿到
  // 上一轮的旧 content（空串）并调用 saveQuickNoteDraft('')，把已有草稿短暂删除。
  // 这里**监听写入调用本身**（而不是事后读 localStorage —— 第二轮 effect 会把草稿写回，
  // 事后读取无法发现瞬时删除，属假阳性）。
  it('打开已有草稿时从不写出空草稿（监听实际写入调用）', async () => {
    const draft = '不应被清空的草稿';
    localStorage.clear();
    localStorage.setItem('pim.quickNotes.floatingDraft', draft);

    // 记录本用例期间对草稿键的每一次写入 / 删除
    const writes: Array<{ op: string; value: string | null }> = [];
    const originalSet = Storage.prototype.setItem;
    const originalRemove = Storage.prototype.removeItem;
    Storage.prototype.setItem = function patchedSet(key: string, value: string) {
      if (key === 'pim.quickNotes.floatingDraft') writes.push({ op: 'set', value });
      return originalSet.call(this, key, value);
    };
    Storage.prototype.removeItem = function patchedRemove(key: string) {
      if (key === 'pim.quickNotes.floatingDraft') writes.push({ op: 'remove', value: null });
      return originalRemove.call(this, key);
    };

    try {
      renderEntry('/today');
      await openMenu();
      fireEvent.click(screen.getByText('写闪念'));
      await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy(), { timeout: 20000 });
      await waitFor(() => {
        expect(screen.getByRole('dialog').textContent ?? '').toContain(draft);
      }, { timeout: 20000 });
    } finally {
      Storage.prototype.setItem = originalSet;
      Storage.prototype.removeItem = originalRemove;
    }

    // 关键断言：整个打开过程中从未把草稿写成空值或删除
    const destructive = writes.filter(w => w.op === 'remove' || w.value === '' || w.value === null);
    expect(destructive).toEqual([]);
    // 且草稿最终仍是原值
    expect(loadQuickNoteDraft()).toBe(draft);
    localStorage.clear();
  });

  // #300：全局悬浮入口创建时必须沿用旧的 web-floating 来源标识，
  // 否则「悬浮入口创建」与「快速记录页创建」在数据里无法区分（review 发现）。
  // 说明：MDX 编辑器不接受合成 input 事件，因此这里直接校验组件树传入卡片的
  // source 属性（渲染断言），而不是伪造一次保存。
  it('全局入口传给编辑卡片的来源是 web-floating，页面内仍是 web-page', () => {
    const dir = path.dirname(fileURLToPath(import.meta.url));
    const entry = readFileSync(path.join(dir, '..', 'QuickNoteFloatingEntry.tsx'), 'utf8');

    // 入口必须显式传入 web-floating（否则会退回卡片的 web-page 默认值）
    expect(entry).toMatch(/<LazyQuickNoteDialog[\s\S]*?source="web-floating"/);

    // 卡片默认值必须是 web-page（快速记录页沿用），并把 source 传给创建接口
    const dialog = readFileSync(path.join(dir, '..', 'QuickNoteDialog.tsx'), 'utf8');
    expect(dialog).toMatch(/source = 'web-page'/);
    expect(dialog).toMatch(/source,\s*\n\s*attachmentIds/);
  });

  // review 发现的时序风险：恢复与持久化在同一提交内依次执行，
  // 若持久化先跑会拿到 stale content 并写出空草稿。断言「打开已有草稿时
  // 草稿键不会被短暂清空」——即打开后立即读取仍是原草稿。
  it('菜单支持 Escape 关闭，并带有正确的菜单语义', async () => {
    renderEntry('/today');
    await openMenu();

    // 语义：容器是 menu，各项是 menuitem。
    expect(screen.getByRole('menu')).toBeTruthy();
    expect(screen.getAllByRole('menuitem').length).toBe(3);

    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByText('写闪念')).toBeNull());
  });
}, 30000);
