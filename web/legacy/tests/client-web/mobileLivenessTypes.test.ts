import type {
  MobileLivenessDeviceBlock,
  MobileLivenessEventPage,
  MobileLivenessOverview,
} from '../../src/client-web/src/api/mobile';

const device: MobileLivenessDeviceBlock = {
  deviceId: 'phone-1', displayName: '测试手机', deviceKind: 'phone', deviceKindLabel: '手机',
  hasData: true, conclusion: '设备存活情况稳定。', coverageByHour: 0.5,
  coverageByExpectedHeartbeat: 0.25, observedHours: 12, totalHours: 24,
  observedHeartbeats: 12, expectedHeartbeats: 48, expectedHeartbeatIntervalMinutes: 15,
  longestSilenceMinutes: 60, longestSilenceStartUtc: null, longestSilenceEndUtc: null,
  longestSilenceSeverity: 'critical', hasSilenceOverOneHour: false, silences: [], causes: [],
  lastEventAtUtc: null, coverageByHourDefinition: '小时覆盖定义',
  coverageByExpectedHeartbeatDefinition: '心跳覆盖定义',
  fulfillment: { rate: 0.5, fulfilled: 1, considered: 2, excludedNoActualTime: 0 },
};
const overview: MobileLivenessOverview = {
  rangeStartUtc: '2026-09-01T00:00:00Z', rangeEndUtc: '2026-09-02T00:00:00Z',
  expectedHeartbeatIntervalMinutes: 15, phones: [device], tablets: [], unclassified: [],
};
const events: MobileLivenessEventPage = {
  items: [{
    id: 'event-1', eventType: 'heartbeat', eventTypeLabel: '存活心跳', occurredAtUtc: '2026-09-01T01:00:00Z',
    reason: null, reasonLabel: null, inference: null, importance: null, pssKb: null, rssKb: null,
    description: '屏幕已解锁', payloadJson: '{"battery":80}',
  }],
  page: 1, pageSize: 50, totalCount: 1, totalPages: 1,
};

if (overview.phones[0].coverageByHourDefinition !== '小时覆盖定义' || events.items[0].payloadJson.length === 0) {
  throw new Error('mobile liveness response contract mismatch');
}

console.error('PASS: mobileLivenessTypes');
