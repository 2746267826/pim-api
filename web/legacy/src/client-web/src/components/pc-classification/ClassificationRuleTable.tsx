import type { ActivityClassificationRule } from '../../types';

interface Props {
  rules: ActivityClassificationRule[];
  selectedRuleId?: string | null;
  isLoading?: boolean;
  onEdit: (rule: ActivityClassificationRule) => void;
}

const sourceLabels: Record<string, string> = {
  builtin: '内置',
  heuristic: '启发式',
  user: '用户',
  llm: 'AI',
};

const statusLabels: Record<string, string> = {
  active: '启用',
  inactive: '停用',
};

function getSourceLabel(source: string) {
  return sourceLabels[source] ?? source;
}

function getStatusLabel(status: string) {
  return statusLabels[status] ?? status;
}

export default function ClassificationRuleTable({
  rules,
  selectedRuleId,
  isLoading = false,
  onEdit,
}: Props) {
  if (isLoading) {
    return (
      <section className="pim-panel p-4">
        <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">
          正在加载分类规则...
        </div>
      </section>
    );
  }

  if (rules.length === 0) {
    return (
      <section className="pim-panel p-4">
        <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">
          暂无分类规则。
        </div>
      </section>
    );
  }

  return (
    <section className="pim-panel min-w-0 overflow-hidden p-4">
      <div className="mb-3 flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold text-slate-950">规则列表</h2>
        <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">
          {rules.length.toLocaleString('zh-CN')} 条
        </span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[760px] text-sm">
          <thead className="border-b border-slate-200 text-xs font-medium text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left">规则</th>
              <th className="px-3 py-2 text-left">分类</th>
              <th className="px-3 py-2 text-left">项目</th>
              <th className="px-3 py-2 text-left">来源</th>
              <th className="px-3 py-2 text-right">优先级</th>
              <th className="px-3 py-2 text-right">操作</th>
            </tr>
          </thead>
          <tbody>
            {rules.map(rule => {
              const selected = selectedRuleId === rule.id;

              return (
                <tr
                  key={rule.id}
                  className={`border-b border-slate-100 last:border-0 ${
                    selected ? 'bg-blue-50/70' : 'hover:bg-slate-50'
                  }`}
                >
                  <td className="px-3 py-3">
                    <div className="flex min-w-0 items-center gap-2">
                      <span
                        aria-hidden="true"
                        className="h-2.5 w-2.5 shrink-0 rounded-full"
                        style={{ backgroundColor: rule.color || '#64748b' }}
                      />
                      <div className="min-w-0">
                        <div className="truncate font-medium text-slate-950">{rule.ruleName}</div>
                        <div className="mt-0.5 max-w-[420px] truncate text-xs text-slate-500">
                          {rule.explanation || rule.conditionsJson}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td className="px-3 py-3 text-slate-700">{rule.categoryName || '-'}</td>
                  <td className="px-3 py-3 text-slate-700">{rule.projectTag || '-'}</td>
                  <td className="px-3 py-3">
                    <div className="flex flex-wrap gap-1">
                      <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600">
                        {getSourceLabel(rule.source)}
                      </span>
                      <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">
                        {getStatusLabel(rule.status)}
                      </span>
                    </div>
                  </td>
                  <td className="px-3 py-3 text-right tabular-nums text-slate-700">{rule.priority}</td>
                  <td className="px-3 py-3 text-right">
                    <button
                      type="button"
                      className="pim-button-secondary h-8 px-3 text-xs"
                      onClick={() => onEdit(rule)}
                    >
                      查看
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}
