import type { TaskResponse } from '../../types';

interface TaskHierarchyPanelProps {
  tasks: TaskResponse[];
  selectedTaskId?: string;
  onSelectTask: (task: TaskResponse) => void;
}

export default function TaskHierarchyPanel({
  tasks,
  selectedTaskId,
  onSelectTask,
}: TaskHierarchyPanelProps) {
  return (
    <aside className="pim-panel min-w-0 p-4" aria-label="任务层级">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-slate-950">项目与任务本</h2>
        <span data-contract="Checklist" className="rounded-full bg-slate-100 px-2 py-1 text-[11px] font-semibold text-slate-500">
          检查项
        </span>
      </div>
      <div className="mt-3 space-y-2">
        {tasks.map(task => (
          <TaskNode
            key={task.id}
            task={task}
            depth={0}
            selectedTaskId={selectedTaskId}
            onSelectTask={onSelectTask}
          />
        ))}
        {tasks.length === 0 && (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            当前筛选下没有任务。
          </p>
        )}
      </div>
    </aside>
  );
}

function TaskNode({
  task,
  depth,
  selectedTaskId,
  onSelectTask,
}: {
  task: TaskResponse;
  depth: number;
  selectedTaskId?: string;
  onSelectTask: (task: TaskResponse) => void;
}) {
  const selected = task.id === selectedTaskId;
  const childTasks = task.subTasks ?? [];

  return (
    <div>
      <button
        type="button"
        onClick={() => onSelectTask(task)}
        className={`flex w-full items-center justify-between gap-2 rounded-lg border px-3 py-2 text-left text-sm transition-colors ${
          selected
            ? 'border-blue-200 bg-blue-50 text-blue-700'
            : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
        }`}
        style={{ paddingLeft: `${12 + depth * 14}px` }}
      >
        <span className="min-w-0 truncate">{task.title}</span>
        <span className="shrink-0 text-[11px] font-semibold text-slate-400">{childTasks.length}</span>
      </button>
      {childTasks.length > 0 && (
        <div className="mt-1 space-y-1">
          {childTasks.map(child => (
            <TaskNode
              key={child.id}
              task={child}
              depth={depth + 1}
              selectedTaskId={selectedTaskId}
              onSelectTask={onSelectTask}
            />
          ))}
        </div>
      )}
    </div>
  );
}
