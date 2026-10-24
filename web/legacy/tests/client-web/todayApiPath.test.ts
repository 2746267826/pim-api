import assert from 'node:assert/strict';
import { todayApiPaths } from '../../src/client-web/src/api/today';

assert.equal(todayApiPaths.sections('2026-05-25'), '/today/sections?date=2026-05-25');
assert.equal(
  todayApiPaths.section('calendar.schedule', '2026-05-25'),
  '/today/sections/calendar.schedule?date=2026-05-25',
);
assert.equal(
  todayApiPaths.section('pc.classification_suggestions', '2026-05-25'),
  '/today/sections/pc.classification_suggestions?date=2026-05-25',
);
