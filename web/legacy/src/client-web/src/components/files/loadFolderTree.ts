import type { FileItem } from '../../types';
import { getFileItems, type FileListParams } from '../../api/files';
import { FILE_PAGE_SIZE } from './fileBrowserState';

/**
 * 树每个目录最多翻多少页（100/页）。超过时调用方必须显式提示「已加载 X / 共 N 项」，
 * 不做静默截断（AC-3.2）。注意计数是**子项**（含文件与目录）数，提示文案不得写成「个文件夹」。
 */
export const MAX_FOLDER_PAGES = 20;

export interface FolderListing {
  items: FileItem[];
  totalCount: number;
  /** 是否因页数上限而只加载了部分目录（AC-3.2：截断必须显式暴露）。 */
  truncated: boolean;
  /** 该目录下未加载完的子项数（truncated 时为 totalCount - items.length）。 */
  missingCount: number;
}

export type GetFileItems = (params: FileListParams) => Promise<{ result: { items: FileItem[]; totalCount: number; totalPages: number } }>;

/**
 * 取回某目录的子项，供左栏树使用（REQ-4）。
 *
 * 与中栏列表**同源同一个查询**（同一排序键），因此「树中展开某目录」的子项集合
 * 与「列表中该目录」的内容一致（AC-4.1）。树按此集合渲染：目录节点可展开、
 * 文件节点只用于预览；超过页数上限时如实报告截断（AC-3.2）。
 */
export async function loadFolderTree(path: string, getItems: GetFileItems = getFileItems): Promise<FolderListing> {
  const first = await getItems({ path, page: 1, pageSize: FILE_PAGE_SIZE, sort: 'name', order: 'asc' });
  const items = [...first.result.items];
  let page = 1;
  while (page < first.result.totalPages && page < MAX_FOLDER_PAGES) {
    page += 1;
    const next = await getItems({ path, page, pageSize: FILE_PAGE_SIZE, sort: 'name', order: 'asc' });
    items.push(...next.result.items);
  }
  const missingCount = Math.max(0, first.result.totalCount - items.length);
  return {
    items,
    totalCount: first.result.totalCount,
    truncated: missingCount > 0,
    missingCount,
  };
}
