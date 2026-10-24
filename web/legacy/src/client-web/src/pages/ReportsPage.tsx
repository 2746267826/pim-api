import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  generateReport,
  getReports,
  requestReportSuggestionAction,
} from '../api/calendar';
import type { GenerateReportRequest, ReportArtifact, ReportSuggestion } from '../types';
import PageHeader from '../ui/PageHeader';
import SegmentedControl from '../ui/SegmentedControl';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';
import TaskFunnel from '../components/charts/TaskFunnel';
import DeviceHealthGauge from '../components/charts/DeviceHealthGauge';
import { useExhibitionData } from '../components/charts/hooks/useExhibitionData';

type ReportKind = 'Daily' | 'Weekly' | 'Monthly' | 'Project';

const reportTabs: Array<{ value: ReportKind; label: string }> = [
  { value: 'Daily', label: '日报' },
  { value: 'Weekly', label: '周报' },
  { value: 'Monthly', label: '月报' },
  { value: 'Project', label: '项目报告' },
];

function todayDate() {
  return new Date().toISOString().slice(0, 10);
}

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function reportTitle(report: ReportArtifact) {
  return report.title || `${report.kind} 报告`;
}

function parseMetrics(report: ReportArtifact) {
  if (!report.metricsJson) return {};

  try {
    const parsed = JSON.parse(report.metricsJson) as Record<string, unknown>;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function suggestionStatus(suggestion: ReportSuggestion) {
  if (suggestion.confirmationId) return `后续确认：${suggestion.confirmationId}`;
  return `后续确认：${suggestion.status}`;
}

export default function ReportsPage() {
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<ReportKind>('Daily');
  const [date, setDate] = useState(todayDate);
  const [status, setStatus] = useState('all');

  const { data: reports = [], isLoading } = useQuery({
    queryKey: ['reports'],
    queryFn: getReports,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const generateMutation = useMutation({
    mutationFn: (request: GenerateReportRequest) => generateReport(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['reports'] }),
  });

  const suggestionMutation = useMutation({
    mutationFn: requestReportSuggestionAction,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['reports'] });
      queryClient.invalidateQueries({ queryKey: ['pending-confirmations'] });
    },
  });

  const visibleReports = useMemo(() => reports.filter(report => {
    const kindMatches = report.kind.toLowerCase() === tab.toLowerCase();
    const statusMatches = status === 'all' || (report.status ?? '').toLowerCase() === status;
    return kindMatches && statusMatches;
  }), [reports, status, tab]);

  const latestReport = visibleReports[0];
  const metrics = latestReport ? parseMetrics(latestReport) : {};
  const suggestions = latestReport?.suggestions ?? [];

  function submitGenerate() {
    generateMutation.mutate({
      kind: tab,
      date,
      projectId: null,
    });
  }

  return (
    <div className="mx-auto w-full max-w-[1300px] space-y-4 pb-20 md:pb-4">
      <PageHeader
        title="报告中心"
        subtitle="生成日报、周报、月报和项目报告，查看指标、正文、建议与后续确认结果。"
        beforeActions={<SegmentedControl value={tab} options={reportTabs} onChange={setTab} ariaLabel="报告类型" />}
        actions={
          <button
            type="button"
            onClick={submitGenerate}
            disabled={generateMutation.isPending}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            {generateMutation.isPending ? '生成中' : `生成${reportTabs.find(item => item.value === tab)?.label ?? '报告'}`}
          </button>
        }
      />

      <section className="pim-panel p-4">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          <label>
            <span className="text-xs font-semibold text-slate-500">报告日期</span>
            <input
              type="date"
              value={date}
              onChange={event => setDate(event.target.value)}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
            />
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">状态</span>
            <select value={status} onChange={event => setStatus(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="all">全部状态</option>
              <option value="draft">草稿</option>
              <option value="published">已发布</option>
              <option value="archived">已归档</option>
            </select>
          </label>
        </div>
      </section>

      <div className="grid grid-cols-1 gap-4 overflow-auto lg:grid-cols-3">
        {[
          ['报告数量', visibleReports.length],
          ['后续建议', suggestions.length],
          ['待后续确认', suggestions.filter(item => item.status.toLowerCase() !== 'done').length],
        ].map(([label, value]) => (
          <section key={label} className="pim-card p-4">
            <p className="text-[11px] font-semibold text-slate-400">{label}</p>
            <p className="mt-2 text-2xl font-semibold text-slate-950">{String(value)}</p>
            <p className="mt-1 text-xs text-slate-500">{tab} / {status}</p>
          </section>
        ))}
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.4fr)_minmax(320px,0.8fr)]">
        <section className="pim-panel p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-slate-950">
              {reportTabs.find(item => item.value === tab)?.label}内容
            </h2>
            {latestReport && (
              <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
                {latestReport.status ?? '未标记'} · {formatDateTime(latestReport.generatedAt)}
              </span>
            )}
          </div>

          {isLoading ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              正在加载报告。
            </p>
          ) : !latestReport ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              当前没有{reportTabs.find(item => item.value === tab)?.label}。
            </p>
          ) : (
            <article className="mt-4 space-y-4">
              <div className="rounded-lg border border-slate-200 bg-white p-3">
                <h3 className="text-sm font-semibold text-slate-950">{reportTitle(latestReport)}</h3>
                <p className="mt-1 text-xs text-slate-500">风险：{latestReport.riskLevel}</p>
              </div>
              <div className="rounded-lg bg-slate-50 p-3">
                <p className="whitespace-pre-wrap text-sm leading-6 text-slate-700">
                  {latestReport.contentMarkdown || '报告正文尚未生成。'}
                </p>
              </div>
            </article>
          )}
        </section>

        <div className="space-y-4">
          <section className="pim-panel p-4">
            <h2 className="text-sm font-semibold text-slate-950">指标</h2>
            <div className="mt-3 grid gap-2">
              {Object.entries(metrics).slice(0, 8).map(([key, value]) => (
                <div key={key} className="rounded-lg bg-slate-50 px-3 py-2">
                  <p className="text-xs font-semibold text-slate-400">{key}</p>
                  <p className="mt-1 break-words text-sm text-slate-700">{String(value)}</p>
                </div>
              ))}
              {Object.keys(metrics).length === 0 && (
                <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
                  暂无指标。
                </p>
              )}
            </div>
          </section>

          <section className="pim-panel p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-sm font-semibold text-slate-950">后续确认</h2>
              <span className="rounded-full bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-700">
                {suggestions.length} 条建议
              </span>
            </div>
            <div className="mt-3 grid gap-2">
              {suggestions.map(suggestion => (
                <article key={suggestion.id} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <h3 className="text-sm font-semibold text-slate-900">{suggestion.action}</h3>
                    <span className="text-xs text-slate-500">{suggestion.status}</span>
                  </div>
                  <p className="mt-2 text-xs leading-5 text-slate-500">{suggestion.summary}</p>
                  <div className="mt-3 flex flex-wrap items-center gap-2">
                    <span className="text-xs text-slate-500">{suggestionStatus(suggestion)}</span>
                    <button
                      type="button"
                      onClick={() => suggestionMutation.mutate(suggestion.id)}
                      disabled={suggestionMutation.isPending}
                      className="pim-button-secondary px-3 py-1.5 text-xs disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      请求确认
                    </button>
                  </div>
                </article>
              ))}
              {suggestions.length === 0 && (
                <p className="rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
                  暂无需要后续确认的建议。
                </p>
              )}
            </div>
          </section>
          </div>
        </div>
        {/* 展览馆嵌入：任务完成率漏斗 + 设备健康仪表盘（真实） */}
        <ReportsExhibitionEmbed />
      </div>
    );
  }

  function ReportsExhibitionEmbed() {
    const q10 = useExhibitionData(10, { real: true });
    const q12 = useExhibitionData(12, { real: true });
    return (
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section className="pim-card p-4">
          <h3 className="text-sm font-semibold text-slate-900">任务完成率 · 漏斗图（真实）</h3>
          <p className="mt-1 text-xs text-slate-500">创建→进行→完成 · {q10.isReal ? '🔗真实' : '🔮模拟'} {q10.isEmpty ? '· 暂无数据' : ''}</p>
          <div className="mt-3">{q10.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q10.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <TaskFunnel data={[]} />}</div>
        </section>
        <section className="pim-card p-4">
          <h3 className="text-sm font-semibold text-slate-900">设备健康 · 仪表盘（真实）</h3>
          <p className="mt-1 text-xs text-slate-500">2在线1离线1告警 · {q12.isReal ? '🔗真实' : '🔮模拟'}</p>
          <div className="mt-3">{q12.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q12.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <DeviceHealthGauge />}</div>
        </section>
      </div>
    );
  }
