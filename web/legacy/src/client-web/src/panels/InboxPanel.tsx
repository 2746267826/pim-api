import { useState, useRef, useEffect } from 'react';
import type {
  DragEvent as ReactDragEvent,
  KeyboardEvent as ReactKeyboardEvent,
  MouseEvent as ReactMouseEvent,
} from 'react';
import { useQuery } from '@tanstack/react-query';
import { getTasks } from '../api/calendar';
import TaskEditorDialog from '../dialogs/TaskEditorDialog';
import EventEditorDialog from '../dialogs/EventEditorDialog';
import type { TaskResponse } from '../types';

interface InboxPanelProps {
  draggable?: boolean;
}

export default function InboxPanel({ draggable = false }: InboxPanelProps) {
  const [taskEditorOpen, setTaskEditorOpen] = useState(false);
  const [editingTask, setEditingTask] = useState<TaskResponse | undefined>();
  const [eventEditorOpen, setEventEditorOpen] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const didDragRef = useRef(false);
  const dragResetTimerRef = useRef<ReturnType<typeof window.setTimeout> | null>(null);

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      if (dragResetTimerRef.current) {
        window.clearTimeout(dragResetTimerRef.current);
      }
    };
  }, []);

  const { data: tasks = [], isLoading } = useQuery({
    queryKey: ['tasks'],
    queryFn: () => getTasks()
  });

  const unscheduled = tasks.filter(t => t.isInbox || !t.dtStart);

  function openTaskEditor(task: TaskResponse) {
    setEditingTask(task);
    setTaskEditorOpen(true);
  }

  function resetDragClickGuardSoon() {
    if (dragResetTimerRef.current) {
      window.clearTimeout(dragResetTimerRef.current);
    }
    dragResetTimerRef.current = window.setTimeout(() => {
      didDragRef.current = false;
      dragResetTimerRef.current = null;
    }, 150);
  }

  function handleTaskDragStart(event: ReactDragEvent<HTMLDivElement>, task: TaskResponse) {
    if (!draggable) return;

    didDragRef.current = true;
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('application/x-pim-task-id', task.id);
    event.dataTransfer.setData('application/x-pim-task-title', task.title);
    event.dataTransfer.setData('text/plain', task.id);
  }

  function handleTaskDragEnd() {
    resetDragClickGuardSoon();
  }

  function handleTaskClick(event: ReactMouseEvent<HTMLDivElement>, task: TaskResponse) {
    if (didDragRef.current) {
      event.preventDefault();
      event.stopPropagation();
      resetDragClickGuardSoon();
      return;
    }

    openTaskEditor(task);
  }

  function handleTaskKeyDown(event: ReactKeyboardEvent<HTMLDivElement>, task: TaskResponse) {
    if (event.key !== 'Enter' && event.key !== ' ') return;

    event.preventDefault();
    openTaskEditor(task);
  }

  return (
    <div className="w-[280px] bg-gray-50 border-l flex flex-col h-full">
      <div className="p-4 border-b">
        <h3 className="font-semibold text-sm text-gray-600">收集箱</h3>
        <p className="text-xs text-gray-400 mt-0.5">
          {unscheduled.length} 个未排程任务
        </p>
      </div>

      <div className="flex-1 overflow-auto p-3 space-y-2">
        {isLoading ? (
          <p className="text-xs text-gray-400 text-center py-8">加载中...</p>
        ) : unscheduled.length === 0 ? (
          <p className="text-xs text-gray-400 text-center py-8">所有任务均已排入日程</p>
        ) : (
          unscheduled.map(task => (
            <div
              key={task.id}
              className={`bg-white rounded-lg p-3 border hover:shadow-sm transition-shadow ${draggable ? 'js-draggable-task cursor-grab active:cursor-grabbing' : 'cursor-pointer'}`}
              role="button"
              tabIndex={0}
              aria-label={`编辑任务：${task.title}`}
              draggable={draggable}
              data-task-id={draggable ? task.id : undefined}
              data-task-title={draggable ? task.title : undefined}
              onClick={event => handleTaskClick(event, task)}
              onDragStart={event => handleTaskDragStart(event, task)}
              onDragEnd={handleTaskDragEnd}
              onKeyDown={event => handleTaskKeyDown(event, task)}
            >
              <div className="flex items-start gap-2">
                <span
                  className="w-2 h-2 rounded-full mt-1.5 flex-shrink-0"
                  style={{ backgroundColor: task.priority === 1 ? '#E53935' : task.priority === 3 ? '#43A047' : '#FFA726' }}
                />
                <div className="flex-1 min-w-0">
                  <p className="text-sm text-gray-800 truncate">{task.title}</p>
                  {task.due && (
                    <p className="text-xs text-red-400 mt-1">
                      截止: {new Date(task.due).toLocaleDateString('zh-CN')}
                    </p>
                  )}
                </div>
              </div>
            </div>
          ))
        )}
      </div>

      <div className="p-3 border-t space-y-2">
        <div className="relative" ref={menuRef}>
          <button
            className="w-full py-2 text-sm bg-blue-600 text-white rounded hover:bg-blue-700"
            onClick={() => setMenuOpen(!menuOpen)}
          >
            + 新建
          </button>
          {menuOpen && (
            <div className="absolute bottom-full left-0 right-0 mb-1 bg-white border rounded shadow-lg overflow-hidden">
              <button
                className="w-full px-3 py-2 text-sm text-left hover:bg-blue-50"
                onClick={() => { setMenuOpen(false); setEditingTask(undefined); setTaskEditorOpen(true); }}
              >
                任务
              </button>
              <button
                className="w-full px-3 py-2 text-sm text-left hover:bg-blue-50 border-t"
                onClick={() => { setMenuOpen(false); setEventEditorOpen(true); }}
              >
                日程
              </button>
            </div>
          )}
        </div>
        <button className="w-full py-2 text-sm border border-gray-300 text-gray-600 rounded hover:bg-gray-100">
          一键重排
        </button>
      </div>

      <TaskEditorDialog
        open={taskEditorOpen}
        onClose={() => setTaskEditorOpen(false)}
        task={editingTask}
      />
      <EventEditorDialog
        open={eventEditorOpen}
        onClose={() => setEventEditorOpen(false)}
      />
    </div>
  );
}
