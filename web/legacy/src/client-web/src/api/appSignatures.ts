import { apiGet, apiPost, apiDelete } from './client';
import type { ApiResponse } from '../types';

export interface AppSignature {
  id: string;
  processName: string;
  displayName: string;
  categoryPath: string | null;
  productivity: string | null;
  description: string | null;
  source: string;
  confidence: number;
  icon: string | null;
  lastSeenAt: string | null;
  createdAt: string;
}

export interface SaveAppSignatureRequest {
  processName: string;
  displayName: string;
  categoryPath?: string;
  productivity?: string;
  description?: string;
  icon?: string;
  confidence?: number;
}

const basePath = '/pc/app-signatures';

export function getAppSignatures(search?: string) {
  const params = search ? `?search=${encodeURIComponent(search)}` : '';
  return apiGet<ApiResponse<AppSignature[]>>(`${basePath}/${params}`).then(r => r.data);
}

export function getAppSignatureCount() {
  return apiGet<ApiResponse<number>>(`${basePath}/count`).then(r => r.data);
}

export function lookupAppSignature(processName: string) {
  return apiGet<ApiResponse<AppSignature>>(`${basePath}/lookup/${encodeURIComponent(processName)}`).then(r => r.data);
}

export function createAppSignature(data: SaveAppSignatureRequest) {
  return apiPost<ApiResponse<AppSignature>>(`${basePath}/`, data).then(r => r.data);
}

export function deleteAppSignature(id: string) {
  return apiDelete<ApiResponse<string>>(`${basePath}/${id}`).then(r => r.data);
}
