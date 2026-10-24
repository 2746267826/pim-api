import type { MobileAnalyticsAnomaly, MobileAnalyticsQuality, MobileAnalyticsSuggestion } from '../../api/mobile';
import { formatDateTime, formatPercent, healthToneClass } from './mobileFormatting';

export interface MobileAnomalyPanelProps {
  anomalies: MobileAnalyticsAnomaly[];
  suggestions: MobileAnalyticsSuggestion[];
  quality?: MobileAnalyticsQuality;
  isLoading?: boolean;
}

const emptyQuality: MobileAnalyticsQuality = {
  usageEventsCoverage: 0,
  fallbackShare: 0,
  missingMetadataAppCount: 0,
  systemNoiseShare: 0,
  shortEventShare: 0,
  failedOrPartialSyncBatchCount: 0,
  lastSyncAt: null,
  qualityFlags: [],
};

export default function MobileAnomalyPanel({
  anomalies,
  suggestions,
  quality,
  isLoading = false,
}: MobileAnomalyPanelProps) {
  const qualitySummary = quality ?? emptyQuality;

  return (
    <section className="rounded-md border border-slate-200 bg-white p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-sm font-semibold text-slate-950">异常与建议</h2>
        <span className="text-xs text-slate-500">最近同步 {formatDateTime(qualitySummary.lastSyncAt)}</span>
      </div>
      <div className="mt-4 grid gap-4 lg:grid-cols-[1fr_1fr_0.8fr]">
        <div>
          <h3 className="text-xs font-semibold text-slate-500">异常</h3>
          <div className="mt-2 space-y-2">
            {anomalies.map(item => (
              <div key={item.code} className={`rounded-md border px-3 py-2 text-sm ${healthToneClass(item.severity)}`}>
                <p className="font-medium">{item.title}</p>
                <p className="mt-1 text-xs">{item.evidence}</p>
              </div>
            ))}
            {anomalies.length === 0 && <p className="text-xs text-slate-500">暂无明显异常</p>}
          </div>
        </div>
        <div>
          <h3 className="text-xs font-semibold text-slate-500">建议</h3>
          <div className="mt-2 space-y-2">
            {suggestions.map(item => (
              <p key={item.code} className="rounded-md border border-blue-200 bg-blue-50 px-3 py-2 text-sm text-blue-800">
                {item.text}
              </p>
            ))}
            {suggestions.length === 0 && <p className="text-xs text-slate-500">暂无建议</p>}
          </div>
        </div>
        <div>
          <h3 className="text-xs font-semibold text-slate-500">质量</h3>
          <dl className="mt-2 grid grid-cols-2 gap-2 text-xs">
            <div className="rounded bg-slate-50 p-2">
              <dt className="text-slate-500">事件覆盖</dt>
              <dd className="mt-1 font-semibold text-slate-900">{formatPercent(qualitySummary.usageEventsCoverage)}</dd>
            </div>
            <div className="rounded bg-slate-50 p-2">
              <dt className="text-slate-500">回退占比</dt>
              <dd className="mt-1 font-semibold text-slate-900">{formatPercent(qualitySummary.fallbackShare)}</dd>
            </div>
          </dl>
        </div>
      </div>
      {isLoading && <p className="mt-3 text-xs text-slate-500">正在分析异常</p>}
    </section>
  );
}
