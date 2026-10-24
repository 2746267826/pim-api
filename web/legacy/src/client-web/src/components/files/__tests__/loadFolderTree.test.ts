import { describe, expect, it, vi } from 'vitest';
import type { FileItem } from '../../../types';
import { loadFolderTree } from '../loadFolderTree';

/**
 * REQ-4 / AC-4.1：树的数据源必须与中栏列表**同源同序**，否则「树中展开某目录」
 * 与「列表中该目录」的条目集合会不一致（这正是现状-3 里两套导航打架的成因之一）。
 *
 * 同时锁定：逐页取全、且因页数上限被截断时必须显式暴露（AC-3.2 / AC-4.2），不得静默缺项。
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

function page(items: FileItem[], totalCount: number, totalPages: number) {
  return { result: { items, page: 1, pageSize: 100, totalCount, totalPages } };
}

describe('loadFolderTree', () => {
  it('与列表同源同序地取回子项（含文件），并按 100/页 逐页取全', async () => {
    const first = Array.from({ length: 100 }, (_, i) => folder(`a${i}`, `/x/a${i}`, `a${i}`));
    const second = [folder('z', '/x/z', 'z')];
    const getItems = vi
      .fn()
      .mockResolvedValueOnce(page(first, 101, 2))
      .mockResolvedValueOnce(page(second, 101, 2));

    const listing = await loadFolderTree('/x', getItems);

    // 不带 type 过滤：树拿到的是与列表完全相同的子项集合（目录 + 文件）
    expect(getItems).toHaveBeenNthCalledWith(1, expect.objectContaining({ path: '/x', page: 1, pageSize: 100 }));
    expect(getItems).toHaveBeenNthCalledWith(2, expect.objectContaining({ path: '/x', page: 2, pageSize: 100 }));
    expect(getItems.mock.calls[0][0]).not.toHaveProperty('type');
    expect(listing.items).toHaveLength(101);
    expect(listing.totalCount).toBe(101);
    expect(listing.truncated).toBe(false);
  });

  it('AC-3.2 截断计数是「子项」总数（含文件与目录），不是目录数', async () => {
    // 混合 100 个文件 + 100 个目录：totalCount 是子项数，提示文案必须按「项」而非「文件夹」表述
    const mixed = [
      ...Array.from({ length: 100 }, (_, i) => folder(`d${i}`, `/mix/d${i}`, `d${i}`)),
      ...Array.from({ length: 100 }, (_, i) => ({ ...folder(`f${i}`, `/mix/f${i}.txt`, `f${i}.txt`), itemType: 'file' })),
    ];
    const getItems = vi.fn().mockResolvedValue(page(mixed, 200, 1));

    const listing = await loadFolderTree('/mix', getItems);

    expect(listing.totalCount).toBe(200);
    expect(listing.items).toHaveLength(200);
    expect(listing.truncated).toBe(false);
  });

  it('AC-3.2 超过上限时如实标记截断，不假装已加载全部', async () => {
    // 上限 20 页 = 2000 个目录；造 5000 个 -> 必须 truncated=true 且给出真实总数
    const getItems = vi.fn(async ({ page: pageNumber }: { page?: number }) =>
      page(
        Array.from({ length: 100 }, (_, i) => folder(`f${pageNumber}-${i}`, `/big/f${pageNumber}-${i}`, `f${i}`)),
        5000,
        50,
      ),
    );

    const listing = await loadFolderTree('/big', getItems);

    expect(listing.items).toHaveLength(2000);
    expect(listing.totalCount).toBe(5000);
    expect(listing.truncated).toBe(true);
    expect(getItems).toHaveBeenCalledTimes(20);
  });

  it('AC-4.2 单页目录不再多发请求', async () => {
    const getItems = vi.fn().mockResolvedValueOnce(page([folder('a', '/x/a', 'a')], 1, 1));

    const listing = await loadFolderTree('/x', getItems);

    expect(getItems).toHaveBeenCalledTimes(1);
    expect(listing.truncated).toBe(false);
  });

  it('空目录返回 0 项且不报错（AC-4.2 反面）', async () => {
    const getItems = vi.fn().mockResolvedValueOnce(page([], 0, 0));

    const listing = await loadFolderTree('/empty', getItems);

    expect(listing.items).toEqual([]);
    expect(listing.truncated).toBe(false);
  });
});
