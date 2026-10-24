import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { deferredIntervalMs } from '@/lib/polling'
import { pcApi } from './api'

export const pcKeys = {
  summary: (date: string) => ['pc', 'summary', date] as const,
  heatmap: (start: string, end: string, dim: string) => ['pc', 'heatmap', start, end, dim] as const,
  activity: (date: string) => ['pc', 'activity-analysis', date] as const,
  appUsage: (key: string) => ['pc', 'app-usage', key] as const,
  categories: (key: string) => ['pc', 'categories', key] as const,
  productivity: (date: string) => ['pc', 'productivity', date] as const,
  queue: (limit: number) => ['pc', 'labeling-queue', limit] as const,
  dictionary: ['pc', 'category-dictionary'] as const,
  suggestions: (date: string) => ['pc', 'suggestions', date] as const,
  browserSummary: (key: string) => ['pc', 'browser', 'summary', key] as const,
  browserDaily: (from: string, to: string) => ['pc', 'browser', 'daily', from, to] as const,
  browserTimeline: (date: string) => ['pc', 'browser', 'timeline', date] as const,
}

/*
 * 聚合类接口（app-usage / category-distribution / focus-blocks / late-night）
 * 同时支持 date 单日与 start&end 范围两种模式（见 05/pc-tracker.md），
 * 故统一用 Scope 表达，缓存键取 "date" 或 "start~end"。
 */
export interface PcScope {
  date?: string
  start?: string
  end?: string
}

export function scopeKey(scope: PcScope): string {
  return scope.start && scope.end ? `${scope.start}~${scope.end}` : (scope.date ?? '')
}

export function usePcSummary(date: string, force = false, enabled = true) {
  return useQuery({
    queryKey: [...pcKeys.summary(date), force],
    queryFn: () => pcApi.summary(date, force),
    enabled,
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useHeatmapGrid(start: string, end: string, dimension: string, enabled = true, force = false) {
  return useQuery({
    queryKey: [...pcKeys.heatmap(start, end, dimension), force],
    queryFn: () => pcApi.heatmapGrid({ start, end, dimension, force }),
    enabled,
  })
}

/** 键鼠范围聚合（GAP-1：30/90 天一次请求，字段与单日 keystats 同构） */
export function useKeystatsRange(start: string, end: string, enabled = true, force = false) {
  return useQuery({
    queryKey: [...pcKeys.heatmap(start, end, 'keystats'), force],
    queryFn: () => pcApi.keystatsRange({ start, end, force }),
    enabled,
  })
}

export function useActivityAnalysis(date: string, enabled = true, force = false) {
  return useQuery({
    queryKey: [...pcKeys.activity(date), force],
    queryFn: () => pcApi.activityAnalysis(date, 60, force),
    enabled,
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useAppUsage(scope: PcScope, enabled = true) {
  const key = scopeKey(scope)
  return useQuery({
    queryKey: pcKeys.appUsage(key),
    queryFn: () => pcApi.appUsage({ ...scope, timezone: 'Asia/Shanghai', limit: 8 }),
    enabled,
  })
}

export function useCategoryDistribution(scope: PcScope, enabled = true) {
  const key = scopeKey(scope)
  return useQuery({
    queryKey: pcKeys.categories(key),
    queryFn: () => pcApi.categoryDistribution({ ...scope, timezone: 'Asia/Shanghai' }),
    enabled,
  })
}

/** 专注块（今日页/总览页用；支持单日与范围） */
export function useFocusBlocks(scope: PcScope, enabled = true) {
  const key = scopeKey(scope)
  return useQuery({
    queryKey: ['pc', 'focus-blocks', key],
    queryFn: () => pcApi.focusBlocks({ ...scope, timezone: 'Asia/Shanghai' }),
    enabled,
  })
}

/** 深夜使用（总览页用；支持单日与范围） */
export function useLateNight(scope: PcScope, enabled = true) {
  const key = scopeKey(scope)
  return useQuery({
    queryKey: ['pc', 'late-night', key],
    queryFn: () => pcApi.lateNight({ ...scope, timezone: 'Asia/Shanghai' }),
    enabled,
  })
}

export function useProductivity(date: string, enabled = true, force = false) {
  return useQuery({
    queryKey: [...pcKeys.productivity(date), force],
    queryFn: () => pcApi.productivity(date, force),
    enabled,
  })
}

export function useLabelingQueue(limit: number) {
  return useQuery({
    queryKey: pcKeys.queue(limit),
    queryFn: () => pcApi.classificationQueue(limit),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useCategoryDictionary() {
  return useQuery({
    queryKey: pcKeys.dictionary,
    queryFn: () => pcApi.categoryDictionary(),
    staleTime: 60_000,
  })
}

export function useContextSuggestions(date: string) {
  return useQuery({
    queryKey: pcKeys.suggestions(date),
    queryFn: () => pcApi.suggestions(date),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useLabelMutation() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { targetType: string; target: string; categoryName?: string; categoryId?: string; scope: 'all' | 'keyword' }) =>
      pcApi.label(body),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['pc'] })
      void qc.invalidateQueries({ queryKey: ['today'] })
    },
  })
}

export function useSuggestionActions() {
  const qc = useQueryClient()
  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ['pc'] })
    void qc.invalidateQueries({ queryKey: ['today'] })
  }
  /*
   * range 必须带 dateFrom/dateTo：后端 today 模式要求两者明确且相同
   * （缺失时报「今天模式需要 DateFrom 和 DateTo 明确且相同。」）。
   * date 为业务日（+08:00 口径），即重算该日的分类影响面。
   */
  const rangeFor = (date: string) => ({ mode: 'today' as const, dateFrom: date, dateTo: date })
  return {
    reject: useMutation({ mutationFn: (id: string) => pcApi.suggestReject(id), onSuccess: invalidate }),
    preview: useMutation({
      mutationFn: ({ id, categoryName, date }: { id: string; categoryName?: string; date: string }) =>
        pcApi.suggestPreview(id, { categoryName, range: rangeFor(date) }),
    }),
    apply: useMutation({
      mutationFn: ({ id, categoryName, date }: { id: string; categoryName?: string; date: string }) =>
        pcApi.suggestApply(id, { categoryName, range: rangeFor(date) }),
      onSuccess: invalidate,
    }),
  }
}
