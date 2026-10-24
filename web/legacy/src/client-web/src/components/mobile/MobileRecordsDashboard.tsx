import type { MobileDevice, MobileQuality, MobileSummary, MobileTimeline as MobileTimelineData } from '../../api/mobile';
import MobileAppRanking from './MobileAppRanking';
import MobileMetricStrip from './MobileMetricStrip';
import MobileQualityPanel from './MobileQualityPanel';
import MobileTimeline from './MobileTimeline';
import { formatDateTime, statusLabel } from './mobileFormatting';

export interface MobileRecordsDashboardProps {
  date: string;
  selectedDeviceId: string;
  devices: MobileDevice[];
  summary?: MobileSummary;
  timeline?: MobileTimelineData;
  quality?: MobileQuality;
  isLoading: boolean;
  isFetching: boolean;
  errorMessage: string | null;
  onDateChange: (value: string) => void;
  onDeviceChange: (value: string) => void;
  onRefresh: () => void;
}

function SyncBatchPanel({ summary }: { summary?: MobileSummary }) {
  const batches = summary?.syncBatches ?? [];

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">同步批次</h2>
          <p className="mt-1 text-xs text-slate-500">Android 客户端上传窗口与接受情况</p>
        </div>
        <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
          {batches.length} 批
        </span>
      </div>

      {batches.length === 0 ? (
        <p className="mt-4 text-sm text-slate-500">暂无同步批次。</p>
      ) : (
        <div className="mt-4 space-y-3">
          {batches.slice(0, 4).map(batch => (
            <article key={batch.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <h3 className="truncate text-sm font-medium text-slate-950">{batch.clientBatchId}</h3>
                  <p className="mt-1 text-xs text-slate-500">{formatDateTime(batch.submittedAtUtc)}</p>
                </div>
                <span className="rounded-full border border-slate-200 bg-white px-2 py-0.5 text-xs text-slate-700">
                  {statusLabel(batch.status)}
                </span>
              </div>
              <dl className="mt-3 grid grid-cols-2 gap-2 text-xs text-slate-600 sm:grid-cols-5">
                <div>
                  <dt className="text-slate-400">已接受条目</dt>
                  <dd>{batch.acceptedEventCount}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">已跳过条目</dt>
                  <dd>{batch.skippedEventCount}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">已拒绝条目</dt>
                  <dd>{batch.rejectedItemCount}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">接受定位</dt>
                  <dd>{batch.acceptedLocationCount}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">拒绝定位</dt>
                  <dd>{batch.rejectedLocationCount}</dd>
                </div>
              </dl>
              {batch.errorMessage && <p className="mt-2 text-xs text-amber-700">{batch.errorMessage}</p>}
            </article>
          ))}
        </div>
      )}
    </section>
  );
}

export default function MobileRecordsDashboard({
  date,
  selectedDeviceId,
  devices,
  summary,
  timeline,
  quality,
  isLoading,
  isFetching,
  errorMessage,
  onDateChange,
  onDeviceChange,
  onRefresh,
}: MobileRecordsDashboardProps) {
  const timelineItems = timeline?.items ?? [];
  const appRanking = summary?.appRanking ?? [];

  return (
    <div className="mx-auto max-w-[1500px] space-y-4 pb-8">
      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h1 className="text-xl font-semibold text-slate-950">手机记录</h1>
            <p className="mt-1 text-sm text-slate-500">查看移动端 App 前台使用、同步批次和质量状态。</p>
          </div>
          <button
            type="button"
            onClick={onRefresh}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
          >
            {isFetching ? '刷新中...' : '刷新'}
          </button>
        </div>

        <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-[220px_minmax(0,1fr)]">
          <label className="min-w-0 text-sm">
            <span className="mb-1 block text-xs text-slate-500">日期</span>
            <input
              type="date"
              value={date}
              onChange={event => onDateChange(event.target.value)}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
            />
          </label>
          <label className="min-w-0 text-sm">
            <span className="mb-1 block text-xs text-slate-500">设备</span>
            <select
              value={selectedDeviceId}
              onChange={event => onDeviceChange(event.target.value)}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
            >
              <option value="">全部设备</option>
              {devices.map(device => (
                <option key={device.deviceId} value={device.deviceId}>
                  {device.displayName || device.model || device.deviceId}
                </option>
              ))}
            </select>
          </label>
        </div>

        {errorMessage && (
          <p className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {errorMessage}
          </p>
        )}
      </section>

      {isLoading ? (
        <section className="rounded-lg border border-slate-200 bg-white p-4 text-sm text-slate-500">
          正在加载手机记录...
        </section>
      ) : (
        <>
          <MobileMetricStrip
            totalForegroundSeconds={summary?.totalForegroundSeconds ?? 0}
            appSwitchCount={summary?.appSwitchCount ?? 0}
            appsUsed={summary?.appsUsed ?? 0}
            completeness={summary?.completeness ?? 0}
            qualityIssueCount={summary?.qualityIssueCount ?? quality?.issues.length ?? 0}
            lastSyncAt={summary?.lastSyncAt ?? null}
            fallbackForegroundSeconds={summary?.fallbackForegroundSeconds ?? 0}
          />

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_420px]">
            <MobileTimeline items={timelineItems} />
            <div className="space-y-4">
              <MobileAppRanking apps={appRanking} totalForegroundSeconds={summary?.totalForegroundSeconds ?? 0} />
              <SyncBatchPanel summary={summary} />
              <MobileQualityPanel quality={quality} qualityIssueCount={summary?.qualityIssueCount} />
            </div>
          </div>
        </>
      )}
    </div>
  );
}
