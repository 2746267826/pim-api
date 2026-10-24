import { AlertCircle, ArrowDown, ArrowUp, ChevronLeft, ChevronRight, Folder, LayoutGrid, List, Loader2, RefreshCw, Search } from 'lucide-react';
import type { FileItem, FileSearchScope, FileSortKey, FileSortOrder } from '../../types';
import ImageGrid from './ImageGrid';
import { breadcrumbSegments, displayParentPath, pageCountLabel, parentDirPath } from './fileBrowserState';

export interface OneDriveFileListProps {
  items: FileItem[];
  /** 加载中（AC-10.1/10.2：必须有可见反馈，不得白屏）。 */
  loading?: boolean;
  /** 可读的失败原因（AC-10.3：不得出现「未知错误」，必须能重试）。 */
  error?: string | null;
  onRetry?: () => void;

  query: string;
  onQueryChange: (value: string) => void;
  searchScope: FileSearchScope;
  onSearchScopeChange: (scope: FileSearchScope) => void;
  /** 全局搜索结果需要展示完整路径（AC-8.1）。 */
  showFullPath?: boolean;
  /**
   * 全局搜索命中某条时：跳到它所在目录，并把该条目作为选中项（以便右栏预览）。
   * 第二个参数是被点击的条目本身，使调用方能在切换目录后仍然保留选择。
   */
  onRevealInFolder?: (path: string, item: FileItem) => void;

  /** 当前目录路径（面包屑/上一级/当前文件夹搜索的范围）。 */
  currentPath: string;
  /** 面包屑或「上一级」导航。 */
  onNavigate: (path: string) => void;

  sort: FileSortKey;
  order: FileSortOrder;
  onSortChange: (sort: FileSortKey, order: FileSortOrder) => void;

  page: number;
  totalPages: number;
  totalCount: number;
  onPageChange: (page: number) => void;

  view: 'list' | 'grid';
  onViewChange: (view: 'list' | 'grid') => void;

  selectedItem: FileItem | null;
  onSelect: (item: FileItem) => void;
  onOpenFolder: (path: string) => void;

  // ---- PR-2（REQ-18 / REQ-19）----
  /** 行首勾选框的多选集合。 */
  selectedIds?: ReadonlySet<string>;
  onToggleSelected?: (id: string) => void;
  onToggleAll?: (ids: string[]) => void;
  onClearSelection?: () => void;
  /** 批量操作入口（下载 / 移动 / 删除）。 */
  onBatch?: (action: 'download' | 'move' | 'delete') => void;
  /** 行内「⋯」菜单渲染（由页面注入，避免列表耦合操作实现）。 */
  rowMenu?: (item: FileItem) => React.ReactNode;
}

const SORT_LABELS: Record<FileSortKey, string> = {
  name: '名称',
  modified: '修改时间',
  size: '大小',
};

function formatSize(size: number | null): string {
  if (size === null || size === undefined) return '—';
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1024 * 1024 * 1024) return `${(size / 1024 / 1024).toFixed(1)} MB`;
  return `${(size / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function formatTime(value: string): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
}

export function typeBadge(item: FileItem): { label: string; className: string } {
  const mime = item.mimeType ?? '';
  const name = item.name.toLowerCase();
  if (item.itemType === 'folder') return { label: 'DIR', className: 'bg-[var(--pim-surface-muted)] text-[var(--pim-text-muted)]' };
  if (mime.includes('pdf') || name.endsWith('.pdf')) return { label: 'PDF', className: 'bg-red-50 text-red-600' };
  if (mime.includes('word') || name.endsWith('.docx') || name.endsWith('.doc')) return { label: 'DOC', className: 'bg-blue-50 text-blue-600' };
  if (mime.includes('sheet') || name.endsWith('.xlsx') || name.endsWith('.xls')) return { label: 'XLS', className: 'bg-green-50 text-green-600' };
  if (mime.includes('presentation') || name.endsWith('.pptx') || name.endsWith('.ppt')) return { label: 'PPT', className: 'bg-orange-50 text-orange-600' };
  if (mime.startsWith('image/')) return { label: 'IMG', className: 'bg-purple-50 text-purple-600' };
  return { label: 'TXT', className: 'bg-zinc-100 text-zinc-600' };
}

/**
 * 中栏：工具条（搜索/排序/视图）+ 面包屑与「上一级」+ 列表/网格 + 分页。
 *
 * 列表是主导航（决策 D-27）：列表负责进入目录、选中预览；树只负责目录层级。
 * 计数、翻页、搜索与排序全部由服务端驱动（REQ-3/REQ-8/REQ-9），
 * 因此这里展示的「共 N 项」是真实总数，不会像旧实现那样把「已加载 2000 项」当成全部（现状-2）。
 */
export default function OneDriveFileList({
  items,
  loading = false,
  error = null,
  onRetry,
  query,
  onQueryChange,
  searchScope,
  onSearchScopeChange,
  showFullPath = false,
  onRevealInFolder,
  currentPath,
  onNavigate,
  sort,
  order,
  onSortChange,
  page,
  totalPages,
  totalCount,
  onPageChange,
  view,
  onViewChange,
  selectedItem,
  onSelect,
  onOpenFolder,
  selectedIds,
  onToggleSelected,
  onToggleAll,
  onClearSelection,
  onBatch,
  rowMenu,
}: OneDriveFileListProps) {
  const keyword = query.trim();
  const parent = parentDirPath(currentPath);
  const segments = breadcrumbSegments(currentPath);
  const canPage = totalPages > 1;
  const selectionCount = selectedIds?.size ?? 0;

  const toggleOrder = () => onSortChange(sort, order === 'asc' ? 'desc' : 'asc');

  /**
   * 跳到条目所在目录。优先用 onRevealInFolder（上层会清掉搜索关键词、退出搜索态，
   * 从而真正展示该目录的内容）；没有该回调时退回纯导航。
   */
  const revealInFolder = (path: string, item: FileItem) => {
    if (onRevealInFolder) onRevealInFolder(path, item);
    else onNavigate(path);
  };

  return (
    <section className="flex min-w-0 flex-1 flex-col" aria-label="文件列表">
      {/* 导航行：面包屑（逐级可点）+ 上一级 */}
      <div className="flex flex-wrap items-center gap-1 border-b border-[var(--pim-border)] px-3 py-2 text-xs">
        <button
          type="button"
          className="pim-button-secondary mr-1 inline-flex items-center gap-1 px-2 py-1 text-xs disabled:opacity-40"
          aria-label="返回上一级"
          disabled={parent === null}
          onClick={() => parent !== null && onNavigate(parent)}
        >
          <ChevronLeft size={13} /> 上一级
        </button>
        <nav className="flex min-w-0 flex-wrap items-center gap-1 text-[var(--pim-text-muted)]" aria-label="目录路径">
          {segments.map((segment, index) => (
            <span key={segment.path} className="flex items-center gap-1">
              {index > 0 && <span aria-hidden="true">/</span>}
              <button
                type="button"
                className={`truncate rounded px-1 py-0.5 hover:bg-[var(--pim-surface-muted)] ${
                  index === segments.length - 1 ? 'text-[var(--pim-text)]' : 'text-[var(--pim-primary)]'
                }`}
                onClick={() => onNavigate(segment.path)}
              >
                {segment.name}
              </button>
            </span>
          ))}
        </nav>
      </div>

      {/* 工具条：搜索 + 排序 + 视图 */}
      <div className="flex flex-wrap items-center gap-2 border-b border-[var(--pim-border)] px-3 py-2">
        <div className="flex items-center gap-2 rounded-full border border-[var(--pim-border)] px-3 py-1.5 text-sm text-[var(--pim-text-muted)]">
          <Search size={14} />
          <input
            className="w-40 bg-transparent outline-none placeholder:text-[var(--pim-text-muted)] sm:w-52"
            placeholder={searchScope === 'global' ? '全盘搜索文件名…' : '搜索当前文件夹…'}
            aria-label="搜索文件名"
            value={query}
            onChange={e => onQueryChange(e.target.value)}
          />
        </div>

        <div className="flex items-center gap-0.5 rounded-lg border border-[var(--pim-border)] p-0.5 text-xs">
          {(['folder', 'global'] as const).map(scope => (
            <button
              key={scope}
              type="button"
              className={`rounded-md px-2 py-1 ${searchScope === scope ? 'bg-[var(--pim-primary-soft)] text-[var(--pim-primary)]' : ''}`}
              aria-pressed={searchScope === scope}
              onClick={() => onSearchScopeChange(scope)}
            >
              {scope === 'folder' ? '当前文件夹' : '全盘'}
            </button>
          ))}
        </div>

        <label className="flex items-center gap-1 text-xs text-[var(--pim-text-muted)]">
          <span className="sr-only">排序字段</span>
          <select
            aria-label="排序字段"
            className="rounded-lg border border-[var(--pim-border)] bg-white px-2 py-1 text-xs"
            value={sort}
            onChange={e => onSortChange(e.target.value as FileSortKey, order)}
          >
            {(Object.keys(SORT_LABELS) as FileSortKey[]).map(key => (
              <option key={key} value={key}>
                {SORT_LABELS[key]}
              </option>
            ))}
          </select>
        </label>
        <button
          type="button"
          aria-label="切换排序方向"
          title={order === 'asc' ? '升序' : '降序'}
          className="rounded-lg border border-[var(--pim-border)] p-1.5 text-[var(--pim-text-muted)]"
          onClick={toggleOrder}
        >
          {order === 'asc' ? <ArrowUp size={13} /> : <ArrowDown size={13} />}
        </button>

        <span
          className="rounded-full border border-[var(--pim-border)] px-2.5 py-1 text-xs text-[var(--pim-text-muted)]"
          data-testid="file-page-count"
        >
          {pageCountLabel(page, totalPages, totalCount)}
        </span>

        <div className="ml-auto flex items-center gap-1 rounded-lg border border-[var(--pim-border)] p-0.5">
          <button
            type="button"
            aria-label="列表视图"
            className={`rounded-md px-2 py-1 ${view === 'list' ? 'bg-[var(--pim-surface-muted)]' : ''}`}
            onClick={() => onViewChange('list')}
          >
            <List size={14} />
          </button>
          <button
            type="button"
            aria-label="网格视图"
            className={`rounded-md px-2 py-1 ${view === 'grid' ? 'bg-[var(--pim-surface-muted)]' : ''}`}
            onClick={() => onViewChange('grid')}
          >
            <LayoutGrid size={14} />
          </button>
        </div>
      </div>

      {/* 批量操作条（REQ-18）：勾选后出现，操作后由页面清零选择态（AC-18.2） */}
      {selectionCount > 0 && (
        <div
          className="flex flex-wrap items-center gap-2 border-b border-[var(--pim-border)] bg-[var(--pim-primary-soft)] px-3 py-1.5 text-xs"
          data-testid="batch-bar"
        >
          <span data-testid="batch-count">已选 {selectionCount} 项</span>
          <button type="button" className="pim-button-secondary px-2 py-1 text-xs" data-testid="batch-download" onClick={() => onBatch?.('download')}>
            批量下载
          </button>
          <button type="button" className="pim-button-secondary px-2 py-1 text-xs" data-testid="batch-move" onClick={() => onBatch?.('move')}>
            批量移动
          </button>
          <button type="button" className="pim-button-secondary px-2 py-1 text-xs" data-testid="batch-delete" onClick={() => onBatch?.('delete')}>
            批量删除
          </button>
          <button type="button" className="ml-auto text-[11px] underline" data-testid="batch-clear" onClick={() => onClearSelection?.()}>
            清空选择
          </button>
        </div>
      )}

      {/* 失败：就地错误条 + 重试（AC-10.3） */}
      {error && (
        <div
          className="flex items-center gap-2 border-b border-[var(--pim-border)] bg-[var(--pim-danger-soft)] px-3 py-2 text-xs text-[var(--pim-danger)]"
          role="alert"
          data-testid="file-list-error"
        >
          <AlertCircle size={14} />
          <span className="min-w-0 flex-1">{error}</span>
          <button
            type="button"
            className="pim-button-secondary inline-flex items-center gap-1 px-2 py-1 text-xs"
            onClick={() => onRetry?.()}
          >
            <RefreshCw size={12} /> 重试
          </button>
        </div>
      )}

      {loading && (
        <div
          className="flex items-center gap-2 border-b border-[var(--pim-border)] bg-[var(--pim-surface-muted)] px-3 py-2 text-xs text-[var(--pim-text-muted)]"
          data-testid="file-list-loading"
        >
          <Loader2 size={13} className="animate-spin" /> 加载中…
        </div>
      )}

      {view === 'list' ? (
        <div className="min-h-0 flex-1 overflow-auto px-2 pb-4">
          <table className="w-full border-collapse">
            <thead>
              <tr className="text-left text-xs text-[var(--pim-text-muted)]">
                <th className="w-8 px-2 py-2">
                  <input
                    type="checkbox"
                    aria-label="全选"
                    data-testid="select-all"
                    checked={selectionCount > 0 && selectionCount === items.length}
                    onChange={() => onToggleAll?.(items.map(item => item.id))}
                  />
                </th>
                <th className="px-3 py-2 font-medium">名称</th>
                {showFullPath && <th className="px-3 py-2 font-medium">所在目录</th>}
                <th className="px-3 py-2 font-medium">大小</th>
                <th className="px-3 py-2 font-medium">修改时间</th>
              </tr>
            </thead>
            <tbody>
              {items.map(item => {
                const badge = typeBadge(item);
                return (
                  <tr
                    key={item.id}
                    data-testid="file-row"
                    className={`cursor-pointer border-b border-[var(--pim-border-soft)] ${
                      selectedItem?.id === item.id ? 'bg-[var(--pim-primary-soft)]' : 'hover:bg-[var(--pim-surface-muted)]'
                    }`}
                    onClick={() => {
                      // 全局搜索结果：点整行即**跳到它所在目录**（AC-8.1）。
                      // 这里必须走 onRevealInFolder（会退出搜索态并重新加载该目录），
                      // 只调 onNavigate 会留下搜索态、用户看不到目标目录的内容。
                      if (showFullPath) revealInFolder(displayParentPath(item.path), item);
                      else if (item.itemType === 'folder') onOpenFolder(item.path);
                      else onSelect(item);
                    }}
                  >
                    <td className="px-2 py-2" onClick={event => event.stopPropagation()}>
                      <input
                        type="checkbox"
                        aria-label={`选择 ${item.name}`}
                        data-testid={`select-${item.name}`}
                        checked={selectedIds?.has(item.id) ?? false}
                        onChange={() => onToggleSelected?.(item.id)}
                      />
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-2.5">
                        <span className={`flex h-7 w-7 items-center justify-center rounded-lg text-[10px] font-bold ${badge.className}`}>
                          {badge.label}
                        </span>
                        <span className="flex items-center gap-1.5 truncate">
                          {item.itemType === 'folder' && <Folder size={14} className="shrink-0 text-[var(--pim-text-muted)]" />}
                          {item.name}
                        </span>
                      </div>
                    </td>
                    {showFullPath && (
                      <td className="px-3 py-2 text-xs text-[var(--pim-text-muted)]">
                        <button
                          type="button"
                          className="truncate text-left text-[var(--pim-primary)] hover:underline"
                          title={displayParentPath(item.path)}
                          onClick={event => {
                            event.stopPropagation();
                            revealInFolder(displayParentPath(item.path), item);
                          }}
                        >
                          {displayParentPath(item.path)}
                        </button>
                      </td>
                    )}
                    <td className="px-3 py-2 text-xs text-[var(--pim-text-muted)]">{formatSize(item.size)}</td>
                    <td className="px-3 py-2 text-xs text-[var(--pim-text-muted)]">{formatTime(item.modifiedAt)}</td>
                    {rowMenu && <td className="w-10 px-2 py-2 text-right">{rowMenu(item)}</td>}
                  </tr>
                );
              })}
              {items.length === 0 && !loading && (
                <tr>
                  <td colSpan={(showFullPath ? 4 : 3) + 2} className="px-3 py-10 text-center text-sm text-[var(--pim-text-muted)]">
                    {keyword && searchScope === 'folder' ? (
                      <span className="inline-flex flex-col items-center gap-2">
                        <span>本文件夹无匹配，试试全盘搜索</span>
                        <button
                          type="button"
                          className="pim-button-secondary px-3 py-1 text-xs"
                          onClick={() => onSearchScopeChange('global')}
                        >
                          去全盘搜索
                        </button>
                      </span>
                    ) : keyword ? (
                      '没有匹配的文件'
                    ) : (
                      '此文件夹为空'
                    )}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      ) : items.length === 0 && !loading ? (
        <div className="flex min-h-0 flex-1 items-center justify-center py-10 text-center text-sm text-[var(--pim-text-muted)]">
          {keyword && searchScope === 'folder' ? (
            <span className="inline-flex flex-col items-center gap-2">
              <span>本文件夹无匹配，试试全盘搜索</span>
              <button
                type="button"
                className="pim-button-secondary px-3 py-1 text-xs"
                onClick={() => onSearchScopeChange('global')}
              >
                去全盘搜索
              </button>
            </span>
          ) : keyword ? (
            '没有匹配的文件'
          ) : (
            '此文件夹为空'
          )}
        </div>
      ) : (
        /* REQ-23：网格视图用 ImageGrid（真缩略图懒加载 + 灯箱左右翻） */
        <ImageGrid
          items={items}
          selectedItem={selectedItem}
          onSelect={onSelect}
        />
      )}

      {canPage && (
        <div className="flex items-center justify-center gap-2 border-t border-[var(--pim-border)] px-3 py-2 text-xs">
          <button
            type="button"
            className="pim-button-secondary inline-flex items-center gap-1 px-2 py-1 disabled:opacity-40"
            aria-label="上一页"
            disabled={page <= 1}
            onClick={() => onPageChange(page - 1)}
          >
            <ChevronLeft size={13} /> 上一页
          </button>
          <span className="text-[var(--pim-text-muted)]" data-testid="file-page-indicator">
            {Math.min(Math.max(1, page), totalPages)} / {totalPages}
          </span>
          <button
            type="button"
            className="pim-button-secondary inline-flex items-center gap-1 px-2 py-1 disabled:opacity-40"
            aria-label="下一页"
            disabled={page >= totalPages}
            onClick={() => onPageChange(page + 1)}
          >
            下一页 <ChevronRight size={13} />
          </button>
        </div>
      )}
    </section>
  );
}
