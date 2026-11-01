import { useState } from 'react'
import { Button, Card, EmptyState, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import type { TaskHistoryView } from './types'

export interface TaskHistoryPanelProps {
  entries: TaskHistoryView[]
  onSelect?: (id: string) => void
  loading?: boolean
  loadError?: string | null
  selectedId?: string | null
  pageSize?: number
  readOnly?: boolean
  disabled?: boolean
}

export function TaskHistoryPanel({
  entries,
  onSelect,
  loading = false,
  loadError = null,
  selectedId = null,
  pageSize = 5,
  readOnly = false,
  disabled = false,
}: TaskHistoryPanelProps) {
  const [page, setPage] = useState(1)
  const boundedPageSize = Math.max(1, pageSize)
  const totalPages = Math.max(1, Math.ceil(entries.length / boundedPageSize))
  const currentPage = Math.min(page, totalPages)
  const visible = entries.slice((currentPage - 1) * boundedPageSize, currentPage * boundedPageSize)

  return <Card className="min-w-0 space-y-3 p-4 sm:p-5">
    <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
      <div>
        <h2 className="text-sm font-semibold text-text-1">不可变执行历史</h2>
        <p className="mt-1 break-words text-xs text-text-3">每次运行、失败原因和旧结果都保留；新的重跑只能追加记录，不能覆盖历史。</p>
      </div>
      <span className="text-xs text-text-3">第 {currentPage} / {totalPages} 页</span>
    </div>
    {loadError && <InlineAlert tone="crit" title="部分历史读取失败">{loadError}；下方已加载记录继续保留，不代表任务失败或被删除。</InlineAlert>}
    {loading ? <Skeleton className="h-28" /> : !entries.length
      ? <EmptyState size="sm" title="还没有执行记录" description="创建任务后，每次尝试都会在这里形成只追加的历史。" />
      : <div className="space-y-2">{visible.map((entry) => <button
        key={entry.id}
        type="button"
        className={`w-full min-w-0 rounded-ctl border p-3 text-left outline-none transition-colors focus-visible:outline-2 focus-visible:outline-primary-ring ${
          entry.id === selectedId ? 'border-primary bg-primary-soft' : 'border-border bg-bg hover:bg-surface'
        }`}
        disabled={disabled || readOnly || !onSelect}
        onClick={() => onSelect?.(entry.id)}
      >
        <span className="flex min-w-0 flex-wrap items-center justify-between gap-2">
          <span className="min-w-0 break-words text-sm font-medium text-text-1">{entry.title}</span>
          <StatusBadge size="xs" dot={false} tone={entry.statusTone ?? 'neutral'}>{entry.statusLabel}</StatusBadge>
        </span>
        <span className="mt-1 block break-words text-xs text-text-3">{entry.createdAtLabel} · {entry.routeLabel}</span>
        <span className="mt-1 block break-all text-xs text-text-3">目标：{entry.destinationLabel}</span>
        {entry.summary && <span className="mt-1 block break-words text-xs text-text-2">{entry.summary}</span>}
        <span className="mt-1 flex flex-wrap gap-2 text-[11px] text-text-4">
          {entry.isCurrent && <span>当前记录</span>}
          {entry.attemptLabel && <span>{entry.attemptLabel}</span>}
        </span>
      </button>)}</div>}
    {entries.length > boundedPageSize && <div className="flex flex-wrap justify-end gap-2">
      <Button size="sm" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>上一页历史</Button>
      <Button size="sm" disabled={currentPage >= totalPages} onClick={() => setPage(currentPage + 1)}>下一页历史</Button>
    </div>}
    {readOnly && <p className="text-xs text-text-3">只读历史：不会创建、停止或重跑任何任务。</p>}
  </Card>
}
