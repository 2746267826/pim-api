import { useMutation } from '@tanstack/react-query';
import { previewDataCenterBatch } from '../../api/calendar';
import type { DataCenterItem } from '../../types';

interface DataCenterBatchPreviewProps {
  selected?: DataCenterItem;
}

export default function DataCenterBatchPreview({ selected }: DataCenterBatchPreviewProps) {
  const previewMutation = useMutation({
    mutationFn: () => previewDataCenterBatch({
      action: 'archive',
      objects: selected ? [{ objectType: selected.objectType, objectId: selected.objectId }] : [],
      reason: '数据中心批量影响预览',
    }),
  });

  return (
    <section className="pim-panel min-w-0 p-4" aria-label="批量影响预览">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-slate-950">批量影响预览</h2>
        <button
          type="button"
          disabled={!selected || previewMutation.isPending}
          onClick={() => previewMutation.mutate()}
          className="pim-button-secondary px-3 py-1.5 text-xs disabled:cursor-not-allowed disabled:opacity-50"
        >
          生成预览
        </button>
      </div>
      {previewMutation.data ? (
        <div className="mt-3 space-y-2 text-sm">
          <p className="font-semibold text-slate-800">{previewMutation.data.riskLevel}</p>
          <p className="text-slate-500">{previewMutation.data.summary}</p>
          <p className="text-xs text-red-600">
            严格确认：{previewMutation.data.requiresStrictConfirmation ? '需要' : '不需要'}
          </p>
        </div>
      ) : (
        <p className="mt-3 rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
          选择对象后可生成批量影响预览。
        </p>
      )}
    </section>
  );
}
