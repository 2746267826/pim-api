import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function assertContains(path: string, snippets: string[]) {
  const src = readFileSync(path, 'utf8');
  for (const s of snippets) assert.ok(src.includes(s), `${path} should contain ${s}`);
}

assertContains('src/client-web/src/ui/MobilePageHeader.tsx', ['MobilePageHeader']);
assertContains('src/client-web/src/pages/TodayPage.tsx', ['grid-cols-1', 'md:hidden']);
assertContains('src/client-web/src/pages/QuickNotesPage.tsx', ['pb-20', 'min-h-[44px]']);
assertContains('src/client-web/src/pages/RemindersPage.tsx', ['overflow-auto', 'pb-20']);
assertContains('src/client-web/src/pages/HabitsPage.tsx', ['grid-cols-1']);
assertContains('src/client-web/src/pages/TaskListPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/ConfirmationsPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/RecycleBinPage.tsx', ['pb-20']);

// 统一规范：触控目标与安全区在 MobilePageHeader 与 MobileNav 中已覆盖，此处抽查

console.error('PASS: aPagesResponsive');
