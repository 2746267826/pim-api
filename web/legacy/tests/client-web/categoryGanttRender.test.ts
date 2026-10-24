/**
 * #282 回归护栏：分类时间线甘特图必须在**真实 ECharts 渲染路径**下产出可见的甘特条。
 *
 * 背景：#153 / #186 / #282 三次「整块空白」。根因是 renderItem 读 `params.value` /
 * `params.data` 取值取色，而 ECharts 真实调用 renderItem 时这两个字段都不存在
 * （只有 dataIndex / dataIndexInside / encode / itemPayload 等），于是：
 *   - 取值走兜底 [0,0,0] → api.coord 落在 1970 年 → x ≈ -1.8e8 px，画布之外；
 *   - 取色恒为灰色兜底 '#94a3b8'。
 * 旧用例用手造 mock 传入 { value, data } 直接调 renderItem，因此在真实渲染下永远测不出问题。
 *
 * 本用例把 option 交给真实 ECharts 渲染（SSR），只在外层包一层记录器收集
 * renderItem **真实收到的参数与真实返回的图形**，再断言这些图形落在画布内且颜色正确。
 */
import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
import { buildCategoryGanttOption } from '../../src/client-web/src/components/charts/pcHeatmapOptions';
import type { TimelineItem } from '../../src/client-web/src/types';

const requireFromClient = createRequire(path.join(process.cwd(), 'src/client-web/package.json'));
const echarts = requireFromClient('echarts') as typeof import('echarts');

function test(name: string, run: () => void) { run(); }

function timelineItem(start: string, end: string, categoryName: string, categoryColor: string, appName: string): TimelineItem {
  return {
    start,
    end,
    durationMinutes: Math.round((new Date(end).getTime() - new Date(start).getTime()) / 60000),
    appName,
    windowTitle: null,
    categoryName,
    categoryColor,
    projectTag: null,
    classificationConfidence: 0.9,
    classificationSource: 'builtin',
    classificationExplanation: '',
  };
}

interface CapturedRect {
  x: number;
  width: number;
  fill: string | undefined;
  /** 真实 ECharts 传入 renderItem 的 params 键（用于证明取到了什么、取不到什么）。 */
  paramKeys: string[];
  apiValue0: unknown;
  apiVisual: unknown;
}

/**
 * 用真实 ECharts（SSR）渲染 option，并记录 renderItem 的实参/返回值。
 * renderItem 仍由 ECharts 自己在渲染过程中调用，因此坐标系、encode、visual 都是真实的。
 */
function renderAndCapture(option: unknown, width = 900, height = 420) {
  const captured: CapturedRect[] = [];
  const series = (option as { series: Array<Record<string, unknown>> }).series;
  const original = series[0].renderItem as (params: unknown, api: unknown) => unknown;

  series[0].renderItem = (params: unknown, api: unknown) => {
    const a = api as { value?: (d: number) => unknown; visual?: (k: string) => unknown };
    const result = original(params, api) as { shape?: { x: number; width: number }; style?: { fill?: string } };
    captured.push({
      x: Number(result.shape?.x),
      width: Number(result.shape?.width),
      fill: result.style?.fill,
      paramKeys: Object.keys(params as object),
      apiValue0: a.value?.(0),
      apiVisual: a.visual?.('color'),
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

const threeSegments: TimelineItem[] = [
  timelineItem('2026-08-15T09:00:00+08:00', '2026-08-15T10:00:00+08:00', '编程', '#6B5EE4', 'Code.exe'),
  timelineItem('2026-08-15T10:00:00+08:00', '2026-08-15T11:00:00+08:00', '文档', '#F59E0B', 'msedge.exe'),
  timelineItem('2026-08-15T11:00:00+08:00', '2026-08-15T11:30:00+08:00', '编程', '#6B5EE4', 'Terminal'),
];

test('#282 renderItem 必须通过 api 取到真实数值（params.value/data 在真实渲染下不存在）', () => {
  const { captured } = renderAndCapture(buildCategoryGanttOption(threeSegments));
  assert.equal(captured.length, 3, '应有三条甘特条走到 renderItem');

  for (const rect of captured) {
    // 记录事实：ECharts 真实传入的 params 里没有 value / data（旧代码正是踩了这个坑）。
    assert.equal(rect.paramKeys.includes('value'), false, 'params 不应包含 value');
    assert.equal(rect.paramKeys.includes('data'), false, 'params 不应包含 data');
    // 因此必须走 api：api.value(0) 是真实分钟起点（0..60），不是兜底的 0。
    assert.equal(typeof rect.apiValue0, 'number', 'api.value(0) 应是数值');
    assert.ok(
      (rect.apiValue0 as number) >= 0 && (rect.apiValue0 as number) <= 60,
      `api.value(0) 应是 0..60 分钟值，实际 ${String(rect.apiValue0)}`,
    );
  }
});

test('#282 甘特条落在画布内（不是 1970 年坐标）', () => {
  const { captured, svg } = renderAndCapture(buildCategoryGanttOption(threeSegments));
  const chartWidth = 900;

  // 修复前 x ≈ -184730635（1970 年），且在 SVG 里表现为极长的负坐标路径。
  for (const rect of captured) {
    assert.ok(
      Number.isFinite(rect.x) && rect.x >= 0 && rect.x <= chartWidth,
      `甘特条 x 应落在 [0,${chartWidth}]，实际 ${rect.x}`,
    );
    assert.ok(rect.width > 2, `甘特条宽度应大于 2px 兜底值，实际 ${rect.width}`);
  }

  assert.equal(svg.includes('-184730'), false, 'SVG 不应出现异常负坐标');
});

test('#282 甘特条使用分类颜色而非灰色兜底', () => {
  const { captured, svg } = renderAndCapture(buildCategoryGanttOption(threeSegments));

  assert.ok(captured.some(r => r.fill === '#6B5EE4'), '应出现「编程」分类色 #6B5EE4');
  assert.ok(captured.some(r => r.fill === '#F59E0B'), '应出现「文档」分类色 #F59E0B');
  assert.equal(
    captured.some(r => r.fill === '#94a3b8'),
    false,
    '分类色存在时不应回退到灰色兜底 #94a3b8（params.data 取不到的征兆）',
  );

  assert.ok(svg.includes('#6B5EE4'), 'SVG 输出中应含分类色');
  assert.equal(svg.includes('#94a3b8'), false, 'SVG 输出中不应含灰色兜底');
});

test('#282 甘特条宽度与时间跨度成正比', () => {
  const { captured } = renderAndCapture(buildCategoryGanttOption([
    timelineItem('2026-08-15T09:00:00+08:00', '2026-08-15T09:30:00+08:00', '短', '#111111', 'a.exe'),
    timelineItem('2026-08-15T09:00:00+08:00', '2026-08-15T10:00:00+08:00', '长', '#222222', 'b.exe'),
  ]));

  assert.equal(captured.length, 2);
  const [shortBar, longBar] = captured;
  assert.ok(
    longBar.width > shortBar.width * 1.5,
    `60 分钟的条应约为 30 分钟条的 2 倍宽，实际 ${shortBar.width} vs ${longBar.width}`,
  );
});

test('#282 空时间线仍安全渲染（无甘特条但不报错）', () => {
  const { captured, svg } = renderAndCapture(buildCategoryGanttOption([]));
  assert.equal(captured.length, 0);
  assert.ok(svg.length > 0);
});

console.log('categoryGanttRender tests passed');
