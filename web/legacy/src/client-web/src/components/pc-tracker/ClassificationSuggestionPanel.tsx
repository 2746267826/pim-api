import { useState } from 'react';
import type { ActivityClassificationSuggestion } from '../../types';

interface Props {
  suggestions: ActivityClassificationSuggestion[];
  isLoading: boolean;
  onAccept: (suggestion: ActivityClassificationSuggestion) => void;
  onCorrect: (suggestion: ActivityClassificationSuggestion) => void;
  onReject: (suggestion: ActivityClassificationSuggestion) => void;
  onBatchAccept: (ids: string[]) => void;
  onBatchReject: (ids: string[]) => void;
}

function formatMinutes(seconds: number) {
  const minutes = Math.round((seconds / 60) * 10) / 10;
  return `${minutes.toLocaleString('zh-CN')} 分钟`;
}

function getEmojiForApp(appIcon?: string | null, clusterKey?: string): string {
  if (appIcon) return appIcon;
  if (!clusterKey) return '❓';
  if (clusterKey.startsWith('web:')) return '🌐';
  if (clusterKey.startsWith('app:')) return '📱';
  return '❓';
}

function getRecognitionBadge(recognitionSource?: string | null, isOnlineLookup?: boolean) {
  if (isOnlineLookup || recognitionSource === 'online') {
    return (
      <span className="inline-block rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">
        🌐 联网识别
      </span>
    );
  }
  if (recognitionSource === 'builtin' || recognitionSource === 'manual' || recognitionSource === 'signature') {
    return (
      <span className="inline-block rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
        📦 内置识别
      </span>
    );
  }
  return (
    <span className="inline-block rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">
      启发式 💡
    </span>
  );
}

export default function ClassificationSuggestionPanel({
  suggestions,
  isLoading,
  onAccept,
  onCorrect,
  onReject,
  onBatchAccept,
  onBatchReject,
}: Props) {
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());

  if (isLoading) {
    return (
      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        正在加载分类建议...
      </div>
    );
  }

  const visibleSuggestions = suggestions.slice(0, 10);

  if (visibleSuggestions.length === 0) {
    return (
      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        暂无需要处理的分类建议。
      </div>
    );
  }

  const toggleSelect = (id: string) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleSelectAll = () => {
    if (selectedIds.size === visibleSuggestions.length) {
      setSelectedIds(new Set());
    } else {
      setSelectedIds(new Set(visibleSuggestions.map(s => s.id)));
    }
  };

  return (
    <div className="space-y-2">
      {visibleSuggestions.map(suggestion => {
        const displayName = suggestion.appDisplayName || suggestion.clusterKey;
        const icon = getEmojiForApp(suggestion.appIcon, suggestion.clusterKey);
        const isSelected = selectedIds.has(suggestion.id);
        const appName = suggestion.clusterKey?.startsWith('app:')
          ? suggestion.clusterKey.slice(4)
          : '';

        return (
          <div
            key={suggestion.id}
            className={`flex min-w-0 flex-col gap-3 rounded-lg border px-3 py-3 transition-colors md:flex-row md:items-start md:justify-between ${
              isSelected
                ? 'border-blue-300 bg-blue-50'
                : 'border-slate-200 bg-white'
            }`}
          >
            <div className="flex min-w-0 items-start gap-3">
              {/* Checkbox for batch */}
              <input
                type="checkbox"
                className="mt-1 h-4 w-4 shrink-0 accent-blue-600"
                checked={isSelected}
                onChange={() => toggleSelect(suggestion.id)}
              />

              {/* App icon */}
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-lg">
                {icon}
              </span>

              <div className="min-w-0 flex-1">
                {/* App display name + recognition badge */}
                <div className="flex flex-wrap items-center gap-2">
                  <span className="truncate text-sm font-semibold text-slate-950">
                    {displayName}
                  </span>
                  {getRecognitionBadge(suggestion.recognitionSource, (suggestion as any).isOnlineLookup)}
                  {appName && displayName !== appName && (
                    <span className="truncate text-xs text-slate-400">
                      {appName}
                    </span>
                  )}
                </div>

                {/* Stats row */}
                <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-500">
                  <span>
                    样本 <strong className="text-slate-700">{suggestion.sampleCount.toLocaleString('zh-CN')}</strong>
                  </span>
                  <span>
                    时长 <strong className="text-slate-700">{formatMinutes(suggestion.totalDurationSeconds)}</strong>
                  </span>
                  {suggestion.currentCategory && (
                    <span>
                      当前 <strong className="text-slate-700">{suggestion.currentCategory}</strong>
                    </span>
                  )}
                </div>

                {/* Suggested category */}
                {suggestion.suggestedCategory && (
                  <div className="mt-1.5 text-xs text-blue-600">
                    建议 → <span className="font-medium">{suggestion.suggestedCategory}</span>
                    <span className="ml-1 text-green-600">99% 置信</span>
                  </div>
                )}
              </div>
            </div>

            {/* Action buttons */}
            <div className="flex shrink-0 gap-2 pl-9 md:pl-0">
              <button
                type="button"
                onClick={() => onAccept(suggestion)}
                className="rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-blue-700"
              >
                接受
              </button>
              <button
                type="button"
                onClick={() => onCorrect(suggestion)}
                className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:bg-slate-50"
              >
                修改
              </button>
              <button
                type="button"
                onClick={() => onReject(suggestion)}
                className="rounded-lg border border-red-200 bg-white px-3 py-1.5 text-xs font-medium text-red-500 transition-colors hover:bg-red-50"
              >
                拒绝
              </button>
            </div>
          </div>
        );
      })}

      {/* Batch action bar */}
      {visibleSuggestions.length > 0 && (
        <div className="flex items-center gap-3 border-t border-slate-100 px-1 pt-3">
          <input
            type="checkbox"
            className="h-4 w-4 accent-blue-600"
            checked={selectedIds.size === visibleSuggestions.length}
            onChange={toggleSelectAll}
          />
          <span className="text-xs text-slate-500">
            {selectedIds.size > 0
              ? `已选 ${selectedIds.size} 项`
              : '全选'}
          </span>
          {selectedIds.size > 0 && (
            <>
              <button
                onClick={() => {
                  onBatchAccept([...selectedIds]);
                  setSelectedIds(new Set());
                }}
                className="rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-blue-700"
              >
                批量接受
              </button>
              <button
                onClick={() => {
                  onBatchReject([...selectedIds]);
                  setSelectedIds(new Set());
                }}
                className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:bg-slate-50"
              >
                批量拒绝
              </button>
              <span className="ml-auto text-xs text-slate-400">
                {visibleSuggestions.length < suggestions.length
                  ? `还有 ${suggestions.length - visibleSuggestions.length} 条未显示`
                  : ''}
              </span>
            </>
          )}
        </div>
      )}
    </div>
  );
}
