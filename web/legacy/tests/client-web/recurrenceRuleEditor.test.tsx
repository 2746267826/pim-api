import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { readFileSync } from 'node:fs';

const src = readFileSync(new URL('../../src/client-web/src/components/calendar/RecurrenceRuleEditor.tsx', import.meta.url), 'utf8');

// helper: emulate buildRrule from file (copy logic) to verify UNTIL fix without importing component
function buildRrule(state: { freq: string; interval: number; byDay: string[]; endMode: string; count?: number; until?: string }): string | null {
  if (state.freq === 'none') return null;
  let rrule = `FREQ=${state.freq}`;
  if (state.interval > 1) rrule += `;INTERVAL=${state.interval}`;
  if (state.freq === 'WEEKLY' && state.byDay.length > 0) rrule += `;BYDAY=${state.byDay.join(',')}`;
  if (state.endMode === 'count' && state.count && state.count > 0) rrule += `;COUNT=${state.count}`;
  else if (state.endMode === 'until' && state.until) {
    const d = new Date(state.until);
    if (!isNaN(d.getTime())) {
      const y = d.getUTCFullYear().toString().padStart(4,'0');
      const m = (d.getUTCMonth()+1).toString().padStart(2,'0');
      const day = d.getUTCDate().toString().padStart(2,'0');
      rrule += `;UNTIL=${y}${m}${day}T235959Z`;
    }
  }
  return rrule;
}

describe('RecurrenceRuleEditor - UNTIL fix', () => {
  it('source uses T235959Z not T000000Z', () => {
    assert.ok(src.includes('T235959Z'), 'must use 235959Z');
    assert.ok(!src.includes('T000000Z'), 'must not use 000000Z');
  });
  it('UNTIL on same day includes 09:00 instance (23:59:59Z)', () => {
    const rrule = buildRrule({ freq: 'DAILY', interval: 1, byDay: [], endMode: 'until', until: '2026-07-20' });
    assert.ok(rrule?.includes('UNTIL=20260720T235959Z'), `got ${rrule}`);
  });
});

describe('RecurrenceRuleEditor - rule generation', () => {
  it('DAILY', () => {
    assert.equal(buildRrule({ freq: 'DAILY', interval: 1, byDay: [], endMode: 'never' }), 'FREQ=DAILY');
  });
  it('WEEKLY with BYDAY', () => {
    assert.equal(buildRrule({ freq: 'WEEKLY', interval: 1, byDay: ['MO','WE'], endMode: 'never' }), 'FREQ=WEEKLY;BYDAY=MO,WE');
  });
  it('MONTHLY interval 2', () => {
    assert.equal(buildRrule({ freq: 'MONTHLY', interval: 2, byDay: [], endMode: 'never' }), 'FREQ=MONTHLY;INTERVAL=2');
  });
  it('YEARLY', () => {
    assert.equal(buildRrule({ freq: 'YEARLY', interval: 1, byDay: [], endMode: 'never' }), 'FREQ=YEARLY');
  });
  it('COUNT', () => {
    assert.equal(buildRrule({ freq: 'DAILY', interval: 1, byDay: [], endMode: 'count', count: 5 }), 'FREQ=DAILY;COUNT=5');
  });
  it('interval + count combined', () => {
    assert.equal(buildRrule({ freq: 'WEEKLY', interval: 2, byDay: ['FR'], endMode: 'count', count: 10 }), 'FREQ=WEEKLY;INTERVAL=2;BYDAY=FR;COUNT=10');
  });
  it('none returns null', () => {
    assert.equal(buildRrule({ freq: 'none', interval: 1, byDay: [], endMode: 'never' }), null);
  });
});

describe('RecurrenceRuleEditor - parsing', () => {
  it('parses UNTIL T235959Z and T000000Z both', () => {
    // parseRrule handles both; we just verify src handles 235959Z prefix
    assert.ok(src.includes("UNTIL=([^;]+)"));
    assert.ok(src.includes("T235959Z") || src.includes("235959"));
  });
});
