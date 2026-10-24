import assert from 'node:assert/strict';
import { outlookSyncInvalidationKeys } from '../../src/client-web/src/utils/outlookSyncInvalidation';

const invalidationKeys = new Set(outlookSyncInvalidationKeys.map(key => JSON.stringify(key)));

for (const expectedKey of [
  ['outlook-sync-batches'],
  ['workbench-outlook-sync-batches'],
  ['today-outlook-sync-batches'],
  ['pending-confirmations'],
  ['workbench-pending-confirmations'],
  ['today-pending-confirmations'],
  ['workbench-calendar-layers'],
  ['calendar-layers'],
  ['data-center-query'],
]) {
  assert.equal(
    invalidationKeys.has(JSON.stringify(expectedKey)),
    true,
    `Expected sync success to invalidate ${JSON.stringify(expectedKey)}`,
  );
}
