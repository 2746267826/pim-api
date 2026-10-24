import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
function assertContains(p: string, ss: string[]) { const s = readFileSync(p,'utf8'); for(const x of ss) assert.ok(s.includes(x), `${p} should contain ${x}`); }
assertContains('src/client-web/src/pages/CalendarPage.tsx', ['pb-20', 'grid-cols-1']);
assertContains('src/client-web/src/pages/ReportsPage.tsx', ['pb-20', 'overflow-auto']);
assertContains('src/client-web/src/pages/HistoricalLocationPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/MobileRecordsPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/StatusPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/SettingsPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/SyncPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/AiSettingsPage.tsx', ['pb-20']);
assertContains('src/client-web/src/pages/AppKnowledgeBasePage.tsx', ['pb-20']);
console.error('PASS: bdPagesResponsive D');
