import { HardDrive, RotateCw, X } from 'lucide-react'
import { useTransferQueue } from '../queries'
import { formatBytes } from '../upload-chunk-plan'
import { Card, Chip, EmptyState, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

const STATUS_META = {
  queued: { label: '排队中', tone: 'neutral' as const },
  uploading: { label: '上传中', tone: 'info' as const },
  done: { label: '已完成', tone: 'ok' as const },
  failed: { label: '失败', tone: 'crit' as const },
  rejected: { label: '已拒绝', tone: 'warn' as const },
}

/** 传输队列面板（02 §files：串行队列、逐文件进度/失败/重试、历史持久化） */
export function TransferPanel({ onClose }: { onClose?: () => void }) {
  const { items, retry, clearFinished } = useTransferQueue()
  const active = items.filter((t) => t.status === 'uploading' || t.status === 'queued').length

  return (
    <Card className="flex max-h-[420px] flex-col p-4">
      <div className="flex items-center gap-2">
        <HardDrive className="size-4 text-text-3" aria-hidden />
        <span className="text-sm font-semibold text-text-1">传输队列</span>
        <StatusBadge tone={active > 0 ? 'info' : 'neutral'} dot={false} className="ml-auto">
          {active > 0 ? `${active} 进行中` : '空闲'}
        </StatusBadge>
        {onClose && (
          <button type="button" aria-label="关闭" onClick={onClose} className="rounded-ctl p-1 text-text-3 hover:bg-surface outline-none">
            <X className="size-4" aria-hidden />
          </button>
        )}
      </div>
      <p className="mt-0.5 text-[11px] text-text-4">串行上传：≤4MB 直传，&gt;4MB 走 Graph 分片续传，&gt;2GB 拒绝</p>

      <div className="mt-3 min-h-0 flex-1 space-y-1.5 overflow-y-auto">
        {items.length === 0 ? (
          <EmptyState size="sm" title="没有传输任务" description="拖放文件到列表区即可上传。" />
        ) : (
          items.slice(0, 20).map((t) => {
            const meta = STATUS_META[t.status]
            return (
              <div key={t.id} className="rounded-ctl border border-border px-2.5 py-2">
                <div className="flex items-center gap-2">
                  <span className="min-w-0 flex-1 truncate text-[13px] text-text-1" title={t.fullPath}>{t.fileName}</span>
                  <span className="tnum shrink-0 text-[11px] text-text-4">{formatBytes(t.fileSize)}</span>
                  <StatusBadge tone={meta.tone} dot={false} className="shrink-0">{meta.label}</StatusBadge>
                  {t.status === 'failed' && (
                    <button type="button" aria-label="重试" onClick={() => retry(t.id)} className="rounded-ctl p-0.5 text-text-3 hover:text-primary outline-none">
                      <RotateCw className="size-3.5" aria-hidden />
                    </button>
                  )}
                </div>
                {t.status === 'uploading' && (
                  <div className="mt-1.5 h-1 overflow-hidden rounded-full bg-surface-2">
                    <div className="h-full rounded-full bg-primary transition-[width] duration-200" style={{ width: `${Math.round(t.progress * 100)}%` }} />
                  </div>
                )}
                {t.error && <p className="mt-1 text-[11px] text-crit">{t.error}</p>}
              </div>
            )
          })
        )}
      </div>

      {items.some((t) => t.status !== 'queued' && t.status !== 'uploading') && (
        <div className="mt-2 flex justify-end">
          <Chip onClick={clearFinished}>清除已完成</Chip>
        </div>
      )}
      <p className={cn('mt-1 text-[10px] text-text-4', items.length === 0 && 'hidden')}>
        历史保留最近 100 条（localStorage）
      </p>
    </Card>
  )
}
