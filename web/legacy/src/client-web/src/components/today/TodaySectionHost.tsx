import { useQuery } from '@tanstack/react-query';
import { getTodaySection } from '../../api/today';
import { getDeferredAutoRefreshInterval } from '../../lib/autoRefresh';
import EmptyState from '../../ui/EmptyState';
import type {
  CalendarScheduleTodayData,
  CalendarTasksTodayData,
  ClassificationSuggestionsTodayData,
  OperationsHealthTodayData,
  PcActivityTodayData,
  PcQualityTodayData,
  TodaySection,
  TodaySectionKind,
  TodaySectionRegistryItem,
  TaskResponse,
} from '../../types';
import TodayClassificationSuggestionsSection from './TodayClassificationSuggestionsSection';
import TodayHealthSection from './TodayHealthSection';
import {
  TodayAiPlaceholdersSection,
  TodayAvailabilitySection,
  TodayConfirmationsSection,
  TodayEndpointsSection,
  TodayHabitsSection,
  TodayRemindersSection,
  TodayReportsSection,
  TodaySyncOutlookSection,
} from './TodayOpsSections';
import TodayPcOverview from './TodayPcOverview';
import TodayPcQualitySection from './TodayPcQualitySection';
import TodayScheduleList from './TodayScheduleList';
import type { ScheduledItem } from './TodayScheduleList';
import TodayTaskColumn from './TodayTaskColumn';

export const todaySectionOrder: TodaySectionKind[] = [
  'calendar.schedule',
  'pc.activity',
  'calendar.tasks',
  'operations.health',
  'pc.quality',
  'pc.classification_suggestions',
  // 2026-09-19 全量接入：此前「未在 Web 端注册」的 8 个模块（A 类 5 个 + B 类 3 个）。
  'operations.confirmations',
  'sync.outlook',
  'reminders.queue',
  'reports.available',
  'endpoints.status',
  'calendar.availability',
  'calendar.habits',
  'calendar.ai_placeholders',
];

const todaySectionTitles: Record<TodaySectionKind, string> = {
  'calendar.schedule': '今日安排',
  'calendar.tasks': '任务关注',
  'pc.activity': 'PC 记录概览',
  'pc.quality': 'PC 数据质量',
  'operations.health': '系统健康',
  'pc.classification_suggestions': '分类建议',
  'operations.confirmations': '待确认',
  'sync.outlook': '微软同步',
  'reminders.queue': '提醒队列',
  'reports.available': '报告',
  'endpoints.status': '设备端点',
  'calendar.availability': '空闲窗口',
  'calendar.habits': '习惯',
  'calendar.ai_placeholders': 'AI 占位',
};

export function getTodaySectionTitle(kind: TodaySectionKind | string) {
  return isKnownTodaySectionKind(kind) ? todaySectionTitles[kind] : kind;
}

export function isKnownTodaySectionKind(kind: TodaySectionKind | string): kind is TodaySectionKind {
  return todaySectionOrder.includes(kind as TodaySectionKind);
}

function SectionLoading({ title }: { title: string }) {
  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">{title}</h2>
      </div>
      <EmptyState title="加载中" description="正在加载这个区块的数据。" />
    </section>
  );
}

function SectionUnavailable({ title, message }: { title: string; message?: string }) {
  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">{title}</h2>
      </div>
      <EmptyState title="暂不可用" description={message || '这个区块暂时无法提供数据。'} />
    </section>
  );
}

export default function TodaySectionHost({
  item,
  date,
  todayPrefix,
  onSelectScheduled,
  onSelectTask,
}: {
  item: TodaySectionRegistryItem;
  date: string;
  todayPrefix: string;
  onSelectScheduled?: (item: ScheduledItem) => void;
  onSelectTask?: (task: TaskResponse) => void;
}) {
  const known = isKnownTodaySectionKind(item.kind);
  const title = getTodaySectionTitle(item.kind);
  const query = useQuery({
    queryKey: ['today-section', item.id, date],
    queryFn: () => getTodaySection(item.id, date),
    enabled: known,
    refetchInterval: item.kind.startsWith('pc.') || item.kind.startsWith('operations.') ? getDeferredAutoRefreshInterval : false,
  });

  if (!known) {
    return <EmptyState title="未知区块" description={`${item.kind} 暂未在 Web 端注册。`} />;
  }

  if (query.isLoading) {
    return <SectionLoading title={title} />;
  }

  if (query.error) {
    return <SectionUnavailable title={title} message={query.error.message || '请稍后重试。'} />;
  }

  const data = query.data;
  if (!data?.data) {
    return <SectionUnavailable title={title} message="服务端没有返回这个区块的数据。" />;
  }

  if (data.status === 'unavailable') {
    return <SectionUnavailable title={title} message={data.error?.message} />;
  }

  switch (data.kind) {
    case 'calendar.schedule':
      return (
        <TodayScheduleList
          section={data as TodaySection<CalendarScheduleTodayData>}
          onSelect={item => {
            if (onSelectScheduled) {
              onSelectScheduled(item);
              return;
            }
            if (item.type === 'task') {
              onSelectTask?.(item.task);
            }
          }}
        />
      );
    case 'calendar.tasks':
      return (
        <TodayTaskColumn
          section={data as TodaySection<CalendarTasksTodayData>}
          todayPrefix={todayPrefix}
          onSelect={onSelectTask}
        />
      );
    case 'pc.activity':
      return <TodayPcOverview section={data as TodaySection<PcActivityTodayData>} />;
    case 'pc.quality':
      return <TodayPcQualitySection section={data as TodaySection<PcQualityTodayData>} />;
    case 'operations.health':
      return <TodayHealthSection section={data as TodaySection<OperationsHealthTodayData>} />;
    case 'pc.classification_suggestions':
      return (
        <TodayClassificationSuggestionsSection
          section={data as TodaySection<ClassificationSuggestionsTodayData>}
        />
      );
    // 运营与状态（2026-09-19 全量接入）：A 类走独立 API / section 数据；B 类待服务端修复后自动出数。
    case 'operations.confirmations':
      return <TodayConfirmationsSection />;
    case 'sync.outlook':
      return <TodaySyncOutlookSection />;
    case 'reminders.queue':
      return <TodayRemindersSection />;
    case 'reports.available':
      return <TodayReportsSection />;
    case 'endpoints.status':
      return <TodayEndpointsSection section={data as TodaySection<unknown>} />;
    case 'calendar.availability':
      return <TodayAvailabilitySection section={data as TodaySection<unknown>} />;
    case 'calendar.habits':
      return <TodayHabitsSection section={data as TodaySection<unknown>} />;
    case 'calendar.ai_placeholders':
      return <TodayAiPlaceholdersSection section={data as TodaySection<unknown>} />;
    default:
      return <EmptyState title="未知区块" description={`${data.kind} 暂未在 Web 端注册。`} />;
  }
}
