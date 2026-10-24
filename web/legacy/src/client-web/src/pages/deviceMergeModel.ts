import type { DeviceListItem } from '../api/mobile';

export interface MergePreviewItem {
  deviceId: string;
  dataCount: number;
}

export interface MergePreview {
  items: MergePreviewItem[];
  total: number;
}

/** 合并弹窗里一台候选设备的可读信息（issue #232：同名设备必须能区分）。 */
export interface MergeCandidate {
  deviceId: string;
  displayName: string;
  /** 设备 ID 短码，用于区分同名设备（展示为 …xxxxxx）。 */
  shortId: string;
  lastSeenLabel: string;
  isOnline: boolean;
  dataCount: number;
}

export interface MergePreviewRow extends MergeCandidate {
  /** 是否为被保留的目标设备。 */
  isTarget: boolean;
  /** keep = 保留；merge-and-remove = 数据并入目标后被移除。 */
  action: 'keep' | 'merge-and-remove';
}

/** 设备 ID 取尾部短码：OPPO 一类设备 displayName 完全相同，短码是唯一可区分的信息。 */
export function shortDeviceId(deviceId: string, keep = 6): string {
  const trimmed = deviceId.trim();
  if (trimmed.length <= keep) return trimmed;
  return trimmed.slice(-keep);
}

/** 与后端合并预览一致的口径：sessions + events + locations + summaries。 */
export function deviceDataCount(device: DeviceListItem): number {
  return device.sessionCount + device.eventCount + device.locationCount + device.summaryCount;
}

function pad(value: number): string {
  return value < 10 ? `0${value}` : String(value);
}

/** 相对时间文案；now 由调用方注入，便于测试与稳定渲染。 */
export function formatLastSeen(lastSeenAtUtc: string | null | undefined, now: Date): string {
  if (!lastSeenAtUtc) return '未知';
  const lastSeen = new Date(lastSeenAtUtc);
  if (Number.isNaN(lastSeen.getTime())) return '未知';

  const diffMs = now.getTime() - lastSeen.getTime();
  if (diffMs <= 0) return '刚刚';

  const minutes = Math.floor(diffMs / 60_000);
  if (minutes < 1) return '刚刚';
  if (minutes < 60) return `${minutes} 分钟前`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} 小时前`;

  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} 天前`;

  return `${lastSeen.getFullYear()}-${pad(lastSeen.getMonth() + 1)}-${pad(lastSeen.getDate())}`;
}

export function describeDevice(device: DeviceListItem, now: Date): MergeCandidate {
  return {
    deviceId: device.deviceId,
    displayName: device.displayName || device.deviceId,
    shortId: shortDeviceId(device.deviceId),
    lastSeenLabel: formatLastSeen(device.lastSeenAtUtc, now),
    isOnline: device.isOnline,
    dataCount: deviceDataCount(device),
  };
}

/**
 * 候选设备排序：在线优先，其次最后活跃时间倒序。
 * 排序结果既用于列表展示，也决定默认保留哪台。
 */
export function orderMergeCandidates(devices: DeviceListItem[]): DeviceListItem[] {
  return [...devices].sort((left, right) => {
    if (left.isOnline !== right.isOnline) return left.isOnline ? -1 : 1;
    const leftSeen = new Date(left.lastSeenAtUtc ?? 0).getTime();
    const rightSeen = new Date(right.lastSeenAtUtc ?? 0).getTime();
    const safeLeft = Number.isNaN(leftSeen) ? 0 : leftSeen;
    const safeRight = Number.isNaN(rightSeen) ? 0 : rightSeen;
    if (safeLeft !== safeRight) return safeRight - safeLeft;
    // 时间相同时按设备 ID 稳定排序，避免顺序随列表返回顺序抖动。
    return left.deviceId.localeCompare(right.deviceId);
  });
}

/**
 * 默认保留最近活跃的那台（issue #232）。用户手动改选后不再覆盖，
 * 因此只在进入弹窗或勾选集合首次确定时调用。
 */
export function pickDefaultMergeTarget(devices: DeviceListItem[]): string {
  const ordered = orderMergeCandidates(devices);
  return ordered.length > 0 ? ordered[0].deviceId : '';
}

/**
 * 候选设备（保持排序）：记录数取设备列表里的统计值，
 * 弹窗选项用这一份，避免预览请求返回后数字跳动。
 */
export function buildMergeCandidates(devices: DeviceListItem[], now: Date): MergeCandidate[] {
  return orderMergeCandidates(devices).map(device => describeDevice(device, now));
}

/**
 * 预览行：按设备列出各自将被并入的记录数，并标明「哪台被保留、哪些会被移除」。
 * 记录数优先用后端预览返回的值（合并口径的权威来源），缺失时回退到设备列表统计值。
 * 目标设备的记录数来自自身，不计入「将并入」的合计。
 */
export function buildMergePreviewRows(
  devices: DeviceListItem[],
  targetDeviceId: string,
  preview: MergePreview | null,
  now: Date,
): MergePreviewRow[] {
  const remoteCounts = new Map((preview?.items ?? []).map(item => [item.deviceId, item.dataCount]));

  return buildMergeCandidates(devices, now).map(candidate => {
    const remote = remoteCounts.get(candidate.deviceId);
    return {
      ...candidate,
      dataCount: remote ?? candidate.dataCount,
      isTarget: candidate.deviceId === targetDeviceId,
      action: candidate.deviceId === targetDeviceId ? 'keep' : 'merge-and-remove',
    };
  });
}

/** 真正会被并入目标设备的记录数（源设备之和，不含目标设备自身）。 */
export function mergeIncomingTotal(rows: MergePreviewRow[]): number {
  return rows.filter(row => !row.isTarget).reduce((sum, row) => sum + row.dataCount, 0);
}

/** 下拉/选项里的一行文案：设备名 + 短码 + 最后活跃 + 记录数 + 活跃标记。 */
export function deviceOptionLabel(candidate: MergeCandidate): string {
  const parts = [
    candidate.displayName,
    `ID …${candidate.shortId}`,
    `最后活跃 ${candidate.lastSeenLabel}`,
    `${candidate.dataCount} 条记录`,
  ];
  if (candidate.isOnline) parts.push('当前活跃');
  return parts.join(' · ');
}
