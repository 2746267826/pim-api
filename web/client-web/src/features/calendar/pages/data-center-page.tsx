import { useEffect, useMemo, useState } from 'react'
import { Download, History, ScrollText } from 'lucide-react'
import { dataCenterApi } from '../api'
import { useDataCenterQuery } from '../queries'
import type { DataCenterItem } from '../types'
import { auditApi, type AuditVersionItem } from '@/features/operations/api'
import { DiffTable } from '@/features/operations/components/diff-table'
import { downloadJson } from '@/lib/download'
import { formatRange, formatTime } from '@/lib/datetime'
import { htmlToPlainText } from '@/lib/text'
import { Button, Card, Drawer, DrawerBody, DrawerContent, DrawerHeader, EmptyState, InlineAlert, Input, PageHeader, Select, Skeleton, StatusBadge } from '@/components/ui'
import { notifySuccess } from '@/lib/notify'
import { cn } from '@/lib/utils'

const OBJECT_TYPES = [
  { value: '', label: '全部类型' },
  { value: 'event', label: '日程' },
  { value: 'task', label: '任务' },
  { value: 'task-segment', label: '任务时间段' },
  { value: 'habit', label: '习惯' },
  { value: 'reminder', label: '提醒' },
  { value: 'report', label: '报告' },
  { value: 'sync-batch', label: '同步批次' },
  { value: 'sync-conflict', label: '同步冲突' },
  { value: 'audit-version', label: '审计版本' },
]

/** 数据中心（02 §data-center：跨对象查询 + 详情 + 恢复预览 + 审计导出） */
export function DataCenterPage() {
  const [search, setSearch] = useState('')
  const [objectType, setObjectType] = useState('')
  const [page, setPage] = useState(1)
  const [detail, setDetail] = useState<{ item: DataCenterItem } | null>(null)
  const [auditVersions, setAuditVersions] = useState<AuditVersionItem[] | null>(null)
  const [showAllVersions, setShowAllVersions] = useState(false)
  const [preview, setPreview] = useState<{ summary: string; changedFields: string[]; beforeJson: string | null; afterJson: string | null } | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)

  const params = useMemo(() => ({ search: search || undefined, objectType: objectType || undefined, pendingOnly: false, page, pageSize: 50 }), [search, objectType, page])
  const { data, isLoading, isFetching } = useDataCenterQuery(params)

  async function exportAudit() {
    const res = await dataCenterApi.auditExport()
    downloadJson(res.fileName, res.content)
    notifySuccess('审计导出已下载')
  }

  /* 详情抽屉打开时拉取该对象的审计版本（应用内写路径才会产生；空=未经应用内修改） */
  useEffect(() => {
    if (!detail) return
    let cancelled = false
    setAuditVersions(null)
    setShowAllVersions(false)
    auditApi
      .timeline(detail.item.objectType, detail.item.objectId)
      .then((res) => !cancelled && setAuditVersions(res.items))
      .catch(() => !cancelled && setAuditVersions([]))
    return () => {
      cancelled = true
    }
  }, [detail])

  async function openRestorePreview(objectType: string, objectId: string) {
    if (objectType !== 'audit-version') {
      setPreview({ summary: '该对象类型支持从回收站恢复（设置 → 回收站）。', changedFields: [], beforeJson: null, afterJson: null })
      return
    }
    setPreviewLoading(true)
    try {
      const res = await dataCenterApi.restorePreview({ auditVersionId: objectId, reason: '数据中心版本恢复预览' })
      setPreview({ summary: res.summary, changedFields: res.changedFields, beforeJson: res.beforeJson, afterJson: res.afterJson })
    } finally {
      setPreviewLoading(false)
    }
  }

  const totalPages = Math.max(1, Math.ceil((data?.totalCount ?? 0) / 50))

  return (
    <div>
      <PageHeader
        title="数据中心"
        subtitle="跨对象治理 / 审计 / 恢复"
        actions={
          <Button variant="secondary" size="sm" onClick={() => void exportAudit()}>
            <Download className="size-4" aria-hidden /> 导出审计
          </Button>
        }
      />

      {/* 筛选栏 */}
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Input className="w-56" placeholder="搜索标题/描述/位置…" value={search} onChange={(e) => { setSearch(e.target.value); setPage(1) }} />
        <Select
          value={objectType}
          onValueChange={(v) => { setObjectType(v); setPage(1) }}
          options={OBJECT_TYPES}
        />
        {isFetching && <span className="text-xs text-text-4">查询中…</span>}
        <span className="tnum ml-auto text-xs text-text-3">共 {data?.totalCount ?? 0} 条</span>
      </div>

      {/* 治理表格 */}
      <Card className="overflow-x-auto">
        {isLoading ? (
          <div className="space-y-2 p-4">{Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="h-10" />)}</div>
        ) : (data?.items?.length ?? 0) === 0 ? (
          <EmptyState title="没有符合条件的对象" />
        ) : (
          <table className="w-full text-[13px]">
            <thead>
              <tr className="bg-surface text-left text-xs text-text-3">
                <th className="px-4 py-2.5 font-medium">标题摘要</th>
                <th className="px-4 py-2.5 font-medium">类型</th>
                <th className="px-4 py-2.5 font-medium">来源</th>
                <th className="px-4 py-2.5 font-medium">状态</th>
                <th className="px-4 py-2.5 font-medium">开始时间</th>
                <th className="px-4 py-2.5 font-medium"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-divider">
              {data!.items.map((item) => (
                <tr key={`${item.objectType}:${item.objectId}`} className="transition-colors hover:bg-surface">
                  <td className="max-w-72 truncate px-4 py-2.5 text-text-1">
                    {item.title}
                    {htmlToPlainText(item.summary) && <span className="ml-2 text-xs text-text-4">{htmlToPlainText(item.summary)}</span>}
                  </td>
                  <td className="px-4 py-2.5 text-text-3">{item.objectType}</td>
                  <td className="px-4 py-2.5 text-text-3">{item.source}</td>
                  <td className="px-4 py-2.5"><StatusBadge tone={item.status === 'Active' ? 'ok' : 'neutral'} dot={false}>{item.status}</StatusBadge></td>
                  <td className="tnum px-4 py-2.5 text-text-3">{item.startsAt ? formatRange(item.startsAt, item.endsAt ?? item.startsAt).split(' ~ ')[0] : '—'}</td>
                  <td className="px-4 py-2.5 text-right">
                    <Button variant="ghost" size="sm" onClick={() => setDetail({ item })}>
                      详情
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {/* 分页 */}
      {totalPages > 1 && (
        <div className="mt-3 flex items-center justify-end gap-2 text-[13px] text-text-3">
          <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          <span className="tnum">{page} / {totalPages}</span>
          <Button variant="secondary" size="sm" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>下一页</Button>
        </div>
      )}

      {/* 对象详情抽屉：键值网格 + 摘要正文 + 审计历史 + 恢复预览（规格 02 §data-center 对象详情面板） */}
      <Drawer open={detail != null} onOpenChange={(o) => !o && setDetail(null)}>
        <DrawerContent side="right">
          <DrawerHeader title="对象详情" description={detail ? `${detail.item.objectType} · ${detail.item.title}` : undefined} />
          <DrawerBody className="space-y-4">
            {detail && (
              <>
                <div className="rounded-ctl border border-border p-3 text-[13px]">
                  <DetailRow label="类型" value={detail.item.objectType} />
                  <DetailRow label="状态" value={detail.item.status} />
                  <DetailRow label="来源" value={detail.item.source} />
                  <DetailRow label="开始" value={detail.item.startsAt ? formatTime(detail.item.startsAt) : '—'} />
                  <DetailRow label="结束" value={detail.item.endsAt ? formatTime(detail.item.endsAt) : '—'} />
                  <DetailRow label="ID" value={detail.item.objectId} mono />
                </div>

                {(() => {
                  const text = htmlToPlainText(detail.item.summary)
                  if (!text) return null
                  return (
                    <details className="rounded-ctl border border-border p-3 text-[13px]">
                      <summary className="cursor-pointer font-medium text-text-1">摘要 / 描述</summary>
                      <p className="mt-2 whitespace-pre-wrap break-words text-text-2">{text}</p>
                    </details>
                  )
                })()}

                <div className="rounded-ctl border border-border p-3">
                  <div className="flex items-center gap-2">
                    <span className="flex items-center gap-1.5 text-[13px] font-medium text-text-1">
                      <ScrollText className="size-3.5 text-text-3" aria-hidden /> 审计历史
                    </span>
                    {auditVersions == null ? (
                      <span className="text-xs text-text-4">加载中…</span>
                    ) : (
                      <span className="tnum text-xs text-text-4">{auditVersions.length} 个版本</span>
                    )}
                  </div>
                  {auditVersions != null && auditVersions.length > 0 && (
                    <>
                      <ul className="mt-2 space-y-1.5">
                        {(showAllVersions ? [...auditVersions].reverse() : [...auditVersions].reverse().slice(0, 8)).map((v) => {
                          let changed: string[] = []
                          try { changed = JSON.parse(v.changedFieldsJson) as string[] } catch { /* 忽略解析失败 */ }
                          return (
                            <li key={v.id} className="rounded-ctl bg-surface px-2.5 py-1.5 text-xs">
                              <div className="flex items-center gap-2">
                                <span className="font-medium text-text-1">{v.actor || v.source}</span>
                                <span className="tnum ml-auto text-text-4">{formatTime(v.createdAt)}</span>
                              </div>
                              {changed.length > 0 && <div className="mt-0.5 text-text-3">变更：{changed.join('、')}</div>}
                            </li>
                          )
                        })}
                      </ul>
                      {auditVersions.length > 8 && (
                        <button
                          type="button"
                          onClick={() => setShowAllVersions((v) => !v)}
                          className="mt-1.5 w-full rounded-ctl py-1 text-center text-xs text-primary outline-none hover:bg-surface"
                        >
                          {showAllVersions ? '收起' : `展开全部 ${auditVersions.length} 条`}
                        </button>
                      )}
                    </>
                  )}
                  {auditVersions != null && auditVersions.length === 0 && (
                    <p className="mt-1.5 text-xs text-text-4">该对象未经应用内修改（如 Outlook 同步直接写入），没有审计版本。</p>
                  )}
                </div>

                <Button
                  variant="secondary"
                  size="sm"
                  loading={previewLoading}
                  onClick={() => void openRestorePreview(detail.item.objectType, detail.item.objectId)}
                >
                  <History className="size-4" aria-hidden /> 恢复预览
                </Button>
                {preview && (
                  <div className="space-y-3">
                    <InlineAlert tone={preview.changedFields.length > 0 ? 'warn' : 'info'} title="恢复预览">
                      {preview.summary}
                      {preview.changedFields.length > 0 && (
                        <span className="mt-1 flex flex-wrap gap-1">
                          {preview.changedFields.map((f) => (
                            <span key={f} className="rounded-badge bg-surface-2 px-1.5 py-0.5 text-xs">{f}</span>
                          ))}
                        </span>
                      )}
                    </InlineAlert>
                    <DiffTable before={preview.beforeJson} after={preview.afterJson} />
                  </div>
                )}
              </>
            )}
          </DrawerBody>
        </DrawerContent>
      </Drawer>
    </div>
  )
}

function DetailRow({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex gap-3 py-1">
      <span className="w-14 shrink-0 text-text-3">{label}</span>
      <span className={cn('min-w-0 break-all text-text-1', mono && 'mono')}>{value}</span>
    </div>
  )
}
