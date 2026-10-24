import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import PcQualitySummary from '../../src/client-web/src/components/pc-tracker/PcQualitySummary';
import type { PcQualityResponse } from '../../src/client-web/src/types';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const React = requireFromClient('react') as typeof import('react');
const { renderToStaticMarkup } = requireFromClient('react-dom/server') as typeof import('react-dom/server');
const reactGlobal = globalThis as typeof globalThis & { React: typeof React };
reactGlobal.React = React;

function test(name: string, run: () => void) { run(); }

const read = (relative: string) => readFileSync(path.join(process.cwd(), relative), 'utf8');
const pcTrackerPageSource = read('src/client-web/src/pages/PcTrackerPage.tsx');
const statusPageSource = read('src/client-web/src/pages/StatusPage.tsx');

// #281：电脑记录页顶部「PC 数据质量」板块与状态信息页重复，已从电脑记录页移除。
test('#281 电脑记录页不再渲染 PC 数据质量板块', () => {
  assert.equal(
    pcTrackerPageSource.includes('PcQualitySummary'),
    false,
    'PcTrackerPage 不应再引用 / 渲染 PcQualitySummary',
  );
  assert.equal(
    pcTrackerPageSource.includes('getPcQuality'),
    false,
    'PcTrackerPage 不应再请求 pc-quality 接口（板块已移除，避免无谓请求）',
  );
});

test('#281 状态信息页的 PC 数据质量板块保持不变', () => {
  assert.equal(
    statusPageSource.includes('PcQualitySummary'),
    true,
    'StatusPage 必须继续渲染 PcQualitySummary',
  );
  assert.equal(statusPageSource.includes('compact'), true, 'StatusPage 使用 compact 模式');
});

test('#281 电脑记录页其余板块保持不变', () => {
  for (const kept of ['PcReviewSummary', 'CategoryTimeline', 'ActivityAnalysisHeatmap', 'DateDimensionBar']) {
    assert.equal(
      pcTrackerPageSource.includes(kept),
      true,
      `PcTrackerPage 应保留 ${kept}`,
    );
  }
});

// 组件本身仍然可用（状态信息页依赖它），用最小数据渲染确认没有连带破坏。
test('#281 PcQualitySummary 组件仍能渲染出质量内容', () => {
  const quality: PcQualityResponse = {
    overallStatus: 'Available',
    label: '正常',
    message: '采集正常',
    checkedAt: '2026-09-16T10:00:00Z',
    components: [],
    issues: [],
    nextSteps: [],
  };

  const html = renderToStaticMarkup(
    React.createElement(PcQualitySummary, { quality, isLoading: false, error: null, compact: true }),
  );

  assert.ok(html.length > 0, 'compact 模式应产出非空标记');
});

console.log('pcQualityPlacement tests passed');
