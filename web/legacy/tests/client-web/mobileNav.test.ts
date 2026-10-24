/* eslint-disable @typescript-eslint/no-explicit-any, @typescript-eslint/no-require-imports */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertSourceContains(path: string, snippets: string[]) {
  const source = readFileSync(path, 'utf8');
  for (const snippet of snippets) {
    assert.ok(source.includes(snippet), `${path} should contain "${snippet}"`);
  }
}

assertSourceContains('src/client-web/src/layout/navItems.ts', [
  "'/today'",
  "'/calendar'",
  "'/quick-notes'",
  "'/tasks'",
  "'/reminders'",
]);

assertSourceContains('src/client-web/src/layout/MobileNav.tsx', [
  "from './navItems'",
  'md:hidden',
  'env(safe-area-inset-bottom)',
  'NavLink',
]);

assertSourceContains('src/client-web/src/layout/AppLayout.tsx', [
  '<MobileNav />',
  "from './MobileNav'",
]);

console.error('PASS: mobileNav');
