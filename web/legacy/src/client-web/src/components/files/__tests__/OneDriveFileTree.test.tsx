import { describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import OneDriveFileTree from '../OneDriveFileTree';
import type { FileItem } from '../../../types';

/**
 * REQ-4 / REQ-5 的左栏文件树（工单 WO-FILES-20260923，PR-1）。
 *
 * 这里锁定的是需求方实点确认后的导航模型（D-13 / D-27）：
 * 单击 = 展开/收起且**不切换中间列表**；双击 = 进入；中间列表导航时树要自动展开到该层并高亮。
 * 需求方实测的三种乱象（点树无反应 / 树闪成展开 / 中间跳回）不允许复现（AC-5.4）。
 */
function folder(id: string, path: string, name: string): FileItem {
  return {
    id,
    providerId: 'p-1',
    externalFileId: `ext-${id}`,
    parentExternalFileId: null,
    path,
    name,
    itemType: 'folder',
    mimeType: null,
    size: null,
    etag: null,
    contentHash: null,
    currentVersionId: null,
    permissions: null,
    isDeleted: false,
    deletedAt: null,
    lastSeenAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    modifiedAt: '2026-09-01T00:00:00Z',
    syncedAt: '2026-09-01T00:00:00Z',
    indexStatus: 'not_indexed',
    ai: null,
  };
}

const main = folder('id-main', '/main', 'main');
const save = folder('id-save', '/main/SAVE', 'SAVE');
const archives = folder('id-archives', '/main/SAVE/archives', 'archives');
const pics = folder('id-pics', '/图片', '图片');
const note = { ...folder('id-note', '/readme.md', 'readme.md'), itemType: 'file', mimeType: 'text/markdown' };

const TREE: Record<string, FileItem[]> = {
  '/': [main, pics, note],
  '/main': [save],
  '/main/SAVE': [archives],
  '/main/SAVE/archives': [],
};

function renderTree(overrides: Partial<React.ComponentProps<typeof OneDriveFileTree>> = {}) {
  const props = {
    foldersByPath: TREE,
    currentPath: '/',
    onToggleFolder: vi.fn(),
    onEnterFolder: vi.fn(),
    ...overrides,
  };
  render(<OneDriveFileTree {...props} />);
  return props;
}

describe('OneDriveFileTree 导航模型', () => {
  it('AC-4.1 树与列表同源：目录与文件都渲染（子项集合与列表一致）', () => {
    renderTree();
    expect(screen.getByText('main')).toBeTruthy();
    expect(screen.getByText('图片')).toBeTruthy();
    expect(screen.getByText('readme.md')).toBeTruthy();
  });

  it('AC-5.1 单击文件只选中预览，既不进入也不切换展开态', () => {
    const onSelectFile = vi.fn();
    const props = renderTree({ onSelectFile });

    act(() => fireEvent.click(screen.getByText('readme.md')));

    expect(onSelectFile).toHaveBeenCalledTimes(1);
    expect(onSelectFile.mock.calls[0][0]).toMatchObject({ path: '/readme.md', itemType: 'file' });
    expect(props.onEnterFolder).not.toHaveBeenCalled();
    expect(props.onToggleFolder).not.toHaveBeenCalled();
  });

  it('AC-5.1 双击文件不进入目录', () => {
    const props = renderTree({ onSelectFile: vi.fn() });

    act(() => fireEvent.doubleClick(screen.getByText('readme.md')));

    expect(props.onEnterFolder).not.toHaveBeenCalled();
  });

  it('AC-5.1 单击目录只展开/收起，不进入（中间列表不变）', () => {
    const props = renderTree();

    act(() => fireEvent.click(screen.getByText('main')));

    expect(props.onToggleFolder).toHaveBeenCalledWith('/main');
    expect(props.onEnterFolder).not.toHaveBeenCalled();
  });

  it('AC-5.1 双击目录才进入', () => {
    const props = renderTree();

    act(() => fireEvent.doubleClick(screen.getByText('main')));

    expect(props.onEnterFolder).toHaveBeenCalledWith('/main');
  });

  it('AC-5.2 当前目录变化时树自动展开到该层并高亮当前位置', () => {
    renderTree({ currentPath: '/main/SAVE/archives' });

    // 祖先目录被自动展开 -> 深层节点可见（不会「点树无反应」或「闪一下才展开」）
    expect(screen.getByText('main')).toBeTruthy();
    expect(screen.getByText('SAVE')).toBeTruthy();
    expect(screen.getByText('archives')).toBeTruthy();

    const currentRow = screen.getByText('archives').closest('[role="treeitem"], [data-tree-row]');
    expect(currentRow?.getAttribute('aria-selected')).toBe('true');
  });

  it('AC-4.2 加载中的目录显示加载态，失败时给出可重试的提示', () => {
    renderTree({
      currentPath: '/main',
      folderStates: { '/main': 'loading', '/main/SAVE': 'error' },
    });

    expect(screen.getByTestId('tree-loading-/main')).toBeTruthy();
    expect(screen.getByTestId('tree-error-/main/SAVE')).toBeTruthy();
  });

  it('AC-4.2 加载失败时提供重试入口', () => {
    const onRetryFolder = vi.fn();
    renderTree({ currentPath: '/main', folderStates: { '/main': 'error' }, onRetryFolder });

    act(() => fireEvent.click(screen.getByRole('button', { name: /重试/ })));

    expect(onRetryFolder).toHaveBeenCalledWith('/main');
  });
});
