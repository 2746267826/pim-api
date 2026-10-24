import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import ActivityAnalysisHeatmap from '../../src/client-web/src/components/pc-tracker/ActivityAnalysisHeatmap';
import ClassificationActionQueue from '../../src/client-web/src/components/pc-tracker/ClassificationActionQueue';
import ClassificationPreviewDialog, {
  canApplyClassificationPreview,
  classificationPreviewConfirmationKey,
  classificationPreviewRequestKey,
  resolveConfirmedClassificationPreviewKey,
} from '../../src/client-web/src/components/pc-tracker/ClassificationPreviewDialog';
import RuleImpactPreviewPanel from '../../src/client-web/src/components/pc-tracker/RuleImpactPreviewPanel';
import {
  isCurrentPcRoute3Request,
  nextPcRoute3RequestId,
} from '../../src/client-web/src/pages/PcTrackerPage';
import type {
  ActivityClassificationPreview,
  ActivityClassificationSuggestionPreview,
  ActivityClassificationSuggestion,
  PcActivityAnalysisResponse,
  SuggestionClassificationPreviewRequest,
} from '../../src/client-web/src/types';
import type { CategoryTreeNode } from '../../src/client-web/src/api/pcTracker';

const suggestion: ActivityClassificationSuggestion = {
  id: 'suggestion-1',
  clusterKey: 'web:docs.microsoft.com',
  sampleCount: 3,
  totalDurationSeconds: 900,
  sampleRecordsJson: '[]',
  sanitizedContextJson: '{}',
  currentCategory: 'Other',
  suggestedCategory: 'Learning',
  suggestedProjectTag: 'docs',
  suggestedRulesJson: null,
  userFeedback: null,
  llmResponseJson: null,
  status: 'pending',
  appDisplayName: 'Microsoft Docs',
  appIcon: null,
  recognitionSource: 'builtin',
};

const preview: ActivityClassificationPreview = {
  affectedRecordCount: 3,
  affectedDurationSeconds: 900,
  currentCategoryCounts: { '其他': 3 },
  newCategoryCounts: { '学习': 3 },
  samples: [],
  requiresConfirmation: true,
  summary: '本次会影响 3 条记录。',
};

const categories: CategoryTreeNode[] = [{
  id: 'cat-root',
  parentId: null,
  name: '学习',
  color: '#64748b',
  icon: null,
  productivity: 'productive',
  sortOrder: 0,
  isBuiltin: true,
  children: [{
    id: 'cat-child',
    parentId: 'cat-root',
    name: '技术学习',
    color: '#8b5cf6',
    icon: null,
    productivity: 'productive',
    sortOrder: 0,
    isBuiltin: true,
    children: [],
  }],
}];

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

const queueHtml = renderToStaticMarkup(
  React.createElement(ClassificationActionQueue, {
    suggestions: [suggestion],
    isLoading: false,
    onPreview: () => undefined,
    onReject: () => undefined,
  })
);

assert.equal(queueHtml.includes('处理并预览'), true);
assert.equal(queueHtml.includes('Accept'), false);
assert.equal(queueHtml.includes('Later'), false);
assert.equal(queueHtml.includes('Microsoft Docs'), true);
assert.equal(queueHtml.includes('已识别'), true);
assert.equal(queueHtml.includes('样本'), true);
assert.equal(queueHtml.includes('分钟'), true);

const previewHtml = renderToStaticMarkup(
  React.createElement(RuleImpactPreviewPanel, { preview })
);

assert.equal(previewHtml.includes('知识库写入影响'), true);
assert.equal(previewHtml.includes('3 条记录'), true);
assert.equal(previewHtml.includes('15 分钟'), true);
assert.equal(previewHtml.includes('当前：其他 3'), true);
assert.equal(previewHtml.includes('应用后：学习 3'), true);

const activityAnalysis: PcActivityAnalysisResponse = {
  date: '2026-07-05',
  blockMinutes: 60,
  blocks: [{
    start: '2026-07-05T00:00:00Z',
    end: '2026-07-05T01:00:00Z',
    intensityLevel: 3,
    intensityMax: 5,
    activeDurationSeconds: 1800,
    pendingClassificationCount: 1,
    contextSwitchCount: 2,
    categoryChangeCount: 1,
    categories: [{ categoryName: 'Programming', color: '#2563eb', durationSeconds: 1800 }],
    apps: [{ appName: 'Code.exe', durationSeconds: 1800 }],
  }],
};

const activityAnalysisHtml = renderToStaticMarkup(
  React.createElement(ActivityAnalysisHeatmap, {
    analysis: activityAnalysis,
    selectedStart: null,
    onSelectBlock: () => undefined,
  })
);

assert.equal(activityAnalysisHtml.includes('活动分析'), true);
assert.equal(activityAnalysisHtml.includes('Keyboard'), false);
assert.equal(activityAnalysisHtml.includes('活跃 30 分钟'), true);
assert.equal(activityAnalysisHtml.includes('Programming'), true);
assert.equal(activityAnalysisHtml.includes('aria-pressed'), false, 'button grid replaced by EChartBox');
assert.equal(activityAnalysisHtml.includes('待分类'), true);
assert.equal(activityAnalysisHtml.includes('role="img"'), true, 'EChartBox placeholder renders');
assert.equal(activityAnalysisHtml.includes('aria-label="活动分析热力图"'), true);

const currentRequestId = nextPcRoute3RequestId(3);
assert.equal(currentRequestId, 4);
assert.equal(isCurrentPcRoute3Request(currentRequestId, 4), true);
assert.equal(isCurrentPcRoute3Request(currentRequestId - 1, 4), false);

const previewRequest: SuggestionClassificationPreviewRequest = {
  categoryName: 'Learning',
  projectTag: 'docs',
  range: { mode: 'today', dateFrom: '2026-07-05', dateTo: '2026-07-05' },
};

const previewRequestKey = classificationPreviewRequestKey(previewRequest);
const previewConfirmationKey = classificationPreviewConfirmationKey(suggestion.id, previewRequest);
const changedRequest: SuggestionClassificationPreviewRequest = {
  ...previewRequest,
  categoryName: 'Work',
};
const changedProjectRequest: SuggestionClassificationPreviewRequest = {
  ...previewRequest,
  projectTag: 'client-work',
};
const changedRangeModeRequest: SuggestionClassificationPreviewRequest = {
  ...previewRequest,
  range: { mode: 'range', dateFrom: '2026-07-01', dateTo: '2026-07-05' },
};
const changedRangeDateRequest: SuggestionClassificationPreviewRequest = {
  ...previewRequest,
  range: { mode: 'today', dateFrom: '2026-07-06', dateTo: '2026-07-06' },
};

const suggestionPreview: ActivityClassificationSuggestionPreview = {
  rule: {
    ruleName: 'Docs',
    scope: 'activity',
    categoryName: 'Learning',
    projectTag: 'docs',
    color: '#64748b',
    priority: 900,
    conditionsJson: '{"all":[]}',
    confidence: 0.95,
    explanation: null,
  },
  preview,
};
const nextSuggestionPreview: ActivityClassificationSuggestionPreview = {
  ...suggestionPreview,
  preview: { ...preview, summary: 'Fresh preview result.' },
};

assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, previewRequest, false, false), true);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, 'suggestion-2', previewRequest, false, false), false);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, changedRequest, false, false), false);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, changedProjectRequest, false, false), false);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, changedRangeModeRequest, false, false), false);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, changedRangeDateRequest, false, false), false);
assert.equal(canApplyClassificationPreview(null, previewConfirmationKey, suggestion.id, previewRequest, false, false), false);
assert.equal(canApplyClassificationPreview(preview, previewConfirmationKey, suggestion.id, previewRequest, true, false), false);

assert.equal(
  resolveConfirmedClassificationPreviewKey({
    previousPreview: null,
    nextPreview: suggestionPreview,
    pendingPreviewConfirmationKey: previewConfirmationKey,
    confirmedPreviewConfirmationKey: null,
  }),
  previewConfirmationKey
);
assert.equal(
  resolveConfirmedClassificationPreviewKey({
    previousPreview: suggestionPreview,
    nextPreview: suggestionPreview,
    pendingPreviewConfirmationKey: classificationPreviewConfirmationKey(suggestion.id, changedRequest),
    confirmedPreviewConfirmationKey: previewConfirmationKey,
  }),
  previewConfirmationKey
);
assert.equal(
  resolveConfirmedClassificationPreviewKey({
    previousPreview: suggestionPreview,
    nextPreview: nextSuggestionPreview,
    pendingPreviewConfirmationKey: classificationPreviewConfirmationKey(suggestion.id, changedRangeModeRequest),
    confirmedPreviewConfirmationKey: previewConfirmationKey,
  }),
  classificationPreviewConfirmationKey(suggestion.id, changedRangeModeRequest)
);
assert.equal(
  resolveConfirmedClassificationPreviewKey({
    previousPreview: suggestionPreview,
    nextPreview: null,
    pendingPreviewConfirmationKey: previewConfirmationKey,
    confirmedPreviewConfirmationKey: previewConfirmationKey,
  }),
  null
);

const dialogHtml = renderToStaticMarkup(
  React.createElement(ClassificationPreviewDialog, {
    suggestion,
    date: '2026-07-05',
    preview: null,
    isPreviewing: false,
    isApplying: false,
    errorMessage: null,
    categories,
    onClose: () => undefined,
    onPreview: () => undefined,
    onApply: () => undefined,
  })
);

assert.equal(dialogHtml.includes('App 知识库写入预览'), true);
assert.equal(dialogHtml.includes('目标分类'), true);
assert.equal(dialogHtml.includes('项目标签'), true);
assert.equal(dialogHtml.includes('预览影响'), true);
assert.equal(dialogHtml.includes('写入 App 知识库'), true);
assert.equal(dialogHtml.includes('创建规则'), false);
assert.equal(dialogHtml.includes('学习'), true);
assert.equal(dialogHtml.includes('技术学习'), true);
assert.equal(dialogHtml.includes('<select'), true);
assert.equal(/<button[^>]*disabled=""[^>]*>写入 App 知识库<\/button>/.test(dialogHtml), true);

const rangeDialogHtml = renderToStaticMarkup(
  React.createElement(ClassificationPreviewDialog, {
    suggestion: { ...suggestion, suggestedCategory: null },
    date: '2026-07-05',
    preview: null,
    isPreviewing: false,
    isApplying: false,
    errorMessage: null,
    categories,
    onClose: () => undefined,
    onPreview: () => undefined,
    onApply: () => undefined,
  })
);

assert.equal(rangeDialogHtml.includes('今天'), true);
assert.equal(rangeDialogHtml.includes('日期范围'), true);

const pcTrackerPageSource = fs.readFileSync(
  path.join(process.cwd(), 'src/client-web/src/pages/PcTrackerPage.tsx'),
  'utf8'
);

assert.equal(pcTrackerPageSource.includes('ActivityAnalysisHeatmap'), true);
assert.equal(pcTrackerPageSource.includes('ContextConfirmationPanel'), true);
assert.equal(pcTrackerPageSource.includes('ClassificationActionQueue'), false);
assert.equal(pcTrackerPageSource.includes('ClassificationPreviewDialog'), true);
assert.equal(pcTrackerPageSource.includes('acceptActivityClassificationSuggestion'), false);
assert.equal(pcTrackerPageSource.includes('previewAppKnowledgeSuggestion'), true);
assert.equal(pcTrackerPageSource.includes('applyAppKnowledgeSuggestion'), true);
assert.equal(pcTrackerPageSource.includes('previewActivityClassificationSuggestion'), false);
assert.equal(pcTrackerPageSource.includes('applyActivityClassificationSuggestion'), false);
assert.equal(pcTrackerPageSource.includes("['app-knowledge-apps']"), true);
assert.equal(pcTrackerPageSource.includes("['app-knowledge-contexts']"), true);
assert.equal(pcTrackerPageSource.includes('ClassificationSuggestionPanel'), false);
assert.equal(pcTrackerPageSource.includes('QuickClassificationDialog'), false);

// REQ-8 回归锁：Workbench 的 PC 汇总必须按业务日取数。
// 原实现用 `range.start.split('T')[0]`（本地午夜的 UTC 日期），UTC+8 下会整体差一天，
// 「PC 活跃」显示成前一天的汇总，与 summary.heatmap 对不上（真实浏览器证据见 PR）。
const workbenchPageSource = fs.readFileSync('src/client-web/src/pages/WorkbenchPage.tsx', 'utf8');
assert.equal(
  workbenchPageSource.includes("getPcSummary(formatPcDate(getPcBusinessDate()))") ||
  workbenchPageSource.includes('getPcSummary(pcBusinessDateStr)'),
  true,
  'Workbench PC summary must query the PC business day'
);
assert.equal(
  workbenchPageSource.includes('getPcSummary(todayStr)'),
  false,
  'Workbench PC summary must not use the UTC date of local midnight'
);
assert.equal(
  workbenchPageSource.includes('formatPcDate(getPcBusinessDate())'),
  true,
  'business day must come from utils/pcBusinessDay'
);
