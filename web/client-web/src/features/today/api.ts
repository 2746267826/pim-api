import { apiGet } from '@/api/client'
import { todayBusinessDay } from '@/lib/businessDay'

/* 今日页（TodayEndpoints）：分区注册表 + 单分区数据，date 为业务日 yyyy-MM-dd */

export type TodaySectionStatus = 'available' | 'normal' | 'empty' | 'warning' | 'critical' | 'unavailable'

export interface TodaySectionLink {
  rel: string
  href: string
}

export interface TodaySectionRef {
  id: string
  kind: string
  status: TodaySectionStatus
  links: TodaySectionLink[]
}

export interface TodaySectionRegistry {
  date: string
  pcBusinessDate: string
  generatedAt: string
  sections: TodaySectionRef[]
}

/** 分区数据载荷随 kind 而异（规格：前端以泛型消费，这里做防御式渲染） */
export interface TodaySection {
  id: string
  kind: string
  status: TodaySectionStatus
  generatedAt: string
  data: unknown
  links: TodaySectionLink[]
  error: { code: string; message: string } | null
}

export const todayApi = {
  registry: (date: string = todayBusinessDay()) =>
    apiGet<TodaySectionRegistry>(`/api/v1/today/sections?date=${date}`),
  section: (sectionId: string, date: string = todayBusinessDay()) =>
    apiGet<TodaySection>(
      `/api/v1/today/sections/${encodeURIComponent(sectionId)}?date=${date}`,
    ),
}
