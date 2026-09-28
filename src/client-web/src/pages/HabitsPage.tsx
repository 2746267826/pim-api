import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { archiveHabit, deleteHabit, getHabits, updateHabit } from '../api/calendar';
import type { HabitRoutine } from '../types';
import HabitRoutineEditor from '../components/schedule/HabitRoutineEditor';
import {
  HABIT_CADENCE_OPTIONS,
  habitCadenceFilterValue,
  habitCadenceLabel,
  habitCadenceValue,
  isArchivedHabit,
} from '../components/schedule/habitCadence';
import PageHeader from '../ui/PageHeader';
import MobilePageHeader from '../ui/MobilePageHeader';
import SegmentedControl from '../ui/SegmentedControl';

type HabitTab = 'active' | 'planning' | 'archive';

const habitTabs: Array<{ value: HabitTab; label: string }> = [
  { value: 'active', label: '执行中' },
  { value: 'planning', label: '规划' },
  { value: 'archive', label: '归档' },
];

export default function HabitsPage() {
  const [tab, setTab] = useState<HabitTab>('active');
  const [cadence, setCadence] = useState('all');
  const [source, setSource] = useState('all');
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editTitle, setEditTitle] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [editCadence, setEditCadence] = useState('Daily');
  const [actionError, setActionError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const { data: habits = [] } = useQuery({
    queryKey: ['habits'],
    queryFn: getHabits,
  });

  /** 任何写操作后同时失效习惯列表与日历图层，保证各展示面同步。 */
  const invalidateHabits = () => {
    queryClient.invalidateQueries({ queryKey: ['habits'] });
    queryClient.invalidateQueries({ queryKey: ['calendar-layers'] });
  };

  const updateMutation = useMutation({
    mutationFn: (habit: HabitRoutine) => updateHabit(habit.id, {
      title: editTitle.trim(),
      description: editDescription.trim(),
      cadence: editCadence,
    }),
    onSuccess: () => {
      setEditingId(null);
      setActionError(null);
      invalidateHabits();
    },
    onError: (error: unknown) => setActionError(describeActionError(error)),
  });

  const archiveMutation = useMutation({
    mutationFn: (id: string) => archiveHabit(id),
    onSuccess: () => {
      setActionError(null);
      invalidateHabits();
    },
    onError: (error: unknown) => setActionError(describeActionError(error)),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteHabit(id),
    onSuccess: () => {
      setActionError(null);
      invalidateHabits();
    },
    onError: (error: unknown) => setActionError(describeActionError(error)),
  });

  const startEditing = (habit: HabitRoutine) => {
    setEditingId(habit.id);
    setEditTitle(habit.title);
    setEditDescription(habit.description ?? '');
    setEditCadence(habitCadenceValue(habit.cadence));
    setActionError(null);
  };

  const filteredHabits = habits.filter(habit => {
    const cadenceMatches = cadence === 'all' || habitCadenceFilterValue(habit.cadence) === cadence;
    const sourceMatches = source === 'all' || habit.source.toLowerCase() === source;
    const archiveMatches = tab === 'archive'
      ? isArchivedHabit(habit)
      : tab === 'active'
        ? !isArchivedHabit(habit)
        : true;
    return cadenceMatches && sourceMatches && archiveMatches;
  });

  return (
    <div className="mx-auto w-full max-w-[1200px] space-y-4 overflow-auto pb-20 md:pb-4">
      <MobilePageHeader title="习惯中心" />
      <PageHeader
        title="习惯中心"
        subtitle="管理习惯规则、完成历史、复盘指标与投射到日历的时间块。"
        actions={<SegmentedControl value={tab} options={habitTabs} onChange={setTab} ariaLabel="习惯视图" />}
      />

      <HabitRoutineEditor />

      {actionError && (
        <p role="alert" className="rounded-lg border border-[var(--pim-danger)] bg-[var(--pim-danger-soft)] px-3 py-2 text-xs text-[var(--pim-danger)]">
          {actionError}
        </p>
      )}

      <section className="pim-panel p-4">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <label>
            <span className="text-xs font-semibold text-slate-500">频率</span>
            <select value={cadence} onChange={event => setCadence(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="all">全部频率</option>
              <option value="daily">每天</option>
              <option value="weekly">每周</option>
              <option value="monthly">每月</option>
            </select>
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">来源</span>
            <select value={source} onChange={event => setSource(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="all">全部来源</option>
              <option value="manual">手动</option>
              <option value="template">模板</option>
              <option value="ai">智能</option>
            </select>
          </label>
        </div>
      </section>

      <section className="pim-panel p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">习惯规则</h2>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
            {cadence} / {source}
          </span>
        </div>
        <div className="mt-4 grid gap-2">
          {filteredHabits.map(habit => (
            <article key={habit.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3" data-testid={`habit-${habit.id}`}>
              {editingId === habit.id ? (
                <div className="grid gap-2">
                  <input
                    type="text"
                    aria-label="习惯名称"
                    value={editTitle}
                    onChange={event => setEditTitle(event.target.value)}
                    className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
                  />
                  <input
                    type="text"
                    aria-label="习惯描述"
                    value={editDescription}
                    onChange={event => setEditDescription(event.target.value)}
                    placeholder="描述（可选）"
                    className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
                  />
                  <select
                    aria-label="习惯频率"
                    value={editCadence}
                    onChange={event => setEditCadence(event.target.value)}
                    className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
                  >
                    {HABIT_CADENCE_OPTIONS.map(option => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </select>
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      data-testid={`habit-save-${habit.id}`}
                      disabled={!editTitle.trim() || updateMutation.isPending}
                      onClick={() => updateMutation.mutate(habit)}
                      className="pim-button-primary px-3 py-1.5 text-xs disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      保存修改
                    </button>
                    <button
                      type="button"
                      data-testid={`habit-cancel-${habit.id}`}
                      onClick={() => setEditingId(null)}
                      className="pim-button-secondary px-3 py-1.5 text-xs"
                    >
                      取消
                    </button>
                  </div>
                </div>
              ) : (
                <>
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <h3 className="text-sm font-semibold text-slate-900">{habit.title}</h3>
                    <span className="rounded-full bg-white px-2.5 py-1 text-xs font-semibold text-slate-500">
                      {habitCadenceLabel(habit.cadence)} · {habit.status}
                    </span>
                  </div>
                  <div className="mt-2 flex flex-wrap gap-2">
                    <button
                      type="button"
                      data-testid={`habit-edit-${habit.id}`}
                      onClick={() => startEditing(habit)}
                      className="pim-button-secondary px-3 py-1.5 text-xs"
                    >
                      编辑
                    </button>
                    {!isArchivedHabit(habit) && (
                      <button
                        type="button"
                        data-testid={`habit-archive-${habit.id}`}
                        disabled={archiveMutation.isPending}
                        onClick={() => archiveMutation.mutate(habit.id)}
                        className="pim-button-secondary px-3 py-1.5 text-xs disabled:opacity-50"
                      >
                        归档
                      </button>
                    )}
                    <button
                      type="button"
                      data-testid={`habit-delete-${habit.id}`}
                      disabled={deleteMutation.isPending}
                      onClick={() => {
                        if (window.confirm(`确定删除习惯「${habit.title}」？删除后不再出现在列表与日历中。`)) {
                          deleteMutation.mutate(habit.id);
                        }
                      }}
                      className="pim-button-secondary px-3 py-1.5 text-xs text-[var(--pim-danger)] disabled:opacity-50"
                    >
                      删除
                    </button>
                  </div>
                </>
              )}
              <p className="mt-2 text-xs text-slate-500">规则变更会进入确认中心，避免误改长期习惯事实。</p>
            </article>
          ))}
          {filteredHabits.length === 0 && (
            <p className="rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              当前筛选下没有习惯记录。
            </p>
          )}
        </div>
      </section>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section className="pim-panel p-4">
          <h2 className="text-sm font-semibold text-slate-950">完成历史</h2>
          <p className="mt-3 text-sm text-slate-500">
            完成记录、漏打卡、连续天数与复盘指标会在这里汇总。
          </p>
        </section>

        <section className="pim-panel p-4">
          <h2 className="text-sm font-semibold text-slate-950">投射到日历</h2>
          <p className="mt-3 text-sm text-slate-500">
            习惯规则会生成日历图层，可请求生成任务或检查项，并对规则变更发起确认。
          </p>
        </section>
      </div>
    </div>
  );
}

function describeActionError(error: unknown): string {
  if (error && typeof error === 'object' && 'message' in error) {
    const message = (error as { message?: unknown }).message;
    if (typeof message === 'string' && message.trim()) return message;
  }
  return '操作失败，请稍后重试。';
}

