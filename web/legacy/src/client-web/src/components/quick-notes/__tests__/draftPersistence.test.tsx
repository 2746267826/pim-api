/**
 * #300 草稿持久化的时序护栏。
 *
 * 背景：恢复草稿与「持续保存草稿」两个 effect 在同一次提交内依次执行，持久化 effect
 * 会先拿到上一轮的旧 content（通常是空串）并写出 `saveQuickNoteDraft('')`，把已有草稿
 * 短暂删除。修复用一个**一次性**「跳过下次持久化」标记精确跳过那一次陈旧写入。
 *
 * 本文件专门覆盖这个一次性标记的两条关键性质（都能在实现写错时失败）：
 *  1. 打开已有草稿时，全程**从不**发生空写 / 删除（旧守卫写反时会失败）；
 *  2. 标记是「消费后清零」而不是「一直为真」——恢复之后用户继续输入仍必须落库
 *     （若把守卫写成永久禁用持久化，本条会失败）。
 *
 * 这里把 Markdown 编辑器替换成一个可控的 textarea：Lexical 不响应合成输入事件，
 * 用它无法验证「后续输入是否持久化」，而本用例关注的是持久化时序本身。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

import QuickNoteDialog from '../QuickNoteDialog';
import { loadQuickNoteDraft } from '../quickNoteFloatingState';

const DRAFT_KEY = 'pim.quickNotes.floatingDraft';

vi.mock('../../../api/quickNotes', () => ({
  createQuickNote: vi.fn().mockResolvedValue({ id: 'note-1' }),
  getQuickNote: vi.fn().mockResolvedValue(null),
  updateQuickNote: vi.fn().mockResolvedValue({ id: 'note-1' }),
  archiveQuickNote: vi.fn().mockResolvedValue(undefined),
  restoreQuickNote: vi.fn().mockResolvedValue(undefined),
  deleteQuickNote: vi.fn().mockResolvedValue(undefined),
  processQuickNote: vi.fn().mockResolvedValue(undefined),
  uploadQuickNoteAttachment: vi.fn().mockResolvedValue({ id: 'att-1' }),
  getQuickNotes: vi.fn().mockResolvedValue({ items: [], total: 0 }),
}));

// 可控编辑器：暴露一个带 aria-label 的 textarea，直接驱动 onChange。
vi.mock('../QuickNoteEditor', () => ({
  default: ({ value, onChange }: { value: string; onChange?: (v: string) => void }) =>
    createElement('textarea', {
      'aria-label': '草稿编辑器',
      value,
      onChange: (e: { target: { value: string } }) => onChange?.(e.target.value),
    }),
}));

function withProviders(node: ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return createElement(
    QueryClientProvider,
    { client: queryClient },
    createElement(MemoryRouter, { initialEntries: ['/today'] }, node),
  );
}

function renderDialog(props: Partial<React.ComponentProps<typeof QuickNoteDialog>> = {}) {
  return render(
    withProviders(
      createElement(QuickNoteDialog, {
        open: true,
        mode: 'create',
        noteId: null,
        onClose: () => undefined,
        onSaved: () => undefined,
        ...props,
      }),
    ),
  );
}

/** 记录对草稿键的每一次 setItem / removeItem。 */
function spyDraftWrites() {
  const writes: Array<{ op: 'set' | 'remove'; value: string | null }> = [];
  const originalSet = Storage.prototype.setItem;
  const originalRemove = Storage.prototype.removeItem;
  Storage.prototype.setItem = function patchedSet(key: string, value: string) {
    if (key === DRAFT_KEY) writes.push({ op: 'set', value });
    return originalSet.call(this, key, value);
  };
  Storage.prototype.removeItem = function patchedRemove(key: string) {
    if (key === DRAFT_KEY) writes.push({ op: 'remove', value: null });
    return originalRemove.call(this, key);
  };
  return {
    writes,
    restore() {
      Storage.prototype.setItem = originalSet;
      Storage.prototype.removeItem = originalRemove;
    },
  };
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe('#300 草稿持久化时序', () => {
  it('打开已有草稿时从不写出空值或删除草稿', async () => {
    const draft = '不应被清空的草稿';
    localStorage.setItem(DRAFT_KEY, draft);
    const spy = spyDraftWrites();

    try {
      renderDialog();
      // 编辑器里应显示恢复出来的草稿
      await waitFor(() => {
        expect((screen.getByLabelText('草稿编辑器') as HTMLTextAreaElement).value).toBe(draft);
      });
    } finally {
      spy.restore();
    }

    const destructive = spy.writes.filter(w => w.op === 'remove' || !w.value);
    expect(destructive).toEqual([]);
    expect(loadQuickNoteDraft()).toBe(draft);
  });

  it('一次性跳过标记会被消费：恢复之后继续输入仍然写入草稿', async () => {
    const draft = '初始草稿';
    localStorage.setItem(DRAFT_KEY, draft);
    const spy = spyDraftWrites();

    try {
      renderDialog();
      const editor = await waitFor(() => screen.getByLabelText('草稿编辑器') as HTMLTextAreaElement);
      await waitFor(() => expect(editor.value).toBe(draft));

      // 打开之后的第一次真实输入必须落库（若守卫被写成「永久禁用持久化」则不会）
      fireEvent.change(editor, { target: { value: '恢复之后的新输入' } });
      await waitFor(() => expect(loadQuickNoteDraft()).toBe('恢复之后的新输入'));

      // 再输入一次，确认持久化持续生效而不是只生效一次
      fireEvent.change(editor, { target: { value: '第二次输入' } });
      await waitFor(() => expect(loadQuickNoteDraft()).toBe('第二次输入'));
    } finally {
      spy.restore();
    }
  });

  it('显式 initialContent 优先于已有草稿，且同样不产生空写', async () => {
    localStorage.setItem(DRAFT_KEY, '旧草稿');
    const spy = spyDraftWrites();

    try {
      renderDialog({ initialContent: '来自分享的内容' });
      await waitFor(() => {
        expect((screen.getByLabelText('草稿编辑器') as HTMLTextAreaElement).value).toBe('来自分享的内容');
      });
    } finally {
      spy.restore();
    }

    const destructive = spy.writes.filter(w => w.op === 'remove' || !w.value);
    expect(destructive).toEqual([]);
    expect(loadQuickNoteDraft()).toBe('来自分享的内容');
  });

  it('编辑模式不触碰草稿', async () => {
    localStorage.setItem(DRAFT_KEY, '应保持不变');
    const spy = spyDraftWrites();

    try {
      renderDialog({ mode: 'edit', noteId: 'note-9' });
      await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());
    } finally {
      spy.restore();
    }

    // 编辑模式的内容属于已存在的记录：既不应改动草稿值，也不应发生任何写入。
    expect(spy.writes).toEqual([]);
    expect(loadQuickNoteDraft()).toBe('应保持不变');
  });
});
