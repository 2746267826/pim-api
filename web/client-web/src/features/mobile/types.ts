/* 移动设备域（05/mobile.md） */

export interface MobileAnalyticsRange {
  rangeStartUtc: string
  rangeEndUtc: string
  localStartDate: string
  localEndDate: string
}

export interface MobileAnalyticsOverview {
  range: MobileAnalyticsRange
  generatedAt: string
  totalForegroundSeconds: number
  dailyAverageSeconds: number
  highestUseLocalDate: string | null
  peakLocalHour: number | null
  appCount: number
  switchOrPickupCount: number
  completeness: number
  quality: {
    usageEventsCoverage: number
    fallbackShare: number
    missingMetadataAppCount: number
    systemNoiseShare: number
    failedOrPartialSyncBatchCount: number
    lastSyncAt: string | null
  }
  goalProgress: { label: string; limitSeconds: number; usedSeconds: number; isOverLimit: boolean; remainingSeconds: number } | null
  anomalies: { code: string; severity: string; title: string; evidence: string; drilldownTarget: string }[]
  suggestions: { code: string; text: string; drilldownTarget: string }[]
}

export interface MobileHeatmapBucket {
  bucketStartUtc: string
  bucketEndUtc: string
  localDate: string
  localHour: number
  lifeCategory: string
  foregroundSeconds: number
}

export interface MobileChartDto {
  key: string
  title: string
  chartType: string
  unit: string
  points: { key: string; label: string; value: number; lifeCategory?: string | null; packageName?: string | null; localDate?: string | null }[]
}

export interface MobileTimelineBlock {
  id: string
  startUtc: string
  endUtc: string
  localStart: string
  localEnd: string
  lifeCategory: string
  foregroundSeconds: number
  sessionCount: number
  appCount: number
  topApps: { packageName: string; displayName: string; foregroundSeconds: number }[]
}

/** GET /mobile/devices 的元素（MobileDeviceDto，规格 mobile.md:19-38） */
export interface MobileDeviceDto {
  id: string
  deviceId: string
  androidIdHash: string | null
  displayName: string
  manufacturer: string
  brand: string
  model: string
  androidVersion: string
  sdkInt: number
  appVersion: string
  metadataJson: string
  firstSeenAt: string
  lastSeenAt: string
  lastHeartbeatAt: string | null
  lastSyncAt: string | null
  isActive: boolean
}

/** GET /mobile/devices/manage 的元素（DeviceListDto，规格 mobile.md:49-69） */
export interface DeviceListItem {
  deviceId: string
  displayName: string
  brand: string
  model: string
  osVersion: string
  appVersion: string
  registeredAtUtc: string
  lastSeenAtUtc: string
  isOnline: boolean
  sessionCount: number
  eventCount: number
  locationCount: number
  summaryCount: number
  earliest: string | null
  latest: string | null
  storageEstimateKb: number
  syncStatus: string
  dataQuality: string
  storagePressure: string
}

export interface DeviceMergePreview {
  items: { deviceId: string; dataCount: number }[]
  total: number
}

export interface DeviceDeletePreview {
  deviceId: string
  displayName: string
  sessionCount: number
  eventCount: number
  locationCount: number
  summaryCount: number
}

/* ── 设备存活（取证） ─────────────────────────────────────── */

export interface DeviceLiveness {
  deviceId: string
  displayName: string
  deviceKind: 'phone' | 'tablet' | 'unknown'
  deviceKindLabel: string
  hasData: boolean
  conclusion: string
  coverageByHour: number | null
  coverageByExpectedHeartbeat: number | null
  longestSilenceMinutes: number
  longestSilenceSeverity: 'none' | 'warning' | 'critical'
  silences: { startUtc: string; endUtc: string; minutes: number; severity: string; severityLabel: string }[]
  causes: { cause: string; label: string; count: number; inference: string | null }[]
  lastEventAtUtc: string | null
}

export interface LivenessOverview {
  rangeStartUtc: string
  rangeEndUtc: string
  expectedHeartbeatIntervalMinutes: number
  phones: DeviceLiveness[]
  tablets: DeviceLiveness[]
  unclassified: DeviceLiveness[]
}

/* ── 位置轨迹（05/mobile.md §位置分析） ───────────────────── */

export interface LocationTrack {
  id: string
  deviceId: string
  startUtc: string
  endUtc: string
  distanceMeters: number
  durationSeconds: number
  pointCount: number
  segmentCount: number
  segments: {
    id: string
    kind: string
    startUtc: string
    endUtc: string
    localStart: string
    localEnd: string
    durationSeconds: number
    distanceMeters: number
    pointCount: number
    path: { id: string; recordedAtUtc: string; latitude: number; longitude: number; horizontalAccuracyMeters: number }[]
  }[]
}

export interface FrequentPlace {
  centerLatitude: number
  centerLongitude: number
  radiusMeters: number
  pointCount: number
  visitDayCount: number
  isHome: boolean
}

export interface LocationOverview {
  range: MobileAnalyticsRange
  pointCount: number
  usablePointCount: number
  rejectedPointCount: number
  distanceMeters: number
  stayCount: number
  longestStaySeconds: number
  averageAccuracyMeters: number
}

export interface SegmentPointsPage {
  items: { id: string; recordedAtUtc: string; latitude: number; longitude: number; horizontalAccuracyMeters: number }[]
  nextCursor: string | null
  hasMore: boolean
}

export interface MovementStats {
  homeCenter: { latitude: number; longitude: number } | null
  outingCount: number
  outingSeconds: number
  distanceMeters: number
  maxSpeedMetersPerSecond: number | null
}

/* ── 设备详情 ─────────────────────────────────────────────── */

export interface DeviceDetail {
  device: {
    deviceId: string
    displayName: string
    brand: string
    model: string
    osVersion: string
    appVersion: string
    registeredAtUtc: string
    lastSeenAtUtc: string
  }
  stats: { sessionCount: number; eventCount: number; locationCount: number; storageEstimateKb: number }
  syncHistory: { batchId: string; createdAt: string; acceptedCount: number; status: string }[]
  healthTimeline: string[]
}
