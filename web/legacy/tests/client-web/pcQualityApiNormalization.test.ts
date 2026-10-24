import assert from 'node:assert/strict';
import { normalizePcQuality } from '../../src/client-web/src/api/pcTracker';

const quality = normalizePcQuality({
  overallStatus: 2,
  label: 'Warning',
  message: 'needs attention',
  checkedAt: '2026-05-25T00:00:00Z',
  components: [
    {
      key: 'aw-buckets',
      name: 'ActivityWatch buckets',
      status: 3,
      message: 'missing window bucket',
      details: { windowBuckets: 0, webBuckets: 1 },
    },
    {
      key: 'unknown',
      name: null,
      status: 99,
      message: null,
      details: null,
    },
  ],
  issues: [
    {
      code: 'missing-aw-window-bucket',
      severity: 3,
      componentKey: 'aw-buckets',
      message: 'missing',
      nextStep: 'Start ActivityWatch',
    },
  ],
  nextSteps: ['Start ActivityWatch', 123],
});

assert.equal(quality.overallStatus, 'Warning');
assert.equal(quality.label, '有警告');
assert.equal(quality.components[0].status, 'Critical');
assert.equal(quality.components[1].status, 'Unknown');
assert.equal(quality.components[0].details.windowBuckets, '0');
assert.equal(quality.components[0].details.webBuckets, '1');
assert.deepEqual(quality.components[1].details, {});
assert.equal(quality.issues[0].severity, 'Critical');
assert.deepEqual(quality.nextSteps, ['Start ActivityWatch', '123']);
