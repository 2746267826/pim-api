import { useMutation, useQueryClient } from '@tanstack/react-query';
import { runAiHealthCheck, runAiTest } from '../../api/ai';
import type { AiStatus } from '../../types';
import StatusBadge from '../../ui/StatusBadge';

interface Props {
  status?: AiStatus;
  isLoading?: boolean;
  error?: Error | null;
}

function formatDateTime(value?: string | null) {
  if (!value) return '未检查';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="truncate text-xs text-slate-500">{label}</dt>
      <dd className="mt-1 break-all text-sm font-medium text-slate-900">{value || '-'}</dd>
    </div>
  );
}

export default function AiStatusPanel({ status, isLoading = false, error = null }: Props) {
  const queryClient = useQueryClient();

  const invalidateAiQueries = () => {
    queryClient.invalidateQueries({ queryKey: ['ai-status'] });
    queryClient.invalidateQueries({ queryKey: ['ai-usage-summary'] });
    queryClient.invalidateQueries({ queryKey: ['ai-requests'] });
    queryClient.invalidateQueries({ queryKey: ['ai-request-detail'] });
  };

  const healthMutation = useMutation({
    mutationFn: runAiHealthCheck,
    onSuccess: invalidateAiQueries,
  });

  const testMutation = useMutation({
    mutationFn: runAiTest,
    onSuccess: invalidateAiQueries,
  });

  const busy = healthMutation.isPending || testMutation.isPending;
  const actionError = healthMutation.error || testMutation.error;

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">LiteLLM 状态</h2>
          <p className="mt-1 text-xs text-slate-500">当前提供商、默认模型与连接检查结果</p>
        </div>
        <StatusBadge tone={status?.enabled ? 'activity' : 'neutral'}>
          {status?.enabled ? '已启用' : '未启用'}
        </StatusBadge>
      </div>

      {isLoading && <p className="mt-4 text-sm text-slate-500">正在加载 AI 状态...</p>}
      {error && <p className="mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error.message}</p>}

      <dl className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
        <Field label="服务商" value={status?.provider ?? '-'} />
        <Field label="默认模型" value={status?.defaultModel ?? '-'} />
        <Field label="Base URL" value={status?.baseUrl ?? '-'} />
        <Field label="上次健康检查" value={formatDateTime(status?.lastHealthCheckAt)} />
        <Field label="最近成功调用" value={formatDateTime(status?.recentSuccessfulCallAt)} />
        <Field label="最后错误" value={status?.lastError ?? '无'} />
      </dl>

      {actionError && (
        <p className="mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {actionError.message}
        </p>
      )}

      <div className="mt-4 flex flex-wrap gap-2">
        <button
          type="button"
          className="pim-button-primary px-3 py-2 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60"
          disabled={busy}
          onClick={() => healthMutation.mutate()}
        >
          {healthMutation.isPending ? '检查中...' : '健康检查'}
        </button>
        <button
          type="button"
          className="pim-button-secondary px-3 py-2 text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60"
          disabled={busy}
          onClick={() => testMutation.mutate()}
        >
          {testMutation.isPending ? '测试中...' : '测试连接'}
        </button>
      </div>
    </section>
  );
}
