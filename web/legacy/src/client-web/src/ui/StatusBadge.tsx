import type { ReactNode } from 'react';

type StatusTone = 'primary' | 'activity' | 'warning' | 'danger' | 'neutral';

const toneClass: Record<StatusTone, string> = {
  primary: 'bg-blue-100 text-blue-700 border-blue-200',
  activity: 'bg-teal-100 text-teal-700 border-teal-200',
  warning: 'bg-amber-100 text-amber-800 border-amber-200',
  danger: 'bg-red-100 text-red-700 border-red-200',
  neutral: 'bg-slate-100 text-slate-600 border-slate-200',
};

export default function StatusBadge({ children, tone = 'neutral' }: { children: ReactNode; tone?: StatusTone }) {
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${toneClass[tone]}`}>
      {children}
    </span>
  );
}
