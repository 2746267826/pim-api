import assert from 'node:assert/strict';
import type { DaemonHeartbeat } from '../../src/client-web/src/api/status';
import type { MobileDevice } from '../../src/client-web/src/api/mobile';
import {
  buildStatusDeviceModel,
  formatStatusDeviceCount,
} from '../../src/client-web/src/pages/statusDeviceModel';

/**
 * #278 的数据用的是 issue 里的生产快照形状：
 * - daemon_heartbeats: 1 台 windows（DESKTOP-ARJ75IN）+ 6 台 android
 *   （其中 5 台最后心跳停在 2026-07-07 ~ 07-11，属已合并掉的旧设备）；
 * - mobile_devices: 仅 1 行 android-a5b98c2e27c8c280（合并轮已把其余并入该行）。
 */
function heartbeat(overrides: Partial<DaemonHeartbeat> & { deviceId: string; daemonKind: string }): DaemonHeartbeat {
  return {
    version: '2026.09.500',
    serverUrl: 'http://127.0.0.1:5858',
    lastSuccessfulUploadAt: null,
    lastAttemptedUploadAt: null,
    lastError: null,
    uploadQueueCount: 0,
    activityWatchState: 'Available',
    keyStatsState: 'Available',
    collectionPaused: false,
    statusJson: '{}',
    receivedAt: '2026-08-23T09:26:22Z',
    plannedOfflineAt: null,
    offlineReason: null,
    ...overrides,
  };
}

function mobileDevice(overrides: Partial<MobileDevice> & { deviceId: string }): MobileDevice {
  return {
    id: '00000000-0000-0000-0000-000000000001',
    androidIdHash: null,
    displayName: 'OPPO PLG110',
    manufacturer: 'OPPO',
    brand: 'OPPO',
    model: 'PLG110',
    androidVersion: '15',
    sdkInt: 35,
    appVersion: '2026.09.504',
    metadataJson: '{}',
    firstSeenAt: '2026-07-07T10:42:16Z',
    lastSeenAt: '2026-08-23T10:03:07Z',
    lastHeartbeatAt: null,
    lastSyncAt: null,
    isActive: true,
    ...overrides,
  };
}

const windowsDaemon = heartbeat({
  deviceId: 'DESKTOP-ARJ75IN',
  daemonKind: 'windows',
  receivedAt: '2026-08-23T09:26:22Z',
});

const currentAndroidDaemon = heartbeat({
  deviceId: 'android-a5b98c2e27c8c280',
  daemonKind: 'android',
  receivedAt: '2026-08-23T10:03:07Z',
});

/** 5 台已合并掉的旧安卓设备（只剩守护心跳，已不在 mobile_devices 中）。 */
const mergedAwayDaemons = [
  heartbeat({ deviceId: 'android-c8afbd44c95ad974', daemonKind: 'android', receivedAt: '2026-07-11T05:19:05Z', uploadQueueCount: 2568 }),
  heartbeat({ deviceId: 'android-86e569f5435a0dd7', daemonKind: 'android', receivedAt: '2026-07-09T03:30:21Z', uploadQueueCount: 0 }),
  heartbeat({ deviceId: 'android-a2e81057cfdff59b', daemonKind: 'android', receivedAt: '2026-07-07T15:46:56Z', uploadQueueCount: 954 }),
  heartbeat({ deviceId: 'android-c59fc81d081e8c62', daemonKind: 'android', receivedAt: '2026-07-07T10:46:57Z', uploadQueueCount: 6549 }),
  heartbeat({ deviceId: 'android-de9183a731eb4712', daemonKind: 'android', receivedAt: '2026-07-07T10:42:32Z', uploadQueueCount: 6485 }),
];

const currentDevice = mobileDevice({ deviceId: 'android-a5b98c2e27c8c280' });

const allDaemons = [windowsDaemon, currentAndroidDaemon, ...mergedAwayDaemons];
const allDevices = [currentDevice];

function test(name: string, run: () => void) { run(); }

test('#278 已合并掉的旧安卓设备不再逐台出现', () => {
  const model = buildStatusDeviceModel(allDaemons, allDevices);

  const shownIds = [
    ...model.pcDaemons.map(d => d.deviceId),
    ...model.mobileDevices.map(m => m.device.deviceId),
  ];

  for (const stale of mergedAwayDaemons) {
    assert.equal(
      shownIds.includes(stale.deviceId),
      false,
      `已合并设备 ${stale.deviceId} 不应再出现在状态页`,
    );
  }
});

test('#278 同一台设备不再重复展示（守护卡与设备卡合并为一张）', () => {
  const model = buildStatusDeviceModel(allDaemons, allDevices);

  const currentId = 'android-a5b98c2e27c8c280';
  const asPc = model.pcDaemons.filter(d => d.deviceId === currentId).length;
  const asMobile = model.mobileDevices.filter(m => m.device.deviceId === currentId).length;

  assert.equal(asPc, 0, '当前手机的守护心跳不应单独成卡');
  assert.equal(asMobile, 1, '当前手机应只出现一次（设备卡）');
  assert.equal(
    model.mobileDevices[0].daemon?.deviceId,
    currentId,
    '守护心跳信息应并入该设备的卡片',
  );
});

test('#278 卡片总数从 8 张降为 2 张（1 台 PC + 1 台移动端）', () => {
  const model = buildStatusDeviceModel(allDaemons, allDevices);

  assert.equal(model.pcDaemons.length, 1);
  assert.equal(model.mobileDevices.length, 1);
  assert.equal(model.pcDaemons[0].deviceId, 'DESKTOP-ARJ75IN');
});

test('#278 计数不再把安卓守护算作 PC', () => {
  const model = buildStatusDeviceModel(allDaemons, allDevices);

  assert.deepEqual(model.counts, { pc: 1, mobile: 1 });
  assert.equal(formatStatusDeviceCount(model.counts), '1 PC · 1 移动端');
  // 修复前是「7 PC · 1 移动端」（7 = 1 台 Windows + 6 台安卓守护）
  assert.notEqual(formatStatusDeviceCount(model.counts), '7 PC · 1 移动端');
});

test('#278 Windows 守护仍然计入 PC（不受移动设备合并影响）', () => {
  const model = buildStatusDeviceModel([windowsDaemon], []);

  assert.equal(model.pcDaemons.length, 1);
  assert.equal(model.mobileDevices.length, 0);
  assert.deepEqual(model.counts, { pc: 1, mobile: 0 });
});

test('#278 未注册的安卓设备不会凭空出现（避免幽灵卡）', () => {
  const orphan = heartbeat({ deviceId: 'android-never-registered', daemonKind: 'android' });
  const model = buildStatusDeviceModel([orphan], []);

  assert.equal(model.pcDaemons.length, 0);
  assert.equal(model.mobileDevices.length, 0);
  assert.deepEqual(model.counts, { pc: 0, mobile: 0 });
});

test('#278 同一设备有多条守护心跳时取最新一条', () => {
  const older = heartbeat({ deviceId: 'android-x', daemonKind: 'android', receivedAt: '2026-07-01T00:00:00Z', uploadQueueCount: 999 });
  const newer = heartbeat({ deviceId: 'android-x', daemonKind: 'android', receivedAt: '2026-08-01T00:00:00Z', uploadQueueCount: 3 });
  const model = buildStatusDeviceModel([older, newer], [mobileDevice({ deviceId: 'android-x' })]);

  assert.equal(model.mobileDevices.length, 1);
  assert.equal(model.mobileDevices[0].daemon?.receivedAt, '2026-08-01T00:00:00Z');
  assert.equal(model.mobileDevices[0].daemon?.uploadQueueCount, 3);
});

test('#278 空输入安全', () => {
  const model = buildStatusDeviceModel([], []);
  assert.deepEqual(model.counts, { pc: 0, mobile: 0 });
  assert.equal(formatStatusDeviceCount(model.counts), '0 PC · 0 移动端');
});

console.log('statusDeviceModel tests passed');
