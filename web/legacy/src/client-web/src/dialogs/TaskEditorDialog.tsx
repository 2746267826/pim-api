import { X } from 'lucide-react';
import { useState, useRef, useEffect, useId, type FormEvent } from 'react';
import { useMutation, useQueryClient, useQuery, type QueryClient } from '@tanstack/react-query';
import { createTask, updateTask, deleteTask, getCalendars, getTaskBooks, addTaskChecklistItem, deleteTaskChecklistItem, updateTaskChecklistItem, taskToMutationData } from '../api/calendar';
import type { TaskMutationData } from '../api/calendar';
import ConfirmActionDialog, { type DeleteConfirmationInput } from '../ui/ConfirmActionDialog';
import StatusBadge from '../ui/StatusBadge';
import { Field } from './common';
import type { TaskChecklistItem, TaskResponse } from '../types';
import { isoToDatetimeLocal, datetimeLocalToUtcIso, isEndAfterStart } from '../utils/dateTimeInput';
import { dotnetDurationToHoursMinutes, hoursMinutesToIsoDuration, isValidDuration, durationErrorMessage } from '../utils/durationInput';

interface Props {
  open: boolean;
  onClose: () => void;
  task?: TaskResponse;
  defaultDtStart?: string;
}

export default function TaskEditorDialog(props: Props) {
  const formKey = [
    props.open ? 'open' : 'closed',
    props.task?.id || 'new',
    props.defaultDtStart || 'none',
  ].join(':');

  return <TaskEditorForm key={formKey} {...props} />;
}

function invalidateTaskRelatedQueries(queryClient: QueryClient) {
  queryClient.invalidateQueries({ queryKey: ['tasks'] });
  queryClient.invalidateQueries({ queryKey: ['tasks-paged'] });
  queryClient.invalidateQueries({ queryKey: ['today-sections'] });
  queryClient.invalidateQueries({ queryKey: ['today-section'] });
}

const statusOptions = [
  { value: 'NEEDS-ACTION', label: '待处理', tone: 'neutral' as const },
  { value: 'IN-PROGRESS', label: '进行中', tone: 'primary' as const },
  { value: 'COMPLETED', label: '已完成', tone: 'activity' as const },
  { value: 'CANCELLED', label: '已取消', tone: 'danger' as const },
];

function TaskEditorForm({ open, onClose, task, defaultDtStart }: Props) {
  const [title, setTitle] = useState(task?.title || '');
  const [description, setDescription] = useState(task?.description || '');
  const [priority, setPriority] = useState(task?.priority || 0);
  const [dtStart, setDtStart] = useState(defaultDtStart || isoToDatetimeLocal(task?.dtStart ?? '') || '');
  const [plannedEnd, setPlannedEnd] = useState(isoToDatetimeLocal(task?.plannedEnd ?? '') || '');
  const [due, setDue] = useState(isoToDatetimeLocal(task?.due ?? '') || '');
  const [durationHours, setDurationHours] = useState(() => {
    if (!task) return '0';
    if (!task.estimatedDuration) return '';
    return String(dotnetDurationToHoursMinutes(task.estimatedDuration).hours);
  });
  const [durationMinutes, setDurationMinutes] = useState(() => {
    if (!task) return '30';
    if (!task.estimatedDuration) return '';
    return String(dotnetDurationToHoursMinutes(task.estimatedDuration).minutes);
  });
  const [taskBookId, setTaskBookId] = useState(task?.taskBookId || '');
  const [calendarId, setCalendarId] = useState(task?.calendarId || '');
  const [status, setStatus] = useState(task?.status || 'NEEDS-ACTION');
  const [percentComplete, setPercentComplete] = useState(task?.percentComplete ?? 0);
  const [minimumSegment, setMinimumSegment] = useState(task?.minimumSegment ? (
    (() => {
      const m = dotnetDurationToHoursMinutes(task.minimumSegment);
      return String(m.hours * 60 + m.minutes);
    })()
  ) : '');
  const [checklistItems, setChecklistItems] = useState<TaskChecklistItem[]>(task?.checklistItems ?? []);
  const [newChecklistTitle, setNewChecklistTitle] = useState('');
  const [deleteInput, setDeleteInput] = useState<DeleteConfirmationInput | null>(null);
  const [validationErrorMessage, setValidationErrorMessage] = useState<string | null>(null);
  const [checklistError, setChecklistError] = useState<string | null>(null);
  const checklistSaveTimers = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map());
  const savedTitlesRef = useRef<Map<string, string>>(new Map((task?.checklistItems ?? []).map(i => [i.id, i.title])));
  const dialogRef = useRef<HTMLElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const titleId = useId();
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialogRef.current?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
    };
  }, [open]);

  useEffect(() => () => {
    checklistSaveTimers.current.forEach(clearTimeout);
  }, []);

  const { data: taskBooks } = useQuery({
    queryKey: ['task-books'],
    queryFn: () => getTaskBooks(),
    enabled: open
  });

  const { data: calendars } = useQuery({
    queryKey: ['calendars', 'task'],
    queryFn: () => getCalendars('task'),
    enabled: open
  });

  const createMut = useMutation({
    mutationFn: (data: Partial<TaskMutationData>) => createTask(data),
    onSuccess: () => { invalidateTaskRelatedQueries(queryClient); onClose(); }
  });

  const updateMut = useMutation({
    mutationFn: (data: TaskMutationData) => updateTask(task!.id, data),
    onSuccess: () => { invalidateTaskRelatedQueries(queryClient); onClose(); }
  });

  const deleteMut = useMutation({
    mutationFn: () => deleteTask(task!.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['calendar-recycle-bin'] });
      invalidateTaskRelatedQueries(queryClient);
      setDeleteInput(null);
      onClose();
    },
    onError: () => setDeleteInput(null),
  });

  const addChecklistMut = useMutation({
    mutationFn: (data: { title: string; sortOrder?: number | null }) => addTaskChecklistItem(task!.id, data),
    onSuccess: (newItem) => {
      setChecklistItems(prev => [...prev, newItem]);
      setNewChecklistTitle('');
      queryClient.invalidateQueries({ queryKey: ['tasks'] });
      queryClient.invalidateQueries({ queryKey: ['tasks-paged'] });
    }
  });

  const mutationError = createMut.error || updateMut.error || deleteMut.error;
  const mutationErrorMessage = validationErrorMessage || (mutationError instanceof Error ? mutationError.message : null);

  function handleDelete() {
    if (!task) return;
    deleteMut.reset();
    setDeleteInput({
      targetType: 'task',
      title: task.title,
      affectedCount: 1,
      samples: [{
        id: task.id,
        type: 'task',
        title: task.title,
        start: task.dtStart,
        end: task.plannedEnd || task.due,
        bookName: undefined,
      }],
    });
  }

  function confirmDelete() {
    if (!task) return;
    deleteMut.mutate();
  }

  function cancelDelete() {
    if (deleteMut.isPending) return;
    setDeleteInput(null);
  }

  function handleToggleComplete() {
    if (!task) return;
    const newStatus = status === 'COMPLETED' ? 'NEEDS-ACTION' : 'COMPLETED';
    updateMut.mutate(taskToMutationData(task, { status: newStatus }), {
      onSuccess: () => { setStatus(newStatus); }
    });
  }

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (task?.plannedEnd && !plannedEnd) {
      setValidationErrorMessage('当前接口暂不支持清空计划结束时间，可改成新的结束时间。');
      return;
    }

    setValidationErrorMessage(null);

    if (durationHours !== '' || durationMinutes !== '') {
      if (!isValidDuration(durationHours, durationMinutes)) {
        setValidationErrorMessage(durationErrorMessage());
        return;
      }
    }

    if (dtStart && plannedEnd && !isEndAfterStart(dtStart, plannedEnd)) {
      setValidationErrorMessage('计划结束时间必须晚于开始时间');
      return;
    }

    const estimatedDuration = (durationHours !== '' || durationMinutes !== '')
      ? hoursMinutesToIsoDuration(Number(durationHours), Number(durationMinutes))
      : undefined;

    const minimumSegmentDuration = minimumSegment !== ''
      ? hoursMinutesToIsoDuration(0, Number(minimumSegment))
      : undefined;

    const data: TaskMutationData = {
      title, description, priority, status,
      calendarId: calendarId || undefined,
      taskBookId: taskBookId || undefined,
      percentComplete,
      minimumSegment: minimumSegmentDuration,
      dtStart: datetimeLocalToUtcIso(dtStart) || undefined,
      plannedEnd: datetimeLocalToUtcIso(plannedEnd) || undefined,
      due: datetimeLocalToUtcIso(due) || undefined,
      estimatedDuration,
    };
    if (task) {
      updateMut.mutate(taskToMutationData(task, data));
    }
    else createMut.mutate(data);
  }

  const isCompleted = status === 'COMPLETED';
  const priorityOptions = [
    { value: 1, label: '高', className: 'bg-red-50 text-red-600 border-red-200' },
    { value: 0, label: '普通', className: 'bg-amber-50 text-amber-700 border-amber-200' },
    { value: 3, label: '低', className: 'bg-teal-50 text-teal-700 border-teal-200' },
  ];

  function handleAddChecklist() {
    const trimmed = newChecklistTitle.trim();
    if (!trimmed || !task) return;
    addChecklistMut.mutate({ title: trimmed });
  }

  function handleChecklistToggle(item: TaskChecklistItem) {
    if (!task) return;
    setChecklistItems(prev =>
      prev.map(i => i.id === item.id ? { ...i, isDone: !i.isDone } : i)
    );
    updateTaskChecklistItem(task.id, item.id, { isDone: !item.isDone })
      .catch(() => {
        setChecklistItems(prev =>
          prev.map(i => i.id === item.id ? { ...i, isDone: item.isDone } : i)
        );
        setChecklistError('检查项保存失败');
      });
  }

  function handleChecklistDelete(itemId: string) {
    if (!task) return;
    const removedIndex = checklistItems.findIndex(i => i.id === itemId);
    const removed = checklistItems.find(i => i.id === itemId);
    setChecklistItems(prev => prev.filter(i => i.id !== itemId));
    deleteTaskChecklistItem(task.id, itemId)
      .catch(() => {
        if (removed) {
          setChecklistItems(prev => {
            const next = [...prev];
            next.splice(Math.min(removedIndex, next.length), 0, removed);
            return next;
          });
        }
        setChecklistError('检查项保存失败');
      });
  }

  function handleChecklistTextChange(itemId: string, text: string) {
    if (!task) return;
    setChecklistItems(prev =>
      prev.map(i => i.id === itemId ? { ...i, title: text } : i)
    );
    const existing = checklistSaveTimers.current.get(itemId);
    if (existing) clearTimeout(existing);
    const timer = setTimeout(() => {
      updateTaskChecklistItem(task!.id, itemId, { title: text })
        .then(() => { savedTitlesRef.current.set(itemId, text); })
        .catch(() => {
          const saved = savedTitlesRef.current.get(itemId);
          if (saved !== undefined) {
            setChecklistItems(prev =>
              prev.map(i => i.id === itemId ? { ...i, title: saved } : i)
            );
          }
          setChecklistError('检查项保存失败，已恢复上次保存的标题');
        });
    }, 500);
    checklistSaveTimers.current.set(itemId, timer);
  }

  if (!open) return null;

  return (
    <>
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/40 backdrop-blur-xs animate-backdrop" onClick={onClose}>
      <aside role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} ref={dialogRef} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }} className="w-full max-w-lg max-h-[85vh] flex flex-col rounded-xl border border-zinc-200 bg-white shadow-dialog animate-dialog" onClick={e => e.stopPropagation()}>
        <header className="flex items-center justify-between border-b border-zinc-200 px-5 py-4 shrink-0">
          <h2 id={titleId} className="text-base font-semibold text-zinc-900">{task ? '编辑任务' : '新建任务'}</h2>
          <button onClick={onClose} className="text-zinc-400 hover:text-zinc-600 p-1 rounded-lg hover:bg-zinc-100">
            <X className="w-4 h-4" />
          </button>
        </header>
        <div className="overflow-y-auto max-h-[75vh] px-5 py-4 space-y-4">
          <form id="task-editor-form" onSubmit={handleSubmit} className="space-y-4">
            {mutationErrorMessage && (
              <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
                {mutationErrorMessage}
              </div>
            )}
            <Field label="所属日历">
              <select value={calendarId} onChange={e => setCalendarId(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm">
                <option value="">未分类</option>
                {calendars?.map(cal => (
                  <option key={cal.id} value={cal.id}>{cal.name}</option>
                ))}
              </select>
            </Field>
            <Field label="所属任务本">
              <select value={taskBookId} onChange={e => setTaskBookId(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm">
                <option value="">未分类</option>
                {taskBooks?.map(book => (
                  <option key={book.id} value={book.id}>{book.name}</option>
                ))}
              </select>
            </Field>
            <Field label="标题">
              <input type="text" value={title} onChange={e => setTitle(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm" required />
            </Field>
            <Field label="描述">
              <textarea value={description} onChange={e => setDescription(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm" rows={3} />
            </Field>
            <Field label="状态">
              <div className="flex flex-wrap gap-2">
                {statusOptions.map(opt => (
                  <button
                    key={opt.value}
                    type="button"
                    onClick={() => setStatus(opt.value)}
                    className={`rounded-full border px-3 py-1.5 text-sm ${
                      status === opt.value
                        ? 'border-blue-600 bg-blue-600 text-white'
                        : 'border-slate-200 bg-white text-slate-500 hover:border-blue-200'
                    }`}
                  >
                    {opt.label}
                  </button>
                ))}
              </div>
              {status && (
                <div className="mt-2">
                  <StatusBadge tone={statusOptions.find(o => o.value === status)?.tone ?? 'neutral'}>
                    {statusOptions.find(o => o.value === status)?.label ?? status}
                  </StatusBadge>
                </div>
              )}
            </Field>
            <Field label="优先级">
              <div className="flex gap-2">
                {priorityOptions.map(item => (
                  <button
                    key={item.value}
                    type="button"
                    onClick={() => setPriority(item.value)}
                    className={`rounded-full border px-3 py-1.5 text-sm ${
                      priority === item.value ? item.className : 'border-slate-200 bg-white text-slate-500'
                    }`}
                  >
                    {item.label}
                  </button>
                ))}
              </div>
            </Field>
            <Field label="完成度">
              <div className="flex items-center gap-3">
                <input
                  type="range"
                  min={0}
                  max={100}
                  step={5}
                  value={percentComplete}
                  onChange={e => setPercentComplete(Number(e.target.value))}
                  className="flex-1 h-2 rounded-full accent-blue-600"
                />
                <span className="text-sm font-medium text-slate-700 w-10 text-right">{percentComplete}%</span>
              </div>
            </Field>
            <Field label="计划时间">
              <input type="datetime-local" value={dtStart} onChange={e => setDtStart(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm" />
            </Field>
            <Field label="计划结束">
              <input type="datetime-local" value={plannedEnd} onChange={e => {
                setPlannedEnd(e.target.value);
                setValidationErrorMessage(null);
              }}
                className="w-full border rounded px-3 py-2 text-sm" />
            </Field>
            <Field label="截止日期">
              <input type="datetime-local" value={due} onChange={e => setDue(e.target.value)}
                className="w-full border rounded px-3 py-2 text-sm" />
            </Field>
            <Field label="预估时长">
              <div className="flex items-center gap-2">
                <input id="task-duration-hours" type="number"
                  aria-label="时"
                  value={durationHours}
                  onChange={e => setDurationHours(e.target.value)}
                  min={0} step={1}
                  className="w-20 border rounded px-3 py-2 text-sm" />
                <span className="text-sm text-gray-500">时</span>
                <input id="task-duration-minutes" type="number"
                  aria-label="分钟"
                  value={durationMinutes}
                  onChange={e => setDurationMinutes(e.target.value)}
                  min={0} max={59} step={1}
                  className="w-20 border rounded px-3 py-2 text-sm" />
                <span className="text-sm text-gray-500">分钟</span>
              </div>
            </Field>
            <Field label="番茄钟（分钟）">
              <input
                type="number"
                value={minimumSegment}
                onChange={e => setMinimumSegment(e.target.value)}
                min={0} step={5}
                placeholder="可选"
                className="w-full border rounded px-3 py-2 text-sm"
              />
            </Field>
          </form>

          {/* Checklist */}
          <div className="border-t border-zinc-200 pt-4">
            <p className="text-sm font-medium text-gray-600 mb-2">子检查项</p>
            {checklistError && (
              <p className="mb-2 text-xs text-red-600">{checklistError}</p>
            )}
            <div className="space-y-2">
              {checklistItems.map(item => (
                <div key={item.id} className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <input
                    type="checkbox"
                    checked={item.isDone}
                    onChange={() => handleChecklistToggle(item)}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-200 shrink-0"
                  />
                  <input
                    type="text"
                    value={item.title}
                    onChange={e => handleChecklistTextChange(item.id, e.target.value)}
                    className={`flex-1 bg-transparent text-sm outline-none ${item.isDone ? 'line-through text-slate-400' : 'text-slate-800'}`}
                  />
                  <button
                    type="button"
                    onClick={() => handleChecklistDelete(item.id)}
                    className="text-slate-400 hover:text-red-500 p-1 shrink-0"
                    title="删除"
                  >
                    <X className="w-3.5 h-3.5" />
                  </button>
                </div>
              ))}
              <div className="flex gap-2">
                <input
                  type="text"
                  value={newChecklistTitle}
                  onChange={e => setNewChecklistTitle(e.target.value)}
                  placeholder="新增检查项..."
                  onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); handleAddChecklist(); } }}
                  className="flex-1 border rounded px-3 py-2 text-sm"
                  disabled={!task || addChecklistMut.isPending}
                />
                <button
                  type="button"
                  onClick={handleAddChecklist}
                  disabled={!task || !newChecklistTitle.trim() || addChecklistMut.isPending}
                  className="rounded-lg bg-blue-600 px-3 py-2 text-sm text-white transition-colors hover:bg-blue-700 disabled:opacity-50"
                >
                  添加
                </button>
              </div>
            </div>
          </div>
        </div>

        <footer className="flex items-center justify-between border-t border-zinc-200 px-5 py-4 shrink-0">
          <div className="flex flex-wrap gap-2">
            {task && (
              <button type="button" onClick={handleDelete}
                disabled={deleteMut.isPending}
                className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600 disabled:opacity-50">
                删除
              </button>
            )}
            {task && (
              <button type="button" onClick={handleToggleComplete}
                disabled={updateMut.isPending}
                className={`rounded-lg border px-3 py-2 text-sm disabled:opacity-50 ${
                  isCompleted
                    ? 'border-gray-300 text-gray-600 hover:bg-gray-50'
                    : 'border-green-300 text-green-600 hover:bg-green-50'
                }`}>
                {isCompleted ? '标记未完成' : '标记完成'}
              </button>
            )}
          </div>
          <div className="flex gap-2">
            <button type="button" onClick={onClose}
              className="pim-button-secondary px-4 py-2 text-sm">取消</button>
            <button type="submit" form="task-editor-form" disabled={createMut.isPending || updateMut.isPending}
              className="pim-button-primary px-4 py-2 text-sm disabled:opacity-50">
              {task ? '保存' : '创建'}
            </button>
          </div>
        </footer>
      </aside>
    </div>
    <ConfirmActionDialog
      open={deleteInput !== null}
      input={deleteInput}
      isPending={deleteMut.isPending}
      onCancel={cancelDelete}
      onConfirm={confirmDelete}
    />
    </>
  );
}
