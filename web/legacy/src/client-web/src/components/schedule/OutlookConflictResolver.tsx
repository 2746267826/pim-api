import type { DataCenterItem } from '../../types';

interface OutlookConflictResolverProps {
  conflicts: DataCenterItem[];
}

export default function OutlookConflictResolver({ conflicts }: OutlookConflictResolverProps) {
  return (
    <section className="pim-panel p-4" aria-label="Outlook 冲突队列">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-slate-950">冲突队列</h2>
        <span className="rounded-full bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-700">
          {conflicts.length} 个冲突
        </span>
      </div>
      <div className="mt-3 space-y-2">
        {conflicts.map(conflict => (
          <article key={`${conflict.objectType}-${conflict.objectId}`} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm font-semibold text-slate-800">{conflict.title}</p>
              <span className="text-xs text-slate-500">{conflict.status}</span>
            </div>
            <p className="mt-1 text-xs text-slate-500">{conflict.summary}</p>
          </article>
        ))}
        {conflicts.length === 0 && (
          <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            暂无 Outlook 冲突。
          </p>
        )}
      </div>
    </section>
  );
}
