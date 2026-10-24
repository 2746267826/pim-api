import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { echarts, type EChartsOption } from '../../src/client-web/src/lib/echarts';
import { chartColors } from '../../src/client-web/src/components/charts/chartColors';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
(globalThis as typeof globalThis & { React: typeof React }).React = React;
const EChartBox = require('../../src/client-web/src/components/charts/EChartBox').default;

function test(name: string, run: () => void) { run(); }

test('echarts core registers required charts without full bundle import', () => {
  assert.ok(echarts.init);
  const source = readFileSync('src/client-web/src/lib/echarts.ts', 'utf8');
  assert.ok(source.includes("from 'echarts/core'"));
  assert.ok(!source.includes("from 'echarts'"), 'should not import full echarts bundle with single quotes');
  assert.ok(!source.includes('from "echarts"'), 'should not import full echarts bundle with double quotes');
});

test('EChartBox renders accessible placeholder in static markup', () => {
  const option: EChartsOption = { series: [{ type: 'bar', data: [1] }] };
  const html = renderToStaticMarkup(React.createElement(EChartBox, { option, height: 120, ariaLabel: '测试图' }));
  assert.ok(html.includes('role="img"'));
  assert.ok(html.includes('aria-label="测试图"'));
  assert.ok(html.includes('height:120px'));
});

test('chart colors mirror pim css variables', () => {
  assert.equal(chartColors.primary, '#2563eb');
  assert.equal(chartColors.activity, '#14b8a6');
  assert.equal(chartColors.heatmapTeal[0], '#f8fafc');
  assert.equal(chartColors.heatmapTeal[4], '#0f766e');
  assert.equal(chartColors.category['编程/折腾'], '#6B5EE4');
});
console.log('echartsInfra tests passed');
