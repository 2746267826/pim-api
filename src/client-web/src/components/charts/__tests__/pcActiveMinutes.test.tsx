import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import PcActiveMinutesNote from '../../pc-tracker/PcActiveMinutesNote';
import { PC_ACTIVE_MINUTES_NOTE, formatActiveHours, sumHeatmapActiveMinutes } from '../pcTodayOptions';
import type { HeatmapBucket } from '../../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-8
 * `summary.heatmap.activeMinutes` 口径扩大（含只有键鼠输入、无窗口事件的分钟），
 * 同一业务日数值明显变大（实测 9/27：569 → 722.8 分钟）。
 * 要求：给出简短口径说明，且界面「活跃」数值与 summary.heatmap.activeMinutes 求和一致。
 * 注意：数值变大是上游有意统一口径，不得改回。
 */

function bucket(hour: number, activeMinutes: number): HeatmapBucket {
  return {
    start: '2026-09-26T20:00:00.0000000+00:00',
    end: '',
    hour,
    activeMinutes,
    totalEvents: 0,
    intensityLevel: 1,
    intensityMax: 5,
  };
}

describe('REQ-8 · AC-8.1 口径说明存在且含指定措辞', () => {
  it('说明文案提到「只有键鼠输入、无窗口记录的分钟」', () => {
    expect(PC_ACTIVE_MINUTES_NOTE).toContain('键鼠输入');
    expect(PC_ACTIVE_MINUTES_NOTE).toContain('无窗口记录');
  });

  it('说明文案明确「数值会变大」而不是把它当 bug', () => {
    expect(PC_ACTIVE_MINUTES_NOTE).toMatch(/大于|变大|多于/);
  });

  it('说明组件渲染出文案', () => {
    const { container } = render(<PcActiveMinutesNote />);
    const text = container.textContent ?? '';
    expect(text).toContain('键鼠输入');
    expect(text).toContain('无窗口记录');
  });
});

describe('REQ-8 · AC-8.2 界面「活跃」数值与 summary.heatmap 求和一致', () => {
  it('求和等于逐桶 activeMinutes 之和（实测 9/27 = 721 / 工单口径 722.8）', () => {
    const heatmap = [bucket(4, 0), bucket(5, 12.8), bucket(6, 60), bucket(7, 60), bucket(8, 588.2)];
    expect(sumHeatmapActiveMinutes(heatmap)).toBeCloseTo(721, 5);
  });

  it('缺数据时不抛错，返回 0', () => {
    expect(sumHeatmapActiveMinutes(undefined)).toBe(0);
    expect(sumHeatmapActiveMinutes([])).toBe(0);
  });

  it('小时展示保留一位小数，与接口分钟数换算一致', () => {
    expect(formatActiveHours(721)).toBe('12.0h');
    expect(formatActiveHours(722.8)).toBe('12.0h');
    expect(formatActiveHours(0)).toBe('0.0h');
  });

  it('口径变大不是 bug：569 分钟与 722.8 分钟都按原值展示，不夹到时间线覆盖时长', () => {
    expect(formatActiveHours(569)).toBe('9.5h');
    expect(formatActiveHours(722.8)).toBe('12.0h');
  });
});
