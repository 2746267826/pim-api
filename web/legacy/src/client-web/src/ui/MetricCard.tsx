import type { ReactNode } from 'react';

type MetricTone = 'primary' | 'activity' | 'warning' | 'danger' | 'neutral';

const valueClass: Record<MetricTone, string> = {
  primary: 'text-blue-600',
  activity: 'text-teal-600',
  warning: 'text-amber-600',
  danger: 'text-red-600',
  neutral: 'text-slate-950',
};

export default function MetricCard({
  label,
  value,
  helper,
  tone = 'neutral',
  dense = false,
}: {
  label: string;
  value: ReactNode;
  helper?: ReactNode;
  tone?: MetricTone;
  /** 紧凑模式：更小的内边距与字号（用于高密度信息区，如 PC 记录概览）。 */
  dense?: boolean;
}) {
  return (
    <section className={`pim-card min-w-0 ${dense ? 'p-3' : 'p-4'}`}>
      <p className={`text-xs text-slate-500 truncate ${dense ? 'mb-1' : 'mb-2'}`}>{label}</p>
      <div className={`min-w-0 break-words font-semibold ${dense ? 'text-lg' : 'text-xl'} ${valueClass[tone]}`}>{value}</div>
      {helper && <p className={`text-xs text-slate-400 truncate ${dense ? 'mt-1' : 'mt-2'}`}>{helper}</p>}
    </section>
  );
}
