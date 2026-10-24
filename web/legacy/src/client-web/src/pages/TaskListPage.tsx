import { useEffect, useState, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  batchDeleteTasks,
  getTaskBooks,
  getTasksPaged,
  updateTask,
  taskToMutationData,
} from '../api/calendar';
import TaskHierarchyPanel from '../components/schedule/TaskHierarchyPanel';
import TaskSegmentEditor from '../components/schedule/TaskSegmentEditor';
import TaskEditorDialog from '../dialogs/TaskEditorDialog';
import MobilePageHeader from '../ui/MobilePageHeader';
import EmptyState from '../ui/EmptyState';
import StatusBadge from '../ui/StatusBadge';
import ConfirmActionDialog, { type DeleteConfirmationInput } from '../ui/ConfirmActionDialog';
import type { GetTasksParams, TaskMutationData } from '../api/calendar';
import type { CalendarOperationSample, TaskResponse } from '../types';

const filters = [
  { key: 'all', label: '全部' },
  { key: 'inbox', label: '收集箱' },
  { key: 'today', label: '今日截止' },
  { key: 'planned', label: '今日已安排' },
  { key: 'high', label: '高优先' },
  { key: 'completed', label: '已完成' },
] as const;

type TaskFilter = typeof filters[number]['key'];

const taskMutationInvalidationKeys = [
  ['tasks'],
  ['tasks-paged'],
  ['today-sections'],
  ['today-section'],
] as const;

const taskDeleteInvalidationKeys = [
  ['tasks'],
  ['tasks-paged'],
  ['calendar-recycle-bin'],
  ['today-sections'],
  ['today-section'],
] as const;

const emptyTasks: TaskResponse[] = [];

function formatLocalDate(date: Date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function useLocalDate() {
  const [dateStr, setDateStr] = useState(() => formatLocalDate(new Date()));

  useEffect(() => {
    const now = new Date();
    const nextMidnight = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
    const delayMs = nextMidnight.getTime() - now.getTime() + 1000;
    const timerId = window.setTimeout(() => setDateStr(formatLocalDate(new Date())), delayMs);

    return () => window.clearTimeout(timerId);
  }, [dateStr]);

  return dateStr;
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

function priorityTone(priority: number) {
  if (priority === 1) return 'danger';
  if (priority === 3) return 'activity';
  return 'warning';
}

function statusLabel(status: string) {
  if (status === 'COMPLETED') return '已完成';
  if (status === 'NEEDS-ACTION') return '待处理';
  return status || '未设置';
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

function getErrorMessage(error: unknown) {
  if (error instanceof Error && error.message) return error.message;
  return '删除失败，请稍后再试。';
}

function pruneSelectedIds(selected: Set<string>, visibleIds: string[]) {
  const visibleIdSet = new Set(visibleIds);
  return new Set(Array.from(selected).filter(id => visibleIdSet.has(id)));
}

function hasStaleSelection(selected: Set<string>, visibleIds: string[]) {
  if (selected.size === 0) return false;
  const visibleIdSet = new Set(visibleIds);
  return Array.from(selected).some(id => !visibleIdSet.has(id));
}

function buildTaskQuery(
  filter: TaskFilter,
  search: string,
  calendarId: string,
  todayStr: string,
): GetTasksParams {
  const todayStart = `${todayStr}T00:00:00`;
  const todayEnd = `${todayStr}T23:59:59`;
  const query: GetTasksParams = {
    page: 1,
    pageSize: 100,
  };
  const normalizedSearch = search.trim();

  if (normalizedSearch) query.search = normalizedSearch;
  if (calendarId) query.calendarId = calendarId;
  if (filter === 'inbox') query.inbox = true;
  if (filter === 'high') query.priority = 1;
  if (filter === 'completed') query.status = 'COMPLETED';
  if (filter === 'planned') {
    query.plannedFrom = todayStart;
    query.plannedTo = todayEnd;
  }
  if (filter === 'today') {
    query.dueFrom = todayStart;
    query.dueTo = todayEnd;
  }

  return query;
}

function toTaskSample(
  task: TaskResponse,
  taskBookNameById: Map<string, string>,
): CalendarOperationSample {
  return {
    id: task.id,
    type: 'task',
    title: task.title,
    start: task.dtStart,
    end: task.plannedEnd || task.due,
    bookName: task.calendarId ? taskBookNameById.get(task.calendarId) : undefined,
  };
}

export default function TaskListPage() {
  const todayStr = useLocalDate();
  const [filter, setFilter] = useState<TaskFilter>('all');
  const [search, setSearch] = useState('');
  const [selectedTaskBook, setSelectedTaskBook] = useState('');
  const [searchParams, setSearchParams] = useSearchParams();

  useEffect(() => {
    const urlTaskBook = searchParams.get('taskBookId') || searchParams.get('calendarId');
    if (urlTaskBook && urlTaskBook !== selectedTaskBook) {
      setSelectedTaskBook(urlTaskBook);
    }
  }, [searchParams, selectedTaskBook]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(() => new Set());
  const [pendingDeleteIds, setPendingDeleteIds] = useState<string[]>([]);
  const [deleteInput, setDeleteInput] = useState<DeleteConfirmationInput | null>(null);
  const [deleteErrorMessage, setDeleteErrorMessage] = useState<string | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingTask, setEditingTask] = useState<TaskResponse | undefined>();
  const [selectedHierarchyTask, setSelectedHierarchyTask] = useState<TaskResponse | undefined>();
  const [segmentTask, setSegmentTask] = useState<TaskResponse | null>(null);
  const queryClient = useQueryClient();

  function invalidateKeys(keys: typeof taskMutationInvalidationKeys | typeof taskDeleteInvalidationKeys) {
    for (const queryKey of keys) {
      queryClient.invalidateQueries({ queryKey });
    }
  }

  const { data: taskBooks = [], isLoading: taskBooksLoading } = useQuery({
    queryKey: ['task-books'],
    queryFn: () => getTaskBooks(),
  });

  const taskQuery = useMemo(
    () => buildTaskQuery(filter, search, selectedTaskBook, todayStr),
    [filter, search, selectedTaskBook, todayStr],
  );

  const { data, isLoading } = useQuery({
    queryKey: ['tasks-paged', filter, search, selectedTaskBook, todayStr],
    queryFn: () => getTasksPaged(taskQuery),
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: TaskMutationData }) =>
      updateTask(id, data),
    onSuccess: () => invalidateKeys(taskMutationInvalidationKeys),
  });

  const deleteMutation = useMutation({
    mutationFn: (ids: string[]) => batchDeleteTasks(ids),
    onSuccess: () => {
      setSelectedIds(new Set());
      setPendingDeleteIds([]);
      setDeleteInput(null);
      setDeleteErrorMessage(null);
      invalidateKeys(taskDeleteInvalidationKeys);
    },
    onError: error => {
      setPendingDeleteIds([]);
      setDeleteInput(null);
      setDeleteErrorMessage(getErrorMessage(error));
    },
  });

  const items = data?.items ?? emptyTasks;
  const totalCount = data?.totalCount ?? items.length;
  const currentIds = useMemo(() => items.map(task => task.id), [items]);
  const allCurrentSelected = currentIds.length > 0 && currentIds.every(id => selectedIds.has(id));
  const taskBookNameById = useMemo(
    () => new Map(taskBooks.map(book => [book.id, book.name])),
    [taskBooks],
  );

  useEffect(() => {
    if (!hasStaleSelection(selectedIds, currentIds)) return;

    let cancelled = false;
    window.queueMicrotask(() => {
      if (!cancelled) setSelectedIds(current => pruneSelectedIds(current, currentIds));
    });

    return () => {
      cancelled = true;
    };
  }, [currentIds, selectedIds]);

  function clearSelectionState() {
    setSelectedIds(new Set());
    setPendingDeleteIds([]);
    setDeleteInput(null);
    setDeleteErrorMessage(null);
  }

  function handleFilterChange(nextFilter: TaskFilter) {
    setFilter(nextFilter);
    clearSelectionState();
  }

  function handleSearchChange(nextSearch: string) {
    setSearch(nextSearch);
    clearSelectionState();
  }

  function handleTaskBookChange(nextTaskBook: string) {
    setSelectedTaskBook(nextTaskBook);
    clearSelectionState();
    if (nextTaskBook) {
      searchParams.set('taskBookId', nextTaskBook);
    } else {
      searchParams.delete('taskBookId');
      searchParams.delete('calendarId');
    }
    setSearchParams(searchParams, { replace: true });
  }

  function toggleTaskSelection(taskId: string, checked: boolean) {
    setSelectedIds(current => {
      const next = new Set(current);
      if (checked) next.add(taskId);
      else next.delete(taskId);
      return next;
    });
  }

  function toggleCurrentResultSelection() {
    if (currentIds.length === 0) return;

    setSelectedIds(current => {
      const next = new Set(current);
      if (allCurrentSelected) currentIds.forEach(id => next.delete(id));
      else currentIds.forEach(id => next.add(id));
      return next;
    });
  }

  function requestDeleteSelected() {
    const selectedTasks = items.filter(task => selectedIds.has(task.id));
    if (selectedTasks.length === 0) return;

    const visibleSelectedIds = selectedTasks.map(task => task.id);
    deleteMutation.reset();
    setPendingDeleteIds(visibleSelectedIds);
    setDeleteErrorMessage(null);
    setDeleteInput({
      targetType: 'task',
      title: '选中的任务',
      affectedCount: visibleSelectedIds.length,
      samples: selectedTasks.slice(0, 5).map(task => toTaskSample(task, taskBookNameById)),
    });
  }

  function confirmDeleteSelected() {
    const ids = pendingDeleteIds;
    if (ids.length === 0) return;
    deleteMutation.mutate(ids);
  }

  function cancelDelete() {
    if (deleteMutation.isPending) return;
    setPendingDeleteIds([]);
    setDeleteInput(null);
  }

  function closeEditor() {
    setEditorOpen(false);
    invalidateKeys(taskMutationInvalidationKeys);
  }

  function selectHierarchyTask(task: TaskResponse) {
    setSelectedHierarchyTask(task);
    setEditingTask(task);
  }

  if (isLoading) return <div className="p-4 text-sm text-slate-500">加载中...</div>;

  return (
    <div className="mx-auto max-w-[1400px] space-y-4 overflow-auto pb-20 md:pb-4">
      <MobilePageHeader title="任务清单" />
      <div data-contract="Checklist" className="sr-only">Checklist</div>
      <section className="pim-panel p-4">
        <div className="flex flex-wrap gap-2">
          {filters.map(f => (
            <button
              key={f.key}
              type="button"
              onClick={() => handleFilterChange(f.key)}
              className={`min-h-[44px] rounded-full border px-3 py-2 text-sm font-medium transition-colors ${
                filter === f.key
                  ? 'border-blue-600 bg-blue-600 text-white'
                  : 'border-slate-200 bg-white text-slate-600 hover:border-blue-200 hover:bg-slate-50'
              }`}
            >
              {f.label}
            </button>
          ))}
        </div>

        <div className="mt-4 grid gap-3 md:grid-cols-[1fr_220px]">
          <label className="block">
            <span className="sr-only">搜索任务</span>
            <input
              type="text"
              placeholder="搜索任务..."
              value={search}
              onChange={e => handleSearchChange(e.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
            />
          </label>

          <label className="block">
            <span className="sr-only">任务本</span>
            <select
              value={selectedTaskBook}
              onChange={e => handleTaskBookChange(e.target.value)}
              disabled={taskBooksLoading}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition-colors focus:border-blue-300 focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
            >
              <option value="">全部任务本</option>
              {taskBooks.map(book => (
                <option key={book.id} value={book.id}>{book.name}</option>
              ))}
            </select>
          </label>
        </div>

        <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
          <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
            <StatusBadge tone="neutral">显示前 {items.length} 项 / 共 {totalCount} 项</StatusBadge>
            {selectedIds.size > 0 && <StatusBadge tone="primary">已选 {selectedIds.size} 项</StatusBadge>}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={toggleCurrentResultSelection}
              disabled={currentIds.length === 0}
              className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-blue-200 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {allCurrentSelected ? '取消全选' : '全选当前结果'}
            </button>
            <button
              type="button"
              onClick={requestDeleteSelected}
              disabled={selectedIds.size === 0 || deleteMutation.isPending}
              className="rounded-lg border border-red-200 bg-red-50 px-3 py-1.5 text-xs font-medium text-red-600 transition-colors hover:bg-red-100 disabled:cursor-not-allowed disabled:opacity-50"
            >
              删除选中
            </button>
          </div>
        </div>

        {deleteErrorMessage && (
          <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {deleteErrorMessage}
          </div>
        )}
      </section>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[320px_1fr]">
        <TaskHierarchyPanel
          tasks={items}
          selectedTaskId={selectedHierarchyTask?.id}
          onSelectTask={selectHierarchyTask}
        />
        <TaskSegmentEditor
          task={segmentTask ?? selectedHierarchyTask ?? items[0] ?? null}
          onClose={() => setSegmentTask(null)}
        />
      </div>

      {items.length === 0 ? (
        <EmptyState title="没有任务" description="调整筛选或搜索条件后再看看。" />
      ) : (
        <div className="space-y-2">
          {items.map(task => (
            <article
              key={task.id}
              className="pim-card flex items-center gap-3 p-3 transition-colors hover:border-blue-200 hover:bg-slate-50"
            >
              <label className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-slate-200 bg-white">
                <span className="sr-only">选择任务：{task.title}</span>
                <input
                  type="checkbox"
                  checked={selectedIds.has(task.id)}
                  onChange={e => toggleTaskSelection(task.id, e.target.checked)}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-200"
                />
              </label>

              <button
                type="button"
                className="flex min-w-0 flex-1 items-center gap-3 rounded-xl text-left focus:outline-none focus:ring-2 focus:ring-blue-200"
                onClick={() => { setEditingTask(task); setEditorOpen(true); }}
                aria-label={`打开任务：${task.title}，${priorityLabel(task.priority)}，${formatDue(task.due)}`}
              >
                <span
                  className={`h-9 w-1 shrink-0 rounded-full ${priorityDot(task.priority)}`}
                  aria-hidden="true"
                />
                <span className="sr-only">{priorityLabel(task.priority)}</span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">{task.title}</p>
                  <div className="mt-2 flex flex-wrap items-center gap-2">
                    <StatusBadge tone={priorityTone(task.priority)}>{priorityLabel(task.priority)}</StatusBadge>
                    <StatusBadge tone={task.status === 'COMPLETED' ? 'activity' : 'neutral'}>
                      {statusLabel(task.status)}
                    </StatusBadge>
                    <StatusBadge tone="neutral">{formatDue(task.due)}</StatusBadge>
                    {task.dtStart && <StatusBadge tone="primary">已安排 {formatDue(task.dtStart)}</StatusBadge>}
                    {task.isInbox && <StatusBadge tone="warning">收集箱</StatusBadge>}
                  </div>
                </div>
              </button>
              <button
                type="button"
                onClick={() => {
                  toggleMutation.mutate({
                    id: task.id,
                    data: taskToMutationData(task, {
                      status: task.status === 'COMPLETED' ? 'NEEDS-ACTION' : 'COMPLETED',
                    }),
                  });
                }}
                className={`shrink-0 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors ${
                  task.status === 'COMPLETED'
                    ? 'border-teal-200 bg-teal-50 text-teal-700'
                    : 'border-slate-200 bg-white text-slate-600 hover:border-blue-200 hover:bg-slate-50'
                }`}
              >
                {task.status === 'COMPLETED' ? '已完成' : '标记完成'}
              </button>
              <button
                type="button"
                onClick={() => {
                  setSelectedHierarchyTask(task);
                  setSegmentTask(task);
                }}
                className="shrink-0 rounded-full border border-blue-200 bg-blue-50 px-3 py-1.5 text-xs font-medium text-blue-700 transition-colors hover:bg-blue-100"
              >
                时间段
              </button>
            </article>
          ))}
        </div>
      )}

      <TaskEditorDialog
        open={editorOpen}
        onClose={closeEditor}
        task={editingTask}
      />
      <ConfirmActionDialog
        open={deleteInput !== null}
        input={deleteInput}
        isPending={deleteMutation.isPending}
        onCancel={cancelDelete}
        onConfirm={confirmDeleteSelected}
      />
    </div>
  );
}
