import assert from 'node:assert/strict';
import type {
  ActivityClassificationSuggestionPreview,
  PcActivityAnalysisResponse,
} from '../../src/client-web/src/types';

const preview: ActivityClassificationSuggestionPreview = {
  rule: {
    ruleName: 'Docs',
    scope: 'activity',
    categoryName: 'Learning',
    projectTag: null,
    color: '#64748b',
    priority: 900,
    conditionsJson: '{"all":[]}',
    confidence: 0.95,
    explanation: null,
  },
  preview: {
    affectedRecordCount: 1,
    affectedDurationSeconds: 60,
    currentCategoryCounts: { Other: 1 },
    newCategoryCounts: { Learning: 1 },
    samples: [],
    requiresConfirmation: true,
    summary: 'Affected 1 record.',
  },
};

const analysis: PcActivityAnalysisResponse = {
  date: '2026-07-05',
  blockMinutes: 60,
  blocks: [{
    start: '2026-07-05T00:00:00Z',
    end: '2026-07-05T01:00:00Z',
    intensityLevel: 3,
    intensityMax: 5,
    activeDurationSeconds: 1200,
    pendingClassificationCount: 1,
    contextSwitchCount: 2,
    categoryChangeCount: 1,
    categories: [{ categoryName: 'Learning', color: '#64748b', durationSeconds: 1200 }],
    apps: [{ appName: 'Edge', durationSeconds: 1200 }],
  }],
};

assert.equal(preview.rule.scope, 'activity');
assert.equal(analysis.blocks[0].pendingClassificationCount, 1);
