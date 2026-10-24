import type { DaemonHeartbeat } from '../api/status';
import type { MobileDevice } from '../api/mobile';

/**
 * 「状态信息」页设备列表的纯模型（#278）。
 *
 * 背景：`daemon_heartbeats` 与 `mobile_devices` 是两套数据源，设备合并只处理
 * `mobile_devices`，导致状态页出现三个问题：
 *   1. 已合并掉的旧安卓设备仍以守护卡逐台出现（幽灵卡）；
 *   2. 当前设备既以守护卡又以设备卡出现（重复展示）；
 *   3. 计数直接把守护心跳条数当「PC」（把 6 台安卓守护算成 6 台 PC）。
 *
 * 规则（与「设备管理」页的合并结果对齐）：
 *   - **PC = 非移动端的守护心跳**（`daemonKind !== 'android'`，即 Windows 等真机）；
 *   - **移动端 = 已登记在 `mobile_devices` 中的设备**，每台一张卡；
 *     其守护心跳（若有）并入同一张卡，不再单独成卡；
 *   - 不在 `mobile_devices` 里的安卓守护心跳 = 已合并/未登记的旧设备 → 不展示。
 */

/** 认作「移动端」的守护类型；其余守护类型计入 PC。 */
const MOBILE_DAEMON_KINDS: readonly string[] = ['android'];

export interface MergedMobileDevice {
  device: MobileDevice;
  /** 该设备最新的一条守护心跳（若有）；用于在同一张卡上展示守护侧信息。 */
  daemon?: DaemonHeartbeat;
}

export interface StatusDeviceModel {
  /** PC / 工作站卡片：非移动端守护心跳，每 (deviceId, daemonKind) 一条。 */
  pcDaemons: DaemonHeartbeat[];
  /** 移动设备卡片：已登记设备，每台一条，附带其守护心跳。 */
  mobileDevices: MergedMobileDevice[];
  counts: { pc: number; mobile: number };
}

function isMobileDaemon(heartbeat: DaemonHeartbeat): boolean {
  return MOBILE_DAEMON_KINDS.includes((heartbeat.daemonKind || '').toLowerCase());
}

/** 同一 (deviceId, daemonKind) 只保留 ReceivedAt 最新的一条。 */
function latestPerDeviceKind(heartbeats: readonly DaemonHeartbeat[]): DaemonHeartbeat[] {
  const latest = new Map<string, DaemonHeartbeat>();

  for (const heartbeat of heartbeats) {
    const key = `${heartbeat.deviceId}\u0000${heartbeat.daemonKind}`;
    const current = latest.get(key);
    if (!current || new Date(heartbeat.receivedAt).getTime() > new Date(current.receivedAt).getTime()) {
      latest.set(key, heartbeat);
    }
  }

  return [...latest.values()];
}

/** 每台设备只保留 ReceivedAt 最新的那条守护心跳（跨 daemonKind 取最新）。 */
function latestDaemonByDevice(heartbeats: readonly DaemonHeartbeat[]): Map<string, DaemonHeartbeat> {
  const latest = new Map<string, DaemonHeartbeat>();

  for (const heartbeat of heartbeats) {
    const current = latest.get(heartbeat.deviceId);
    if (!current || new Date(heartbeat.receivedAt).getTime() > new Date(current.receivedAt).getTime()) {
      latest.set(heartbeat.deviceId, heartbeat);
    }
  }

  return latest;
}

/**
 * 由两套数据源构建状态页的设备列表模型。
 *
 * @param heartbeats `GET /api/v1/daemon/heartbeats` 的原始结果
 * @param devices    `GET /api/v1/mobile/devices` 的原始结果（设备合并后的在册设备）
 */
export function buildStatusDeviceModel(
  heartbeats: readonly DaemonHeartbeat[],
  devices: readonly MobileDevice[],
): StatusDeviceModel {
  const dedupedHeartbeats = latestPerDeviceKind(heartbeats);

  // PC：非移动端守护（Windows 工作站等），按心跳时间倒序。
  const pcDaemons = dedupedHeartbeats
    .filter(heartbeat => !isMobileDaemon(heartbeat))
    .sort((a, b) => new Date(b.receivedAt).getTime() - new Date(a.receivedAt).getTime());

  // 移动端：以「已登记设备」为准（设备管理页的合并结果），守护心跳并入同一张卡。
  const mobileDaemonByDevice = latestDaemonByDevice(
    dedupedHeartbeats.filter(isMobileDaemon),
  );

  const mobileDevices: MergedMobileDevice[] = devices.map(device => ({
    device,
    daemon: mobileDaemonByDevice.get(device.deviceId),
  }));

  // 移动端按最后活跃时间倒序（与设备管理页的默认排序一致）。
  mobileDevices.sort((a, b) => {
    const aTime = new Date(a.device.lastSeenAt).getTime();
    const bTime = new Date(b.device.lastSeenAt).getTime();
    return bTime - aTime;
  });

  return {
    pcDaemons,
    mobileDevices,
    counts: { pc: pcDaemons.length, mobile: mobileDevices.length },
  };
}

/** 「N PC · M 移动端」计数文案。 */
export function formatStatusDeviceCount(counts: { pc: number; mobile: number }): string {
  return `${counts.pc} PC · ${counts.mobile} 移动端`;
}
