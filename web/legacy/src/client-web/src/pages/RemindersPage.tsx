import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  dismissReminder,
  getReminderDeliveryLog,
  getReminders,
  handleReminderAction,
  snoozeReminder,
} from '../api/calendar';
import type { ReminderDelivery, ReminderSummary } from '../types';
import PageHeader from '../ui/PageHeader';
import MobilePageHeader from '../ui/MobilePageHeader';
import SegmentedControl from '../ui/SegmentedControl';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

type ReminderTab = 'due' | 'rules' | 'delivery';

const reminderTabs: Array<{ value: ReminderTab; label: string }> = [
  { value: 'due', label: '待提醒' },
  { value: 'rules', label: '规则' },
  { value: 'delivery', label: '发送历史' },
];

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function isSameBusinessDate(value?: string) {
  if (!value) return false;
  const date = new Date(value);
  const now = new Date();
  return date.getFullYear() === now.getFullYear()
    && date.getMonth() === now.getMonth()
    && date.getDate() === now.getDate();
}

function isWithinWeek(value?: string) {
  if (!value) return false;
  const date = new Date(value);
  const now = new Date();
  const end = new Date(now);
  end.setDate(now.getDate() + 7);
  return date >= now && date <= end;
}

function isOverdue(value?: string) {
  if (!value) return false;
  return new Date(value).getTime() < Date.now();
}

function deliveryMatchesReminder(delivery: ReminderDelivery, reminder: ReminderSummary) {
  return delivery.reminderId === reminder.id;
}

export default function RemindersPage() {
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<ReminderTab>('due');
  const [horizon, setHorizon] = useState('today');
  const [channel, setChannel] = useState('all');
  const [status, setStatus] = useState('open');

  const { data: reminders = [], isLoading } = useQuery({
    queryKey: ['reminders'],
    queryFn: getReminders,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: deliveryLog = [] } = useQuery({
    queryKey: ['reminder-delivery-log'],
    queryFn: getReminderDeliveryLog,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const actionMutation = useMutation({
    mutationFn: ({ id, action }: { id: string; action: string }) => handleReminderAction(id, action),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['reminders'] });
      queryClient.invalidateQueries({ queryKey: ['reminder-delivery-log'] });
    },
  });

  const snoozeMutation = useMutation({
    mutationFn: (id: string) => snoozeReminder(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['reminders'] }),
  });

  const dismissMutation = useMutation({
    mutationFn: dismissReminder,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['reminders'] }),
  });

  const filteredReminders = useMemo(() => reminders.filter(reminder => {
    const horizonMatches = horizon === 'today'
      ? isSameBusinessDate(reminder.scheduledAt)
      : horizon === 'week'
        ? isWithinWeek(reminder.scheduledAt)
        : horizon === 'overdue'
          ? isOverdue(reminder.scheduledAt)
          : true;
    const channelMatches = channel === 'all'
      || reminder.channels.some(item => item.toLowerCase() === channel);
    const statusMatches = status === 'all'
      || reminder.status.toLowerCase() === status;

    return horizonMatches && channelMatches && statusMatches;
  }), [channel, horizon, reminders, status]);

  const responseLog = deliveryLog.filter(item => item.respondedAt);

  return (
    <div className="mx-auto w-full max-w-[1300px] space-y-4 overflow-auto pb-20 md:pb-4">
      <MobilePageHeader title="提醒中心" />
      <PageHeader
        title="提醒中心"
        subtitle="统一处理日程、任务、确认和报告的提醒触发原因、通知渠道、DND 与发送历史。"
        actions={<SegmentedControl value={tab} options={reminderTabs} onChange={setTab} ariaLabel="提醒视图" />}
      />

      <section className="pim-panel p-4">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
          <label>
            <span className="text-xs font-semibold text-slate-500">时间范围</span>
            <select value={horizon} onChange={event => setHorizon(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="today">今天</option>
              <option value="week">未来 7 天</option>
              <option value="overdue">已过期</option>
              <option value="all">全部</option>
            </select>
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">通知渠道</span>
            <select value={channel} onChange={event => setChannel(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="all">全部渠道</option>
              <option value="desktop">桌面</option>
              <option value="email">邮件</option>
              <option value="web">Web</option>
              <option value="android">Android</option>
            </select>
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">状态</span>
            <select value={status} onChange={event => setStatus(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="open">待处理</option>
              <option value="snoozed">已稍后提醒</option>
              <option value="sent">已发送</option>
              <option value="dismissed">已忽略</option>
              <option value="all">全部状态</option>
            </select>
          </label>
        </div>
      </section>

      {tab === 'delivery' ? (
        <section className="pim-panel p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-slate-950">发送历史</h2>
            <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
              {deliveryLog.length} 条记录
            </span>
          </div>
          <div className="mt-4 grid gap-2">
            {deliveryLog.map(delivery => (
              <article key={delivery.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h3 className="text-sm font-semibold text-slate-900">{delivery.channel} / {delivery.status}</h3>
                  <span className="text-xs text-slate-500">{formatDateTime(delivery.createdAt)}</span>
                </div>
                <p className="mt-2 break-words text-xs text-slate-500">{delivery.payloadJson || '无发送载荷'}</p>
                {delivery.respondedAt && (
                  <p className="mt-2 text-xs font-semibold text-emerald-700">响应历史：{formatDateTime(delivery.respondedAt)}</p>
                )}
              </article>
            ))}
            {deliveryLog.length === 0 && (
              <p className="rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
                暂无发送历史。
              </p>
            )}
          </div>
        </section>
      ) : (
        <section className="pim-panel p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-slate-950">{tab === 'due' ? '提醒队列' : '提醒规则'}</h2>
            <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
              {horizon} / {channel} / {status}
            </span>
          </div>

          {isLoading ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              正在加载提醒。
            </p>
          ) : filteredReminders.length === 0 ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              当前筛选下没有提醒记录。
            </p>
          ) : (
            <div className="mt-4 grid gap-3">
              {filteredReminders.map(reminder => {
                const deliveries = [
                  ...(reminder.deliveryHistory ?? []),
                  ...deliveryLog.filter(delivery => deliveryMatchesReminder(delivery, reminder)),
                ];
                const responses = [
                  ...(reminder.responseHistory ?? []),
                  ...deliveries.filter(delivery => delivery.respondedAt),
                ];
                const relatedUrl = reminder.relatedObjectType && reminder.relatedObjectId
                  ? `/audit/${encodeURIComponent(reminder.relatedObjectType)}/${encodeURIComponent(reminder.relatedObjectId)}`
                  : null;

                return (
                  <article key={reminder.id} className="rounded-lg border border-slate-200 bg-white p-4">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div className="min-w-0">
                        <h3 className="truncate text-sm font-semibold text-slate-950">{reminder.title}</h3>
                        <p className="mt-1 text-xs leading-5 text-slate-500">{reminder.body || '无正文'}</p>
                      </div>
                      <span className="rounded-full bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-700">
                        {reminder.riskLevel}
                      </span>
                    </div>

                    <div className="mt-3 grid grid-cols-1 gap-2 md:grid-cols-2 xl:grid-cols-4">
                      <div className="rounded-lg bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-400">触发原因</p>
                        <p className="mt-1 text-sm text-slate-700">{reminder.triggerReason || '规则触发'}</p>
                      </div>
                      <div className="rounded-lg bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-400">通知渠道</p>
                        <p className="mt-1 text-sm text-slate-700">{reminder.channels.join(' / ') || '未配置'}</p>
                      </div>
                      <div className="rounded-lg bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-400">DND</p>
                        <p className="mt-1 text-sm text-slate-700">
                          {reminder.doNotDisturbStart || reminder.doNotDisturbEnd
                            ? `${reminder.doNotDisturbStart ?? '开始'} - ${reminder.doNotDisturbEnd ?? '结束'}`
                            : '未启用'}
                        </p>
                      </div>
                      <div className="rounded-lg bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-400">升级策略</p>
                        <p className="mt-1 text-sm text-slate-700">{reminder.escalationPolicy || '高风险打开确认详情'}</p>
                      </div>
                    </div>

                    <div className="mt-3 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                      <span>计划时间：{formatDateTime(reminder.scheduledAt)}</span>
                      <span>状态：{reminder.status}</span>
                      {relatedUrl && (
                        <Link to={relatedUrl} className="font-semibold text-blue-600 hover:text-blue-700">
                          关联对象
                        </Link>
                      )}
                    </div>

                    <div className="mt-3 grid grid-cols-1 gap-3 lg:grid-cols-2">
                      <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-500">发送历史</p>
                        <p className="mt-1 text-xs text-slate-500">
                          {deliveries.length > 0
                            ? deliveries.slice(0, 3).map(item => `${item.channel}:${item.status}`).join(' / ')
                            : '暂无发送记录'}
                        </p>
                      </div>
                      <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                        <p className="text-xs font-semibold text-slate-500">响应历史</p>
                        <p className="mt-1 text-xs text-slate-500">
                          {responses.length > 0
                            ? responses.slice(0, 3).map(item => formatDateTime(item.respondedAt)).join(' / ')
                            : '暂无用户响应'}
                        </p>
                      </div>
                    </div>

                    <div className="mt-3 flex flex-wrap items-center gap-2">
                      <span className="text-xs font-semibold text-slate-500">操作按钮</span>
                      <button
                        type="button"
                        onClick={() => actionMutation.mutate({ id: reminder.id, action: 'open' })}
                        disabled={actionMutation.isPending}
                        className="pim-button-secondary min-h-[44px] px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        打开详情
                      </button>
                      <button
                        type="button"
                        onClick={() => snoozeMutation.mutate(reminder.id)}
                        disabled={snoozeMutation.isPending}
                        className="pim-button-secondary min-h-[44px] px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        稍后提醒
                      </button>
                      <button
                        type="button"
                        onClick={() => dismissMutation.mutate(reminder.id)}
                        disabled={dismissMutation.isPending}
                        className="pim-button-secondary min-h-[44px] px-3 py-2 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        忽略
                      </button>
                    </div>
                  </article>
                );
              })}
            </div>
          )}
        </section>
      )}

      {responseLog.length > 0 && (
        <section className="pim-panel p-4">
          <h2 className="text-sm font-semibold text-slate-950">用户响应历史</h2>
          <div className="mt-3 flex flex-wrap gap-2">
            {responseLog.slice(0, 8).map(item => (
              <span key={item.id} className="rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-700">
                {item.channel} · {formatDateTime(item.respondedAt)}
              </span>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
