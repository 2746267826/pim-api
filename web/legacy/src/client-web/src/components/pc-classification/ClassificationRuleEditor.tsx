import type { ActivityClassificationRule } from '../../types';

interface Props {
  rule: ActivityClassificationRule | null;
  onClose: () => void;
}

function formatJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function formatPercent(value: number) {
  return `${Math.round(value * 100)}%`;
}

export default function ClassificationRuleEditor({ rule, onClose }: Props) {
  if (!rule) {
    return (
      <section className="pim-panel min-h-[320px] p-4">
        <h2 className="text-sm font-semibold text-slate-950">规则详情</h2>
        <div className="mt-4 rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">
          从规则列表中选择一条规则。
        </div>
      </section>
    );
  }

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex min-w-0 items-center gap-2">
            <span
              aria-hidden="true"
              className="h-3 w-3 shrink-0 rounded-full"
              style={{ backgroundColor: rule.color || '#64748b' }}
            />
            <h2 className="truncate text-sm font-semibold text-slate-950">{rule.ruleName}</h2>
          </div>
          <p className="mt-1 text-xs text-slate-500">
            {rule.source} / {rule.status}
          </p>
        </div>
        <button type="button" className="pim-button-secondary h-8 shrink-0 px-3 text-xs" onClick={onClose}>
          关闭
        </button>
      </div>

      <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
        <div className="min-w-0 rounded-lg bg-slate-50 px-3 py-2">
          <dt className="text-xs font-medium text-slate-500">分类</dt>
          <dd className="mt-1 truncate text-slate-950">{rule.categoryName || '-'}</dd>
        </div>
        <div className="min-w-0 rounded-lg bg-slate-50 px-3 py-2">
          <dt className="text-xs font-medium text-slate-500">项目标签</dt>
          <dd className="mt-1 truncate text-slate-950">{rule.projectTag || '-'}</dd>
        </div>
        <div className="min-w-0 rounded-lg bg-slate-50 px-3 py-2">
          <dt className="text-xs font-medium text-slate-500">优先级</dt>
          <dd className="mt-1 tabular-nums text-slate-950">{rule.priority}</dd>
        </div>
        <div className="min-w-0 rounded-lg bg-slate-50 px-3 py-2">
          <dt className="text-xs font-medium text-slate-500">置信度</dt>
          <dd className="mt-1 tabular-nums text-slate-950">{formatPercent(rule.confidence)}</dd>
        </div>
      </dl>

      {rule.explanation && (
        <div className="mt-4 rounded-lg bg-blue-50 px-3 py-2 text-sm text-blue-900">
          {rule.explanation}
        </div>
      )}

      <div className="mt-4">
        <div className="mb-2 text-xs font-medium text-slate-500">条件 JSON</div>
        <pre className="max-h-[360px] overflow-auto rounded-lg bg-slate-950 p-3 text-xs leading-relaxed text-slate-100">
          {formatJson(rule.conditionsJson)}
        </pre>
      </div>
    </section>
  );
}
