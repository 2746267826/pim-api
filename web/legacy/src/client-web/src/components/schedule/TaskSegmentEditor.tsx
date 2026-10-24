import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createTaskExecutionSegment, listTaskExecutionSegments } from '../../api/calendar';
import type { TaskResponse } from '../../types';

interface TaskSegmentEditorProps {
  task?: TaskResponse | null;
  onClose: () => void;
}

export default function TaskSegmentEditor({ task, onClose }: TaskSegmentEditorProps) {
  const queryClient = useQueryClient();
  const [startsAt, setStartsAt] = useState('');
  const [endsAt, setEndsAt] = useState('');
  const [planningReason, setPlanningReason] = useState('');

  useEffect(() => {
    setStartsAt(task?.dtStart?.slice(0, 16) ?? '');
    setEndsAt((task?.plannedEnd || task?.due)?.slice(0, 16) ?? '');
    setPlanningReason('');
  }, [task]);

  const { data: segments = [] } = useQuery({
    queryKey: ['task-segments', task?.id],
    queryFn: () => listTaskExecutionSegments(task!.id),
    enabled: Boolean(task),
  });

  const createMutation = useMutation({
    mutationFn: () => createTaskExecutionSegment(task!.id, {
      startsAt,
      endsAt,
      status: 'Planned',
      source: 'manual',
      planningReason: planningReason || null,
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['task-segments', task?.id] });
      queryClient.invalidateQueries({ queryKey: ['calendar-layers'] });
      queryClient.invalidateQueries({ queryKey: ['today-sections'] });
    },
  });

  if (!task) return null;

  return (
    <section className="pim-panel p-4" aria-label="任务时间段编辑器">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-950">多时间段</h2>
          <p className="mt-1 text-xs text-slate-500">{task.title}</p>
        </div>
        <button type="button" className="pim-button-secondary px-3 py-1.5 text-xs" onClick={onClose}>
          收起
        </button>
      </div>
      <div className="mt-3 grid gap-2 md:grid-cols-2">
        <label className="block">
          <span className="text-xs font-semibold text-slate-500">开始</span>
          <input
            type="datetime-local"
            value={startsAt}
            onChange={event => setStartsAt(event.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
          />
        </label>
        <label className="block">
          <span className="text-xs font-semibold text-slate-500">结束</span>
          <input
            type="datetime-local"
            value={endsAt}
            onChange={event => setEndsAt(event.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
          />
        </label>
      </div>
      <label className="mt-3 block">
        <span className="text-xs font-semibold text-slate-500">原因</span>
        <input
          type="text"
          value={planningReason}
          onChange={event => setPlanningReason(event.target.value)}
          placeholder="例如：等待反馈、阻塞复盘、深度工作"
          className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
        />
      </label>
      <button
        type="button"
        disabled={!startsAt || !endsAt || createMutation.isPending}
        onClick={() => createMutation.mutate()}
        className="pim-button-primary mt-3 px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-50"
      >
        添加时间段
      </button>
      <div className="mt-4 space-y-2">
        {segments.map(segment => (
          <div key={segment.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-xs text-slate-600">
            {segment.startsAt} → {segment.endsAt} · {segment.status}
          </div>
        ))}
      </div>
    </section>
  );
}
