import type { ActivityClassificationPreview } from '../../types';

interface Props {
  preview: ActivityClassificationPreview;
}

function formatMinutes(seconds: number) {
  const minutes = Math.round((seconds / 60) * 10) / 10;
  return `${minutes.toLocaleString('zh-CN', { maximumFractionDigits: 1 })} 分钟`;
}

function formatCounts(counts: Record<string, number>) {
  const text = Object.entries(counts)
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([name, count]) => `${name || '未分类'} ${count.toLocaleString('zh-CN')}`)
    .join(' | ');

  return text || '无';
}

export default function RuleImpactPreviewPanel({ preview }: Props) {
  return (
    <section className="rounded-lg border border-cyan-200 bg-cyan-50 p-3 text-sm text-cyan-950">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-semibold">知识库写入影响</h3>
        {preview.requiresConfirmation && (
          <span className="rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800">
            需要确认
          </span>
        )}
      </div>

      <p className="mt-2 text-sm text-cyan-900">
        将影响 {preview.affectedRecordCount.toLocaleString('zh-CN')} 条记录，合计{' '}
        {formatMinutes(preview.affectedDurationSeconds)}。
      </p>

      {preview.summary && (
        <p className="mt-1 break-words text-xs text-cyan-800">{preview.summary}</p>
      )}

      <div className="mt-3 grid gap-1 text-xs text-cyan-900">
        <p className="break-words">当前：{formatCounts(preview.currentCategoryCounts)}</p>
        <p className="break-words">应用后：{formatCounts(preview.newCategoryCounts)}</p>
      </div>

      {preview.samples.length > 0 && (
        <div className="mt-3 border-t border-cyan-200 pt-2">
          <p className="text-xs font-semibold text-cyan-900">样本记录</p>
          <ul className="mt-1 space-y-1">
            {preview.samples.slice(0, 3).map((sample, index) => (
              <li key={sample.recordKey || `${sample.start}-${index}`} className="min-w-0 break-words text-xs text-cyan-800">
                <span className="font-medium">{sample.displayName || sample.appName || sample.domain || '活动'}</span>
                {sample.title && <span className="ml-1 text-cyan-700">{sample.title}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
