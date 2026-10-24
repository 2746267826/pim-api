import { useEffect, useState } from 'react'
import { Link2, Trash2 } from 'lucide-react'
import { filesApi } from '../api'
import { useFileMutations } from '../queries'
import type { FileItem, FileShare } from '../types'
import { Button, ConfirmDialog, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, EmptyState, Input, Label, Segmented, Select, Skeleton, StatusBadge } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { formatBytes } from '../upload-chunk-plan'

/* ── 新建文件夹 / 重命名 ──────────────────────────────────── */

export function FileNameDialog({
  mode,
  target,
  currentPath,
  providerId,
  onClose,
}: {
  mode: 'folder' | 'rename'
  target?: FileItem
  currentPath: string
  /** 新建文件夹落在哪个账号（多账号必须指明） */
  providerId: string
  onClose: () => void
}) {
  const mutations = useFileMutations()
  const [name, setName] = useState(target?.name ?? '')

  const fullPath = mode === 'folder' ? `${currentPath.endsWith('/') ? currentPath : currentPath + '/'}${name}` : name

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[420px]">
        <DialogHeader title={mode === 'folder' ? '新建文件夹' : '重命名'} />
        <DialogBody>
          <Label htmlFor="file-name">{mode === 'folder' ? '文件夹名称' : '新名称'}</Label>
          <Input id="file-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && void submit()} />
          <p className="mono mt-1.5 truncate text-[11px] text-text-4">{mode === 'folder' ? fullPath : target?.path}</p>
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={onClose}>取消</Button>
          <Button
            variant="primary"
            disabled={!name.trim()}
            loading={mutations.createFolder.isPending || mutations.rename.isPending}
            onClick={() => void submit()}
          >
            {mode === 'folder' ? '创建' : '保存'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )

  async function submit() {
    const value = name.trim()
    if (!value) return
    try {
      if (mode === 'folder') {
        await mutations.createFolder.mutateAsync({ path: fullPath, providerId })
        notifySuccess('文件夹已创建')
      } else if (target) {
        await mutations.rename.mutateAsync({ id: target.id, name: value })
        notifySuccess('已重命名')
      }
      onClose()
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '操作失败')
    }
  }
}

/* ── 移动到目标文件夹 ─────────────────────────────────────── */

export function MoveDialog({
  items,
  providerId,
  onClose,
}: {
  items: FileItem[]
  /** 移动只能在同一个账号内进行：跨账号移动需要先下载再上传，本页不做那种隐式搬运 */
  providerId: string
  onClose: () => void
}) {
  const mutations = useFileMutations()
  const [folders, setFolders] = useState<FileItem[]>([])
  const [target, setTarget] = useState('/')
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    void filesApi
      .listItems({ path: '/', providerId, pageSize: 100, type: 'folder' })
      .then((res) => setFolders(res.result.items))
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [providerId])

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[460px]">
        <DialogHeader title="移动到…" description={`${items.length} 项将被移动`} />
        <DialogBody className="space-y-3">
          <div>
            <Label>目标文件夹</Label>
            <Select
              value={target}
              onValueChange={setTarget}
              options={[{ value: '/', label: '我的文件（根目录）' }, ...folders.map((f) => ({ value: f.path, label: f.path }))]}
            />
          </div>
          <div className="max-h-40 space-y-1 overflow-y-auto rounded-ctl border border-border p-2">
            {loading ? (
              <Skeleton className="h-16" />
            ) : (
              items.map((i) => (
                <div key={i.id} className="flex items-center gap-2 text-[13px]">
                  <span className="min-w-0 flex-1 truncate text-text-1">{i.name}</span>
                  <span className="mono text-[11px] text-text-4">{i.itemType === 'folder' ? '文件夹' : formatBytes(i.size)}</span>
                </div>
              ))
            )}
          </div>
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={onClose}>取消</Button>
          <Button
            variant="primary"
            loading={mutations.move.isPending}
            onClick={async () => {
              try {
                for (const item of items) {
                  await mutations.move.mutateAsync({ id: item.id, destinationPath: target })
                }
                notifySuccess(`已移动 ${items.length} 项到 ${target}`)
                onClose()
              } catch (err) {
                notifyError(err instanceof Error ? err.message : '移动失败')
              }
            }}
          >
            移动
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/* ── 批量删除确认（含 OneDrive 回收站提示） ───────────────── */

export function DeleteDialog({ items, onClose, onDone }: { items: FileItem[]; onClose: () => void; onDone?: () => void }) {
  const mutations = useFileMutations()
  const totalSize = items.reduce((acc, i) => acc + (i.size ?? 0), 0)

  return (
    <ConfirmDialog
      open
      onOpenChange={(o) => !o && onClose()}
      tone="danger"
      title={`删除 ${items.length} 项？`}
      description="文件将移入 OneDrive 回收站：如需还原，请到 OneDrive 网页版操作（个人版 API 不提供还原接口）。"
      impact={{
        summary: `共 ${items.length} 项${totalSize > 0 ? `，合计 ${formatBytes(totalSize)}` : ''}`,
        samples: items.map((i) => i.name),
      }}
      confirmLabel="删除"
      loading={mutations.remove.isPending}
      onConfirm={async () => {
        try {
          for (const item of items) {
            await mutations.remove.mutateAsync(item.id)
          }
          notifySuccess(`已删除 ${items.length} 项`)
          onDone?.()
        } catch (err) {
          notifyError(err instanceof Error ? err.message : '删除失败')
        }
      }}
    />
  )
}

/* ── 分享 / 我的分享 ──────────────────────────────────────── */

export function ShareDialog({ item, onClose }: { item: FileItem; onClose: () => void }) {
  const mutations = useFileMutations()
  const [permissionType, setPermissionType] = useState<'view' | 'edit'>('view')
  const [expires, setExpires] = useState<string>('')
  const [shares, setShares] = useState<FileShare[] | null>(null)

  useEffect(() => {
    void filesApi.shares(item.id).then(setShares).catch(() => setShares([]))
  }, [item.id])

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[480px]">
        <DialogHeader title="分享" description={item.name} />
        <DialogBody className="space-y-3">
          <div className="flex items-end gap-3">
            <div>
              <Label>权限</Label>
              <Segmented
                value={permissionType}
                onValueChange={(v) => setPermissionType(v)}
                options={[
                  { value: 'view', label: '仅查看' },
                  { value: 'edit', label: '可编辑' },
                ]}
              />
            </div>
            <div className="flex-1">
              <Label htmlFor="share-expires">有效期</Label>
              <Select
                id="share-expires"
                value={expires}
                onValueChange={setExpires}
                options={[
                  { value: '', label: '不过期' },
                  { value: '7', label: '7 天' },
                  { value: '30', label: '30 天' },
                ]}
              />
            </div>
            <Button
              variant="primary"
              loading={mutations.share.isPending}
              onClick={async () => {
                try {
                  const share = await mutations.share.mutateAsync({
                    id: item.id,
                    permissionType,
                    expiresInDays: expires === '' ? null : (Number(expires) as 7 | 30),
                  })
                  await navigator.clipboard.writeText(share.webUrl).catch(() => {})
                  notifySuccess('分享链接已创建并复制')
                  setShares(await filesApi.shares(item.id))
                } catch (err) {
                  notifyError(err instanceof Error ? err.message : '创建分享失败')
                }
              }}
            >
              <Link2 className="size-4" aria-hidden /> 创建链接
            </Button>
          </div>

          <div className="space-y-1.5">
            <Label>已有分享（{shares?.length ?? 0}）</Label>
            {shares == null ? (
              <Skeleton className="h-12" />
            ) : shares.length === 0 ? (
              <EmptyState size="sm" title="暂无分享链接" />
            ) : (
              shares.map((s) => (
                <div key={s.permissionId ?? s.webUrl} className="flex items-center gap-2 rounded-ctl border border-border px-2.5 py-2 text-[13px]">
                  <StatusBadge tone={s.permissionType === 'edit' ? 'warn' : 'info'} dot={false}>{s.permissionType === 'edit' ? '可编辑' : '仅查看'}</StatusBadge>
                  <button
                    type="button"
                    className="min-w-0 flex-1 truncate text-left text-primary outline-none hover:underline"
                    onClick={() => void navigator.clipboard.writeText(s.webUrl).then(() => notifySuccess('已复制链接'))}
                  >
                    {s.webUrl}
                  </button>
                  {s.permissionId && (
                    <button
                      type="button"
                      aria-label="撤销分享"
                      className="shrink-0 rounded-ctl p-1 text-text-4 outline-none hover:text-crit"
                      onClick={async () => {
                        await mutations.revokeShare.mutateAsync({ id: item.id, permissionId: s.permissionId! })
                        notifySuccess('已撤销')
                        setShares(await filesApi.shares(item.id))
                      }}
                    >
                      <Trash2 className="size-3.5" aria-hidden />
                    </button>
                  )}
                </div>
              ))
            )}
          </div>
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={onClose}>关闭</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export function MySharesDialog({ onClose }: { onClose: () => void }) {
  const [shares, setShares] = useState<FileShare[] | null>(null)
  useEffect(() => {
    void filesApi.allShares().then(setShares).catch(() => setShares([]))
  }, [])

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-[560px]">
        <DialogHeader title="我的分享" description="个人版 OneDrive 无全量分享 API：此处扫描最近修改的 ≤50 个文件（非全量保证）" />
        <DialogBody className="space-y-1.5">
          {shares == null ? (
            <Skeleton className="h-20" />
          ) : shares.length === 0 ? (
            <p className="py-4 text-center text-[13px] text-text-4">未发现分享链接</p>
          ) : (
            shares.map((s) => (
              <div key={`${s.itemId}-${s.webUrl}`} className="flex items-center gap-2 rounded-ctl border border-border px-2.5 py-2 text-[13px]">
                <span className="min-w-0 flex-1 truncate text-text-1">{s.path}</span>
                <StatusBadge tone={s.permissionType === 'edit' ? 'warn' : 'info'} dot={false}>{s.permissionType === 'edit' ? '可编辑' : '仅查看'}</StatusBadge>
                <button
                  type="button"
                  className="shrink-0 text-primary outline-none hover:underline"
                  onClick={() => void navigator.clipboard.writeText(s.webUrl).then(() => notifySuccess('已复制链接'))}
                >
                  复制
                </button>
              </div>
            ))
          )}
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={onClose}>关闭</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
