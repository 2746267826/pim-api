import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { addPcDays, addPcMonths, formatPcDate, getPcBusinessDate } from '../utils/pcBusinessDay';
import {
  getActivityClassificationSuggestions,
  getCategoryTree,
  getPcActivityAnalysis,
  getPcAppUsage,
  getPcCategoryDistribution,
  getPcFocusBlocks,
  getPcHeatmapGrid,
  getPcKeystatsRange,
  getPcLateNight,
  getPcSummary,
  pcKeystatsScope,
  rejectActivityClassificationSuggestion,
} from '../api/pcTracker';
import { applyAppKnowledgeSuggestion, previewAppKnowledgeSuggestion } from '../api/appKnowledge';
import DateDimensionBar from '../components/pc-tracker/DateDimensionBar';
import ActivityHeatmap from '../components/pc-tracker/ActivityHeatmap';
import ActivityAnalysisHeatmap from '../components/pc-tracker/ActivityAnalysisHeatmap';
import CategoryTimeline from '../components/pc-tracker/CategoryTimeline';
import DailyActivityPanel from '../components/pc-tracker/DailyActivityPanel';
import KeyboardHeatmap from '../components/pc-tracker/KeyboardHeatmap';
import LabelingQueue from '../components/labeling/LabelingQueue';
import PcAppDonut from '../components/charts/PcAppDonut';
import KeyboardHeatmapChart from '../components/charts/KeyboardHeatmap';
import { useExhibitionData } from '../components/charts/hooks/useExhibitionData';
import PcReviewSummary from '../components/pc-tracker/PcReviewSummary';
import ContextConfirmationPanel from '../components/pc-tracker/ContextConfirmationPanel';
import ProductivityDashboardPanel from '../components/pc-tracker/ProductivityDashboard';
import ClassificationPreviewDialog, { type PreviewLike } from '../components/pc-tracker/ClassificationPreviewDialog';
import EventTimelineDialog from '../components/pc-tracker/EventTimelineDialog';
import PageHeader from '../ui/PageHeader';
import type {
  ActivityClassificationSuggestion,
  SuggestionClassificationApplyRequest,
  SuggestionClassificationPreviewRequest,
} from '../types';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

export function nextPcRoute3RequestId(current: number) {
  return current + 1;
}

export function isCurrentPcRoute3Request(requestId: number, current: number) {
  return requestId === current;
}

function AnalysisCard({
  title,
  subtitle,
  actions,
  children,
}: {
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section className="pim-panel min-w-0 overflow-visible p-4">
      <div className="mb-4 flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">{title}</h2>
          {subtitle && <p className="mt-1 text-xs text-slate-500">{subtitle}</p>}
        </div>
        {actions && <div className="shrink-0">{actions}</div>}
      </div>
      {children}
    </section>
  );
}

export default function PcTrackerPage() {
  const queryClient = useQueryClient();
  const [selectedDate, setSelectedDate] = useState(() => getPcBusinessDate());
  const [dimension, setDimension] = useState<'hour' | 'day' | 'month' | 'year'>('day');
  const [selectedCategory, setSelectedCategory] = useState<string | null>(null);
  const [selectedApp, setSelectedApp] = useState<string | null>(null);
  const [timelineDialogOpen, setTimelineDialogOpen] = useState(false);
  const [activeSuggestion, setActiveSuggestion] = useState<ActivityClassificationSuggestion | null>(null);
  const [preview, setPreview] = useState<PreviewLike | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [selectedAnalysisBlockStart, setSelectedAnalysisBlockStart] = useState<string | null>(null);
  const previewRequestIdRef = useRef(0);
  const applyRequestIdRef = useRef(0);

  const dateStr = formatPcDate(selectedDate);

  const { data } = useQuery({
    queryKey: ['pc-summary', dateStr],
    queryFn: () => getPcSummary(dateStr),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: suggestions = [], isLoading: suggestionsLoading } = useQuery({
    queryKey: ['pc-classification-suggestions', dateStr],
    queryFn: () => getActivityClassificationSuggestions(dateStr),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: activityAnalysis } = useQuery({
    queryKey: ['pc-activity-analysis', dateStr, 60],
    queryFn: () => getPcActivityAnalysis(dateStr, 60),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: focusBlocks } = useQuery({
    queryKey: ['pc-aggregation-focus-blocks', dateStr],
    queryFn: () => getPcFocusBlocks({ date: dateStr }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: appUsage } = useQuery({
    queryKey: ['pc-aggregation-app-usage', dateStr],
    queryFn: () => getPcAppUsage({ date: dateStr, limit: 8 }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: lateNight } = useQuery({
    queryKey: ['pc-aggregation-late-night', dateStr],
    queryFn: () => getPcLateNight({ date: dateStr }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: categoryDistribution } = useQuery({
    queryKey: ['pc-aggregation-category-distribution', dateStr],
    queryFn: () => getPcCategoryDistribution({ date: dateStr }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: categoryTree = [] } = useQuery({
    queryKey: ['pc-category-tree'],
    queryFn: getCategoryTree,
    staleTime: 60000,
  });

  const heatmapRange = dimension === 'hour'
    ? { start: dateStr, end: dateStr }
    : dimension === 'day'
      ? { start: formatPcDate(addPcDays(selectedDate, -30)), end: dateStr }
      : dimension === 'month'
        ? { start: formatPcDate(addPcMonths(selectedDate, -12)), end: dateStr }
        : { start: formatPcDate(addPcMonths(selectedDate, -60)), end: dateStr };

  const { data: heatmapData, isLoading: heatmapLoading, error: heatmapError } = useQuery({
    queryKey: ['pc-heatmap-grid', heatmapRange.start, heatmapRange.end, dimension],
    queryFn: () => getPcHeatmapGrid(heatmapRange.start, heatmapRange.end, dimension),
  });

  // REQ-6：单日（hour）用 summary.keystats，范围模式（day/month/year）用范围聚合端点；
  // 同一 KeyboardHeatmap 组件承载两种数据源（AC-6.2）。
  const keystatsScope = pcKeystatsScope(dimension);
  const {
    data: rangeKeystats,
    isLoading: rangeKeystatsLoading,
    error: keystatsError,
  } = useQuery({
    queryKey: ['pc-keystats-range', heatmapRange.start, heatmapRange.end],
    queryFn: () => getPcKeystatsRange(heatmapRange.start, heatmapRange.end),
    enabled: keystatsScope === 'range',
  });

  const keystats = keystatsScope === 'range' ? rangeKeystats ?? null : data?.keystats ?? null;
  const keystatsLoading = keystatsScope === 'range' && rangeKeystatsLoading;

  const previewMutation = useMutation({
    mutationFn: ({
      id,
      request,
      requestId,
    }: {
      id: string;
      request: SuggestionClassificationPreviewRequest;
      requestId: number;
    }) => previewAppKnowledgeSuggestion(id, request).then(result => ({ result, requestId })),
    onSuccess: ({ result, requestId }) => {
      if (isCurrentPcRoute3Request(requestId, previewRequestIdRef.current)) {
        setPreview(result);
        setPreviewError(null);
      }
    },
    onError: (error, variables) => {
      if (isCurrentPcRoute3Request(variables.requestId, previewRequestIdRef.current)) {
        setPreviewError(error instanceof Error ? error.message : '预览失败');
      }
    },
  });

  const applyMutation = useMutation({
    mutationFn: ({
      id,
      request,
      requestId,
    }: {
      id: string;
      request: SuggestionClassificationApplyRequest;
      requestId: number;
    }) => applyAppKnowledgeSuggestion(id, request).then(result => ({ result, requestId })),
    onSuccess: ({ requestId }) => {
      if (!isCurrentPcRoute3Request(requestId, applyRequestIdRef.current)) return;

      queryClient.invalidateQueries({ queryKey: ['pc-summary'] });
      queryClient.invalidateQueries({ queryKey: ['pc-activity-analysis'] });
      queryClient.invalidateQueries({ queryKey: ['pc-classification-suggestions'] });
      queryClient.invalidateQueries({ queryKey: ['pc-recent-project-tags'] });
      queryClient.invalidateQueries({ queryKey: ['productivity-dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-apps'] });
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-contexts'] });
      queryClient.invalidateQueries({ queryKey: ['pc-aggregation-focus-blocks'] });
      queryClient.invalidateQueries({ queryKey: ['pc-aggregation-app-usage'] });
      queryClient.invalidateQueries({ queryKey: ['pc-aggregation-late-night'] });
      queryClient.invalidateQueries({ queryKey: ['pc-aggregation-category-distribution'] });
      setActiveSuggestion(null);
      setPreview(null);
      setPreviewError(null);
    },
    onError: (error, variables) => {
      if (isCurrentPcRoute3Request(variables.requestId, applyRequestIdRef.current)) {
        setPreviewError(error instanceof Error ? error.message : '写入失败');
      }
    },
  });

  const rejectMutation = useMutation({
    mutationFn: (id: string) => rejectActivityClassificationSuggestion(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pc-classification-suggestions'] });
    },
  });

  function handleCorrectSuggestion(suggestion: ActivityClassificationSuggestion) {
    setActiveSuggestion(suggestion);
    handleDraftChange();
  }

  function handleCloseDialog() {
    setActiveSuggestion(null);
    handleDraftChange();
    applyRequestIdRef.current = nextPcRoute3RequestId(applyRequestIdRef.current);
    setPreviewError(null);
    previewMutation.reset();
    applyMutation.reset();
  }

  function handleDraftChange() {
    previewRequestIdRef.current = nextPcRoute3RequestId(previewRequestIdRef.current);
    setPreview(null);
    setPreviewError(null);
    previewMutation.reset();
  }

  function handlePreview(request: SuggestionClassificationPreviewRequest) {
    if (!activeSuggestion) return;
    const requestId = nextPcRoute3RequestId(previewRequestIdRef.current);
    previewRequestIdRef.current = requestId;
    setPreview(null);
    setPreviewError(null);
    previewMutation.mutate({ id: activeSuggestion.id, request, requestId });
  }

  function handleApply(request: SuggestionClassificationApplyRequest) {
    if (!activeSuggestion) return;
    const requestId = nextPcRoute3RequestId(applyRequestIdRef.current);
    applyRequestIdRef.current = requestId;
    setPreviewError(null);
    applyMutation.mutate({ id: activeSuggestion.id, request, requestId });
  }

  return (
    <div className="mx-auto w-full max-w-[1500px] space-y-4 pb-8">
      <PageHeader
        title="PC 记录"
        subtitle="从全局概览进入应用、分类与输入行为下钻分析"
        actions={
          <div className="min-w-0 max-w-full">
            <DateDimensionBar
              date={selectedDate}
              dimension={dimension}
              onDateChange={setSelectedDate}
              onDimensionChange={setDimension}
            />
          </div>
        }
      />

      <PcReviewSummary
        summary={data}
        pendingSuggestions={suggestions}
        focusBlocks={focusBlocks}
        lateNight={lateNight}
        categoryDistribution={categoryDistribution}
        dateStr={dateStr}
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.7fr)_minmax(360px,0.9fr)]">
        <AnalysisCard
          title="分类时间线"
          subtitle="按小时分组的甘特图时间线，悬停查看详情"
          actions={
            <button
              type="button"
              onClick={() => setTimelineDialogOpen(true)}
              className="pim-button-primary h-8 px-3 text-xs font-medium"
            >
              查看详情
            </button>
          }
        >
          <CategoryTimeline
            timeline={data?.timeline || []}
          />
        </AnalysisCard>

        <ContextConfirmationPanel
          suggestions={suggestions}
          isLoading={suggestionsLoading}
          onPreview={handleCorrectSuggestion}
          onReject={suggestion => rejectMutation.mutate(suggestion.id)}
        />
      </div>

      <ProductivityDashboardPanel
        focusBlocks={focusBlocks}
        lateNight={lateNight}
        summaryMetrics={data?.metrics ?? null}
        dateStr={dateStr}
      />

      <AnalysisCard title="活动分析" subtitle="按时间块查看活动强度、切换频率和待分类缺口">
        <ActivityAnalysisHeatmap
          analysis={activityAnalysis}
          selectedStart={selectedAnalysisBlockStart}
          onSelectBlock={block => setSelectedAnalysisBlockStart(block.start)}
        />
      </AnalysisCard>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.7fr)_minmax(360px,0.9fr)]">
        <AnalysisCard
          title="活动热力图"
          subtitle={
            dimension === 'hour'
              ? '按所选时间维度汇总输入强度（单日 04:00 起算）'
              : `按所选时间维度汇总输入强度（${heatmapRange.start} ~ ${heatmapRange.end}）`
          }
        >
          <ActivityHeatmap data={heatmapData} isLoading={heatmapLoading} error={heatmapError} />
        </AnalysisCard>

        <AnalysisCard title="当日活动排行" subtitle="分类和应用支持点击筛选">
          <DailyActivityPanel
            metrics={data?.metrics || null}
            categories={data?.categories || []}
            appRanking={data?.appRanking || []}
            appUsage={appUsage}
            selectedCategory={selectedCategory}
            onSelectCategory={setSelectedCategory}
            selectedApp={selectedApp}
            onSelectApp={setSelectedApp}
          />
        </AnalysisCard>
      </div>

      <div className="space-y-4">
        <AnalysisCard
          title="键盘鼠标热力图"
          subtitle={
            keystatsScope === 'range'
              ? `范围聚合 ${heatmapRange.start} ~ ${heatmapRange.end} · 108 键键盘、鼠标按键与快捷键统计`
              : '单日汇总 · 108 键键盘、鼠标按键与快捷键统计'
          }
        >
          <KeyboardHeatmap keystats={keystats} error={keystatsError} />
          {keystatsLoading && <p className="mt-1 text-xs text-slate-400">正在加载键鼠统计...</p>}
        </AnalysisCard>
      </div>

      <AnalysisCard title="待打标队列" subtitle="为未分类的应用、域名和手机应用选择分类，让时间线更准确">
        <LabelingQueue limit={20} />
      </AnalysisCard>

      {/* 展览馆嵌入：PC应用环形 + 键盘热力（真实 via useExhibitionData） */}
      <PcExhibitionEmbed dateStr={dateStr} />

      <ClassificationPreviewDialog
        suggestion={activeSuggestion}
        date={dateStr}
        preview={preview}
        isPreviewing={previewMutation.isPending}
        isApplying={applyMutation.isPending}
        errorMessage={previewError}
        categories={categoryTree}
        onClose={handleCloseDialog}
        onPreview={handlePreview}
        onApply={handleApply}
      />

      <EventTimelineDialog
        open={timelineDialogOpen}
        timeline={data?.timeline || []}
        dateStr={dateStr}
        onClose={() => setTimelineDialogOpen(false)}
      />
    </div>
  );
}

function PcExhibitionEmbed({ dateStr }: { dateStr: string }) {
  const q8 = useExhibitionData(8, { real: true, date: dateStr });
  const q9 = useExhibitionData(9, { real: true, date: dateStr });
  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
      <section className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">PC应用时长 · 环形图（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">VS Code/Chrome占大头 · {q8.isReal ? '🔗真实' : '🔮模拟'} {q8.isEmpty ? '· 暂无数据' : ''}</p>
        <div className="mt-3">{q8.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q8.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <PcAppDonut />}</div>
      </section>
      <section className="pim-card p-4">
        <h3 className="text-sm font-semibold text-slate-900">键盘热力 · 矩阵（真实）</h3>
        <p className="mt-1 text-xs text-slate-500">QWERTY真实频率 · {q9.isReal ? '🔗真实' : '🔮模拟'}</p>
        <div className="mt-3">{q9.loading ? <div className="h-[168px] animate-pulse rounded-md bg-slate-100" /> : q9.error ? <div className="rounded-md border border-red-200 bg-red-50 p-3 text-xs text-red-600">加载失败</div> : <KeyboardHeatmapChart />}</div>
      </section>
    </div>
  );
}
