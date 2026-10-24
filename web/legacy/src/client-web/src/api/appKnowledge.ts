import { apiGet, apiPost, apiDelete } from './client';
import type { ApiResponse, ActivityClassificationPreview } from '../types';

export type AppKnowledgePatternType = 'app-default' | 'domain' | 'title' | 'url-path' | 'source-family';

export interface AppKnowledgeApp {
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
  contextCount: number;
  pendingContextCount: number;
  recentAffectedDurationSeconds: number;
}

export interface AppKnowledgeContextPattern {
  id: string;
  appId: string | null;
  processName: string;
  patternType: AppKnowledgePatternType;
  patternValue: string;
  targetCategoryName: string | null;
  projectTag: string | null;
  scopeSummary: string;
  source: string;
  confidence: number;
  enabled: boolean;
  affectedRecordCount: number;
  affectedDurationSeconds: number;
  lastMatchedAt: string | null;
}

export interface SaveAppKnowledgeContextRequest {
  appId?: string | null;
  processName: string;
  patternType: AppKnowledgePatternType;
  patternValue: string;
  targetCategoryName: string | null;
  projectTag?: string | null;
  confidence?: number;
  enabled?: boolean;
}

export interface AppKnowledgeSuggestionPreview {
  suggestionId: string;
  recommendedContext: AppKnowledgeContextPattern;
  alternatives: AppKnowledgeContextPattern[];
  preview: ActivityClassificationPreview;
}

export interface AppKnowledgeSuggestionApply {
  suggestionId: string;
  savedContext: AppKnowledgeContextPattern;
  preview: ActivityClassificationPreview;
  auditId: string;
  suggestionStatus: string;
  message: string;
}

export const appKnowledgeApiPaths = {
  apps: (search?: string) => `/pc/app-knowledge/apps${search ? `?search=${encodeURIComponent(search)}` : ''}`,
  appContexts: (appId: string) => `/pc/app-knowledge/apps/${appId}/contexts`,
  contexts: '/pc/app-knowledge/contexts',
  suggestionPreview: (id: string) => `/pc/app-knowledge/suggestions/${id}/preview`,
  suggestionApply: (id: string) => `/pc/app-knowledge/suggestions/${id}/apply`,
} as const;

export function getAppKnowledgeApps(search?: string) {
  return apiGet<ApiResponse<AppKnowledgeApp[]>>(appKnowledgeApiPaths.apps(search)).then(r => r.data);
}

export function getAppKnowledgeContexts(appId: string) {
  return apiGet<ApiResponse<AppKnowledgeContextPattern[]>>(appKnowledgeApiPaths.appContexts(appId)).then(r => r.data);
}

export function saveAppKnowledgeContext(request: SaveAppKnowledgeContextRequest) {
  return apiPost<ApiResponse<AppKnowledgeContextPattern>>(appKnowledgeApiPaths.contexts, request).then(r => r.data);
}

export function deleteAppKnowledgeContext(id: string) {
  return apiDelete<ApiResponse<string>>(`${appKnowledgeApiPaths.contexts}/${id}`).then(r => r.data);
}

export function previewAppKnowledgeSuggestion(id: string, request: {
  categoryName: string | null;
  projectTag: string | null;
  range: { mode: 'today' | 'range'; dateFrom?: string | null; dateTo?: string | null };
}) {
  return apiPost<ApiResponse<AppKnowledgeSuggestionPreview>>(appKnowledgeApiPaths.suggestionPreview(id), request)
    .then(r => r.data);
}

export function applyAppKnowledgeSuggestion(id: string, request: {
  categoryName: string | null;
  projectTag: string | null;
  range: { mode: 'today' | 'range'; dateFrom?: string | null; dateTo?: string | null };
}) {
  return apiPost<ApiResponse<AppKnowledgeSuggestionApply>>(appKnowledgeApiPaths.suggestionApply(id), request)
    .then(r => r.data);
}
