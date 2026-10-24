import { useState } from 'react'
import { Link } from 'react-router'
import { Download, GitMerge, Pencil, Trash2 } from 'lucide-react'
import { useDeviceActions, useMobileDevices } from '../queries'
import { mobileApi } from '../api'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { downloadBlob } from '@/lib/download'
import { Button, Card, CardTitle, ConfirmDialog, Dialog, DialogContent, EmptyState, PageHeader, Segmented, StatusBadge } from '@/components/ui'
import type { DeviceDeletePreview } from '../types'

/** 设备管理（02 §devices：排序/批量合并/重命名/删除预览/导出） */
export function DevicesPage() {
  const [sort, setSort] = useState<'active' | 'data'>('active')
  const { data: devices = [] } = useMobileDevices(sort)
  const actions = useDeviceActions()
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [mergePreview, setMergePreview] = useState<{ items: { deviceId: string; dataCount: number }[]; total: number } | null>(null)
  const [mergeTarget, setMergeTarget] = useState<string | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; preview: DeviceDeletePreview } | null>(null)
  const [renaming, setRenaming] = useState<string | null>(null)
  const [renameValue, setRenameValue] = useState('')

  const toggle = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const selectedIds = [...selected]

  return (
    <div>
      <PageHeader
        title="设备管理"
        subtitle="Android 设备数据治理"
        actions={
          <Segmented
            size="sm"
            value={sort}
            onValueChange={(v) => setSort(v)}
            options={[
              { value: 'active', label: '按活跃' },
              { value: 'data', label: '按数据量' },
            ]}
          />
        }
      />

      {/* 批量引导条 */}
      {selectedIds.length >= 2 && (
        <div className="mb-3 flex items-center gap-3 rounded-card border border-primary bg-primary-soft px-4 py-2.5">
          <span className="tnum text-[13px] text-text-2">已选 {selectedIds.length} 台</span>
          <Button
            variant="primary"
            size="sm"
            loading={actions.mergePreview.isPending}
            onClick={async () => {
              const target = selectedIds[0]!
              setMergeTarget(target)
              setMergePreview(await actions.mergePreview.mutateAsync({ sourceDeviceIds: selectedIds.filter((x) => x !== target), targetDeviceId: target }))
            }}
          >
            <GitMerge className="size-4" aria-hidden /> 合并到…
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setSelected(new Set())}>取消选择</Button>
        </div>
      )}

      {devices.length === 0 ? (
        <Card>
          <EmptyState title="没有已注册的设备" description="Android 客户端注册后会出现在这里。" />
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
          {devices.map((d) => (
            <Card key={d.deviceId} className="p-4">
              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={selected.has(d.deviceId)}
                  onChange={() => toggle(d.deviceId)}
                  className="size-4 accent-primary"
                  aria-label={`选择 ${d.displayName}`}
                />
                {renaming === d.deviceId ? (
                  <form
                    className="flex flex-1 gap-1.5"
                    onSubmit={async (e) => {
                      e.preventDefault()
                      const name = renameValue.trim()
                      if (name) await actions.rename.mutateAsync({ id: d.deviceId, name })
                      setRenaming(null)
                    }}
                  >
                    <input
                      autoFocus
                      className="h-7 w-full rounded-ctl border border-primary px-2 text-[13px] outline-none"
                      value={renameValue}
                      onChange={(e) => setRenameValue(e.target.value)}
                      onBlur={() => setRenaming(null)}
                    />
                  </form>
                ) : (
                  <>
                    <span className="min-w-0 flex-1 truncate text-sm font-semibold text-text-1">{d.displayName}</span>
                    <StatusBadge tone={d.isOnline ? 'ok' : 'neutral'}>{d.isOnline ? '在线' : '离线'}</StatusBadge>
                  </>
                )}
              </div>
              <div className="mt-1 text-xs text-text-3">
                {d.brand} {d.model} · {d.osVersion} · App {d.appVersion}
              </div>
              <div className="mono mt-1 truncate text-[11px] text-text-4" title={d.deviceId}>{d.deviceId}</div>

              {/* 数据计数 */}
              <div className="mt-2.5 grid grid-cols-4 gap-1.5 text-center">
                {[
                  { label: '会话', v: d.sessionCount },
                  { label: '事件', v: d.eventCount },
                  { label: '位置', v: d.locationCount },
                  { label: '存储 KB', v: d.storageEstimateKb },
                ].map((c) => (
                  <div key={c.label} className="rounded-ctl bg-surface py-1.5">
                    <div className="tnum text-[13px] font-semibold text-text-1">{c.v.toLocaleString()}</div>
                    <div className="text-[10px] text-text-4">{c.label}</div>
                  </div>
                ))}
              </div>

              {/* 徽标行 */}
              <div className="mt-2 flex flex-wrap gap-1.5">
                <StatusBadge tone={d.syncStatus === 'normal' ? 'ok' : d.syncStatus === 'delayed' ? 'warn' : 'crit'} dot={false}>
                  {d.syncStatus === 'normal' ? '同步正常' : d.syncStatus === 'delayed' ? '同步延迟' : '已断连'}
                </StatusBadge>
                {d.dataQuality === 'abnormal' && <StatusBadge tone="warn" dot={false}>数据异常</StatusBadge>}
                {d.storagePressure === 'pending' && <StatusBadge tone="warn" dot={false}>待上传积压</StatusBadge>}
                <span className="tnum ml-auto self-center text-[11px] text-text-4">活跃 {formatTime(d.lastSeenAtUtc)}</span>
              </div>

              {/* 操作行 */}
              <div className="mt-2.5 flex items-center gap-1.5 border-t border-divider pt-2.5">
                <Button variant="ghost" size="sm" onClick={() => { setRenaming(d.deviceId); setRenameValue(d.displayName) }}>
                  <Pencil className="size-3.5" aria-hidden /> 重命名
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={async () => {
                    try {
                      const { blob } = await mobileApi.exportDeviceBlob(d.deviceId)
                      downloadBlob(
                        `pim-export-${d.displayName}-${new Date().toISOString().slice(0, 10)}.json`,
                        blob,
                      )
                      notifySuccess('已导出（含各表最新 5000 条）')
                    } catch (err) {
                      notifyError(err instanceof Error ? err.message : '导出失败')
                    }
                  }}
                >
                  <Download className="size-3.5" aria-hidden /> 导出
                </Button>
                <Link to={`/devices/${encodeURIComponent(d.deviceId)}`} className="ml-auto text-xs text-primary hover:underline outline-none">
                  详情 →
                </Link>
                <Button
                  variant="ghost"
                  size="sm"
                  className="text-crit"
                  onClick={async () => {
                    const preview = await actions.deletePreview.mutateAsync(d.deviceId)
                    setDeleteTarget({ id: d.deviceId, preview })
                  }}
                >
                  <Trash2 className="size-3.5" aria-hidden /> 删除
                </Button>
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* 合并确认弹窗 */}
      <Dialog open={mergePreview != null} onOpenChange={(o: boolean) => { if (!o) { setMergePreview(null); setMergeTarget(null) } }}>
        <DialogContent className="max-w-[460px]">
          <DialogHeaderWithChildren
            title="合并设备"
            description={`源设备的数据将迁入并删除。目标：${mergeTarget ?? ''}`}
          />
          <div className="space-y-2 px-5 py-3">
            {(mergePreview?.items ?? []).map((it) => (
              <div key={it.deviceId} className="flex items-center gap-2 rounded-ctl bg-surface px-3 py-2 text-[13px]">
                <span className="mono min-w-0 flex-1 truncate">{it.deviceId}</span>
                <span className="tnum text-text-3">{it.dataCount.toLocaleString()} 条</span>
                <StatusBadge tone={it.deviceId === mergeTarget ? 'ok' : 'crit'} dot={false}>
                  {it.deviceId === mergeTarget ? '保留' : '移除'}
                </StatusBadge>
              </div>
            ))}
            <p className="text-xs text-text-3">合计 {mergePreview?.total.toLocaleString()} 条数据将并入目标设备。</p>
          </div>
          <div className="flex justify-end gap-2 px-5 pb-4">
            <Button variant="secondary" size="sm" onClick={() => setMergePreview(null)}>取消</Button>
            <Button
              variant="danger"
              size="sm"
              loading={actions.merge.isPending}
              onClick={async () => {
                if (!mergeTarget) return
                await actions.merge.mutateAsync({ sourceDeviceIds: selectedIds.filter((x) => x !== mergeTarget), targetDeviceId: mergeTarget })
                setMergePreview(null)
                setSelected(new Set())
              }}
            >
              执行合并
            </Button>
          </div>
        </DialogContent>
      </Dialog>

      {/* 删除确认（含预览计数） */}
      <ConfirmDialog
        open={deleteTarget != null}
        onOpenChange={(o) => !o && setDeleteTarget(null)}
        tone="danger"
        title={`删除设备「${deleteTarget?.preview.displayName ?? ''}」？`}
        description="该设备的全部数据将被删除。"
        impact={{
          summary: `将删除 ${deleteTarget?.preview.sessionCount ?? 0} 会话 / ${deleteTarget?.preview.eventCount ?? 0} 事件 / ${deleteTarget?.preview.locationCount ?? 0} 位置 / ${deleteTarget?.preview.summaryCount ?? 0} 汇总。`,
        }}
        confirmLabel="删除设备"
        loading={actions.delete.isPending}
        onConfirm={async () => {
          if (deleteTarget) await actions.delete.mutateAsync(deleteTarget.id)
        }}
      />
    </div>
  )
}

function DialogHeaderWithChildren({ title, description }: { title: string; description: string }) {
  return (
    <div className="border-b border-divider px-5 py-4">
      <CardTitle className="text-base">{title}</CardTitle>
      <p className="mt-0.5 text-[13px] text-text-3">{description}</p>
    </div>
  )
}
