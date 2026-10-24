import { apiGet } from './client';
import type { ApiResponse, SystemStatusDetail, SystemStatusSummary } from '../types';
import type { PimHealthStatus, StatusComponent } from '../types';

export const statusApiPaths = {
  summary: '/status/summary',
  detail: '/status/',
} as const;

const statusByNumber: Record<number, PimHealthStatus> = {
  0: 'Unknown',
  1: 'Healthy',
  2: 'Warning',
  3: 'Critical',
};

const statusNames = new Set<PimHealthStatus>(['Unknown', 'Healthy', 'Warning', 'Critical']);

const healthStatusLabels: Record<PimHealthStatus, string> = {
  Unknown: '未知',
  Healthy: '正常',
  Warning: '有警告',
  Critical: '故障',
};

const componentKindLabels: Record<string, string> = {
  Api: 'API',
  API: 'API',
  Database: '数据库',
  Db: '数据库',
  Daemon: 'daemon',
  Collector: '采集源',
  Source: '采集源',
  BackgroundTask: '后台任务',
  BackgroundTasks: '后台任务',
  Job: '后台任务',
  Queue: '后台任务',
};

type RawStatusSummary = Omit<SystemStatusSummary, 'status'> & { status: unknown };
type RawStatusComponent = Omit<StatusComponent, 'status' | 'kind' | 'message' | 'checkedAt' | 'details'> & {
  status: unknown;
  kind: unknown;
  message: unknown;
  checkedAt: unknown;
  details: unknown;
};
type RawStatusDetail = {
  summary?: unknown;
  components?: unknown;
  nextSteps?: unknown;
};

function normalizeHealthStatus(value: unknown): PimHealthStatus {
  if (typeof value === 'number') return statusByNumber[value] ?? 'Unknown';
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (/^\d+$/.test(trimmed)) return statusByNumber[Number(trimmed)] ?? 'Unknown';
    if (statusNames.has(trimmed as PimHealthStatus)) return trimmed as PimHealthStatus;
  }
  return 'Unknown';
}

function textOrEmpty(value: unknown): string {
  if (value === null || value === undefined) return '';
  return String(value);
}

function normalizeLabel(value: unknown, status: PimHealthStatus): string {
  const label = textOrEmpty(value).trim();
  return statusNames.has(label as PimHealthStatus) ? getHealthStatusLabel(status) : label;
}

function normalizeKind(value: unknown): string {
  const kind = textOrEmpty(value).trim();
  return /^\d+$/.test(kind) ? '' : kind;
}

function normalizeDetails(details: unknown): Record<string, string> {
  if (!details || typeof details !== 'object' || Array.isArray(details)) return {};

  return Object.fromEntries(
    Object.entries(details).map(([key, value]) => [key, textOrEmpty(value)])
  );
}

export function getHealthStatusLabel(status: PimHealthStatus) {
  return healthStatusLabels[status] ?? healthStatusLabels.Unknown;
}

export function getComponentKindLabel(kind: string) {
  if (!kind) return '';
  return componentKindLabels[kind] ?? kind;
}

export function normalizeStatusSummary(raw: unknown): SystemStatusSummary {
  const summary = (raw && typeof raw === 'object' ? raw : {}) as Partial<RawStatusSummary>;
  const status = normalizeHealthStatus(summary.status);

  return {
    status,
    label: normalizeLabel(summary.label, status) || getHealthStatusLabel(status),
    message: textOrEmpty(summary.message),
    checkedAt: textOrEmpty(summary.checkedAt),
  };
}

function normalizeStatusComponent(raw: unknown): StatusComponent {
  const component = (raw && typeof raw === 'object' ? raw : {}) as Partial<RawStatusComponent>;

  return {
    key: textOrEmpty(component.key),
    name: textOrEmpty(component.name),
    kind: normalizeKind(component.kind),
    status: normalizeHealthStatus(component.status),
    message: textOrEmpty(component.message),
    checkedAt: textOrEmpty(component.checkedAt),
    details: normalizeDetails(component.details),
  };
}

export function normalizeStatusDetail(raw: unknown): SystemStatusDetail {
  const detail = (raw && typeof raw === 'object' ? raw : {}) as RawStatusDetail;

  return {
    summary: normalizeStatusSummary(detail.summary),
    components: Array.isArray(detail.components)
      ? detail.components.map(normalizeStatusComponent)
      : [],
    nextSteps: Array.isArray(detail.nextSteps)
      ? detail.nextSteps.map(textOrEmpty).filter(Boolean)
      : [],
  };
}

export async function getStatusSummary() {
  const response = await apiGet<ApiResponse<unknown>>(statusApiPaths.summary);
  return normalizeStatusSummary(response.data);
}

export async function getStatusDetail() {
  const response = await apiGet<ApiResponse<unknown>>(statusApiPaths.detail);
  return normalizeStatusDetail(response.data);
}

export interface DaemonHeartbeat {
  deviceId: string;
  daemonKind: string;
  version: string;
  serverUrl: string;
  lastSuccessfulUploadAt?: string | null;
  lastAttemptedUploadAt?: string | null;
  lastError?: string | null;
  uploadQueueCount?: number | null;
  activityWatchState: string;
  keyStatsState: string;
  collectionPaused: boolean;
  statusJson: string;
  receivedAt: string;
  plannedOfflineAt?: string | null;
  offlineReason?: string | null;
}

export async function getDaemonHeartbeats(): Promise<DaemonHeartbeat[]> {
  try {
    const response = await apiGet<ApiResponse<DaemonHeartbeat[]>>('/daemon/heartbeats');
    return response.data ?? [];
  } catch {
    return [];
  }
}
