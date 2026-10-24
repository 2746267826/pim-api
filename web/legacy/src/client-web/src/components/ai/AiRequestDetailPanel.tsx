import type { AiRequestLogDetail, AiRequestStatus } from '../../types';
import MetricCard from '../../ui/MetricCard';
import StatusBadge from '../../ui/StatusBadge';

interface Props {
  detail?: AiRequestLogDetail;
  isLoading?: boolean;
  error?: Error | null;
}

const statusLabels: Record<AiRequestStatus, string> = {
  Succeeded: '成功',
  Failed: '失败',
  Blocked: '已阻止',
  TimedOut: '超时',
  FailedValidation: '校验失败',
};

function statusTone(status: AiRequestStatus) {
  if (status === 'Succeeded') return 'activity';
  if (status === 'FailedValidation' || status === 'Blocked') return 'warning';
  return 'danger';
}

function formatNumber(value?: number | null) {
  return value == null ? '-' : value.toLocaleString('zh-CN');
}

function formatCost(value?: number | null, currency?: string | null) {
  if (value == null) return '-';
  return `${currency || '$'}${value.toFixed(4)}`;
}

function formatJson(text?: string | null) {
  if (!text) return '-';
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

function InfoRow({ label, value }: { label: string; value?: string | number | null }) {
  return (
    <div className="min-w-0">
      <dt className="truncate text-xs text-slate-500">{label}</dt>
      <dd className="mt-1 break-all text-sm font-medium text-slate-900">{value ?? '-'}</dd>
    </div>
  );
}

function CodeBlock({ title, value }: { title: string; value?: string | null }) {
  return (
    <section className="min-w-0">
      <h3 className="mb-2 text-xs font-semibold text-slate-500">{title}</h3>
      <pre className="max-h-72 overflow-auto rounded-lg border border-slate-200 bg-slate-950 p-3 text-xs leading-5 text-slate-100">
        <code>{formatJson(value)}</code>
      </pre>
    </section>
  );
}

export default function AiRequestDetailPanel({ detail, isLoading = false, error = null }: Props) {
  if (!detail && !isLoading && !error) {
    return (
      <section className="pim-panel min-h-[280px] min-w-0 p-4">
        <div className="flex h-full min-h-[240px] items-center justify-center rounded-lg border border-dashed border-slate-200 bg-slate-50 p-6 text-center">
          <div className="max-w-sm">
            <h2 className="text-sm font-semibold text-slate-800">选择请求</h2>
            <p className="mt-2 text-sm text-slate-500">选择一条请求查看完整提示、输出、JSON 与校验结果。</p>
          </div>
        </div>
      </section>
    );
  }

  return (
    <section className="pim-panel min-w-0 p-4">
      <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">请求详情</h2>
          <p className="mt-1 break-all text-xs text-slate-500">{detail?.id ?? '正在加载...'}</p>
        </div>
        {detail && <StatusBadge tone={statusTone(detail.status)}>{statusLabels[detail.status] ?? detail.status}</StatusBadge>}
      </div>

      {isLoading && <p className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">正在加载请求详情...</p>}
      {error && <p className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error.message}</p>}

      {detail && (
        <div className="space-y-4">
          <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <InfoRow label="模块" value={detail.module} />
            <InfoRow label="用途" value={detail.purpose} />
            <InfoRow label="模型" value={detail.model} />
            <InfoRow label="服务商" value={detail.provider} />
            <InfoRow label="Correlation ID" value={detail.correlationId} />
            <InfoRow label="LiteLLM Request ID" value={detail.liteLlmRequestId} />
            <InfoRow label="尝试次数" value={`${detail.attemptNumber} / ${detail.maxAttempts}`} />
            <InfoRow label="来源对象" value={`${detail.sourceObjectType || '-'} ${detail.sourceObjectId || ''}`} />
          </dl>

          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <MetricCard label="输入 Token" value={formatNumber(detail.usage.promptTokens)} />
            <MetricCard label="输出 Token" value={formatNumber(detail.usage.completionTokens)} />
            <MetricCard label="总 Token" value={formatNumber(detail.usage.totalTokens ?? detail.totalTokens)} tone="activity" />
            <MetricCard label="预估成本" value={formatCost(detail.usage.estimatedCost ?? detail.estimatedCost, detail.usage.currency)} />
          </div>

          {(detail.errorCode || detail.errorMessage || detail.errorSummary) && (
            <div className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
              <p className="font-semibold">{detail.errorCode || detail.errorSummary || '请求错误'}</p>
              {detail.errorMessage && <p className="mt-1 break-words">{detail.errorMessage}</p>}
            </div>
          )}

          <div className="space-y-4">
            <CodeBlock title="消息列表" value={detail.requestMessagesJson} />
            <CodeBlock title="请求载荷" value={detail.requestPayloadJson} />
            <CodeBlock title="响应文本" value={detail.responseText} />
            <CodeBlock title="原始响应" value={detail.responseRawJson} />
            <CodeBlock title="解析后的 JSON" value={detail.parsedOutputJson} />
            <CodeBlock title="结构校验错误" value={detail.schemaValidationErrorsJson} />
          </div>
        </div>
      )}
    </section>
  );
}
