import { apiGet, apiPost } from './client';
import type { ApiResponse, PagedResult } from '../types';
import type { AiRequestLogDetail, AiRequestLogListItem, AiStatus, AiUsageSummary } from '../types';

export const aiApiPaths = {
  status: '/ai/status',
  test: '/ai/test',
  requests: '/ai/requests',
  requestDetail: (id: string) => `/ai/requests/${id}`,
  usageSummary: '/ai/usage/summary',
  healthCheck: '/ai/health-check',
} as const;

export interface AiRequestFilters {
  module?: string;
  purpose?: string;
  model?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

function query(params: AiRequestFilters) {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== '') search.set(key, String(value));
  });
  const text = search.toString();
  return text ? `?${text}` : '';
}

export async function getAiStatus() {
  const response = await apiGet<ApiResponse<AiStatus>>(aiApiPaths.status);
  return response.data;
}

export async function runAiTest() {
  const response = await apiPost<ApiResponse<unknown>>(aiApiPaths.test);
  return response.data;
}

export async function runAiHealthCheck() {
  const response = await apiPost<ApiResponse<AiStatus>>(aiApiPaths.healthCheck);
  return response.data;
}

export async function getAiRequests(filters: AiRequestFilters) {
  const response = await apiGet<ApiResponse<PagedResult<AiRequestLogListItem>>>(
    `${aiApiPaths.requests}${query(filters)}`
  );
  return response.data;
}

export async function getAiRequestDetail(id: string) {
  const response = await apiGet<ApiResponse<AiRequestLogDetail>>(aiApiPaths.requestDetail(id));
  return response.data;
}

export async function getAiUsageSummary() {
  const response = await apiGet<ApiResponse<AiUsageSummary>>(aiApiPaths.usageSummary);
  return response.data;
}
