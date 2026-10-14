import { useEffect, useMemo, useRef, useState } from 'react'
import {
  ChevronRight,
  Download,
  ExternalLink,
  File as FileIcon,
  FolderPlus,
  Grid2X2,
  Link2,
  List,
  MoreHorizontal,
  RefreshCw,
  Search,
  Share2,
  Trash2,
  Upload,
} from 'lucide-react'
import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import { fetchBlob } from '@/api/client'
import { filesApi } from '../api'
import { FolderTree } from '../components/folder-tree'
import { TransferPanel } from '../components/transfer-panel'
import { FilePreview } from '../components/file-preview'
import { FileNameDialog, MoveDialog, ShareDialog, DeleteDialog, MySharesDialog } from '../components/file-dialogs'
import {
  useFileItems,
  useFileMutations,
  useFileSearch,
  useProviders,
  useSyncStatus,
  useTransferQueue,
} from '../queries'
import { uploadEngine } from '../upload-engine'
import { formatBytes } from '../upload-chunk-plan'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import { notifySuccess } from '@/lib/notify'
import { Button, Card, Chip, EmptyState, Input, PageHeader, Segmented, Skeleton } from '@/components/ui'
import { cn } from '@/lib/utils'
import { providerLabel, type FileItem } from '../types'

interface BrowserMemory {
  path: string
  q?: string
  sort: 'name' | 'modified' | 'size'
  order: 'asc' | 'desc'
  view: 'list' | 'grid'
  searchScope: 'folder' | 'all'
  expanded?: boolean
}

const DEFAULT_MEMORY: BrowserMemory = { path: '/', sort: 'name', order: 'asc', view: 'list', searchScope: 'folder', expanded: true }

/** 文件页（02 §files：多网盘三栏浏览器，可在账号间切换） */
export function FilesPage() {
  const [memory, setMemory] = useState<BrowserMemory>(() => ({ ...DEFAULT_MEMORY, ...getJSON<Partial<BrowserMemory>>(STORAGE_KEYS.fileBrowserMemory, {}) }))
  const [q, setQ] = useState('')
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [previewItem, setPreviewItem] = useState<FileItem | null>(null)
  const [treeOpen, setTreeOpen] = useState(false)
  const [transferOpen, setTransferOpen] = useState(false)
  const [nameDialog, setNameDialog] = useState<{ mode: 'folder' | 'rename'; target?: FileItem } | null>(null)
  const [moveDialog, setMoveDialog] = useState<FileItem[] | null>(null)
  const [deleteDialog, setDeleteDialog] = useState<FileItem[] | null>(null)
  const [shareDialog, setShareDialog] = useState<FileItem | null>(null)
  const [mySharesOpen, setMySharesOpen] = useState(false)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)

  const providers = useProviders()
  const accounts = providers.data ?? []
  const { items: transfers } = useTransferQueue()

  /*
   * 账号选择（多网盘）：仅显示**已连接**的账号；默认选中记住的那个，
   * 其次是默认落点账号，最后退回第一个。绝不写死 providers[0] ——
   * 那正是多账号第一次落地的坑（会把另一个账号的文件当成这个账号的）。
   */
  const connectedAccounts = useMemo(() => accounts.filter((a) => a.status === 'connected'), [accounts])
  const [accountId, setAccountId] = useState<string | null>(() => getJSON<string | null>(STORAGE_KEYS.fileAccountId, null))
  const provider = useMemo(
    () =>
      connectedAccounts.find((a) => a.id === accountId) ??
      connectedAccounts.find((a) => a.isDefault) ??
      connectedAccounts[0],
    [connectedAccounts, accountId],
  )

  useEffect(() => {
    if (provider && provider.id !== accountId) {
      setAccountId(provider.id)
      setJSON(STORAGE_KEYS.fileAccountId, provider.id)
    }
  }, [provider, accountId])

  const providerId = provider?.id

  const isGlobalSearch = memory.searchScope === 'all' && q.trim().length > 0
  const folderQuery = useFileItems(providerId, memory.path, isGlobalSearch ? '' : q, memory.sort, memory.order)
  // 搜索默认跨账号（与后端口径一致）；结果里的每条都带 providerId
  const globalQuery = useFileSearch(q, memory.searchScope === 'all')

  const listItems = isGlobalSearch ? (globalQuery.data?.items ?? []) : (folderQuery.data?.result.items ?? [])
  const isLoading = isGlobalSearch ? globalQuery.isLoading : folderQuery.isLoading
  const total = isGlobalSearch ? (globalQuery.data?.totalCount ?? 0) : (folderQuery.data?.result.totalCount ?? 0)

  const syncStatus = useSyncStatus(provider?.id)
  const mutations = useFileMutations()

  /* 持久化浏览器记忆 */
  function patchMemory(patch: Partial<BrowserMemory>) {
    setMemory((prev) => {
      const next = { ...prev, ...patch }
      setJSON(STORAGE_KEYS.fileBrowserMemory, next)
      return next
    })
  }

  const breadcrumbs = useMemo(() => {
    const parts = memory.path.split('/').filter(Boolean)
    return [{ name: '我的文件', path: '/' }, ...parts.map((p, i) => ({ name: p, path: '/' + parts.slice(0, i + 1).join('/') }))]
  }, [memory.path])

  const activeTransfers = transfers.filter((t) => t.status === 'uploading' || t.status === 'queued').length

  /* 拖放上传：整中栏为热区 */
  function onDropFiles(files: FileList | null) {
    if (!files?.length || !provider) return
    for (const file of Array.from(files)) {
      uploadEngine.enqueue({ providerId: provider.id, file, targetPath: memory.path })
    }
    setTransferOpen(true)
    notifySuccess(`已加入上传队列（${files.length} 个文件）`)
  }

  /* 粘贴上传（文档级） */
  function onPaste(e: React.ClipboardEvent) {
    const files = e.clipboardData?.files
    if (files?.length) onDropFiles(files)
  }

  async function download(item: FileItem) {
    if (item.size != null && item.size > 100 * 1024 * 1024) {
      const okConfirm = window.confirm(`该文件 ${formatBytes(item.size)}，超过 100MB，确认打开下载？`)
      if (!okConfirm) return
    }
    const { url } = await filesApi.downloadUrl(item.id)
    window.open(url, '_blank', 'noopener')
  }

  /** 在网盘网页端打开（OneDrive / Google Drive 各自的原生页面） */
  async function openInProvider(item: FileItem) {
    const { url } = await filesApi.openLink(item.id)
    window.open(url, '_blank', 'noopener')
  }

  /* 未绑定引导 */
  if (!providers.isLoading && provider == null) {
    return (
      <div>
        <PageHeader title="文件" subtitle="网盘文件浏览器" />
        <Card className="mx-auto max-w-[480px] p-6 text-center">
          <EmptyState
            icon={Link2}
            title="尚未连接网盘"
            description="连接 OneDrive 或 Google Drive 后即可在此浏览、上传、预览与分享文件（文件仍在你自己网盘里）。"
            action={
              <Button variant="primary" size="sm" onClick={() => (window.location.href = '/settings/microsoft?tab=onedrive')}>
                <Link2 className="size-4" aria-hidden /> 去连接网盘
              </Button>
            }
          />
        </Card>
      </div>
    )
  }

  return (
    <div className="flex h-full min-h-0 flex-col" onPaste={onPaste}>
      <PageHeader
        title="文件"
        subtitle={provider ? `${providerLabel(provider.provider)} · ${provider.accountName ?? '已连接'}` : '网盘文件浏览器'}
        actions={
          <>
            {/* 账号选择：两个及以上账号时必须能切换（工单 F2.3 / AC-5） */}
            {connectedAccounts.length > 1 && (
              <label className="flex items-center gap-1.5 text-xs text-text-3">
                <span className="sr-only">选择网盘账号</span>
                <select
                  aria-label="选择网盘账号"
                  className="rounded-ctl border border-border bg-bg px-2 py-1 text-xs text-text-1 outline-none"
                  value={provider?.id ?? ''}
                  onChange={(e) => {
                    setAccountId(e.target.value)
                    setJSON(STORAGE_KEYS.fileAccountId, e.target.value)
                    // 换账号 = 换一棵目录树，路径回到根并清空选择
                    patchMemory({ path: '/' })
                    setSelectedIds(new Set())
                  }}
                >
                  {connectedAccounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {providerLabel(account.provider)} · {account.accountName ?? account.id.slice(0, 8)}
                      {account.isDefault ? '（默认）' : ''}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {connectedAccounts.length === 1 && provider && (
              <Chip>{providerLabel(provider.provider)}</Chip>
            )}
            <Button variant="ghost" size="sm" className="lg:hidden" onClick={() => setTreeOpen(true)}>
              目录
            </Button>
            <Button variant="ghost" size="sm" className="relative" onClick={() => setTransferOpen((o) => !o)}>
              <Upload className="size-4" aria-hidden /> 传输
              {activeTransfers > 0 && (
                <span className="absolute -top-1 -right-1 grid size-4 place-items-center rounded-full bg-primary text-[10px] text-primary-fg">{activeTransfers}</span>
              )}
            </Button>
            <Button variant="secondary" size="sm" onClick={() => setMySharesOpen(true)}>
              <Share2 className="size-4" aria-hidden /> 我的分享
            </Button>
            <Button variant="primary" size="sm" onClick={() => setNameDialog({ mode: 'folder' })}>
              <FolderPlus className="size-4" aria-hidden /> 新建文件夹
            </Button>
          </>
        }
      />

      {/* 同步状态横幅（条件轮询） */}
      {syncStatus.data?.syncStatus === 'syncing' && (
        <div className="mb-2 flex items-center gap-2 rounded-ctl border border-info-border bg-info-soft px-3 py-1.5 text-xs text-info">
          <RefreshCw className="size-3.5 animate-spin" aria-hidden />
          正在同步 {provider ? providerLabel(provider.provider) : '网盘'} 目录…（每 2 秒刷新状态）
        </div>
      )}
      {syncStatus.data?.syncStatus === 'error' && syncStatus.data.lastError && (
        <div className="mb-2 flex items-center gap-2 rounded-ctl border border-crit-border bg-crit-soft px-3 py-1.5 text-xs text-crit">
          {syncStatus.data.lastError}
          <Button
            variant="ghost"
            size="sm"
            className="ml-auto"
            loading={mutations.startSync.isPending}
            onClick={() => provider && void mutations.startSync.mutateAsync(provider.id)}
          >
            重新同步
          </Button>
        </div>
      )}

      <div className="flex min-h-0 flex-1 gap-4">
        {/* 左：文件夹树 */}
        <aside className="hidden w-[220px] shrink-0 overflow-y-auto rounded-card border border-border bg-bg p-2 shadow-card lg:block">
          {providerId && (
            <FolderTree
              providerId={providerId}
              currentPath={memory.path}
              onNavigate={(p) => patchMemory({ path: p })}
            />
          )}
        </aside>

        {/* 中：工具行 + 列表（拖放热区） */}
        <div
          className={cn('flex min-w-0 flex-1 flex-col rounded-card border bg-bg shadow-card transition-colors', dragging ? 'border-primary bg-primary-soft/30' : 'border-border')}
          onDragEnter={(e) => {
            e.preventDefault()
            dragDepth.current++
            setDragging(true)
          }}
          onDragOver={(e) => e.preventDefault()}
          onDragLeave={() => {
            dragDepth.current--
            if (dragDepth.current <= 0) setDragging(false)
          }}
          onDrop={(e) => {
            e.preventDefault()
            dragDepth.current = 0
            setDragging(false)
            onDropFiles(e.dataTransfer.files)
          }}
        >
          {/* 工具行 */}
          <div className="flex flex-wrap items-center gap-2 border-b border-divider px-3 py-2">
            {/* 面包屑 */}
            <nav className="flex min-w-0 items-center gap-0.5 text-[13px]">
              {breadcrumbs.map((b, i) => (
                <span key={b.path} className="flex min-w-0 items-center gap-0.5">
                  {i > 0 && <ChevronRight className="size-3.5 shrink-0 text-text-4" aria-hidden />}
                  <button
                    type="button"
                    onClick={() => patchMemory({ path: b.path })}
                    className={cn('truncate rounded-ctl px-1 py-0.5 outline-none hover:bg-surface', i === breadcrumbs.length - 1 ? 'font-medium text-text-1' : 'text-text-3')}
                  >
                    {b.name}
                  </button>
                </span>
              ))}
            </nav>

            <div className="ml-auto flex items-center gap-1.5">
              <div className="relative">
                <Search className="pointer-events-none absolute top-2 left-2 size-3.5 text-text-4" aria-hidden />
                <Input
                  className="h-7 w-40 pl-7 text-[13px]"
                  placeholder="搜索文件…"
                  value={q}
                  onChange={(e) => setQ(e.target.value)}
                />
              </div>
              <Chip active={memory.searchScope === 'all'} onClick={() => patchMemory({ searchScope: memory.searchScope === 'all' ? 'folder' : 'all' })}>
                全盘
              </Chip>
              <span className="tnum hidden text-[11px] text-text-4 sm:inline">{total} 项</span>
              <Segmented
                size="sm"
                value={memory.view}
                onValueChange={(v) => patchMemory({ view: v })}
                options={[
                  { value: 'list', label: <List className="size-3.5" aria-hidden /> },
                  { value: 'grid', label: <Grid2X2 className="size-3.5" aria-hidden /> },
                ]}
              />
            </div>
          </div>

          {/* 列表/网格 */}
          <div className="min-h-0 flex-1 overflow-y-auto">
            {isLoading ? (
              <div className="space-y-1.5 p-3">
                {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} className="h-9" />)}
              </div>
            ) : listItems.length === 0 ? (
              <EmptyState
                title={q.trim() ? '没有匹配的文件' : '这个文件夹是空的'}
                description={q.trim() ? '试试更换关键词或切换搜索范围。' : '把文件拖到这里即可上传。'}
              />
            ) : memory.view === 'list' ? (
              <table className="w-full text-[13px]">
                <thead className="sticky top-0 z-10 bg-surface text-left text-xs text-text-3">
                  <tr>
                    <th className="w-9 px-3 py-2">
                      <input
                        type="checkbox"
                        aria-label="全选"
                        className="size-3.5 accent-primary"
                        checked={selectedIds.size === listItems.length && listItems.length > 0}
                        onChange={(e) => setSelectedIds(e.target.checked ? new Set(listItems.map((i) => i.id)) : new Set())}
                      />
                    </th>
                    <th className="px-2 py-2 font-medium">
                      <SortHeader label="名称" active={memory.sort === 'name'} order={memory.order} onClick={() => patchMemory({ sort: 'name', order: memory.sort === 'name' && memory.order === 'asc' ? 'desc' : 'asc' })} />
                    </th>
                    <th className="w-28 px-2 py-2 text-right font-medium">
                      <SortHeader label="大小" active={memory.sort === 'size'} order={memory.order} onClick={() => patchMemory({ sort: 'size', order: memory.sort === 'size' && memory.order === 'asc' ? 'desc' : 'asc' })} />
                    </th>
                    <th className="hidden w-36 px-2 py-2 font-medium sm:table-cell">
                      <SortHeader label="修改时间" active={memory.sort === 'modified'} order={memory.order} onClick={() => patchMemory({ sort: 'modified', order: memory.sort === 'modified' && memory.order === 'asc' ? 'desc' : 'asc' })} />
                    </th>
                    <th className="w-10 px-2 py-2" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-divider">
                  {listItems.map((item) => (
                    <FileRow
                      key={item.id}
                      item={item}
                      selected={selectedIds.has(item.id)}
                      previewing={previewItem?.id === item.id}
                      onToggleSelect={() =>
                        setSelectedIds((prev) => {
                          const next = new Set(prev)
                          if (next.has(item.id)) next.delete(item.id)
                          else next.add(item.id)
                          return next
                        })
                      }
                      onOpen={() => (item.itemType === 'folder' ? patchMemory({ path: item.path, q: '' }) : setPreviewItem(item))}
                      onPreview={() => setPreviewItem(item)}
                      onRename={() => setNameDialog({ mode: 'rename', target: item })}
                      onMove={() => setMoveDialog([item])}
                      onDelete={() => setDeleteDialog([item])}
                      onShare={() => setShareDialog(item)}
                      onDownload={() => void download(item)}
                      onOpenOneDrive={() => void openInProvider(item)}
                    />
                  ))}
                </tbody>
              </table>
            ) : (
              <div className="grid grid-cols-2 gap-3 p-3 sm:grid-cols-3 xl:grid-cols-4">
                {listItems.map((item) => (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => (item.itemType === 'folder' ? patchMemory({ path: item.path, q: '' }) : setPreviewItem(item))}
                    className={cn(
                      'flex flex-col items-center gap-2 rounded-card border p-3 text-center transition-colors outline-none',
                      previewItem?.id === item.id ? 'border-primary bg-primary-soft' : 'border-border hover:border-border-strong',
                    )}
                  >
                    <Thumbnail item={item} />
                    <span className="line-clamp-2 break-all text-[12px] text-text-1">{item.name}</span>
                    {item.itemType === 'file' && <span className="tnum text-[10px] text-text-4">{formatBytes(item.size)}</span>}
                  </button>
                ))}
              </div>
            )}
          </div>

          {/* 多选批操作条 */}
          {selectedIds.size > 0 && (
            <div className="flex shrink-0 items-center gap-2 border-t border-border bg-surface px-3 py-2">
              <span className="tnum text-[13px] text-text-2">已选 {selectedIds.size} 项</span>
              <Button
                variant="secondary"
                size="sm"
                loading={mutations.move.isPending}
                onClick={() => setMoveDialog(listItems.filter((i) => selectedIds.has(i.id)))}
              >
                移动
              </Button>
              <Button
                variant="danger-soft"
                size="sm"
                onClick={() => setDeleteDialog(listItems.filter((i) => selectedIds.has(i.id)))}
              >
                <Trash2 className="size-3.5" aria-hidden /> 删除
              </Button>
              <Button variant="ghost" size="sm" className="ml-auto" onClick={() => setSelectedIds(new Set())}>
                取消选择
              </Button>
            </div>
          )}
        </div>

        {/*
          右：预览/传输面板。
          仅在有选中文件或传输任务时占位，否则整列收起、中间栏自动占满宽度
          （此前恒定显示「选择文件以预览」空态，白白吃掉 340px）。
        */}
        {(previewItem != null || transferOpen || activeTransfers > 0) && (
          <aside className="hidden w-[340px] shrink-0 xl:block">
            {transferOpen || activeTransfers > 0 ? (
              <TransferPanel onClose={() => setTransferOpen(false)} />
            ) : (
              <FilePreview item={previewItem!} onClose={() => setPreviewItem(null)} />
            )}
          </aside>
        )}
      </div>

      {/* 窄屏抽屉：目录树 */}
      {treeOpen && (
        <div className="fixed inset-0 z-40 flex lg:hidden">
          <div className="absolute inset-0 bg-text-1/30" onClick={() => setTreeOpen(false)} />
          <div className="relative z-10 h-full w-[260px] animate-[pim-slide-in-left_240ms_cubic-bezier(.32,.72,.24,1)] overflow-y-auto bg-bg p-3">
            <FolderTree
              providerId={provider?.id ?? ''}
              currentPath={memory.path}
              onNavigate={(p) => {
                patchMemory({ path: p })
                setTreeOpen(false)
              }}
            />
          </div>
        </div>
      )}

      {/* 窄屏抽屉：预览 */}
      {previewItem && (
        <div className="fixed inset-0 z-40 xl:hidden">
          <div className="absolute inset-0 bg-text-1/30" onClick={() => setPreviewItem(null)} />
          <div className="absolute inset-y-0 right-0 z-10 flex w-[min(420px,100vw)] flex-col bg-bg">
            <FilePreview item={previewItem} onClose={() => setPreviewItem(null)} />
          </div>
        </div>
      )}

      {/* 弹窗族 */}
      {nameDialog && (
        <FileNameDialog
          mode={nameDialog.mode}
          target={nameDialog.target}
          currentPath={memory.path}
          providerId={provider?.id ?? ''}
          onClose={() => setNameDialog(null)}
        />
      )}
      {moveDialog && <MoveDialog items={moveDialog} providerId={provider?.id ?? ''} onClose={() => setMoveDialog(null)} />}
      {deleteDialog && <DeleteDialog items={deleteDialog} onClose={() => setDeleteDialog(null)} onDone={() => setSelectedIds(new Set())} />}
      {shareDialog && <ShareDialog item={shareDialog} onClose={() => setShareDialog(null)} />}
      {mySharesOpen && <MySharesDialog onClose={() => setMySharesOpen(false)} />}
    </div>
  )
}

/* ── 行 / 缩略图 / 排序头 ─────────────────────────────────── */

function SortHeader({ label, active, order, onClick }: { label: string; active: boolean; order: 'asc' | 'desc'; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className={cn('inline-flex items-center gap-1 outline-none hover:text-text-1', active && 'text-text-1')}>
      {label}
      {active && <span className="text-[9px]">{order === 'asc' ? '▲' : '▼'}</span>}
    </button>
  )
}

function Thumbnail({ item }: { item: FileItem }) {
  const [url, setUrl] = useState<string | null>(null)
  const isImage = item.mimeType?.startsWith('image/')

  useEffect(() => {
    if (!isImage || item.itemType === 'folder') return
    let revoked: string | null = null
    let cancelled = false
    void fetchBlob(`/api/v1/files/items/${item.id}/thumbnail?size=medium`)
      .then((blob) => {
        if (cancelled) return
        const u = URL.createObjectURL(blob)
        revoked = u
        setUrl(u)
      })
      .catch(() => {})
    return () => {
      cancelled = true
      if (revoked) URL.revokeObjectURL(revoked)
    }
  }, [item.id, isImage, item.itemType])

  if (item.itemType === 'folder') {
    return <span className="grid size-10 place-items-center rounded-ctl bg-surface-2 text-text-3"><FolderPlus className="size-5" aria-hidden /></span>
  }
  if (url) return <img src={url} alt="" className="size-10 rounded-ctl object-cover" />
  return <span className="grid size-10 place-items-center rounded-ctl bg-surface-2 text-text-3"><FileIcon className="size-5" aria-hidden /></span>
}

function FileRow({
  item,
  selected,
  previewing,
  onToggleSelect,
  onOpen,
  onPreview,
  onRename,
  onMove,
  onDelete,
  onShare,
  onDownload,
  onOpenOneDrive,
}: {
  item: FileItem
  selected: boolean
  previewing: boolean
  onToggleSelect: () => void
  onOpen: () => void
  onPreview: () => void
  onRename: () => void
  onMove: () => void
  onDelete: () => void
  onShare: () => void
  onDownload: () => void
  onOpenOneDrive: () => void
}) {
  return (
    <tr className={cn('transition-colors hover:bg-surface', (selected || previewing) && 'bg-primary-soft/60')}>
      <td className="px-3 py-1.5">
        <input type="checkbox" checked={selected} onChange={onToggleSelect} className="size-3.5 accent-primary" aria-label={`选择 ${item.name}`} />
      </td>
      <td className="px-2 py-1.5">
        <button type="button" onClick={onOpen} className="flex min-w-0 items-center gap-2 text-left outline-none">
          <span className="grid size-5 shrink-0 place-items-center text-text-3">
            {item.itemType === 'folder' ? <FolderPlus className="size-3.5" aria-hidden /> : <FileIcon className="size-3.5" aria-hidden />}
          </span>
          <span className="truncate text-text-1" title={item.path}>{item.name}</span>
        </button>
      </td>
      <td className="tnum px-2 py-1.5 text-right text-text-3">{item.itemType === 'folder' ? '—' : formatBytes(item.size)}</td>
      <td className="tnum hidden px-2 py-1.5 text-text-3 sm:table-cell">{new Date(item.modifiedAt).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })}</td>
      <td className="px-2 py-1.5">
        <DropdownMenu.Root>
          <DropdownMenu.Trigger asChild>
            <button type="button" aria-label={`${item.name} 操作`} className="rounded-ctl p-1 text-text-3 outline-none hover:bg-surface-2 data-[state=open]:bg-surface-2">
              <MoreHorizontal className="size-4" aria-hidden />
            </button>
          </DropdownMenu.Trigger>
          <DropdownMenu.Portal>
            <DropdownMenu.Content sideOffset={4} align="end" className="z-50 min-w-32 rounded-ctl border border-border bg-bg p-1 shadow-overlay animate-[pim-pop-in_150ms_ease-out]">
              {item.itemType === 'file' && (
                <>
                  <MenuItem onSelect={onPreview}>预览</MenuItem>
                  <MenuItem onSelect={onDownload}><Download className="size-3.5" aria-hidden /> 下载</MenuItem>
                </>
              )}
              <MenuItem onSelect={onRename}>重命名</MenuItem>
              <MenuItem onSelect={onMove}>移动到…</MenuItem>
              <MenuItem onSelect={onShare}>
                <ExternalLink className="size-3.5" aria-hidden /> 分享
              </MenuItem>
              <MenuItem onSelect={onOpenOneDrive}>在网盘中打开</MenuItem>
              <DropdownMenu.Separator className="my-1 h-px bg-divider" />
              <MenuItem onSelect={onDelete} danger>
                <Trash2 className="size-3.5" aria-hidden /> 删除
              </MenuItem>
            </DropdownMenu.Content>
          </DropdownMenu.Portal>
        </DropdownMenu.Root>
      </td>
    </tr>
  )
}

function MenuItem({ children, onSelect, danger, disabled }: { children: React.ReactNode; onSelect: () => void; danger?: boolean; disabled?: boolean }) {
  return (
    <DropdownMenu.Item
      disabled={disabled}
      onSelect={onSelect}
      className={cn(
        'flex cursor-default items-center gap-2 rounded-ctl px-2 py-1.5 text-[13px] outline-none data-[highlighted]:bg-surface data-[disabled]:opacity-50',
        danger ? 'text-crit data-[highlighted]:bg-crit-soft' : 'text-text-1',
      )}
    >
      {children}
    </DropdownMenu.Item>
  )
}

