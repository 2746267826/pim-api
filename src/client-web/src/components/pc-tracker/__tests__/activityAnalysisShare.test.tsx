import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ActivityAnalysisHeatmap from '../ActivityAnalysisHeatmap';
import type { PcActivityAnalysisBlock, PcActivityAnalysisResponse } from '../../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-2
 * AC-2.1 时间块提示出现「活跃 X 分钟」且 X ≤ 块分钟数（提示断言见 pcContractHeatmap.test.ts）。
 * AC-2.2 块内应用/分类占比条合计 ≤ 100%。
 */

function analysisWith(block: Partial<PcActivityAnalysisBlock>): PcActivityAnalysisResponse {
  return {
    date: '2026-09-27',
    blockMinutes: 60,
    blocks: [
      {
        start: '2026-09-27T03:00:00.0000000+00:00',
        end: '2026-09-27T04:00:00.0000000+00:00',
        intensityLevel: 5,
        intensityMax: 5,
        activeDurationSeconds: 3420,
        pendingClassificationCount: 56,
        contextSwitchCount: 22,
        categoryChangeCount: 47,
        categories: [
          { categoryName: '编程/折腾', color: '#6B5EE4', durationSeconds: 1382.95 },
          { categoryName: '文档', color: '#F59E0B', durationSeconds: 839.99 },
          { categoryName: '其他', color: '#64748b', durationSeconds: 787.06 },
          { categoryName: '浏览', color: '#0EA8A0', durationSeconds: 409.99 },
        ],
        apps: [
          { appName: 'zcode', durationSeconds: 1239.99 },
          { appName: 'explorer', durationSeconds: 839.99 },
          { appName: 'unknown', durationSeconds: 737.06 },
          { appName: 'msedge', durationSeconds: 289.99 },
          { appName: 'cs2eo.org', durationSeconds: 109.99 },
        ],
        ...block,
      },
    ],
  };
}

function sharesFrom(container: HTMLElement): number[] {
  return [...container.querySelectorAll('[data-share-bar]')].map(el => Number(el.getAttribute('data-share-percent')));
}

describe('REQ-2 · 时间块热力：活跃分钟与块内占比', () => {
  it('选中块展示「活跃 X 分钟」，且不超过块时长', () => {
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({})}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const text = container.textContent ?? '';
    const m = text.match(/活跃\s*([0-9]+)\s*分钟/);
    expect(m, `块内摘要应包含「活跃 X 分钟」，实际：${text}`).not.toBeNull();
    expect(Number(m![1])).toBeLessThanOrEqual(60);
  });

  it('分类与应用占比条各自合计 ≤ 100%', () => {
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({})}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const bars = container.querySelectorAll('[data-share-bar]');
    expect(bars.length).toBeGreaterThan(0);

    const groups = new Set([...bars].map(el => el.getAttribute('data-share-group')));
    expect(groups).toEqual(new Set(['categories', 'apps']));
    for (const group of groups) {
      const sum = sharesFrom(container)
        .filter((_, i) => bars[i].getAttribute('data-share-group') === group)
        .reduce((a, b) => a + b, 0);
      expect(sum, `${group} 占比合计 ${sum}% 应 ≤ 100%`).toBeLessThanOrEqual(100.0001);
      expect(sum, `${group} 占比合计不该为 0`).toBeGreaterThan(0);
    }
  });

  it('占比条数值与块时长一致（干净数据下 = durationSeconds / 块时长）', () => {
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({})}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const bars = [...container.querySelectorAll('[data-share-bar]')];
    const categoryBar = bars.find(el => el.getAttribute('data-share-group') === 'categories');
    // 1382.95 / 3600 = 38.4%
    expect(Number(categoryBar!.getAttribute('data-share-percent'))).toBeCloseTo(38.4, 0);
  });

  it('占比条明示只列出占比最高的 4 项，且确实按占比降序取前 4', () => {
    const many = Array.from({ length: 6 }, (_, i) => ({
      appName: `app-${i}`,
      durationSeconds: (i + 1) * 100,
    }));
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({ apps: many })}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const appBars = [...container.querySelectorAll('[data-share-bar][data-share-group="apps"]')];
    expect(appBars.length).toBe(4);
    // app-5 (600s) 最大，必须出现；app-0 (100s) 最小，必须被隐藏
    const text = container.textContent ?? '';
    expect(text).toContain('app-5');
    expect(text).not.toContain('app-0');
    expect(text).toContain('另有 2 项未展示');
  });

  it('后端返回乱序时仍取占比最高的前 4 项（文案与行为一致）', () => {
    const shuffled = [
      { appName: 'appSmall', durationSeconds: 60 },
      { appName: 'appHuge', durationSeconds: 900 },
      { appName: 'appMid', durationSeconds: 300 },
      { appName: 'appLarge', durationSeconds: 600 },
      { appName: 'appTiny', durationSeconds: 10 },
    ];
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({ apps: shuffled })}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const order = container.querySelector('[data-share-section="apps"]')!.textContent ?? '';
    expect(order.indexOf('appHuge')).toBeLessThan(order.indexOf('appLarge'));
    expect(order.indexOf('appLarge')).toBeLessThan(order.indexOf('appMid'));
    expect(order.indexOf('appMid')).toBeLessThan(order.indexOf('appSmall'));
    expect(order).not.toContain('appTiny');
  });

  it('起止颠倒的脏数据下，「活跃 X 分钟」与「块时长」用同一套长度（不自相矛盾）', () => {
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({
          start: '2026-09-27T04:00:00.0000000+00:00',
          end: '2026-09-27T03:00:00.0000000+00:00',
          activeDurationSeconds: 5960,
          categories: [{ categoryName: 'A', color: '#111111', durationSeconds: 600 }],
          apps: [],
        })}
        selectedStart={'2026-09-27T04:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const text = container.textContent ?? '';
    expect(text).toContain('活跃 60 分钟');
    // 块时长也应是 60（绝对值），不能出现「块时长（0 分钟）」
    expect(text).toContain('块时长（60 分钟）');
    expect(text).not.toContain('块时长（0 分钟）');
  });

  it('脏数据（各项之和超过块时长）时占比条仍合计 ≤ 100%', () => {
    const { container } = render(
      <ActivityAnalysisHeatmap
        analysis={analysisWith({
          categories: [
            { categoryName: 'A', color: '#111111', durationSeconds: 4000 },
            { categoryName: 'B', color: '#222222', durationSeconds: 4000 },
          ],
          apps: [{ appName: 'X', durationSeconds: 5000 }, { appName: 'Y', durationSeconds: 5000 }],
        })}
        selectedStart={'2026-09-27T03:00:00.0000000+00:00'}
        onSelectBlock={() => {}}
      />,
    );
    const bars = [...container.querySelectorAll('[data-share-bar]')];
    for (const group of ['categories', 'apps']) {
      const sum = bars
        .filter(el => el.getAttribute('data-share-group') === group)
        .reduce((a, el) => a + Number(el.getAttribute('data-share-percent')), 0);
      expect(sum, `${group} 占比合计应被夹到 ≤ 100%`).toBeLessThanOrEqual(100.0001);
    }
  });
});
