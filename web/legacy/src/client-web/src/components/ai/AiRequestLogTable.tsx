import type { AiRequestLogListItem, AiRequestStatus, PagedResult } from '../../types';
import StatusBadge from '../../ui/StatusBadge';

interface Props {
  data?: PagedResult<AiRequestLogListItem>;
  selectedId?: string | null;
  isLoading?: boolean;
  error?: Error | null;
  onSelect: (id: string) => void;
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

function formatDateTime(value?: string | null) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function formatNumber(value?: number | null) {
  return value == null ? '-' : value.toLocaleString('zh-CN');
}

export default function AiRequestLogTable({ data, selectedId, isLoading = false, error = null, onSelect }: Props) {
  const items = data?.items ?? [];

  return (
    <section className="pim-panel min-w-0 overflow-hidden p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">请求日志</h2>
          <p className="mt-1 text-xs text-slate-500">最近 AI 网关调用记录</p>
        </div>
        <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-500">
          {formatNumber(data?.totalCount ?? items.length)} 条
        </span>
      </div>

      {isLoading && <p className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">正在加载请求日志...</p>}
      {error && <p className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error.message}</p>}
      {!isLoading && !error && items.length === 0 && (
        <p className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm text-slate-500">
          暂无请求日志。
        </p>
      )}

      {!isLoading && !error && items.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[960px] text-sm">
            <thead className="border-b border-slate-200 text-xs font-medium text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">时间</th>
                <th className="px-3 py-2 text-left">模块 / 用途</th>
                <th className="px-3 py-2 text-left">模型</th>
                <th className="px-3 py-2 text-left">状态</th>
                <th className="px-3 py-2 text-right">Token</th>
                <th className="px-3 py-2 text-right">耗时</th>
                <th className="px-3 py-2 text-left">错误摘要</th>
              </tr>
            </thead>
            <tbody>
              {items.map(item => {
                const selected = selectedId === item.id;

                return (
                  <tr
                    key={item.id}
                    className={`cursor-pointer border-b border-slate-100 last:border-0 ${
                      selected ? 'bg-blue-50/70' : 'hover:bg-slate-50'
                    }`}
                    onClick={() => onSelect(item.id)}
                  >
                    <td className="whitespace-nowrap px-3 py-3 text-xs text-slate-600">{formatDateTime(item.startedAt)}</td>
                    <td className="px-3 py-3">
                      <div className="max-w-[220px] truncate font-medium text-slate-900">{item.module || '-'}</div>
                      <div className="mt-0.5 max-w-[220px] truncate text-xs text-slate-500">{item.purpose || '-'}</div>
                    </td>
                    <td className="px-3 py-3">
                      <div className="max-w-[180px] truncate text-slate-700">{item.model || '-'}</div>
                    </td>
                    <td className="px-3 py-3">
                      <StatusBadge tone={statusTone(item.status)}>{statusLabels[item.status] ?? item.status}</StatusBadge>
                    </td>
                    <td className="px-3 py-3 text-right tabular-nums text-slate-700">{formatNumber(item.totalTokens)}</td>
                    <td className="px-3 py-3 text-right tabular-nums text-slate-700">
                      {item.durationMs == null ? '-' : `${formatNumber(item.durationMs)} ms`}
                    </td>
                    <td className="px-3 py-3">
                      <div className="max-w-[260px] truncate text-xs text-red-600">{item.errorSummary || '-'}</div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
