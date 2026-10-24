import { apiGet, apiPost } from './client';
import type {
  ApiResponse,
  AuditExportResponse,
  AuditTimelineResponse,
  OperationConfirmation,
  RestorePreviewResponse,
} from '../types';

export const operationsApiPaths = {
  pendingConfirmations() {
    return '/operations/confirmations/pending';
  },
  detail(id: string) {
    return `/operations/confirmations/${encodeURIComponent(id)}`;
  },
  confirm(id: string) {
    return `/operations/confirmations/${encodeURIComponent(id)}/confirm`;
  },
  confirmSecondLevel(id: string) {
    return `/operations/confirmations/${encodeURIComponent(id)}/confirm-second-level`;
  },
  confirmStrict(id: string) {
    return `/operations/confirmations/${encodeURIComponent(id)}/confirm-strict`;
  },
  reject(id: string) {
    return `/operations/confirmations/${encodeURIComponent(id)}/reject`;
  },
  auditTimeline(objectType: string, objectId: string) {
    return `/operations/audit/${encodeURIComponent(objectType)}/${encodeURIComponent(objectId)}`;
  },
  restorePreview(auditVersionId: string) {
    return `/operations/audit/${encodeURIComponent(auditVersionId)}/restore-preview`;
  },
  auditExport() {
    return '/operations/audit/export';
  },
} as const;

export async function getPendingConfirmations() {
  const r = await apiGet<ApiResponse<OperationConfirmation[]>>(
    operationsApiPaths.pendingConfirmations()
  );
  return r.data;
}

export async function getConfirmationDetail(id: string) {
  const r = await apiGet<ApiResponse<OperationConfirmation>>(
    operationsApiPaths.detail(id)
  );
  return r.data;
}

export async function confirmOperation(id: string) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    operationsApiPaths.confirm(id),
    {}
  );
  return r.data;
}

export async function confirmOperationSecondLevel(id: string) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    operationsApiPaths.confirmSecondLevel(id),
    {}
  );
  return r.data;
}

export async function confirmOperationStrict(id: string) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    operationsApiPaths.confirmStrict(id),
    {}
  );
  return r.data;
}

export async function rejectOperation(id: string) {
  const r = await apiPost<ApiResponse<OperationConfirmation>>(
    operationsApiPaths.reject(id),
    {}
  );
  return r.data;
}

export async function getAuditTimeline(objectType: string, objectId: string) {
  const r = await apiGet<ApiResponse<AuditTimelineResponse>>(
    operationsApiPaths.auditTimeline(objectType, objectId)
  );
  return r.data;
}

export async function getRestorePreview(auditVersionId: string) {
  const r = await apiPost<ApiResponse<RestorePreviewResponse>>(
    operationsApiPaths.restorePreview(auditVersionId),
    {}
  );
  return r.data;
}

export async function exportAudit() {
  const r = await apiGet<ApiResponse<AuditExportResponse>>(operationsApiPaths.auditExport());
  return r.data;
}
