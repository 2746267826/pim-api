import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import OneDriveFileList, { typeBadge } from '../OneDriveFileList';
import type { FileItem } from '../../../types';

/**
 * REQ-3（分页与完整性）、REQ-5（面包屑/上一级）、REQ-8（搜索）、REQ-9（排序）、
 * REQ-10（交互反馈）的中栏列表（工单 WO-FILES-20260923，PR-1）。
 */
function makeItem(overrides: Partial<FileItem>): FileItem {
  return {
    id: 'id-1',
    providerId: 'p-1',
    externalFileId: 'ext-1',
    parentExternalFileId: null,
    path: '/a.txt',
    name: 'a.txt',
    itemType: 'file',
    mimeType: 'text/plain',
    size: 2048,
    etag: null,
    contentHash: null,
    currentVersionId: null,
    permissions: null,
    isDeleted: false,
    deletedAt: null,
    lastSeenAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    modifiedAt: '2026-09-02T00:00:00Z',
    syncedAt: '2026-09-02T00:00:00Z',
    indexStatus: 'not_indexed',
    ai: null,
    ...overrides,
  };
}

const folder = makeItem({ id: 'f-1', path: '/合同', name: '合同', itemType: 'folder', mimeType: null, size: null });
const pdf = makeItem({ id: 'f-2', path: '/合同/租赁.pdf', name: '租赁.pdf', mimeType: 'application/pdf', size: 1024 * 512 });

function renderList(overrides: Partial<React.ComponentProps<typeof OneDriveFileList>> = {}) {
  const props = {
    items: [folder, pdf],
    loading: false,
    error: null,
    onRetry: vi.fn(),
    query: '',
    onQueryChange: vi.fn(),
    searchScope: 'folder' as const,
    onSearchScopeChange: vi.fn(),
    currentPath: '/合同',
    onNavigate: vi.fn(),
    sort: 'name' as const,
    order: 'asc' as const,
    onSortChange: vi.fn(),
    page: 1,
    totalPages: 1,
    totalCount: 2,
    onPageChange: vi.fn(),
    view: 'list' as const,
    onViewChange: vi.fn(),
    selectedItem: null,
    onSelect: vi.fn(),
    onOpenFolder: vi.fn(),
    ...overrides,
  };
  render(<OneDriveFileList {...props} />);
  return props;
}

describe('OneDriveFileList 类型徽章', () => {
  it('按扩展名/MIME 给出徽章', () => {
    expect(typeBadge(folder).label).toBe('DIR');
    expect(typeBadge(pdf).label).toBe('PDF');
  });
});

describe('OneDriveFileList 列表与网格', () => {
  it('列表视图渲染行与大小/时间', () => {
    renderList();
    // 面包屑里也会出现同名目录，这里只断言表格内容
    const table = within(screen.getByRole('table'));
    expect(table.getByText('合同')).toBeTruthy();
    expect(table.getByText('租赁.pdf')).toBeTruthy();
    expect(table.getByText('512.0 KB')).toBeTruthy();
  });

  it('AC-5.1 点击文件夹进入，点击文件只选中预览', () => {
    const rendered = renderList();
    const table = within(screen.getByRole('table'));

    fireEvent.click(table.getByText('合同'));
    expect(rendered.onOpenFolder).toHaveBeenCalledWith('/合同');
    expect(rendered.onSelect).not.toHaveBeenCalled();

    fireEvent.click(table.getByText('租赁.pdf'));
    expect(rendered.onSelect).toHaveBeenCalledWith(pdf);
  });
});

describe('OneDriveFileList 分页（REQ-3）', () => {
  it('AC-3.1 显示真实总数与当前页，并提供翻页', () => {
    const props = renderList({ totalCount: 8465, totalPages: 85, page: 1 });

    expect(screen.getByTestId('file-page-count').textContent).toBe('共 8465 项 · 第 1/85 页');

    fireEvent.click(screen.getByRole('button', { name: '下一页' }));
    expect(props.onPageChange).toHaveBeenCalledWith(2);
  });

  it('AC-3.1 首页「上一页」禁用、末页「下一页」禁用', () => {
    renderList({ totalCount: 8465, totalPages: 85, page: 1 });
    expect(screen.getByRole('button', { name: '上一页' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('button', { name: '下一页' }).hasAttribute('disabled')).toBe(false);
  });

  it('AC-3.2 空目录不显示翻页控件，只显示「共 0 项」', () => {
    renderList({ items: [], totalCount: 0, totalPages: 0, page: 1 });
    expect(screen.getByTestId('file-page-count').textContent).toBe('共 0 项');
    expect(screen.queryByRole('button', { name: '下一页' })).toBeNull();
  });
});

describe('OneDriveFileList 搜索（REQ-8）', () => {
  it('AC-8.2 当前文件夹模式无匹配时引导去全盘搜索', () => {
    const props = renderList({ items: [], totalCount: 0, totalPages: 0, query: '不存在的文件' });

    expect(screen.getByText(/本文件夹无匹配/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /全盘搜索/ }));
    expect(props.onSearchScopeChange).toHaveBeenCalledWith('global');
  });

  it('AC-8.1 全盘搜索模式渲染结果所在目录', () => {
    renderList({
      items: [makeItem({ id: 'g-1', path: '/main/SAVE/a/x.jpg', name: 'x.jpg' })],
      searchScope: 'global',
      query: 'x',
      showFullPath: true,
      currentPath: '/',
    });

    expect(screen.getByText('/main/SAVE/a')).toBeTruthy();
  });

  it('AC-8.1 全盘搜索模式点整行即跳到该条所在目录（并退出搜索态）', () => {
    const onRevealInFolder = vi.fn();
    const props = renderList({
      items: [makeItem({ id: 'g-2', path: '/main/SAVE/a/x.jpg', name: 'x.jpg' })],
      searchScope: 'global',
      query: 'x',
      showFullPath: true,
      currentPath: '/',
      onRevealInFolder,
    });

    fireEvent.click(within(screen.getByRole('table')).getByText('x.jpg'));

    // 必须走 reveal（上层据此清掉关键词、退出搜索态），并带上被点的条目以便预览；
    // 只导航会留下搜索态，且用户刚点的那一条会丢。
    expect(onRevealInFolder).toHaveBeenCalledTimes(1);
    expect(onRevealInFolder.mock.calls[0][0]).toBe('/main/SAVE/a');
    expect(onRevealInFolder.mock.calls[0][1]).toMatchObject({ name: 'x.jpg' });
  });

  it('AC-8.1 没有 reveal 回调时退回纯导航（不静默无反应）', () => {
    const props = renderList({
      items: [makeItem({ id: 'g-3', path: '/main/SAVE/a/y.jpg', name: 'y.jpg' })],
      searchScope: 'global',
      query: 'y',
      showFullPath: true,
      currentPath: '/',
    });

    fireEvent.click(within(screen.getByRole('table')).getByText('y.jpg'));

    expect(props.onNavigate).toHaveBeenCalledWith('/main/SAVE/a');
  });

  it('AC-8.2 网格视图无匹配时同样给出全盘搜索引导', () => {
    const props = renderList({ items: [], totalCount: 0, totalPages: 0, query: '不存在', view: 'grid' });

    expect(screen.getByText(/本文件夹无匹配/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /全盘搜索/ }));
    expect(props.onSearchScopeChange).toHaveBeenCalledWith('global');
  });

  it('搜索框输入回调（不直接过滤已加载条目）', () => {
    const props = renderList();
    fireEvent.change(screen.getByLabelText('搜索文件名'), { target: { value: '租赁' } });
    expect(props.onQueryChange).toHaveBeenCalledWith('租赁');
  });
});

describe('OneDriveFileList 排序（REQ-9）', () => {
  it('AC-9.1 切换排序键与方向会回报给上层（由服务端排序）', () => {
    const props = renderList();

    fireEvent.change(screen.getByLabelText('排序字段'), { target: { value: 'modified' } });
    expect(props.onSortChange).toHaveBeenCalledWith('modified', 'asc');

    fireEvent.click(screen.getByRole('button', { name: '切换排序方向' }));
    expect(props.onSortChange).toHaveBeenCalledWith('name', 'desc');
  });
});

describe('OneDriveFileList 导航（REQ-5）', () => {
  it('AC-5.2 面包屑逐级可点', () => {
    const props = renderList({ currentPath: '/main/SAVE' });

    fireEvent.click(screen.getByRole('button', { name: 'SAVE' }));
    expect(props.onNavigate).toHaveBeenCalledWith('/main/SAVE');

    fireEvent.click(screen.getByRole('button', { name: 'main' }));
    expect(props.onNavigate).toHaveBeenCalledWith('/main');

    fireEvent.click(screen.getByRole('button', { name: 'OneDrive' }));
    expect(props.onNavigate).toHaveBeenCalledWith('/');
  });

  it('AC-5.3 「上一级」在根目录禁用，在子目录回到上级', () => {
    const atRoot = renderList({ currentPath: '/' });
    expect(screen.getByRole('button', { name: /上一级/ }).hasAttribute('disabled')).toBe(true);
    expect(atRoot.onNavigate).not.toHaveBeenCalled();
  });

  it('AC-5.3 子目录点「上一级」回到父目录', () => {
    const props = renderList({ currentPath: '/main/SAVE' });
    fireEvent.click(screen.getByRole('button', { name: /上一级/ }));
    expect(props.onNavigate).toHaveBeenCalledWith('/main');
  });
});

describe('OneDriveFileList 反馈（REQ-10）', () => {
  it('AC-10.3 失败时就地显示可读原因与重试入口', () => {
    const props = renderList({ items: [], error: '文件服务暂时不可用（503）' });

    expect(screen.getByTestId('file-list-error').textContent).toContain('503');
    fireEvent.click(screen.getByRole('button', { name: /重试/ }));
    expect(props.onRetry).toHaveBeenCalled();
  });

  it('AC-10.1 加载中显示加载态（不白屏）', () => {
    renderList({ items: [], loading: true });
    expect(screen.getByTestId('file-list-loading')).toBeTruthy();
  });
});
