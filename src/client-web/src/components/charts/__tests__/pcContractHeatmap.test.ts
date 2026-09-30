import { describe, it, expect } from 'vitest';
import {
  bucketDatePart,
  buildActivityHeatmapOption,
  buildAnalysisBlocksOption,
  mapActivityGrid,
} from '../pcHeatmapOptions';
import type {
  HeatmapBucket,
  HeatmapGridResponse,
  PcActivityAnalysisBlock,
} from '../../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-1 / REQ-2 / REQ-3 / REQ-9
 *
 * 后端 PR #370 已把强度字段改名：`heatmap/grid` 单元格用原始计数 `keyPressCount`
 * （上界 `maxKeyCount`），`summary.heatmap` 与 `activity-analysis` 用 0–5 档
 * `intensityLevel` + `intensityMax`。旧字段已从后端移除，
 * 前端若继续读它只会拿到 undefined（历史上是 `?? 0` 兜底 → 静默画空图）。
 */

/** grid 单元格（后端实测样本：/pc/heatmap/grid?dimension=day → grid[0][0]） */
function gridCell(start: string, keyPressCount: number, extra: Partial<HeatmapBucket> = {}): HeatmapBucket {
  return {
    start,
    end: '2026-09-27T20:00:00.0000000+00:00',
    hour: 0,
    activeMinutes: 722,
    totalEvents: 0,
    intensityLevel: 4,
    intensityMax: 5,
    keyPressCount,
    ...extra,
  };
}

function gridResponse(dimension: string, cells: HeatmapBucket[], maxKeyCount: number): HeatmapGridResponse {
  return { grid: [cells], dimension, maxKeyCount };
}

function block(over: Partial<PcActivityAnalysisBlock> = {}): PcActivityAnalysisBlock {
  return {
    start: '2026-09-27T03:00:00.0000000+00:00',
    end: '2026-09-27T04:00:00.0000000+00:00',
    intensityLevel: 5,
    intensityMax: 5,
    activeDurationSeconds: 3420,
    pendingClassificationCount: 56,
    contextSwitchCount: 22,
    categoryChangeCount: 47,
    categories: [],
    apps: [],
    ...over,
  };
}

type Formatter = (params: unknown) => string;

function tooltipFormatter(option: unknown): Formatter {
  const t = (option as { tooltip?: { formatter?: unknown } }).tooltip;
  expect(typeof t?.formatter).toBe('function');
  return t!.formatter as Formatter;
}

describe('REQ-1 · 四个维度热力图的着色取值改读 keyPressCount', () => {
  it('hour 维度 value 取 keyPressCount（不是档位）', () => {
    const map = mapActivityGrid(gridResponse('hour', [gridCell('2026-09-27T10:00:00.0000000+00:00', 3276, { hour: 18 })], 24949));
    expect(map?.cells).toHaveLength(1);
    expect(map?.cells[0].value).toBe(3276);
  });

  it('day 维度 value 取 keyPressCount', () => {
    const map = mapActivityGrid(gridResponse('day', [gridCell('2026-09-26T20:00:00.0000000+00:00', 24949)], 24949));
    expect(map?.cells[0].value).toBe(24949);
  });

  it('month 维度 value 取 keyPressCount', () => {
    const map = mapActivityGrid(gridResponse('month', [gridCell('2026-09-26T20:00:00.0000000+00:00', 1234)], 24949));
    expect(map?.cells[0].value).toBe(1234);
  });

  it('year 维度 value 取 keyPressCount', () => {
    const map = mapActivityGrid(gridResponse('year', [gridCell('2026-09-26T20:00:00.0000000+00:00', 777)], 24949));
    expect(map?.cells[0].value).toBe(777);
  });

  it('四个维度都不得把 0–5 档 intensityLevel 当作着色值', () => {
    // 同一单元格 keyPressCount=24949 而 intensityLevel=4：若取错字段会得到 4。
    for (const dimension of ['hour', 'day', 'month', 'year']) {
      const map = mapActivityGrid(gridResponse(dimension, [gridCell('2026-09-26T20:00:00.0000000+00:00', 24949)], 24949));
      expect(map?.cells[0].value, `${dimension} 必须用原始计数着色`).toBe(24949);
    }
  });

  it('热力图色阶上界仍取响应 maxKeyCount', () => {
    const option = buildActivityHeatmapOption(
      gridResponse('day', [gridCell('2026-09-26T20:00:00.0000000+00:00', 24949)], 24949),
    ) as { visualMap?: { max?: number } };
    expect(option.visualMap?.max).toBe(24949);
  });
});

describe('REQ-3 · 跨日贡献图的格子必须落在业务日坐标上（不是 UTC 日历日）', () => {
  // 后端 day/month/year 桶起点 = 业务日窗口 `[前一日 20:00Z, 当日 20:00Z)`，
  // 其 UTC 日历日恰好是业务日的**前一天**。格子画在哪一天必须按 +08:00 算，
  // 否则会出现「提示写 2026-09-27、格子画在 26 日/周六」的整体偏移（REQ-3 的反面行为）。
  const dayBucket = (start: string) => gridCell(start, 100, { activeMinutes: 10 });

  it('day 维度：业务日 2026-09-27（周日）画在「周日」列，不是周六', () => {
    const map = mapActivityGrid(gridResponse('day', [dayBucket('2026-09-26T20:00:00.0000000+00:00')], 100));
    expect(map?.cells[0].x).toBe(6); // 周日
    expect(map?.yLabels).toEqual(['2026-09-21']); // 2026-09-21 是周一
  });

  it('day 维度：业务日 2026-09-21（周一）画在「周一」列，且与 09-27 同一周', () => {
    const map = mapActivityGrid(gridResponse('day', [
      dayBucket('2026-09-20T20:00:00.0000000+00:00'), // 业务日 2026-09-21 周一
      dayBucket('2026-09-26T20:00:00.0000000+00:00'), // 业务日 2026-09-27 周日
    ], 100));
    const byStart = new Map(map!.cells.map(c => [c.bucket.start, c]));
    expect(byStart.get('2026-09-20T20:00:00.0000000+00:00')?.x).toBe(0);
    expect(byStart.get('2026-09-20T20:00:00.0000000+00:00')?.y).toBe(0);
    expect(byStart.get('2026-09-26T20:00:00.0000000+00:00')?.x).toBe(6);
    expect(byStart.get('2026-09-26T20:00:00.0000000+00:00')?.y).toBe(0);
    expect(map?.yLabels).toEqual(['2026-09-21']);
  });

  it('month 维度：跨月桶归属业务月份（08-31T20:00Z → 9 月 1 日，09-30T20:00Z → 10 月 1 日）', () => {
    const map = mapActivityGrid(gridResponse('month', [
      dayBucket('2026-08-31T20:00:00.0000000+00:00'),
      dayBucket('2026-09-26T20:00:00.0000000+00:00'),
      dayBucket('2026-09-30T20:00:00.0000000+00:00'),
    ], 100));
    const byStart = new Map(map!.cells.map(c => [c.bucket.start, c]));
    // 业务日 2026-09-01 → 9 月行、第 1 天（x=0）
    expect(byStart.get('2026-08-31T20:00:00.0000000+00:00')?.x).toBe(0);
    expect(byStart.get('2026-08-31T20:00:00.0000000+00:00')?.y).toBe(0);
    // 业务日 2026-09-27 → 9 月行、第 27 天（x=26）
    expect(byStart.get('2026-09-26T20:00:00.0000000+00:00')?.x).toBe(26);
    expect(byStart.get('2026-09-26T20:00:00.0000000+00:00')?.y).toBe(0);
    // 业务日 2026-10-01 → 10 月行、第 1 天（x=0）
    expect(byStart.get('2026-09-30T20:00:00.0000000+00:00')?.x).toBe(0);
    expect(byStart.get('2026-09-30T20:00:00.0000000+00:00')?.y).toBe(1);
    expect(map?.yLabels).toEqual(['2026-09', '2026-10']);
  });

  it('year 维度：跨年桶归属业务年份（2025-12-31T20:00Z → 2026-01-01 周四）', () => {
    const map = mapActivityGrid(gridResponse('year', [
      dayBucket('2025-12-31T20:00:00.0000000+00:00'),
      dayBucket('2026-09-26T20:00:00.0000000+00:00'),
    ], 100));
    const byStart = new Map(map!.cells.map(c => [c.bucket.start, c]));
    // 2026-01-01 是周四 → 行 3；该年的第 0 周（周一锚定）
    expect(byStart.get('2025-12-31T20:00:00.0000000+00:00')?.y).toBe(3);
    expect(byStart.get('2025-12-31T20:00:00.0000000+00:00')?.x).toBe(0);
    // 2026-09-27 是周日 → 行 6
    expect(byStart.get('2026-09-26T20:00:00.0000000+00:00')?.y).toBe(6);
  });

  it('工具提示日期与格子坐标同源（同一业务日，两处不得互相矛盾）', () => {
    const cell = dayBucket('2026-09-26T20:00:00.0000000+00:00');
    const map = mapActivityGrid(gridResponse('day', [cell], 100))!;
    const tooltipDate = tooltipFormatter(buildActivityHeatmapOption(gridResponse('day', [cell], 100)))({ data: { bucket: cell } });
    const weekdayLabels = ['周一', '周二', '周三', '周四', '周五', '周六', '周日'];
    // 提示里的日期是 2026-09-27（周日）→ 格子必须在「周日」列
    expect(tooltipDate).toContain('2026-09-27');
    expect(weekdayLabels[map.cells[0].x]).toBe('周日');
  });
});

describe('REQ-1 / REQ-9 · 网格工具提示显示原始键数与桶内活跃分钟', () => {  it('工具提示的键数与该单元格 keyPressCount 相等', () => {
    const cells = [
      gridCell('2026-09-26T20:00:00.0000000+00:00', 24949, { activeMinutes: 722 }),
      gridCell('2026-09-27T20:00:00.0000000+00:00', 1024, { activeMinutes: 90 }),
      gridCell('2026-09-28T20:00:00.0000000+00:00', 7, { activeMinutes: 3 }),
    ];
    const option = buildActivityHeatmapOption(gridResponse('day', cells, 24949));
    const fmt = tooltipFormatter(option);
    for (const cell of cells) {
      const text = fmt({ data: { bucket: cell } });
      expect(text).toContain(String(cell.keyPressCount));
      expect(text).toMatch(new RegExp(`${cell.keyPressCount}\\s*次输入`));
      // REQ-9：day/month/year 的 activeMinutes 已有真实值，提示里必须给出
      expect(text).toContain(String(cell.activeMinutes));
      expect(text).toMatch(/活跃/);
    }
  });

  it('工具提示日期按业务日归属（+08:00），不是原始 UTC 日期', () => {
    // 后端 day 桶起点：2026-09-26T20:00:00Z = 2026-09-27 04:00 (+08:00) → 业务日 2026-09-27
    expect(bucketDatePart('2026-09-26T20:00:00.0000000+00:00')).toBe('2026-09-27');
    expect(bucketDatePart('2026-09-27T20:00:00.0000000+00:00')).toBe('2026-09-28');
    // hour 桶同理
    expect(bucketDatePart('2026-09-27T10:00:00.0000000+00:00')).toBe('2026-09-27');
    expect(bucketDatePart('2026-09-26T16:00:00.0000000+00:00')).toBe('2026-09-27');
  });

  it('跨月边界的业务日归属正确（AC-3.1 口径）', () => {
    // 2026-08-31T20:00:00Z → 2026-09-01 04:00 (+08:00)
    expect(bucketDatePart('2026-08-31T20:00:00.0000000+00:00')).toBe('2026-09-01');
    // 2026-09-30T20:00:00Z → 2026-10-01 04:00 (+08:00)
    expect(bucketDatePart('2026-09-30T20:00:00.0000000+00:00')).toBe('2026-10-01');
  });
});

describe('REQ-1 / REQ-2 · 时间块热力用 intensityLevel 档位着色', () => {
  it('块取值取 intensityLevel（0–5 档），色阶上界用 intensityMax', () => {
    const blocks = [block({ intensityLevel: 5, intensityMax: 5 }), block({ intensityLevel: 2, intensityMax: 5 })];
    const option = buildAnalysisBlocksOption(blocks) as {
      visualMap?: { max?: number };
      series?: { data?: { value?: number[] }[] }[];
    };
    expect(option.visualMap?.max).toBe(5);
    expect(option.series?.[0]?.data?.[0]?.value?.[2]).toBe(5);
    expect(option.series?.[0]?.data?.[1]?.value?.[2]).toBe(2);
  });

  it('块提示出现「活跃 X 分钟」且 X ≤ 块分钟数', () => {
    const blocks = [
      block({ activeDurationSeconds: 3420 }), // 60 分钟块 → 57
      block({ activeDurationSeconds: 3600 }),
      block({ activeDurationSeconds: 0 }),
      block({ start: '2026-09-27T03:30:00.0000000+00:00', end: '2026-09-27T04:00:00.0000000+00:00', activeDurationSeconds: 1800 }),
    ];
    const option = buildAnalysisBlocksOption(blocks);
    const fmt = tooltipFormatter(option);
    blocks.forEach((b, i) => {
      const text = fmt({ data: { blockIndex: i } });
      const m = text.match(/活跃\s*([0-9]+)\s*分钟/);
      expect(m, `块 ${i} 提示应包含「活跃 X 分钟」，实际：${text}`).not.toBeNull();
      const blockMinutes = (new Date(b.end).getTime() - new Date(b.start).getTime()) / 60000;
      expect(Number(m![1])).toBeLessThanOrEqual(blockMinutes);
    });
  });

  it('不再出现「活跃 99 分钟」这类超限数值（历史 bug 回归）', () => {
    // 修复前：本地 11:00 块 activeDurationSeconds=5960（99.3 分钟）> 60 分钟块
    const option = buildAnalysisBlocksOption([block({ activeDurationSeconds: 5960 })]);
    const text = tooltipFormatter(option)({ data: { blockIndex: 0 } });
    const m = text.match(/活跃\s*([0-9]+)\s*分钟/);
    expect(Number(m![1])).toBeLessThanOrEqual(60);
  });
});
