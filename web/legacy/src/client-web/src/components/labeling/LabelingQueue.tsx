import { useCallback, useEffect, useState } from 'react';
import {
  fetchLabelingQueue,
  fetchCategoryDictionary,
  submitLabel,
  type LabelingQueueItem,
  type CategoryDictionaryItem,
} from '../../api/classificationLabeling';
import { buildLabelRequest, mergeCustomCategories } from './labelingUtils';

const CUSTOM_CATS_STORAGE_KEY = 'pim_custom_cats';

export interface LabelingQueueData {
  items: LabelingQueueItem[];
  dictionary: CategoryDictionaryItem[];
}

export type LabelingQueueDataFetcher = (limit: number) => Promise<LabelingQueueData>;

const defaultFetchData: LabelingQueueDataFetcher = limit =>
  Promise.all([fetchLabelingQueue(limit), fetchCategoryDictionary()]).then(([queue, dict]) => ({
    items: queue.items ?? [],
    dictionary: dict ?? [],
  }));

export function loadCustomCategories(): string[] {
  try {
    const raw = localStorage.getItem(CUSTOM_CATS_STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

export function saveCustomCategories(names: string[]) {
  try {
    localStorage.setItem(CUSTOM_CATS_STORAGE_KEY, JSON.stringify(names));
  } catch {
    // localStorage unavailable
  }
}

export function LabelingCard({
  item,
  dictionary,
  customCats,
  onLabel,
  onAddCustom,
  defaultExpanded = false,
}: {
  item: LabelingQueueItem;
  dictionary: CategoryDictionaryItem[];
  customCats: string[];
  onLabel: (categoryName: string, scope?: 'all' | 'keyword', keyword?: string) => void;
  onAddCustom: (name: string) => void;
  defaultExpanded?: boolean;
}) {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const [keyword, setKeyword] = useState('');
  const [keywordMode, setKeywordMode] = useState(false);

  const chips = mergeCustomCategories(dictionary.map(c => c.name), customCats);

  const typeLabel = item.targetType === 'domain' ? '域名' : item.targetType === 'mobile_app' ? '手机应用' : '应用';

  function handleCustomKeyDown(event: { key: string; preventDefault: () => void; currentTarget: { value: string } }) {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    const value = event.currentTarget.value.trim();
    if (!value) return;
    onAddCustom(value);
    onLabel(value, keywordMode ? 'keyword' : 'all', keywordMode ? keyword : undefined);
  }

  function handleChip(categoryName: string) {
    onLabel(categoryName, keywordMode ? 'keyword' : 'all', keywordMode ? keyword : undefined);
  }

  return (
    <div className="q-item rounded-xl border border-slate-200 bg-slate-50">
      <button
        type="button"
        className="q-head flex w-full items-center justify-between gap-3 px-3 py-2 text-left"
        onClick={() => setExpanded(v => !v)}
      >
        <span className="min-w-0">
          <span className="block truncate text-sm font-medium text-slate-900">{item.displayName}</span>
          <span className="block truncate text-xs text-slate-500">{typeLabel} · {item.minutes} 分钟</span>
        </span>
        <span className="shrink-0 text-slate-400">{expanded ? '▾' : '▸'}</span>
      </button>

      {expanded && (
        <div className="q-body border-t border-slate-200 px-3 py-3">
          {item.sampleTitles.length > 0 && (
            <p className="mb-2 truncate text-xs text-slate-500">{item.sampleTitles[0]}</p>
          )}

          <div className="chips flex flex-wrap gap-1.5">
            {chips.map(name => (
              <button
                key={name}
                type="button"
                onClick={() => handleChip(name)}
                className="rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs text-slate-700 hover:border-blue-300 hover:bg-blue-50 hover:text-blue-700"
              >
                {name}
              </button>
            ))}
          </div>

          <div className="q-custom mt-2 flex items-center gap-2">
            <input
              type="text"
              placeholder="自定义分类，回车添加…"
              maxLength={64}
              onKeyDown={handleCustomKeyDown}
              className="h-8 min-w-0 flex-1 rounded-md border border-slate-200 bg-white px-2 text-xs text-slate-900"
            />
          </div>

          {item.targetType === 'domain' && (
            <div className="q-scope mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-600">
              <label className="inline-flex items-center gap-1">
                <input
                  type="radio"
                  name={`scope-${item.target}`}
                  checked={!keywordMode}
                  onChange={() => setKeywordMode(false)}
                />
                所有情况
              </label>
              <label className="inline-flex items-center gap-1">
                <input
                  type="radio"
                  name={`scope-${item.target}`}
                  checked={keywordMode}
                  onChange={() => setKeywordMode(true)}
                />
                仅含关键词页面
              </label>
              {keywordMode && (
                <input
                  type="text"
                  placeholder="关键词（如：教程）"
                  value={keyword}
                  onChange={e => setKeyword(e.target.value)}
                  className="h-8 w-40 rounded-md border border-slate-200 bg-white px-2 text-xs text-slate-900"
                />
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function LabelingQueue({
  limit = 20,
  compact = false,
  fetchData = defaultFetchData,
  initialItems = [],
  initialDictionary = [],
  initialLoading = true,
}: {
  limit?: number;
  compact?: boolean;
  fetchData?: LabelingQueueDataFetcher;
  initialItems?: LabelingQueueItem[];
  initialDictionary?: CategoryDictionaryItem[];
  initialLoading?: boolean;
}) {
  const [items, setItems] = useState<LabelingQueueItem[]>(initialItems);
  const [dictionary, setDictionary] = useState<CategoryDictionaryItem[]>(initialDictionary);
  const [customCats, setCustomCats] = useState<string[]>(() => loadCustomCategories());
  const [loading, setLoading] = useState(initialLoading);
  const [recentResult, setRecentResult] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    fetchData(limit)
      .then(({ items: queueItems, dictionary: dict }) => {
        if (cancelled) return;
        setItems(queueItems);
        setDictionary(dict);
        setLoading(false);
      })
      .catch(() => {
        if (cancelled) return;
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, [limit, fetchData]);

  const handleAddCustom = useCallback((name: string) => {
    setCustomCats(prev => {
      const next = prev.includes(name) ? prev : [...prev, name];
      saveCustomCategories(next);
      return next;
    });
  }, []);

  const handleLabel = useCallback((item: LabelingQueueItem) => (
    categoryName: string,
    scope: 'all' | 'keyword' = 'all',
    keyword?: string,
  ) => {
    void submitLabel(buildLabelRequest(item, categoryName, scope, keyword))
      .then(result => {
        setRecentResult(`已归入「${result.categoryName || categoryName}」`);
        setItems(prev => prev.filter(x => x.target !== item.target || x.targetType !== item.targetType));
      })
      .catch(() => {
        setRecentResult('提交失败，请重试');
      });
  }, []);

  if (loading) {
    return <p className="text-sm text-slate-500">正在加载待分类项…</p>;
  }

  if (items.length === 0) {
    return (
      <div>
        {recentResult && <p className="mb-2 text-sm text-emerald-600">{recentResult}</p>}
        <p className="text-sm text-slate-500">暂无待分类项</p>
      </div>
    );
  }

  return (
    <div className={compact ? 'space-y-2' : 'space-y-3'}>
      {recentResult && <p className="text-sm text-emerald-600">{recentResult}</p>}
      {items.map((item, index) => (
        <LabelingCard
          key={`${item.targetType}-${item.target}`}
          item={item}
          dictionary={dictionary}
          customCats={customCats}
          defaultExpanded={index === 0}
          onLabel={handleLabel(item)}
          onAddCustom={handleAddCustom}
        />
      ))}
    </div>
  );
}

export default LabelingQueue;
