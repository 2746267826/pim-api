import { apiGet } from './client';
import type { ApiResponse, TodaySection, TodaySectionRegistry } from '../types';

export const todayApiPaths = {
  sections: (date: string) => `/today/sections?date=${encodeURIComponent(date)}`,
  section: (sectionId: string, date: string) =>
    `/today/sections/${encodeURIComponent(sectionId)}?date=${encodeURIComponent(date)}`,
} as const;

export function getTodaySectionRegistry(date: string) {
  return apiGet<ApiResponse<TodaySectionRegistry>>(todayApiPaths.sections(date)).then(r => r.data);
}

export function getTodaySection<TData = unknown>(sectionId: string, date: string) {
  return apiGet<ApiResponse<TodaySection<TData>>>(todayApiPaths.section(sectionId, date)).then(r => r.data);
}
