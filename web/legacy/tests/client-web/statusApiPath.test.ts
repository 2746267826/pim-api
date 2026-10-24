import assert from 'node:assert/strict';
import { statusApiPaths } from '../../src/client-web/src/api/status';

assert.equal(statusApiPaths.summary, '/status/summary');
assert.equal(statusApiPaths.detail, '/status/');
