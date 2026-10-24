import { describe, it, expect } from 'vitest';
import { pcAggregationApiPaths, pcKeystatsScope } from '../../../api/pcTracker';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-6
 * 接入新增端点 `GET /api/v1/pc/aggregation/keystats?start&end`（references/A §A-5），
 * 范围模式恢复键盘/鼠标热力图；字段与单日 `summary.keystats` 同构，额外 `totalKeyPresses`。
 */

describe('REQ-6 · 键鼠范围聚合端点', () => {
  it('AC-6.1/6.2 · keystats 路径带 start/end', () => {
    expect(pcAggregationApiPaths.keystats({ start: '2026-08-29', end: '2026-09-27' }))
      .toBe('/pc/aggregation/keystats?start=2026-08-29&end=2026-09-27');
  });

  it('空参数不产生多余 ? （与其它聚合路径一致）', () => {
    expect(pcAggregationApiPaths.keystats({})).toBe('/pc/aggregation/keystats');
  });

  it('范围模式（day/month/year）走聚合端点，单日（hour）走 summary.keystats', () => {
    expect(pcKeystatsScope('hour')).toBe('single-day');
    expect(pcKeystatsScope('day')).toBe('range');
    expect(pcKeystatsScope('month')).toBe('range');
    expect(pcKeystatsScope('year')).toBe('range');
  });
});
