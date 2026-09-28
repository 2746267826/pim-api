import { useState } from 'react';
import { Dialog } from '../../dialogs/common';
import type { DataReliabilityRuleReport } from '../../api/dataReliabilityTypes';
import {
  dataReliabilityViolationExportLimit,
  getDataReliabilityViolations,
} from '../../api/dataReliability';
import {
  buildViolationExportFileName,
  describeTrend,
  formatCurrentValue,
  formatDateTime,
  serializeViolationExport,
  statusPresentation,
} from './dataReliabilityModel';
import StatusBadge from '../../ui/StatusBadge';

const relatedIssueBaseUrl = 'https://github.com/2746267826/pim-platform/issues';

interface DataReliabilityRuleDialogProps {
  rule: DataReliabilityRuleReport | null;
  onClose: () => void;
  /** 便于测试注入；默认走真实接口。 */
  exportViolations?: (code: string, limit: number) => Promise<unknown>;
  now?: () => Date;
}

/** 下钻弹窗：判据原文、阈值、当前值与分档、违规样例、完整清单导出、关联 issue。只读，不提供修复动作。 */
export default function DataReliabilityRuleDialog({
  rule,
  onClose,
  exportViolations = getDataReliabilityViolations,
  now = () => new Date(),
}: DataReliabilityRuleDialogProps) {
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  if (!rule) return null;
  const presentation = statusPresentation(rule.status);

  async function handleExport() {
    if (!rule) return;
    setExporting(true);
    setExportError(null);
    try {
      const payload = (await exportViolations(
        rule.code,
        dataReliabilityViolationExportLimit
      )) as import('../../api/dataReliabilityTypes').DataReliabilityViolationExport;
      const blob = new Blob([serializeViolationExport(payload)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = buildViolationExportFileName(rule.code, now());
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      setExportError(error instanceof Error ? error.message : '导出失败，请稍后重试');
    } finally {
      setExporting(false);
    }
  }

  return (
    <Dialog open onClose={onClose} title={`${rule.code} · ${rule.name}`}>
      <div className="space-y-4 text-sm text-slate-700" data-testid="data-reliability-dialog">
        <div className="flex flex-wrap items-center gap-2">
          <StatusBadge tone={presentation.tone}>
            <span aria-hidden="true">{presentation.icon}</span>
            <span className="ml-1">
              {presentation.label} · {rule.statusLabel}
            </span>
          </StatusBadge>
          <span className="font-mono text-xs text-slate-500">
            {rule.key} / {rule.invariantCode}
          </span>
          {rule.thresholdFallback && <span className="text-xs text-amber-700">阈值配置非法，已回退默认值</span>}
        </div>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">判据</h3>
          <p>{rule.criterion}</p>
        </section>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">阈值</h3>
          <p>{rule.threshold}</p>
        </section>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">为什么这么定</h3>
          <p>{rule.rationale}</p>
        </section>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">当前情况</h3>
          <p>
            当前值：{formatCurrentValue(rule)}；违规 {rule.totalViolations} 条（
            <span className="text-red-700">新增 {rule.windowViolations}</span> /{' '}
            <span className="text-amber-700">存量 {rule.historicalViolations}</span>）
          </p>
          <p>
            最早发生：{formatDateTime(rule.earliestOccurrenceUtc)}；最近发生：{formatDateTime(rule.latestOccurrenceUtc)}
          </p>
          <p>存量趋势：{describeTrend(rule)}</p>
        </section>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">违规样例（最多 {rule.samples.length} 条）</h3>
          {rule.samples.length > 0 ? (
            <ul className="list-disc space-y-1 pl-5 text-xs text-slate-600">
              {rule.samples.slice(0, 10).map((sample, index) => (
                <li key={`${rule.code}-sample-${index}`}>{sample}</li>
              ))}
            </ul>
          ) : (
            <p className="text-slate-500">暂无违规样例</p>
          )}
        </section>

        <section className="space-y-2">
          <button
            type="button"
            onClick={handleExport}
            disabled={exporting}
            className="min-h-[44px] rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 disabled:opacity-50"
          >
            {exporting ? '导出中…' : '导出完整违规清单'}
          </button>
          <p className="text-xs text-slate-500">
            导出为 JSON（ID + 业务时间 + 设备 + 关键字段），最多 {dataReliabilityViolationExportLimit} 条；面板内只展示 10 条样例。
          </p>
          {exportError && (
            <p role="alert" className="text-xs text-red-700">
              导出失败：{exportError}
            </p>
          )}
        </section>

        <section className="space-y-1">
          <h3 className="font-semibold text-slate-900">关联 issue</h3>
          {rule.relatedIssues.length > 0 ? (
            <ul className="flex flex-wrap gap-2 text-xs">
              {rule.relatedIssues.map(issue => (
                <li key={issue}>
                  <a
                    className="text-blue-700 underline"
                    href={`${relatedIssueBaseUrl}/${issue}`}
                    target="_blank"
                    rel="noreferrer"
                  >
                    #{issue}
                  </a>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-slate-500">无</p>
          )}
        </section>

        <p className="text-xs text-slate-400">本面板只读：修复动作另行开工单，这里不提供任何修复入口。</p>

        <div className="flex justify-end">
          <button
            type="button"
            onClick={onClose}
            className="min-h-[44px] rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700"
          >
            关闭
          </button>
        </div>
      </div>
    </Dialog>
  );
}
