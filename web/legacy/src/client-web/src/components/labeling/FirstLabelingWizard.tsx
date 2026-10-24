import { useCallback, useEffect, useState } from 'react';
import {
  fetchLabelingQueue,
  fetchCategoryDictionary,
  submitLabel,
  type LabelingQueueItem,
  type CategoryDictionaryItem,
} from '../../api/classificationLabeling';
import { LabelingCard, loadCustomCategories, saveCustomCategories } from './LabelingQueue';

export function FirstLabelingWizard({
  onDone,
}: {
  onDone?: () => void;
}) {
  const [items, setItems] = useState<LabelingQueueItem[]>([]);
  const [dictionary, setDictionary] = useState<CategoryDictionaryItem[]>([]);
  const [customCats, setCustomCats] = useState<string[]>(() => loadCustomCategories());
  const [initialTotal, setInitialTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([fetchLabelingQueue(50, 'wizard'), fetchCategoryDictionary()])
      .then(([queue, dict]) => {
        if (cancelled) return;
        const apps = (queue.items ?? []).filter(item => item.targetType === 'app');
        setItems(apps);
        setInitialTotal(apps.length);
        setDictionary(dict ?? []);
        setLoading(false);
      })
      .catch(() => {
        if (cancelled) return;
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, []);

  const handleAddCustom = useCallback((name: string) => {
    setCustomCats(prev => {
      const next = prev.includes(name) ? prev : [...prev, name];
      saveCustomCategories(next);
      return next;
    });
  }, []);

  const handleLabel = useCallback((item: LabelingQueueItem) => (categoryName: string) => {
    void submitLabel({
      targetType: item.targetType,
      target: item.target,
      categoryName,
      scope: 'all',
    })
      .then(() => {
        setItems(prev => prev.filter(x => x.target !== item.target || x.targetType !== item.targetType));
        setError(null);
      })
      .catch(() => setError('提交失败，请重试'));
  }, []);

  const handleSkip = useCallback((item: LabelingQueueItem) => {
    setItems(prev => prev.filter(x => x.target !== item.target || x.targetType !== item.targetType));
  }, []);

  const total = items.length;
  const labeledCount = initialTotal - total;
  if (loading) {
    return <p className="text-sm text-slate-500">正在加载待打标应用…</p>;
  }

  if (total === 0 && done) {
    return (
      <div className="space-y-3">
        <p className="text-sm font-medium text-slate-900">打标完成</p>
        <p className="text-sm text-slate-500">感谢参与，Top 50 应用问卷已结束。</p>
      </div>
    );
  }

  if (total === 0) {
    return (
      <div className="space-y-3">
        <p className="text-sm font-medium text-slate-900">打标完成</p>
        <p className="text-sm text-slate-500">暂无待打标应用。可稍后再来补充。{error ?? ''}</p>
        <button
          type="button"
          onClick={() => { setDone(true); onDone?.(); }}
          className="pim-button-primary h-8 px-3 text-xs font-medium"
        >
          完成
        </button>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold text-slate-900">Top 50 应用问卷</h2>
        <span className="text-xs text-slate-500">已打标 {labeledCount} / 总数 {initialTotal}</span>
      </div>
      <p className="text-xs text-slate-500">
        {dictionary.length} 个分类可选，可直接跳过不感兴趣的应用。
      </p>
      {error && <p className="text-sm text-red-500">{error}</p>}
      <div className="space-y-2">
        {items.map((item, index) => (
          <div key={`${item.targetType}-${item.target}`} className="space-y-1">
            {item.currentCategory && (
              <p className="text-xs text-slate-500">
                当前分类：<span className="font-medium text-slate-700">{item.currentCategory}</span>
              </p>
            )}
            <LabelingCard
              item={item}
              dictionary={dictionary}
              customCats={customCats}
              defaultExpanded={index === 0}
              onLabel={handleLabel(item)}
              onAddCustom={handleAddCustom}
            />
            <button
              type="button"
              onClick={() => handleSkip(item)}
              className="text-xs text-slate-400 hover:text-slate-600"
            >
              跳过
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

export default FirstLabelingWizard;
