import type { MobileQuality } from '../../api/mobile';
import { formatDateTime, healthStatusLabel, healthToneClass } from './mobileFormatting';

export interface MobileQualityPanelProps {
  quality?: MobileQuality;
  qualityIssueCount?: number;
  isLoading?: boolean;
}

export default function MobileQualityPanel({
  quality,
  qualityIssueCount = quality?.issues.length ?? 0,
  isLoading = false,
}: MobileQualityPanelProps) {
  const status = quality?.overallStatus ?? 'Unknown';

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">质量面板</h2>
          <p className="mt-1 text-xs text-slate-500">移动端采集、同步和定位质量</p>
        </div>
        <span className={`rounded-full border px-2 py-0.5 text-xs font-medium ${healthToneClass(status)}`}>
          {quality?.label || healthStatusLabel(status)}
        </span>
      </div>

      {isLoading ? (
        <p className="mt-4 text-sm text-slate-500">正在加载质量状态...</p>
      ) : (
        <>
          <p className="mt-4 text-sm leading-6 text-slate-700">
            {quality?.message || '暂无质量诊断数据。'}
          </p>
          <dl className="mt-4 grid grid-cols-2 gap-3 border-t border-slate-100 pt-3 text-xs">
            <div>
              <dt className="text-slate-400">质量问题</dt>
              <dd className="mt-1 font-semibold text-slate-950">{qualityIssueCount}</dd>
            </div>
            <div>
              <dt className="text-slate-400">检查时间</dt>
              <dd className="mt-1 truncate font-medium text-slate-700">{formatDateTime(quality?.checkedAt)}</dd>
            </div>
          </dl>

          {quality?.components.length ? (
            <div className="mt-4 space-y-2">
              {quality.components.slice(0, 4).map(component => (
                <article key={component.key} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                  <div className="flex items-start justify-between gap-2">
                    <h3 className="min-w-0 truncate text-sm font-medium text-slate-950">{component.name}</h3>
                    <span className={`rounded-full border px-2 py-0.5 text-xs ${healthToneClass(component.status)}`}>
                      {healthStatusLabel(component.status)}
                    </span>
                  </div>
                  <p className="mt-2 text-xs leading-5 text-slate-600">{component.message}</p>
                </article>
              ))}
            </div>
          ) : null}

          {quality?.issues.length ? (
            <div className="mt-4 rounded-lg bg-amber-50 p-3">
              <h3 className="text-xs font-semibold text-amber-900">待处理问题</h3>
              <ul className="mt-2 space-y-1">
                {quality.issues.slice(0, 3).map(issue => (
                  <li key={issue.code} className="text-sm leading-5 text-amber-900">
                    {issue.message}
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}
