import StatusBadge from '../../ui/StatusBadge';
import EmptyState from '../../ui/EmptyState';
import type { CalendarTasksTodayData, TaskResponse, TodaySection } from '../../types';

function validTimestamp(value?: string) {
  if (!value) return Number.POSITIVE_INFINITY;
  const time = new Date(value).getTime();
  return Number.isNaN(time) ? Number.POSITIVE_INFINITY : time;
}

function priorityDot(priority: number) {
  if (priority === 1) return 'bg-red-500';
  if (priority === 3) return 'bg-teal-500';
  return 'bg-amber-500';
}

function priorityLabel(priority: number) {
  if (priority === 1) return '高优先级';
  if (priority === 3) return '低优先级';
  return '普通优先级';
}

function dueTone(task: TaskResponse, todayPrefix: string) {
  if (!task.due) return 'neutral';
  const dueTime = new Date(task.due).getTime();
  if (Number.isNaN(dueTime)) return 'neutral';
  if (task.due.startsWith(todayPrefix)) return 'warning';
  return dueTime < new Date(`${todayPrefix}T00:00:00`).getTime() ? 'danger' : 'neutral';
}

function formatDue(value?: string) {
  if (!value) return '无截止';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '截止时间无效';
  return parsed.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function sortTasksByDue(tasks: TaskResponse[]) {
  return [...tasks].sort((a, b) => {
    const dueDelta = validTimestamp(a.due) - validTimestamp(b.due);
    if (dueDelta !== 0) return dueDelta;
    return a.title.localeCompare(b.title, 'zh-CN');
  });
}

export default function TodayTaskColumn({
  section,
  todayPrefix,
  onSelect,
}: {
  section: TodaySection<CalendarTasksTodayData>;
  todayPrefix: string;
  onSelect?: (task: TaskResponse) => void;
}) {
  const incompleteTasks = sortTasksByDue(
    Array.from(
      new Map(
        [
          ...section.data.overdueTasks,
          ...section.data.dueTodayTasks,
          ...section.data.unscheduledTasks,
        ].map(task => [task.id, task]),
      ).values(),
    ),
  );

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">待办任务</h2>
        <StatusBadge tone="neutral">{incompleteTasks.length} 项</StatusBadge>
      </div>

      {incompleteTasks.length === 0 ? (
        <EmptyState title="没有未完成任务" description="可以新建任务，或打开日历安排今天要推进的工作。" />
      ) : (
        // #285：长任务列表用独立滚动容器，内容再多也不撑大整页视口。
        // 高度按「同屏尽量多显示」调大（38rem ≈ 7~8 条），避免列表又矮又要频繁滚动。
        <div className="max-h-[38rem] space-y-2 overflow-y-auto pr-1">
          {incompleteTasks.map(task => (
            <button
              key={task.id}
              type="button"
              onClick={() => onSelect?.(task)}
              aria-label={`任务：${task.title}，${priorityLabel(task.priority)}，${formatDue(task.due)}`}
              className="w-full rounded-xl border border-slate-200 bg-white p-3 text-left transition-colors hover:border-blue-200 hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-blue-200"
            >
              <div className="flex items-start gap-2">
                <span
                  className={`mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full ${priorityDot(task.priority)}`}
                  aria-hidden="true"
                />
                <span className="sr-only">{priorityLabel(task.priority)}</span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">{task.title}</p>
                  {task.description && (
                    <p className="mt-1 line-clamp-2 text-xs leading-5 text-slate-500">{task.description}</p>
                  )}
                </div>
              </div>
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <StatusBadge tone={dueTone(task, todayPrefix)}>{formatDue(task.due)}</StatusBadge>
                {task.dtStart && <StatusBadge tone="activity">已排程</StatusBadge>}
                {task.plannedEnd && <StatusBadge tone="neutral">计划至 {formatDue(task.plannedEnd)}</StatusBadge>}
              </div>
            </button>
          ))}
        </div>
      )}
    </section>
  );
}
