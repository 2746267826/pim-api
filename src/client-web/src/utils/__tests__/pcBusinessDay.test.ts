import { describe, it, expect } from 'vitest';
import {
  formatPcDate,
  formatSuggestionBusinessDate,
  getPcBusinessDate,
} from '../pcBusinessDay';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-3 / REQ-5 / REQ-8 的日期口径基础
 *
 * 业务日 = Asia/Shanghai 04:00 起算，且**不得**依赖浏览器时区。
 * Workbench 曾用「本地午夜的 UTC 日期」（`new Date(); setHours(0,0,0,0); toISOString().slice(0,10)`）
 * 查 PC 汇总，在 UTC+8 下整体差一天 —— 这里用边界时刻把它锁住。
 */

describe('业务日（Asia/Shanghai 04:00 起算）', () => {
  it('04:00 之前算前一天', () => {
    // 2026-09-26T18:00Z = 2026-09-27 02:00 +08:00 → 业务日 2026-09-26
    expect(formatPcDate(getPcBusinessDate(new Date('2026-09-26T18:00:00Z')))).toBe('2026-09-26');
    // 2026-09-26T19:59:59Z = 2026-09-27 03:59:59 +08:00 → 仍是 2026-09-26
    expect(formatPcDate(getPcBusinessDate(new Date('2026-09-26T19:59:59Z')))).toBe('2026-09-26');
  });

  it('04:00 起算当天', () => {
    // 2026-09-26T20:00:00Z = 2026-09-27 04:00 +08:00 → 业务日 2026-09-27
    expect(formatPcDate(getPcBusinessDate(new Date('2026-09-26T20:00:00Z')))).toBe('2026-09-27');
    // 2026-09-27T06:00:00Z = 14:00 +08:00 → 2026-09-27
    expect(formatPcDate(getPcBusinessDate(new Date('2026-09-27T06:00:00Z')))).toBe('2026-09-27');
  });

  it('业务日字符串与「本地午夜的 UTC 日期」不同（Workbench 回归对照）', () => {
    // 北京时间 2026-09-27 14:00：本地午夜 = 2026-09-27T00:00+08:00 = 2026-09-26T16:00Z，
    // 其 UTC 日期是 2026-09-26（差一天）；业务日是 2026-09-27。
    const instant = new Date('2026-09-27T06:00:00Z');
    expect(formatPcDate(getPcBusinessDate(instant))).toBe('2026-09-27');
  });

  it('跨年边界正确', () => {
    // 2025-12-31T18:00Z = 2026-01-01 02:00 +08:00 → 业务日 2025-12-31
    expect(formatPcDate(getPcBusinessDate(new Date('2025-12-31T18:00:00Z')))).toBe('2025-12-31');
    // 2025-12-31T20:00Z = 2026-01-01 04:00 +08:00 → 业务日 2026-01-01
    expect(formatPcDate(getPcBusinessDate(new Date('2025-12-31T20:00:00Z')))).toBe('2026-01-01');
  });
});

describe('建议业务日展示（REQ-5）', () => {
  it('合法 yyyy-MM-dd 原样返回', () => {
    expect(formatSuggestionBusinessDate('2026-09-04')).toBe('2026-09-04');
    expect(formatSuggestionBusinessDate(' 2026-09-04 ')).toBe('2026-09-04');
  });

  it('缺失或非法格式返回 null（不伪造日期）', () => {
    expect(formatSuggestionBusinessDate(undefined)).toBeNull();
    expect(formatSuggestionBusinessDate(null)).toBeNull();
    expect(formatSuggestionBusinessDate('')).toBeNull();
    expect(formatSuggestionBusinessDate('2026/09/04')).toBeNull();
    expect(formatSuggestionBusinessDate('not-a-date')).toBeNull();
  });
});
