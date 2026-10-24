import assert from 'node:assert/strict';
import {
  getComponentKindLabel,
  getHealthStatusLabel,
  normalizeStatusDetail,
  normalizeStatusSummary,
} from '../../src/client-web/src/api/status';

const summary = normalizeStatusSummary({
  status: 1,
  label: 'Healthy',
  message: 'ok',
  checkedAt: '2026-05-25T00:00:00Z',
});

assert.equal(summary.status, 'Healthy');
assert.equal(summary.label, '正常');
assert.equal(getHealthStatusLabel(summary.status), '正常');

const detail = normalizeStatusDetail({
  summary: {
    status: 2,
    label: 'Warning',
    message: 'needs attention',
    checkedAt: '2026-05-25T00:00:00Z',
  },
  components: [
    {
      key: 'api',
      name: 'API',
      kind: 0,
      status: 3,
      message: 'down',
      checkedAt: '2026-05-25T00:00:00Z',
      details: { code: 500, ok: false },
    },
    {
      key: 'unknown',
      name: 'Unknown',
      kind: null,
      status: 99,
      message: null,
      checkedAt: null,
      details: null,
    },
  ],
  nextSteps: ['restart daemon', 123],
});

assert.equal(detail.summary.status, 'Warning');
assert.equal(detail.components[0].status, 'Critical');
assert.equal(detail.components[1].status, 'Unknown');
assert.equal(detail.components[0].kind, '');
assert.equal(detail.components[1].kind, '');
assert.deepEqual(detail.components[0].details, { code: '500', ok: 'false' });
assert.deepEqual(detail.components[1].details, {});
assert.deepEqual(detail.nextSteps, ['restart daemon', '123']);

assert.equal(getHealthStatusLabel('Critical'), '故障');
assert.equal(getHealthStatusLabel('Unknown'), '未知');
assert.equal(getComponentKindLabel('Api'), 'API');
assert.equal(getComponentKindLabel(''), '');
