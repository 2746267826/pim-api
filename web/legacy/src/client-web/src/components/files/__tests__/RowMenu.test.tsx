import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import RowMenu from '../RowMenu';
import type { FileItem } from '../../types';

/**
 * REQ-19 行内菜单。AC-19.1 菜单项按类型差异；AC-19.2 点击外部关闭、
 * 且不与应用其他点击（行选中/进入）冲突。
 */
function item(itemType: string, name = 'x.txt'): FileItem {
  return {
    id: `id-${name}`,
    providerId: 'p-1',
    externalFileId: 'ext-1',
    parentExternalFileId: null,
    path: `/${name}`,
    name,
    itemType,
    mimeType: 'text/plain',
    size: 100,
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

describe('RowMenu / REQ-19', () => {
  it('默认收起，点触发按钮展开', () => {
    render(<RowMenu item={item('file')} onAction={vi.fn()} />);

    expect(screen.queryByTestId('row-menu-items')).toBeNull();
    fireEvent.click(screen.getByTestId('row-menu-trigger'));
    expect(screen.getByTestId('row-menu-items')).toBeTruthy();
  });

  it('AC-19.1 文件菜单有「下载」，文件夹没有', () => {
    const { unmount } = render(<RowMenu item={item('file')} onAction={vi.fn()} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));
    expect(screen.getByTestId('row-menu-download')).toBeTruthy();
    unmount();

    render(<RowMenu item={item('folder')} onAction={vi.fn()} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));
    expect(screen.queryByTestId('row-menu-download')).toBeNull();
    expect(screen.getByTestId('row-menu-open')).toBeTruthy();
  });

  it('两种类型都有分享 / 重命名 / 移动 / 删除 / 在 OneDrive 打开', () => {
    render(<RowMenu item={item('folder')} onAction={vi.fn()} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));

    for (const action of ['share', 'rename', 'move', 'delete', 'open-in-onedrive']) {
      expect(screen.getByTestId(`row-menu-${action}`)).toBeTruthy();
    }
  });

  it('选择菜单项会回调并关闭菜单', () => {
    const onAction = vi.fn();
    render(<RowMenu item={item('file', 'a.txt')} onAction={onAction} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));

    fireEvent.click(screen.getByTestId('row-menu-delete'));

    expect(onAction).toHaveBeenCalledTimes(1);
    expect(onAction.mock.calls[0][0]).toBe('delete');
    expect(onAction.mock.calls[0][1].name).toBe('a.txt');
    expect(screen.queryByTestId('row-menu-items')).toBeNull();
  });

  it('AC-19.2 点击外部关闭菜单', () => {
    render(
      <div>
        <RowMenu item={item('file')} onAction={vi.fn()} />
        <button type="button" data-testid="outside">外部</button>
      </div>,
    );
    fireEvent.click(screen.getByTestId('row-menu-trigger'));
    expect(screen.getByTestId('row-menu-items')).toBeTruthy();

    fireEvent.pointerDown(screen.getByTestId('outside'));

    expect(screen.queryByTestId('row-menu-items')).toBeNull();
  });

  it('AC-19.2 点菜单内部不会误关（否则点不到菜单项）', () => {
    render(<RowMenu item={item('file')} onAction={vi.fn()} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));

    fireEvent.pointerDown(screen.getByTestId('row-menu-items'));

    expect(screen.getByTestId('row-menu-items')).toBeTruthy();
  });

  it('AC-19.2 触发按钮阻止冒泡，不与行点击（选中/进入）冲突', () => {
    const onRowClick = vi.fn();
    render(
      <div onClick={onRowClick}>
        <RowMenu item={item('file')} onAction={vi.fn()} />
      </div>,
    );

    fireEvent.click(screen.getByTestId('row-menu-trigger'));

    expect(onRowClick).not.toHaveBeenCalled();
  });

  it('AC-19.2 菜单项点击也阻止冒泡', () => {
    const onRowClick = vi.fn();
    render(
      <div onClick={onRowClick}>
        <RowMenu item={item('file')} onAction={vi.fn()} />
      </div>,
    );
    fireEvent.click(screen.getByTestId('row-menu-trigger'));
    onRowClick.mockClear();

    fireEvent.click(screen.getByTestId('row-menu-share'));

    expect(onRowClick).not.toHaveBeenCalled();
  });

  it('ESC 关闭菜单', () => {
    render(<RowMenu item={item('file')} onAction={vi.fn()} />);
    fireEvent.click(screen.getByTestId('row-menu-trigger'));

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.queryByTestId('row-menu-items')).toBeNull();
  });
});
