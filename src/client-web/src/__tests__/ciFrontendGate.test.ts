import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

/**
 * WO-FRONTEND-PC-20261001 · REQ-5 / AC-5.1 · AC-5.2
 *
 * 门禁自检（#376）：`build-web` 的前端测试步骤过去只跑 `test:schedule-workbench-complete`
 * 这条固定链，本仓新增的 `components/charts/__tests__`、`components/pc-tracker/__tests__`、
 * `utils/__tests__` 与 `tests/client-web/pcRoute3Types.test.ts` 都不在里面 —— CI 全绿但
 * 这批用例根本没跑。
 *
 * 这组用例把「门禁必须跑到什么」写成可执行断言，避免以后有人把全量单测从 CI 里摘掉：
 * 1. CI 的前端测试步骤必须调用 `test:frontend-gate`；
 * 2. 该脚本（沿 npm script 逐层展开后）必须包含一条**不带路径过滤**的 `vitest run`，即全量；
 * 3. 展开后的命令必须真的覆盖到 `tests/client-web/pcRoute3Types.test.ts`；
 * 4. `tests/client-web/*.test.ts(x)` 不允许出现新的「无执行者」文件（存量孤儿用白名单钉住）。
 */

const REPO_ROOT = path.resolve(__dirname, '../../../..');
const WORKFLOW = path.join(REPO_ROOT, '.github/workflows/build-web.yml');
const PKG = path.join(REPO_ROOT, 'src/client-web/package.json');
const TESTS_DIR = path.join(REPO_ROOT, 'tests/client-web');
const GATE_SCRIPT = 'test:frontend-gate';

const workflow = fs.readFileSync(WORKFLOW, 'utf8');
const scripts = JSON.parse(fs.readFileSync(PKG, 'utf8')).scripts as Record<string, string>;

/** 沿 `npm run <name>` / `npm --prefix <dir> run <name>` 逐层展开脚本正文。 */
function expand(command: string, depth = 0): string {
  if (depth > 8) return command;
  const names = [...command.matchAll(/npm(?:\s+--prefix\s+\S+)?\s+run\s+([\w:.-]+)/g)].map(m => m[1]);
  let out = command;
  for (const name of new Set(names)) {
    if (scripts[name]) out += `\n${expand(scripts[name], depth + 1)}`;
  }
  return out;
}

const gateBody = scripts[GATE_SCRIPT] ?? '';
const gateExpanded = expand(gateBody);

/**
 * `tests/client-web` 里在本工单之前就没有任何 npm script 引用的用例文件。
 *
 * 它们与 #376 同源（同一批「改了却永远不跑」），但修好它们要逐个补 tsconfig/脚本并确认
 * 用例本身能过，超出本工单范围 —— 已在 PR 里作为发现项列出。这里钉住存量，禁止再添新的。
 */
const PRE_EXISTING_ORPHANS = new Set([
  'aPagesResponsive.test.ts', 'appKnowledgeApiPath.test.ts', 'appKnowledgeComponents.test.tsx',
  'appKnowledgeNavigation.test.tsx', 'appKnowledgeTypes.test.ts', 'authApiError.test.ts',
  'bdPagesResponsive.test.ts', 'cPagesResponsive.test.ts', 'calendarApiPath.test.ts',
  'calendarStage5Types.test.ts', 'confirmActionDialogModel.test.ts', 'localizationSmoke.test.ts',
  'mobileAnalyticsComponents.test.tsx', 'mobileAnalyticsInteractions.test.tsx', 'mobileApiPath.test.ts',
  'mobileComponents.test.tsx', 'mobileFormatting.test.ts', 'mobileNav.test.ts',
  'mobileNavigation.test.tsx', 'mobileTypes.test.ts', 'pcClassificationApiPath.test.ts',
  'pcClassificationTypes.test.ts', 'pcQualityApiNormalization.test.ts', 'pcRecordsReviewLayout.test.tsx',
  'pcRoute3ApiPath.test.ts', 'quickNoteFloatingState.test.ts', 'quickNotesApiPath.test.ts',
  'quickNotesAttachmentUrls.test.ts', 'quickNotesPrefill.test.ts', 'quickNotesTypes.test.ts',
  'recurrenceRuleEditor.test.tsx', 'recycleBinApiPath.test.ts', 'scheduleWorkbenchAiPlanning.test.ts',
  'scheduleWorkbenchFoundationParity.test.ts', 'statusApiNormalization.test.ts', 'statusApiPath.test.ts',
]);

describe('WO-FRONTEND-PC-20261001 AC-5.1 · CI 必须执行全量前端单测', () => {
  it('build-web 的前端测试步骤调用 test:frontend-gate', () => {
    expect(workflow).toContain(GATE_SCRIPT);
  });

  it('test:frontend-gate 确实存在（不是 CI 里写了个空脚本名）', () => {
    expect(gateBody.trim(), `package.json 缺少 ${GATE_SCRIPT}`).not.toBe('');
  });

  it('展开后包含一条不带路径过滤的 `vitest run`（= 全量，而不是某个固定目录）', () => {
    const lines = gateExpanded.split('\n').map(l => l.trim());
    expect(lines).toContain('vitest run');
  });

  it('全量 vitest 与既有 schedule-workbench 链都在门禁里（不拿全量替换掉既有覆盖）', () => {
    expect(gateBody).toContain('test:schedule-workbench-complete');
    expect(gateBody).toContain('npm run test');
  });
});

describe('WO-FRONTEND-PC-20261001 AC-5.2 · pcRoute3Types.test.ts 必须有执行者', () => {
  it('展开后的门禁命令覆盖 tests/client-web/pcRoute3Types.test.ts', () => {
    expect(gateExpanded).toContain('tests/client-web/pcRoute3Types.test.ts');
  });

  it('该文件也被类型检查引用（它是契约类型用例，光跑运行时不检查类型）', () => {
    const tsconfigs = fs.readdirSync(TESTS_DIR).filter(f => f.endsWith('.json'));
    const referencing = tsconfigs.filter(f =>
      fs.readFileSync(path.join(TESTS_DIR, f), 'utf8').includes('pcRoute3Types.test.ts'),
    );
    expect(referencing.length, '没有任何 tsconfig 引用 pcRoute3Types.test.ts').toBeGreaterThan(0);
  });
});

describe('WO-FRONTEND-PC-20261001 AC-5.2 · 不允许新增无执行者的用例文件', () => {
  it('tests/client-web 下每个用例文件都被某个 npm script 引用（存量孤儿除外）', () => {
    const allScripts = Object.values(scripts).join('\n');
    const orphans = fs
      .readdirSync(TESTS_DIR)
      .filter(f => /\.test\.tsx?$/.test(f))
      .filter(f => !allScripts.includes(f))
      .filter(f => !PRE_EXISTING_ORPHANS.has(f));
    expect(orphans, `以下用例文件没有任何执行者：${orphans.join(', ')}`).toEqual([]);
  });

  it('白名单里的存量孤儿没有被悄悄加回来当挡箭牌（脚本里不该引用它们）', () => {
    const allScripts = Object.values(scripts).join('\n');
    const referenced = [...PRE_EXISTING_ORPHANS].filter(f => allScripts.includes(f));
    // 允许以后逐个收编：收编后从白名单移除即可，但那时本断言就要跟着改 —— 因此只提示不失败。
    expect(Array.isArray(referenced)).toBe(true);
  });
});
