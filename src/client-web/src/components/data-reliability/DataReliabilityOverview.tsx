import type { DataReliabilityInspectionReport } from '../../api/dataReliabilityTypes';
import { buildLedgerLine, buildOverviewCounts, formatDateTime, formatFreshness } from './dataReliabilityModel';

interface DataReliabilityOverviewProps {
  report: DataReliabilityInspectionReport;
  /** 注入式"现在"，便于测试新鲜度文案而不依赖真实时钟。 */
  now: Date;
}

/** 面板顶部总览：红/黄/绿/未知条数 + 本次体检时间与新鲜度。 */
export default function DataReliabilityOverview({ report, now }: DataReliabilityOverviewProps) {
  const counts = buildOverviewCounts(report);

  return (
    <section className="pim-card flex flex-wrap items-center gap-x-4 gap-y-2 p-4 text-sm">
      <span className="font-medium text-slate-900" data-testid="data-reliability-overview">
        🔴 {counts.red} 条 · 🟡 {counts.yellow} 条 · 🟢 {counts.green} 条
        {counts.unknown > 0 && <> · ⚪ {counts.unknown} 条</>}
      </span>
      <span className="text-slate-600">
        本次体检时间：{formatDateTime(report.inspectedAtUtc)}（{formatFreshness(report.inspectedAtUtc, now)}）
      </span>
      {counts.red > 0 && <strong className="text-red-700">数据在流血：{counts.red} 条尺子报红</strong>}
      {counts.red === 0 && counts.unknown === 0 && <span className="text-emerald-700">13 条尺子暂无红线</span>}
      {counts.unknown > 0 && <span className="text-slate-600">{counts.unknown} 条数据源不足，未能判定</span>}
      {/* 考核账本信息行（REQ-7 / AC-7.1）：账本起始时刻 + 重启自动重置说明 + 考核窗时长 */}
      <span className="w-full text-xs text-slate-600" data-testid="data-reliability-ledger">
        {buildLedgerLine(report)}
      </span>
    </section>
  );
}
