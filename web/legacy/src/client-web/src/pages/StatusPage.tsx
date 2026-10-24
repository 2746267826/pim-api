import { useMemo } from 'react';
import { useQuery, useQueries } from '@tanstack/react-query';
import { getMobileDevices, getMobileQuality } from '../api/mobile';
import { getPcQuality, getLatestTrackerHealth, type TrackerHealth } from '../api/pcTracker';
import { getComponentKindLabel, getHealthStatusLabel, getStatusDetail, getDaemonHeartbeats } from '../api/status';
import PcQualitySummary from '../components/pc-tracker/PcQualitySummary';
import MobileDiagnosticsPanel, { type MobileQualityDiagnosticsData } from '../components/status/MobileDiagnosticsPanel';
import type { PimHealthStatus, StatusComponent } from '../types';
import type { MobileQuality } from '../api/mobile';
import PageHeader from '../ui/PageHeader';
import StatusBadge from '../ui/StatusBadge';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';
import { buildStatusDeviceModel, formatStatusDeviceCount } from './statusDeviceModel';

const statusStyles: Record<PimHealthStatus, { text: string; bg: string; border: string; dot: string }> = {
  Healthy: {
    text: 'text-emerald-700',
    bg: 'bg-emerald-50',
    border: 'border-emerald-200',
    dot: 'bg-emerald-500',
  },
  Warning: {
    text: 'text-amber-700',
    bg: 'bg-amber-50',
    border: 'border-amber-200',
    dot: 'bg-amber-500',
  },
  Critical: {
    text: 'text-red-700',
    bg: 'bg-red-50',
    border: 'border-red-200',
    dot: 'bg-red-500',
  },
  Unknown: {
    text: 'text-slate-600',
    bg: 'bg-slate-50',
    border: 'border-slate-200',
    dot: 'bg-slate-400',
  },
};

function formatCheckedAt(value?: string) {
  if (!value) return '未知';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function StatusPill({ status, label }: { status: PimHealthStatus; label: string }) {
  const styles = statusStyles[status];
  return (
    <span className={`inline-flex max-w-full items-center gap-2 rounded-full border px-2.5 py-1 text-xs font-semibold ${styles.bg} ${styles.border} ${styles.text}`}>
      <span className={`h-2 w-2 shrink-0 rounded-full ${styles.dot}`} aria-hidden="true" />
      <span className="truncate">{label}</span>
    </span>
  );
}

function ComponentCard({ component }: { component: StatusComponent }) {
  const detailEntries = Object.entries(component.details || {});
  const kindLabel = getComponentKindLabel(component.kind);

  return (
    <section className="min-w-0 rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-sm font-semibold text-slate-950">{component.name}</h2>
          {kindLabel && <p className="mt-1 truncate text-xs text-slate-500">{kindLabel}</p>}
        </div>
        <StatusPill status={component.status} label={getHealthStatusLabel(component.status)} />
      </div>

      <p className="mt-3 text-sm text-slate-600">{component.message || '系统状态暂不可用'}</p>
      <p className="mt-2 text-xs text-slate-400">检查时间：{formatCheckedAt(component.checkedAt)}</p>

      {detailEntries.length > 0 && (
        <dl className="mt-4 grid grid-cols-1 gap-2 border-t border-slate-100 pt-3 sm:grid-cols-2">
          {detailEntries.map(([key, value]) => (
            <div key={key} className="min-w-0">
              <dt className="truncate text-[11px] font-medium uppercase text-slate-400">{key}</dt>
              <dd className="mt-0.5 break-words text-xs text-slate-700">{value}</dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}

function formatAgeSeconds(age: number | null | undefined): string {
  if (age === null || age === undefined) return '—';
  if (age < 60) return `${Math.round(age)} 秒前`;
  if (age < 3600) return `${Math.round(age / 60)} 分钟前`;
  return `${Math.round(age / 3600)} 小时前`;
}

function TrackerHealthCard() {
  const { data, isLoading } = useQuery({
    queryKey: ['status-tracker-health'],
    queryFn: () => getLatestTrackerHealth().catch(() => null),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const health = data as TrackerHealth | null;
  const pill = (ok: boolean | undefined, okLabel: string, badLabel: string) => (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium ${
      ok ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'
    }`}>
      <span aria-hidden="true" className={`h-1.5 w-1.5 rounded-full ${ok ? 'bg-emerald-500' : 'bg-slate-400'}`} />
      {ok ? okLabel : badLabel}
    </span>
  );

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">电脑记录采集</p>
          <p className="mt-1 text-sm text-slate-600">
            {health ? `设备 ${health.deviceId} · 状态 ${health.status}` : '守护进程尚未上报'}
          </p>
        </div>
        {pill(!!health && health.status === 'running', '守护进程运行中', '守护进程离线/异常')}
      </div>
      {isLoading && <p className="mt-3 text-xs text-slate-400">加载中...</p>}
      {health && (
        <dl className="mt-4 grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <div className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2">
            <dt className="text-slate-500">URL 级浏览器插件</dt>
            <dd className="flex items-center gap-2">
              {pill(health.browserConnected, '已连接', '未连接')}
              <span className="text-xs text-slate-400">{formatAgeSeconds(health.browserHeartbeatAgeSeconds)}</span>
            </dd>
          </div>
          <div className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2">
            <dt className="text-slate-500">站点数据通道（Time Tracker）</dt>
            <dd className="flex items-center gap-2">
              {pill(health.siteConnected, '已连接', '未连接')}
              <span className="text-xs text-slate-400">{formatAgeSeconds(health.siteLastEventAgeSeconds)}</span>
            </dd>
          </div>
          <div className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2">
            <dt className="text-slate-500">已上传事件</dt>
            <dd className="text-slate-700">{health.eventsUploaded}</dd>
          </div>
          <div className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2">
            <dt className="text-slate-500">已上传站点数据</dt>
            <dd className="text-slate-700">{health.siteEventsUploaded}{health.siteLastError ? ` · 错误：${health.siteLastError}` : ''}</dd>
          </div>
        </dl>
      )}
      {!isLoading && !health && (
        <p className="mt-3 text-xs text-slate-400">启动 Windows 客户端后，这里会显示 URL 级浏览器插件与站点数据通道的连接状态。</p>
      )}
    </section>
  );
}

export default function StatusPage() {
  const { data, isLoading, isError, refetch: refetchStatus, isFetching: statusFetching } = useQuery({
    queryKey: ['status-detail'],
    queryFn: getStatusDetail,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const {
    data: pcQuality,
    isLoading: pcQualityLoading,
    error: pcQualityError,
    refetch: refetchPcQuality,
    isFetching: pcQualityFetching,
  } = useQuery({
    queryKey: ['status-pc-quality'],
    queryFn: () => getPcQuality(),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const {
    data: mobileQuality,
    isLoading: mobileQualityLoading,
    error: mobileQualityError,
    refetch: refetchMobileQuality,
    isFetching: mobileQualityFetching,
  } = useQuery({
    queryKey: ['status-mobile-quality'],
    queryFn: () => getMobileQuality(),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { data: devices = [] } = useQuery({
    queryKey: ['status-mobile-devices'],
    queryFn: getMobileDevices,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const {
    data: daemons = [],
    refetch: refetchDaemons,
    isFetching: daemonsFetching,
  } = useQuery({
    queryKey: ['status-daemon-heartbeats'],
    queryFn: getDaemonHeartbeats,
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const deviceQualityQueries = useQueries({
    queries: devices.map(device => ({
      queryKey: ['status-device-quality', device.deviceId],
      queryFn: () => getMobileQuality(undefined, device.deviceId),
      staleTime: 30_000,
      refetchInterval: 60_000,
    })),
  });

  const deviceQualityMap = useMemo(() => {
    const map = new Map<string, MobileQuality>();
    deviceQualityQueries.forEach((query, idx) => {
      if (query.data) {
        map.set(devices[idx]?.deviceId, query.data as MobileQuality);
      }
    });
    return map;
  }, [deviceQualityQueries, devices]);

  const isOnline = (lastSeenAt: string): boolean => {
    const elapsed = Date.now() - new Date(lastSeenAt).getTime();
    return elapsed < 15 * 60 * 1000;
  };

  // #278：以「设备管理」的合并结果为准重建列表 —— 已合并的旧设备不再出现、
  // 当前设备不重复展示、计数不把安卓守护算作 PC。
  const deviceModel = useMemo(
    () => buildStatusDeviceModel(daemons, devices),
    [daemons, devices],
  );
  const pcDaemons = deviceModel.pcDaemons;
  const mobileDevices = deviceModel.mobileDevices;

  const summary = data?.summary;
  const summaryStatus = summary?.status ?? 'Unknown';

  return (
    <div className="mx-auto w-full max-w-[1200px] space-y-4 pb-20 md:pb-4">
      <PageHeader
        title="状态信息"
        subtitle="查看 API、数据库、daemon、采集源和后台任务状态。"
        actions={
          <button
            type="button"
            onClick={() => {
              void refetchStatus();
              void refetchPcQuality();
              void refetchMobileQuality();
              void refetchDaemons();
            }}
            disabled={statusFetching || pcQualityFetching || mobileQualityFetching || daemonsFetching}
            className="pim-button-secondary px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            刷新
          </button>
        }
      />

      {isLoading && (
        <section className="rounded-lg border border-slate-200 bg-white p-6 text-sm text-slate-500">
          正在检查系统状态...
        </section>
      )}

      {isError && (
        <section className="rounded-lg border border-red-200 bg-red-50 p-6">
          <p className="text-sm font-semibold text-red-700">系统状态暂不可用</p>
          <p className="mt-1 text-sm text-red-600">请稍后刷新重试。</p>
        </section>
      )}

      {!isLoading && !isError && summary && (
        <>
          <section className="rounded-lg border border-slate-200 bg-white p-5">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div className="min-w-0">
                <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">总体状态</p>
                <h2 className="mt-2 text-2xl font-semibold text-slate-950">{summary.label || '未知'}</h2>
                <p className="mt-2 text-sm text-slate-600">{summary.message || '系统状态暂不可用'}</p>
              </div>
              <StatusPill status={summaryStatus} label={getHealthStatusLabel(summaryStatus)} />
            </div>
            <p className="mt-4 text-xs text-slate-400">检查时间：{formatCheckedAt(summary.checkedAt)}</p>
          </section>

          <PcQualitySummary
            quality={pcQuality}
            isLoading={pcQualityLoading}
            error={pcQualityError}
            compact
          />

          <TrackerHealthCard />

          <MobileDiagnosticsPanel
            quality={mobileQuality as MobileQualityDiagnosticsData | undefined}
            isLoading={mobileQualityLoading}
            error={mobileQualityError}
          />

          {(pcDaemons.length > 0 || mobileDevices.length > 0) && (
            <section className="space-y-3">
              <div className="flex items-center justify-between">
                <h2 className="text-sm font-semibold text-slate-800">连接设备与工作站 (Multi-Device Status)</h2>
                <span className="text-xs text-slate-400 font-mono">
                  {formatStatusDeviceCount(deviceModel.counts)}
                </span>
              </div>
              <div className="grid grid-cols-1 gap-4 lg:grid-cols-2 xl:grid-cols-3">
                {/* Windows Workstations */}
                {pcDaemons.map((daemon) => {
                  const isPlanned = Boolean(daemon.plannedOfflineAt);
                  const isRecent = isOnline(daemon.receivedAt);
                  return (
                    <section key={`${daemon.daemonKind}-${daemon.deviceId}`} className="min-w-0 rounded-lg border border-slate-200 bg-white p-4">
                      <div className="flex items-start justify-between gap-2">
                        <div className="min-w-0">
                          <h3 className="truncate text-sm font-semibold text-slate-900">{daemon.deviceId}</h3>
                          <p className="mt-0.5 truncate text-xs text-slate-400 font-mono">
                            {daemon.daemonKind === 'windows' ? 'Windows 守护程序' : daemon.daemonKind}
                          </p>
                        </div>
                        {isPlanned ? (
                          <StatusBadge tone="neutral">计划离线</StatusBadge>
                        ) : isRecent ? (
                          <StatusBadge tone="activity">在线</StatusBadge>
                        ) : (
                          <StatusBadge tone="warning">离线</StatusBadge>
                        )}
                      </div>
                      <div className="mt-3 space-y-1 text-xs text-slate-600 font-mono">
                        <p className="truncate">程序版本: <span className="text-slate-800 font-sans">{daemon.version || '未知'}</span></p>
                        <p>最后心跳: <span className={isRecent ? 'text-emerald-600 font-medium font-sans' : 'text-slate-500 font-sans'}>{new Date(daemon.receivedAt).toLocaleString('zh-CN')}</span></p>
                        <p className="truncate">Native 追踪: <span className="text-slate-800">{daemon.activityWatchState} / {daemon.keyStatsState}</span></p>
                        <p>待上传队列: <span className="text-slate-800">{daemon.uploadQueueCount ?? 0} 条</span></p>
                        {daemon.plannedOfflineAt && (
                          <p className="text-slate-500 font-sans">离线原因: {daemon.offlineReason || '正常关机/休眠'}</p>
                        )}
                      </div>
                    </section>
                  );
                })}

                {/* Mobile Devices */}
                {mobileDevices.map(({ device, daemon }) => {
                  const q = deviceQualityMap.get(device.deviceId) as MobileQuality | undefined;
                  const online = isOnline(daemon?.receivedAt ?? device.lastSeenAt);
                  return (
                    <section key={device.deviceId} className="min-w-0 rounded-lg border border-slate-200 bg-white p-4">
                      <div className="flex items-start justify-between gap-2">
                        <div className="min-w-0">
                          <h3 className="truncate text-sm font-semibold text-slate-900">{device.displayName}</h3>
                          <p className="mt-0.5 truncate text-xs text-slate-400 font-mono">{device.deviceId}</p>
                        </div>
                        {online ? (
                          <StatusBadge tone="activity">在线</StatusBadge>
                        ) : (
                          <StatusBadge tone="neutral">离线</StatusBadge>
                        )}
                      </div>
                      <div className="mt-3 space-y-1 text-xs text-slate-600">
                        <p>{device.brand ? `${device.brand} ` : ''}{device.model || '移动设备'}</p>
                        <p>{device.androidVersion ? `Android ${device.androidVersion}` : '移动端'} · App {device.appVersion || '未知'}</p>
                        <p>最后活跃：{new Date(device.lastSeenAt).toLocaleString('zh-CN')}</p>
                        {/* #278：守护心跳并入本卡（原先它自己单独成卡，导致同一设备出现两次） */}
                        {daemon && (
                          <div className="mt-2 space-y-1 border-t border-slate-100 pt-2 font-mono text-slate-600">
                            <p className="truncate">守护版本: <span className="font-sans text-slate-800">{daemon.version || '未知'}</span></p>
                            <p>守护心跳: <span className={online ? 'font-sans font-medium text-emerald-600' : 'font-sans text-slate-500'}>{new Date(daemon.receivedAt).toLocaleString('zh-CN')}</span></p>
                            <p className="truncate">Native 追踪: <span className="text-slate-800">{daemon.activityWatchState} / {daemon.keyStatsState}</span></p>
                            <p>待上传队列: <span className="text-slate-800">{daemon.uploadQueueCount ?? 0} 条</span></p>
                          </div>
                        )}
                        {q && (
                          <p className="mt-1">
                            数据质量：<StatusBadge tone={q.overallStatus === 'Healthy' ? 'activity' : q.overallStatus === 'Warning' ? 'warning' : 'danger'}>{q.label}</StatusBadge>
                          </p>
                        )}
                      </div>
                    </section>
                  );
                })}
              </div>
            </section>
          )}

          {data.nextSteps.length > 0 && (
            <section className="rounded-lg border border-amber-200 bg-amber-50 p-4">
              <h2 className="text-sm font-semibold text-amber-800">需要关注</h2>
              <ul className="mt-2 space-y-1">
                {data.nextSteps.map((step, index) => (
                  <li key={`${step}-${index}`} className="text-sm text-amber-800">
                    {step}
                  </li>
                ))}
              </ul>
            </section>
          )}

          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
            {data.components.map(component => (
              <ComponentCard key={component.key} component={component} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
