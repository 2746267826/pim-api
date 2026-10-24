import { Link } from 'react-router-dom';
import EmptyState from '../../ui/EmptyState';
import StatusBadge from '../../ui/StatusBadge';
import type { PcQualityTodayData, TodaySection, TodaySectionStatus } from '../../types';
import EChartBox from '../charts/EChartBox';
import { buildQualityRingOption } from '../charts/pcTodayOptions';

function statusTone(status?: TodaySectionStatus | string) {
  if (status === 'critical' || status === 'Critical') return 'danger';
  if (status === 'warning' || status === 'Warning') return 'warning';
  if (status === 'normal' || status === 'Healthy') return 'activity';
  return 'neutral';
}

export default function TodayPcQualitySection({ section }: { section: TodaySection<PcQualityTodayData> }) {
  const { quality, issueCount } = section.data;
  const firstNextStep = quality.nextSteps[0] || quality.issues.find(issue => issue.nextStep)?.nextStep;
  const healthyComponents = quality.components.filter(component => component.status === 'Healthy').length;
  const totalComponents = quality.components.length;

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="font-semibold text-slate-900">PC 数据质量</h2>
        <StatusBadge tone={statusTone(section.status === 'normal' ? section.status : quality.overallStatus)}>
          {quality.label}
        </StatusBadge>
      </div>

      <div className="space-y-3">
        <EChartBox
          option={buildQualityRingOption(healthyComponents, totalComponents)}
          height={120}
          ariaLabel="PC 数据质量完成率"
        />
        <p className="text-sm leading-6 text-slate-600">{quality.message}</p>
        <div className="grid grid-cols-2 gap-3">
          <div className="rounded-xl bg-slate-50 p-3">
            <p className="text-xs text-slate-500">问题</p>
            <p className="mt-1 text-lg font-semibold text-slate-900">{issueCount}</p>
          </div>
          <div className="rounded-xl bg-slate-50 p-3">
            <p className="text-xs text-slate-500">组件</p>
            <p className="mt-1 text-lg font-semibold text-slate-900">{quality.components.length}</p>
          </div>
        </div>

        {firstNextStep ? (
          <p className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs leading-5 text-amber-800">
            {firstNextStep}
          </p>
        ) : (
          <EmptyState title="暂无处理建议" description="PC 数据质量当前没有需要处理的下一步。" />
        )}

        <Link className="text-sm font-medium text-blue-600 hover:text-blue-700" to="/pc-tracker">
          查看 PC 记录
        </Link>
      </div>
    </section>
  );
}
