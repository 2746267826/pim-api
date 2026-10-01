import type { ActivityClassificationSuggestion } from '../../types';
import { formatSuggestionBusinessDate } from '../../utils/pcBusinessDay';

interface Props {
  suggestions: ActivityClassificationSuggestion[];
  isLoading: boolean;
  onPreview: (suggestion: ActivityClassificationSuggestion) => void;
  onReject: (suggestion: ActivityClassificationSuggestion) => void;
  onLater?: (suggestion: ActivityClassificationSuggestion) => void;
}

function formatMinutes(seconds: number) {
  const minutes = Math.round((seconds / 60) * 10) / 10;
  return `${minutes.toLocaleString('zh-CN', { maximumFractionDigits: 1 })} 分钟`;
}

function displayName(suggestion: ActivityClassificationSuggestion) {
  return suggestion.appDisplayName || suggestion.clusterKey || '未分类活动';
}

function suggestionBadge(suggestion: ActivityClassificationSuggestion) {
  if (suggestion.recognitionSource === 'builtin' || suggestion.recognitionSource === 'manual') {
    return '已识别';
  }

  if (suggestion.suggestedCategory) {
    return '系统建议';
  }

  return '待处理';
}

export default function ClassificationActionQueue({
  suggestions,
  isLoading,
  onPreview,
  onReject,
  onLater,
}: Props) {
  if (isLoading) {
    return (
      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        正在加载分类任务...
      </div>
    );
  }

  const visibleSuggestions = suggestions.slice(0, 10);

  if (visibleSuggestions.length === 0) {
    return (
      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        暂无待处理分类任务。
      </div>
    );
  }

  return (
    <div className="space-y-2">
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
                  {suggestionBadge(suggestion)}
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
                {suggestion.sampleCount.toLocaleString('zh-CN')} 个样本 |{' '}
                {formatMinutes(suggestion.totalDurationSeconds)}
                {suggestion.currentCategory ? ` | 当前 ${suggestion.currentCategory}` : ''}
              </p>

              {(suggestion.suggestedCategory || suggestion.suggestedProjectTag) && (
                <p className="mt-1 text-xs text-cyan-700">
                  建议 {suggestion.suggestedCategory || '分类不变'}
                  {suggestion.suggestedProjectTag ? ` | 项目 ${suggestion.suggestedProjectTag}` : ''}
                </p>
              )}
            </div>

            <div className="flex shrink-0 flex-wrap gap-2 md:justify-end">
              <button
                type="button"
                onClick={() => onPreview(suggestion)}
                className="pim-button-primary min-h-8 px-3 py-1.5 text-xs font-medium"
              >
                处理并预览
              </button>
              {onLater && (
                <button
                  type="button"
                  onClick={() => onLater(suggestion)}
                  className="pim-button-secondary min-h-8 px-3 py-1.5 text-xs font-medium"
                >
                  稍后
                </button>
              )}
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
          还有 {suggestions.length - visibleSuggestions.length} 项等待处理。
        </p>
      )}
    </div>
  );
}
