import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const appLayout = readFileSync('src/client-web/src/layout/AppLayout.tsx', 'utf8');
const floatingEntry = readFileSync(
  'src/client-web/src/components/quick-notes/QuickNoteFloatingEntry.tsx',
  'utf8',
);

assert.match(
  appLayout,
  /const QuickNotesPage = lazy\(\(\) => import\('\.\.\/pages\/QuickNotesPage'\)\)/,
  'QuickNotesPage should be lazy-loaded so the Markdown editor is not part of the app entry bundle.',
);

assert.doesNotMatch(
  appLayout,
  /import QuickNotesPage from '\.\.\/pages\/QuickNotesPage'/,
  'AppLayout should not statically import QuickNotesPage.',
);

// #280 / #300：全站入口改为 QuickNoteFloatingEntry，点击「+」弹出菜单，
// 「写闪念」打开与「快速记录」页同款的编辑卡片（QuickNoteDialog，含 Markdown 编辑器）。
// 该卡片必须懒加载，否则体积很大的编辑器会被带进应用入口 bundle。
assert.match(
  floatingEntry,
  /const LazyQuickNoteDialog = lazy\(\(\) => import\('\.\/QuickNoteDialog'\)\)/,
  'QuickNoteDialog should be lazy-loaded from the global entry so the Markdown editor stays out of the app entry bundle.',
);

assert.doesNotMatch(
  floatingEntry,
  /^import QuickNoteDialog from '\.\/QuickNoteDialog';/m,
  'The global entry should not statically import QuickNoteDialog.',
);

assert.doesNotMatch(
  floatingEntry,
  /^import QuickNoteEditor from '\.\/QuickNoteEditor';/m,
  'The global entry should not statically import QuickNoteEditor.',
);

// 入口按钮本身要足够轻量（不应直接引入编辑器）。
assert.doesNotMatch(
  appLayout,
  /^import QuickNoteEditor from/m,
  'AppLayout should not statically import QuickNoteEditor.',
);

console.error('PASS: quickNotesEntryBundle');
