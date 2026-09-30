import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ActivityHeatmap from '../ActivityHeatmap';
import KeyboardHeatmap, { type KeystatsView } from '../KeyboardHeatmap';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-4 / REQ-6
 *
 * REQ-4：`dimension=hour` + 跨日现在返回 400（后端文案见 references/A §A-3）。
 *        前端必须把 400 显示成可读提示，不能吞掉当空数据渲染成一张空图。
 * REQ-6.3：范围键鼠聚合端点 400（起止颠倒 / 空范围）同样要给可读提示。
 */

// 后端 400 实测文案（references/A §A-3）
const HOUR_400_MESSAGE =
  'dimension=hour 仅支持单日范围（start 与 end 必须是同一天），当前收到 2026-09-26 ~ 2026-09-28。跨日请使用 dimension=day，或按天分别请求。';

const KEYSTATS_400_MESSAGE = 'start 不能晚于 end.';

describe('REQ-4 · hour 跨日 400 显示为可读提示', () => {
  it('有 error 时显示后端文案，而不是「暂无活动数据」', () => {
    const { container } = render(
      <ActivityHeatmap data={undefined} isLoading={false} error={new Error(HOUR_400_MESSAGE)} />,
    );
    const text = container.textContent ?? '';
    expect(text).toContain('仅支持单日范围');
    expect(text).not.toContain('暂无活动数据');
  });

  it('error 与 data 同时存在时优先显示错误（400 时 data 为 undefined）', () => {
    const { container } = render(
      <ActivityHeatmap data={undefined} isLoading={false} error={new Error(KEYSTATS_400_MESSAGE)} />,
    );
    expect(container.textContent).toContain('start 不能晚于 end');
  });

  it('无错误时保持原样（不误报）', () => {
    const { container } = render(
      <ActivityHeatmap
        data={{ grid: [[{ start: '2026-09-26T20:00:00.0000000+00:00', end: '', hour: 0, activeMinutes: 722, totalEvents: 0, intensityLevel: 4, intensityMax: 5, keyPressCount: 24949 }]], dimension: 'day', maxKeyCount: 24949 }}
        isLoading={false}
      />,
    );
    expect(container.textContent ?? '').not.toMatch(/失败|错误|不可用/);
  });
});

describe('REQ-6 · 键鼠范围聚合出错时显示可读提示', () => {
  const cached: KeystatsView = {
    keyPresses: 24949,
    totalClicks: 10474,
    leftClicks: 7409,
    rightClicks: 649,
    middleClicks: 39,
    sideBackClicks: 16,
    sideForwardClicks: 2361,
    mouseDistance: 100,
    scrollDistance: 10,
    peakKps: 15,
    peakCps: 11,
    keyPressCounts: { A: 3317 },
    topKeys: [],
  };

  it('keystats 请求 400 且没有任何数据时：显示后端文案，而不是「暂无键鼠数据」骨架或全 0 矩阵', () => {
    const { container } = render(
      <KeyboardHeatmap keystats={null} error={new Error(KEYSTATS_400_MESSAGE)} />,
    );
    const text = container.textContent ?? '';
    expect(text).toContain('start 不能晚于 end');
    expect(text).not.toContain('当前日期暂无键鼠数据');
    // 不能画一张「全是 0 次」的矩阵（会被读成真实数据）
    expect(container.querySelector('[aria-label="鼠标热力图"]')).toBeNull();
    expect(text).not.toContain('A\n0');
  });

  it('刷新失败但缓存里仍有上一次成功数据时：保留矩阵，只叠加错误条', () => {
    const { container } = render(
      <KeyboardHeatmap keystats={cached} error={new Error(KEYSTATS_400_MESSAGE)} />,
    );
    const text = container.textContent ?? '';
    expect(text).toContain('start 不能晚于 end');
    // 上一份成功数据必须还在（否则一次刷新失败会丢掉已经拿到的真实数据）
    expect(container.querySelector('[aria-label="鼠标热力图"]')).not.toBeNull();
    expect(text).toContain('7,409');
    expect(text).toContain('上次刷新失败');
  });
});
