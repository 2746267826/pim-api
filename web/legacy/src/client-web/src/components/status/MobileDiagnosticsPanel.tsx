import type { ReactNode } from 'react';
import type { PimHealthStatus } from '../../types';
import StatusBadge from '../../ui/StatusBadge';

type StatusTone = 'primary' | 'warning' | 'danger' | 'neutral';

type MobileQualityComponentLike = {
  key?: string;
  name?: string;
  status?: unknown;
  message?: string;
  checkedAt?: string;
  details?: Record<string, unknown>;
};

type MobileQualityIssueLike = {
  code?: string;
  severity?: unknown;
  componentKey?: string;
  message?: string;
  nextStep?: string | null;
};

export type MobileQualityDiagnosticsData = {
  overallStatus?: unknown;
  label?: string;
  message?: string;
  checkedAt?: string;
  components?: MobileQualityComponentLike[];
  issues?: MobileQualityIssueLike[];
  nextSteps?: string[];
};

const statusNames = new Set<PimHealthStatus>(['Unknown', 'Healthy', 'Warning', 'Critical']);

const statusByNumber: Record<number, PimHealthStatus> = {
  0: 'Unknown',
  1: 'Healthy',
  2: 'Warning',
  3: 'Critical',
};

const toneByStatus: Record<PimHealthStatus, StatusTone> = {
  Unknown: 'neutral',
  Healthy: 'primary',
  Warning: 'warning',
  Critical: 'danger',
};

const statusLabels: Record<PimHealthStatus, string> = {
  Unknown: '未知',
  Healthy: '正常',
  Warning: '有警告',
  Critical: '严重',
};

const detailLabels: Record<string, string> = {
  androidIdHash: 'Android ID',
  appMetadataCount: '应用元数据包数',
  appVersion: '应用版本',
  acceptedCount: '已接受条目',
  batchCount: '批次数',
  deviceId: '设备',
  displayName: '设备名称',
  failedBatchCount: '失败批次',
  fallbackSummaryCount: '汇总数据数',
  lastError: '最近错误',
  lastSuccessfulUploadAt: '最近成功上传',
  lastSyncAt: '最近同步',
  evaluationEndAt: '评估截止',
  latestSummaryAt: '最近汇总',
  locationPointCount: '定位点数',
  metadataFreshness: '元数据新鲜度',
  missingAppMetadataCount: '缺元数据包数',
  missingPackages: '待补包名',
  rejectedCount: '已拒绝条目',
  receivedAt: '最近心跳',
  staleAppCount: '过期应用数',
  stalledBatchCount: '未完成批次',
  summaryLagHours: '汇总滞后（小时）',
  uploadQueueCount: '上传队列',
  usedPackageCount: '使用包数',
};

const diagnostics = [
  {
    label: 'Android heartbeat',
    keys: ['android-heartbeat', 'mobile-heartbeat'],
    fallbackMessage: '暂无 Android heartbeat 数据。',
  },
  {
    label: '移动使用采集',
    keys: ['mobile-usage-coverage', 'mobile-usage', 'usage-collection', 'event-coverage', 'fallback-only-days'],
    fallbackMessage: '暂无移动使用采集诊断。',
  },
  {
    label: '移动同步批次',
    keys: ['mobile-sync', 'mobile-sync-batches', 'sync-batches', 'sync-batch-failures'],
    fallbackMessage: '暂无移动同步批次诊断。',
  },
  {
    label: '移动定位',
    keys: ['mobile-location', 'location-capture', 'location-accuracy-rejections'],
    fallbackMessage: '暂无移动定位诊断。',
  },
  {
    label: '移动应用元数据诊断',
    keys: ['mobile-app-metadata', 'app-metadata', 'app-metadata-completeness'],
    fallbackMessage: '暂无移动应用元数据诊断。',
  },
] as const;

function normalizeStatus(value: unknown): PimHealthStatus {
  if (typeof value === 'number') return statusByNumber[value] ?? 'Unknown';
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (/^\d+$/.test(trimmed)) return statusByNumber[Number(trimmed)] ?? 'Unknown';
    if (statusNames.has(trimmed as PimHealthStatus)) return trimmed as PimHealthStatus;
  }
  return 'Unknown';
}

function formatCheckedAt(value?: string) {
  if (!value) return '未知';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function errorMessage(error: unknown) {
  if (error instanceof Error && error.message) return error.message;
  return '移动诊断暂不可用，请稍后刷新重试。';
}

function formatDetailKey(key: string) {
  return detailLabels[key] ?? key;
}

function formatDetailValue(value: unknown) {
  if (value === null || value === undefined || value === '') return '无';
  if (typeof value === 'boolean') return value ? '是' : '否';
  return String(value);
}

function findComponent(
  components: MobileQualityComponentLike[],
  keys: readonly string[],
) {
  return components.find(component => {
    const key = component.key?.trim().toLowerCase();
    return key ? keys.includes(key) : false;
  });
}

function PanelShell({ children }: { children: ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      {children}
    </section>
  );
}

function DiagnosticCard({
  label,
  component,
  fallbackMessage,
}: {
  label: string;
  component?: MobileQualityComponentLike;
  fallbackMessage: string;
}) {
  const status = normalizeStatus(component?.status);
  const detailEntries = Object.entries(component?.details ?? {}).slice(0, 6);

  return (
    <article className="min-w-0 rounded-lg border border-slate-200 bg-slate-50 p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-slate-950">{component?.name || label}</h3>
          <p className="mt-1 text-xs text-slate-500">检查时间：{formatCheckedAt(component?.checkedAt)}</p>
        </div>
        <StatusBadge tone={toneByStatus[status]}>{statusLabels[status]}</StatusBadge>
      </div>
      <p className="mt-3 text-sm leading-5 text-slate-600">
        {component?.message || fallbackMessage}
      </p>

      {detailEntries.length > 0 && (
        <dl className="mt-3 grid grid-cols-1 gap-2 border-t border-slate-200/70 pt-3 sm:grid-cols-2">
          {detailEntries.map(([key, value]) => (
            <div key={key} className="min-w-0">
              <dt className="truncate text-[11px] text-slate-400">{formatDetailKey(key)}</dt>
              <dd className="mt-0.5 break-words text-xs font-medium text-slate-700">{formatDetailValue(value)}</dd>
            </div>
          ))}
        </dl>
      )}
    </article>
  );
}

export default function MobileDiagnosticsPanel({
  quality,
  isLoading = false,
  error,
}: {
  quality: MobileQualityDiagnosticsData | undefined;
  isLoading?: boolean;
  error?: unknown;
}) {
  if (isLoading) {
    return (
      <PanelShell>
        <div className="flex items-center justify-between gap-3">
          <h2 className="text-sm font-semibold text-slate-950">移动诊断</h2>
          <StatusBadge tone="neutral">检查中</StatusBadge>
        </div>
        <p className="mt-2 text-sm text-slate-500">正在检查移动端采集和同步状态...</p>
      </PanelShell>
    );
  }

  if (error) {
    return (
      <section className="rounded-lg border border-red-200 bg-white p-4">
        <div className="flex items-center justify-between gap-3">
          <h2 className="text-sm font-semibold text-slate-950">移动诊断</h2>
          <StatusBadge tone="danger">不可用</StatusBadge>
        </div>
        <p className="mt-2 text-sm text-red-600">{errorMessage(error)}</p>
      </section>
    );
  }

  const components = Array.isArray(quality?.components) ? quality.components : [];
  const status = normalizeStatus(quality?.overallStatus);
  const issues = Array.isArray(quality?.issues) ? quality.issues : [];
  const nextSteps = Array.isArray(quality?.nextSteps) ? quality.nextSteps : [];

  return (
    <PanelShell>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">移动诊断</h2>
          <p className="mt-1 text-sm text-slate-600">
            {quality?.message || '查看 Android heartbeat、移动使用采集、同步批次、定位和应用元数据状态。'}
          </p>
        </div>
        <StatusBadge tone={toneByStatus[status]}>{quality?.label || statusLabels[status]}</StatusBadge>
      </div>

      <dl className="mt-4 grid grid-cols-3 gap-3 border-t border-slate-100 pt-3 text-xs">
        <div className="min-w-0">
          <dt className="text-slate-400">检查时间</dt>
          <dd className="mt-1 truncate font-medium text-slate-700">{formatCheckedAt(quality?.checkedAt)}</dd>
        </div>
        <div className="min-w-0">
          <dt className="text-slate-400">问题数</dt>
          <dd className="mt-1 font-medium text-slate-700">{issues.length}</dd>
        </div>
        <div className="min-w-0">
          <dt className="text-slate-400">诊断项</dt>
          <dd className="mt-1 font-medium text-slate-700">{diagnostics.length}</dd>
        </div>
      </dl>

      <div className="mt-4 grid grid-cols-1 gap-3 lg:grid-cols-2">
        {diagnostics.map(item => (
          <DiagnosticCard
            key={item.label}
            label={item.label}
            component={findComponent(components, item.keys)}
            fallbackMessage={item.fallbackMessage}
          />
        ))}
      </div>

      {nextSteps.length > 0 && (
        <div className="mt-4 rounded-lg bg-amber-50 px-3 py-2">
          <h3 className="text-xs font-semibold text-amber-800">移动端下一步</h3>
          <ul className="mt-2 space-y-1">
            {nextSteps.slice(0, 3).map((step, index) => (
              <li key={`${step}-${index}`} className="text-sm text-amber-800">
                {step}
              </li>
            ))}
          </ul>
        </div>
      )}
    </PanelShell>
  );
}
