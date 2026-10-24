import { apiGet, apiPost } from '@/api/client'
import type { components } from '@/api/generated'
import type {
  CategoryDictionaryItem,
  ContextSuggestion,
  LabelingQueueItem,
  PcActivityAnalysis,
  PcAppUsageItem,
  PcCategoryDistributionItem,
  PcSummaryResponse,
  ProductivityDashboard,
  SiteDailyRow,
  SiteSummary,
  SiteTimelineRow,
} from './types'

export interface AppKnowledgePreview {
  recommendation: { categoryName: string | null; categoryPath: string | null }
  preview: {
    affectedRecordCount: number
    affectedDurationSeconds: number
    currentCategoryCounts: Record<string, number>
    newCategoryCounts: Record<string, number>
    requiresConfirmation: boolean
    summary: string
  }
}

export const pcApi = {
  summary: (date: string, force = false) =>
    apiGet<PcSummaryResponse>(`/api/v1/pc/summary?date=${date}${force ? '&force=true' : ''}`),
  heatmapGrid: (params: { start: string; end: string; dimension: string; force?: boolean }) =>
    apiGet<import('./types').HeatmapGridResponse>(
      `/api/v1/pc/heatmap/grid?start=${params.start}&end=${params.end}&dimension=${params.dimension}${params.force ? '&force=true' : ''}`,
    ),
  activityAnalysis: (date: string, blockMinutes = 60, force = false) =>
    apiGet<PcActivityAnalysis>(
      `/api/v1/pc/activity-analysis?date=${date}&blockMinutes=${blockMinutes}${force ? '&force=true' : ''}`,
    ),
  /** 键鼠范围聚合（GAP-1：与单日 keystats 同构，字段不随天数膨胀） */
  keystatsRange: (params: { start: string; end: string; force?: boolean }) =>
    apiGet<PcSummaryResponse['keystats']>(
      `/api/v1/pc/aggregation/keystats?start=${params.start}&end=${params.end}${params.force ? '&force=true' : ''}`,
    ),
  /** 聚合组公共参数（date 或 start/end 二选一 + timezone + force） */
  appUsage: (params: { date?: string; start?: string; end?: string; timezone?: string; limit?: number; force?: boolean }) => {
    const q = new URLSearchParams()
    if (params.date) q.set('date', params.date)
    if (params.start) q.set('start', params.start)
    if (params.end) q.set('end', params.end)
    if (params.timezone) q.set('timezone', params.timezone)
    if (params.limit) q.set('limit', String(params.limit))
    if (params.force) q.set('force', 'true')
    return apiGet<{ items: PcAppUsageItem[]; totalMinutes: number }>(`/api/v1/pc/aggregation/app-usage?${q}`)
  },
  categoryDistribution: (params: { date?: string; start?: string; end?: string; timezone?: string; force?: boolean }) => {
    const q = new URLSearchParams()
    if (params.date) q.set('date', params.date)
    if (params.start) q.set('start', params.start)
    if (params.end) q.set('end', params.end)
    if (params.timezone) q.set('timezone', params.timezone)
    if (params.force) q.set('force', 'true')
    return apiGet<{ items: PcCategoryDistributionItem[] }>(`/api/v1/pc/aggregation/category-distribution?${q}`)
  },
  /** 专注块（02 §pc-tracker：今日页 PC 概览 / PC 追踪总览页使用） */
  focusBlocks: (params: { date?: string; start?: string; end?: string; timezone?: string; force?: boolean }) => {
    const q = new URLSearchParams()
    if (params.date) q.set('date', params.date)
    if (params.start) q.set('start', params.start)
    if (params.end) q.set('end', params.end)
    if (params.timezone) q.set('timezone', params.timezone)
    if (params.force) q.set('force', 'true')
    return apiGet<{ items: { startUtc: string; endUtc: string; startLocal: string; endLocal: string; durationMinutes: number; mainApp: string; topApps: { name: string; minutes: number }[] }[] }>(
      `/api/v1/pc/aggregation/focus-blocks?${q}`,
    )
  },
  /** 深夜使用（按业务日） */
  lateNight: (params: { date?: string; start?: string; end?: string; timezone?: string; force?: boolean }) => {
    const q = new URLSearchParams()
    if (params.date) q.set('date', params.date)
    if (params.start) q.set('start', params.start)
    if (params.end) q.set('end', params.end)
    if (params.timezone) q.set('timezone', params.timezone)
    if (params.force) q.set('force', 'true')
    return apiGet<{ items: { date: string; minutes: number; hadActivity: boolean }[] }>(
      `/api/v1/pc/aggregation/late-night?${q}`,
    )
  },
  productivity: (date: string, force = false) =>
    apiGet<ProductivityDashboard>(`/api/v1/pc/productivity/dashboard?date=${date}${force ? '&force=true' : ''}`),
  classificationQueue: (limit: number, mode = 'queue') =>
    apiGet<{ items: LabelingQueueItem[] }>(`/api/v1/pc/classification/queue?limit=${limit}&mode=${mode}`),
  label: (body: components['schemas']['ActivityLabelingRequest']) =>
    apiPost<{ ok: boolean; categoryId: string | null; categoryName: string | null; created: string }>(
    '/api/v1/pc/classification/label',
    body,
  ),
  categoryDictionary: () => apiGet<CategoryDictionaryItem[]>('/api/v1/pc/categories/dictionary'),

  /** 浏览器站点（browser-tt） */
  browserSummary: (params: { date?: string; from?: string; to?: string }) => {
    const q = new URLSearchParams()
    if (params.date) q.set('date', params.date)
    if (params.from) q.set('from', params.from)
    if (params.to) q.set('to', params.to)
    return apiGet<SiteSummary>(`/api/v1/pc/browser-tt/summary?${q}`)
  },
  browserDaily: (params: { from: string; to: string }) =>
    apiGet<SiteDailyRow[]>(`/api/v1/pc/browser-tt/daily?from=${params.from}&to=${params.to}`),
  browserTimeline: (date: string) => apiGet<SiteTimelineRow[]>(`/api/v1/pc/browser-tt/timeline?date=${date}`),
  browserImport: (body: components['schemas']['SiteImportRequest']) =>
    apiPost<{ rows: number; dates: number; hosts: number; skipped: number; format: string }>(
      '/api/v1/pc/browser-tt/import',
      body,
    ),

  /** 上下文建议（app-knowledge 同构预览/应用） */
  suggestions: (date: string) =>
    apiGet<ContextSuggestion[]>(`/api/v1/pc/classification/suggestions?date=${date}`),
  suggestReject: (id: string) =>
    apiPost<string>(`/api/v1/pc/classification/suggestions/${id}/reject`, {}),
  suggestPreview: (id: string, body: components['schemas']['SuggestionClassificationPreviewRequest']) =>
    apiPost<AppKnowledgePreview>(
      `/api/v1/pc/app-knowledge/suggestions/${id}/preview`,
      body,
    ),
  suggestApply: (id: string, body: components['schemas']['SuggestionClassificationApplyRequest']) =>
    apiPost<{ message: string }>(`/api/v1/pc/app-knowledge/suggestions/${id}/apply`, body),
}
