import { describe, it, expect } from 'vitest';
import { buildActivityHeatmapOption, mapActivityGrid } from '../pcHeatmapOptions';
import type { HeatmapBucket, HeatmapGridResponse } from '../../../types';

/**
 * WO-FRONTEND-PC-20261001 · REQ-2 / AC-2.1 ~ AC-2.4
 *
 * `heatmap/grid?dimension=hour` 的桶按业务日 04:00 起算：业务日 D 的最后 4 个桶
 * （本地 D+1 00:00–03:00）在 +08:00 日历日上属于 D+1，工具提示过去就照日历日显示成次日
 * （实测：业务日 2026-09-27 的 `2026-09-27T16:00Z`~`19:00Z` 四个桶显示 2026-09-28）。
 * 后端在 WO-PC-BACKEND-20261001 REQ-7 给 hour 桶补 `businessDay`；字段到位前不得用
 * 日历日推断后**当作**业务日呈现，必须显式标注（AC-2.3）。
 *
 * 桶样本取自隔离实例实测（127.0.0.1:5912 + 克隆库，
 * `GET /api/v1/pc/heatmap/grid?start=2026-09-27&end=2026-09-27&dimension=hour`）。
 */

type Formatter = (params: unknown) => string;

function tooltipFormatter(option: unknown): Formatter {
  const t = (option as { tooltip?: { formatter?: unknown } }).tooltip;
  expect(typeof t?.formatter).toBe('function');
  return t!.formatter as Formatter;
}

function hourBucket(start: string, hour: number, extra: Partial<HeatmapBucket> = {}): HeatmapBucket {
  return {
    start,
    end: start,
    hour,
    activeMinutes: 0,
    totalEvents: 0,
    intensityLevel: 0,
    intensityMax: 5,
    keyPressCount: 0,
    ...extra,
  };
}

function gridResponse(dimension: string, cells: HeatmapBucket[]): HeatmapGridResponse {
  return { grid: [cells], dimension, maxKeyCount: 100 };
}

/** 工具提示里被点到的那一格。 */
function tooltipFor(dimension: string, bucket: HeatmapBucket): string {
  const option = buildActivityHeatmapOption(gridResponse(dimension, [bucket]));
  return tooltipFormatter(option)({ data: { bucket } });
}

/** 业务日 2026-09-27 的最后四个桶：本地 9/28 00:00–03:00，`start` = 2026-09-27T16:00Z ~ 19:00Z。 */
const LAST_FOUR_HOURS = [
  { start: '2026-09-27T16:00:00.0000000+00:00', hour: 0, clock: '00:00' },
  { start: '2026-09-27T17:00:00.0000000+00:00', hour: 1, clock: '01:00' },
  { start: '2026-09-27T18:00:00.0000000+00:00', hour: 2, clock: '02:00' },
  { start: '2026-09-27T19:00:00.0000000+00:00', hour: 3, clock: '03:00' },
];

describe('WO-FRONTEND-PC-20261001 AC-2.1 / AC-2.2 · hour 桶提示按业务日标注', () => {
  it('业务日 00:00–03:00 的四个桶显示业务日 2026-09-27，不再显示 2026-09-28', () => {
    for (const bucket of LAST_FOUR_HOURS) {
      const text = tooltipFor('hour', hourBucket(bucket.start, bucket.hour, { businessDay: '2026-09-27' }));
      expect(text, `${bucket.start} 必须按业务日标注`).toContain('业务日 2026-09-27');
      expect(text).not.toContain('2026-09-28');
      expect(text).toContain(bucket.clock);
    }
  });

  it('同一业务日清晨的桶（本地 04:00 起）同样显示 2026-09-27，四个维度口径一致', () => {
    const text = tooltipFor('hour', hourBucket('2026-09-26T20:00:00.0000000+00:00', 4, { businessDay: '2026-09-27' }));
    expect(text).toContain('业务日 2026-09-27');
    expect(text).toContain('04:00');
  });

  it('day 维度提示也优先用 businessDay 字段（同一业务日口径）', () => {
    const text = tooltipFor('day', hourBucket('2026-09-26T20:00:00.0000000+00:00', 0, { businessDay: '2026-09-27' }));
    expect(text).toContain('2026-09-27');
  });

  it('键数与活跃分钟仍在同一行（修复标注不得丢掉原有取值）', () => {
    const text = tooltipFor(
      'hour',
      hourBucket('2026-09-27T16:00:00.0000000+00:00', 0, { businessDay: '2026-09-27', keyPressCount: 574, activeMinutes: 5 }),
    );
    expect(text).toContain('574 次输入');
    expect(text).toContain('活跃 5 分钟');
  });
});

describe('WO-FRONTEND-PC-20261001 AC-2.3 · businessDay 缺失时不得把日历日当业务日', () => {
  it('字段缺失：不得出现「业务日」标注，且显式说明该日期是日历日、业务日不可用', () => {
    const text = tooltipFor('hour', hourBucket('2026-09-27T16:00:00.0000000+00:00', 0));
    expect(text).not.toContain('业务日 2026-09-28');
    expect(text).not.toContain('业务日 2026-09-27');
    expect(text).toContain('日历日');
    expect(text).toContain('未提供业务日');
    // 本地墙钟时刻仍然是事实，必须保留
    expect(text).toContain('2026-09-28');
    expect(text).toContain('00:00');
  });

  it('字段非法（格式不对 / 非字符串）按缺失处理，不拿它当业务日', () => {
    for (const bad of ['2026-9-27', '', '  ', 'not-a-date'] as unknown[]) {
      const text = tooltipFor('hour', hourBucket('2026-09-27T16:00:00.0000000+00:00', 0, { businessDay: bad as string }));
      expect(text, `businessDay=${JSON.stringify(bad)} 必须按缺失处理`).not.toMatch(/业务日 \d{4}/);
      expect(text).toContain('未提供业务日');
    }
  });

  it('day 维度在字段缺失时保持原行为（按 +08:00 算业务日，不需要降级标注）', () => {
    const text = tooltipFor('day', hourBucket('2026-09-26T20:00:00.0000000+00:00', 0));
    expect(text).toContain('2026-09-27');
    expect(text).not.toContain('未提供业务日');
  });
});

describe('WO-FRONTEND-PC-20261001 AC-2.4 · 覆盖字段存在与缺失两条分支', () => {
  it('同一格子在两种字段状态下给出不同且都能读懂的提示', () => {
    const withField = tooltipFor('hour', hourBucket('2026-09-27T16:00:00.0000000+00:00', 0, { businessDay: '2026-09-27' }));
    const withoutField = tooltipFor('hour', hourBucket('2026-09-27T16:00:00.0000000+00:00', 0));
    expect(withField).not.toBe(withoutField);
    expect(withField.startsWith('业务日 2026-09-27')).toBe(true);
    expect(withoutField.startsWith('2026-09-28 00:00')).toBe(true);
  });

  it('tooltip 只是标注，格子坐标不受影响（x 仍由 hour 字段决定）', () => {
    const map = mapActivityGrid(gridResponse('hour', [hourBucket('2026-09-27T16:00:00.0000000+00:00', 0)]));
    expect(map?.cells[0].x).toBe(20); // (0 - 4 + 24) % 24 = 20，即业务时序号 20
  });
});
