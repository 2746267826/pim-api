import assert from 'node:assert/strict';
import {
  QUICK_NOTE_DRAFT_KEY,
  QUICK_NOTE_PANEL_POSITION_KEY,
  clampPanelPosition,
} from '../../src/client-web/src/components/quick-notes/quickNoteFloatingState';

assert.equal(QUICK_NOTE_DRAFT_KEY, 'pim.quickNotes.floatingDraft');
assert.equal(QUICK_NOTE_PANEL_POSITION_KEY, 'pim.quickNotes.panelPosition');
assert.deepEqual(
  clampPanelPosition({ x: -50, y: 9999 }, { width: 1200, height: 800 }, { width: 360, height: 420 }),
  { x: 12, y: 368 },
);
assert.deepEqual(
  clampPanelPosition({ x: 500, y: 200 }, { width: 1200, height: 800 }, { width: 360, height: 420 }),
  { x: 500, y: 200 },
);
