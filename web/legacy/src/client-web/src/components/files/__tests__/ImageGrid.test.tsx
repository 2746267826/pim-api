import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import ImageGrid from '../ImageGrid';
import type { FileItem } from '../../../types';

/**
 * REQ-23：图片网格 + 灯箱（AC-23.2 大图来源为微软、左右翻与关闭返回）。
 * 缩略图的懒加载/占位在 FileThumbnail.test.tsx 单独覆盖。
 */
function item(id: string, name: string, mime: string | null): FileItem {
  return {
    id,
    providerId: 'p-1',
    externalFileId: `ext-${id}`,
    parentExternalFileId: null,
    path: `/图片/${name}`,
    name,
    itemType: 'file',
    mimeType: mime,
    size: 2048,
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

const photoA = item('a', 'a.jpg', 'image/jpeg');
const photoB = item('b', 'b.png', 'image/png');
const doc = item('c', 'c.pdf', 'application/pdf');

describe('ImageGrid / REQ-23 灯箱', () => {
  it('渲染所有条目的网格瓦片', () => {
    render(<ImageGrid items={[photoA, photoB, doc]} onSelect={vi.fn()} selectedItem={null} />);

    expect(screen.getAllByTestId('grid-tile')).toHaveLength(3);
    expect(screen.getByTestId('image-grid')).toBeTruthy();
  });

  it('AC-23.2 点击图片打开灯箱；非图片只选中不打开', () => {
    const onSelect = vi.fn();
    render(<ImageGrid items={[photoA, photoB, doc]} onSelect={onSelect} selectedItem={null} />);

    fireEvent.click(screen.getByText('c.pdf'));
    // 非图片：仅回调选中，不出现灯箱关闭按钮
    expect(onSelect).toHaveBeenCalledWith(doc);
    expect(screen.queryByTestId('lightbox-close')).toBeNull();
  });

  it('AC-23.2 灯箱提供明显的关闭入口', () => {
    render(<ImageGrid items={[photoA, photoB]} onSelect={vi.fn()} selectedItem={null} />);

    fireEvent.click(screen.getByText('a.jpg'));

    expect(screen.getByTestId('lightbox-close')).toBeTruthy();
  });

  it('AC-23.2 关闭按钮可关闭灯箱（返回列表）', () => {
    render(<ImageGrid items={[photoA, photoB]} onSelect={vi.fn()} selectedItem={null} />);
    fireEvent.click(screen.getByText('a.jpg'));

    fireEvent.click(screen.getByTestId('lightbox-close'));

    expect(screen.queryByTestId('lightbox-close')).toBeNull();
  });

  it('反面：未点击时灯箱不渲染（不得预加载大图）', () => {
    render(<ImageGrid items={[photoA]} onSelect={vi.fn()} selectedItem={null} />);

    expect(screen.queryByTestId('lightbox-close')).toBeNull();
  });

  it('选中态高亮当前条目', () => {
    render(<ImageGrid items={[photoA, photoB]} onSelect={vi.fn()} selectedItem={photoB} />);

    const tiles = screen.getAllByTestId('grid-tile');
    const selected = tiles.find(tile => tile.className.includes('pim-primary'));
    expect(selected?.getAttribute('data-file')).toBe('b.png');
  });
});
