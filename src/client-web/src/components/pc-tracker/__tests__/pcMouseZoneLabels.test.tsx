import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import KeyboardHeatmap, { type KeystatsView } from '../KeyboardHeatmap';

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
    expect(container.textContent ?? '').not.toContain('滚轮 1,230');
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

describe('WO-FRONTEND-PC-20261001 AC-1.3 · 单日与范围两个数据源呈现一致', () => {
  it('同一份数值下，单日视图与范围视图的鼠标区标签+取值逐项相同', () => {
    const range = render(<KeyboardHeatmap keystats={RANGE_KEYSTATS} />);
    const single = render(<KeyboardHeatmap keystats={SINGLE_DAY_KEYSTATS} />);
    const pick = (c: HTMLElement) =>
      readMouseZones(c).map(z => ({ zone: z.zone, ariaLabel: z.ariaLabel }));
    expect(pick(single.container)).toEqual(pick(range.container));
    expect(readScrollDistanceText(single.container)).toBe(readScrollDistanceText(range.container));
    // 不只「两边一样」，还要「两边都对」：光有 date 字段差异不得改变任何取值。
    for (const container of [range.container, single.container]) {
      expect(container.textContent ?? '').toContain('1,230');
      expect(readScrollDistanceText(container)).toMatch(/滚轮量\s*474,069\.94 px/);
    }
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
