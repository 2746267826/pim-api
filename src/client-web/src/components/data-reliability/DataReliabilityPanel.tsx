import type { DataReliabilityInspectionReport, DataReliabilityRuleReport } from '../../api/dataReliabilityTypes';
import DataReliabilityOverview from './DataReliabilityOverview';
import DataReliabilityRuleRow from './DataReliabilityRuleRow';
import { groupRules } from './dataReliabilityModel';

interface DataReliabilityPanelProps {
  report: DataReliabilityInspectionReport;
  now: Date;
  onSelectRule: (rule: DataReliabilityRuleReport) => void;
}

/** 只读体检面板主体：总览 + 三个分组下的 13 条尺子。 */
export default function DataReliabilityPanel({ report, now, onSelectRule }: DataReliabilityPanelProps) {
  const groups = groupRules(report.rules);

  return (
    <div className="space-y-4">
      <DataReliabilityOverview report={report} now={now} />

      {report.totalViolations > 0 && (
        <p className="px-1 text-xs text-slate-500">
          本次共 {report.totalViolations} 条违规，其中新增 {report.windowViolations} 条、存量 {report.historicalViolations} 条。
          存量只计数不当红线，看趋势判断历史修复是否起作用。
        </p>
      )}

      {groups.map(group => (
        <section key={group.label} className="space-y-2">
          <h2 className="px-1 text-base font-semibold text-slate-900">{group.label}</h2>
          {group.rules.map(rule => (
            <DataReliabilityRuleRow key={rule.code} rule={rule} onOpen={onSelectRule} />
          ))}
        </section>
      ))}
    </div>
  );
}
