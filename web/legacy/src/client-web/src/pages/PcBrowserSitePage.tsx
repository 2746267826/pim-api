import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import PageHeader from '../ui/PageHeader';
import SummaryCards from '../components/pc-browser/SummaryCards';
import SiteList from '../components/pc-browser/SiteList';
import DayTimeline from '../components/pc-browser/DayTimeline';
import TrendChart from '../components/pc-browser/TrendChart';
import ImportDialog from '../components/pc-browser/ImportDialog';
import {
  getPcBrowserDaily,
  getPcBrowserSummary,
  getPcBrowserTimeline,
} from '../api/pcBrowserSite';
import { addPcDays, formatPcDate, formatPcDateCn, getPcBusinessDate } from '../utils/pcBusinessDay';

type DateMode = 'day' | 'range';
type RangeShortcut = '7d' | '30d' | 'custom';

const RANGE_SHORTCUTS: Array<{ key: Exclude<RangeShortcut, 'custom'>; label: string; days: number }> = [
  { key: '7d', label: '近7天', days: 7 },
  { key: '30d', label: '近30天', days: 30 },
];

function ChartCard({
  title,
  subtitle,
  error,
  children,
}: {
  title: string;
  subtitle: string;
  error?: string | null;
  children: React.ReactNode;
}) {
  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-4">
        <h2 className="text-sm font-semibold text-slate-950">{title}</h2>
        <p className="mt-1 text-xs text-slate-500">{subtitle}</p>
      </div>
      {error ? (
        <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-600">{error}</div>
      ) : (
        children
      )}
    </section>
  );
}

export default function PcBrowserSitePage() {
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<DateMode>('day');
  const [selectedDate, setSelectedDate] = useState(() => getPcBusinessDate());
  const [rangeShortcut, setRangeShortcut] = useState<RangeShortcut>('7d');
  const [rangeStart, setRangeStart] = useState(() => formatPcDate(addPcDays(getPcBusinessDate(), -6)));
  const [rangeEnd, setRangeEnd] = useState(() => formatPcDate(getPcBusinessDate()));
  const [importOpen, setImportOpen] = useState(false);

  const dateStr = formatPcDate(selectedDate);

  const summaryQuery = useQuery({
    queryKey: ['pc-browser-summary', mode, dateStr, rangeStart, rangeEnd],
    queryFn: () => getPcBrowserSummary(mode === 'day' ? { date: dateStr } : { from: rangeStart, to: rangeEnd }),
  });

  const timelineQuery = useQuery({
    queryKey: ['pc-browser-timeline', dateStr],
    queryFn: () => getPcBrowserTimeline(dateStr),
    enabled: mode === 'day',
  });

  const dailyQuery = useQuery({
    queryKey: ['pc-browser-daily', rangeStart, rangeEnd],
    queryFn: () => getPcBrowserDaily({ from: rangeStart, to: rangeEnd }),
    enabled: mode === 'range',
  });

  function switchShortcut(shortcut: RangeShortcut) {
    setRangeShortcut(shortcut);
    if (shortcut !== 'custom') {
      const days = RANGE_SHORTCUTS.find(item => item.key === shortcut)?.days ?? 7;
      const today = formatPcDate(getPcBusinessDate());
      setRangeStart(formatPcDate(addPcDays(getPcBusinessDate(), -(days - 1))));
      setRangeEnd(today);
    }
  }

  function handleImportSuccess() {
    queryClient.invalidateQueries({ queryKey: ['pc-browser'] });
  }

  const summaryError = summaryQuery.error instanceof Error ? summaryQuery.error.message : null;
  const rangeLabel = `${rangeStart} ~ ${rangeEnd}`;

  return (
    <div className="mx-auto w-full max-w-[1500px] space-y-4 pb-8">
      <PageHeader
        title="浏览器使用"
        subtitle="来自 time-tracker-4-browser 插件通道的域名级浏览数据"
        actions={
          <div className="flex min-w-0 max-w-full flex-wrap items-center justify-end gap-2">
            <div className="flex shrink-0 items-center gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1">
              {([['day', '单日'], ['range', '范围']] as const).map(([value, label]) => (
                <button
                  key={value}
                  type="button"
                  className={`rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors sm:px-3 ${
                    mode === value
                      ? 'bg-blue-600 text-white shadow-sm'
                      : 'text-slate-500 hover:bg-white hover:text-slate-800'
                  }`}
                  onClick={() => setMode(value)}
                >
                  {label}
                </button>
              ))}
            </div>

            {mode === 'day' ? (
              <div className="flex min-w-0 max-w-full flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1">
                <button
                  type="button"
                  className="shrink-0 rounded-lg bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white transition-colors hover:bg-blue-700 sm:px-3"
                  onClick={() => setSelectedDate(getPcBusinessDate())}
                >
                  今天
                </button>
                <button
                  type="button"
                  className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 transition-colors hover:border-blue-200 hover:text-blue-700 sm:px-2.5"
                  onClick={() => setSelectedDate(addPcDays(selectedDate, -1))}
                  aria-label="前一天"
                >
                  前
                </button>
                <button
                  type="button"
                  className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 transition-colors hover:border-blue-200 hover:text-blue-700 sm:px-2.5"
                  onClick={() => setSelectedDate(addPcDays(selectedDate, 1))}
                  aria-label="后一天"
                >
                  后
                </button>
                <span className="min-w-0 max-w-[11rem] truncate px-1 text-sm font-semibold text-slate-900 sm:max-w-[15rem] sm:px-2">
                  {formatPcDateCn(selectedDate)}
                </span>
                <input
                  type="date"
                  value={dateStr}
                  onChange={event => {
                    if (!event.target.value) return;
                    const [year, month, day] = event.target.value.split('-').map(Number);
                    setSelectedDate(new Date(Date.UTC(year, month - 1, day)));
                  }}
                  aria-label="选择日期"
                  className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 outline-none transition-colors focus:border-blue-400"
                />
              </div>
            ) : (
              <div className="flex min-w-0 max-w-full flex-wrap items-center gap-1 rounded-xl border border-slate-200 bg-slate-50 p-1">
                {RANGE_SHORTCUTS.map(shortcut => (
                  <button
                    key={shortcut.key}
                    type="button"
                    className={`shrink-0 rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors sm:px-3 ${
                      rangeShortcut === shortcut.key
                        ? 'bg-teal-600 text-white shadow-sm'
                        : 'text-slate-500 hover:bg-white hover:text-slate-800'
                    }`}
                    onClick={() => switchShortcut(shortcut.key)}
                  >
                    {shortcut.label}
                  </button>
                ))}
                <button
                  type="button"
                  className={`shrink-0 rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors ${
                    rangeShortcut === 'custom'
                      ? 'bg-teal-600 text-white shadow-sm'
                      : 'text-slate-500 hover:bg-white hover:text-slate-800'
                  }`}
                  onClick={() => switchShortcut('custom')}
                >
                  自定义
                </button>
                <span className="min-w-0 truncate px-1 text-sm font-semibold text-slate-900">
                  {rangeLabel}
                </span>
                <input
                  type="date"
                  value={rangeStart}
                  max={rangeEnd}
                  onChange={event => {
                    setRangeShortcut('custom');
                    if (event.target.value) setRangeStart(event.target.value);
                  }}
                  aria-label="开始日期"
                  className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 outline-none transition-colors focus:border-blue-400"
                />
                <input
                  type="date"
                  value={rangeEnd}
                  min={rangeStart}
                  onChange={event => {
                    setRangeShortcut('custom');
                    if (event.target.value) setRangeEnd(event.target.value);
                  }}
                  aria-label="结束日期"
                  className="shrink-0 rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-xs text-slate-600 outline-none transition-colors focus:border-blue-400"
                />
              </div>
            )}

            <button
              type="button"
              onClick={() => setImportOpen(true)}
              className="pim-button-primary h-8 shrink-0 px-3 text-xs font-medium"
            >
              导入历史数据
            </button>
          </div>
        }
      />

      {summaryError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
          加载浏览器使用数据失败：{summaryError}
        </div>
      )}

      <SummaryCards summary={summaryQuery.data} isLoading={summaryQuery.isLoading} />

      {mode === 'day' ? (
        <ChartCard
          title="时段分布"
          subtitle="按域名展示当日 0-24 时的浏览时段（仅统计最近约一年内的插件 tick 数据）"
          error={timelineQuery.error instanceof Error ? timelineQuery.error.message : null}
        >
          <DayTimeline items={timelineQuery.data ?? []} isLoading={timelineQuery.isLoading} />
        </ChartCard>
      ) : (
        <ChartCard
          title="每日趋势"
          subtitle="所选范围内每日浏览器专注时长"
          error={dailyQuery.error instanceof Error ? dailyQuery.error.message : null}
        >
          <TrendChart daily={dailyQuery.data ?? []} isLoading={dailyQuery.isLoading} />
        </ChartCard>
      )}

      <section className="pim-panel min-w-0 p-4">
        <div className="mb-4">
          <h2 className="text-sm font-semibold text-slate-950">站点排行</h2>
          <p className="mt-1 text-xs text-slate-500">
            {mode === 'day'
              ? `当日各域名专注时长排行（${dateStr}）`
              : `范围内各域名专注时长汇总排行（${rangeLabel}）`}
          </p>
        </div>
        <SiteList hosts={summaryQuery.data?.topHosts ?? []} isLoading={summaryQuery.isLoading} />
      </section>

      <ImportDialog
        open={importOpen}
        onClose={() => setImportOpen(false)}
        onSuccess={handleImportSuccess}
      />
    </div>
  );
}
