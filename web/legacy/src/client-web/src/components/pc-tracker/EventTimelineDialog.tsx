import { useEffect, useId, useMemo, useRef } from 'react';
import { format } from 'date-fns';
import type { TimelineItem } from '../../types';

const PRODUCTIVE_CATS = ['工作', '编程', '文档', '学习', '邮件', '终端'];
const DISTRACTING_CATS = ['游戏', '视频', '娱乐', '社交'];

function getBadge(category: string): { label: string; className: string } {
  if (PRODUCTIVE_CATS.some(c => category.includes(c))) return { label: 'P', className: 'bg-emerald-400' };
  if (DISTRACTING_CATS.some(c => category.includes(c))) return { label: 'D', className: 'bg-rose-400' };
  return { label: 'N', className: 'bg-slate-300 text-slate-500' };
}

interface Props {
  open: boolean;
  timeline: TimelineItem[];
  dateStr: string;
  onClose: () => void;
}

export default function EventTimelineDialog({ open, timeline, dateStr, onClose }: Props) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    const dialog = dialogRef.current;
    dialog?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function handleKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [open, onClose]);

  const { entries, totalMinutes, productivePercent } = useMemo(() => {
    if (!timeline.length) return { entries: [], totalMinutes: 0, productivePercent: 0 };

    const parsed = timeline
      .filter(item => item.start && item.end)
      .map(item => ({
        start: new Date(item.start),
        end: new Date(item.end),
        appName: item.appName || '',
        windowTitle: item.windowTitle,
        categoryName: item.categoryName || '其他',
        categoryColor: item.categoryColor || '#64748b',
        durationMinutes: item.durationMinutes || (new Date(item.end).getTime() - new Date(item.start).getTime()) / 60000,
      }))
      .sort((a, b) => a.start.getTime() - b.start.getTime());

    const total = parsed.reduce((s, e) => s + e.durationMinutes, 0);
    const prod = parsed
      .filter(e => PRODUCTIVE_CATS.some(c => e.categoryName.includes(c)))
      .reduce((s, e) => s + e.durationMinutes, 0);

    return { entries: parsed, totalMinutes: total, productivePercent: total > 0 ? Math.round(prod / total * 100) : 0 };
  }, [timeline]);

  const maxDuration = useMemo(
    () => Math.max(...entries.map(e => e.durationMinutes), 1),
    [entries]
  );

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center px-4 py-8">
      <div className="fixed inset-0 bg-slate-950/40 backdrop-blur-sm" onClick={onClose} />
      <section
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className="relative flex max-h-full w-full max-w-[640px] flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl outline-none"
      >
        {/* Header */}
        <header className="flex shrink-0 items-center justify-between border-b border-slate-100 px-5 py-4">
          <h3 id={titleId} className="text-sm font-semibold text-slate-900">
            详细时间线 · {format(new Date(dateStr), 'M月d日 EEEE')}
          </h3>
          <button
            type="button"
            onClick={onClose}
            className="flex h-8 w-8 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700"
          >
            ✕
          </button>
        </header>

        {/* Body */}
        <div className="overflow-y-auto px-5 py-4">
          {/* Summary */}
          <div className="mb-4 flex flex-wrap items-center justify-between gap-2 text-xs">
            <div className="flex items-center gap-3">
              <span className="font-semibold text-slate-700">
                共计 {totalMinutes} 分钟
              </span>
              <span className="text-slate-400">
                {entries.length} 条事件
              </span>
              <span className="font-medium text-emerald-600">
                专注 {productivePercent}%
              </span>
            </div>
            <div className="flex items-center gap-2 text-[10px] text-slate-400">
              <span className="flex items-center gap-1">
                <span className="inline-block h-2 w-2 rounded-full bg-emerald-400" /> 专注
              </span>
              <span className="flex items-center gap-1">
                <span className="inline-block h-2 w-2 rounded-full bg-slate-300" /> 中性
              </span>
              <span className="flex items-center gap-1">
                <span className="inline-block h-2 w-2 rounded-full bg-rose-400" /> 休闲
              </span>
            </div>
          </div>

          {/* Entries */}
          {entries.length === 0 ? (
            <div className="py-10 text-center text-sm text-slate-400">暂无时间线数据</div>
          ) : (
            <div className="space-y-0.5">
              {entries.map((entry, i) => {
                const badge = getBadge(entry.categoryName);
                const barWidth = Math.max((entry.durationMinutes / maxDuration) * 100, 3);
                return (
                  <div
                    key={i}
                    className="group flex items-start gap-2.5 rounded-lg px-2 py-2 transition-colors hover:bg-slate-50"
                  >
                    {/* Time column */}
                    <div className="w-[66px] shrink-0 pt-0.5 text-right">
                      <span className="text-[11px] font-medium text-slate-600">
                        {format(entry.start, 'HH:mm')}
                      </span>
                      <span className="mx-0.5 text-[10px] text-slate-400">-</span>
                      <span className="text-[11px] text-slate-500">
                        {format(entry.end, 'HH:mm')}
                      </span>
                    </div>

                    {/* Dot + connector */}
                    <div className="flex shrink-0 flex-col items-center pt-1">
                      <div
                        className="h-2.5 w-2.5 rounded-full border-2 border-white shadow-sm"
                        style={{ backgroundColor: entry.categoryColor }}
                      />
                      <div
                        className="mt-0.5 w-px flex-1 bg-slate-200"
                        style={{ minHeight: i < entries.length - 1 ? '100%' : '0' }}
                      />
                    </div>

                    {/* Content */}
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5">
                        <span className="truncate text-xs font-semibold text-slate-800">
                          {entry.appName}
                        </span>
                        <span
                          className={`inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full text-[7px] font-bold text-white ${badge.className}`}
                        >
                          {badge.label}
                        </span>
                        <span className="shrink-0 text-[10px] text-slate-400">
                          {entry.durationMinutes.toFixed(0)}m
                        </span>
                      </div>
                      {entry.windowTitle && (
                        <div className="mt-0.5 truncate text-[10px] leading-tight text-slate-400">
                          {entry.windowTitle}
                        </div>
                      )}
                      <div className="mt-1 h-1 max-w-[200px] overflow-hidden rounded-full bg-slate-100">
                        <div
                          className="h-full rounded-full transition-all duration-200"
                          style={{ width: `${barWidth}%`, backgroundColor: entry.categoryColor }}
                        />
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
