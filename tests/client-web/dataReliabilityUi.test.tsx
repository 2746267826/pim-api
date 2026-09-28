import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import type {
  DataReliabilityInspectionReport,
  DataReliabilityRuleReport,
} from '../../src/client-web/src/api/dataReliabilityTypes';
import DataReliabilityPanel from '../../src/client-web/src/components/data-reliability/DataReliabilityPanel';
import DataReliabilityRuleDialog from '../../src/client-web/src/components/data-reliability/DataReliabilityRuleDialog';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;

/** SSR 会在相邻文本节点间插入 `<!-- -->` 注释，比较前先清掉并归一空白。 */
function text(markup: string): string {
  return markup.replace(/<!--.*?-->/g, '').replace(/\s+/g, ' ');
}

function rule(overrides: Partial<DataReliabilityRuleReport> & { code: string }): DataReliabilityRuleReport {
  return {
    invariantCode: `INV-${overrides.code}`,
    key: `${overrides.code}_INV`,
    order: Number(overrides.code.slice(1)) || 99,
    name: `尺子 ${overrides.code}`,
    group: 'SelfConsistency',
    groupLabel: '数据自洽',
    status: 'green',
    statusLabel: '绿',
    detail: '',
    currentValue: null,
    currentValueUnit: null,
    currentValueLabel: null,
    threshold: `阈值 ${overrides.code}`,
    criterion: `判据原文 ${overrides.code}`,
    rationale: `设定理由 ${overrides.code}`,
    relatedIssues: [],
    totalViolations: 0,
    windowViolations: 0,
    historicalViolations: 0,
    earliestOccurrenceUtc: null,
    latestOccurrenceUtc: null,
    samples: [],
    thresholdFallback: false,
    thresholdNote: null,
    coveredLayers: null,
    trend: 'unknown',
    trendDelta: null,
    trendBaselineUtc: null,
    threeState: null,
    scanTruncated: false,
    ...overrides,
  };
}

const rules: DataReliabilityRuleReport[] = [
  rule({ code: 'S1', status: 'red', statusLabel: '红', totalViolations: 14, windowViolations: 3, historicalViolations: 11, currentValue: 14, currentValueUnit: '对', relatedIssues: [249] }),
  rule({ code: 'S2', groupLabel: '数据自洽', currentValue: 4, threeState: {
    inputActiveSeconds: 159 * 60,
    mediaActiveSeconds: 90 * 60,
    suspectedUnclosedSeconds: 444 * 60,
    totalSeconds: (159 + 90 + 444) * 60,
    inputActiveCount: 1,
    mediaActiveCount: 1,
    suspectedUnclosedCount: 1,
    declaredGapSeconds: 536 * 60,
  } }),
  rule({ code: 'S3' }),
  rule({ code: 'S4' }),
  rule({ code: 'S5', status: 'yellow', statusLabel: '黄' }),
  rule({ code: 'S6', groupLabel: '覆盖完整' }),
  rule({ code: 'S7', groupLabel: '覆盖完整' }),
  rule({ code: 'S8', groupLabel: '覆盖完整' }),
  rule({ code: 'S9', groupLabel: '覆盖完整', status: 'unknown', statusLabel: '未知' }),
  rule({ code: 'S10', groupLabel: '链路健康' }),
  rule({ code: 'S11', groupLabel: '链路健康' }),
  rule({ code: 'S12', groupLabel: '链路健康' }),
  rule({ code: 'S13', groupLabel: '链路健康' }),
];

const report: DataReliabilityInspectionReport = {
  inspectedAtUtc: '2026-09-14T10:00:00Z',
  version: 3,
  elapsedMilliseconds: 812,
  status: 'red',
  redCount: 1,
  yellowCount: 1,
  greenCount: 10,
  unknownCount: 1,
  totalViolations: 14,
  windowViolations: 3,
  historicalViolations: 11,
  notices: {},
  rules,
  message: '体检完成',
};

// ---- 面板 ----
const panel = text(
  renderToStaticMarkup(
    React.createElement(DataReliabilityPanel, {
      report,
      now: new Date('2026-09-14T10:03:00Z'),
      onSelectRule: () => {},
    })
  )
);

for (const code of ['S1', 'S2', 'S3', 'S4', 'S5', 'S6', 'S7', 'S8', 'S9', 'S10', 'S11', 'S12', 'S13']) {
  assert.ok(panel.includes(code), `面板应展示尺子 ${code}`);
}

assert.ok(panel.includes('数据自洽'), '面板应有「数据自洽」分组');
assert.ok(panel.includes('覆盖完整'), '面板应有「覆盖完整」分组');
assert.ok(panel.includes('链路健康'), '面板应有「链路健康」分组');

// 顶部总览：红黄绿未知计数 + 本次体检时间 + 新鲜度
assert.ok(panel.includes('data-reliability-overview'), '面板应有总览计数');
assert.ok(panel.includes('🔴 1 条'), `总览应显示红 1 条: ${panel.slice(0, 400)}`);
assert.ok(panel.includes('🟡 1 条'), '总览应显示黄 1 条');
assert.ok(panel.includes('🟢 10 条'), '总览应显示绿 10 条');
assert.ok(panel.includes('⚪ 1 条'), '总览应显示未知 1 条');
assert.ok(panel.includes('本次体检时间'), '总览应显示本次体检时间');
assert.ok(panel.includes('3 分钟前'), '总览应显示体检结果新鲜度');
assert.ok(panel.includes('数据在流血'), '有红线时必须显式提示数据在流血');

// 状态徽标同时给出图标与中文，不能只靠颜色
assert.ok(panel.includes('🔴'), '红尺子应有红色图标');
assert.ok(panel.includes('>红<') || panel.includes('红 · 红'), '红尺子应有中文状态文字');
assert.ok(panel.includes('⚪'), '未知尺子应有灰色图标');

// 分档：新增 vs 存量
assert.ok(panel.includes('新增 3'), '应展示新增违规数');
assert.ok(panel.includes('存量 11'), '应展示存量违规数');
assert.ok(panel.includes('影响 14 条'), '应展示影响行数');

// S2 三态分布
assert.ok(panel.includes('data-testid="s2-three-state"'), 'S2 应有三态分布区域');
assert.ok(panel.includes('操作活跃'), 'S2 应展示操作活跃');
assert.ok(panel.includes('观看活跃'), 'S2 应展示观看活跃');
assert.ok(panel.includes('疑似未收尾'), 'S2 应展示疑似未收尾');
assert.ok(panel.includes('明确空档'), 'S2 应说明明确空档不计入活跃时长');

// 只读：面板不得出现一键修复入口
assert.ok(!/一键修复/.test(panel), '面板是只读的，不得提供一键修复');
assert.ok(!/<button[^>]*>[^<]*修复/.test(panel), '面板不得有任何修复按钮');

// 「重新体检」按钮位于页头（不在面板内），空态时也始终可用 —— 由 dataReliabilityResponsive 静态断言守护。

// ---- 下钻弹窗 ----
const s1 = rules[0];
const dialog = text(
  renderToStaticMarkup(
    React.createElement(DataReliabilityRuleDialog, {
      rule: s1,
      onClose: () => {},
      exportViolations: async () => ({
        ruleCode: 'S1',
        generatedAtUtc: '2026-09-14T10:00:00Z',
        totalCount: 0,
        truncated: false,
        items: [],
      }),
      now: () => new Date('2026-09-14T10:00:00Z'),
    })
  )
);

assert.ok(dialog.includes('S1'), '弹窗应显示尺子编号');
assert.ok(dialog.includes('判据'), '弹窗应显示判据原文标题');
assert.ok(dialog.includes('判据原文 S1'), '弹窗应显示判据原文');
assert.ok(dialog.includes('阈值'), '弹窗应显示阈值标题');
assert.ok(dialog.includes('阈值 S1'), '弹窗应显示阈值内容');
assert.ok(dialog.includes('为什么这么定'), '弹窗应解释阈值依据');
assert.ok(dialog.includes('设定理由 S1'), '弹窗应显示设定理由内容');
assert.ok(dialog.includes('新增 3'), '弹窗应显示新增违规数');
assert.ok(dialog.includes('存量 11'), '弹窗应显示存量违规数');
assert.ok(dialog.includes('导出完整违规清单'), '弹窗应提供完整清单导出');
assert.ok(dialog.includes('暂无违规样例'), '无样例时不得静默留白');
assert.ok(
  dialog.includes('https://github.com/2746267826/pim-platform/issues/249'),
  '弹窗应链接关联 issue'
);
assert.ok(!/一键修复/.test(dialog), '弹窗是只读的，不得提供一键修复');
assert.ok(!/<button[^>]*>[^<]*修复/.test(dialog), '弹窗不得有任何修复按钮');
assert.ok(dialog.includes('本面板只读'), '弹窗应声明面板只读');

// 有样例时最多展示 10 条
const manySamples = rule({
  code: 'S4',
  samples: Array.from({ length: 10 }, (_, index) => `重复行样例 ${index}`),
  totalViolations: 40,
  windowViolations: 0,
  historicalViolations: 40,
});
const sampleDialog = text(
  renderToStaticMarkup(
    React.createElement(DataReliabilityRuleDialog, {
      rule: manySamples,
      onClose: () => {},
      exportViolations: async () => ({}),
      now: () => new Date('2026-09-14T10:00:00Z'),
    })
  )
);
assert.ok(sampleDialog.includes('重复行样例 9'), '弹窗应展示样例');
assert.ok(!sampleDialog.includes('重复行样例 10'), '弹窗样例不应超过 10 条');

// 空规则时不渲染弹窗
const emptyDialog = renderToStaticMarkup(
  React.createElement(DataReliabilityRuleDialog, { rule: null, onClose: () => {} })
);
assert.equal(emptyDialog, '', '无选中尺子时不渲染弹窗');

console.error('PASS: dataReliabilityUi');
