import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { Download, History } from 'lucide-react'
import { auditApi } from '../api'
import type { AuditRestorePreview, AuditVersionItem } from '../api'
import { DiffTable } from '../components/diff-table'
import { formatTime } from '@/lib/datetime'
import { downloadJson } from '@/lib/download'
import { notifySuccess } from '@/lib/notify'
import { Button, Card, CardTitle, EmptyState, InlineAlert, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

/** 审计时间线（02 §audit：版本行列表 + 详情 + 恢复预览 + 导出） */
export function AuditTimelinePage() {
  const { objectType = '', objectId = '' } = useParams()
  const [items, setItems] = useState<AuditVersionItem[] | null>(null)
  const [selected, setSelected] = useState<AuditVersionItem | null>(null)
  const [preview, setPreview] = useState<AuditRestorePreview | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)

  useEffect(() => {
    let cancelled = false
    setItems(null)
    auditApi
      .timeline(objectType, objectId)
      .then((res) => {
        if (cancelled) return
        setItems(res.items)
        setSelected(res.items[res.items.length - 1] ?? null)
      })
      .catch(() => !cancelled && setItems([]))
    return () => {
      cancelled = true
    }
  }, [objectType, objectId])

  async function restorePreview(versionId: string) {
    setPreviewLoading(true)
    try {
      setPreview(await auditApi.restorePreview(versionId))
    } finally {
      setPreviewLoading(false)
    }
  }

  async function exportAudit() {
    const res = await auditApi.export()
    downloadJson(res.fileName, res.content)
    notifySuccess('审计导出已下载')
  }

  return (
    <div>
      <PageHeader
        title="审计时间线"
        subtitle={`${objectType} · ${objectId.slice(0, 8)}…`}
        actions={
          <Button variant="secondary" size="sm" onClick={() => void exportAudit()}>
            <Download className="size-4" aria-hidden /> 导出审计
          </Button>
        }
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
        {/* 版本列表 */}
        <div className="space-y-1.5">
          {items == null ? (
            Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-16" />)
          ) : items.length === 0 ? (
            <Card>
              <EmptyState title="没有审计版本" description="该对象还没有被修改过。" />
            </Card>
          ) : (
            items.map((v, i) => (
              <button
                key={v.id}
                type="button"
                onClick={() => setSelected(v)}
                className={cn(
                  'w-full rounded-card border px-3 py-2.5 text-left transition-colors outline-none',
                  selected?.id === v.id ? 'border-primary bg-primary-soft' : 'border-border bg-bg shadow-card hover:border-border-strong',
                )}
              >
                <div className="flex items-center gap-2">
                  <span className="tnum text-[13px] font-semibold text-text-1">v{i + 1}</span>
                  <span className="text-xs text-text-3">{v.source}</span>
                  <span className="tnum ml-auto text-[11px] text-text-4">{formatTime(v.createdAt)}</span>
                </div>
                <div className="mt-1 flex flex-wrap gap-1">
                  {(safeParseArray(v.changedFieldsJson) ?? []).slice(0, 4).map((f) => (
                    <span key={f} className="rounded-badge bg-surface-2 px-1.5 py-0.5 text-[11px] text-text-3">{f}</span>
                  ))}
                </div>
              </button>
            ))
          )}
        </div>

        {/* 版本详情 */}
        <div className="space-y-4">
          {selected ? (
            <>
              <Card className="p-5">
                <div className="flex flex-wrap items-center gap-2">
                  <CardTitle className="text-base">版本详情</CardTitle>
                  <StatusBadge tone="neutral" dot={false}>{selected.source}</StatusBadge>
                  <span className="tnum ml-auto text-xs text-text-4">{formatTime(selected.createdAt)}</span>
                </div>
                <div className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-[13px]">
                  <div className="text-text-3">对象类型</div><div>{selected.objectType}</div>
                  <div className="text-text-3">操作者</div><div>{selected.actor}</div>
                  <div className="text-text-3">确认单</div>
                  <div className="mono break-all">{selected.confirmationId ?? '—'}</div>
                </div>
                <Button
                  variant="secondary"
                  size="sm"
                  className="mt-3"
                  loading={previewLoading}
                  onClick={() => void restorePreview(selected.id)}
                >
                  <History className="size-4" aria-hidden /> 恢复到此版本（预览）
                </Button>
              </Card>

              {preview && (
                <div className="space-y-3">
                  <InlineAlert tone="warn" title="恢复预览">{preview.summary}</InlineAlert>
                  <DiffTable before={preview.beforeJson} after={preview.afterJson} />
                </div>
              )}

              <Card className="p-5">
                <CardTitle className="text-sm">前后差异</CardTitle>
                <div className="mt-2">
                  <DiffTable before={selected.beforeJson} after={selected.afterJson} />
                </div>
              </Card>
            </>
          ) : (
            <Card>
              <EmptyState title="选择左侧版本查看详情" />
            </Card>
          )}
        </div>
      </div>
    </div>
  )
}

function safeParseArray(json: string): string[] | null {
  try {
    const parsed = JSON.parse(json)
    return Array.isArray(parsed) ? (parsed as string[]) : null
  } catch {
    return null
  }
}
