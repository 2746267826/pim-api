import fs from 'node:fs';
import path from 'node:path';

/**
 * 前端门禁自检的**判定逻辑**（与断言分离，便于对工作流文本直接施加变异后复算结论）。
 *
 * - REQ-7：工作流必须有一条独立 step 直接执行自检文件本身。
 * - REQ-8：判定必须是**结构判定** —— `continue-on-error: true` / `if: false` 不改变 `run:` 行正文，
 *   只做子串包含是看不见的；`|| true` / `&& true` / `set +e` 则直接吞掉失败码。
 * - REQ-9：孤儿棘轮视野 = `tests/client-web` 递归下的 `*.test.ts(x)` 与 `*.spec.ts`，判据不变。
 */

export const REPO_ROOT = path.resolve(__dirname, '../../../..');
export const WORKFLOW = path.join(REPO_ROOT, '.github/workflows/build-web.yml');
export const PKG = path.join(REPO_ROOT, 'src/client-web/package.json');
export const TESTS_DIR = path.join(REPO_ROOT, 'tests/client-web');
export const GATE_SCRIPT = 'test:frontend-gate';
/** 自检文件自身（相对 `src/client-web`）。REQ-7 要求它在工作流里有一个独立执行者。 */
export const GATE_SELFCHECK = 'src/__tests__/ciFrontendGate.test.ts';

export const readWorkflowText = (): string => fs.readFileSync(WORKFLOW, 'utf8');
export const readScripts = (): Record<string, string> =>
  JSON.parse(fs.readFileSync(PKG, 'utf8')).scripts as Record<string, string>;

export const scripts = readScripts();

/** 沿 `npm run <name>` / `npm --prefix <dir> run <name>` 逐层展开脚本正文。 */
export function expand(command: string, depth = 0): string {
  if (depth > 8) return command;
  const names = [...command.matchAll(/npm(?:\s+--prefix\s+\S+)?\s+run\s+([\w:.-]+)/g)].map(m => m[1]);
  let out = command;
  for (const name of new Set(names)) {
    if (scripts[name]) out += `\n${expand(scripts[name], depth + 1)}`;
  }
  return out;
}

export const gateBody = scripts[GATE_SCRIPT] ?? '';
export const gateExpanded = expand(gateBody);

/** 工作流里所有 `run:` 步骤的命令正文（排除注释行）。 */
export function runLines(text: string): string[] {
  return text
    .split('\n')
    .filter(line => /^\s*run:\s*\S/.test(line))
    .map(line => line.replace(/^\s*run:\s*/, '').trim());
}

export interface WorkflowStep {
  /** 步骤起始行号（1 基），用于失败信息定位。 */
  line: number;
  name: string;
  /** 该步骤所有 `run:` 的正文（多行 `run: |` 以换行拼接）。 */
  run: string;
  ifValue: string;
  continueOnError: string;
}

export interface GateFinding {
  stepLine: number;
  stepName: string;
  reason: string;
}

/** `- ` 列表项后紧跟这些键之一时才算一个 step（这些工作流里 steps 是唯一的列表）。 */
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
    // 单行形式（`- run: ...` / `- name: ...`）本身就是一个 step 起点；
    // 否则必须紧跟一个更缩进的步骤键（多行形式）。
    const singleLineKey = !!m && /^(name|uses|run|id|if|with|env|shell|working-directory|continue-on-error|timeout-minutes):/.test(line.slice(m[1].length + 2));
    const isStepStart = !!m && (singleLineKey
      || (next.length - next.trimStart().length > m[1].length && STEP_KEY.test(next)));
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

/** YAML 里 `if: false # disabled` 与 `if: false` 等价：先去掉行尾注释，再剥一层引号。 */
const stripComment = (value: string) => value.split('#')[0].trim();
/** 去掉行尾注释、外层引号与**全部空白**：`${{ true }}` 与 `${{true}}` 必须归一成同一串。 */
const normalize = (value: string) => stripComment(value).replace(/^['"]|['"]$/g, '').replace(/\s+/g, '');
/** 静态为真 / 静态为假：表达式一律不算（无法静态证明）。 */
const isStaticTrue = (value: string) => normalize(value) === 'true';
const isStaticFalse = (value: string) => normalize(value) === 'false' || normalize(value) === '0';

/**
 * 会「洗绿」的写法：把失败码换成 0 的任何组合。工作流默认 shell 是
 * `bash --noprofile --norc -eo pipefail`，因此只做子串匹配会漏掉 `|| :`、`|| exit 0`、
 * `|| /bin/true`、`||  true`（多空格）、`set +o errexit; …; true`、`|| $OK`（env 间接）等。
 * 这里先把空白归一，再按「连接符 + 必定成功的命令」判定。
 */
const GREEN_WASH_PATTERNS: Array<{ re: RegExp; label: string }> = [
  { re: /(\|\||&&|;)\s*(:|true|\/bin\/true|\/usr\/bin\/true|exit\s+0)\s*(?=$|[;&|\n])/m, label: '短路到必定成功的命令' },
  { re: /\bset\s+\+e\b/, label: '`set +e`（关掉失败即退出）' },
  { re: /\bset\s+\+o\s+errexit\b/, label: '`set +o errexit`（关掉失败即退出）' },
  { re: /\|\|\s*\$/, label: '短路到一个变量（可能是必定成功的命令）' },
];

const collapseSpaces = (value: string) => value.replace(/[ \t]+/g, ' ');

/** 单个步骤级「永远绿」写法（不改变 run 行正文的那些）。 */
export function checkStepAlwaysGreen(step: WorkflowStep, findings: GateFinding[]): void {
  const push = (reason: string) => findings.push({ stepLine: step.line, stepName: step.name || '(未命名步骤)', reason });
  // 只有「静态 false」可以接受；`${{ … }}` 表达式、`always()` 等都无法静态证明失败会变红。
  if (step.continueOnError && !isStaticFalse(step.continueOnError)) {
    push(`步骤带 \`continue-on-error: ${stripComment(step.continueOnError)}\`，不能证明失败会让 job 变红`);
  }
  // 只有「无 if」或「静态 true」可以接受：动态条件无法静态证明这一步一定会执行。
  if (step.ifValue && !isStaticTrue(step.ifValue)) {
    push(`步骤带 \`if: ${stripComment(step.ifValue)}\`，不能证明步骤一定会执行`);
  }
  const run = collapseSpaces(step.run);
  for (const { re, label } of GREEN_WASH_PATTERNS) {
    if (re.test(run)) push(`run 正文含${label}：\`${run.trim().slice(0, 120)}\``);
  }
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

  const expanded = collapseSpaces(gateExpanded);
  for (const { re, label } of GREEN_WASH_PATTERNS) {
    if (re.test(expanded)) {
      findings.push({ stepLine: 0, stepName: GATE_SCRIPT, reason: `${GATE_SCRIPT} 展开后含${label}` });
    }
  }
  return findings;
}

/**
 * `tests/client-web` 里在本工单之前就没有任何 npm script 引用的用例文件。
 *
 * 它们与 #376 同源（同一批「改了却永远不跑」），但修好它们要逐个补 tsconfig/脚本并确认
 * 用例本身能过，超出本工单范围 —— 已在 PR 里作为发现项列出。这里钉住存量，禁止再添新的。
 * 子目录用例用相对 `tests/client-web` 的 posix 路径登记（REQ-9 视野扩展后新增
 * `playwright/shellResponsive.spec.ts`：本次只纳入视野并登记，不补执行者）。
 */
export const PRE_EXISTING_ORPHANS: ReadonlySet<string> = new Set([
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
export const TEST_FILE_RE = /\.(test|spec)\.tsx?$/;

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

/**
 * 判据不变：用例文件必须被某个 npm script 按文件名显式引用。
 * - 顶层文件：按文件名（= 相对路径）判定，与旧棘轮一致。
 * - 子目录文件：只认**相对路径**，不再用 basename 兜底 —— 否则
 *   `playwright/autoRefreshInterval.test.ts` 会因为顶层同名文件被引用而躲过棘轮。
 */
export function isReferenced(file: string, scriptText: string): boolean {
  // 顶层文件时 file 就是文件名本身；嵌套文件时它是相对路径，两者都按「整串出现」判定。
  return scriptText.includes(file);
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
