import assert from 'node:assert/strict';
import { pcClassificationApiPaths, pcActivityAnalysisApiPath } from '../../src/client-web/src/api/pcTracker';

assert.equal(
  pcClassificationApiPaths.suggestionPreview('abc'),
  '/pc/classification/suggestions/abc/preview'
);
assert.equal(
  pcClassificationApiPaths.suggestionApply('abc'),
  '/pc/classification/suggestions/abc/apply'
);
assert.equal(pcClassificationApiPaths.recompute, '/pc/classification/recompute');
assert.equal(
  pcActivityAnalysisApiPath('2026-07-05', 60),
  '/pc/activity-analysis?date=2026-07-05&blockMinutes=60'
);
