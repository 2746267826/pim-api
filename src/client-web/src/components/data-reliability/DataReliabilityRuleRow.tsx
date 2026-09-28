import StatusBadge from '../../ui/StatusBadge';
import type { DataReliabilityRuleReport } from '../../api/dataReliabilityTypes';
import { formatCurrentValue, statusPresentation } from './dataReliabilityModel';
import S2ThreeStateDistribution from './S2ThreeStateDistribution';

interface DataReliabilityRuleRowProps {
  rule: DataReliabilityRuleReport;
  onOpen: (rule: DataReliabilityRuleReport) => void;
}

/**
 * 首屏 13 条尺子中的一行：名称 / 状态徽标 / 当前值 / 窗内与历史欠账两个数字。
 * 手机端自动换行，不产生横向滚动。
 */
export default function DataReliabilityRuleRow({ rule, onOpen }: DataReliabilityRuleRowProps) {
  const presentation = statusPresentation(rule.status);

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3 transition-colors hover:border-blue-300">
      <button
        type="button"
        onClick={() => onOpen(rule)}
        aria-label={`查看 ${rule.code} ${rule.name} 详情`}
        className="flex min-h-[44px] w-full flex-wrap items-center gap-x-3 gap-y-2 text-left focus:outline-none focus:ring-2 focus:ring-blue-200"
      >
        <span className="w-8 shrink-0 text-xs font-mono text-slate-500">{rule.code}</span>
        <span className="min-w-0 flex-1 text-sm font-medium text-slate-950">{rule.name}</span>
        <StatusBadge tone={presentation.tone}>
          <span aria-hidden="true">{presentation.icon}</span>
          <span className="ml-1">
            {presentation.label} · {rule.statusLabel}
          </span>
        </StatusBadge>
        <span className="text-sm text-slate-700">{formatCurrentValue(rule)}</span>
        <span className="text-xs text-slate-500">
          窗内 {rule.windowViolations} · 历史欠账{' '}
          {/* 欠账数为灰字：它只计数，不带红 / 黄的颜色语义 */}
          <span className="text-slate-400">{rule.historicalViolations}</span>
        </span>
        {rule.scanCoveredDays !== null && rule.scanCoveredDays !== undefined && (
          <span className="text-xs text-slate-400">取数覆盖 {rule.scanCoveredDays} 个业务日</span>
        )}
        {rule.scanTruncated && <span className="text-xs text-amber-700">查询已达上限，结果可能不完整</span>}
      </button>

      {rule.threeState && (
        <div className="mt-3">
          <S2ThreeStateDistribution distribution={rule.threeState} />
        </div>
      )}
    </div>
  );
}
