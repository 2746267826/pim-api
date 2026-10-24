import type { MobileAppUsageSummary } from '../../api/mobile';
import { formatDateTime, formatDuration, formatPercent } from './mobileFormatting';

export interface MobileAppRankingProps {
  apps: MobileAppUsageSummary[];
  totalForegroundSeconds: number;
  isLoading?: boolean;
}

function sourceLabel(source: string) {
  if (source === 'events') return '事件明细';
  if (source === 'fallback') return '回退汇总';
  return source;
}

export default function MobileAppRanking({
  apps,
  totalForegroundSeconds,
  isLoading = false,
}: MobileAppRankingProps) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">App 排行</h2>
          <p className="mt-1 text-xs text-slate-500">按前台时长排序的手机使用情况</p>
        </div>
        <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-xs text-slate-600">
          {apps.length} 个 App
        </span>
      </div>

      {isLoading ? (
        <p className="mt-4 text-sm text-slate-500">正在加载 App 排行...</p>
      ) : apps.length === 0 ? (
        <p className="mt-4 text-sm text-slate-500">暂无 App 使用数据。</p>
      ) : (
        <div className="mt-4 space-y-3">
          {apps.map((app, index) => {
            const share = totalForegroundSeconds > 0 ? app.foregroundSeconds / totalForegroundSeconds : app.share;
            return (
              <article key={app.packageName} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                <div className="flex items-start gap-3">
                  <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-slate-900 text-xs font-semibold text-white">
                    {index + 1}
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-start justify-between gap-2">
                      <div className="min-w-0">
                        <h3 className="truncate text-sm font-semibold text-slate-950">{app.displayName || app.packageName}</h3>
                        {app.categoryName && <p className="mt-1 text-xs text-slate-500">{app.categoryName}</p>}
                        {(app.displayName !== app.packageName || !app.categoryName) && (
                          <p className="mt-1 truncate font-mono text-xs text-slate-400">{app.packageName}</p>
                        )}
                      </div>
                      <span className="text-sm font-semibold text-slate-950">{formatDuration(app.foregroundSeconds)}</span>
                    </div>
                    <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-200">
                      <div
                        className="h-full rounded-full bg-blue-500"
                        style={{ width: `${Math.max(3, Math.min(100, Math.round(share * 100)))}%` }}
                      />
                    </div>
                    <dl className="mt-3 grid grid-cols-2 gap-2 text-xs text-slate-600 sm:grid-cols-4">
                      <div>
                        <dt className="text-slate-400">占比</dt>
                        <dd>{formatPercent(share)}</dd>
                      </div>
                      <div>
                        <dt className="text-slate-400">启动</dt>
                        <dd>{app.launchCount} 次</dd>
                      </div>
                      <div>
                        <dt className="text-slate-400">会话</dt>
                        <dd>{app.sessionCount} 段</dd>
                      </div>
                      <div>
                        <dt className="text-slate-400">最近使用</dt>
                        <dd className="truncate">{formatDateTime(app.lastUsedAt)}</dd>
                      </div>
                    </dl>
                    <p className="mt-2 text-xs text-slate-400">{sourceLabel(app.source)}</p>
                  </div>
                </div>
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}
