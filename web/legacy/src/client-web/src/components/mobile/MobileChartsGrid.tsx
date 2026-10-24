import type { MobileAnalyticsChart } from '../../api/mobile';
import EChartBox from '../charts/EChartBox';
import { buildAnalyticsChartOption } from '../charts/mobileChartOptions';

export interface MobileChartsGridProps {
  charts: MobileAnalyticsChart[];
  isLoading?: boolean;
  onCategorySelect?: (category: string) => void;
  onAppSelect?: (packageName: string) => void;
}

function parseClickData(params: unknown): { lifeCategory?: string | null; packageName?: string | null } | null {
  const p = (Array.isArray(params) ? params[0] : params) as { data?: { lifeCategory?: string | null; packageName?: string | null } } | undefined;
  const data = p?.data;
  if (!data) return null;
  return {
    lifeCategory: data.lifeCategory ?? null,
    packageName: data.packageName ?? null,
  };
}

export default function MobileChartsGrid({
  charts,
  isLoading = false,
  onCategorySelect,
  onAppSelect,
}: MobileChartsGridProps) {
  return (
    <section className="grid grid-cols-1 gap-4 xl:grid-cols-2">
      {charts.map(chart => {
        const hasPoints = chart.points.length > 0;
        return (
          <div key={chart.key} className="rounded-md border border-slate-200 bg-white p-4">
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-sm font-semibold text-slate-950">{chart.title}</h2>
              <span className="text-xs text-slate-500">{chart.chartType}</span>
            </div>
            <div className="mt-4">
              {hasPoints ? (
                <EChartBox
                  option={buildAnalyticsChartOption(chart)}
                  height={190}
                  ariaLabel={`${chart.title} 图表`}
                  onEvents={{
                    click: params => {
                      const data = parseClickData(params);
                      if (!data) return;
                      if (chart.chartType === 'top-apps' && data.packageName && onAppSelect) {
                        onAppSelect(data.packageName);
                      } else if (chart.chartType === 'category-share' && data.lifeCategory && onCategorySelect) {
                        onCategorySelect(data.lifeCategory);
                      }
                      // 其余类型不触发回调
                    },
                  }}
                />
              ) : (
                <p className="py-8 text-center text-xs text-slate-500">暂无数据</p>
              )}
            </div>
            {isLoading && <p className="mt-3 text-xs text-slate-500">正在加载图表</p>}
            {!isLoading && !hasPoints && <p className="mt-3 text-xs text-slate-500">暂无图表数据</p>}
          </div>
        );
      })}
    </section>
  );
}
