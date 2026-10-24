import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { DeviceListItem } from '../../src/client-web/src/api/mobile';
import {
  buildMergePreviewRows,
  deviceDataCount,
  deviceOptionLabel,
  formatLastSeen,
  mergeIncomingTotal,
  orderMergeCandidates,
  pickDefaultMergeTarget,
  shortDeviceId,
} from '../../src/client-web/src/pages/deviceMergeModel';

const NOW = new Date('2026-09-13T10:00:00Z');

function device(overrides: Partial<DeviceListItem> & { deviceId: string }): DeviceListItem {
  return {
    displayName: 'OPPO PLG110',
    brand: 'OPPO',
    model: 'PLG110',
    osVersion: '15',
    appVersion: '2026.09.504',
    registeredAtUtc: '2026-07-07T10:42:16Z',
    lastSeenAtUtc: '2026-07-07T10:42:16Z',
    isOnline: false,
    sessionCount: 0,
    eventCount: 0,
    locationCount: 0,
    summaryCount: 0,
    earliest: null,
    latest: null,
    storageEstimateKb: 0,
    syncStatus: 'disconnected',
    dataQuality: 'normal',
    storagePressure: 'normal',
    ...overrides,
  };
}

/**
 * issue #232 的真实数据形态：6 台设备 displayName 全部相同，
 * 只有 android-a5b98c2e27c8c280 是当天仍在同步的活跃设备。
 */
const PRODUCTION_LIKE_DEVICES: DeviceListItem[] = [
  device({
    deviceId: 'android-a5b98c2e27c8c280',
    lastSeenAtUtc: '2026-09-13T09:48:00Z',
    isOnline: true,
    sessionCount: 63302,
    eventCount: 256447,
    locationCount: 5938,
    summaryCount: 99695,
  }),
  device({
    deviceId: 'android-c8afbd44c95ad974',
    lastSeenAtUtc: '2026-07-11T05:19:02Z',
    sessionCount: 19990,
    eventCount: 79501,
    locationCount: 275,
    summaryCount: 16188,
  }),
  device({
    deviceId: 'android-86e569f5435a0dd7',
    lastSeenAtUtc: '2026-07-09T03:24:38Z',
    sessionCount: 18444,
    eventCount: 72170,
    locationCount: 2,
    summaryCount: 14681,
  }),
];

describe('deviceMergeModel 设备区分信息', () => {
  it('同名的多台设备靠短码区分', () => {
    const ids = PRODUCTION_LIKE_DEVICES.map(item => `…${shortDeviceId(item.deviceId)}`);
    assert.deepEqual(ids, ['…c8c280', '…5ad974', '…5a0dd7']);
    assert.equal(new Set(ids).size, ids.length, '短码必须互不相同');
  });

  it('过短的 device id 原样返回，不做越界截取', () => {
    assert.equal(shortDeviceId('abc'), 'abc');
    assert.equal(shortDeviceId('  android-x  '), 'roid-x');
  });

  it('按 sessions+events+locations+summaries 统计记录数，与后端预览口径一致', () => {
    assert.equal(deviceDataCount(PRODUCTION_LIKE_DEVICES[0]), 63302 + 256447 + 5938 + 99695);
  });

  it('把最后活跃时间渲染成相对文案', () => {
    assert.equal(formatLastSeen('2026-09-13T09:58:00Z', NOW), '2 分钟前');
    assert.equal(formatLastSeen('2026-09-13T07:00:00Z', NOW), '3 小时前');
    assert.equal(formatLastSeen('2026-08-20T10:00:00Z', NOW), '24 天前');
    // 超过 30 天不再用「N 天前」，直接给日期，避免出现「64 天前」这类难读文案
    assert.equal(formatLastSeen('2026-07-11T05:19:02Z', NOW), '2026-07-11');
    assert.equal(formatLastSeen('2026-09-13T10:00:30Z', NOW), '刚刚');
    assert.equal(formatLastSeen('2025-01-05T00:00:00Z', NOW), '2025-01-05');
    assert.equal(formatLastSeen(null, NOW), '未知');
    assert.equal(formatLastSeen('not-a-date', NOW), '未知');
  });

  it('选项文案同时给出设备名、短码、最后活跃、记录数与活跃标记', () => {
    const rows = buildMergePreviewRows(PRODUCTION_LIKE_DEVICES, 'android-a5b98c2e27c8c280', null, NOW);
    const label = deviceOptionLabel(rows[0]);
    assert.equal(
      label,
      'OPPO PLG110 · ID …c8c280 · 最后活跃 12 分钟前 · 425382 条记录 · 当前活跃',
    );
  });
});

describe('deviceMergeModel 默认保留设备', () => {
  it('默认保留最近活跃的那台', () => {
    assert.equal(pickDefaultMergeTarget(PRODUCTION_LIKE_DEVICES), 'android-a5b98c2e27c8c280');
  });

  it('没有在线设备时取最后活跃时间最新的一台', () => {
    const offline = PRODUCTION_LIKE_DEVICES.map(item => ({ ...item, isOnline: false }));
    assert.equal(pickDefaultMergeTarget(offline), 'android-a5b98c2e27c8c280');
  });

  it('按在线优先、其次最后活跃倒序稳定排序', () => {
    const ordered = orderMergeCandidates(PRODUCTION_LIKE_DEVICES).map(item => item.deviceId);
    assert.deepEqual(ordered, [
      'android-a5b98c2e27c8c280',
      'android-c8afbd44c95ad974',
      'android-86e569f5435a0dd7',
    ]);
  });

  it('勾选集合为空时不返回任何默认目标', () => {
    assert.equal(pickDefaultMergeTarget([]), '');
  });
});

describe('deviceMergeModel 合并预览', () => {
  const target = 'android-a5b98c2e27c8c280';

  it('按设备列出明细，并标明保留哪台、哪些会被移除', () => {
    const rows = buildMergePreviewRows(PRODUCTION_LIKE_DEVICES, target, null, NOW);

    assert.equal(rows.length, 3);
    assert.deepEqual(
      rows.map(row => [row.deviceId, row.action]),
      [
        [target, 'keep'],
        ['android-c8afbd44c95ad974', 'merge-and-remove'],
        ['android-86e569f5435a0dd7', 'merge-and-remove'],
      ],
    );
  });

  it('合计只统计将被并入的源设备，不含目标设备自身', () => {
    const rows = buildMergePreviewRows(PRODUCTION_LIKE_DEVICES, target, null, NOW);
    const expected = (19990 + 79501 + 275 + 16188) + (18444 + 72170 + 2 + 14681);
    assert.equal(mergeIncomingTotal(rows), expected);
    assert.notEqual(mergeIncomingTotal(rows), rows.reduce((sum, row) => sum + row.dataCount, 0));
  });

  it('优先使用后端预览返回的每台记录数', () => {
    const rows = buildMergePreviewRows(PRODUCTION_LIKE_DEVICES, target, {
      items: [
        { deviceId: target, dataCount: 1 },
        { deviceId: 'android-c8afbd44c95ad974', dataCount: 999 },
        { deviceId: 'android-86e569f5435a0dd7', dataCount: 111 },
      ],
      total: 1111,
    }, NOW);

    const byId = new Map(rows.map(row => [row.deviceId, row.dataCount]));
    assert.equal(byId.get('android-c8afbd44c95ad974'), 999);
    assert.equal(byId.get('android-86e569f5435a0dd7'), 111);
    assert.equal(mergeIncomingTotal(rows), 1110, '目标设备自身不计入将并入的合计');
  });

  it('后端预览缺少某台设备时回退到列表里的本地记录数', () => {
    const rows = buildMergePreviewRows(PRODUCTION_LIKE_DEVICES, target, {
      items: [{ deviceId: 'android-c8afbd44c95ad974', dataCount: 7 }],
      total: 7,
    }, NOW);

    const fallback = rows.find(row => row.deviceId === 'android-86e569f5435a0dd7');
    assert.equal(fallback?.dataCount, 18444 + 72170 + 2 + 14681);
  });
});
