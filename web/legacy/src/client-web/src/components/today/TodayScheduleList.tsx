import StatusBadge from '../../ui/StatusBadge';
import EmptyState from '../../ui/EmptyState';
import type { CalendarScheduleTodayData, EventResponse, TaskResponse, TodaySection } from '../../types';

export type ScheduledItem =
  | {
      type: 'event';
      id: string;
      event: EventResponse;
      title: string;
      start: string;
      end?: string;
      meta?: string;
      color?: string;
    }
  | {
      type: 'task';
      id: string;
      task: TaskResponse;
      title: string;
      start: string;
      end?: string;
      meta?: string;
      priority: number;
    };

function safeTime(value?: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return null;
  return parsed;
}

function formatTime(value?: string) {
  const parsed = safeTime(value);
  return parsed
    ? parsed.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
    : '时间未知';
}

function priorityBorder(priority: number) {
  if (priority === 1) return 'border-l-red-500';
  if (priority === 3) return 'border-l-teal-500';
  return 'border-l-amber-500';
}

function priorityLabel(priority: number) {
  if (priority === 1) return '高优先级';
  if (priority === 3) return '低优先级';
  return '普通优先级';
}

export function buildScheduledItems(
  events: EventResponse[],
  tasks: TaskResponse[],
  datePrefix: string,
): ScheduledItem[] {
  const eventItems: ScheduledItem[] = events.map(event => ({
    type: 'event',
    id: event.id,
    event,
    title: event.title,
    start: event.dtStart,
    end: event.dtEnd,
    meta: event.location || event.description || '日程',
  }));

  const taskItems: ScheduledItem[] = tasks
    .filter(task => task.dtStart?.startsWith(datePrefix))
    .map(task => ({
      type: 'task',
      id: task.id,
      task,
      title: task.title,
      start: task.dtStart!,
      meta: task.description || '已排程任务',
      priority: task.priority,
    }));

  return [...eventItems, ...taskItems].sort((a, b) => {
    const aTime = safeTime(a.start)?.getTime() ?? Number.POSITIVE_INFINITY;
    const bTime = safeTime(b.start)?.getTime() ?? Number.POSITIVE_INFINITY;
    return aTime - bTime;
  });
}

export default function TodayScheduleList({
  section,
  onSelect,
}: {
  section: TodaySection<CalendarScheduleTodayData>;
  onSelect?: (item: ScheduledItem) => void;
}) {
  const eventItems: ScheduledItem[] = section.data.events.map(event => ({
    type: 'event',
    id: event.id,
    event,
    title: event.title,
    start: event.dtStart,
    end: event.dtEnd,
    meta: event.location || event.description || '日程',
  }));
  const taskItems: ScheduledItem[] = section.data.scheduledTasks.map(task => ({
    type: 'task',
    id: task.id,
    task,
    title: task.title,
    start: task.dtStart!,
    meta: task.description || '已排程任务',
    priority: task.priority,
  }));
  const items = [...eventItems, ...taskItems].sort((a, b) => {
    const aTime = safeTime(a.start)?.getTime() ?? Number.POSITIVE_INFINITY;
    const bTime = safeTime(b.start)?.getTime() ?? Number.POSITIVE_INFINITY;
    return aTime - bTime;
  });

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">今日安排</h2>
        <StatusBadge tone="neutral">{items.length} 项</StatusBadge>
      </div>

      {items.length === 0 ? (
        <EmptyState title="今天还没有安排" description="日程和已排程任务会显示在这里。" />
      ) : (
        <div className="space-y-2">
          {items.map(item => {
            const itemLabel = item.type === 'task' ? `任务，${priorityLabel(item.priority)}` : '日程';
            const canSelect = Boolean(onSelect);
            const interactionClass = canSelect
              ? 'cursor-pointer transition-colors hover:bg-white focus:outline-none focus:ring-2 focus:ring-blue-200'
              : 'cursor-default opacity-90';

            return (
              <button
                key={`${item.type}-${item.id}`}
                type="button"
                onClick={() => {
                  if (canSelect) onSelect?.(item);
                }}
                aria-label={`${itemLabel}：${item.title}，${formatTime(item.start)}`}
                disabled={!canSelect}
                className={`w-full rounded-xl border border-slate-200 border-l-4 bg-slate-50 p-3 text-left ${
                  item.type === 'task' ? priorityBorder(item.priority) : 'border-l-blue-500'
                } ${interactionClass}`}
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-slate-900">{item.title}</p>
                    {item.meta && <p className="mt-1 truncate text-xs text-slate-500">{item.meta}</p>}
                  </div>
                  <StatusBadge tone={item.type === 'task' ? 'activity' : 'primary'}>
                    {item.type === 'task' ? '任务' : '日程'}
                  </StatusBadge>
                </div>
                {item.type === 'task' && <span className="sr-only">{priorityLabel(item.priority)}</span>}
                <p className="mt-3 text-xs font-medium text-slate-600">
                  {formatTime(item.start)}
                  {item.end ? ` - ${formatTime(item.end)}` : ''}
                </p>
              </button>
            );
          })}
        </div>
      )}
    </section>
  );
}
