import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

/**
 * WO-FRONTEND-PC-20261001 · REQ-5 / AC-5.1 · AC-5.2
 * + WO-ISSUES-386-390-20261005 · REQ-7 / REQ-8 / REQ-9（#389）
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
 * 4. `tests/client-web` 下（含子目录与 `*.spec.ts`）不允许出现新的「无执行者」文件
 *    （存量孤儿用白名单钉住）。
 *
 * #389 追加的三件事：
 * - **REQ-7 独立执行者**：本文件过去唯一的执行者是它自己守护的 `npm run test`（裸 `vitest run`），
 *   谁把全量 vitest 从 `test:frontend-gate` 摘掉，自检就一起不跑。现在 `build-web.yml`
 *   必须有一条**独立 step** 直接执行本文件，且该 step 本身不能被「永远绿」写法架空。
 * - **REQ-8 结构判定**：判定从「run 行里有没有 `test:frontend-gate` 这个子串」升级为结构判定 ——
 *   `run` 行里的 `|| true` / `&& true` / `set +e`，以及步骤上的 `continue-on-error: true` / `if: false`，
 *   都必须让自检失败（这些写法不改变 `run:` 行正文，只看子串是看不见的）。
 * - **REQ-9 视野扩展**：孤儿棘轮从「`tests/client-web` 顶层 `*.test.ts(x)`」扩到
 *   「`tests/client-web` 递归的 `*.test.ts(x)` 与 `*.spec.ts`」。判据不变（仍以「被某个 npm script
 *   按文件名显式引用」为准），因此**不**把 `src/client-web/src/**` 纳入视野：那批用例由无路径过滤的
 *   全量 `vitest run` 直接覆盖，而该命令正文里没有任何文件名，纳入即全红。
 */

const REPO_ROOT = path.resolve(__dirname, '../../../..');
const WORKFLOW = path.join(REPO_ROOT, '.github/workflows/build-web.yml');
const PKG = path.join(REPO_ROOT, 'src/client-web/package.json');
const TESTS_DIR = path.join(REPO_ROOT, 'tests/client-web');
const GATE_SCRIPT = 'test:frontend-gate';
/** 本自检文件自身（相对 `src/client-web`）。REQ-7 要求它在工作流里有一个独立执行者。 */
const GATE_SELFCHECK = 'src/__tests__/ciFrontendGate.test.ts';

const workflow = fs.readFileSync(WORKFLOW, 'utf8');
const scripts = JSON.parse(fs.readFileSync(PKG, 'utf8')).scripts as Record<string, string>;

/** 工作流里所有 `run:` 步骤的命令正文（排除注释行）。 */
function runLines(): string[] {
  return workflow
    .split('\n')
    .filter(line => /^\s*run:\s*\S/.test(line))
    .map(line => line.replace(/^\s*run:\s*/, '').trim());
}

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

// ---------------------------------------------------------------------------
// REQ-8：工作流结构解析与「永远绿」判定
// ---------------------------------------------------------------------------

interface WorkflowStep {
  /** 步骤起始行号（1 基），用于失败信息定位。 */
  line: number;
  name: string;
  /** 该步骤所有 `run:` 的正文（多行 `run: |` 以换行拼接）。 */
  run: string;
  ifValue: string;
  continueOnError: string;
}

interface GateFinding {
  stepLine: number;
  stepName: string;
  reason: string;
}

/** `- ` 列表项后紧跟这些键之一时才算一个 step（本文件里 steps 是唯一的列表）。 */
const STEP_KEY = /^\s+(name|uses|run|id|if|with|env|shell|working-directory|continue-on-error|timeout-minutes):/;

function toStep(block: { line: number; indent: number; lines: string[] }): WorkflowStep {
  // 把首行 `- key: value` 归一成 `  key: value`，后续解析对所有行一视同仁。
  const lines = block.lines.map((line, idx) => (idx === 0 ? line.replace(/^(\s*)- /, '$1  ') : line));
  const keyValue = (key: string): string => {
    for (const line of lines) {
      const m = /^(\s*)([\w-]+):\s*(.*)$/.exec(line);
      if (!m) continue;
      if (m[1].length <= block.indent) break; // 回到同级/外层，step 块结束
      if (m[2] === key && !m[3].trim().startsWith('|')) return m[3].trim();
    }
    return '';
  };
  const runParts: string[] = [];
  for (let i = 0; i < lines.length; i += 1) {
    const m = /^(\s*)run:\s*(.*)$/.exec(lines[i]);
    if (!m) continue;
    const runIndent = m[1].length;
    if (runIndent <= block.indent) break;
    const value = m[2].trim();
    if (value === '|' || value === '>' || value.startsWith('|') || value.startsWith('>')) {
      const body: string[] = [];
      for (let j = i + 1; j < lines.length; j += 1) {
        const line = lines[j];
        if (line.trim() === '') continue;
        const indent = line.length - line.trimStart().length;
        if (indent <= runIndent) break;
        body.push(line.trim());
      }
      runParts.push(body.join('\n'));
    } else {
      runParts.push(value);
    }
  }
  return {
    line: block.line,
    name: keyValue('name'),
    run: runParts.join('\n'),
    ifValue: keyValue('if'),
    continueOnError: keyValue('continue-on-error'),
  };
}

/** 把工作流文本拆成 step 块（含 `run: |` 多行正文）。 */
export function parseWorkflowSteps(text: string): WorkflowStep[] {
  const lines = text.split('\n');
  const steps: WorkflowStep[] = [];
  let block: { line: number; indent: number; lines: string[] } | null = null;
  lines.forEach((line, idx) => {
    const m = /^(\s*)- /.exec(line);
    const next = lines[idx + 1] ?? '';
    const isStepStart =
      !!m && next.length - next.trimStart().length > m[1].length && STEP_KEY.test(next);
    if (isStepStart) {
      if (block) steps.push(toStep(block));
      block = { line: idx + 1, indent: m![1].length, lines: [line] };
      return;
    }
    if (block) block.lines.push(line);
  });
  if (block) steps.push(toStep(block));
  return steps;
}

const normalize = (value: string) => value.trim().replace(/^['"]|['"]$/g, '');
const isFalsyCondition = (value: string) => normalize(value) === 'false' || normalize(value) === '${{ false }}';
const isTruthyFlag = (value: string) => normalize(value) === 'true' || normalize(value) === '${{ true }}';

const SHORT_CIRCUIT = ['|| true', '||true', '&& true', '&&true'];

/** 单个步骤级「永远绿」写法（不改变 run 行正文的那些）。 */
function checkStepAlwaysGreen(step: WorkflowStep, findings: GateFinding[]): void {
  const push = (reason: string) => findings.push({ stepLine: step.line, stepName: step.name || '(未命名步骤)', reason });
  if (step.continueOnError && isTruthyFlag(step.continueOnError)) {
    push(`步骤带 \`continue-on-error: ${step.continueOnError.trim()}\`，失败不会让 job 变红`);
  }
  if (step.ifValue && isFalsyCondition(step.ifValue)) {
    push(`步骤带 \`if: ${step.ifValue.trim()}\`，步骤根本不会执行`);
  }
  for (const pattern of SHORT_CIRCUIT) {
    if (step.run.includes(pattern)) push(`run 正文含短路成功写法 \`${pattern}\``);
  }
  if (/(^|[;&\s])set \+e(\s|;|$)/.test(step.run)) push('run 正文含 `set +e`，失败码被吞掉');
}

/**
 * 对**工作流文本**做结构判定，返回全部「永远绿」违规。
 * 空数组 = 门禁内容可信（步骤级 + 门禁脚本展开后都没有短路/关停写法）。
 */
export function checkGateWorkflow(text: string): GateFinding[] {
  const findings: GateFinding[] = [];
  const steps = parseWorkflowSteps(text);

  const gateSteps = steps.filter(step => step.run.includes(GATE_SCRIPT));
  if (gateSteps.length === 0) {
    findings.push({ stepLine: 0, stepName: '(整个工作流)', reason: `没有任何 step 的 run 正文调用 ${GATE_SCRIPT}` });
  }
  gateSteps.forEach(step => checkStepAlwaysGreen(step, findings));

  const selfCheckSteps = steps.filter(step => step.run.includes(GATE_SELFCHECK));
  if (selfCheckSteps.length === 0) {
    findings.push({
      stepLine: 0,
      stepName: '(整个工作流)',
      reason: `没有独立 step 执行门禁自检文件 ${GATE_SELFCHECK}（自检仍只被它守护的全量 vitest 拉起）`,
    });
  }
  selfCheckSteps.forEach(step => checkStepAlwaysGreen(step, findings));

  for (const pattern of SHORT_CIRCUIT) {
    if (gateExpanded.includes(pattern)) {
      findings.push({ stepLine: 0, stepName: GATE_SCRIPT, reason: `展开后含短路成功写法 \`${pattern}\`` });
    }
  }
  if (/(^|[;&\s])set \+e(\s|;|$)/.test(gateExpanded)) {
    findings.push({ stepLine: 0, stepName: GATE_SCRIPT, reason: '展开后含 `set +e`' });
  }
  return findings;
}

// ---------------------------------------------------------------------------
// REQ-9：孤儿棘轮（视野 = tests/client-web 递归的 *.test.ts(x) 与 *.spec.ts）
// ---------------------------------------------------------------------------

/**
 * `tests/client-web` 里在本工单之前就没有任何 npm script 引用的用例文件。
 *
 * 它们与 #376 同源（同一批「改了却永远不跑」），但修好它们要逐个补 tsconfig/脚本并确认
 * 用例本身能过，超出本工单范围 —— 已在 PR 里作为发现项列出。这里钉住存量，禁止再添新的。
 * 子目录用例用相对 `tests/client-web` 的 posix 路径登记（REQ-9 视野扩展后新增
 * `playwright/shellResponsive.spec.ts`：本次只纳入视野并登记，不补执行者）。
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
  'playwright/shellResponsive.spec.ts',
]);

/** 用例文件判定：`*.test.ts(x)` 与 `*.spec.ts(x)`（REQ-9 的视野）。 */
const TEST_FILE_RE = /\.(test|spec)\.tsx?$/;

/** 递归列出用例文件，返回相对 `tests/client-web` 的 posix 路径。 */
export function listTestFiles(dir: string, base: string = dir): string[] {
  const out: string[] = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...listTestFiles(full, base));
    } else if (TEST_FILE_RE.test(entry.name)) {
      out.push(path.relative(base, full).split(path.sep).join('/'));
    }
  }
  return out.sort();
}

/** 判据不变：文件名（或子目录相对路径）必须出现在某个 npm script 正文里。 */
function isReferenced(file: string, scriptText: string): boolean {
  return scriptText.includes(file) || scriptText.includes(path.posix.basename(file));
}

/** 视野内、未被脚本引用、且不在累积白名单里的用例文件。 */
export function findOrphans(
  files: string[],
  scriptText: string,
  whitelist: ReadonlySet<string> = PRE_EXISTING_ORPHANS,
): string[] {
  return files.filter(file => !isReferenced(file, scriptText) && !whitelist.has(file)).sort();
}

/** 白名单里已过期（文件消失、或已被收编获得执行者）的条目。 */
export function staleWhitelist(
  files: string[],
  scriptText: string,
  whitelist: ReadonlySet<string> = PRE_EXISTING_ORPHANS,
): string[] {
  const present = new Set(files);
  return [...whitelist].filter(entry => !present.has(entry) || isReferenced(entry, scriptText)).sort();
}

const allScripts = Object.values(scripts).join('\n');
const testFiles = listTestFiles(TESTS_DIR);

describe('WO-FRONTEND-PC-20261001 AC-5.1 · CI 必须执行全量前端单测', () => {
  it('build-web 的某个 run 步骤**实际调用** test:frontend-gate（注释不算）', () => {
    expect(runLines().some(command => command.includes(GATE_SCRIPT))).toBe(true);
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

describe('REQ-7 · 门禁自检必须有独立执行者（#389）', () => {
  it('build-web 有一条独立 step 直接执行本自检文件，而不是只被全量 vitest 拉起', () => {
    const steps = parseWorkflowSteps(workflow).filter(step => step.run.includes(GATE_SELFCHECK));
    expect(steps.length, `没有任何独立 step 执行 ${GATE_SELFCHECK}`).toBeGreaterThan(0);
    expect(steps[0].name).not.toBe('');
  });

  it('该独立执行者不是门禁链本身（run 正文不含 test:frontend-gate）', () => {
    const steps = parseWorkflowSteps(workflow).filter(step => step.run.includes(GATE_SELFCHECK));
    expect(steps.some(step => step.run.includes(GATE_SCRIPT))).toBe(false);
  });
});

describe('REQ-8 · 自检必须拒绝「永远绿」的写法（#389）', () => {
  const GATE_RUN_LINE = '        run: npm run test:frontend-gate';
  expect(workflow, '工作流里找不到门禁步骤的 run 行，变异用例需要它').toContain(GATE_RUN_LINE);

  const appendToRunLine = (suffix: string) => workflow.replace(GATE_RUN_LINE, `${GATE_RUN_LINE}${suffix}`);
  const addStepKey = (line: string) => workflow.replace(GATE_RUN_LINE, `${GATE_RUN_LINE}\n${line}`);

  it('反面（AC-8.6）：不加任何变异时，结构判定无违规', () => {
    expect(checkGateWorkflow(workflow)).toEqual([]);
  });

  it.each([
    ['run 行尾追加 `|| true`（AC-8.1）', appendToRunLine(' || true')],
    ['run 行尾追加 `&& true`（AC-8.2）', appendToRunLine(' && true')],
    ['步骤加 `continue-on-error: true`（AC-8.3）', addStepKey('        continue-on-error: true')],
    ['步骤加 `if: false`（AC-8.4）', addStepKey('        if: false')],
    ['run 行内插入 `set +e`（AC-8.7）', workflow.replace(GATE_RUN_LINE, '        run: set +e; npm run test:frontend-gate')],
  ])('%s 时自检报错，并指出该步骤', (_label, mutated) => {
    expect(mutated, '变异未生效').not.toBe(workflow);
    const findings = checkGateWorkflow(mutated);
    expect(findings.length).toBeGreaterThan(0);
    expect(findings.some(f => f.stepName.includes('Frontend tests') && f.stepLine > 0)).toBe(true);
  });

  it('独立执行者被架空（continue-on-error / if: false）时同样报错', () => {
    const selfCheckStepName = /- name: (.+)\n\s+run: [^\n]*ciFrontendGate\.test\.ts/;
    const name = selfCheckStepName.exec(workflow)?.[1];
    expect(name, '找不到自检独立执行者步骤').toBeTruthy();
    for (const variant of [
      `- name: ${name}\n        continue-on-error: true\n`,
      `- name: ${name}\n        if: false\n`,
    ]) {
      const mutated = workflow.replace(`- name: ${name}\n`, variant);
      const findings = checkGateWorkflow(mutated);
      expect(findings.length, `变异未生效：${variant}`).toBeGreaterThan(0);
      expect(findings.some(f => f.stepName === name)).toBe(true);
    }
  });

  it('整个门禁步骤被删掉时自检报错（不允许「谁都不调用门禁」）', () => {
    const mutated = workflow.replace(GATE_RUN_LINE, '        run: echo skipped');
    expect(checkGateWorkflow(mutated).length).toBeGreaterThan(0);
  });
});

describe('REQ-9 · 孤儿棘轮覆盖子目录与 *.spec.ts（#389）', () => {
  it('视野 = tests/client-web 递归下的 *.test.ts(x) 与 *.spec.ts', () => {
    expect(testFiles).toContain('autoRefreshInterval.test.ts');
    expect(testFiles).toContain('playwright/shellResponsive.spec.ts');
    // 视野不得外扩到 src/client-web/src（那批用例由无路径过滤的全量 vitest 覆盖）
    expect(testFiles.every(f => !f.startsWith('..'))).toBe(true);
  });

  it('在视野内的子目录里新增无执行者用例时会被报出（AC-9.1 的纯函数形态）', () => {
    const orphans = findOrphans(['woProbe.spec.ts', 'woProbeSub/woProbeSub.spec.ts'], allScripts);
    expect(orphans).toEqual(['woProbe.spec.ts', 'woProbeSub/woProbeSub.spec.ts']);
  });

  it('白名单外的孤儿列表为空；被引用的用例不被报出（AC-9.2）', () => {
    const orphans = findOrphans(testFiles, allScripts);
    expect(orphans, `以下用例文件没有任何执行者：${orphans.join(', ')}`).toEqual([]);
    expect(orphans).not.toContain('autoRefreshInterval.test.ts');
  });

  it('白名单必须保持准确：列出的文件仍然存在、且确实仍是孤儿（AC-9.3）', () => {
    const stale = staleWhitelist(testFiles, allScripts);
    expect(stale, `白名单已过期（文件已消失或已被收编），请从白名单移除：${stale.join(', ')}`).toEqual([]);
  });

  it('白名单里的嵌套 spec 条目被删掉时会指名报错（AC-9.3 的纯函数形态）', () => {
    const withoutNestedSpec = testFiles.filter(f => f !== 'playwright/shellResponsive.spec.ts');
    expect(staleWhitelist(withoutNestedSpec, allScripts)).toEqual(['playwright/shellResponsive.spec.ts']);
  });
});
