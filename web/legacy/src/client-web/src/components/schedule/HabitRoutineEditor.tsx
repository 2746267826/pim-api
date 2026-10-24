import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { createHabit } from '../../api/calendar';

interface HabitRoutineEditorProps {
  onCreated?: () => void;
}

export default function HabitRoutineEditor({ onCreated }: HabitRoutineEditorProps) {
  const queryClient = useQueryClient();
  const [title, setTitle] = useState('');
  const [cadence, setCadence] = useState('Daily');

  const createMutation = useMutation({
    mutationFn: () => createHabit({
      title,
      cadence,
      source: 'manual',
      status: 'Active',
      description: null,
    }),
    onSuccess: () => {
      setTitle('');
      queryClient.invalidateQueries({ queryKey: ['habits'] });
      queryClient.invalidateQueries({ queryKey: ['calendar-layers'] });
      onCreated?.();
    },
  });

  return (
    <section className="pim-panel p-4" aria-label="习惯规则编辑器">
      <h2 className="text-sm font-semibold text-slate-950">创建或编辑习惯规则</h2>
      <div className="mt-3 grid gap-3 md:grid-cols-[1fr_160px_auto]">
        <input
          type="text"
          value={title}
          onChange={event => setTitle(event.target.value)}
          placeholder="习惯名称"
          className="rounded-lg border border-slate-200 px-3 py-2 text-sm"
        />
        <select
          value={cadence}
          onChange={event => setCadence(event.target.value)}
          className="rounded-lg border border-slate-200 px-3 py-2 text-sm"
        >
          <option value="Daily">每天</option>
          <option value="Weekly">每周</option>
          <option value="Monthly">每月</option>
        </select>
        <button
          type="button"
          disabled={!title.trim() || createMutation.isPending}
          onClick={() => createMutation.mutate()}
          className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-50"
        >
          保存
        </button>
      </div>
    </section>
  );
}
