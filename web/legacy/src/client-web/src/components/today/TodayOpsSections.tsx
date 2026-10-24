/**
 * 今日页「运营与状态」区块组件（2026-09-19）。
 *
 * 背景：服务端「今日注册表」规划 14 个模块，此前 Web 端只实现 6 个，其余 8 个
 * 要么被静默过滤、要么由 TodayPage 里的临时硬编码卡代替（同一功能两条路径）。
 * 本文件把 8 个模块正式接入注册表渲染管线：
 *   - 数据取自独立 API（待确认 / 微软同步 / 提醒队列 / 报告）；
 *   - 或直接渲染服务端 section 数据（设备端点 / 空闲窗口 / 习惯 / AI 占位）。
 * 区块不可用时由 TodaySectionHost 统一拦截（显示「暂不可用」），不会进入这里。
 */
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';

import { getOutlookSyncBatches, getReminders, getReports } from '../../api/calendar';
import { getPendingConfirmations } from '../../api/operations';
import { getDeferredAutoRefreshInterval } from '../../lib/autoRefresh';
import type { CalendarLayerItem, TodaySection } from '../../types';

function formatDateTime(value?: string | null) {
  if (!value) return 'Not available';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

type BadgeTone = 'amber' | 'blue' | 'emerald' | 'violet' | 'slate';
const badgeClass: Record<BadgeTone, string> = {
  amber: 'bg-amber-50 text-amber-700',
  blue: 'bg-blue-50 text-blue-700',
  emerald: 'bg-emerald-50 text-emerald-700',
  violet: 'bg-violet-50 text-violet-700',
  slate: 'bg-slate-100 text-slate-600',
};

const ROW_LIMIT = 3;

function SectionShell({
  title,
  subtitle,
  badge,
  tone = 'slate',
  children,
}: {
  title: string;
  subtitle: string;
  badge: string;
  tone?: BadgeTone;
  children: ReactNode;
}) {
  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-950">{title}</h2>
          <p className="mt-1 text-xs text-slate-500">{subtitle}</p>
        </div>
        <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${badgeClass[tone]}`}>{badge}</span>
      </div>
      <div className="mt-3 space-y-2">{children}</div>
    </section>
  );
}

function EmptyHint({ text }: { text: string }) {
  return (
    <p className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center text-xs text-slate-400">
      {text}
    </p>
  );
}

function LoadingHint() {
  return <p className="py-2 text-center text-xs text-slate-400">加载中…</p>;
}

/** 待确认（operations.confirmations）：数据源 /operations/pending-confirmations */
export function TodayConfirmationsSection() {
  const { data: items = [], isLoading } = useQuery({
    queryKey: ['today-pending-confirmations'],
    queryFn: getPendingConfirmations,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  return (
    <SectionShell title="待确认" subtitle={`${items.length} 个操作等待复核`} badge="复核" tone="amber">
      {isLoading && <LoadingHint />}
      {!isLoading && items.length === 0 && <EmptyHint text="暂无待确认操作。" />}
      {items.slice(0, ROW_LIMIT).map(item => (
        <div key={item.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{item.summary}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{item.riskLevel}</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">
            {item.source} / {item.operationType}
          </p>
        </div>
      ))}
    </SectionShell>
  );
}

/** 微软同步（sync.outlook）：数据源 /calendar/outlook/sync-batches */
export function TodaySyncOutlookSection() {
  const { data: batches = [], isLoading } = useQuery({
    queryKey: ['today-outlook-sync-batches'],
    queryFn: () => getOutlookSyncBatches(),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  return (
    <SectionShell title="微软同步" subtitle={`${batches.length} 个最近批次`} badge="同步" tone="blue">
      {isLoading && <LoadingHint />}
      {!isLoading && batches.length === 0 && <EmptyHint text="暂无微软同步批次。" />}
      {batches.slice(0, ROW_LIMIT).map(batch => (
        <div key={batch.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{batch.status}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{batch.failureCount} 个错误</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">
            {batch.provider} / 读取 {batch.readCount} / 确认 {batch.confirmationCount} / {formatDateTime(batch.startedAt)}
          </p>
        </div>
      ))}
    </SectionShell>
  );
}

/** 提醒队列（reminders.queue）：数据源 /calendar/reminders */
export function TodayRemindersSection() {
  const { data: reminders = [], isLoading } = useQuery({
    queryKey: ['today-reminders'],
    queryFn: getReminders,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  return (
    <SectionShell title="提醒队列" subtitle={`${reminders.length} 条提醒`} badge="提醒" tone="emerald">
      {isLoading && <LoadingHint />}
      {!isLoading && reminders.length === 0 && <EmptyHint text="暂无提醒。" />}
      {reminders.slice(0, ROW_LIMIT).map(reminder => (
        <div key={reminder.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{reminder.title}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{reminder.riskLevel}</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">
            {reminder.triggerReason || reminder.status}
            {reminder.scheduledAt ? ` · ${formatDateTime(reminder.scheduledAt)}` : ''}
          </p>
        </div>
      ))}
    </SectionShell>
  );
}

/** 报告（reports.available）：数据源 /calendar/reports */
export function TodayReportsSection() {
  const { data: reports = [], isLoading } = useQuery({
    queryKey: ['today-reports'],
    queryFn: getReports,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  return (
    <SectionShell title="报告" subtitle={`${reports.length} 份可用报告`} badge="报告" tone="violet">
      {isLoading && <LoadingHint />}
      {!isLoading && reports.length === 0 && <EmptyHint text="暂无报告。" />}
      {reports.slice(0, ROW_LIMIT).map(report => (
        <div key={report.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{report.title || report.kind}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{report.status || report.kind}</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">生成于 {formatDateTime(report.generatedAt)}</p>
        </div>
      ))}
    </SectionShell>
  );
}

/** 设备端点（endpoints.status）：渲染服务端 section 数据 */
export function TodayEndpointsSection({ section }: { section: TodaySection<unknown> }) {
  const data = (section.data ?? {}) as {
    endpointCount?: number;
    onlineOnlyBlockedCount?: number;
    items?: Array<{
      deviceId?: string;
      platform?: string;
      uploadStatus?: string;
      lastHeartbeatAt?: string | null;
    }>;
  };
  const items = data.items ?? [];
  const subtitle = `${data.endpointCount ?? items.length} 个端点${
    data.onlineOnlyBlockedCount ? ` · ${data.onlineOnlyBlockedCount} 个仅在线可见` : ''
  }`;

  return (
    <SectionShell title="设备端点" subtitle={subtitle} badge="端点" tone="slate">
      {items.length === 0 && <EmptyHint text="暂无设备端点。" />}
      {items.slice(0, ROW_LIMIT).map(item => (
        <div
          key={item.deviceId ?? item.platform ?? 'endpoint'}
          className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2"
        >
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{item.deviceId || '未知设备'}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{item.uploadStatus || ''}</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">
            {item.platform || '未知平台'} · {item.lastHeartbeatAt ? formatDateTime(item.lastHeartbeatAt) : '无心跳记录'}
          </p>
        </div>
      ))}
    </SectionShell>
  );
}

/** 空闲窗口 / 习惯（calendar.availability / calendar.habits）：渲染服务端 layer-count 数据 */
function LayerCountSection({
  section,
  title,
  badge,
  emptyText,
}: {
  section: TodaySection<unknown>;
  title: string;
  badge: string;
  emptyText: string;
}) {
  const data = (section.data ?? {}) as { count?: number; items?: CalendarLayerItem[] };
  const items = data.items ?? [];

  return (
    <SectionShell title={title} subtitle={`${data.count ?? items.length} 个条目`} badge={badge} tone="slate">
      {items.length === 0 && <EmptyHint text={emptyText} />}
      {items.slice(0, ROW_LIMIT).map(item => (
        <div key={item.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="flex items-start justify-between gap-2">
            <p className="min-w-0 truncate text-sm font-medium text-slate-800">{item.title}</p>
            <span className="shrink-0 text-[11px] font-semibold text-slate-500">{item.status}</span>
          </div>
          <p className="mt-1 truncate text-xs text-slate-500">
            {formatDateTime(item.startsAt)} → {formatDateTime(item.endsAt)}
          </p>
        </div>
      ))}
    </SectionShell>
  );
}

export function TodayAvailabilitySection({ section }: { section: TodaySection<unknown> }) {
  return <LayerCountSection section={section} title="空闲窗口" badge="空闲" emptyText="暂无空闲窗口。" />;
}

export function TodayHabitsSection({ section }: { section: TodaySection<unknown> }) {
  return <LayerCountSection section={section} title="习惯" badge="习惯" emptyText="暂无习惯。" />;
}

/** AI 占位（calendar.ai_placeholders）：服务端数据 {kind, count} */
export function TodayAiPlaceholdersSection({ section }: { section: TodaySection<unknown> }) {
  const data = (section.data ?? {}) as { count?: number };
  const count = data.count ?? 0;

  return (
    <SectionShell title="AI 占位" subtitle={`${count} 个待处理占位`} badge="AI" tone="slate">
      {count === 0 ? (
        <EmptyHint text="暂无 AI 占位。" />
      ) : (
        <p className="text-sm text-slate-600">共 {count} 个占位，等待生成或确认。</p>
      )}
    </SectionShell>
  );
}
