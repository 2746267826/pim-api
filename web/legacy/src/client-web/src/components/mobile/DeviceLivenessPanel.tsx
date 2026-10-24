import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  getMobileLivenessEvents,
  type MobileLivenessDeviceBlock,
  type MobileLivenessEvent,
  type MobileLivenessOverview,
} from '../../api/mobile';
import {
  FULFILLMENT_DEFINITION,
  formatCoverage,
  formatFulfillment,
  formatPayload,
  formatUtc,
  groupDeviceBlocks,
  silenceSeverityClass,
} from './deviceLivenessModel';

function EventRow({ event }: { event: MobileLivenessEvent }) {
  const [showPayload, setShowPayload] = useState(false);
  return (
    <div className="border-t border-slate-100 py-3 text-sm">
      <div className="grid gap-2 md:grid-cols-[170px_120px_minmax(0,1fr)_minmax(0,1fr)_auto]">
        <span className="text-slate-600">{formatUtc(event.occurredAtUtc)}</span>
        <span className="font-medium text-slate-900">{event.eventTypeLabel}</span>
        <span className="text-slate-700">{event.reasonLabel ?? event.reason ?? '—'}</span>
        <span className="text-slate-600">{event.description ?? '—'}</span>
        <button type="button" className="text-left text-blue-700 hover:underline" onClick={() => setShowPayload(value => !value)}>
          {showPayload ? '收起原始 JSON' : '查看原始 JSON'}
        </button>
      </div>
      {showPayload && <pre className="mt-2 max-h-56 overflow-auto rounded-md bg-slate-950 p-3 text-xs text-slate-100">{formatPayload(event.payloadJson)}</pre>}
    </div>
  );
}

function DeviceEvents({ device, rangeStartUtc, rangeEndUtc }: { device: MobileLivenessDeviceBlock; rangeStartUtc: string; rangeEndUtc: string }) {
  const query = useQuery({
    queryKey: ['mobile-liveness-events', device.deviceId, rangeStartUtc, rangeEndUtc],
    queryFn: () => getMobileLivenessEvents(device.deviceId, { rangeStartUtc, rangeEndUtc, page: 1, pageSize: 50 }),
  });
  if (query.isLoading) return <div className="mt-3 h-16 animate-pulse rounded-md bg-slate-100" />;
  if (query.error) return <div className="mt-3 rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700"><p>事件加载失败，请重试。</p><button type="button" onClick={() => void query.refetch()} className="mt-2 rounded-md border border-red-300 px-2.5 py-1 text-xs font-medium text-red-800 hover:bg-red-100">重试事件</button></div>;
  if (!query.data?.items.length) return <p className="mt-3 text-sm text-slate-500">该范围内暂无事件。</p>;
  return <div className="mt-3">{query.data.items.map(event => <EventRow key={event.id} event={event} />)}</div>;
}

function DeviceBlock({ device, rangeStartUtc, rangeEndUtc }: { device: MobileLivenessDeviceBlock; rangeStartUtc: string; rangeEndUtc: string }) {
  const [expanded, setExpanded] = useState(false);
  const silenceClass = silenceSeverityClass(device.longestSilenceSeverity);
  return (
    <article className="pim-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 className="font-semibold text-slate-950">{device.displayName || device.deviceId}</h3>
          <p className="mt-1 text-xs text-slate-500">{device.deviceKindLabel} · {device.deviceId}</p>
        </div>
        <button type="button" className="rounded-md border border-slate-200 px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-50" onClick={() => setExpanded(value => !value)}>
          {expanded ? '收起事件' : '展开事件'}
        </button>
      </div>
      <p className="mt-3 text-sm font-medium text-slate-800">{device.hasData ? device.conclusion : '无数据/未上报'}</p>
      <div className="mt-4 grid gap-3 md:grid-cols-2">
        <div className="rounded-md bg-slate-50 p-3">
          <p className="text-xs font-semibold text-slate-500">小时覆盖</p>
          <p className="mt-1 text-lg font-semibold text-slate-950">{formatCoverage(device.hasData ? device.coverageByHour : null)}（{device.observedHours}/{device.totalHours} 小时）</p>
          <p className="mt-1 text-xs text-slate-500">{device.coverageByHourDefinition}</p>
        </div>
        <div className="rounded-md bg-slate-50 p-3">
          <p className="text-xs font-semibold text-slate-500">预期心跳覆盖</p>
          <p className="mt-1 text-lg font-semibold text-slate-950">{formatCoverage(device.hasData ? device.coverageByExpectedHeartbeat : null)}（{device.observedHeartbeats}/{device.expectedHeartbeats} 次）</p>
          <p className="mt-1 text-xs text-slate-500">{device.coverageByExpectedHeartbeatDefinition}</p>
        </div>
        <div className="rounded-md bg-slate-50 p-3 md:col-span-2" data-testid="device-liveness-fulfillment">
          <p className="text-xs font-semibold text-slate-500">叫醒兑现率</p>
          <p className="mt-1 text-lg font-semibold text-slate-950">{formatFulfillment(device.fulfillment)}</p>
          <p className="mt-1 text-xs text-slate-500">{FULFILLMENT_DEFINITION}</p>
        </div>
      </div>
      {device.longestSilenceSeverity !== 'none' ? (
        <div className={`mt-3 inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${silenceClass}`} data-severity={device.longestSilenceSeverity}>
          最长静默 {device.longestSilenceMinutes} 分钟：{formatUtc(device.longestSilenceStartUtc)} 至 {formatUtc(device.longestSilenceEndUtc)}
        </div>
      ) : (
        <p className="mt-3 text-sm text-slate-500">最长静默：无</p>
      )}
      <div className="mt-4 grid gap-4 md:grid-cols-2">
        <div>
          <h4 className="text-xs font-semibold text-slate-500">静默时间线</h4>
          {device.silences.length ? <ul className="mt-2 space-y-2">{device.silences.map(silence => {
            const className = silenceSeverityClass(silence.severity);
            return <li key={`${silence.startUtc}-${silence.endUtc}`} className={`text-sm ${className ? `rounded px-2 py-1 ${className}` : 'text-slate-500'}`}>
              {formatUtc(silence.startUtc)} 至 {formatUtc(silence.endUtc)} · {silence.minutes} 分钟{silence.severity !== 'none' ? ` · ${silence.severityLabel}` : ''}
            </li>;
          })}</ul> : <p className="mt-2 text-sm text-slate-500">暂无静默</p>}
        </div>
        <div>
          <h4 className="text-xs font-semibold text-slate-500">原因分布</h4>
          {device.causes.length ? <ul className="mt-2 space-y-2">{device.causes.map(cause => <li key={cause.cause} className="text-sm text-slate-700"><span className="font-medium">{cause.label}：{cause.count}</span>{cause.inference && <span className="mt-1 block text-xs text-slate-500">{cause.inference}</span>}</li>)}</ul> : <p className="mt-2 text-sm text-slate-500">暂无原因记录</p>}
        </div>
      </div>
      {expanded && <DeviceEvents device={device} rangeStartUtc={rangeStartUtc} rangeEndUtc={rangeEndUtc} />}
    </article>
  );
}

export function DeviceLivenessGroups({ overview }: { overview: MobileLivenessOverview }) {
  return <div className="space-y-6">{groupDeviceBlocks(overview).map(group => <section key={group.title}>
    <div className="mb-3 flex items-center justify-between"><h2 className="text-lg font-semibold text-slate-950">{group.title}</h2><span className="text-sm text-slate-500">{group.devices.length} 台</span></div>
    {group.devices.length ? <div className="space-y-3">{group.devices.map(device => <DeviceBlock key={device.deviceId} device={device} rangeStartUtc={overview.rangeStartUtc} rangeEndUtc={overview.rangeEndUtc} />)}</div> : <div className="rounded-md border border-dashed border-slate-200 bg-white p-4 text-sm text-slate-500">暂无{group.title}数据</div>}
  </section>)}</div>;
}

export default function DeviceLivenessPanel({ overview, isLoading, error, onRetry }: { overview?: MobileLivenessOverview; isLoading: boolean; error: unknown; onRetry: () => void }) {
  if (isLoading) return <div className="space-y-3">{[1, 2, 3].map(item => <div key={item} className="h-36 animate-pulse rounded-lg bg-white shadow-sm" />)}</div>;
  if (error || !overview) return <div className="rounded-lg border border-red-200 bg-red-50 p-5 text-sm text-red-700"><p>设备存活加载失败，请稍后重试。</p><button type="button" onClick={onRetry} className="mt-3 rounded-md bg-red-600 px-3 py-1.5 font-medium text-white hover:bg-red-700">重试</button></div>;
  return <DeviceLivenessGroups overview={overview} />;
}
