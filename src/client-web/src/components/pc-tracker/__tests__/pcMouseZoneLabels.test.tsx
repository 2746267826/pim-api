import { describe, it, expect, vi, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import KeyboardHeatmap, { type KeystatsView } from '../KeyboardHeatmap';
import { getPcKeystatsRange, getPcSummary, pcAggregationApiPaths, type KeystatsRangeSummary } from '../../../api/pcTracker';
import type { KeystatsSummary } from '../../../types';

/**
 * WO-FRONTEND-PC-20261001 · REQ-1 / AC-1.1 · AC-1.2 · AC-1.3 · AC-1.5
 *
 * 鼠标区过去把 `middleClicks`（滚轮键被按下的次数）挂在「滚轮」标签下，滚轮的真实量
 * `scrollDistance` 在界面上没有任何位置（interface 实测：middleClicks 1230 /
 * scrollDistance 474069.94，见 references/A §A-1）。本组用例锁定「名实相符」：
 * 每个鼠标标签显示的值必须来自同名的接口字段。
 */

/** 接口实测样本（references/A §A-1：近 30 天 2026-08-28 ~ 2026-09-27，范围聚合端点）。 */
const RANGE_KEYSTATS: KeystatsView = {
  keyPresses: 0,
  totalClicks: 242456,
  leftClicks: 185320,
  rightClicks: 32970,
  middleClicks: 1230,
  sideBackClicks: 573,
  sideForwardClicks: 22363,
  mouseDistance: 0,
  scrollDistance: 474069.9416666676,
  peakKps: 0,
  peakCps: 0,
  keyPressCounts: {},
  topKeys: [],
};

/** 同一份字段的单日视图（`summary.keystats`，多一个 `date`）——两条数据源共用同一渲染路径。 */
const SINGLE_DAY_KEYSTATS: KeystatsView = { ...RANGE_KEYSTATS, date: '2026-09-27' };

// ---------------------------------------------------------------------------
// REQ-4（#387）：两条数据源必须**由原始响应体驱动**并在取数层上被分别断言。
//
// 端点契约（`src/client-web/src/api/pcTracker.ts`）：
// - 范围：`pcAggregationApiPaths.keystats({start,end})` = `/pc/aggregation/keystats?start=…&end=…`
//   → `getPcKeystatsRange(start, end)` → `KeystatsRangeSummary`（与单日同构、无 `date`、多 `totalKeyPresses`）。
// - 单日：`/pc/summary?date=…` → `getPcSummary(date)` → `PcSummaryResponse.keystats`（`KeystatsSummary`，带 `date`）。
// 两个端点共用 `pcKeystatsScope()` 的选择逻辑，最终都渲染同一个 `KeyboardHeatmap`。
// ---------------------------------------------------------------------------

const RANGE_START = '2026-08-29';
const RANGE_END = '2026-09-27';
const SINGLE_DAY_DATE = '2026-09-27';

/** 范围端点原始响应体（键名即接口契约字段名，含实测样本值）。 */
const RANGE_RAW: KeystatsRangeSummary = {
  keyPresses: 0,
  totalClicks: 242456,
  leftClicks: 185320,
  rightClicks: 32970,
  middleClicks: 1230,
  sideBackClicks: 573,
  sideForwardClicks: 22363,
  mouseDistance: 0,
  scrollDistance: 474069.9416666676,
  peakKps: 0,
  peakCps: 0,
  keyPressCounts: {},
  topKeys: [],
  totalKeyPresses: 0,
};

/** 单日端点原始响应体中的 `keystats`（`PcSummaryResponse.keystats`，带 `date`）。 */
const SINGLE_DAY_RAW: KeystatsSummary = {
  date: SINGLE_DAY_DATE,
  keyPresses: 0,
  totalClicks: 242456,
  leftClicks: 185320,
  rightClicks: 32970,
  middleClicks: 1230,
  sideBackClicks: 573,
  sideForwardClicks: 22363,
  mouseDistance: 0,
  scrollDistance: 474069.9416666676,
  peakKps: 0,
  peakCps: 0,
  keyPressCounts: {},
  topKeys: [],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

/** 把 `fetch` 换成按端点返回原始响应体的桩，记录请求 URL。 */
function stubRawResponses(responseFor: (url: string) => unknown) {
  const calls: string[] = [];
  const fetchStub = vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : String(input);
    calls.push(url);
    return new Response(JSON.stringify(responseFor(url)), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchStub);
  return calls;
}

/** 范围数据源：原始响应体 → 取数层（真实 `apiGet`）→ 视图对象。 */
async function loadRangeKeystats(raw: KeystatsRangeSummary): Promise<KeystatsView> {
  const calls = stubRawResponses(url => {
    expect(url, '范围数据源必须走 /pc/aggregation/keystats').toContain(pcAggregationApiPaths.keystats());
    return { code: 0, message: 'ok', data: raw, timestamp: '2026-09-27T00:00:00Z' };
  });
  const data = await getPcKeystatsRange(RANGE_START, RANGE_END);
  expect(calls).toHaveLength(1);
  expect(calls[0]).toContain(`start=${RANGE_START}`);
  expect(calls[0]).toContain(`end=${RANGE_END}`);
  return data as KeystatsView;
}

/** 单日数据源：原始响应体 → 取数层（真实 `apiGet`）→ `summary.keystats`。 */
async function loadSingleDayKeystats(raw: KeystatsSummary): Promise<KeystatsView> {
  const calls = stubRawResponses(url => {
    expect(url, '单日数据源必须走 /pc/summary').toContain(`/pc/summary?date=${SINGLE_DAY_DATE}`);
    return {
      code: 0,
      message: 'ok',
      data: { keystats: raw, heatmap: [], appRanking: [], timeline: [], sessions: [], metrics: null, categories: [] },
      timestamp: '2026-09-27T00:00:00Z',
    };
  });
  const summary = await getPcSummary(SINGLE_DAY_DATE);
  expect(calls).toHaveLength(1);
  expect(summary.keystats).not.toBeNull();
  return summary.keystats as KeystatsView;
}

/** `aria-label` → 取值 的映射（逐项字段绑定的读数方式）。 */
function labelValueMap(container: HTMLElement): Map<string, string> {
  return new Map(
    readMouseZones(container).map(z => {
      const [label, value] = z.ariaLabel.split(': ');
      return [label, value];
    }),
  );
}

interface MouseZoneView {
  zone: string;
  /** 该区自己的可读标签，如「左键: 185,320」 */
  ariaLabel: string;
  /** 区内实际渲染出来的文字（标签 + 计数） */
  text: string;
}

function readMouseZones(container: HTMLElement): MouseZoneView[] {
  return [...container.querySelectorAll('[data-mouse-zone]')].map(node => ({
    zone: node.getAttribute('data-mouse-zone') ?? '',
    ariaLabel: node.getAttribute('aria-label') ?? '',
    text: (node.textContent ?? '').replace(/\s+/g, ''),
  }));
}

/** 卡片内除鼠标图形外另行呈现的滚轮量（AC-1.2：`scrollDistance` 必须在本卡片内有位置）。 */
function readScrollDistanceText(container: HTMLElement): string {
  const node = container.querySelector('[data-mouse-metric="scrollDistance"]');
  return (node?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

describe('WO-FRONTEND-PC-20261001 AC-1.1 · 鼠标区不得再把 middleClicks 挂在「滚轮」标签下', () => {
  it('范围数据源：没有任何鼠标区的可读标签把 1,230 说成滚轮', () => {
    const { container } = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    for (const zone of readMouseZones(container)) {
      expect(zone.ariaLabel).not.toContain('滚轮');
    }
    // #387 / REQ-5 / AC-5.1：对**真实渲染结构**断言，而不是对不可能出现的字符串做否定。
    // 中键区的可读标签必须是「中键: 1,230」，且其文本节点里不得出现「滚轮」。
    const middle = container.querySelector('[data-mouse-zone="middle"]');
    expect(middle).not.toBeNull();
    expect(middle!.getAttribute('aria-label')).toBe('中键: 1,230');
    const middleText = [...middle!.querySelectorAll('text')]
      .map(node => (node.textContent ?? '').replace(/\s+/g, ''))
      .join('');
    expect(middleText).toContain('中键');
    expect(middleText).toContain('1,230');
    expect(middleText).not.toContain('滚轮');
    // 滚轮量只出现在 `data-mouse-metric="scrollDistance"` 那一处，且为接口原值。
    expect(readScrollDistanceText(container)).toContain('474,069.94');
  });

  it('单日数据源：同样不出现「滚轮 = 中键次数」', () => {
    const { container } = render(<KeyboardHeatmap keystats={SINGLE_DAY_KEYSTATS} />);
    for (const zone of readMouseZones(container)) {
      expect(zone.ariaLabel).not.toContain('滚轮');
    }
  });

  it('负向对照：把 middleClicks 换成别的值，界面上的「滚轮」也不会跟着变（说明它已不绑定该字段）', () => {
    const { container } = render(
      <KeyboardHeatmap keystats={{ ...RANGE_KEYSTATS, middleClicks: 999999 }} />,
    );
    // 没有任何鼠标区的可读标签是「滚轮: <中键次数>」
    for (const zone of readMouseZones(container)) {
      const [label, value] = zone.ariaLabel.split(': ');
      expect(label === '滚轮' && value === '999,999').toBe(false);
      expect(zone.ariaLabel).not.toContain('滚轮');
    }
    // 滚轮量只认 scrollDistance，与 middleClicks 无关
    expect(readScrollDistanceText(container)).toContain('474,069.94');
  });
});

describe('WO-FRONTEND-PC-20261001 AC-1.2 · 中键标签必须始终可读（含高热度单日）', () => {
  /**
   * 真实数据反例（克隆库 pc_keystats_daily，单日 2026-07-28）：
   * 左键 342 / 右键 1 / 中键 215 / 侧键 0 —— 中键占 maxMouse 的 0.63。
   *
   * 鼠标区的字色过去是按「字画在色块上」选的（占比 > 0.42 就填白）。滚轮槽只有 18px，
   * 「中键」与计数被移到槽下方、落在浅色鼠标轮廓（#f1f5f9）上，白字会直接看不见。
   */
  const HIGH_MIDDLE_KEY = 215;
  const HIGH_LEFT_CLICK = 342;

  it('中键占比 > 0.42 时，槽下方的标签/计数不得用白字', () => {
    const { container } = render(
      <KeyboardHeatmap
        keystats={{
          ...RANGE_KEYSTATS,
          date: '2026-07-28',
          leftClicks: HIGH_LEFT_CLICK,
          rightClicks: 1,
          middleClicks: HIGH_MIDDLE_KEY,
          sideBackClicks: 0,
          sideForwardClicks: 0,
          totalClicks: HIGH_LEFT_CLICK + 1 + HIGH_MIDDLE_KEY,
        }}
      />,
    );
    const zone = container.querySelector('[data-mouse-zone="middle"]');
    expect(zone).not.toBeNull();
    const fills = [...zone!.querySelectorAll('text')].map(t => t.getAttribute('fill'));
    expect(fills.length).toBeGreaterThan(0);
    for (const fill of fills) {
      expect(fill, '槽下方的文字落在浅色鼠标轮廓上，不能是白色').not.toBe('#fff');
      expect(fill).not.toBe('#ffffff');
      expect(fill).not.toBe('white');
    }
    expect(zone!.textContent).toContain('215');
  });

  it('槽本身仍然按次数着色（热度不能被这次修复抹平）', () => {
    const { container } = render(
      <KeyboardHeatmap
        keystats={{
          ...RANGE_KEYSTATS,
          leftClicks: HIGH_LEFT_CLICK,
          rightClicks: 1,
          middleClicks: HIGH_MIDDLE_KEY,
          sideBackClicks: 0,
          sideForwardClicks: 0,
        }}
      />,
    );
    const hot = container.querySelector('[data-mouse-zone="middle"] rect');
    const cold = container.querySelector('[data-mouse-zone="side-back"] rect');
    expect(hot?.getAttribute('fill')).toBeTruthy();
    expect(hot?.getAttribute('fill')).not.toBe(cold?.getAttribute('fill'));
  });
});

describe('WO-FRONTEND-PC-20261001 AC-1.2 · 中键次数与滚轮量分别呈现', () => {
  it('中键次数单列，标签为「中键」，取值 1,230', () => {
    const { container } = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    const zones = readMouseZones(container);
    const middle = zones.find(z => z.ariaLabel.startsWith('中键'));
    expect(middle).toBeDefined();
    expect(middle!.ariaLabel).toBe('中键: 1,230');
    expect(middle!.text).toContain('1,230');
  });

  it('滚轮量（scrollDistance）在同一张鼠标卡片内呈现，为接口原值 474,069.94', () => {
    const { container } = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    const text = readScrollDistanceText(container);
    expect(text).toContain('滚轮量');
    expect(text).toContain('474,069.94');
  });

  it('滚轮量为 0 时按原值渲染 0 px，不留空也不伪造', () => {
    const { container } = render(
      <KeyboardHeatmap keystats={{ ...RANGE_KEYSTATS, scrollDistance: 0 }} />,
    );
    expect(readScrollDistanceText(container)).toMatch(/滚轮量\s*0 px/);
  });
});

describe('WO-FRONTEND-PC-20261001 AC-1.3 · 单日与范围两个数据源（经取数层驱动）', () => {
  /**
   * #387 / REQ-4 / AC-4.5：输入是**原始响应体**，经真实取数层（`getPcKeystatsRange` /
   * `getPcSummary`）变成视图对象后渲染；两条数据源各自断言一次，取值一律写成字面量期望，
   * 所以「响应里字段名错位」与「两个字段取值被互换」都会让用例失败（见 PR 中的变异证据）。
   */
  it('范围端点：原始响应字段与渲染取值一一对应', async () => {
    const keystats = await loadRangeKeystats(RANGE_RAW);
    const { container } = render(<KeyboardHeatmap keystats={keystats} />);
    const byLabel = labelValueMap(container);
    expect(byLabel.get('左键')).toBe('185,320');
    expect(byLabel.get('右键')).toBe('32,970');
    expect(byLabel.get('中键')).toBe('1,230');
    expect(byLabel.get('侧后')).toBe('573');
    expect(byLabel.get('侧前')).toBe('22,363');
    expect(readScrollDistanceText(container)).toMatch(/滚轮量\s*474,069\.94 px/);
    expect(container.textContent ?? '').toContain('242,456');
  });

  it('单日端点：同一组断言在 `summary.keystats` 链路上独立成立', async () => {
    const keystats = await loadSingleDayKeystats(SINGLE_DAY_RAW);
    const { container } = render(<KeyboardHeatmap keystats={keystats} />);
    const byLabel = labelValueMap(container);
    expect(byLabel.get('左键')).toBe('185,320');
    expect(byLabel.get('右键')).toBe('32,970');
    expect(byLabel.get('中键')).toBe('1,230');
    expect(byLabel.get('侧后')).toBe('573');
    expect(byLabel.get('侧前')).toBe('22,363');
    expect(readScrollDistanceText(container)).toMatch(/滚轮量\s*474,069\.94 px/);
    expect(container.textContent ?? '').toContain('242,456');
  });

  it('两条链路对同一份原始数据渲染出相同结果（相等是断言出来的，不是构造保证的）', async () => {
    const range = render(<KeyboardHeatmap keystats={await loadRangeKeystats(RANGE_RAW)} />);
    vi.unstubAllGlobals();
    const single = render(<KeyboardHeatmap keystats={await loadSingleDayKeystats(SINGLE_DAY_RAW)} />);
    const pick = (c: HTMLElement) =>
      readMouseZones(c).map(z => ({ zone: z.zone, ariaLabel: z.ariaLabel }));
    expect(pick(single.container)).toEqual(pick(range.container));
    expect(readScrollDistanceText(single.container)).toBe(readScrollDistanceText(range.container));
  });
});

describe('WO-FRONTEND-PC-20261001 AC-1.5 · 逐项字段绑定', () => {
  it('鼠标区各标签的取值等于同名接口字段', () => {
    const { container } = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    const byLabel = new Map(
      readMouseZones(container).map(z => {
        const [label, value] = z.ariaLabel.split(': ');
        return [label, value];
      }),
    );
    expect(byLabel.get('左键')).toBe('185,320');
    expect(byLabel.get('右键')).toBe('32,970');
    expect(byLabel.get('中键')).toBe('1,230');
    expect(byLabel.get('侧后')).toBe('573');
    expect(byLabel.get('侧前')).toBe('22,363');
    expect(readScrollDistanceText(container)).toContain('474,069.94');
  });

  it('总点击仍取 totalClicks（修复中键标签不得影响它）', () => {
    const { container } = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    expect(container.textContent ?? '').toContain('242,456');
  });
});
