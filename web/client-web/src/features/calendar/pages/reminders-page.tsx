import { useMemo, useState } from 'react'
import { BellOff, Clock, Inbox } from 'lucide-react'
import { useDeliveryLog, useReminderActions, useReminders } from '../queries'
import type { ReminderResponse } from '../types'
import { normalizeRiskLevel, RISK_LABEL, riskTone } from '@/lib/enums'
import { formatTime } from '@/lib/datetime'
import { Button, Card, Chip, EmptyState, PageHeader, Segmented, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

type Tab = 'pending' | 'rules' | 'history'

/** 提醒页（02 §reminders：待提醒/规则/发送历史三标签） */
export function RemindersPage() {
  const [tab, setTab] = useState<Tab>('pending')
  const { data: all = [], isLoading } = useReminders()
  const { data: log = [] } = useDeliveryLog(tab === 'history')
  const actions = useReminderActions()

  const pending = useMemo(
    () => all.filter((r) => r.status !== 'Dismissed').sort((a, b) => a.scheduledAt.localeCompare(b.scheduledAt)),
    [all],
  )

  const [range, setRange] = useState<'all' | 'today' | 'overdue'>('all')
  const filtered = useMemo(() => {
    const now = new Date()
    const todayStr = now.toISOString().slice(0, 10)
    return pending.filter((r) => {
      if (range === 'today') return r.scheduledAt.slice(0, 10) === todayStr
      if (range === 'overdue') return new Date(r.scheduledAt).getTime() < now.getTime()
      return true
    })
  }, [pending, range])

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader
        title="提醒"
        subtitle={`待提醒 ${pending.length} 条`}
        actions={<Segmented value={tab} onValueChange={setTab} options={[
          { value: 'pending', label: '待提醒' },
          { value: 'rules', label: '规则' },
          { value: 'history', label: '发送历史' },
        ]} />}
      />

      {tab === 'pending' && (
        <>
          <div className="mb-3 flex gap-1.5">
            {(
              [
                { v: 'all', label: '全部' },
                { v: 'today', label: '今天' },
                { v: 'overdue', label: '逾期' },
              ] as const
            ).map((o) => (
              <Chip key={o.v} active={range === o.v} onClick={() => setRange(o.v)}>{o.label}</Chip>
            ))}
          </div>

          {isLoading ? (
            <div className="space-y-2">{Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="h-28" />)}</div>
          ) : filtered.length === 0 ? (
            <Card>
              <EmptyState icon={Inbox} title="没有待提醒" description="提醒队列是空的。" />
            </Card>
          ) : (
            <div className="space-y-3">
              {filtered.map((r) => (
                <ReminderCard key={r.id} reminder={r} actions={actions} />
              ))}
            </div>
          )}
        </>
      )}

      {tab === 'rules' && (
        <Card>
          <EmptyState icon={BellOff} title="提醒规则" description="规则引擎产生的提醒策略将在这里展示与配置（规则数据由后端规则引擎生成）。" />
        </Card>
      )}

      {tab === 'history' && (
        <Card className="divide-y divide-divider">
          {log.length === 0 ? (
            <EmptyState size="sm" title="暂无投递记录" />
          ) : (
            log.map((d) => (
              <div key={d.id} className="flex items-center gap-3 px-4 py-2.5 text-[13px]">
                <StatusBadge tone={d.status === 'Executed' ? 'ok' : d.status === 'OpenDetailRequired' ? 'warn' : 'neutral'} dot={false}>
                  {d.status}
                </StatusBadge>
                <span className="text-text-2">{d.channel}</span>
                <span className="ml-auto tnum text-text-4">{formatTime(d.createdAt)}</span>
              </div>
            ))
          )}
        </Card>
      )}
    </div>
  )
}

function ReminderCard({
  reminder,
  actions,
}: {
  reminder: ReminderResponse
  actions: ReturnType<typeof useReminderActions>
}) {
  const risk = normalizeRiskLevel(reminder.riskLevel)
  const overdue = new Date(reminder.scheduledAt).getTime() < Date.now()
  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-sm font-semibold text-text-1">{reminder.title}</span>
        <StatusBadge tone={riskTone(risk)}>{RISK_LABEL[risk]}</StatusBadge>
        <StatusBadge tone={reminder.status === 'Open' ? 'info' : 'neutral'} dot={false}>{reminder.status}</StatusBadge>
      </div>
      {reminder.body && <p className="mt-1 text-[13px] text-text-2">{reminder.body}</p>}

      {/* 4 瓦片信息格（触发原因/通道/勿扰/升级——后两项目前后端未返回则留占位） */}
      <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
        <InfoTile label="触发原因" value={reminder.triggerReason || '—'} />
        <InfoTile label="通道" value={reminder.channels.join('、') || '—'} />
        <InfoTile label="勿扰窗口" value={reminder.doNotDisturbStart ? '已设置' : '—'} />
        <InfoTile label="升级策略" value="—" />
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <span className={cn('tnum text-xs', overdue ? 'text-crit' : 'text-text-3')}>
          <Clock className="mr-1 inline size-3" aria-hidden />
          {formatTime(reminder.scheduledAt)}
          {overdue && ' · 已逾期'}
        </span>
        <div className="ml-auto flex gap-2">
          <Button
            variant="secondary"
            size="sm"
            loading={actions.snooze.isPending}
            onClick={() => void actions.snooze.mutateAsync({ id: reminder.id })}
          >
            稍后提醒
          </Button>
          <Button
            variant="ghost"
            size="sm"
            loading={actions.dismiss.isPending}
            onClick={() => void actions.dismiss.mutateAsync(reminder.id)}
          >
            忽略
          </Button>
        </div>
      </div>
    </Card>
  )
}

function InfoTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-ctl bg-surface px-2.5 py-1.5">
      <div className="text-[11px] text-text-4">{label}</div>
      <div className="truncate text-xs text-text-2">{value}</div>
    </div>
  )
}
