import type { DerivedMetrics, CategorySummary, AppRankingItem } from '../../types';
import type { PcAppUsageResponse } from '../../api/pcTracker';
import EChartBox from '../charts/EChartBox';
import { buildAppUsageBarOption } from '../charts/pcPanelOptions';

interface Props {
  metrics: DerivedMetrics | null;
  categories: CategorySummary[];
  appRanking: AppRankingItem[];
  appUsage?: PcAppUsageResponse;
  selectedCategory: string | null;
  onSelectCategory: (cat: string | null) => void;
  selectedApp: string | null;
  onSelectApp: (app: string | null) => void;
}

function CompactStat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
      <div className="text-[11px] text-slate-500">{label}</div>
      <div className="mt-1 min-w-0 break-words text-sm font-semibold text-slate-950">{value}</div>
    </div>
  );
}

export default function DailyActivityPanel({
  metrics,
  categories,
  appRanking,
  appUsage,
  selectedCategory,
  onSelectCategory,
  selectedApp,
  onSelectApp,
}: Props) {
  if (!metrics) return <div className="rounded-xl border border-slate-200 bg-slate-50 py-10 text-center text-sm text-slate-400">暂无活动数据</div>;

  const top5Categories = categories.slice(0, 5);
  const top5Apps = appRanking.slice(0, 5);

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <CompactStat label="工作会话" value={`${metrics.sessionCount} 个`} />
        <CompactStat label="应用切换" value={`${metrics.appSwitchCount} 次`} />
        <CompactStat label="最专注应用" value={metrics.mostFocusedApp || '-'} />
        <CompactStat label="按键/点击比" value={`${metrics.keyClickRatio}:1`} />
      </div>

      <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
        <div className="mb-3 flex items-center justify-between">
          <div className="text-xs font-semibold text-slate-700">分类排行</div>
          {selectedCategory && (
            <button type="button" className="text-xs text-blue-600 hover:text-blue-700" onClick={() => onSelectCategory(null)}>
              清除
            </button>
          )}
        </div>
        <div className="space-y-2">
          {top5Categories.length === 0 ? (
            <p className="py-3 text-center text-xs text-slate-400">暂无分类数据</p>
          ) : top5Categories.map(category => (
            <button
              key={category.categoryName}
              type="button"
              className={`w-full rounded-lg border px-3 py-2 text-left transition-colors ${
                selectedCategory === category.categoryName
                  ? 'border-blue-300 bg-blue-50'
                  : 'border-slate-200 bg-white hover:border-blue-200'
              }`}
              onClick={() => onSelectCategory(selectedCategory === category.categoryName ? null : category.categoryName)}
            >
              <div className="flex items-center justify-between gap-3 text-xs">
                <span className="flex min-w-0 items-center gap-2 font-medium text-slate-800">
                  <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: category.color }} />
                  <span className="truncate">{category.categoryName}</span>
                </span>
                <span className="shrink-0 text-slate-500">{category.share}%</span>
              </div>
            </button>
          ))}
        </div>
      </div>

      <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
        <div className="mb-3 flex items-center justify-between">
          <div className="text-xs font-semibold text-slate-700">{appUsage && appUsage.items.length > 0 ? '应用时长排行' : '应用排行'}</div>
          {(!appUsage || appUsage.items.length === 0) && selectedApp && (
            <button type="button" className="text-xs text-blue-600 hover:text-blue-700" onClick={() => onSelectApp(null)}>
              清除
            </button>
          )}
        </div>
        {appUsage && appUsage.items.length > 0 ? (
          <EChartBox
            option={buildAppUsageBarOption(appUsage)}
            height={Math.min(appUsage.items.length, 8) * 28 + 40}
            ariaLabel="应用时长排行"
          />
        ) : (
          <div className="space-y-2">
            {top5Apps.length === 0 ? (
              <p className="py-3 text-center text-xs text-slate-400">暂无应用数据</p>
            ) : top5Apps.map(app => {
              // #301：直接使用接口返回的 share —— 它已是「占全部应用（按键+点击）」的占比。
              // 若在此按前五名重新归一，被截掉的第 6+ 名不会进入分母，显示会重新变成
              // 「Top 5 内部占比」，与口径不符（review 发现）。
              // #301：占总量占比，显示 1 位小数。
              const share = (app.share * 100).toFixed(1);
              return (
                <button
                  key={app.appName}
                  type="button"
                  className={`w-full rounded-lg border px-3 py-2 text-left transition-colors ${
                    selectedApp === app.appName
                      ? 'border-teal-300 bg-teal-50'
                      : 'border-slate-200 bg-white hover:border-teal-200'
                  }`}
                  onClick={() => onSelectApp(selectedApp === app.appName ? null : app.appName)}
                >
                  <div className="mb-1 flex items-center justify-between gap-3 text-xs">
                    <span className="min-w-0 truncate font-medium text-slate-800">{app.displayName || app.appName}</span>
                    <span className="shrink-0 text-slate-500">{share}%</span>
                  </div>
                  <div className="h-1.5 overflow-hidden rounded-full bg-slate-200">
                    <div className="h-full rounded-full bg-teal-500" style={{ width: `${share}%` }} />
                  </div>
                </button>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
