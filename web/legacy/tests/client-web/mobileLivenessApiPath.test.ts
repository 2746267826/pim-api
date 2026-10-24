import assert from 'node:assert/strict';
import { mobileApiPaths } from '../../src/client-web/src/api/mobile';

assert.equal(
  mobileApiPaths.livenessOverview({ rangeStartUtc: '2026-09-01T00:00:00Z', rangeEndUtc: '2026-09-08T00:00:00Z' }),
  '/mobile/liveness/overview?rangeStartUtc=2026-09-01T00%3A00%3A00Z&rangeEndUtc=2026-09-08T00%3A00%3A00Z'
);
assert.equal(
  `/api/v1${mobileApiPaths.livenessOverview({ rangeStartUtc: '2026-09-01T00:00:00Z', rangeEndUtc: '2026-09-08T00:00:00Z' })}`,
  '/api/v1/mobile/liveness/overview?rangeStartUtc=2026-09-01T00%3A00%3A00Z&rangeEndUtc=2026-09-08T00%3A00%3A00Z'
);
assert.equal(
  `/api/v1${mobileApiPaths.livenessEvents('phone / 1', { page: 1, pageSize: 50 })}`,
  '/api/v1/mobile/devices/phone%20%2F%201/liveness/events?page=1&pageSize=50'
);

console.error('PASS: mobileLivenessApiPath');
