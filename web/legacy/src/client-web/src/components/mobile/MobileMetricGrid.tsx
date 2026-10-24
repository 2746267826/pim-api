export interface MobileMetricItem {
  label: string;
  value: string;
  helper: string;
  tone?: 'default' | 'good' | 'warning';
}

export default function MobileMetricGrid({ items }: { items: MobileMetricItem[] }) {
  return (
    <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-6">
      {items.map(item => {
        const toneClass = item.tone === 'good'
          ? 'border-teal-200 bg-teal-50'
          : item.tone === 'warning'
            ? 'border-amber-200 bg-amber-50'
            : 'border-slate-200 bg-white';
        return (
          <div key={item.label} className={`min-h-[86px] min-w-0 rounded-md border p-3 ${toneClass}`}>
            <dt className="truncate text-xs font-semibold text-slate-500">{item.label}</dt>
            <dd className="mt-2 truncate text-2xl font-bold tracking-normal text-slate-950">{item.value}</dd>
            <p className="mt-1 truncate text-xs text-slate-500">{item.helper}</p>
          </div>
        );
      })}
    </dl>
  );
}
