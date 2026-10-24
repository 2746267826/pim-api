import { apiDelete, apiDownloadBlob, apiGet, apiPost, apiPut, apiUpload } from './client';
import type {
  ApiResponse,
  CreateQuickNoteRequest,
  PagedResult,
  QuickNoteAttachmentUpload,
  QuickNoteDetail,
  QuickNoteListItem,
  QuickNoteStatus,
  UpdateQuickNoteRequest,
} from '../types';

export interface GetQuickNotesParams {
  status?: QuickNoteStatus;
  search?: string;
  page?: number;
  pageSize?: number;
}

function buildQuery(params: GetQuickNotesParams = {}) {
  const searchParams = new URLSearchParams();
  if (params.status) searchParams.set('status', params.status);
  if (params.search) searchParams.set('search', params.search);
  if (params.page) searchParams.set('page', String(params.page));
  if (params.pageSize) searchParams.set('pageSize', String(params.pageSize));

  const query = searchParams.toString();
  return query ? `?${query}` : '';
}

export const quickNoteApiPaths = {
  list: (params: GetQuickNotesParams = {}) => `/quick-notes${buildQuery(params)}`,
  detail: (id: string) => `/quick-notes/${id}`,
  process: (id: string) => `/quick-notes/${id}/process`,
  archive: (id: string) => `/quick-notes/${id}/archive`,
  restore: (id: string) => `/quick-notes/${id}/restore`,
  attachments: () => '/quick-notes/attachments',
  attachmentDownload: (id: string) => `/quick-notes/attachments/${id}/download`,
} as const;

export function getQuickNotes(params: GetQuickNotesParams = {}) {
  return apiGet<ApiResponse<PagedResult<QuickNoteListItem>>>(quickNoteApiPaths.list(params))
    .then(r => r.data);
}

export function getQuickNote(id: string) {
  return apiGet<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.detail(id)).then(r => r.data);
}

export function createQuickNote(data: CreateQuickNoteRequest) {
  return apiPost<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.list(), data).then(r => r.data);
}

export function updateQuickNote(id: string, data: UpdateQuickNoteRequest) {
  return apiPut<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.detail(id), data).then(r => r.data);
}

export function processQuickNote(id: string) {
  return apiPost<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.process(id), {}).then(r => r.data);
}

export function archiveQuickNote(id: string) {
  return apiPost<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.archive(id), {}).then(r => r.data);
}

export function restoreQuickNote(id: string, status: QuickNoteStatus = 'inbox') {
  return apiPost<ApiResponse<QuickNoteDetail>>(quickNoteApiPaths.restore(id), { status }).then(r => r.data);
}

export function deleteQuickNote(id: string) {
  return apiDelete<ApiResponse<string>>(quickNoteApiPaths.detail(id)).then(r => r.data);
}

export async function uploadQuickNoteAttachment(file: File): Promise<QuickNoteAttachmentUpload> {
  const formData = new FormData();
  formData.append('file', file);

  return apiUpload<ApiResponse<QuickNoteAttachmentUpload>>(
    quickNoteApiPaths.attachments(),
    formData,
  ).then(r => r.data);
}

export function downloadQuickNoteAttachmentBlob(id: string): Promise<Blob> {
  return apiDownloadBlob(quickNoteApiPaths.attachmentDownload(id));
}
