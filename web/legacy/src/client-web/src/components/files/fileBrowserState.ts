import type { FileSearchScope, FileSortKey, FileSortOrder } from '../../types';

/**
 * 文件页的导航/视图记忆与路径工具的纯函数集合（REQ-1 / REQ-6 / REQ-9）。
 *
 * 抽成纯模块是为了让「记忆损坏要安全回退」这类反面验收可以直接用单测证明，
 * 而不是靠人肉清一遍 localStorage。
 */

export const FILE_BROWSER_STORAGE_KEY = 'pim.files.browser.v1';

/** 列表分页大小（工单 P3：100/页）。 */
export const FILE_PAGE_SIZE = 100;

export interface FileBrowserMemory {
  /** 上次离开的目录（REQ-1）。 */
  path: string;
  view: 'list' | 'grid';
  sort: FileSortKey;
  order: FileSortOrder;
  searchScope: FileSearchScope;
}

export const DEFAULT_FILE_BROWSER_MEMORY: FileBrowserMemory = {
  path: '/',
  view: 'list',
  sort: 'name',
  order: 'asc',
  searchScope: 'folder',
};

const SORT_KEYS: readonly FileSortKey[] = ['name', 'modified', 'size'];
const SORT_ORDERS: readonly FileSortOrder[] = ['asc', 'desc'];
const SEARCH_SCOPES: readonly FileSearchScope[] = ['folder', 'global'];

/** 规范化目录路径：统一分隔符、补前导斜杠、去掉尾部斜杠；非法输入回退根目录。 */
export function normalizeDirPath(value: unknown): string {
  if (typeof value !== 'string') return '/';
  const trimmed = value.trim().replace(/\\/g, '/');
  if (!trimmed) return '/';
  const withLeading = trimmed.startsWith('/') ? trimmed : `/${trimmed}`;
  const collapsed = withLeading.replace(/\/{2,}/g, '/');
  const withoutTrailing = collapsed.replace(/\/+$/, '');
  return withoutTrailing.length === 0 ? '/' : withoutTrailing;
}

/**
 * 解析持久化的记忆（AC-1.2 反面）。
 * 首次访问（null）、JSON 损坏、字段类型错误、路径非法——一律安全回落到默认值，绝不抛错，
 * 也绝不把用户留在半损坏的状态里。
 */
export function parseFileBrowserMemory(raw: string | null | undefined): FileBrowserMemory {
  if (!raw) return { ...DEFAULT_FILE_BROWSER_MEMORY };

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { ...DEFAULT_FILE_BROWSER_MEMORY };
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { ...DEFAULT_FILE_BROWSER_MEMORY };
  }

  const record = parsed as Record<string, unknown>;
  return {
    path: normalizeDirPath(record.path),
    view: record.view === 'grid' ? 'grid' : 'list',
    sort: SORT_KEYS.includes(record.sort as FileSortKey) ? (record.sort as FileSortKey) : 'name',
    order: SORT_ORDERS.includes(record.order as FileSortOrder) ? (record.order as FileSortOrder) : 'asc',
    searchScope: SEARCH_SCOPES.includes(record.searchScope as FileSearchScope)
      ? (record.searchScope as FileSearchScope)
      : 'folder',
  };
}

export function readFileBrowserMemory(storage?: Pick<Storage, 'getItem'> | null): FileBrowserMemory {
  if (!storage) return { ...DEFAULT_FILE_BROWSER_MEMORY };
  try {
    return parseFileBrowserMemory(storage.getItem(FILE_BROWSER_STORAGE_KEY));
  } catch {
    // 隐私模式等场景下读取会抛错：按「没有记忆」处理（AC-1.2）
    return { ...DEFAULT_FILE_BROWSER_MEMORY };
  }
}

export function writeFileBrowserMemory(
  memory: FileBrowserMemory,
  storage?: Pick<Storage, 'setItem'> | null,
): void {
  if (!storage) return;
  try {
    storage.setItem(FILE_BROWSER_STORAGE_KEY, JSON.stringify(memory));
  } catch {
    // 记忆写不进去不影响使用（隐私模式 / 配额满）
  }
}

export interface BreadcrumbSegment {
  name: string;
  path: string;
}

/** 面包屑分段：根 + 逐级目录（REQ-5/REQ-7：每一段都可点）。 */
export function breadcrumbSegments(path: string): BreadcrumbSegment[] {
  const normalized = normalizeDirPath(path);
  const segments: BreadcrumbSegment[] = [{ name: 'OneDrive', path: '/' }];
  if (normalized === '/') return segments;

  let accumulated = '';
  for (const part of normalized.slice(1).split('/')) {
    if (!part) continue;
    accumulated += `/${part}`;
    segments.push({ name: part, path: accumulated });
  }
  return segments;
}

/** 「← 上一级」的目标；已在根目录时返回 null（按钮置灰）。 */
export function parentDirPath(path: string): string | null {
  const normalized = normalizeDirPath(path);
  if (normalized === '/') return null;
  const index = normalized.lastIndexOf('/');
  return index <= 0 ? '/' : normalized.slice(0, index);
}

/** 翻页控件的可见性：只有一页时不显示页码（避免「第 1/1 页」的噪音）。 */
export function pageCountLabel(page: number, totalPages: number, totalCount: number): string {
  if (totalCount === 0) return '共 0 项';
  const safePage = Math.min(Math.max(1, page), Math.max(1, totalPages));
  return `共 ${totalCount} 项 · 第 ${safePage}/${totalPages} 页`;
}

/**
 * AC-1.2 后半句：「记忆损坏/失效时安全回退根目录」。
 *
 * 「失效」不只是 JSON 坏掉——上次离开的目录可能已被改名或删除。判定方式：
 * 取该路径的**父目录已加载的子项集合**，若父目录已加载完而其中没有这一项，则记忆失效。
 *
 * 关键：父目录尚未加载时一律返回 <c>true</c>（无法判定 ≠ 失效）。
 * 否则刚进入一个深层目录、树的查询还没回来时，会被误判成「目录已不存在」而把用户弹回根目录。
 */
export interface ParentListing {
  items: unknown[];
  /**
   * 该目录的**真实**子项总数。用于区分「目录确实不在」与「目录排在树加载上限之后」：
   * 树每个目录最多加载 {@link MAX_FOLDER_PAGES} 页，超过时集合是子集，
   * 此时不能据此判定记忆路径已失效（复审 Important：会把真实目录误判成已删除、把用户弹回根）。
   */
  totalCount?: number;
}

export function isRestorablePath(path: string, childrenByPath: Record<string, ParentListing | undefined>): boolean {
  const normalized = normalizeDirPath(path);
  if (normalized === '/') return true;

  const parent = parentDirPath(normalized);
  if (parent === null) return true;

  const listing = childrenByPath[parent];
  if (!listing) return true;

  const found = listing.items.some(item => {
    const siblingPath = (item as { path?: unknown })?.path;
    return typeof siblingPath === 'string' && normalizeDirPath(siblingPath) === normalized;
  });
  if (found) return true;

  // 没找到：只有当父目录的子项**确实已全部加载**（items 数 == 真实总数）时，
  // 才能判定该路径已失效；否则它可能只是被截断在加载上限之外。
  const total = listing.totalCount;
  if (typeof total !== 'number' || total > listing.items.length) return true;

  return false;
}

/** 全局搜索结果条目的展示路径（REQ-8：结果必须带完整路径）。 */
export function displayParentPath(itemPath: string): string {
  const normalized = normalizeDirPath(itemPath);
  const index = normalized.lastIndexOf('/');
  return index <= 0 ? '/' : normalized.slice(0, index);
}
