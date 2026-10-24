import assert from 'node:assert/strict';
import type {
  ActivityClassificationApplyRange,
  ActivityClassificationPreview,
  ActivityClassificationSettings,
} from '../../src/client-web/src/types';

const preview: ActivityClassificationPreview = {
  affectedRecordCount: 2,
  affectedDurationSeconds: 300,
  currentCategoryCounts: { Uncategorized: 2 },
  newCategoryCounts: { Work: 2 },
  samples: [],
  requiresConfirmation: true,
  summary: '2 records will be updated.',
};

const settings: ActivityClassificationSettings = {
  recommendedMinimumClassificationDurationMinutes: 5,
  supportedRecommendedMinimumDurations: [1, 5, 10, 15],
};

const range: ActivityClassificationApplyRange = {
  mode: 'today',
  dateFrom: '2026-07-05',
  dateTo: '2026-07-05',
};

assert.equal(preview.requiresConfirmation, true);
assert.equal(settings.supportedRecommendedMinimumDurations.includes(5), true);
assert.equal(range.mode, 'today');
