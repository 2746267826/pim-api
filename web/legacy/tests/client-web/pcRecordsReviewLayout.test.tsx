import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import ContextConfirmationPanel from '../../src/client-web/src/components/pc-tracker/ContextConfirmationPanel';
import PcReviewSummary from '../../src/client-web/src/components/pc-tracker/PcReviewSummary';
import type { ActivityClassificationSuggestion, PcSummaryResponse } from '../../src/client-web/src/types';

const summary: PcSummaryResponse = {
  keystats: null,
  heatmap: [],
  appRanking: [],
  timeline: [],
  sessions: [],
  metrics: {
    totalRecordedDuration: '8h 12m',
    activeInputDuration: '5h 40m',
    idleDuration: '2h 32m',
    sessionCount: 4,
    activeAppCount: 9,
    totalKeyPresses: 1234,
    totalClicks: 456,
    appSwitchCount: 78,
    switchFrequency: 9.5,
    mostFocusedApp: 'Code.exe',
    keyClickRatio: 2.7,
  },
  categories: [
    { categoryName: '工作 / 开发', color: '#2563eb', share: 0.62, keyPresses: 1000, totalClicks: 300 },
  ],
};

const suggestion: ActivityClassificationSuggestion = {
  id: 'suggestion-1',
  clusterKey: 'msedge.exe|github.com',
  sampleCount: 42,
  totalDurationSeconds: 3600,
  sampleRecordsJson: '[]',
  sanitizedContextJson: '{}',
  currentCategory: '其他',
  suggestedCategory: '工作 / 开发',
  suggestedProjectTag: null,
  suggestedRulesJson: null,
  userFeedback: null,
  llmResponseJson: null,
  status: 'pending',
  appDisplayName: 'Edge',
  appIcon: null,
  recognitionSource: 'system',
};

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

const html = renderToStaticMarkup(
  React.createElement(PcReviewSummary, { summary, pendingSuggestions: [suggestion] })
);

assert.equal(html.includes('今日复盘'), true);
assert.equal(html.includes('记录时长'), true);
assert.equal(html.includes('主要分类'), true);
assert.equal(html.includes('专注块'), true);
assert.equal(html.includes('分类覆盖率'), true);
assert.equal(html.includes('工作 / 开发'), true);
assert.equal(html.includes('8h 12m'), true);
assert.equal(html.includes('5h 40m'), true);
assert.equal(html.includes('78'), true);
assert.equal(html.includes('等待同步'), true);
assert.equal(html.includes('Code.exe'), true);

const contextPanelHtml = renderToStaticMarkup(
  React.createElement(ContextConfirmationPanel, {
    suggestions: [suggestion],
    isLoading: false,
    onPreview: () => undefined,
    onReject: () => undefined,
  })
);

assert.equal(contextPanelHtml.includes('待确认上下文'), true);
assert.equal(contextPanelHtml.includes('写入 App 知识库'), true);
assert.equal(contextPanelHtml.includes('旧规则'), false);
assert.equal(contextPanelHtml.includes('纠错规则'), false);
