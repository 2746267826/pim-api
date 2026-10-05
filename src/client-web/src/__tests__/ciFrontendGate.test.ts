import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import {
  GATE_SCRIPT,
  GATE_SELFCHECK,
  PRE_EXISTING_ORPHANS,
  TESTS_DIR,
  checkGateWorkflow,
  findOrphans,
  gateBody,
  gateExpanded,
  listTestFiles,
  parseWorkflowSteps,
  readScripts,
  readWorkflowText,
  runLines,
  staleWhitelist,
} from './ciFrontendGate.rules';

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
 * - **REQ-8 结构判定**：判定从「run 行里有没有 `test:frontend-gate` 这个子串」升级为结构判定
 *   （实现见 `ciFrontendGate.rules.ts`）—— `run` 行里的 `|| true` / `&& true` / `set +e`，
 *   以及步骤上的 `continue-on-error: true` / `if: false`，都必须让自检失败。下面还有一组
 *   用例把**真实工作流文本**改坏后直接复算判定结论（不只靠人工试）。
 * - **REQ-9 视野扩展**：孤儿棘轮从「`tests/client-web` 顶层 `*.test.ts(x)`」扩到
 *   「`tests/client-web` 递归的 `*.test.ts(x)` 与 `*.spec.ts`」。判据不变（仍以「被某个 npm script
 *   按文件名显式引用」为准），因此**不**把 `src/client-web/src/**` 纳入视野：那批用例由无路径过滤的
 *   全量 `vitest run` 直接覆盖，而该命令正文里没有任何文件名，纳入即全红。
 */

const workflow = readWorkflowText();
const scripts = readScripts();

const allScripts = Object.values(scripts).join('\n');
const testFiles = listTestFiles(TESTS_DIR);

describe('WO-FRONTEND-PC-20261001 AC-5.1 · CI 必须执行全量前端单测', () => {
  it('build-web 的某个 run 步骤**实际调用** test:frontend-gate（注释不算）', () => {
    expect(runLines(workflow).some(command => command.includes(GATE_SCRIPT))).toBe(true);
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
  /** 整行匹配（含行尾可能的其它写法），保证变异只作用于这一行本身。 */
  const GATE_RUN_RE = /^[ \t]*run: npm run test:frontend-gate.*$/m;

  const appendToRunLine = (suffix: string) => workflow.replace(GATE_RUN_RE, m => `${m}${suffix}`);
  const addStepKey = (key: string) => workflow.replace(GATE_RUN_RE, m => `${m}\n${key}`);

  it('工作流里能找到门禁步骤的 run 行（下面是变异用例的锚点）', () => {
    expect(workflow).toContain(GATE_RUN_LINE);
  });

  it('反面（AC-8.6）：不加任何变异时，结构判定无违规', () => {
    expect(checkGateWorkflow(workflow)).toEqual([]);
  });

  it.each([
    ['run 行尾追加 `|| true`（AC-8.1）', appendToRunLine(' || true')],
    ['run 行尾追加 `&& true`（AC-8.2）', appendToRunLine(' && true')],
    ['步骤加 `continue-on-error: true`（AC-8.3）', addStepKey('        continue-on-error: true')],
    ['步骤加 `if: false`（AC-8.4）', addStepKey('        if: false')],
    ['run 行内插入 `set +e`（AC-8.7）', workflow.replace(GATE_RUN_RE, '        run: set +e; npm run test:frontend-gate')],
  ])('%s 时自检报错，并指出该步骤', (_label, mutated) => {
    expect(mutated, '变异未生效').not.toBe(workflow);
    const findings = checkGateWorkflow(mutated);
    expect(findings.length).toBeGreaterThan(0);
    expect(findings.some(f => f.stepName.includes('Frontend tests') && f.stepLine > 0)).toBe(true);
  });

  it('独立执行者被架空（continue-on-error / if: false）时同样报错', () => {
    const steps = parseWorkflowSteps(workflow).filter(step => step.run.includes(GATE_SELFCHECK));
    const name = steps[0].name;
    expect(name, '找不到自检独立执行者步骤').toBeTruthy();
    for (const variant of [`        continue-on-error: true`, `        if: false`]) {
      const mutated = workflow.replace(
        `      - name: ${name}\n`,
        `      - name: ${name}\n${variant}\n`,
      );
      expect(mutated, `变异未生效：${variant}`).not.toBe(workflow);
      const findings = checkGateWorkflow(mutated);
      expect(findings.length, `未检出：${variant}`).toBeGreaterThan(0);
      expect(findings.some(f => f.stepName === name)).toBe(true);
    }
  });

  it('整个门禁步骤被删掉时自检报错（不允许「谁都不调用门禁」）', () => {
    const mutated = workflow.replace(GATE_RUN_RE, '        run: echo skipped');
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

  it('白名单确实是累积型（存量条目仍在），且新增的嵌套 spec 条目已登记', () => {
    expect(PRE_EXISTING_ORPHANS.size).toBe(37);
    expect(PRE_EXISTING_ORPHANS.has('playwright/shellResponsive.spec.ts')).toBe(true);
  });
});
