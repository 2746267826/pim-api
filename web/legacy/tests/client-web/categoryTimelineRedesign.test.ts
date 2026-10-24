import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { buildCategoryGanttOption, splitCategoryTimelineIntoChunks } from '../../src/client-web/src/components/charts/pcHeatmapOptions';
import type { TimelineItem } from '../../src/client-web/src/types';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const echarts = requireFromClient('echarts') as typeof import('echarts');

function test(name: string, run: () => void) { run(); }

function timelineItem(start: string, end: string, categoryColor = '#6B5EE4'): TimelineItem {
  return {
    start,
    end,
    durationMinutes: Math.round((new Date(end).getTime() - new Date(start).getTime()) / 60000),
    appName: 'Code.exe',
    windowTitle: '编辑器',
    categoryName: '编程',
    categoryColor,
    projectTag: null,
    classificationConfidence: 1,
    classificationSource: 'builtin',
    classificationExplanation: '',
  };
}

function renderAndCapture(option: unknown, width = 900, height = 240) {
  const captured: Array<{ x: number; width: number; fill: string | undefined; y: number }> = [];
  const series = (option as { series: Array<Record<string, unknown>> }).series;
  const original = series[0].renderItem as (params: unknown, api: unknown) => unknown;
  series[0].renderItem = (params: unknown, api: unknown) => {
    const result = original(params, api) as { shape?: { x: number; y: number; width: number }; style?: { fill?: string } };
    captured.push({
      x: Number(result.shape?.x),
      y: Number(result.shape?.y),
      width: Number(result.shape?.width),
      fill: result.style?.fill,
    });
    return result;
  };
  const chart = echarts.init(null, null, { renderer: 'svg', ssr: true, width, height });
  let svg = '';
  try {
    chart.setOption(option as never);
    svg = chart.renderToSVGString();
  } finally {
    chart.dispose();
  }
  return { captured, svg };
}

// 真实接口的 wire format 是 UTC（`Z` / `+00:00`），不是 +08:00：
//   curl '/api/v1/pc/summary?date=...' -> "2026-09-13T02:39:24.5180000Z"
// 05:36Z == 北京 13:36，因此这里用真实格式作为主夹具，同时断言上海墙钟结果。
const crossing = timelineItem('2026-08-15T05:36:00Z', '2026-08-15T06:02:00Z');

test('跨小时段拆成 13:36→60 与 14:00→2（UTC 入参、上海墙钟出参），轴为分钟且行升序', () => {
  const option = buildCategoryGanttOption([crossing]) as any;
  assert.equal(option.xAxis[0].type, 'value');
  assert.equal(option.xAxis[0].min, 0);
  assert.equal(option.xAxis[0].max, 60);
  assert.deepEqual(option.yAxis[0].data, ['13:00', '14:00']);
  assert.deepEqual(option.series[0].data.map((d: any) => d.value), [[36, 60, 0], [0, 2, 1]]);
  assert.equal(option.series[0].data[0].itemStyle.color, '#6B5EE4');
});

test('默认只显示有数据的小时，全时段切换显示 0–23 行', () => {
  const timeline = [crossing, timelineItem('2026-08-15T09:05:00+08:00', '2026-08-15T09:10:00+08:00', '#F59E0B')];
  const onlyData = buildCategoryGanttOption(timeline) as any;
  assert.deepEqual(onlyData.yAxis[0].data, ['09:00', '13:00', '14:00']);

  const allHours = buildCategoryGanttOption(timeline, true) as any;
  assert.equal(allHours.yAxis[0].data.length, 24);
  assert.equal(allHours.yAxis[0].data[0], '00:00');
  assert.equal(allHours.yAxis[0].data[23], '23:00');
  assert.deepEqual(allHours.series[0].data.map((d: any) => d.value), [[36, 60, 13], [0, 2, 14], [5, 10, 9]]);
});

test('跨小时的两条自定义图形在 SSR 画布内并使用分类色', () => {
  const { captured, svg } = renderAndCapture(buildCategoryGanttOption([crossing]));
  assert.equal(captured.length, 2);
  for (const rect of captured) {
    assert.ok(Number.isFinite(rect.x) && rect.x >= 0 && rect.x <= 900, `x 应在画布内，实际 ${rect.x}`);
    assert.ok(rect.width > 2, `条宽应大于 2px，实际 ${rect.width}`);
    assert.equal(rect.fill, '#6B5EE4');
  }
  assert.ok(svg.includes('#6B5EE4'));
});

// review 发现：切片必须按固定 +08:00 墙钟计算，不能用浏览器本地时区。
// 非 UTC+8 的用户否则会看到整体平移的小时行（UTC 下 13:36 落到 5 点行），
// 在有夏令时的时区还会出现重复 / 跳过的整点行。
// 本用例断言的是「与运行时时区无关」这一性质：同一输入在不同 TZ 下结果一致，
// 且与 Asia/Shanghai 的墙钟一致。CI 以 TZ=UTC 运行，因此这里也显式断言上海墙钟值。
test('切片按固定 +08:00 墙钟计算，不随浏览器时区漂移', () => {
  const chunks = splitCategoryTimelineIntoChunks([crossing]);
  // crossing = 13:36–14:02 (+08:00)
  assert.deepEqual(
    chunks.map(c => [c.hour, c.minuteStart, c.minuteEnd]),
    [[13, 36, 60], [14, 0, 2]],
    `在 TZ=${Intl.DateTimeFormat().resolvedOptions().timeZone} 下切片应仍按上海墙钟得到 13/14 行`,
  );
});

test('跨午夜与多小时的切片在每个 TZ 下都落在上海墙钟小时上', () => {
  const overnight = timelineItem('2026-08-15T22:30:00+08:00', '2026-08-16T01:20:00+08:00', '#6B5EE4');
  const chunks = splitCategoryTimelineIntoChunks([overnight]);
  assert.deepEqual(
    chunks.map(c => [c.hour, c.minuteStart, c.minuteEnd]),
    [[22, 30, 60], [23, 0, 60], [0, 0, 60], [1, 0, 20]],
  );
  // 每个 chunk 都必须是合法区间，且时长之和等于原段时长
  for (const c of chunks) {
    assert.ok(c.minuteStart >= 0 && c.minuteEnd <= 60 && c.minuteEnd > c.minuteStart,
      `非法 chunk: ${JSON.stringify(c)}`);
  }
  const total = chunks.reduce((sum, c) => sum + (c.minuteEnd - c.minuteStart), 0);
  assert.equal(total, 170, '切片时长之和应等于原段时长（22:30–01:20 = 170 分钟）');
});

console.log('categoryTimelineRedesign tests passed');
