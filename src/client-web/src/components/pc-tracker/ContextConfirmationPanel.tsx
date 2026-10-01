import type { ActivityClassificationSuggestion } from '../../types';
import { formatSuggestionBusinessDate } from '../../utils/pcBusinessDay';

interface Props {
  suggestions: ActivityClassificationSuggestion[];
  isLoading: boolean;
  onPreview: (suggestion: ActivityClassificationSuggestion) => void;
  onReject: (suggestion: ActivityClassificationSuggestion) => void;
}

function formatDuration(seconds: number) {
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return `${minutes.toLocaleString('zh-CN')} 分钟`;
  }

  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes > 0
    ? `${hours.toLocaleString('zh-CN')}h ${remainingMinutes}m`
    : `${hours.toLocaleString('zh-CN')}h`;
}

function displayName(suggestion: ActivityClassificationSuggestion) {
  return suggestion.appDisplayName || suggestion.clusterKey || '未识别上下文';
}

function targetText(suggestion: ActivityClassificationSuggestion) {
  const category = suggestion.suggestedCategory || '保持分类';
  return suggestion.suggestedProjectTag
    ? `${category} · ${suggestion.suggestedProjectTag}`
    : category;
}

export default function ContextConfirmationPanel({
  suggestions,
  isLoading,
  onPreview,
  onReject,
}: Props) {
  if (isLoading) {
    return (
      <section className="pim-panel min-w-0 p-4">
        <h2 className="text-sm font-semibold text-slate-950">待确认上下文</h2>
        <div className="mt-4 rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
          正在加载需要确认的上下文...
        </div>
      </section>
    );
  }

  const visibleSuggestions = suggestions.slice(0, 6);

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">待确认上下文</h2>
          <p className="mt-1 text-xs text-slate-500">
            预览高置信度活动上下文，确认后写入 App 知识库。
          </p>
        </div>
        <span className="rounded-full bg-cyan-50 px-2 py-1 text-xs font-medium text-cyan-700">
          {suggestions.length.toLocaleString('zh-CN')} 项
        </span>
      </div>

      {visibleSuggestions.length === 0 ? (
        <div className="mt-4 rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
          暂无待确认上下文，App 知识库不需要新的写入。
        </div>
      ) : (
        <div className="mt-4 space-y-2">
          {visibleSuggestions.map(suggestion => {
            // REQ-5：业务日以建议自带的 generatedForDate 为准（可能是历史业务日）
            const businessDate = formatSuggestionBusinessDate(suggestion.generatedForDate);
            return (
            <article
              key={suggestion.id}
              className="rounded-lg border border-slate-200 bg-white px-3 py-3"
            >
              <div className="flex min-w-0 flex-col gap-3 md:flex-row md:items-start md:justify-between">
                <div className="min-w-0">
                  <div className="flex min-w-0 flex-wrap items-center gap-2">
                    <h3 className="min-w-0 break-words text-sm font-semibold text-slate-950">
                      {displayName(suggestion)}
                    </h3>
                    <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">
                      待写入 App 知识库
                    </span>
                    {businessDate && (
                      <span
                        data-suggestion-date={businessDate}
                        className="rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700"
                        title="该建议归属的业务日（后端 generatedForDate）"
                      >
                        业务日 {businessDate}
                      </span>
                    )}
                  </div>

                  <p className="mt-1 text-xs text-slate-600">
                    {suggestion.sampleCount.toLocaleString('zh-CN')} 个样本 ·{' '}
                    {formatDuration(suggestion.totalDurationSeconds)}
                    {suggestion.currentCategory ? ` · 当前 ${suggestion.currentCategory}` : ''}
                  </p>

                  <p className="mt-1 text-xs text-cyan-700">
                    建议上下文：{targetText(suggestion)}
                  </p>
                </div>

                <div className="flex shrink-0 flex-wrap gap-2 md:justify-end">
                  <button
                    type="button"
                    onClick={() => onPreview(suggestion)}
                    className="pim-button-primary min-h-8 px-3 py-1.5 text-xs font-medium"
                  >
                    预览并确认
                  </button>
                  <button
                    type="button"
                    onClick={() => onReject(suggestion)}
                    className="pim-button-secondary min-h-8 px-3 py-1.5 text-xs font-medium"
                  >
                    忽略
                  </button>
                </div>
              </div>
            </article>
            );
          })}

          {suggestions.length > visibleSuggestions.length && (
            <p className="px-1 text-xs text-slate-500">
              还有 {(suggestions.length - visibleSuggestions.length).toLocaleString('zh-CN')} 项待确认上下文。
            </p>
          )}
        </div>
      )}
    </section>
  );
}
