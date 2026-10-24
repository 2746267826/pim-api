import { Link } from 'react-router-dom';
import EmptyState from '../../ui/EmptyState';
import StatusBadge from '../../ui/StatusBadge';
import type { OperationsHealthTodayData, TodaySection, TodaySectionStatus } from '../../types';

function statusTone(status?: TodaySectionStatus | string) {
  if (status === 'critical' || status === 'Critical') return 'danger';
  if (status === 'warning' || status === 'Warning') return 'warning';
  if (status === 'normal' || status === 'Healthy') return 'activity';
  return 'neutral';
}

export default function TodayHealthSection({ section }: { section: TodaySection<OperationsHealthTodayData> }) {
  const { detail, summary } = section.data;
  const daemon = detail.components.find(component => component.key === 'windows-daemon');
  const firstNextStep = detail.nextSteps[0];

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">系统健康</h2>
        <StatusBadge tone={statusTone(section.status === 'normal' ? section.status : summary.status)}>
          {summary.label}
        </StatusBadge>
      </div>

      <div className="space-y-3">
        <p className="text-sm leading-6 text-slate-600">{summary.message}</p>

        {daemon && (
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
            <div className="flex items-center justify-between gap-3">
              <p className="truncate text-sm font-medium text-slate-800">{daemon.name}</p>
              <StatusBadge tone={statusTone(daemon.status)}>{daemon.status}</StatusBadge>
            </div>
            <p className="mt-1 text-xs leading-5 text-slate-500">{daemon.message}</p>
          </div>
        )}

        {firstNextStep ? (
          <p className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs leading-5 text-amber-800">
            {firstNextStep}
          </p>
        ) : (
          <EmptyState title="暂无处理建议" description="系统健康当前没有需要处理的下一步。" />
        )}

        <Link className="text-sm font-medium text-blue-600 hover:text-blue-700" to="/status">
          查看状态
        </Link>
      </div>
    </section>
  );
}
