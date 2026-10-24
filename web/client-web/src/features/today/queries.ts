import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { deferredIntervalMs } from '@/lib/polling'
import { todayBusinessDay } from '@/lib/businessDay'
import { todayApi } from './api'
import type { TodaySection, TodaySectionRegistry } from './api'

export const todayKeys = {
  registry: (date: string) => ['today', 'sections', date] as const,
  section: (id: string, date: string) => ['today', 'section', id, date] as const,
}

export function useTodayRegistry(date: string) {
  return useQuery({
    queryKey: todayKeys.registry(date),
    queryFn: () => todayApi.registry(date),
    refetchInterval: () => deferredIntervalMs(),
  })
}

export function useTodaySection(sectionId: string, date: string, poll: boolean) {
  return useQuery({
    queryKey: todayKeys.section(sectionId, date),
    queryFn: () => todayApi.section(sectionId, date),
    refetchInterval: poll ? () => deferredIntervalMs() : false,
  })
}

/** 业务日状态：午夜自动翻转日期（规格：午夜自动翻转日期） */
export function useBusinessDate(): string {
  const [date, setDate] = useState(() => todayBusinessDay())
  useEffect(() => {
    const timer = setInterval(() => {
      const next = todayBusinessDay()
      setDate((prev) => (prev === next ? prev : next))
    }, 30_000)
    return () => clearInterval(timer)
  }, [])
  return date
}

export type { TodaySection, TodaySectionRegistry }
