import { apiGet, apiPost, apiPut, apiDelete } from './client';
import type { ApiResponse } from '../types';
import type {
  PcSummaryResponse,
  DetailQueryParams, DetailQueryResponse,
  AppCategoryRule, HeatmapGridResponse,
  ActivityClassificationRule, ActivityClassificationSuggestion,
  ActivityClassificationApplyRange, ActivityClassificationPreview,
  ActivityClassificationSettings, SaveActivityClassificationRuleRequest,
  ActivityClassificationSuggestionPreview, ActivityClassificationSuggestionApply,
  SuggestionClassificationPreviewRequest, SuggestionClassificationApplyRequest,
  PcActivityAnalysisResponse,
  KeystatsSummary,
  PcQualityResponse, PcQualityQueryParams, PcQualityComponent, PcQualityIssue,
  PimHealthStatus
} from '../types';

export function getPcSummary(date: string) {
  return apiGet<ApiResponse<PcSummaryResponse>>(`/pc/summary?date=${date}`).then(r => r.data);
}

export function getPcHeatmapGrid(start: string, end: string, dimension: string) {
  return apiGet<ApiResponse<HeatmapGridResponse>>(
    `/pc/heatmap/grid?start=${start}&end=${end}&dimension=${dimension}`
  ).then(r => r.data);
}

export function queryPcDetail(params: DetailQueryParams) {
  const searchParams = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v !== undefined && v !== null && v !== '') searchParams.set(k, String(v));
  });
  return apiGet<ApiResponse<DetailQueryResponse>>(`/pc/detail?${searchParams.toString()}`).then(r => r.data);
}

const healthStatusByNumber: Record<number, PimHealthStatus> = {
  0: 'Unknown',
  1: 'Healthy',
  2: 'Warning',
  3: 'Critical',
};

const healthStatusNames = new Set<PimHealthStatus>(['Unknown', 'Healthy', 'Warning', 'Critical']);

const healthStatusLabels: Record<PimHealthStatus, string> = {
  Unknown: '未知',
  Healthy: '正常',
  Warning: '有警告',
  Critical: '故障',
};

type RawPcQualityComponent = Omit<PcQualityComponent, 'status' | 'message' | 'details'> & {
  status: unknown;
  message: unknown;
  details: unknown;
};

type RawPcQualityIssue = Omit<PcQualityIssue, 'severity' | 'message' | 'nextStep'> & {
  severity: unknown;
  message: unknown;
  nextStep: unknown;
};

type RawPcQuality = {
  overallStatus?: unknown;
  label?: unknown;
  message?: unknown;
  checkedAt?: unknown;
  components?: unknown;
  issues?: unknown;
  nextSteps?: unknown;
};

function textOrEmpty(value: unknown): string {
  if (value === null || value === undefined) return '';
  return String(value);
}

function normalizeHealthStatus(value: unknown): PimHealthStatus {
  if (typeof value === 'number') return healthStatusByNumber[value] ?? 'Unknown';
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (/^\d+$/.test(trimmed)) return healthStatusByNumber[Number(trimmed)] ?? 'Unknown';
    if (healthStatusNames.has(trimmed as PimHealthStatus)) return trimmed as PimHealthStatus;
  }
  return 'Unknown';
}

function getHealthStatusLabel(status: PimHealthStatus) {
  return healthStatusLabels[status] ?? healthStatusLabels.Unknown;
}

function normalizeQualityLabel(value: unknown, status: PimHealthStatus): string {
  const label = textOrEmpty(value).trim();
  if (!label) return getHealthStatusLabel(status);
  return healthStatusNames.has(label as PimHealthStatus) ? getHealthStatusLabel(status) : label;
}

function normalizeDetails(details: unknown): Record<string, string> {
  if (!details || typeof details !== 'object' || Array.isArray(details)) return {};

  return Object.fromEntries(
    Object.entries(details).map(([key, value]) => [key, textOrEmpty(value)])
  );
}

function normalizeQualityComponent(raw: unknown): PcQualityComponent {
  const component = (raw && typeof raw === 'object' ? raw : {}) as Partial<RawPcQualityComponent>;

  return {
    key: textOrEmpty(component.key),
    name: textOrEmpty(component.name),
    status: normalizeHealthStatus(component.status),
    message: textOrEmpty(component.message),
    details: normalizeDetails(component.details),
  };
}

function normalizeQualityIssue(raw: unknown): PcQualityIssue {
  const issue = (raw && typeof raw === 'object' ? raw : {}) as Partial<RawPcQualityIssue>;
  const nextStep = issue.nextStep === null || issue.nextStep === undefined
    ? null
    : String(issue.nextStep);

  return {
    code: textOrEmpty(issue.code),
    severity: normalizeHealthStatus(issue.severity),
    componentKey: textOrEmpty(issue.componentKey),
    message: textOrEmpty(issue.message),
    nextStep,
  };
}

export function normalizePcQuality(raw: unknown): PcQualityResponse {
  const quality = (raw && typeof raw === 'object' ? raw : {}) as RawPcQuality;
  const overallStatus = normalizeHealthStatus(quality.overallStatus);

  return {
    overallStatus,
    label: normalizeQualityLabel(quality.label, overallStatus),
    message: textOrEmpty(quality.message),
    checkedAt: textOrEmpty(quality.checkedAt),
    components: Array.isArray(quality.components)
      ? quality.components.map(normalizeQualityComponent)
      : [],
    issues: Array.isArray(quality.issues)
      ? quality.issues.map(normalizeQualityIssue)
      : [],
    nextSteps: Array.isArray(quality.nextSteps)
      ? quality.nextSteps.map(textOrEmpty).filter(Boolean)
      : [],
  };
}

export function getPcQuality(params: PcQualityQueryParams = {}) {
  const searchParams = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v !== undefined && v !== null && v !== '') searchParams.set(k, String(v));
  });
  const query = searchParams.toString();
  const path = query ? `/pc/quality?${query}` : '/pc/quality';
  return apiGet<ApiResponse<unknown>>(path).then(r => normalizePcQuality(r.data));
}

export function getPcCategories() {
  return apiGet<ApiResponse<AppCategoryRule[]>>('/pc/categories').then(r => r.data);
}

export const pcClassificationApiPaths = {
  rules: '/pc/classification/rules',
  preview: '/pc/classification/rules/preview',
  apply: '/pc/classification/rules/apply',
  suggestions: (date: string) => `/pc/classification/suggestions?date=${date}`,
  suggestionPreview: (id: string) => `/pc/classification/suggestions/${id}/preview`,
  suggestionApply: (id: string) => `/pc/classification/suggestions/${id}/apply`,
  recompute: '/pc/classification/recompute',
  settings: '/pc/classification/settings',
  recentProjectTags: '/pc/classification/project-tags/recent',
} as const;

export function pcActivityAnalysisApiPath(date: string, blockMinutes = 60) {
  return `/pc/activity-analysis?date=${date}&blockMinutes=${blockMinutes}`;
}

export function getActivityClassificationRules() {
  return apiGet<ApiResponse<ActivityClassificationRule[]>>(pcClassificationApiPaths.rules).then(r => r.data);
}

export function getActivityClassificationSuggestions(date: string) {
  return apiGet<ApiResponse<ActivityClassificationSuggestion[]>>(
    pcClassificationApiPaths.suggestions(date)
  ).then(r => r.data);
}

export function rejectActivityClassificationSuggestion(id: string) {
  return apiPost<ApiResponse<string>>(`/pc/classification/suggestions/${id}/reject`, {})
    .then(r => r.data);
}

export function acceptActivityClassificationSuggestion(
  id: string,
  data: {
    ruleName: string;
    scope: string;
    categoryName: string | null;
    conditionsJson: string;
  }
) {
  return apiPost<ApiResponse<ActivityClassificationRule>>(
    `/pc/classification/suggestions/${id}/accept`,
    {
      ruleName: data.ruleName,
      scope: data.scope,
      categoryName: data.categoryName,
      conditionsJson: data.conditionsJson,
    }
  ).then(r => r.data);
}

export function previewActivityClassificationSuggestion(
  id: string,
  request: SuggestionClassificationPreviewRequest
) {
  return apiPost<ApiResponse<ActivityClassificationSuggestionPreview>>(
    pcClassificationApiPaths.suggestionPreview(id),
    request
  ).then(r => r.data);
}

export function applyActivityClassificationSuggestion(
  id: string,
  request: SuggestionClassificationApplyRequest
) {
  return apiPost<ApiResponse<ActivityClassificationSuggestionApply>>(
    pcClassificationApiPaths.suggestionApply(id),
    request
  ).then(r => r.data);
}

export function previewActivityClassificationRule(
  rule: SaveActivityClassificationRuleRequest,
  range: ActivityClassificationApplyRange
) {
  return apiPost<ApiResponse<ActivityClassificationPreview>>(
    pcClassificationApiPaths.preview,
    { rule, range }
  ).then(r => r.data);
}

export function applyActivityClassificationRule(
  rule: SaveActivityClassificationRuleRequest,
  range: ActivityClassificationApplyRange
) {
  return apiPost<ApiResponse<ActivityClassificationPreview>>(
    pcClassificationApiPaths.apply,
    { rule, range }
  ).then(r => r.data);
}

export function getActivityClassificationSettings() {
  return apiGet<ApiResponse<ActivityClassificationSettings>>(pcClassificationApiPaths.settings).then(r => r.data);
}

export function saveActivityClassificationSettings(minutes: number) {
  return apiPut<ApiResponse<ActivityClassificationSettings>>(
    pcClassificationApiPaths.settings,
    { recommendedMinimumClassificationDurationMinutes: minutes }
  ).then(r => r.data);
}

export function getRecentActivityProjectTags() {
  return apiGet<ApiResponse<string[]>>(pcClassificationApiPaths.recentProjectTags).then(r => r.data);
}

export function getPcActivityAnalysis(date: string, blockMinutes = 60) {
  return apiGet<ApiResponse<PcActivityAnalysisResponse>>(
    pcActivityAnalysisApiPath(date, blockMinutes)
  ).then(r => r.data);
}

export function savePcCategory(rule: { appPattern: string; categoryName: string; color: string; priority: number }) {
  return apiPost<ApiResponse<AppCategoryRule>>('/pc/categories', rule).then(r => r.data);
}

export function deletePcCategory(id: string) {
  return apiDelete<ApiResponse<string>>(`/pc/categories/${id}`).then(r => r.data);
}

// === Phase 2: 分类树 API ===
export interface CategoryTreeNode {
  id: string;
  parentId: string | null;
  name: string;
  color: string;
  icon: string | null;
  productivity: string;
  sortOrder: number;
  isBuiltin: boolean;
  children: CategoryTreeNode[];
}

export interface CategorySaveRequest {
  id?: string;
  parentId?: string | null;
  name: string;
  color: string;
  icon?: string | null;
  productivity: string;
  sortOrder: number;
}

export function getCategoryTree() {
  return apiGet<ApiResponse<CategoryTreeNode[]>>('/pc/categories/tree').then(r => r.data);
}

export function saveCategory(req: CategorySaveRequest) {
  return apiPost<ApiResponse<CategoryTreeNode>>('/pc/categories', req).then(r => r.data);
}

export function deleteCategory(id: string) {
  return apiDelete<ApiResponse<string>>(`/pc/categories/${id}`).then(r => r.data);
}

export function seedCategories() {
  return apiPost<ApiResponse<string>>('/pc/categories/seed', {}).then(r => r.data);
}

// === Phase 2: Productivity API ===
export interface ProductivityDashboard {
  todayScore: number;
  productiveHours: number;
  distractingHours: number;
  neutralHours: number;
  targetHours: number;
  goalMet: boolean;
  weeklyTrend: DailyProductivity[];
}

export interface DailyProductivity {
  date: string;
  productiveMinutes: number;
  distractingMinutes: number;
  neutralMinutes: number;
  totalMinutes: number;
  productiveRatio: number;
}

export function getProductivityDashboard(date: string) {
  return apiGet<ApiResponse<ProductivityDashboard>>(`/pc/productivity/dashboard?date=${date}`).then(r => r.data);
}

export function getProductivityRange(start: string, end: string) {
  return apiGet<ApiResponse<DailyProductivity[]>>(`/pc/productivity/range?start=${start}&end=${end}`).then(r => r.data);
}

// === Phase 2: 时间线 v2 API ===
// 业务日固定为 Asia/Shanghai 04:00 起算（服务端决定，不接受 timezone 参数）；
// start/end 为带 +08:00 偏移的 ISO-8601，块之间互不重叠（#235 / #236 / #237）。
export interface TimelineV2Item {
  start: string;
  end: string;
  appName: string;
  appDisplayName?: string | null;
  windowTitle: string | null;
  categoryName: string;
  categoryColor: string | null;
  productivity: string;
  confidence: number;
  durationMinutes: number;
}

export function getTimelineV2(date: string) {
  return apiGet<ApiResponse<TimelineV2Item[]>>(`/pc/timeline/v2?date=${date}`).then(r => r.data);
}

// === Phase 2: 聚合接口（专注块 / 应用时长 / 深夜使用 / 分类分布）===
export interface PcFocusBlockItem {
  startUtc: string;
  endUtc: string;
  startLocal: string;
  endLocal: string;
  durationMinutes: number;
  mainApp: string;
  topApps: { name: string; minutes: number }[];
}

export interface PcFocusBlocksResponse {
  items: PcFocusBlockItem[];
}

export interface PcAppUsageItem {
  appName: string;
  displayName: string | null;
  totalMinutes: number;
  percentage: number;
}

export interface PcAppUsageResponse {
  items: PcAppUsageItem[];
  totalMinutes: number;
}

export interface PcLateNightDayItem {
  date: string;
  minutes: number;
  hadActivity: boolean;
}

export interface PcLateNightResponse {
  items: PcLateNightDayItem[];
}

export interface PcCategoryDistributionItem {
  categoryName: string;
  color: string;
  minutes: number;
  percentage: number;
}

export interface PcCategoryDistributionResponse {
  items: PcCategoryDistributionItem[];
}

export interface PcAggregationQueryParams {
  date?: string;
  start?: string;
  end?: string;
  timezone?: string;
}

function buildAggregationQuery(params: PcAggregationQueryParams & { limit?: number }): string {
  const searchParams = new URLSearchParams();
  Object.entries(params).forEach(([k, v]) => {
    if (v !== undefined && v !== null && v !== '') searchParams.set(k, String(v));
  });
  const query = searchParams.toString();
  return query ? `?${query}` : '';
}

export const pcAggregationApiPaths = {
  focusBlocks(params: PcAggregationQueryParams = {}): string {
    return `/pc/aggregation/focus-blocks${buildAggregationQuery(params)}`;
  },
  appUsage(params: PcAggregationQueryParams & { limit?: number } = {}): string {
    return `/pc/aggregation/app-usage${buildAggregationQuery(params)}`;
  },
  lateNight(params: PcAggregationQueryParams = {}): string {
    return `/pc/aggregation/late-night${buildAggregationQuery(params)}`;
  },
  categoryDistribution(params: PcAggregationQueryParams = {}): string {
    return `/pc/aggregation/category-distribution${buildAggregationQuery(params)}`;
  },
  /**
   * 键鼠范围聚合（REQ-6，后端 PR #370 新增）。
   * 字段与单日 `summary.keystats` 同构，额外返回 `totalKeyPresses`。
   */
  keystats(params: PcAggregationQueryParams = {}): string {
    return `/pc/aggregation/keystats${buildAggregationQuery(params)}`;
  },
};

/** 范围键鼠聚合结果：与单日同构，无 `date`，多一个范围内的键盘总次数。 */
export type KeystatsRangeSummary = Omit<KeystatsSummary, 'date'> & { totalKeyPresses: number };

/** 键盘热力卡片的数据源：单日取 `summary.keystats`，范围模式取 `/pc/aggregation/keystats`。 */
export type PcKeystatsScope = 'single-day' | 'range';

/**
 * REQ-6：`dimension=hour` 是单日视图，用 `summary.keystats`（带 `date`）；
 * day/month/year 是范围视图（近 30 天 / 近 12 月 / 近 60 月），用范围聚合端点。
 * 范围端点在同一组件下复用（AC-6.2）。
 */
export function pcKeystatsScope(dimension: string): PcKeystatsScope {
  return dimension === 'hour' ? 'single-day' : 'range';
}

export function getPcKeystatsRange(start: string, end: string) {
  return apiGet<ApiResponse<KeystatsRangeSummary>>(
    pcAggregationApiPaths.keystats({ start, end })
  ).then(r => r.data);
}

export function getPcFocusBlocks(params: PcAggregationQueryParams = {}) {
  return apiGet<ApiResponse<PcFocusBlocksResponse>>(pcAggregationApiPaths.focusBlocks(params)).then(r => r.data);
}

export function getPcAppUsage(params: PcAggregationQueryParams & { limit?: number } = {}) {
  return apiGet<ApiResponse<PcAppUsageResponse>>(pcAggregationApiPaths.appUsage(params)).then(r => r.data);
}

export function getPcLateNight(params: PcAggregationQueryParams = {}) {
  return apiGet<ApiResponse<PcLateNightResponse>>(pcAggregationApiPaths.lateNight(params)).then(r => r.data);
}

export function getPcCategoryDistribution(params: PcAggregationQueryParams = {}) {
  return apiGet<ApiResponse<PcCategoryDistributionResponse>>(pcAggregationApiPaths.categoryDistribution(params)).then(r => r.data);
}

// === Classification 2.0 APIs ===
export function getSuggestionsV2() {
  return apiGet<ApiResponse<import('../types').ActivityClassificationSuggestionV2[]>>('/pc/classification/suggestions/v2').then(r => r.data);
}

export function batchAcceptSuggestions(req: import('../types').BatchAcceptSuggestionsRequest) {
  return apiPost<ApiResponse<import('../types').BatchAcceptResult>>('/pc/classification/suggestions/batch-accept', req).then(r => r.data);
}

export function exportAppSignatures() {
  return apiGet<ApiResponse<import('../types').AppSignature[]>>('/pc/app-signatures/export').then(r => r.data);
}

export function importAppSignatures(signatures: Partial<import('../types').AppSignature>[]) {
  return apiPost<ApiResponse<{ imported: number; updated: number }>>('/pc/app-signatures/import', signatures).then(r => r.data);
}

export function lookupAppSignature(processName: string) {
  return apiPost<ApiResponse<import('../types').AppSignature>>('/pc/app-signatures/lookup', { processName }).then(r => r.data);
}

export function getProductivityGoals() {
  return apiGet<ApiResponse<import('../types').ProductivityGoal>>('/pc/productivity/goals').then(r => r.data);
}

export function updateProductivityGoals(goal: import('../types').ProductivityGoal) {
  return apiPut<ApiResponse<import('../types').ProductivityGoal>>('/pc/productivity/goals', goal).then(r => r.data);
}

export function getPcTimelineV2(date?: string) {
  const q = date ? `?date=${encodeURIComponent(date)}` : "";
  return apiGet<ApiResponse<import("../types").TimelineV2Item[]>>(`/pc/timeline/v2${q}`).then(r => r.data);
}

// —— 采集健康（守护进程心跳上报） ——
export type TrackerHealth = {
  deviceId: string;
  status: string;
  uptimeSeconds: number;
  hookActive: boolean;
  pollCount: number;
  sessionsCreated: number;
  eventsUploaded: number;
  uploadFailures: number;
  lastError: string | null;
  browserConnected: boolean;
  browserHeartbeatAgeSeconds: number | null;
  siteConnected: boolean;
  siteLastEventAgeSeconds: number | null;
  siteEventsUploaded: number;
  siteLastError: string | null;
  reportedAt: string;
};

export function getLatestTrackerHealth() {
  return apiGet<ApiResponse<TrackerHealth>>('/pc/tracker/health/latest').then(r => r.data);
}
