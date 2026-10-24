import { useEffect, useState } from 'react';
import StatusBadge from '../../ui/StatusBadge';
import { fetchLabelingQueue } from '../../api/classificationLabeling';
import FirstLabelingWizard from '../labeling/FirstLabelingWizard';
import LabelingQueue from '../labeling/LabelingQueue';
import type { ClassificationSuggestionsTodayData, TodaySection } from '../../types';

export default function TodayClassificationSuggestionsSection({
  section,
}: {
  section: TodaySection<ClassificationSuggestionsTodayData>;
}) {
  const { pendingCount } = section.data;
  const [queueEmpty, setQueueEmpty] = useState<boolean | null>(null);
  const [showWizard, setShowWizard] = useState(false);

  // 常规打标队列为空时引导新用户完成 Top 50 问卷（FirstLabelingWizard，走 wizard 数据源）；
  // 向导完成/跳过（onDone）后切回常规打标队列视图（即使仍为空也显示常规空态，避免死循环）。
  useEffect(() => {
    let cancelled = false;
    fetchLabelingQueue(1)
      .then(queue => {
        if (cancelled) return;
        const empty = (queue.items ?? []).length === 0;
        setQueueEmpty(empty);
        if (empty) setShowWizard(true);
      })
      .catch(() => {
        if (cancelled) return;
        setQueueEmpty(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const handleWizardDone = () => {
    setShowWizard(false);
  };

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">分类建议</h2>
        <StatusBadge tone={pendingCount > 0 ? 'warning' : 'neutral'}>{pendingCount} 待处理</StatusBadge>
      </div>

      <div className="space-y-3">
        {queueEmpty === null ? (
          <p className="text-sm text-slate-500">正在加载待分类项…</p>
        ) : showWizard ? (
          <FirstLabelingWizard onDone={handleWizardDone} />
        ) : (
          <LabelingQueue limit={5} compact />
        )}
      </div>
    </section>
  );
}
