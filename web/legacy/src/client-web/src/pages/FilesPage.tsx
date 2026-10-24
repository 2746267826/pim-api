import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { FolderPlus, FolderTree, Link2, Loader2, Upload, X } from 'lucide-react';
import { toast } from 'sonner';
import PageHeader from '../ui/PageHeader';
import OneDriveBindDialog from '../components/files/OneDriveBindDialog';
import OneDriveFileList from '../components/files/OneDriveFileList';
import OneDriveFileTree, { type FolderLoadState } from '../components/files/OneDriveFileTree';
import OneDrivePreviewPane from '../components/files/OneDrivePreviewPane';
import { loadFolderTree } from '../components/files/loadFolderTree';
import {
  FILE_PAGE_SIZE,
  breadcrumbSegments,
  isRestorablePath,
  normalizeDirPath,
  readFileBrowserMemory,
  writeFileBrowserMemory,
  type FileBrowserMemory,
} from '../components/files/fileBrowserState';
import {
  createFolder,
  createShare,
  deleteFile,
  disconnectFileProvider,
  getFileItems,
  getFileProviders,
  getAllShares,
  getItemShares,
  getFileOpenLink,
  getDownloadUrl,
  getOneDriveSyncStatus,
  moveFile,
  renameFile,
  revokeShare,
  searchFiles,
  startOneDriveSync,
} from '../api/files';
import SyncBanner from '../components/files/SyncBanner';
import TransferPanel, { dispatchFilesForUpload, extractFiles, hasActiveTransfers, type TransferTask } from '../components/files/upload/TransferPanel';
import RowMenu from '../components/files/RowMenu';
import { DeleteDialog, MoveDialog, MySharesDialog, NameDialog, ShareDialog } from '../components/files/FileOperationDialogs';
import { summarizeBatchResults, describeBatchOutcome, resolveSelectedItems, formatBytes, requiresDownloadConfirmation } from '../components/files/fileActions';
import type { RowAction } from '../components/files/fileActions';
import type { FileShare, OneDriveSyncStatus } from '../types';
import type { FileItem, FileSearchScope, FileSortKey, FileSortOrder } from '../types';

const EMPTY_ITEMS: FileItem[] = [];

/** 树数据保留窗口：超出后淘汰最早展开的目录（见 expandedPaths 注释）。 */
const MAX_REMEMBERED_EXPANDED = 64;

/**
 * 文件页（REQ-1 ~ REQ-10）：左栏目录树 ｜ 中栏列表/网格 + 搜索 + 分页 ｜ 右栏预览。
 *
 * 与上一版的区别（工单 §1）：
 * - 列表数据由服务端分页提供，不再「逐页拉到 2000 条就停且不提示」（现状-2）；
 * - 树与列表共用同一条导航状态：单击树只展开/收起，双击/列表点击才切换目录（现状-3）；
 * - 打开时回到上次离开的目录（REQ-1）；三栏撑满整屏（REQ-6）；窄屏为「列表 → 全屏预览 + 树抽屉」（REQ-7）。
 *
 * 服务端只存元数据；预览与下载经 PIM 稳定端点 302 到 OneDrive 直链（内容不经服务器）。
 */
export default function FilesPage() {
  const queryClient = useQueryClient();
  const [bindDialogOpen, setBindDialogOpen] = useState(false);

  // 记忆：上次离开的目录 / 视图 / 排序 / 搜索范围（REQ-1、REQ-9）
  const [memory, setMemory] = useState<FileBrowserMemory>(() =>
    readFileBrowserMemory(typeof window === 'undefined' ? null : window.localStorage),
  );
  const [currentPath, setCurrentPath] = useState(memory.path);
  const [page, setPage] = useState(1);
  const [queryInput, setQueryInput] = useState('');
  const [selectedItem, setSelectedItem] = useState<FileItem | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [mobilePreviewOpen, setMobilePreviewOpen] = useState(false);
  /** 手动同步刚入队时的本地乐观态（服务器状态到达前的即时反馈）。 */
  const [syncPending, setSyncPending] = useState(false);
  const [syncStarting, setSyncStarting] = useState(false);
  const [syncError, setSyncError] = useState<string | null>(null);
  const [transferOpen, setTransferOpen] = useState(false);
  /** 传输任务的活跃状态（用于在工具条上显示入口；由面板通过回调上报）。 */
  const [transferTasks, setTransferTasks] = useState<TransferTask[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [renameTarget, setRenameTarget] = useState<FileItem | null>(null);
  const [moveTarget, setMoveTarget] = useState<FileItem | null>(null);
  const [deleteTargets, setDeleteTargets] = useState<FileItem[] | null>(null);
  const [batchMoveTargets, setBatchMoveTargets] = useState<FileItem[] | null>(null);
  const [sharesOpen, setSharesOpen] = useState(false);
  const [allShares, setAllShares] = useState<FileShare[]>([]);
  const [sharesLoading, setSharesLoading] = useState(false);
  const [sharesError, setSharesError] = useState<string | null>(null);
  const [shareTarget, setShareTarget] = useState<FileItem | null>(null);
  const [shareExisting, setShareExisting] = useState<FileShare[]>([]);
  const [newFolderOpen, setNewFolderOpen] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [downloadConfirm, setDownloadConfirm] = useState<FileItem | null>(null);
  /**
   * 已展开（或需要为其准备数据）的目录集合。收起后不立即丢弃以获得「展开过的目录不再重新拉取」
   * 的体验，但保留一个**有界窗口**：超过 {@link MAX_REMEMBERED_EXPANDED} 个时淘汰最早的那些，
   * 避免长时间浏览大量目录后 useQueries 无界增长（复审 Minor）。
   */
  const [expandedPaths, setExpandedPaths] = useState<string[]>(['/']);

  const providersQuery = useQuery({
    queryKey: ['files', 'providers'],
    queryFn: getFileProviders,
  });

  const oneDriveProvider = useMemo(
    () => providersQuery.data?.find(p => p.provider === 'onedrive') ?? null,
    [providersQuery.data],
  );
  const connected = oneDriveProvider?.status === 'connected';

  const updateMemory = useCallback((patch: Partial<FileBrowserMemory>) => {
    setMemory(prev => {
      const next = { ...prev, ...patch };
      writeFileBrowserMemory(next, typeof window === 'undefined' ? null : window.localStorage);
      return next;
    });
  }, []);

  const { sort, order, view, searchScope } = memory;

  // ---- 中栏数据：当前目录（服务端过滤/排序/分页）或全盘搜索结果 ----
  const debouncedQuery = useDebouncedValue(queryInput.trim(), 250);
  const globalSearchActive = searchScope === 'global' && debouncedQuery.length > 0;

  const listQuery = useQuery({
    queryKey: ['files', 'items', oneDriveProvider?.id ?? 'none', currentPath, page, sort, order, searchScope === 'folder' ? debouncedQuery : ''],
    queryFn: () =>
      getFileItems({
        path: currentPath,
        page,
        pageSize: FILE_PAGE_SIZE,
        sort,
        order,
        q: searchScope === 'folder' ? debouncedQuery || undefined : undefined,
      }),
    enabled: connected && !globalSearchActive,
  });

  const searchQuery = useQuery({
    queryKey: ['files', 'search', oneDriveProvider?.id ?? 'none', debouncedQuery, page],
    queryFn: () => searchFiles(debouncedQuery, 'keyword', page, FILE_PAGE_SIZE),
    enabled: connected && globalSearchActive,
  });

  const activeQuery = globalSearchActive ? searchQuery : listQuery;
  const items = useMemo(() => {
    if (globalSearchActive) return searchQuery.data?.items ?? EMPTY_ITEMS;
    return listQuery.data?.result.items ?? EMPTY_ITEMS;
  }, [globalSearchActive, searchQuery.data, listQuery.data]);

  const totalCount = globalSearchActive
    ? searchQuery.data?.totalCount ?? 0
    : listQuery.data?.result.totalCount ?? 0;
  const totalPages = globalSearchActive
    ? searchQuery.data?.totalPages ?? 0
    : listQuery.data?.result.totalPages ?? 0;

  const listError = activeQuery.isError ? describeError(activeQuery.error) : null;
  const listLoading = activeQuery.isPending || activeQuery.isFetching;

  // 页码越界（例如切目录后页数变少）时收敛回有效页，避免停在空白页
  useEffect(() => {
    if (totalPages > 0 && page > totalPages) setPage(totalPages);
  }, [page, totalPages]);

  // ---- 左栏数据：目录树（只取目录，按目录懒加载）----
  const rememberExpanded = useCallback((path: string) => {
    setExpandedPaths(prev => {
      if (prev.includes(path)) return prev;
      const next = [...prev, path];
      // 淘汰最早的条目，但始终保留根目录与当前目录的祖先链
      while (next.length > MAX_REMEMBERED_EXPANDED) {
        const removable = next.findIndex(candidate => candidate !== '/' && candidate !== path);
        if (removable < 0) break;
        next.splice(removable, 1);
      }
      return next;
    });
  }, []);

  const foldersToLoad = useMemo(() => {
    const wanted = new Set<string>(['/']);
    for (const segment of breadcrumbSegments(currentPath)) wanted.add(segment.path);
    for (const path of expandedPaths) wanted.add(path);
    return [...wanted].sort();
  }, [currentPath, expandedPaths]);

  const folderQueries = useQueries({
    queries: foldersToLoad.map(path => ({
      queryKey: ['files', 'folders', oneDriveProvider?.id ?? 'none', path],
      queryFn: () => loadFolderTree(path),
      enabled: connected,
    })),
  });

  const { foldersByPath, folderStates, folderTruncation, folderTotals } = useMemo(() => {
    const byPath: Record<string, FileItem[]> = {};
    const states: Record<string, FolderLoadState> = {};
    const truncation: Record<string, { loaded: number; total: number }> = {};
    const totals: Record<string, number> = {};

    foldersToLoad.forEach((path, index) => {
      const query = folderQueries[index];
      if (!query) return;
      if (query.data) {
        byPath[path] = query.data.items;
        states[path] = 'loaded';
        totals[path] = query.data.totalCount;
        if (query.data.truncated) truncation[path] = { loaded: query.data.items.length, total: query.data.totalCount };
      } else if (query.isError) {
        // 失败时不能把该目录当成空目录：给出明确的失败态（AC-4.2）
        states[path] = 'error';
        byPath[path] = [];
      } else {
        states[path] = 'loading';
      }
    });

    return { foldersByPath: byPath, folderStates: states, folderTruncation: truncation, folderTotals: totals };
  }, [folderQueries, foldersToLoad]);

  const currentTruncation = folderTruncation[currentPath] ?? null;
  const rootState = folderStates['/'] ?? 'loading';
  // 只把「已成功加载」的目录纳入记忆有效性判定（见 isRestorablePath 注释）；
  // 同时带上真实总数，避免把「排在树加载上限之后」的真实目录误判成已删除。
  const loadedChildrenByPath = useMemo(() => {
    const loaded: Record<string, { items: FileItem[]; totalCount: number }> = {};
    for (const [path, state] of Object.entries(folderStates)) {
      if (state !== 'loaded') continue;
      loaded[path] = {
        items: foldersByPath[path] ?? [],
        totalCount: folderTotals[path] ?? foldersByPath[path]?.length ?? 0,
      };
    }
    return loaded;
  }, [folderStates, foldersByPath, folderTotals]);
  const rootTruncation = folderTruncation['/'] ?? null;

  // AC-1.2 后半句：记忆里的目录若已被改名/删除，安全回退根目录。
  // 只在根目录数据已加载完成时才判定，避免把「暂时没加载完」误判成「目录没了」。
  useEffect(() => {
    if (rootState !== 'loaded') return;
    if (currentPath === '/') return;
    if (isRestorablePath(currentPath, loadedChildrenByPath)) return;
    setCurrentPath('/');
    setPage(1);
    setSelectedItem(null);
    updateMemory({ path: '/' });
  }, [currentPath, loadedChildrenByPath, rootState, updateMemory]);

  // ---- 导航 ----
  const navigateTo = useCallback(
    (path: string) => {
      const normalized = normalizeDirPath(path);
      setCurrentPath(normalized);
      setPage(1);
      setSelectedItem(null);
      setMobilePreviewOpen(false);
      // 切换目录时清空关键词：当前文件夹过滤是**针对某个目录**的，
      // 带着上一个目录的词进入新目录，看到的往往是「本文件夹无匹配」的空列表，
      // 用户会以为新目录是空的（独立验收 Minor）。宁可清空，行为可预期。
      setQueryInput('');
      updateMemory({ path: normalized });
    },
    [updateMemory],
  );

  const handleOpenFolder = useCallback(
    (path: string) => {
      navigateTo(path);
      rememberExpanded(path);
    },
    [navigateTo, rememberExpanded],
  );

  /** 树：单击只展开/收起（数据按需加载），不改变中栏（AC-5.1）。 */
  const handleToggleFolder = useCallback(
    (path: string) => {
      rememberExpanded(path);
    },
    [rememberExpanded],
  );

  /** 手机抽屉：点一下直接进入并收起抽屉（AC-5.3）。 */
  const handleEnterFolderFromDrawer = useCallback(
    (path: string) => {
      handleOpenFolder(path);
      setDrawerOpen(false);
    },
    [handleOpenFolder],
  );

  const handleScopeChange = useCallback(
    (scope: FileSearchScope) => {
      setPage(1);
      updateMemory({ searchScope: scope });
    },
    [updateMemory],
  );

  const handleSortChange = useCallback(
    (nextSort: FileSortKey, nextOrder: FileSortOrder) => {
      setPage(1);
      updateMemory({ sort: nextSort, order: nextOrder });
    },
    [updateMemory],
  );

  /** 全盘搜索命中某条 → 跳到它所在目录（AC-8.1）；跳出搜索态，避免中栏继续显示旧结果。 */
  const handleRevealInFolder = useCallback(
    (path: string, item?: FileItem) => {
      setQueryInput('');
      updateMemory({ searchScope: 'folder', path: normalizeDirPath(path) });
      setCurrentPath(normalizeDirPath(path));
      setPage(1);
      // 保留被点条目为选中项：搜索结果点一下应当能直接进预览（复审 Important），
      // 而不是把用户刚点的那一条丢掉。
      setSelectedItem(item ?? null);
      setMobilePreviewOpen(Boolean(item));
    },
    [updateMemory],
  );

  const handleRetry = useCallback(() => {
    void activeQuery.refetch();
  }, [activeQuery]);

  // REQ-25：同步状态轮询（横幅数据源）。仅在同步进行中或刚触发时轮询，避免无谓请求。
  const statusQuery = useQuery({
    queryKey: ['files', 'sync-status', oneDriveProvider?.id ?? 'none'],
    queryFn: () => getOneDriveSyncStatus(oneDriveProvider!.id),
    enabled: connected && Boolean(oneDriveProvider),
    refetchInterval: query => {
      const current = query.state.data as OneDriveSyncStatus | undefined;
      return current?.syncStatus === 'syncing' ? 2000 : false;
    },
  });

  /**
   * REQ-25 / AC-25.1：手动同步**后台化**——点击后立即反馈「已开始」，不阻塞页面。
   * 完成后由状态轮询把结果反映到横幅，并刷新列表。
   */
  const handleSync = useCallback(async () => {
    if (!oneDriveProvider) return;
    setSyncStarting(true);
    setSyncError(null);
    try {
      const started = await startOneDriveSync(oneDriveProvider.id);
      toast.success(started.message || '已开始同步，可继续浏览');
      // 乐观态：横幅立刻显示「同步中」（AC-25.1 立即反馈）
      setSyncPending(true);
      await statusQuery.refetch();
      await queryClient.invalidateQueries({ queryKey: ['files'] });
    } catch (e) {
      const message = describeError(e);
      setSyncError(message);
      toast.error(message);
    } finally {
      setSyncStarting(false);
      setSyncPending(false);
    }
  }, [oneDriveProvider, queryClient, statusQuery]);

  const handleDisconnect = useCallback(async () => {
    if (!oneDriveProvider) return;
    try {
      await disconnectFileProvider(oneDriveProvider.id);
      toast.success('已断开 OneDrive 绑定（仅清除本地元数据）');
      setExpandedPaths(['/']);
      navigateTo('/');
      await queryClient.invalidateQueries({ queryKey: ['files'] });
    } catch (e) {
      toast.error(describeError(e));
    }
  }, [navigateTo, oneDriveProvider, queryClient]);

  // 横幅状态直接从查询派生：不再用 effect 往 state 里搬（避免级联渲染）
  const syncStatus: OneDriveSyncStatus | null = statusQuery.data ?? null;

  /**
   * REQ-11 剪贴板入口：挂在 document 上而不是某个容器上。
   * 挂在容器上的话必须先点进该容器再按 Ctrl+V，用户按了没反应会以为功能不存在；
   * 页面级监听才是「复制一个文件 → 在文件页粘贴」的真实预期行为。
   */
  useEffect(() => {
    const onPaste = (event: ClipboardEvent) => {
      const files = extractFiles(event.clipboardData);
      if (files.length === 0) return;
      dispatchFilesForUpload(files, currentPath);
      setTransferOpen(true);
    };
    document.addEventListener('paste', onPaste);
    return () => document.removeEventListener('paste', onPaste);
  }, [currentPath]);

  /** 刷新当前视图与树（任何写操作后调用，保证列表/树同步，AC-16.1）。 */
  const refreshAll = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ['files'] });
  }, [queryClient]);

  /** REQ-20：下载走微软直链、**新窗口触发**，不把整文件读进页面内存（AC-20.1）。 */
  const startDownload = useCallback((item: FileItem) => {
    // REQ-20 / AC-20.1：先向 PIM 取 JSON 形态的直链，再 window.open 由浏览器直接从微软域下载。
    //
    // 这里**刻意不用** 302 端点 + fetch 跟随去读 response.url：那样虽然只想要一个 URL，
    // 浏览器却会顺着 302 真发一次文件请求并下载响应体（验收 F-3：额外传输一次文件体，
    // 大文件风险最高）。JSON 端点只返回链接字符串，服务器与页面都不搬字节。
    void (async () => {
      try {
        const url = await getDownloadUrl(item.id);
        if (!url) throw new Error('未取到下载直链');
        window.open(url, '_blank', 'noopener,noreferrer');
      } catch (e) {
        toast.error(describeError(e));
      }
    })();
  }, []);

  const handleDownload = useCallback(
    (item: FileItem) => {
      // AC-20.2：超过 100MB 先确认（显示大小）
      if (requiresDownloadConfirmation(item.size)) {
        setDownloadConfirm(item);
        return;
      }
      startDownload(item);
    },
    [startDownload],
  );

  const handleRowAction = useCallback(
    async (action: RowAction, item: FileItem) => {
      switch (action) {
        case 'open':
          handleOpenFolder(item.path);
          break;
        case 'download':
          handleDownload(item);
          break;
        case 'rename':
          setRenameTarget(item);
          break;
        case 'move':
          setMoveTarget(item);
          break;
        case 'delete':
          setDeleteTargets([item]);
          break;
        case 'share':
          setShareTarget(item);
          try {
            setShareExisting(await getItemShares(item.id));
          } catch {
            setShareExisting([]);
          }
          break;
        case 'open-in-onedrive':
          // REQ-22：必须是微软域页面（AC-22.2），敏感路径由后端拒绝
          try {
            const link = await getFileOpenLink(item.id, 'view');
            window.open(link.url, '_blank', 'noopener,noreferrer');
          } catch (e) {
            toast.error(describeError(e));
          }
          break;
        default:
          break;
      }
    },
    [handleDownload, handleOpenFolder],
  );

  /** REQ-18：批量操作逐项执行并逐项报告（AC-18.1），失败项列出原因。 */
  const runBatch = useCallback(
    async (action: 'delete' | 'move' | 'download', targets: FileItem[], destination?: string) => {
      const results: { item: FileItem; error?: string | null }[] = [];
      for (const item of targets) {
        try {
          if (action === 'delete') {
            await deleteFile(item.id);
          } else if (action === 'move' && destination) {
            await moveFile(item.id, { destinationPath: destination });
          } else if (action === 'download') {
            handleDownload(item);
          }
          results.push({ item });
        } catch (e) {
          results.push({ item, error: describeError(e) });
        }
      }

      const outcome = summarizeBatchResults(results);
      const summary = describeBatchOutcome(outcome, action === 'delete' ? '删除' : action === 'move' ? '移动' : '下载');
      if (outcome.failed.length > 0) {
        toast.error(summary);
      } else {
        toast.success(summary);
      }
      // AC-18.2：操作后选择态清零
      setSelectedIds(new Set());
      await refreshAll();
    },
    [handleDownload, refreshAll],
  );

  const handleConnected = useCallback(() => {
    setBindDialogOpen(false);
    toast.success('OneDrive 绑定成功');
    queryClient.invalidateQueries({ queryKey: ['files'] });
  }, [queryClient]);

  // 同步后元数据可能变化：按 id 从当前列表里取最新对象派生
  const selectedItemLive = useMemo(() => {
    if (!selectedItem) return null;
    return items.find(item => item.id === selectedItem.id) ?? selectedItem;
  }, [selectedItem, items]);

  const handleSelectFileFromTree = useCallback((item: FileItem) => {
    setSelectedItem(item);
    setMobilePreviewOpen(true);
  }, []);

  const retryFolderTree = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['files', 'folders'] });
  }, [queryClient]);

  const tree = (
    <OneDriveFileTree
      foldersByPath={foldersByPath}
      folderStates={folderStates}
      currentPath={currentPath}
      onToggleFolder={handleToggleFolder}
      onEnterFolder={handleOpenFolder}
      onSelectFile={handleSelectFileFromTree}
      onRetryFolder={path => {
        void queryClient.invalidateQueries({ queryKey: ['files', 'folders', oneDriveProvider?.id ?? 'none', path] });
      }}
    />
  );

  const treeDrawerTree = (
    <OneDriveFileTree
      foldersByPath={foldersByPath}
      folderStates={folderStates}
      currentPath={currentPath}
      // 手机抽屉：点一下目录 = 直接进入（AC-5.3）；文件仍是选中预览
      onToggleFolder={handleEnterFolderFromDrawer}
      onEnterFolder={handleEnterFolderFromDrawer}
      onSelectFile={item => {
        handleSelectFileFromTree(item);
        setDrawerOpen(false);
      }}
      onRetryFolder={path => {
        void queryClient.invalidateQueries({ queryKey: ['files', 'folders', oneDriveProvider?.id ?? 'none', path] });
      }}
    />
  );

  return (
    <div className="flex h-full flex-col">
      <PageHeader title="文件" subtitle="OneDrive 个人版 · 只存元数据，内容留在云端" />

      {/* 三栏撑满可用宽度（REQ-6）：不再居中限宽，窗口变化时按比例分配 */}
      <div className="flex w-full flex-1 flex-col overflow-hidden px-2 pb-2 md:px-4 md:pb-4">
        {!connected ? (
          <EmptyBindingState
            loading={providersQuery.isLoading}
            hasPending={oneDriveProvider?.status === 'pending'
              || oneDriveProvider?.status === 'expired'
              || oneDriveProvider?.status === 'denied'}
            onBind={() => setBindDialogOpen(true)}
          />
        ) : (
          <div className="pim-card flex min-h-0 flex-1 overflow-hidden">
            {/* 左栏：目录树（~240px；手机收入抽屉） */}
            <aside
              className="hidden w-60 shrink-0 flex-col border-r border-[var(--pim-border)] bg-[var(--pim-surface)] md:flex"
              data-testid="tree-pane"
            >
              <div className="flex items-center justify-between gap-2 border-b border-[var(--pim-border)] px-3 py-2 text-xs text-[var(--pim-text-muted)]">
                <span className="truncate">{oneDriveProvider?.accountName ?? 'OneDrive'}</span>
                <button type="button" className="shrink-0 text-[var(--pim-primary)] underline" onClick={handleDisconnect}>
                  断开
                </button>
              </div>
              <div className="min-h-0 flex-1 overflow-auto">{tree}</div>
              <TreeStatusBanner
                state={rootState}
                truncation={currentTruncation ?? rootTruncation}
                onRetry={retryFolderTree}
                testIdPrefix="desktop"
              />
            </aside>

            {/* 中栏（REQ-11：桌面拖拽到列表 = 上传到当前目录；Ctrl+V 粘贴同样入口） */}
            <div
              className={`flex min-w-0 flex-1 flex-col ${dragging ? 'ring-2 ring-inset ring-[var(--pim-primary)]' : ''}`}
              data-testid="drop-zone"
              onDragOver={event => {
                // 拖拽文件时 DataTransfer.types 里会出现文件类型标记（英文常量）。
                // 用小写比较，既兼容大小写差异，也避免本地化扫描把这个 DOM 常量
                // 误判成用户可见的英文界面文案（它不是界面文本）。
                if (event.dataTransfer?.types?.some(type => type.toLowerCase() === 'files')) {
                  event.preventDefault();
                  setDragging(true);
                }
              }}
              onDragLeave={() => setDragging(false)}
              onDrop={event => {
                const files = extractFiles(event.dataTransfer);
                if (files.length === 0) return;
                event.preventDefault();
                setDragging(false);
                dispatchFilesForUpload(files, currentPath);
                setTransferOpen(true);
              }}
            >
              <div className="flex items-center gap-2 border-b border-[var(--pim-border)] px-3 py-2">
                <button
                  type="button"
                  className="pim-button-secondary inline-flex items-center gap-1 px-2 py-1 text-xs md:hidden"
                  aria-label="打开目录抽屉"
                  onClick={() => setDrawerOpen(true)}
                >
                  <FolderTree size={14} /> 目录
                </button>
                <div className="min-w-0 flex-1">
                  <SyncBanner
                    status={syncStatus}
                    starting={syncStarting || syncPending}
                    onSync={() => void handleSync()}
                    error={syncError}
                  />
                </div>
                {/* REQ-11：上传按钮入口（桌面拖拽与剪贴板粘贴见列表区域） */}
                <label className="pim-button-secondary inline-flex cursor-pointer items-center gap-1.5 px-3 text-sm">
                  <Upload size={14} />
                  上传
                  <input
                    type="file"
                    multiple
                    className="hidden"
                    aria-label="上传文件"
                    data-testid="upload-input"
                    onChange={event => {
                      const files = Array.from(event.target.files ?? []);
                      if (files.length > 0) {
                        dispatchFilesForUpload(files, currentPath);
                        setTransferOpen(true);
                      }
                      event.target.value = '';
                    }}
                  />
                </label>
                <button
                  type="button"
                  className="pim-button-secondary inline-flex items-center gap-1.5 px-3 text-sm"
                  data-testid="new-folder-button"
                  onClick={() => setNewFolderOpen(true)}
                >
                  <FolderPlus size={14} />
                  新建文件夹
                </button>
                {/* 常驻：只有历史记录时也要能打开面板查看结果与失败原因（AC-13.3） */}
                <button
                  type="button"
                  className="pim-button-secondary inline-flex items-center gap-1.5 px-3 text-sm"
                  data-testid="transfer-toggle"
                  onClick={() => setTransferOpen(open => !open)}
                >
                  <Upload size={14} />
                  传输任务
                  {hasActiveTransfers(transferTasks) && (
                    <span className="ml-0.5 h-1.5 w-1.5 rounded-full bg-[var(--pim-primary)]" data-testid="transfer-active-dot" />
                  )}
                </button>
                {/* REQ-21：与「传输任务」平级的独立入口。曾一度被误写成它内部的子按钮——
                    按钮嵌套按钮是非法 HTML，子按钮还会盖住父按钮的命中区，导致点「传输任务」
                    时命中的是「我的分享」，同时弹出传输面板和全屏分享弹窗（遮罩拦住后续所有点击）。 */}
                <button
                  type="button"
                  className="pim-button-secondary inline-flex items-center gap-1.5 px-3 text-sm"
                  data-testid="my-shares-button"
                  onClick={() => {
                    setSharesOpen(true);
                    setSharesLoading(true);
                    setSharesError(null);
                    void getAllShares(50)
                      .then(setAllShares)
                      .catch(e => setSharesError(describeError(e)))
                      .finally(() => setSharesLoading(false));
                  }}
                >
                  <Link2 size={14} />
                  我的分享
                </button>
              </div>
              <OneDriveFileList
                items={items}
                loading={listLoading && items.length === 0}
                error={listError}
                onRetry={handleRetry}
                query={queryInput}
                onQueryChange={value => {
                  setQueryInput(value);
                  setPage(1);
                }}
                searchScope={searchScope}
                onSearchScopeChange={handleScopeChange}
                showFullPath={globalSearchActive}
                onRevealInFolder={handleRevealInFolder}
                currentPath={currentPath}
                onNavigate={navigateTo}
                sort={sort}
                order={order}
                onSortChange={handleSortChange}
                page={page}
                totalPages={totalPages}
                totalCount={totalCount}
                onPageChange={setPage}
                view={view}
                onViewChange={nextView => updateMemory({ view: nextView })}
                selectedItem={selectedItemLive}
                onSelect={item => {
                  setSelectedItem(item);
                  setMobilePreviewOpen(true);
                }}
                onOpenFolder={handleOpenFolder}
                selectedIds={selectedIds}
                onToggleSelected={id =>
                  setSelectedIds(current => {
                    const next = new Set(current);
                    if (next.has(id)) next.delete(id);
                    else next.add(id);
                    return next;
                  })
                }
                onToggleAll={ids => {
                  setSelectedIds(current => {
                    const allSelected = ids.length > 0 && ids.every(id => current.has(id));
                    return allSelected ? new Set() : new Set(ids);
                  });
                }}
                onClearSelection={() => setSelectedIds(new Set())}
                onBatch={action => {
                  const targets = resolveSelectedItems(items, selectedIds);
                  if (targets.length === 0) return;
                  if (action === 'delete') {
                    setDeleteTargets(targets);
                  } else if (action === 'move') {
                    // 批量移动需要先选目标目录：用一个虚拟条目驱动同一个移动弹窗，
                    // 确认后对**全部勾选项**执行（AC-18.1）
                    setBatchMoveTargets(targets);
                  } else {
                    void runBatch(action, targets);
                  }
                }}
                rowMenu={item => <RowMenu item={item} onAction={(action, target) => void handleRowAction(action, target)} />}
              />
            </div>

            {/* 右栏：预览（~340px；窄屏为全屏浮层）。
                无选中时保留同宽占位说明，不得塌缩成 0 宽、也不得遮挡列表（AC-6.2）。 */}
            <div
              className={
                mobilePreviewOpen && selectedItem
                  ? 'fixed inset-0 z-40 flex flex-col overflow-auto bg-[var(--pim-surface)] md:static md:z-auto md:w-[340px] md:shrink-0 md:overflow-y-auto md:border-l md:border-[var(--pim-border)] md:bg-transparent'
                  : 'hidden md:flex md:w-[340px] md:shrink-0 md:flex-col md:border-l md:border-[var(--pim-border)]'
              }
              data-testid="preview-column"
            >
              <button
                type="button"
                className="border-b border-[var(--pim-border)] px-4 py-2 text-left text-sm text-[var(--pim-primary)] md:hidden"
                onClick={() => setMobilePreviewOpen(false)}
              >
                ← 返回列表
              </button>
              <div className="min-h-0 flex-1 md:flex">
                {selectedItemLive ? (
                  <OneDrivePreviewPane item={selectedItemLive} onToast={message => toast(message)} />
                ) : (
                  <div
                    className="flex w-full flex-1 items-center justify-center p-6 text-center text-sm text-[var(--pim-text-muted)]"
                    data-testid="preview-placeholder"
                  >
                    选择一个文件查看预览
                  </div>
                )}
              </div>
            </div>
          </div>
        )}
      </div>

      {/* 手机：目录抽屉（AC-5.3 / AC-7.1） */}
      {connected && drawerOpen && (
        <div className="fixed inset-0 z-50 flex md:hidden" data-testid="tree-drawer">
          <div className="flex h-full w-64 flex-col bg-[var(--pim-surface)] shadow-[var(--pim-shadow-pop)]">
            <div className="flex items-center justify-between border-b border-[var(--pim-border)] px-3 py-2 text-sm">
              <span className="truncate">{oneDriveProvider?.accountName ?? 'OneDrive'}</span>
              <button type="button" aria-label="关闭目录抽屉" onClick={() => setDrawerOpen(false)}>
                <X size={16} />
              </button>
            </div>
            <div className="min-h-0 flex-1 overflow-auto">{treeDrawerTree}</div>
            <TreeStatusBanner
              state={rootState}
              truncation={rootTruncation}
              onRetry={retryFolderTree}
              testIdPrefix="drawer"
            />
          </div>
          <button
            type="button"
            className="h-full flex-1 bg-black/30"
            aria-label="关闭目录抽屉遮罩"
            onClick={() => setDrawerOpen(false)}
          />
        </div>
      )}

      {/* 传输任务面板（REQ-13） */}
      {connected && (
        <TransferPanel
          target={{ path: currentPath, providerId: oneDriveProvider?.id ?? '' }}
          open={transferOpen}
          onClose={() => setTransferOpen(false)}
          onFinished={() => void refreshAll()}
          onTasksChange={setTransferTasks}
        />
      )}

      {/* REQ-15：新建文件夹 */}
      {newFolderOpen && (
        <NameDialog
          title="新建文件夹"
          initialValue="新建文件夹"
          submitLabel="创建"
          testId="new-folder-dialog"
          onCancel={() => setNewFolderOpen(false)}
          onSubmit={async name => {
            await createFolder(`${currentPath === '/' ? '' : currentPath}/${name}`);
            toast.success(`已创建「${name}」`);
            setNewFolderOpen(false);
            await refreshAll();
          }}
        />
      )}

      {/* REQ-15：重命名 */}
      {renameTarget && (
        <NameDialog
          title={`重命名「${renameTarget.name}」`}
          initialValue={renameTarget.name}
          submitLabel="保存"
          testId="rename-dialog"
          onCancel={() => setRenameTarget(null)}
          onSubmit={async name => {
            await renameFile(renameTarget.id, { name });
            toast.success('已重命名');
            setRenameTarget(null);
            await refreshAll();
          }}
        />
      )}

      {/* REQ-16：移动 */}
      {moveTarget && (
        <MoveDialog
          item={moveTarget}
          folders={Object.keys(foldersByPath).map(path => ({ path, name: path }))}
          onCancel={() => setMoveTarget(null)}
          onMove={async destination => {
            await moveFile(moveTarget.id, { destinationPath: destination });
            toast.success('已移动');
            setMoveTarget(null);
            await refreshAll();
          }}
        />
      )}

      {/* REQ-21 / AC-21.3：我的分享列表 + 就地撤销 */}
      {sharesOpen && (
        <MySharesDialog
          shares={allShares}
          loading={sharesLoading}
          error={sharesError}
          onClose={() => setSharesOpen(false)}
          onReveal={share => {
            setSharesOpen(false);
            handleRevealInFolder(share.path.replace(/\/[^/]*$/, '') || '/');
          }}
          onRevoke={async share => {
            if (!share.permissionId) return;
            try {
              await revokeShare(share.itemId, share.permissionId);
              setAllShares(current => current.filter(item => item.permissionId !== share.permissionId));
              toast.success('已撤销该分享链接');
            } catch (e) {
              setSharesError(describeError(e));
            }
          }}
        />
      )}

      {/* REQ-18：批量移动（逐项执行并逐项报告，AC-18.1） */}
      {batchMoveTargets && batchMoveTargets.length > 0 && (
        <MoveDialog
          item={{ ...batchMoveTargets[0], name: `已选 ${batchMoveTargets.length} 项` }}
          folders={Object.keys(foldersByPath).map(path => ({ path, name: path }))}
          onCancel={() => setBatchMoveTargets(null)}
          onMove={async destination => {
            const targets = batchMoveTargets;
            setBatchMoveTargets(null);
            await runBatch('move', targets, destination);
          }}
        />
      )}

      {/* REQ-17：删除二次确认（含还原指引） */}
      {deleteTargets && (
        <DeleteDialog
          items={deleteTargets}
          onCancel={() => setDeleteTargets(null)}
          onConfirm={async () => {
            await runBatch('delete', deleteTargets);
            setDeleteTargets(null);
          }}
        />
      )}

      {/* REQ-21：分享 */}
      {shareTarget && (
        <ShareDialog
          item={shareTarget}
          existing={shareExisting}
          // V2 已由验收方用真实账号现场实测：个人版支持有效期（edit + 7 天 → 201）。
          // 因此按「支持」呈现；若将来某账号不支持，后端的档位校验会如实报错而不是假装成功。
          expirationSupported
          onCancel={() => setShareTarget(null)}
          onCreate={(permissionType, expiresInDays) =>
            createShare(shareTarget.id, permissionType, expiresInDays || null)
          }
          onRevoke={async permissionId => {
            await revokeShare(shareTarget.id, permissionId);
            toast.success('已撤销该分享链接');
          }}
        />
      )}

      {/* REQ-20：超过 100MB 先确认（显示大小） */}
      {downloadConfirm && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/30 p-4" data-testid="download-confirm">
          <div role="dialog" aria-modal="true" aria-label="确认下载" className="w-full max-w-sm rounded-xl border border-[var(--pim-border)] bg-[var(--pim-surface)] p-4">
            <h2 className="mb-2 text-sm font-medium">确认下载</h2>
            <p className="text-xs text-[var(--pim-text-muted)]" data-testid="download-confirm-message">
              「{downloadConfirm.name}」大小为 {formatBytes(downloadConfirm.size)}，确定要下载吗？
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                className="pim-button-secondary px-3 py-1.5 text-sm"
                data-testid="download-confirm-cancel"
                onClick={() => setDownloadConfirm(null)}
              >
                取消
              </button>
              <button
                type="button"
                className="pim-button-primary px-3 py-1.5 text-sm"
                data-testid="download-confirm-ok"
                onClick={() => {
                  const item = downloadConfirm;
                  setDownloadConfirm(null);
                  startDownload(item);
                }}
              >
                下载
              </button>
            </div>
          </div>
        </div>
      )}

      {bindDialogOpen && (
        <OneDriveBindDialog
          onClose={() => setBindDialogOpen(false)}
          onConnected={handleConnected}
          initialClientId={oneDriveProvider?.clientId ?? ''}
        />
      )}
    </div>
  );
}

/** 把任意异常翻译成可读原因（AC-10.3：不得出现「未知错误」）。 */
function describeError(error: unknown): string {
  if (error instanceof Error && error.message.trim()) return error.message;
  return '加载失败，请稍后重试';
}

/** 输入去抖：避免每敲一个字就打一次服务端搜索。 */
function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setDebounced(value), delayMs);
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [value, delayMs]);

  return debounced;
}

/**
 * 目录树的状态条：失败时可读原因 + 重试，截断时如实说明（AC-10.3 / AC-3.2）。
 * 导出以便直接断言文案口径（桌面左栏与手机抽屉共用同一个实例）。
 */
export function TreeStatusBanner({
  state,
  truncation,
  onRetry,
  testIdPrefix,
}: {
  state: FolderLoadState;
  truncation: { loaded: number; total: number } | null;
  onRetry: () => void;
  testIdPrefix: string;
}) {
  if (state === 'error') {
    return (
      <div
        className="flex items-center gap-2 border-t border-[var(--pim-border)] bg-[var(--pim-danger-soft)] px-3 py-1.5 text-[11px] text-[var(--pim-danger)]"
        role="alert"
        data-testid={`${testIdPrefix}-tree-root-error`}
      >
        <span className="min-w-0 flex-1">目录树加载失败</span>
        <button
          type="button"
          className="rounded border border-[var(--pim-border)] px-1 text-[10px]"
          onClick={onRetry}
        >
          重试
        </button>
      </div>
    );
  }

  if (!truncation) return null;

  return (
    <div
      className="border-t border-[var(--pim-border)] px-3 py-1.5 text-[11px] text-[var(--pim-warning)]"
      data-testid={`${testIdPrefix}-tree-truncated`}
    >
      {/* 这是**子项**总数（含文件与目录），不是目录数——措辞必须与口径一致，不谎报 */}
      已加载 {truncation.loaded} / 共 {truncation.total} 项（超出部分未在树中展开）
    </div>
  );
}

function EmptyBindingState({ loading, hasPending, onBind }: { loading: boolean; hasPending: boolean; onBind: () => void }) {
  useEffect(() => {
    // 绑定未完成（pending/expired/denied）时自动弹出对话框，免去手动点击
    if (hasPending) onBind();
  }, [hasPending, onBind]);
  if (loading) {
    return (
      <div className="flex flex-1 items-center justify-center p-16 text-sm text-[var(--pim-text-muted)]" data-testid="files-loading">
        <Loader2 size={18} className="mr-2 animate-spin" /> 加载中…
      </div>
    );
  }
  return (
    <div className="pim-card mt-6 flex flex-col items-center gap-3 p-12 text-center" data-testid="files-empty">
      <p className="text-sm text-[var(--pim-text-muted)]">
        {hasPending
          ? 'OneDrive 绑定尚未完成授权，请重新获取设备码并登录'
          : '还没有绑定 OneDrive。绑定后文件树与元数据自动同步，内容留在云端。'}
      </p>
      <button type="button" className="pim-button-primary px-4" onClick={onBind}>
        {hasPending ? '重新绑定' : '绑定 OneDrive'}
      </button>
    </div>
  );
}
