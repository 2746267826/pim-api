import type { ActivityClassificationSettings } from '../../types';

interface Props {
  settings: ActivityClassificationSettings | undefined;
  selectedMinutes: number;
  onSelectedMinutesChange: (minutes: number) => void;
  onSaveSettings: () => void;
  isSaving: boolean;
  isDirty: boolean;
}

export default function ClassificationRecomputePanel({
  settings,
  selectedMinutes,
  onSelectedMinutesChange,
  onSaveSettings,
  isSaving,
  isDirty,
}: Props) {
  const presets = settings?.supportedRecommendedMinimumDurations ?? [1, 3, 5, 10, 15];

  return (
    <section className="pim-panel p-4">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">推荐最短分类时长</h2>
          <p className="mt-1 max-w-2xl text-sm text-slate-500">
            影响时间线平滑和建议聚类。较短更细，较长更整洁。
          </p>
          <div className="mt-3 flex flex-wrap gap-2">
            {presets.map(minutes => (
              <button
                key={minutes}
                type="button"
                onClick={() => onSelectedMinutesChange(minutes)}
                className={`h-9 rounded-lg border px-3 text-sm font-medium transition-colors ${
                  selectedMinutes === minutes
                    ? 'border-blue-600 bg-blue-50 text-blue-700'
                    : 'border-slate-200 bg-white text-slate-600 hover:border-blue-200 hover:bg-slate-50'
                }`}
              >
                {minutes} 分钟
              </button>
            ))}
          </div>
        </div>
        <button
          type="button"
          className="pim-button-primary h-10 px-4 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60"
          onClick={onSaveSettings}
          disabled={isSaving || !isDirty}
        >
          {isSaving ? '保存中...' : '保存设置'}
        </button>
      </div>
    </section>
  );
}
