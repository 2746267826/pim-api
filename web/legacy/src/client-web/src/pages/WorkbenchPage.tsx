import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  calendarApiPaths,
  getCalendarLayers,
  getOutlookSettings,
  getOutlookSyncBatches,
  getTasksPaged,
  updateTask,
  getTaskBooks,
  taskToMutationData,
  getAiPlaceholders,
  generateAiPlan,
  confirmAiPlaceholder,
  dismissAiPlaceholder,
} from '../api/calendar';
import { getPcSummary } from '../api/pcTracker';
import { PC_ACTIVE_MINUTES_NOTE, formatActiveHours, sumHeatmapActiveMinutes } from '../components/charts/pcTodayOptions';
import { formatPcDate, getPcBusinessDate } from '../utils/pcBusinessDay';
import PcActiveMinutesNote from '../components/pc-tracker/PcActiveMinutesNote';
import { getPendingConfirmations, operationsApiPaths } from '../api/operations';
import TaskEditorDialog from '../dialogs/TaskEditorDialog';
import type { TaskMutationData } from '../api/calendar';
import type { TaskResponse, AiPlanPlaceholderViewDto } from '../types';
import { AlertCircle, RefreshCw, Monitor, Plus, CheckCircle2, Circle, Sparkles, Check, X, Clock, Loader2 } from 'lucide-react';
import { toast } from 'sonner';
import PageHeader from '../ui/PageHeader';
import SegmentedControl from '../ui/SegmentedControl';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

type DensityMode = 'standard' | 'dense' | 'focus';
type WorkbenchView = 'schedule' | 'execute' | 'feedback';

const densityOptions: Array<{ value: DensityMode; label: string }> = [
  { value: 'standard', label: '标准' },
  { value: 'dense', label: '紧凑' },
  { value: 'focus', label: '专注' },
];

const workbenchViewOptions: Array<{ value: WorkbenchView; label: string }> = [
  { value: 'schedule', label: '排程' },
  { value: 'execute', label: '执行' },
  { value: 'feedback', label: '反馈' },
];

const dashboardLayers = ['events', 'task-segments', 'habits', 'availability', 'ai-placeholders'];
const layerLabels: Record<string, string> = {
  events: '日程事件',
  'task-segments': '任务时间段',
  habits: '习惯',
  availability: '可用时间',
  'ai-placeholders': '智能占位',
};
const statusLabels: Record<string, string> = {
  Unknown: '未知',
  None: '无',
  Healthy: '正常',
  Warning: '警告',
  Failed: '失败',
  missing: '缺失',
  healthy: '正常',
  connected: '已连接',
  'not-connected': '未连接',
  pending: '等待中',
  completed: '已完成',
  failed: '失败',
};

function todayRange() {
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  const end = new Date(start);
  end.setDate(start.getDate() + 1);

  return {
    start: start.toISOString(),
    end: end.toISOString(),
  };
}

function formatDateTime(value?: string | null) {
  if (!value) return '不可用';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function formatStatus(value?: string | null) {
  if (!value) return '未知';
  return statusLabels[value] ?? value;
}

function formatProvider(value?: string | null) {
  if (!value) return '微软日历';
  return value.toLowerCase() === 'outlook' ? '微软日历' : value;
}

function formatTimeRange(startStr?: string | null, endStr?: string | null) {
  if (!startStr || !endStr) return '时间未定';
  const start = new Date(startStr);
  const end = new Date(endStr);
  if (Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) return `${startStr} ~ ${endStr}`;
  const startMonthDay = `${start.getMonth() + 1}月${start.getDate()}日`;
  const startTime = start.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });
  const endTime = end.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });
  const durationMinutes = Math.round((end.getTime() - start.getTime()) / 60000);
  const durationText = durationMinutes >= 60
    ? `${(durationMinutes / 60).toFixed(1)}小时`
    : `${durationMinutes}分钟`;
  return `${startMonthDay} ${startTime} - ${endTime} (${durationText})`;
}

function compactNumber(value: number | undefined) {
  return String(value ?? 0);
}

function DashboardMetric({ label, value, detail }: { label: string; value: string; detail: string }) {
  return (
    <section className="pim-card min-w-0 p-4">
      <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-400">{label}</p>
      <p className="mt-2 text-2xl font-semibold text-slate-950">{value}</p>
      <p className="mt-1 truncate text-xs text-slate-500">{detail}</p>
    </section>
  );
}

export default function WorkbenchPage() {
  const [densityMode, setDensityMode] = useState<DensityMode>('standard');
  const [aiHorizonDays, setAiHorizonDays] = useState<number>(7);
  const [workbenchView, setWorkbenchView] = useState<WorkbenchView>('schedule');
  const [taskEditorOpen, setTaskEditorOpen] = useState(false);
  const [selectedTask, setSelectedTask] = useState<TaskResponse | undefined>();
  const queryClient = useQueryClient();
  const range = useMemo(todayRange, []);

  const { data: tasksData, isLoading: tasksLoading } = useQuery({
    queryKey: ['workbench-tasks'],
    queryFn: () => getTasksPaged({ pageSize: 50 }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: taskBooks = [] } = useQuery({
    queryKey: ['workbench-task-books'],
    queryFn: () => getTaskBooks(),
  });

  const taskBookMap = useMemo(() => {
    return new Map(taskBooks.map(b => [b.id, b.name]));
  }, [taskBooks]);

  // REQ-8：PC 汇总必须按**业务日**取数（Asia/Shanghai 04:00 起算）。
  // 原先用 `range.start.split('T')[0]`（本地午夜的 UTC 日期），在 UTC+8 下会整体差一天，
  // 于是「PC 活跃」显示的是前一天的汇总，与 summary.heatmap 对不上。
  const pcBusinessDateStr = useMemo(() => formatPcDate(getPcBusinessDate()), []);

  const { data: pcSummary } = useQuery({
    queryKey: ['workbench-pc-summary', pcBusinessDateStr],
    queryFn: () => getPcSummary(pcBusinessDateStr),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  // REQ-8：「PC 活跃」= summary.heatmap[].activeMinutes 求和（口径含只有键鼠输入、无窗口记录的分钟），
  // 数值变大是上游有意统一口径的结果，这里只做说明与换算，不夹回时间线覆盖时长。
  const pcActiveHours = pcSummary
    ? formatActiveHours(sumHeatmapActiveMinutes(pcSummary.heatmap))
    : '—';

  const toggleTaskMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: TaskMutationData }) => updateTask(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workbench-tasks'] });
      queryClient.invalidateQueries({ queryKey: ['tasks'] });
      queryClient.invalidateQueries({ queryKey: ['tasks-paged'] });
    },
  });

  const tasksList = tasksData?.items ?? [];

  const { data: layerData, isLoading: layersLoading } = useQuery({
    queryKey: ['workbench-calendar-layers', range.start, range.end],
    queryFn: () => getCalendarLayers({ start: range.start, end: range.end, layers: dashboardLayers }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: confirmations = [], isLoading: confirmationsLoading } = useQuery({
    queryKey: ['workbench-pending-confirmations'],
    queryFn: getPendingConfirmations,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: settings } = useQuery({
    queryKey: ['workbench-outlook-settings'],
    queryFn: getOutlookSettings,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: syncBatches = [] } = useQuery({
    queryKey: ['workbench-outlook-sync-batches'],
    queryFn: getOutlookSyncBatches,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: placeholders = [], isLoading: placeholdersLoading } = useQuery<AiPlanPlaceholderViewDto[]>({
    queryKey: ['workbench-ai-placeholders'],
    queryFn: () => getAiPlaceholders('Suggested'),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const generatePlanMutation = useMutation({
    mutationFn: (days: number) => generateAiPlan({ horizonDays: days }),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['workbench-ai-placeholders'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-calendar-layers'] });
      if (res.placeholders?.length > 0) {
        toast.success(`已由${res.source === 'ai' ? ' AI 模型' : '排程规则引擎'}生成 ${res.placeholders.length} 条排程建议`);
      } else {
        toast.info('未发现可排程的待办任务或可用空闲时段');
      }
    },
    onError: (err: any) => {
      toast.error(`生成建议失败：${err?.message || '未知错误'}`);
    },
  });

  const confirmPlaceholderMutation = useMutation({
    mutationFn: (id: string) => confirmAiPlaceholder(id),
    onSuccess: () => {
      toast.success('排程建议已采纳，已进入待确认流程并同步至日程');
      queryClient.invalidateQueries({ queryKey: ['workbench-ai-placeholders'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-pending-confirmations'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-calendar-layers'] });
    },
    onError: (err: any) => {
      toast.error(`采纳失败：${err?.message || '未知错误'}`);
    },
  });

  const dismissPlaceholderMutation = useMutation({
    mutationFn: (id: string) => dismissAiPlaceholder(id),
    onSuccess: () => {
      toast.info('已忽略该排程建议');
      queryClient.invalidateQueries({ queryKey: ['workbench-ai-placeholders'] });
    },
    onError: (err: any) => {
      toast.error(`忽略失败：${err?.message || '未知错误'}`);
    },
  });

  const layerCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const item of layerData?.items ?? []) {
      counts.set(item.layer, (counts.get(item.layer) ?? 0) + 1);
    }
    return counts;
  }, [layerData?.items]);

  const latestBatch = syncBatches[0];
  const compact = densityMode === 'dense';
  const focus = densityMode === 'focus';
  const pageSpacingClassName = compact ? 'space-y-3' : 'space-y-4';

  return (
    <div className={`mx-auto w-full max-w-[1500px] ${pageSpacingClassName} pb-20`}>
      <PageHeader
        title="日程工作台"
        subtitle="集中查看日程图层、确认队列、微软日历同步、提醒和报告运行状态。"
        beforeActions={
          <SegmentedControl
            value={densityMode}
            options={densityOptions}
            onChange={setDensityMode}
            ariaLabel="工作台密度"
          />
        }
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <Link to="/status" className="pim-button-secondary inline-flex min-h-[44px] items-center px-3 py-2 text-sm">
              状态
            </Link>
            <Link to="/data-center" className="pim-button-primary inline-flex min-h-[44px] items-center px-3 py-2 text-sm">
              数据中心
            </Link>
          </div>
        }
      />

      <div className="flex flex-wrap items-center gap-2">
        <SegmentedControl
          value={workbenchView}
          options={workbenchViewOptions}
          onChange={setWorkbenchView}
          ariaLabel="工作台视图"
        />
      </div>

      <section className={`grid grid-cols-1 gap-3 items-start ${focus ? 'lg:grid-cols-3' : 'md:grid-cols-2 xl:grid-cols-4'}`}>
        <DashboardMetric
          label="日程图层"
          value={compactNumber(layerData?.items.length)}
          detail={layersLoading ? '正在加载图层索引' : `${dashboardLayers.length} 个图层已配置`}
        />
        <DashboardMetric
          label="待确认操作"
          value={compactNumber(confirmations.length)}
          detail={confirmationsLoading ? '正在加载确认队列' : '等待复核的操作'}
        />
        <DashboardMetric
          label="智能排程建议"
          value={compactNumber(placeholders.length)}
          detail={placeholdersLoading ? '正在加载建议' : `${placeholders.length} 条待采纳建议`}
        />
        <DashboardMetric
          label="微软日历同步"
          value={formatStatus(settings?.status)}
          detail={`令牌：${formatStatus(settings?.tokenHealth)}`}
        />
        {!focus && (
          <DashboardMetric
            label="最近同步批次"
            value={formatStatus(latestBatch?.status)}
            detail={latestBatch ? formatDateTime(latestBatch.startedAt) : '暂无同步批次'}
          />
        )}
      </section>

      {/* 日程图层概览 */}
      <section className="pim-panel min-w-0 p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <h2 className="text-sm font-semibold text-slate-950">日程图层</h2>
            <p className="mt-1 text-xs text-slate-500">今日范围：{formatDateTime(range.start)} 至 {formatDateTime(range.end)}</p>
          </div>
          <Link to="/calendar" className="pim-button-secondary inline-flex min-h-[38px] items-center px-3 py-1.5 text-sm">
            打开日历
          </Link>
        </div>
        <div className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-5">
          {dashboardLayers.map(layer => (
            <div key={layer} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
              <p className="truncate text-xs font-semibold text-slate-700">{layerLabels[layer]}</p>
              <p className="mt-1 text-lg font-semibold text-slate-950">{layerCounts.get(layer) ?? 0}</p>
            </div>
          ))}
        </div>
      </section>

      {/* AI 智能排程规划面板 */}
      <section className="rounded-xl border border-zinc-200 bg-white p-4 shadow-xs">
        <div className="flex flex-wrap items-center justify-between gap-3 pb-3 border-b border-zinc-100">
          <div className="flex items-center gap-2">
            <div className="p-1.5 bg-blue-50 rounded-lg text-blue-600">
              <Sparkles className="w-5 h-5" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <h2 className="text-sm font-semibold text-zinc-900">AI 智能排程建议</h2>
                <span className="text-[10px] bg-blue-50 text-blue-700 border border-blue-200 px-2 py-0.5 rounded-full font-mono">
                  {placeholders.length} 条候选
                </span>
              </div>
              <p className="mt-0.5 text-xs text-zinc-500">
                基于待办任务优先级与预估耗时，自动避让已有日程与忙碌时段，由规划引擎协同推荐时间槽。
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2">
            <div className="flex items-center rounded-lg border border-zinc-200 bg-zinc-50 p-0.5 text-xs font-medium">
              {[
                { label: '3天', days: 3 },
                { label: '7天', days: 7 },
                { label: '14天', days: 14 },
              ].map(opt => (
                <button
                  key={opt.days}
                  type="button"
                  onClick={() => setAiHorizonDays(opt.days)}
                  className={`px-2.5 py-1 rounded-md transition-colors ${
                    aiHorizonDays === opt.days
                      ? 'bg-white text-zinc-900 shadow-xs font-semibold'
                      : 'text-zinc-600 hover:text-zinc-900'
                  }`}
                >
                  {opt.label}
                </button>
              ))}
            </div>

            <button
              type="button"
              disabled={generatePlanMutation.isPending}
              onClick={() => generatePlanMutation.mutate(aiHorizonDays)}
              className="pim-button-primary inline-flex min-h-[36px] items-center gap-1.5 px-3 py-1.5 text-xs"
            >
              {generatePlanMutation.isPending ? (
                <>
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  <span>正在规划...</span>
                </>
              ) : (
                <>
                  <Sparkles className="w-3.5 h-3.5" />
                  <span>一键生成排程建议</span>
                </>
              )}
            </button>
          </div>
        </div>

        {placeholdersLoading && (
          <div className="py-8 text-center text-xs text-zinc-400">
            正在加载智能排程建议...
          </div>
        )}

        {!placeholdersLoading && placeholders.length === 0 && (
          <div className="py-8 text-center text-xs text-zinc-500">
            <p>暂无待处理的排程建议。</p>
            <p className="mt-1 text-zinc-400">
              点击上方「一键生成排程建议」，系统将自动排布待办任务并提供最佳时间分配方案。
            </p>
          </div>
        )}

        {!placeholdersLoading && placeholders.length > 0 && (
          <div className="mt-3 grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
            {placeholders.map(item => (
              <div
                key={item.id}
                className="flex flex-col justify-between rounded-lg border border-zinc-200 bg-zinc-50/60 p-3 hover:border-blue-200 hover:bg-blue-50/20 transition-all"
              >
                <div>
                  <div className="flex items-start justify-between gap-2">
                    <h3 className="font-semibold text-xs text-zinc-900 line-clamp-1" title={item.title}>
                      {item.title}
                    </h3>
                    <span
                      className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-mono font-medium ${
                        item.source === 'ai'
                          ? 'bg-purple-100 text-purple-700 border border-purple-200'
                          : 'bg-emerald-100 text-emerald-700 border border-emerald-200'
                      }`}
                    >
                      {item.source === 'ai' ? 'AI 规划' : '规则引擎'}
                    </span>
                  </div>

                  <div className="mt-2 flex items-center gap-1 text-[11px] text-zinc-600">
                    <Clock className="w-3.5 h-3.5 shrink-0 text-zinc-400" />
                    <span className="font-mono">{formatTimeRange(item.startsAt, item.endsAt)}</span>
                  </div>

                  {item.reason && (
                    <p className="mt-1.5 text-[11px] text-zinc-500 line-clamp-2" title={item.reason}>
                      {item.reason}
                    </p>
                  )}
                </div>

                <div className="mt-3 pt-2 border-t border-zinc-200/60 flex items-center justify-end gap-2">
                  <button
                    type="button"
                    disabled={dismissPlaceholderMutation.isPending}
                    onClick={() => dismissPlaceholderMutation.mutate(item.id)}
                    className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs text-zinc-600 hover:bg-zinc-200/60 hover:text-zinc-900 transition-colors"
                  >
                    <X className="w-3 h-3" />
                    <span>忽略</span>
                  </button>
                  <button
                    type="button"
                    disabled={confirmPlaceholderMutation.isPending}
                    onClick={() => confirmPlaceholderMutation.mutate(item.id)}
                    className="inline-flex items-center gap-1 rounded bg-blue-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-blue-700 transition-colors shadow-2xs"
                  >
                    <Check className="w-3 h-3" />
                    <span>采纳排程</span>
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {/* 核心工作台两列独立布局 (针对 #192 卡片强制拉伸超长) */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        {/* 左侧列：待确认、微软同步、PC概览 (紧凑自适应堆叠，绝不被右侧拉伸留白) */}
        <div className="lg:col-span-5 space-y-4">
          {/* 卡片 A：待确认操作 */}
          <div className="p-4 rounded-xl border border-zinc-200 bg-white shadow-xs">
            <div className="flex items-center justify-between mb-2">
              <span className="font-semibold text-xs text-zinc-800 flex items-center gap-1.5">
                <AlertCircle className="w-4 h-4 text-amber-500" />
                <span>待确认操作</span>
              </span>
              <span className="text-[10px] bg-amber-50 text-amber-700 border border-amber-200 px-1.5 py-0.5 rounded-full font-mono">
                {confirmations.length} 条待处理
              </span>
            </div>
            {confirmations.length > 0 ? (
              <div className="space-y-2 mt-2">
                {confirmations.slice(0, compact ? 2 : 3).map(item => (
                  <Link
                    key={item.id}
                    to="/confirmations"
                    className="block rounded-lg border border-zinc-100 bg-zinc-50/50 p-2.5 transition-colors hover:border-blue-200 hover:bg-blue-50/40"
                  >
                    <div className="flex items-start justify-between gap-2">
                      <p className="min-w-0 truncate text-xs font-medium text-zinc-800">{item.summary}</p>
                      <span className="shrink-0 rounded bg-amber-100/70 px-1.5 py-0.5 text-[10px] font-semibold text-amber-800 font-mono">
                        {item.riskLevel}
                      </span>
                    </div>
                    <p className="mt-1 truncate text-[11px] text-zinc-500">{item.source} · {item.operationType}</p>
                  </Link>
                ))}
                <div className="pt-1">
                  <Link to="/confirmations" className="text-xs text-blue-600 hover:underline font-medium">
                    前往核验队列 ({confirmations.length}) →
                  </Link>
                </div>
              </div>
            ) : (
              <p className="text-xs text-zinc-500 mt-2 py-2">暂无待核验的外部操作或异常变更。</p>
            )}
          </div>

          {/* 卡片 B：微软日历同步状态 */}
          <div className="p-4 rounded-xl border border-zinc-200 bg-white shadow-xs">
            <div className="flex items-center justify-between mb-2">
              <span className="font-semibold text-xs text-zinc-800 flex items-center gap-1.5">
                <RefreshCw className="w-4 h-4 text-blue-500" />
                <span>Microsoft Outlook 同步</span>
              </span>
              <span className="text-[10px] bg-emerald-50 text-emerald-700 border border-emerald-200 px-1.5 py-0.5 rounded-full font-mono">
                {formatStatus(settings?.status)}
              </span>
            </div>
            <div className="text-xs text-zinc-600 space-y-1.5 font-mono pt-1">
              <div className="flex justify-between">
                <span className="text-zinc-400 font-sans">上次同步:</span>
                <span className="text-zinc-700">{formatDateTime(settings?.lastSyncedAt)}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-zinc-400 font-sans">服务状态:</span>
                <span className="text-zinc-700">{formatProvider(settings?.provider)} ({formatStatus(settings?.tokenHealth)})</span>
              </div>
              {settings?.lastError && (
                <div className="mt-1 text-[11px] text-red-600 font-sans bg-red-50 p-1.5 rounded border border-red-100">
                  {settings.lastError}
                </div>
              )}
            </div>
            <div className="mt-3 pt-2 border-t border-zinc-100 flex justify-end">
              <Link to="/settings/sync" className="text-xs text-blue-600 hover:underline font-medium font-sans">
                配置同步设置 →
              </Link>
            </div>
          </div>

          {/* 卡片 C：PC 活跃状态概览 */}
          <div className="p-4 rounded-xl border border-zinc-200 bg-white shadow-xs">
            <div className="flex items-center justify-between mb-2">
              <span className="font-semibold text-xs text-zinc-800 flex items-center gap-1.5">
                <Monitor className="w-4 h-4 text-indigo-500" />
                <span>PC 记录概览</span>
              </span>
              <Link to="/pc-records" className="text-xs text-blue-600 hover:underline cursor-pointer">
                查看详情 →
              </Link>
            </div>
            <div className="grid grid-cols-3 gap-2 text-center pt-1 font-mono text-xs">
              <div className="p-2 bg-zinc-50 rounded-lg border border-zinc-100">
                <div className="text-zinc-400 text-[10px] font-sans">今日键盘输入</div>
                <div className="font-bold text-zinc-900 text-sm mt-0.5">
                  {pcSummary?.keystats?.keyPresses ? pcSummary.keystats.keyPresses.toLocaleString() : '—'}
                </div>
              </div>
              <div className="p-2 bg-zinc-50 rounded-lg border border-zinc-100">
                <div className="text-zinc-400 text-[10px] font-sans">专注应用</div>
                <div className="font-bold text-emerald-600 text-sm mt-0.5 truncate" title={pcSummary?.appRanking?.[0]?.displayName || pcSummary?.appRanking?.[0]?.appName || '—'}>
                  {pcSummary?.appRanking?.[0]?.displayName || pcSummary?.appRanking?.[0]?.appName || '—'}
                </div>
              </div>
              <div className="p-2 bg-zinc-50 rounded-lg border border-zinc-100">
                <div className="text-zinc-400 text-[10px] font-sans" title={PC_ACTIVE_MINUTES_NOTE}>PC 活跃</div>
                <div className="font-bold text-zinc-900 text-sm mt-0.5 truncate" data-pc-active-hours={pcActiveHours}>
                  {pcActiveHours}
                </div>
              </div>
            </div>
            <PcActiveMinutesNote className="mt-2" />
          </div>
        </div>

        {/* 右侧列：待办任务列表 (设置 max-h-[520px] 内部独立滚动，再多任务也不会把整页撑炸) */}
        <div className="lg:col-span-7">
          <div className="rounded-xl border border-zinc-200 bg-white shadow-xs overflow-hidden">
            <div className="px-4 py-3 border-b border-zinc-100 flex items-center justify-between bg-zinc-50/70">
              <div className="flex items-center gap-2">
                <span className="font-semibold text-xs text-zinc-800">待办任务 (Tasks · 独立滚动容器)</span>
                <span className="text-[10px] bg-zinc-200/80 text-zinc-700 px-1.5 py-0.5 rounded-full font-mono font-semibold">
                  {tasksList.length} 项
                </span>
              </div>
              <button
                type="button"
                onClick={() => { setSelectedTask(undefined); setTaskEditorOpen(true); }}
                className="text-xs text-blue-600 hover:text-blue-700 font-medium flex items-center gap-1 hover:underline"
              >
                <Plus className="w-3.5 h-3.5" />
                <span>添加任务</span>
              </button>
            </div>

            {/* 限制最大高度并内置独立滚动条 */}
            <div className="max-h-[520px] overflow-y-auto divide-y divide-zinc-100">
              {tasksLoading && (
                <div className="p-6 text-center text-xs text-zinc-400">正在加载待办任务...</div>
              )}
              {!tasksLoading && tasksList.length === 0 && (
                <div className="p-8 text-center text-xs text-zinc-400">
                  暂无待办任务。点击右上角「添加任务」创建。
                </div>
              )}
              {tasksList.map(task => {
                const isCompleted = task.status === 'COMPLETED';
                const bookName = task.calendarId ? taskBookMap.get(task.calendarId) : undefined;
                return (
                  <div
                    key={task.id}
                    onClick={() => { setSelectedTask(task); setTaskEditorOpen(true); }}
                    className="flex items-center gap-3 px-4 py-3 hover:bg-zinc-50/80 transition-colors cursor-pointer group"
                  >
                    <button
                      type="button"
                      onClick={e => {
                        e.stopPropagation();
                        toggleTaskMutation.mutate({
                          id: task.id,
                          data: taskToMutationData(task, {
                            status: isCompleted ? 'NEEDS-ACTION' : 'COMPLETED',
                          }),
                        });
                      }}
                      className="shrink-0 text-zinc-400 hover:text-blue-600 transition-colors"
                      title={isCompleted ? '标为未完成' : '标为完成'}
                    >
                      {isCompleted ? (
                        <CheckCircle2 className="w-4 h-4 text-emerald-600" />
                      ) : (
                        <Circle className="w-4 h-4" />
                      )}
                    </button>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className={`text-sm font-medium truncate group-hover:text-blue-600 ${isCompleted ? 'line-through text-zinc-400' : 'text-zinc-900'}`}>
                          {task.title}
                        </span>
                        {task.priority !== undefined && task.priority !== null && task.priority > 0 && (
                          <span className="text-[10px] px-1.5 py-0.5 rounded bg-amber-50 text-amber-700 border border-amber-200 font-mono">
                            P{task.priority}
                          </span>
                        )}
                        {bookName && (
                          <span className="text-[10px] px-1.5 py-0.5 rounded bg-zinc-100 text-zinc-600 font-mono truncate max-w-[100px]">
                            {bookName}
                          </span>
                        )}
                      </div>
                      {task.description && (
                        <p className="text-xs text-zinc-400 truncate mt-0.5">{task.description}</p>
                      )}
                    </div>
                    {task.due && (
                      <div className="text-xs font-mono text-zinc-400 shrink-0">
                        {task.due.split('T')[0]}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>

            <div className="px-4 py-2 border-t border-zinc-100 bg-zinc-50/50 text-[11px] text-zinc-400 flex justify-between items-center">
              <span>💡 容器内部独立滚动，右侧再长也不影响左侧卡片紧凑排布</span>
              <span className="font-mono">共 {tasksList.length} 项</span>
            </div>
          </div>
        </div>
      </div>

      <TaskEditorDialog
        open={taskEditorOpen}
        onClose={() => { setTaskEditorOpen(false); setSelectedTask(undefined); }}
        task={selectedTask}
      />

      <section className="pim-panel p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold text-slate-950">端点与状态链接</h2>
            <p className="mt-1 text-xs text-slate-500">当前界面使用的接口契约与状态入口。</p>
          </div>
          <Link to="/status" className="pim-button-secondary inline-flex min-h-[44px] items-center px-3 py-1.5 text-sm">
            系统状态
          </Link>
        </div>
        <div className="mt-3 overflow-x-auto">
          <div className="grid min-w-[640px] grid-cols-1 gap-2 md:grid-cols-2 xl:grid-cols-4">
          {[
            ['日程图层', calendarApiPaths.calendarLayers({ start: range.start, end: range.end, layers: dashboardLayers })],
            ['微软日历设置', calendarApiPaths.outlookSettings()],
            ['同步批次', calendarApiPaths.outlookSyncBatches()],
            ['待确认操作', operationsApiPaths.pendingConfirmations()],
          ].map(([label, endpoint]) => (
            <div key={label} className="min-w-0 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
              <p className="text-xs font-semibold text-slate-600">{label}</p>
              <code className="mt-1 block truncate text-[11px] text-slate-500">{endpoint}</code>
            </div>
          ))}
          </div>
        </div>
      </section>
    </div>
  );
}
