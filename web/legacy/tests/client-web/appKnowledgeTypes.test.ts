import assert from 'node:assert/strict';
import type {
  AppKnowledgeApp,
  AppKnowledgeContextPattern,
  AppKnowledgeSuggestionPreview,
} from '../../src/client-web/src/api/appKnowledge';

const app: AppKnowledgeApp = {
  id: 'app-1',
  processName: 'msedge.exe',
  displayName: 'Edge',
  categoryPath: '工作 / 开发',
  productivity: 'productive',
  description: null,
  source: 'builtin',
  confidence: 0.9,
  icon: null,
  lastSeenAt: null,
  createdAt: '2026-07-06T00:00:00Z',
  contextCount: 2,
  pendingContextCount: 1,
  recentAffectedDurationSeconds: 3600,
};

const context: AppKnowledgeContextPattern = {
  id: 'context-1',
  appId: 'app-1',
  processName: 'msedge.exe',
  patternType: 'domain',
  patternValue: 'github.com',
  targetCategoryName: '工作 / 开发',
  projectTag: null,
  scopeSummary: 'Edge / github.com',
  source: 'user-confirmed',
  confidence: 1,
  enabled: true,
  affectedRecordCount: 42,
  affectedDurationSeconds: 7200,
  lastMatchedAt: null,
};

const contextWithoutApp: AppKnowledgeContextPattern = {
  id: 'context-2',
  appId: null,
  processName: 'msedge.exe',
  patternType: 'title',
  patternValue: 'Release notes',
  targetCategoryName: null,
  projectTag: 'docs',
  scopeSummary: 'msedge.exe / Release notes',
  source: 'system-suggested',
  confidence: 0.75,
  enabled: false,
  affectedRecordCount: 3,
  affectedDurationSeconds: 900,
  lastMatchedAt: '2026-07-06T01:00:00Z',
};

const preview: AppKnowledgeSuggestionPreview = {
  suggestionId: 'suggestion-1',
  recommendedContext: context,
  alternatives: [context, contextWithoutApp],
  preview: {
    affectedRecordCount: 42,
    affectedDurationSeconds: 7200,
    currentCategoryCounts: { 其他: 42 },
    newCategoryCounts: { '工作 / 开发': 42 },
    samples: [],
    requiresConfirmation: true,
    summary: '将写入 App 知识库并重算 42 条记录。',
  },
};

assert.equal(app.contextCount, 2);
assert.equal(context.patternType, 'domain');
assert.equal(contextWithoutApp.appId, null);
assert.equal(preview.alternatives.length, 2);
assert.equal(preview.recommendedContext.scopeSummary, 'Edge / github.com');
