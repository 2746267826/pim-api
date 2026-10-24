import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getEndpointCollectionQuality,
  handleEndpointNotificationAction,
  heartbeatEndpoint,
  listEndpointStatuses,
} from '../api/endpoints';
import type { EndpointPlatform, EndpointStatus, OperationRiskLevel } from '../types';
import PageHeader from '../ui/PageHeader';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

function formatDateTime(value?: string | null) {
  if (!value) return '暂无';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function defaultDeviceId(platform: EndpointPlatform) {
  return platform === 'android' ? 'android-companion' : 'windows-companion';
}

function statusTone(status: string) {
  const normalized = status.toLowerCase();
  if (normalized === 'healthy') return 'bg-emerald-50 text-emerald-700';
  if (normalized === 'warning') return 'bg-amber-50 text-amber-700';
  if (normalized === 'critical') return 'bg-rose-50 text-rose-700';
  return 'bg-slate-100 text-slate-600';
}

export default function EndpointShellPage() {
  const queryClient = useQueryClient();
  const [selectedDeviceId, setSelectedDeviceId] = useState<string>('');
  const [manualDeviceId, setManualDeviceId] = useState(defaultDeviceId('windows'));
  const [manualPlatform, setManualPlatform] = useState<EndpointPlatform>('windows');

  const { data: endpoints = [], isLoading } = useQuery({
    queryKey: ['endpoint-statuses'],
    queryFn: listEndpointStatuses,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  useEffect(() => {
    if (!selectedDeviceId && endpoints.length > 0) {
      setSelectedDeviceId(endpoints[0].deviceId);
    }
  }, [endpoints, selectedDeviceId]);

  const selectedEndpoint = useMemo<EndpointStatus | undefined>(() => (
    endpoints.find(endpoint => endpoint.deviceId === selectedDeviceId) ?? endpoints[0]
  ), [endpoints, selectedDeviceId]);

  const qualityDeviceId = selectedEndpoint?.deviceId || manualDeviceId;
  const { data: quality } = useQuery({
    queryKey: ['endpoint-collection-quality', qualityDeviceId],
    queryFn: () => getEndpointCollectionQuality(qualityDeviceId),
    enabled: Boolean(qualityDeviceId),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const heartbeatMutation = useMutation({
    mutationFn: () => heartbeatEndpoint(manualDeviceId, {
      platform: manualPlatform,
      appVersion: 'web-shell',
      uploadStatus: 'Healthy',
      collectionCacheCount: selectedEndpoint?.collectionCacheCount ?? 0,
    }),
    onSuccess: endpoint => {
      setSelectedDeviceId(endpoint.deviceId);
      queryClient.invalidateQueries({ queryKey: ['endpoint-statuses'] });
      queryClient.invalidateQueries({ queryKey: ['endpoint-collection-quality', endpoint.deviceId] });
    },
  });

  const notificationActionMutation = useMutation({
    mutationFn: (riskLevel: OperationRiskLevel) => handleEndpointNotificationAction(qualityDeviceId, {
      action: riskLevel === 'L1LowRiskAction' ? 'dismiss' : 'open-confirmation',
      riskLevel,
      confirmationId: riskLevel === 'L1LowRiskAction' ? null : 'pending-confirmation',
      relatedObjectType: 'task',
      relatedObjectId: 'endpoint-shell-preview',
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['endpoint-statuses'] });
      queryClient.invalidateQueries({ queryKey: ['endpoint-collection-quality', qualityDeviceId] });
    },
  });

  const totalBlocked = endpoints.reduce((sum, endpoint) => sum + endpoint.onlineOnlyBlockedCount, 0);

  return (
    <div className="mx-auto w-full max-w-[1300px] space-y-4 pb-8" data-page="EndpointShellPage">
      <PageHeader
        title="端点外壳"
        subtitle="Windows 与 Android 只缓存采集上传，复杂事实变更统一回到 Web 确认。"
        actions={
          <button
            type="button"
            onClick={() => heartbeatMutation.mutate()}
            disabled={heartbeatMutation.isPending || !manualDeviceId.trim()}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            发送心跳
          </button>
        }
      />

      <section className="pim-panel p-4" data-contract="online-only boundary">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-[minmax(0,1fr)_180px_auto]">
          <label>
            <span className="text-xs font-semibold text-slate-500">设备 ID</span>
            <input
              value={manualDeviceId}
              onChange={event => setManualDeviceId(event.target.value)}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
            />
          </label>
          <label>
            <span className="text-xs font-semibold text-slate-500">平台</span>
            <select
              value={manualPlatform}
              onChange={event => {
                const next = event.target.value as EndpointPlatform;
                setManualPlatform(next);
                setManualDeviceId(defaultDeviceId(next));
              }}
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm"
            >
              <option value="windows">Windows</option>
              <option value="android">Android</option>
            </select>
          </label>
          <div className="flex items-end">
            <span className="rounded-lg bg-slate-100 px-3 py-2 text-xs font-semibold text-slate-600">
              在线专属拦截 {totalBlocked}
            </span>
          </div>
        </div>
      </section>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.15fr)_minmax(320px,0.85fr)]">
        <section className="pim-panel p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 className="text-sm font-semibold text-slate-950">端点状态</h2>
            <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
              {endpoints.length} 台设备
            </span>
          </div>

          {isLoading ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              正在加载端点状态。
            </p>
          ) : endpoints.length === 0 ? (
            <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-10 text-center text-sm text-slate-500">
              暂无端点心跳，可先发送一次本机心跳。
            </p>
          ) : (
            <div className="mt-4 grid gap-3">
              {endpoints.map(endpoint => (
                <button
                  type="button"
                  key={endpoint.deviceId}
                  onClick={() => setSelectedDeviceId(endpoint.deviceId)}
                  className={`rounded-lg border p-3 text-left transition-colors ${
                    endpoint.deviceId === qualityDeviceId
                      ? 'border-blue-300 bg-blue-50'
                      : 'border-slate-200 bg-white hover:border-blue-200'
                  }`}
                >
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="min-w-0">
                      <h3 className="truncate text-sm font-semibold text-slate-950">{endpoint.deviceId}</h3>
                      <p className="mt-1 text-xs text-slate-500">
                        {endpoint.platform} / {formatDateTime(endpoint.lastHeartbeatAt)}
                      </p>
                    </div>
                    <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${statusTone(endpoint.uploadStatus)}`}>
                      {endpoint.uploadStatus}
                    </span>
                  </div>
                  <div className="mt-3 grid grid-cols-2 gap-2">
                    <div className="rounded-lg bg-slate-50 px-3 py-2">
                      <p className="text-xs font-semibold text-slate-400">采集缓存</p>
                      <p className="mt-1 text-sm text-slate-700">{endpoint.collectionCacheCount}</p>
                    </div>
                    <div className="rounded-lg bg-slate-50 px-3 py-2">
                      <p className="text-xs font-semibold text-slate-400">在线处理</p>
                      <p className="mt-1 text-sm text-slate-700">{endpoint.onlineOnlyBlockedCount}</p>
                    </div>
                  </div>
                </button>
              ))}
            </div>
          )}
        </section>

        <div className="space-y-4">
          <section className="pim-panel p-4" data-contract="collection quality">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-sm font-semibold text-slate-950">采集质量</h2>
              {quality && (
                <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${statusTone(quality.uploadStatus)}`}>
                  {quality.uploadStatus}
                </span>
              )}
            </div>
            <div className="mt-3 grid gap-2">
              <div className="rounded-lg bg-slate-50 px-3 py-2">
                <p className="text-xs font-semibold text-slate-400">检查设备</p>
                <p className="mt-1 break-words text-sm text-slate-700">{quality?.deviceId ?? qualityDeviceId}</p>
              </div>
              <div className="rounded-lg bg-slate-50 px-3 py-2">
                <p className="text-xs font-semibold text-slate-400">问题数量</p>
                <p className="mt-1 text-sm text-slate-700">{quality?.issueCount ?? 0}</p>
              </div>
              <div className="rounded-lg bg-slate-50 px-3 py-2">
                <p className="text-xs font-semibold text-slate-400">检查时间</p>
                <p className="mt-1 text-sm text-slate-700">{formatDateTime(quality?.checkedAt)}</p>
              </div>
            </div>
          </section>

          <section className="pim-panel p-4" data-contract="notification action">
            <h2 className="text-sm font-semibold text-slate-950">通知动作</h2>
            <p className="mt-1 text-xs leading-5 text-slate-500">
              低风险动作可在线执行，高风险动作打开确认或审计详情。
            </p>
            <div className="mt-3 flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => notificationActionMutation.mutate('L1LowRiskAction')}
                disabled={notificationActionMutation.isPending}
                className="pim-button-secondary px-3 py-1.5 text-xs disabled:cursor-not-allowed disabled:opacity-60"
              >
                执行低风险
              </button>
              <button
                type="button"
                onClick={() => notificationActionMutation.mutate('L3ExternalSourceOrWriteback')}
                disabled={notificationActionMutation.isPending}
                className="pim-button-secondary px-3 py-1.5 text-xs disabled:cursor-not-allowed disabled:opacity-60"
              >
                打开高风险详情
              </button>
            </div>
            {notificationActionMutation.data && (
              <div className="mt-3 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600">
                <p>结果：{notificationActionMutation.data.result}</p>
                {notificationActionMutation.data.detailUrl && (
                  <p className="mt-1 break-words">详情：{notificationActionMutation.data.detailUrl}</p>
                )}
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}
