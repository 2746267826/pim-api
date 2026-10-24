import type { AppKnowledgeContextPattern, AppKnowledgePatternType } from '../../api/appKnowledge';
import AppKnowledgeImpactSummary from './AppKnowledgeImpactSummary';

interface Props {
  contexts: AppKnowledgeContextPattern[];
  isLoading: boolean;
  onDelete: (id: string) => void;
}

const patternLabels: Record<AppKnowledgePatternType, string> = {
  'app-default': 'App 默认',
  domain: '域名',
  title: '窗口标题',
  'url-path': '网址路径',
  'source-family': '来源类型',
};

function getSourceLabel(source: string) {
  if (source === 'builtin') return '内置';
  if (source === 'learned') return '学习';
  if (source === 'custom') return '自定义';
  if (source === 'system') return '系统';
  if (source === 'manual') return '手动';

  return source;
}

function renderTarget(context: AppKnowledgeContextPattern) {
  if (!context.targetCategoryName && !context.projectTag) {
    return <span className="text-slate-400">未分配目标</span>;
  }

  return (
    <div className="flex flex-wrap gap-2">
      {context.targetCategoryName && (
        <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">
          {context.targetCategoryName}
        </span>
      )}
      {context.projectTag && (
        <span className="rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700">
          #{context.projectTag}
        </span>
      )}
    </div>
  );
}

export default function AppKnowledgeContextList({ contexts, isLoading, onDelete }: Props) {
  if (isLoading) {
    return (
      <div className="rounded border border-dashed border-slate-200 px-4 py-8 text-center text-sm text-slate-500">
        正在加载上下文知识...
      </div>
    );
  }

  if (contexts.length === 0) {
    return (
      <div className="rounded border border-dashed border-slate-200 px-4 py-8 text-center text-sm text-slate-500">
        暂无上下文知识模式。
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {contexts.map(context => (
        <article key={context.id} className="rounded border border-slate-200 bg-slate-50 p-3">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0 space-y-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                  上下文知识
                </span>
                <span className="rounded-full bg-white px-2 py-0.5 text-xs font-medium text-slate-700">
                  {patternLabels[context.patternType]}
                </span>
                {!context.enabled && (
                  <span className="rounded-full bg-slate-200 px-2 py-0.5 text-xs font-medium text-slate-600">
                    已停用
                  </span>
                )}
              </div>
              <div className="break-words font-mono text-sm font-semibold text-slate-900">
                {context.patternValue || context.scopeSummary}
              </div>
              <div className="text-xs text-slate-500">
                {context.processName}
                {context.source ? ` · ${getSourceLabel(context.source)}` : ''}
              </div>
            </div>
            <button
              type="button"
              onClick={() => onDelete(context.id)}
              className="shrink-0 rounded px-2 py-1 text-xs font-medium text-red-500 transition-colors hover:bg-red-50"
            >
              删除
            </button>
          </div>

          <div className="mt-3 space-y-2">
            {renderTarget(context)}
            <AppKnowledgeImpactSummary
              affectedRecordCount={context.affectedRecordCount}
              affectedDurationSeconds={context.affectedDurationSeconds}
            />
          </div>
        </article>
      ))}
    </div>
  );
}
