import assert from 'node:assert/strict';
import { pcClassificationApiPaths } from '../../src/client-web/src/api/pcTracker';

const paths = [
  pcClassificationApiPaths.rules,
  pcClassificationApiPaths.preview,
  pcClassificationApiPaths.apply,
  pcClassificationApiPaths.suggestions('2026-05-25'),
  pcClassificationApiPaths.suggestionPreview('suggestion-1'),
  pcClassificationApiPaths.suggestionApply('suggestion-1'),
  pcClassificationApiPaths.recompute,
  pcClassificationApiPaths.settings,
  pcClassificationApiPaths.recentProjectTags,
];

assert.deepEqual(paths, [
  '/pc/classification/rules',
  '/pc/classification/rules/preview',
  '/pc/classification/rules/apply',
  '/pc/classification/suggestions?date=2026-05-25',
  '/pc/classification/suggestions/suggestion-1/preview',
  '/pc/classification/suggestions/suggestion-1/apply',
  '/pc/classification/recompute',
  '/pc/classification/settings',
  '/pc/classification/project-tags/recent',
]);

for (const path of paths) {
  assert.equal(path.startsWith('/pc/classification'), true);
}
