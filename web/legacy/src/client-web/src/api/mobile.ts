import { apiDelete, apiGet, apiPost, apiPut } from './client';
import type { ApiResponse, PimHealthStatus } from '../types';
import { MOBILE_LIFE_CATEGORY_LABELS } from '../components/mobile/mobileAnalyticsCopy';

type QueryValue = string | number | boolean | null | undefined;

function withQuery(path: string, entries: Array<[string, QueryValue]>) {
  const searchParams = new URLSearchParams();

  entries.forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') {
      searchParams.set(key, String(value));
    }
  });

  const query = searchParams.toString();
  return query ? `${path}?${query}` : path;
}

function pathSegment(value: string) {
  return encodeURIComponent(value);
}

export const MOBILE_DEFAULT_TIMEZONE = 'Asia/Shanghai';

export const MOBILE_LIFE_CATEGORIES = MOBILE_LIFE_CATEGORY_LABELS;

export type MobileLifeCategory = (typeof MOBILE_LIFE_CATEGORIES)[number];
export type MobileAnalyticsGranularity = 'hour' | '30m' | '15m' | 'day';

export interface MobileAnalyticsQuery {
  rangeStartUtc?: string | null;
  rangeEndUtc?: string | null;
  timezone?: string | null;
  deviceId?: string | null;
  category?: MobileLifeCategory | string | null;
  packageName?: string | null;
  source?: string | null;
  includeSystemNoise?: boolean | null;
  minDurationSeconds?: number | null;
  granularity?: MobileAnalyticsGranularity | null;
  cursor?: string | null;
  page?: number | null;
  pageSize?: number | null;
  force?: boolean;
}

export interface MobileLivenessQuery {
  rangeStartUtc?: string | null;
  rangeEndUtc?: string | null;
}

export interface MobileLivenessEventsQuery extends MobileLivenessQuery {
  page?: number | null;
  pageSize?: number | null;
}

export type MobileLivenessDeviceKind = 'phone' | 'tablet' | 'unknown';
export type MobileLivenessSeverity = 'none' | 'warning' | 'critical';

export interface MobileLivenessSilence {
  startUtc: string;
  endUtc: string;
  minutes: number;
  severity: MobileLivenessSeverity;
  severityLabel: string;
}

export interface MobileLivenessCause {
  cause: string;
  label: string;
  count: number;
  inference: string | null;
}

export interface MobileLivenessDeviceBlock {
  deviceId: string;
  displayName: string;
  deviceKind: MobileLivenessDeviceKind;
  deviceKindLabel: string;
  hasData: boolean;
  conclusion: string;
  coverageByHour: number | null;
  coverageByExpectedHeartbeat: number | null;
  observedHours: number;
  totalHours: number;
  observedHeartbeats: number;
  expectedHeartbeats: number;
  expectedHeartbeatIntervalMinutes: number;
  longestSilenceMinutes: number;
  longestSilenceStartUtc: string | null;
  longestSilenceEndUtc: string | null;
  longestSilenceSeverity: MobileLivenessSeverity;
  hasSilenceOverOneHour: boolean;
  silences: MobileLivenessSilence[];
  causes: MobileLivenessCause[];
  lastEventAtUtc: string | null;
  coverageByHourDefinition: string;
  coverageByExpectedHeartbeatDefinition: string;
  /**
   * REQ-18 叫醒兑现率（可选展示项）。
   * `null` 表示该设备没有闹钟数据——**不得显示为 0%**（AC-18.2）。
   */
  fulfillment: MobileLivenessFulfillment | null;
}

/** REQ-18 兑现率；rate 为 null 表示无可判定记录。 */
export interface MobileLivenessFulfillment {
  rate: number | null;
  fulfilled: number;
  considered: number;
  excludedNoActualTime: number;
}

export interface MobileLivenessOverview {
  rangeStartUtc: string;
  rangeEndUtc: string;
  expectedHeartbeatIntervalMinutes: number;
  phones: MobileLivenessDeviceBlock[];
  tablets: MobileLivenessDeviceBlock[];
  unclassified: MobileLivenessDeviceBlock[];
}

export interface MobileLivenessEvent {
  id: string;
  eventType: string;
  eventTypeLabel: string;
  occurredAtUtc: string;
  reason: string | null;
  reasonLabel: string | null;
  inference: string | null;
  importance: number | null;
  pssKb: number | null;
  rssKb: number | null;
  description: string | null;
  payloadJson: string;
}

export interface MobileLivenessEventPage {
  items: MobileLivenessEvent[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

function withLivenessQuery(path: string, query: MobileLivenessQuery | MobileLivenessEventsQuery = {}) {
  return withQuery(path, [
    ['rangeStartUtc', query.rangeStartUtc],
    ['rangeEndUtc', query.rangeEndUtc],
    ['page', 'page' in query ? query.page : undefined],
    ['pageSize', 'pageSize' in query ? query.pageSize : undefined],
  ]);
}

function withAnalyticsQuery(path: string, query: MobileAnalyticsQuery = {}) {
  return withQuery(path, [
    ['rangeStartUtc', query.rangeStartUtc],
    ['rangeEndUtc', query.rangeEndUtc],
    ['timezone', query.timezone],
    ['deviceId', query.deviceId],
    ['category', query.category],
    ['packageName', query.packageName],
    ['source', query.source],
    ['includeSystemNoise', query.includeSystemNoise],
    ['minDurationSeconds', query.minDurationSeconds],
    ['granularity', query.granularity],
    ['cursor', query.cursor],
    ['page', query.page],
    ['pageSize', query.pageSize],
    ['force', query.force === true ? 'true' : undefined],
  ]);
}

export interface MobileLocationAnalyticsParams {
  rangeStartUtc?: string | null;
  rangeEndUtc?: string | null;
  timezone?: string | null;
  deviceId?: string | null;
  maxAccuracyMeters?: number | null;
  includeRejected?: boolean | null;
  cursor?: string | null;
  pageSize?: number | null;
  force?: boolean;
}

function withLocationAnalyticsQuery(path: string, query: MobileLocationAnalyticsParams = {}) {
  return withQuery(path, [
    ['rangeStartUtc', query.rangeStartUtc],
    ['rangeEndUtc', query.rangeEndUtc],
    ['timezone', query.timezone],
    ['deviceId', query.deviceId],
    ['maxAccuracyMeters', query.maxAccuracyMeters],
    ['includeRejected', query.includeRejected],
    ['cursor', query.cursor],
    ['pageSize', query.pageSize],
    ...(query.force === true ? [['force', 'true'] as [string, string]] : []),
  ]);
}

export interface MobileLocationHistoryParams {
  start: string;
  end: string;
  deviceId?: string;
  maxAccuracyMeters?: number;
}

export const mobileApiPaths = {
  devices: '/mobile/devices',
  summary: (date: string, deviceId?: string) =>
    withQuery('/mobile/summary', [
      ['date', date],
      ['deviceId', deviceId],
    ]),
  timeline: (date: string, deviceId?: string, page?: number, pageSize?: number) =>
    withQuery('/mobile/timeline', [
      ['date', date],
      ['deviceId', deviceId],
      ['page', page],
      ['pageSize', pageSize],
    ]),
  locations: (start: string, end: string, deviceId?: string, maxAccuracyMeters = 50) =>
    withQuery('/mobile/location/history', [
      ['start', start],
      ['end', end],
      ['maxAccuracyMeters', maxAccuracyMeters],
      ['deviceId', deviceId],
    ]),
  locationHistory: (params: MobileLocationHistoryParams) =>
    withQuery('/mobile/location/history', [
      ['start', params.start],
      ['end', params.end],
      ['maxAccuracyMeters', params.maxAccuracyMeters ?? 50],
      ['deviceId', params.deviceId],
    ]),
  locationAnalyticsOverview: (query: MobileLocationAnalyticsParams = {}) =>
    withLocationAnalyticsQuery('/mobile/location/analytics/overview', query),
  locationAnalyticsTracks: (query: MobileLocationAnalyticsParams = {}) =>
    withLocationAnalyticsQuery('/mobile/location/analytics/tracks', query),
  locationAnalyticsSegment: (segmentId: string) =>
    `/mobile/location/analytics/segments/${pathSegment(segmentId)}`,
  locationAnalyticsSegmentPoints: (segmentId: string, query: MobileLocationAnalyticsParams = {}) =>
    withLocationAnalyticsQuery(`/mobile/location/analytics/segments/${pathSegment(segmentId)}/points`, query),
  locationAnalyticsFrequentPlaces: (query: MobileLocationAnalyticsParams = {}) =>
    withLocationAnalyticsQuery('/mobile/location/analytics/frequent-places', query),
  locationAnalyticsMovementStats: (query: MobileLocationAnalyticsParams = {}) =>
    withLocationAnalyticsQuery('/mobile/location/analytics/movement-stats', query),
  quality: (date?: string, deviceId?: string) =>
    withQuery('/mobile/quality', [
      ['date', date],
      ['deviceId', deviceId],
    ]),
  analyticsOverview: (query: MobileAnalyticsQuery = {}) =>
    withAnalyticsQuery('/mobile/analytics/overview', query),
  analyticsHeatmap: (query: MobileAnalyticsQuery = {}) =>
    withAnalyticsQuery('/mobile/analytics/heatmap', query),
  analyticsCharts: (query: MobileAnalyticsQuery = {}) =>
    withAnalyticsQuery('/mobile/analytics/charts', query),
  analyticsTimelineBlocks: (query: MobileAnalyticsQuery = {}) =>
    withAnalyticsQuery('/mobile/analytics/timeline-blocks', query),
  timelineBlockSessions: (blockId: string, query: MobileAnalyticsQuery = {}) =>
    withAnalyticsQuery(`/mobile/analytics/timeline-blocks/${pathSegment(blockId)}/sessions`, query),
  sessionEvents: (sessionId: string) =>
    `/mobile/analytics/sessions/${pathSegment(sessionId)}/events`,
  appCatalogOverrides: () => '/mobile/apps/catalog-overrides',
  appCatalogOverride: (packageName: string) =>
    `/mobile/apps/catalog-overrides/${pathSegment(packageName)}`,
  appCategoryRules: () => '/mobile/apps/category-rules',
  appCategoryRule: (id: string) => `/mobile/apps/category-rules/${pathSegment(id)}`,
  usageGoals: () => '/mobile/analytics/goals',
  usageGoal: (id: string) => `/mobile/analytics/goals/${pathSegment(id)}`,
  livenessOverview: (query: MobileLivenessQuery = {}) =>
    withLivenessQuery('/mobile/liveness/overview', query),
  livenessEvents: (deviceId: string, query: MobileLivenessEventsQuery = {}) =>
    withLivenessQuery(`/mobile/devices/${pathSegment(deviceId)}/liveness/events`, query),
} as const;

export interface MobileDevice {
  id: string;
  deviceId: string;
  androidIdHash: string | null;
  displayName: string;
  manufacturer: string;
  brand: string;
  model: string;
  androidVersion: string;
  sdkInt: number;
  appVersion: string;
  metadataJson: string;
  firstSeenAt: string;
  lastSeenAt: string;
  lastHeartbeatAt: string | null;
  lastSyncAt: string | null;
  isActive: boolean;
}

export type MobileUsageSource = 'events' | 'fallback' | string;

export interface MobileAppUsageSummary {
  packageName: string;
  displayName: string;
  categoryName: string | null;
  foregroundSeconds: number;
  sessionCount: number;
  launchCount: number;
  lastUsedAt: string | null;
  source: MobileUsageSource;
  share: number;
}

export interface MobileSyncBatchSummary {
  id: string;
  deviceId: string;
  clientBatchId: string;
  sourceWindowStartUtc: string;
  sourceWindowEndUtc: string;
  submittedAtUtc: string;
  status: string;
  acceptedEventCount: number;
  skippedEventCount: number;
  rejectedItemCount: number;
  acceptedLocationCount: number;
  rejectedLocationCount: number;
  errorMessage: string | null;
}

export interface MobileSummary {
  date: string;
  deviceId: string | null;
  generatedAt: string;
  totalForegroundSeconds: number;
  fallbackForegroundSeconds: number;
  appSwitchCount: number;
  appsUsed: number;
  completeness: number;
  lastSyncAt: string | null;
  appRanking: MobileAppUsageSummary[];
  syncBatches: MobileSyncBatchSummary[];
  qualityIssueCount: number;
}

interface MobileTimelineBase {
  id: string;
  deviceId: string;
  packageName: string;
  displayName: string;
  start: string;
  end: string;
  durationSeconds: number;
}

export interface MobileTimelineSession extends MobileTimelineBase {
  kind: 'session';
  source: 'events' | string;
  confidence: number;
}

export interface MobileTimelineFallback extends MobileTimelineBase {
  kind: 'fallback';
  source: 'fallback' | string;
  reason: string;
}

export type MobileTimelineItem = MobileTimelineSession | MobileTimelineFallback;

export interface MobileTimeline {
  date: string;
  deviceId: string | null;
  generatedAt: string;
  sessions: MobileTimelineSession[];
  fallbackSummaries: MobileTimelineFallback[];
  items: MobileTimelineItem[];
  // #330：单日会话可达数千条，服务端分页返回。hasMore/truncated 为 true 时
  // sessions/items 只是当前页，必须继续翻页才能拿到全天数据。
  page: number;
  pageSize: number;
  totalCount: number;
  sessionTotalCount: number;
  fallbackTotalCount: number;
  hasMore: boolean;
  truncated: boolean;
}

export type MobileLocationQuality = 'high' | 'usable' | 'rejected' | string;

export interface MobileLocationPoint {
  id: string;
  deviceId: string;
  recordedAtUtc: string;
  submittedAtUtc: string;
  latitude: number;
  longitude: number;
  horizontalAccuracyMeters: number;
  provider: string;
  sourceKind: string;
  altitudeMeters: number | null;
  verticalAccuracyMeters: number | null;
  speedMetersPerSecond: number | null;
  speedAccuracyMetersPerSecond: number | null;
  bearingDegrees: number | null;
  bearingAccuracyDegrees: number | null;
  isAutoSubmitted: boolean;
  quality: MobileLocationQuality;
  rawJson: string;
}

export interface MobileLocationHistory {
  start: string;
  end: string;
  deviceId: string | null;
  maxAccuracyMeters: number;
  points: MobileLocationPoint[];
}

export interface MobileLocationBounds {
  minLatitude: number;
  minLongitude: number;
  maxLatitude: number;
  maxLongitude: number;
}

export interface MobileLocationPathPoint {
  id?: string | null;
  latitude: number;
  longitude: number;
  recordedAtUtc?: string | null;
  horizontalAccuracyMeters?: number | null;
  quality?: MobileLocationQuality | string | null;
  qualityFlags?: string[] | null;
}

export type MobileLocationSegmentKind = 'move' | 'stay' | 'gap' | 'low-confidence' | string;

export interface MobileLocationAnalyticsOverview {
  range: MobileAnalyticsRange;
  generatedAt: string;
  pointCount: number;
  usablePointCount: number;
  rejectedPointCount: number;
  activeSpanSeconds: number;
  distanceMeters: number;
  stayCount: number;
  longestStaySeconds: number;
  averageAccuracyMeters: number;
  qualityIssueCount: number;
  qualityFlags: string[];
}

export interface MobileLocationSegment {
  id: string;
  trackId: string;
  deviceId: string;
  kind: MobileLocationSegmentKind;
  startUtc: string;
  endUtc: string;
  localStart: string;
  localEnd: string;
  durationSeconds: number;
  distanceMeters: number;
  pointCount: number;
  averageSpeedMetersPerSecond: number;
  averageAccuracyMeters: number;
  maxAccuracyMeters: number;
  quality: MobileLocationQuality;
  qualityFlags: string[];
  bounds: MobileLocationBounds | null;
  path: MobileLocationPathPoint[];
}

export interface MobileLocationTrack {
  id: string;
  deviceId: string;
  startUtc: string;
  endUtc: string;
  distanceMeters: number;
  durationSeconds: number;
  pointCount: number;
  segmentCount: number;
  bounds: MobileLocationBounds | null;
  qualityFlags: string[];
  segments: MobileLocationSegment[];
}

export interface MobileLocationSegmentPointPage {
  items: MobileLocationPoint[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface MobileFrequentPlace {
  centerLatitude: number;
  centerLongitude: number;
  radiusMeters: number;
  pointCount: number;
  visitDayCount: number;
  isHome: boolean;
}

export interface MobileFrequentPlacesResponse {
  home: MobileFrequentPlace | null;
  places: MobileFrequentPlace[];
}

export interface MobileMovementOuting {
  startUtc: string;
  endUtc: string;
  seconds: number;
}

export interface MobileMovementStatsDay {
  date: string;
  outingCount: number;
  outingSeconds: number;
  distanceMeters: number;
}

export interface MobileMovementStatsResponse {
  homeCenter: { latitude: number; longitude: number } | null;
  outingCount: number;
  outingSeconds: number;
  outings: MobileMovementOuting[];
  distanceMeters: number;
  maxSpeedMetersPerSecond: number | null;
  perDay: MobileMovementStatsDay[];
}

export interface MobileQualityComponent {
  key: 'android-heartbeat' | 'mobile-sync' | 'mobile-usage-coverage' | 'mobile-location' | string;
  name: string;
  status: PimHealthStatus;
  message: string;
  checkedAt: string;
  details: Record<string, string>;
}

export interface MobileQualityIssue {
  code: string;
  severity: PimHealthStatus;
  componentKey: string;
  message: string;
  nextStep: string | null;
}

export interface MobileQuality {
  overallStatus: PimHealthStatus;
  label: string;
  message: string;
  checkedAt: string;
  components: MobileQualityComponent[];
  issues: MobileQualityIssue[];
  nextSteps: string[];
}

export interface MobileAnalyticsRange {
  rangeStartUtc: string;
  rangeEndUtc: string;
  timezone: string;
  localStartDate: string;
  localEndDate: string;
}

export interface MobileAnalyticsQuality {
  usageEventsCoverage: number;
  fallbackShare: number;
  missingMetadataAppCount: number;
  systemNoiseShare: number;
  shortEventShare: number;
  failedOrPartialSyncBatchCount: number;
  lastSyncAt: string | null;
  qualityFlags: string[];
}

export interface MobileAnalyticsGoal {
  key: string;
  label: string;
  limitSeconds: number;
  usedSeconds: number;
  isOverLimit: boolean;
  remainingSeconds: number;
}

export interface MobileAnalyticsAnomaly {
  code: string;
  severity: PimHealthStatus | 'Info' | string;
  title: string;
  evidence: string;
  drilldownTarget: string;
}

export interface MobileAnalyticsSuggestion {
  code: string;
  text: string;
  drilldownTarget: string;
}

export interface MobileAnalyticsOverview {
  range: MobileAnalyticsRange;
  generatedAt: string;
  isStale: boolean;
  totalForegroundSeconds: number;
  dailyAverageSeconds: number;
  previousPeriodChange: number;
  highestUseLocalDate: string | null;
  peakLocalHour: number | null;
  appCount: number;
  switchOrPickupCount: number;
  completeness: number;
  quality: MobileAnalyticsQuality;
  goalProgress: MobileAnalyticsGoal | null;
  anomalies: MobileAnalyticsAnomaly[];
  suggestions: MobileAnalyticsSuggestion[];
}

export interface MobileHeatmapBucket {
  bucketStartUtc: string;
  bucketEndUtc: string;
  localDate: string;
  localHour: number;
  lifeCategory: MobileLifeCategory | string;
  foregroundSeconds: number;
  qualityFlags: string[];
}

export type MobileAnalyticsChartType =
  | 'category-share'
  | 'category-trend'
  | 'daily-total'
  | 'hour-distribution'
  | 'top-apps'
  | 'switch-trend'
  | 'comparison'
  | 'goal-marker'
  | string;

export type MobileAnalyticsChartUnit = 'seconds' | 'count' | 'ratio' | string;

export interface MobileAnalyticsChartPoint {
  key: string;
  label: string;
  value: number;
  foregroundSeconds?: number;
  lifeCategory?: MobileLifeCategory | string | null;
  packageName?: string | null;
  localDate?: string | null;
  localHour?: number | null;
}

export interface MobileAnalyticsChart {
  key: string;
  title: string;
  chartType: MobileAnalyticsChartType;
  unit: MobileAnalyticsChartUnit;
  points: MobileAnalyticsChartPoint[];
}

export interface MobileTimelineBlockApp {
  packageName: string;
  displayName: string;
  foregroundSeconds: number;
}

export interface MobileTimelineBlock {
  id: string;
  startUtc: string;
  endUtc: string;
  localStart: string;
  localEnd: string;
  lifeCategory: MobileLifeCategory | string;
  foregroundSeconds: number;
  sessionCount: number;
  appCount: number;
  topApps: MobileTimelineBlockApp[];
  qualityFlags: string[];
  sourceMix?: Record<string, number>;
  includesSystemNoise?: boolean;
}

export interface MobileTimelineBlockPage {
  items: MobileTimelineBlock[];
  nextCursor: string | null;
  hasMore: boolean;
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface MobileTimelineBlockSession {
  id: string;
  deviceId: string;
  packageName: string;
  displayName: string;
  startUtc: string;
  endUtc: string;
  durationSeconds: number;
  lifeCategory: MobileLifeCategory | string;
  source: MobileUsageSource;
  confidence: number;
  qualityFlags: string[];
}

export interface MobileSessionEvent {
  id: string;
  sessionId: string;
  deviceId: string;
  packageName: string;
  eventType: string;
  eventTimeUtc: string;
  className: string | null;
  rawJson: string;
}

export interface MobileAppCatalogOverride {
  packageName: string;
  displayNameOverride: string | null;
  lifeCategory: MobileLifeCategory | string;
  isSystemNoise: boolean;
  hideShortEvents: boolean;
  createdAt?: string;
  updatedAt?: string;
}

export interface MobileAppCategoryRule {
  id: string;
  ruleType: string;
  pattern: string;
  lifeCategory: MobileLifeCategory | string;
  priority: number;
  isEnabled: boolean;
  displayNameOverride?: string | null;
  isSystemNoise?: boolean | null;
  createdAt?: string;
  updatedAt?: string;
}

export type MobileAppCategoryRuleUpsertRequest = Omit<
  MobileAppCategoryRule,
  'id' | 'createdAt' | 'updatedAt'
> & {
  id?: string;
};

export type MobileUsageGoalScope = 'total-daily' | 'category-daily' | 'app-daily' | string;

export interface MobileUsageGoal {
  id: string;
  scope: MobileUsageGoalScope;
  packageName: string | null;
  lifeCategory: MobileLifeCategory | string | null;
  label: string;
  limitSeconds: number;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MobileUsageGoalUpsertRequest {
  scope: MobileUsageGoalScope;
  packageName?: string | null;
  lifeCategory?: MobileLifeCategory | string | null;
  label: string;
  limitSeconds: number;
  isEnabled: boolean;
}

export function getMobileDevices(): Promise<MobileDevice[]> {
  return apiGet<ApiResponse<MobileDevice[]>>(mobileApiPaths.devices).then(r => r.data);
}

export function getMobileSummary(date: string, deviceId?: string): Promise<MobileSummary> {
  return apiGet<ApiResponse<MobileSummary>>(mobileApiPaths.summary(date, deviceId)).then(r => r.data);
}

export function getMobileTimeline(
  date: string,
  deviceId?: string,
  page?: number,
  pageSize?: number,
): Promise<MobileTimeline> {
  return apiGet<ApiResponse<MobileTimeline>>(mobileApiPaths.timeline(date, deviceId, page, pageSize)).then(r => r.data);
}

export function getMobileLocationHistory(params: MobileLocationHistoryParams): Promise<MobileLocationHistory> {
  return apiGet<ApiResponse<MobileLocationHistory>>(mobileApiPaths.locationHistory(params)).then(r => r.data);
}

export function getMobileLocationAnalyticsOverview(
  query: MobileLocationAnalyticsParams = {},
): Promise<MobileLocationAnalyticsOverview> {
  return apiGet<ApiResponse<MobileLocationAnalyticsOverview>>(
    mobileApiPaths.locationAnalyticsOverview(query),
  ).then(r => r.data);
}

export function getMobileLocationAnalyticsTracks(
  query: MobileLocationAnalyticsParams = {},
): Promise<MobileLocationTrack[]> {
  return apiGet<ApiResponse<MobileLocationTrack[]>>(mobileApiPaths.locationAnalyticsTracks(query)).then(r => r.data);
}

export function getMobileLocationAnalyticsSegment(segmentId: string): Promise<MobileLocationSegment> {
  return apiGet<ApiResponse<MobileLocationSegment>>(mobileApiPaths.locationAnalyticsSegment(segmentId)).then(r => r.data);
}

export function getMobileLocationAnalyticsSegmentPoints(
  segmentId: string,
  query: MobileLocationAnalyticsParams = {},
): Promise<MobileLocationSegmentPointPage> {
  return apiGet<ApiResponse<MobileLocationSegmentPointPage>>(
    mobileApiPaths.locationAnalyticsSegmentPoints(segmentId, query),
  ).then(r => r.data);
}

export function getMobileFrequentPlaces(
  query: MobileLocationAnalyticsParams = {},
): Promise<MobileFrequentPlacesResponse> {
  return apiGet<ApiResponse<MobileFrequentPlacesResponse>>(
    mobileApiPaths.locationAnalyticsFrequentPlaces(query),
  ).then(r => r.data);
}

export function getMobileMovementStats(
  query: MobileLocationAnalyticsParams = {},
): Promise<MobileMovementStatsResponse> {
  return apiGet<ApiResponse<MobileMovementStatsResponse>>(
    mobileApiPaths.locationAnalyticsMovementStats(query),
  ).then(r => r.data);
}

export function getMobileQuality(date?: string, deviceId?: string): Promise<MobileQuality> {
  return apiGet<ApiResponse<MobileQuality>>(mobileApiPaths.quality(date, deviceId)).then(r => r.data);
}

export function getMobileAnalyticsOverview(
  query: MobileAnalyticsQuery = {},
): Promise<MobileAnalyticsOverview> {
  return apiGet<ApiResponse<MobileAnalyticsOverview>>(mobileApiPaths.analyticsOverview(query)).then(r => r.data);
}

export function getMobileAnalyticsHeatmap(query: MobileAnalyticsQuery = {}): Promise<MobileHeatmapBucket[]> {
  return apiGet<ApiResponse<MobileHeatmapBucket[]>>(mobileApiPaths.analyticsHeatmap(query)).then(r => r.data);
}

export const getMobileHeatmap = getMobileAnalyticsHeatmap;

export function getMobileAnalyticsCharts(query: MobileAnalyticsQuery = {}): Promise<MobileAnalyticsChart[]> {
  return apiGet<ApiResponse<MobileAnalyticsChart[]>>(mobileApiPaths.analyticsCharts(query)).then(r => r.data);
}

export function getMobileAnalyticsTimelineBlocks(
  query: MobileAnalyticsQuery = {},
): Promise<MobileTimelineBlockPage> {
  return apiGet<ApiResponse<MobileTimelineBlockPage>>(mobileApiPaths.analyticsTimelineBlocks(query)).then(r => r.data);
}

export function getMobileTimelineBlockSessions(
  blockId: string,
  query: MobileAnalyticsQuery = {},
): Promise<MobileTimelineBlockSession[]> {
  return apiGet<ApiResponse<MobileTimelineBlockSession[]>>(
    mobileApiPaths.timelineBlockSessions(blockId, query),
  ).then(r => r.data);
}

export function getMobileSessionEvents(sessionId: string): Promise<MobileSessionEvent[]> {
  return apiGet<ApiResponse<MobileSessionEvent[]>>(mobileApiPaths.sessionEvents(sessionId)).then(r => r.data);
}

export function getMobileLivenessOverview(query: MobileLivenessQuery = {}): Promise<MobileLivenessOverview> {
  return apiGet<ApiResponse<MobileLivenessOverview>>(mobileApiPaths.livenessOverview(query)).then(r => r.data);
}

export function getMobileLivenessEvents(
  deviceId: string,
  query: MobileLivenessEventsQuery = {},
): Promise<MobileLivenessEventPage> {
  return apiGet<ApiResponse<MobileLivenessEventPage>>(
    mobileApiPaths.livenessEvents(deviceId, query),
  ).then(r => r.data);
}

export function getMobileAppCatalogOverrides(): Promise<MobileAppCatalogOverride[]> {
  return apiGet<ApiResponse<MobileAppCatalogOverride[]>>(mobileApiPaths.appCatalogOverrides()).then(r => r.data);
}

export function saveMobileAppCatalogOverride(
  override: MobileAppCatalogOverride,
): Promise<MobileAppCatalogOverride> {
  return apiPut<ApiResponse<MobileAppCatalogOverride>>(
    mobileApiPaths.appCatalogOverride(override.packageName),
    override,
  ).then(r => r.data);
}

export function deleteMobileAppCatalogOverride(packageName: string): Promise<string> {
  return apiDelete<ApiResponse<string>>(mobileApiPaths.appCatalogOverride(packageName)).then(r => r.data);
}

export function getMobileAppCategoryRules(): Promise<MobileAppCategoryRule[]> {
  return apiGet<ApiResponse<MobileAppCategoryRule[]>>(mobileApiPaths.appCategoryRules()).then(r => r.data);
}

export function createMobileAppCategoryRule(
  rule: MobileAppCategoryRuleUpsertRequest,
): Promise<MobileAppCategoryRule> {
  return apiPost<ApiResponse<MobileAppCategoryRule>>(mobileApiPaths.appCategoryRules(), rule).then(r => r.data);
}

export function updateMobileAppCategoryRule(
  id: string,
  rule: MobileAppCategoryRuleUpsertRequest,
): Promise<MobileAppCategoryRule> {
  return apiPut<ApiResponse<MobileAppCategoryRule>>(mobileApiPaths.appCategoryRule(id), rule).then(r => r.data);
}

export function deleteMobileAppCategoryRule(id: string): Promise<string> {
  return apiDelete<ApiResponse<string>>(mobileApiPaths.appCategoryRule(id)).then(r => r.data);
}

export function getMobileUsageGoals(): Promise<MobileUsageGoal[]> {
  return apiGet<ApiResponse<MobileUsageGoal[]>>(mobileApiPaths.usageGoals()).then(r => r.data);
}

export function saveMobileUsageGoal(goal: MobileUsageGoalUpsertRequest): Promise<MobileUsageGoal> {
  return apiPost<ApiResponse<MobileUsageGoal>>(mobileApiPaths.usageGoals(), goal).then(r => r.data);
}

export function deleteMobileUsageGoal(id: string): Promise<string> {
  return apiDelete<ApiResponse<string>>(mobileApiPaths.usageGoal(id)).then(r => r.data);
}

export interface DeviceListItem {
  deviceId: string; displayName: string; brand: string; model: string; osVersion: string; appVersion: string;
  registeredAtUtc: string; lastSeenAtUtc: string; isOnline: boolean;
  sessionCount: number; eventCount: number; locationCount: number; summaryCount: number;
  earliest: string | null; latest: string | null; storageEstimateKb: number;
  syncStatus: string; dataQuality: string; storagePressure: string;
}
export function getManagedDevices(sortBy?: string): Promise<DeviceListItem[]> {
  return apiGet<ApiResponse<DeviceListItem[]>>(withQuery('/mobile/devices/manage', [['sortBy', sortBy]])).then(r => r.data);
}
export function renameDevice(deviceId: string, displayName: string): Promise<void> {
  return apiPost(`/mobile/devices/${pathSegment(deviceId)}/rename`, { displayName }).then(() => {});
}
export function previewMerge(sourceDeviceIds: string[], targetDeviceId: string): Promise<any> {
  return apiPost('/mobile/devices/merge/preview', { sourceDeviceIds, targetDeviceId }).then((r: any) => r.data ?? r);
}
export function mergeDevices(sourceDeviceIds: string[], targetDeviceId: string): Promise<void> {
  return apiPost('/mobile/devices/merge', { sourceDeviceIds, targetDeviceId }).then(() => {});
}
export function previewDeleteDevice(deviceId: string): Promise<any> {
  return apiGet(`/mobile/devices/${pathSegment(deviceId)}/delete-preview`).then((r: any) => r.data ?? r);
}
export function deleteDevice(deviceId: string): Promise<void> { return apiDelete(`/mobile/devices/${pathSegment(deviceId)}`).then(()=>{}); }
export function getDeviceDetail(deviceId: string): Promise<any> { return apiGet(`/mobile/devices/${pathSegment(deviceId)}/detail`).then((r:any)=>r.data ?? r); }
export function exportDevice(deviceId: string): Promise<Blob> { return fetch(`/api/v1/mobile/devices/${pathSegment(deviceId)}/export`, { headers: { Authorization: `Bearer ${localStorage.getItem('accessToken')||localStorage.getItem('pim_token')||''}` } }).then(r=>r.blob()); }
