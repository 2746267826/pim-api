import assert from 'node:assert/strict';
import {
  getAutoRefreshInterval,
  getDeferredAutoRefreshInterval,
  notifyUserInteraction,
  resetInteractionStateForTests,
} from '../../src/client-web/src/lib/autoRefresh';

function test(_name: string, run: () => void) {
  run();
}

test('daytime returns 5 minute interval', () => {
  assert.equal(getAutoRefreshInterval(new Date(2026, 7, 15, 12, 0, 0)), 300000);
});

test('nighttime returns 30 minute interval', () => {
  assert.equal(getAutoRefreshInterval(new Date(2026, 7, 15, 3, 0, 0)), 1800000);
});

test('hour boundary falls on the daytime interval', () => {
  assert.equal(getAutoRefreshInterval(new Date(2026, 7, 15, 6, 0, 0)), 300000);
  assert.equal(getAutoRefreshInterval(new Date(2026, 7, 15, 5, 59, 59)), 1800000);
});

test('deferred interval returns 1s shortly after a user interaction', () => {
  resetInteractionStateForTests();
  notifyUserInteraction();
  assert.equal(getDeferredAutoRefreshInterval(), 1000);
});

test('resetInteractionStateForTests restores the baseline interval', () => {
  resetInteractionStateForTests();
  notifyUserInteraction();
  assert.equal(getDeferredAutoRefreshInterval(), 1000);
  resetInteractionStateForTests();
  const now = new Date();
  assert.equal(getDeferredAutoRefreshInterval(), getAutoRefreshInterval(now));
});
