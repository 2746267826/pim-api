import { apiGet, apiPost } from './client';
import type { ApiResponse } from '../types';
import type {
  DataReliabilityInspectionReport,
  DataReliabilityViolationExport,
} from './dataReliabilityTypes';

/** 体检违规清单导出的默认上限（T7：面板只显示 10 条，导出走独立请求）。 */
export const dataReliabilityViolationExportLimit = 2000;

export const dataReliabilityApiPaths = {
  inspection() {
    return '/data-reliability/inspection';
  },
  refresh() {
    return '/data-reliability/inspection/refresh';
  },
  violations(code: string, limit: number = dataReliabilityViolationExportLimit) {
    return `/data-reliability/rules/${encodeURIComponent(code)}/violations?limit=${limit}`;
  },
} as const;

export async function getDataReliabilityInspection() {
  const response = await apiGet<ApiResponse<DataReliabilityInspectionReport>>(
    dataReliabilityApiPaths.inspection()
  );
  return response.data;
}

export async function refreshDataReliabilityInspection() {
  const response = await apiPost<ApiResponse<DataReliabilityInspectionReport>>(
    dataReliabilityApiPaths.refresh()
  );
  return response.data;
}

export async function getDataReliabilityViolations(
  code: string,
  limit: number = dataReliabilityViolationExportLimit
) {
  const response = await apiGet<ApiResponse<DataReliabilityViolationExport>>(
    dataReliabilityApiPaths.violations(code, limit)
  );
  return response.data;
}
