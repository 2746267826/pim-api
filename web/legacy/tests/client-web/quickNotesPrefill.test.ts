import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertContains(path: string, snippets: string[]) {
  const src = readFileSync(path, 'utf8');
  for (const s of snippets) assert.ok(src.includes(s), `${path} should contain ${s}`);
}

assertContains('src/client-web/src/hooks/useShellShare.ts', [
  "pim-shell:share",
  "addEventListener",
  "removeEventListener",
]);

assertContains('src/client-web/src/pages/QuickNotesPage.tsx', [
  "useShellShare",
  "prefill",
  "useSearchParams",
]);

console.error('PASS: quickNotesPrefill');
