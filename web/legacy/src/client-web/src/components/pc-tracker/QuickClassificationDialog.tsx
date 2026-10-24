import { useEffect, useId, useMemo, useState } from 'react';
import type {
  ActivityClassificationApplyRange,
  ActivityClassificationPreview,
  ActivityClassificationSuggestion,
  SaveActivityClassificationRuleRequest,
} from '../../types';

const categoryColors: Record<string, string> = {
  编程: '#6B5EE4',
  终端: '#E05A7A',
  沟通: '#F5935A',
  办公: '#F59E0B',
  文件: '#3B82F6',
  浏览: '#0EA8A0',
  学习: '#2563EB',
  娱乐: '#EC4899',
  其他: '#64748b',
};

const categoryOptions = Object.keys(categoryColors);

interface Props {
  suggestion: ActivityClassificationSuggestion | null;
  date: string;
  recentProjectTags: string[];
  preview?: ActivityClassificationPreview | null;
  isPreviewing: boolean;
  isApplying: boolean;
  onClose: () => void;
  onDraftChange: () => void;
  onPreview: (rule: SaveActivityClassificationRuleRequest, range: ActivityClassificationApplyRange) => void;
  onApply: (rule: SaveActivityClassificationRuleRequest, range: ActivityClassificationApplyRange) => void;
}

function getClusterParts(clusterKey: string) {
  const separator = clusterKey.indexOf(':');
  if (separator < 0) return { prefix: '', value: clusterKey.trim() };
  return {
    prefix: clusterKey.slice(0, separator).trim().toLowerCase(),
    value: clusterKey.slice(separator + 1).trim(),
  };
}

function buildConditionsJson(clusterKey: string) {
  const { prefix, value } = getClusterParts(clusterKey);
  if (!value || (prefix !== 'web' && prefix !== 'app')) {
    return null;
  }

  const condition = prefix === 'web'
    ? { field: 'domain', op: 'domainSuffix', value }
    : { field: 'appNameNormalized', op: 'equals', value };

  return JSON.stringify({ all: [condition] });
}

function formatMinutes(seconds: number) {
  const minutes = Math.round((seconds / 60) * 10) / 10;
  return `${minutes.toLocaleString('zh-CN')} 分钟`;
}

function compactEntries(entries: Record<string, number>) {
  return Object.entries(entries)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 4)
    .map(([name, count]) => `${name || '未分类'} ${count}`)
    .join('、');
}

export default function QuickClassificationDialog({
  suggestion,
  date,
  recentProjectTags,
  preview,
  isPreviewing,
  isApplying,
  onClose,
  onDraftChange,
  onPreview,
  onApply,
}: Props) {
  const titleId = useId();
  const projectTagListId = useId();
  const [categoryName, setCategoryName] = useState('其他');
  const [projectTag, setProjectTag] = useState('');
  const [rangeMode, setRangeMode] = useState<ActivityClassificationApplyRange['mode']>('today');
  const [dateFrom, setDateFrom] = useState(date);
  const [dateTo, setDateTo] = useState(date);

  useEffect(() => {
    if (!suggestion) return;

    setCategoryName(suggestion.suggestedCategory || suggestion.currentCategory || '其他');
    setProjectTag(suggestion.suggestedProjectTag || '');
    setRangeMode('today');
    setDateFrom(date);
    setDateTo(date);
    onDraftChange();
  }, [date, suggestion]);

  const rule = useMemo<SaveActivityClassificationRuleRequest | null>(() => {
    if (!suggestion) return null;

    const trimmedCategory = categoryName.trim() || '其他';
    const trimmedProjectTag = projectTag.trim();
    const clusterValue = getClusterParts(suggestion.clusterKey).value;
    const conditionsJson = buildConditionsJson(suggestion.clusterKey);
    if (!conditionsJson) return null;

    return {
      ruleName: `用户纠错: ${suggestion.clusterKey} ${new Date().toISOString()}`,
      scope: 'both',
      categoryName: trimmedCategory,
      projectTag: trimmedProjectTag || null,
      color: categoryColors[trimmedCategory] || categoryColors['其他'],
      priority: 900,
      conditionsJson,
      confidence: 0.95,
      explanation: `用户快捷纠错，来源建议 ${suggestion.id}，匹配 ${clusterValue}。`,
    };
  }, [categoryName, projectTag, suggestion]);

  if (!suggestion) return null;

  const range: ActivityClassificationApplyRange = {
    mode: rangeMode,
    dateFrom,
    dateTo,
  };
  const canSubmit = Boolean(rule) && categoryName.trim().length > 0;
  const isRangeDisabled = rangeMode !== 'range';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center px-3 py-6">
      <div className="absolute inset-0 bg-slate-950/25" onClick={onClose} />
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="relative flex max-h-full w-full max-w-[640px] flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 id={titleId} className="text-base font-semibold text-slate-950">快捷纠错</h2>
              <p className="mt-1 truncate text-sm text-slate-500">{suggestion.clusterKey}</p>
            </div>
            <button type="button" onClick={onClose} className="pim-button-secondary h-9 shrink-0 px-3 text-sm">
              关闭
            </button>
          </div>
        </header>

        <div className="min-h-0 flex-1 space-y-4 overflow-auto px-5 py-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="min-w-0 text-sm">
              <span className="mb-1 block text-xs font-medium text-slate-500">分类</span>
              <select
                value={categoryName}
                onChange={e => {
                  setCategoryName(e.target.value);
                  onDraftChange();
                }}
                className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-900 outline-none focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
              >
                {categoryOptions.map(category => (
                  <option key={category} value={category}>{category}</option>
                ))}
              </select>
            </label>

            <label className="min-w-0 text-sm">
              <span className="mb-1 block text-xs font-medium text-slate-500">项目标签</span>
              <input
                value={projectTag}
                onChange={e => {
                  setProjectTag(e.target.value);
                  onDraftChange();
                }}
                list={projectTagListId}
                placeholder="可留空"
                className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
              />
              <datalist id={projectTagListId}>
                {recentProjectTags.map(tag => (
                  <option key={tag} value={tag} />
                ))}
              </datalist>
            </label>
          </div>

          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-2">
              {(['today', 'range'] as const).map(mode => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => {
                    setRangeMode(mode);
                    onDraftChange();
                  }}
                  className={`h-10 rounded-lg border px-2 text-sm font-medium transition-colors ${
                    rangeMode === mode
                      ? 'border-blue-600 bg-blue-50 text-blue-700'
                      : 'border-slate-200 bg-white text-slate-600 hover:border-blue-200'
                  }`}
                >
                  {mode === 'today' ? '今天' : '范围'}
                </button>
              ))}
            </div>

            <div className="grid gap-3 sm:grid-cols-2">
              <label className="min-w-0 text-sm">
                <span className="mb-1 block text-xs font-medium text-slate-500">开始日期</span>
                <input
                  type="date"
                  value={dateFrom}
                  disabled={isRangeDisabled}
                  onChange={e => {
                    setDateFrom(e.target.value);
                    onDraftChange();
                  }}
                  className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-900 outline-none disabled:bg-slate-50 disabled:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
                />
              </label>
              <label className="min-w-0 text-sm">
                <span className="mb-1 block text-xs font-medium text-slate-500">结束日期</span>
                <input
                  type="date"
                  value={dateTo}
                  disabled={isRangeDisabled}
                  onChange={e => {
                    setDateTo(e.target.value);
                    onDraftChange();
                  }}
                  className="h-10 w-full rounded-lg border border-slate-200 bg-white px-3 text-sm text-slate-900 outline-none disabled:bg-slate-50 disabled:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100"
                />
              </label>
            </div>
          </div>

          {rule ? (
            <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3 text-xs text-slate-600">
              <div className="break-all font-medium text-slate-800">{rule.conditionsJson}</div>
              <div className="mt-2 grid gap-1 sm:grid-cols-3">
                <span>优先级 {rule.priority}</span>
                <span>置信度 {rule.confidence}</span>
                <span>范围 {rule.scope}</span>
              </div>
            </div>
          ) : (
            <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-3 text-sm text-amber-900">
              暂不支持这个建议类型，无法自动生成安全的纠错规则。
            </div>
          )}

          {preview && (
            <div className="rounded-lg border border-blue-200 bg-blue-50 px-3 py-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 className="text-sm font-semibold text-blue-950">预览结果</h3>
                {preview.requiresConfirmation && (
                  <span className="rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800">
                    需要确认
                  </span>
                )}
              </div>
              <p className="mt-2 text-sm text-blue-900">
                将影响 {preview.affectedRecordCount.toLocaleString('zh-CN')} 条记录，合计 {formatMinutes(preview.affectedDurationSeconds)}。
              </p>
              {preview.summary && <p className="mt-1 break-words text-xs text-blue-700">{preview.summary}</p>}
              <div className="mt-2 space-y-1 text-xs text-blue-800">
                <p className="break-words">当前：{compactEntries(preview.currentCategoryCounts) || '无'}</p>
                <p className="break-words">应用后：{compactEntries(preview.newCategoryCounts) || '无'}</p>
              </div>
            </div>
          )}
        </div>

        <footer className="flex flex-col gap-2 border-t border-slate-200 px-5 py-4 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-slate-500">先预览影响范围，再应用纠错规则。</p>
          <div className="grid grid-cols-2 gap-2 sm:w-[220px]">
            <button
              type="button"
              onClick={() => {
                if (rule) onPreview(rule, range);
              }}
              disabled={!canSubmit || isPreviewing || isApplying}
              className="pim-button-secondary h-10 px-3 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60"
            >
              {isPreviewing ? '预览中' : '预览'}
            </button>
            <button
              type="button"
              onClick={() => {
                if (rule) onApply(rule, range);
              }}
              disabled={!preview || !canSubmit || isPreviewing || isApplying}
              className="pim-button-primary h-10 px-3 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60"
            >
              {isApplying ? '应用中' : '应用'}
            </button>
          </div>
        </footer>
      </section>
    </div>
  );
}
