import { addPcDays, formatPcDateCn, getPcBusinessDate } from '../../utils/pcBusinessDay';

const DIMENSIONS = [
  { key: 'hour' as const, label: '时' },
  { key: 'day' as const, label: '日' },
  { key: 'month' as const, label: '月' },
  { key: 'year' as const, label: '年' },
];

interface Props {
  date: Date;
  dimension: 'hour' | 'day' | 'month' | 'year';
  onDateChange: (d: Date) => void;
  onDimensionChange: (dim: 'hour' | 'day' | 'month' | 'year') => void;
}

export default function DateDimensionBar({ date, dimension, onDateChange, onDimensionChange }: Props) {
  return (
    <div className="flex max-w-full flex-wrap items-center justify-end gap-2">
      <div className="flex min-w-0 max-w-full flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1">
        <button
          type="button"
          className="shrink-0 rounded-lg bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white transition-colors hover:bg-blue-700 sm:px-3"
          onClick={() => onDateChange(getPcBusinessDate())}
        >
          今天
        </button>
        <button
          type="button"
          className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 transition-colors hover:border-blue-200 hover:text-blue-700 sm:px-2.5"
          onClick={() => onDateChange(addPcDays(date, -1))}
          aria-label="前一天"
        >
          前
        </button>
        <button
          type="button"
          className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 transition-colors hover:border-blue-200 hover:text-blue-700 sm:px-2.5"
          onClick={() => onDateChange(addPcDays(date, 1))}
          aria-label="后一天"
        >
          后
        </button>
        <span className="min-w-0 max-w-[11rem] truncate px-1 text-sm font-semibold text-slate-900 sm:max-w-[15rem] sm:px-2">
          {formatPcDateCn(date)}
        </span>
      </div>

      <div className="flex max-w-full shrink-0 flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1">
        {DIMENSIONS.map(d => (
          <button
            key={d.key}
            type="button"
            className={`rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors sm:px-3 ${
              dimension === d.key
                ? 'bg-teal-600 text-white shadow-sm'
                : 'text-slate-500 hover:bg-white hover:text-slate-800'
            }`}
            onClick={() => onDimensionChange(d.key)}
          >
            {d.label}
          </button>
        ))}
      </div>
    </div>
  );
}
