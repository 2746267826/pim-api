import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import PcQualitySummary from '../PcQualitySummary';
import { describePcQualityLocatability } from '../pcQualityLocatability';
import type { PcQualityResponse } from '../../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-7
 * `pc/quality` 新增「缺数时段」与「本库滞后 vs 采集端停机」的可定位信息
 * （references/A §A-6），状态页 PC 体检卡片必须把它们显示出来。
 *
 * 响应样本形状取自真实接口（127.0.0.1:5859 = 合入 PR #370 的 Pim.Api + pim_test 镜像库）；
 * 三种 situation 的字段组合与后端 PcTrackerQualityService 的 staleCause/staleReason 判定一一对应。
 */

function daemonComponent(details: Record<string, string>) {
  return {
    key: 'daemon-upload',
    name: '守护程序上报',
    status: 'Critical' as const,
    message: '组件存在严重采集质量问题。',
    details,
  };
}

function trackerComponent(details: Record<string, string>) {
  return {
    key: 'tracker-events',
    name: '原生追踪事件',
    status: 'Warning' as const,
    message: '组件存在采集质量警告。',
    details,
  };
}

const STALE_HEARTBEAT_ISSUE = {
  code: 'stale-windows-daemon-heartbeat',
  severity: 'Critical' as const,
  componentKey: 'daemon-upload',
  message: 'Windows 守护程序心跳已过期。',
  nextStep: '重启 Windows 守护程序，并确认它能访问 API。',
};

/** 情形 A：缺数时段 + 本库内容与心跳一起停住（后端实测样本） */
function qualityFrozen(): PcQualityResponse {
  return {
    overallStatus: 'Critical',
    label: '故障',
    message: '存在严重采集问题。',
    checkedAt: '2026-09-30T17:44:35.0000000+00:00',
    components: [
      trackerComponent({
        eventCount: '1928',
        baselineMethod: '近 7 个业务日事件数中位数',
        baselineDays: '7',
        baselineEventCount: '401',
        currentDailyEventCount: '385.6',
        missingHourCount: '8',
        missingHours: '2026-09-26 16:00、2026-09-26 17:00、2026-09-28 22:00、2026-09-28 23:00',
        // 连续缺数时段改由结构化字段给出（WO-FRONTEND-PC-20261001 REQ-3 / AC-3.1）：
        // 本地 2026-09-26 16:00–18:00 = UTC 08:00–10:00；本地 2026-09-28 21:44–2026-09-29 04:00
        // = UTC 2026-09-28T13:44–2026-09-28T20:00。文案里的同形文字不再被解析。
        missingSegments: JSON.stringify([
          { startUtc: '2026-09-26T08:00:00.0000000+00:00', endUtc: '2026-09-26T10:00:00.0000000+00:00' },
          { startUtc: '2026-09-28T13:44:00.0000000+00:00', endUtc: '2026-09-28T20:00:00.0000000+00:00' },
        ]),
        coverageEmpty: 'False',
        disconnectedFromUtc: '2026-09-26T08:00:00.0000000+00:00',
      }),
      daemonComponent({
        dataHorizonUtc: '2026-09-28T13:43:25.3368200+00:00',
        contentHorizonUtc: '2026-09-28T13:44:00.0000000+00:00',
        databaseLagMinutes: '3120.6',
        heartbeatStaleAt: 'True',
        libraryFrozenAtHorizon: 'True',
        staleCause: 'content-and-heartbeat-frozen',
        staleReason: 'collector-heartbeat-stale',
      }),
    ],
    issues: [
      {
        code: 'tracker-events-missing-hours',
        severity: 'Warning',
        componentKey: 'tracker-events',
        message:
          '检测到 2 段连续缺数（本地时间）：2026-09-26 16:00–2026-09-26 18:00；2026-09-28 21:44–2026-09-29 04:00；最近一次中断自 2026-09-26 16:00 起。',
        nextStep: '检查采集端在该时段是否离线。',
      },
      {
        code: 'content-and-heartbeat-frozen',
        severity: 'Warning',
        componentKey: 'daemon-upload',
        message: '本库最新内容与心跳都停在 2026-09-28 21:44（距今 52 小时）。',
        nextStep: '对照 dataHorizonUtc、contentHorizonUtc、databaseLagMinutes 判断。',
      },
      STALE_HEARTBEAT_ISSUE,
    ],
    nextSteps: [],
  };
}

/** 情形 B：仅采集端心跳过期，本库内容与心跳同步（staleCause=none） */
function qualityCollectorStopped(): PcQualityResponse {
  return {
    ...qualityFrozen(),
    components: [
      trackerComponent({ missingHourCount: '0', missingHours: '', coverageEmpty: 'False' }),
      daemonComponent({
        dataHorizonUtc: '2026-09-28T13:43:25.0000000+00:00',
        contentHorizonUtc: '2026-09-28T13:44:00.0000000+00:00',
        databaseLagMinutes: '10.0',
        heartbeatStaleAt: 'True',
        libraryFrozenAtHorizon: 'False',
        staleCause: 'none',
        staleReason: 'collector-heartbeat-stale',
      }),
    ],
    issues: [STALE_HEARTBEAT_ISSUE],
  };
}

/** 情形 C：本库仍有更新的内容，只有心跳停（staleCause=collector-heartbeat-stale） */
function qualityHeartbeatChannel(): PcQualityResponse {
  const q = qualityCollectorStopped();
  return {
    ...q,
    components: [
      q.components[0],
      daemonComponent({
        dataHorizonUtc: '2026-09-20T00:00:00.0000000+00:00',
        contentHorizonUtc: '2026-09-28T13:44:00.0000000+00:00',
        databaseLagMinutes: '10.0',
        heartbeatStaleAt: 'True',
        libraryFrozenAtHorizon: 'False',
        staleCause: 'collector-heartbeat-stale',
        staleReason: 'collector-heartbeat-stale',
      }),
    ],
  };
}

/** 情形 D：本库内容落后于查询范围（staleCause=query-range-beyond-database-horizon） */
function qualityDatabaseLag(): PcQualityResponse {
  const q = qualityCollectorStopped();
  return {
    ...q,
    components: [
      q.components[0],
      daemonComponent({
        dataHorizonUtc: '2026-09-28T13:43:25.0000000+00:00',
        contentHorizonUtc: '2026-09-28T13:44:00.0000000+00:00',
        databaseLagMinutes: '3120.6',
        heartbeatStaleAt: 'False',
        libraryFrozenAtHorizon: 'False',
        rangeShortfallMinutes: '3121',
        staleCause: 'query-range-beyond-database-horizon',
        staleReason: 'none',
      }),
    ],
    issues: [
      {
        code: 'range-beyond-database-horizon',
        severity: 'Warning',
        componentKey: 'daemon-upload',
        message: '本库最新内容止于 2026-09-28 21:44，比查询范围末尾落后 52 小时 —— 这段范围在本库里没有数据。',
        nextStep: '确认查询范围是否超出了本库已同步的数据。',
      },
    ],
  };
}

describe('REQ-7 · AC-7.1 缺数时段可定位', () => {
  it('卡片显示连续缺数时段（起止时间）', () => {
    const { container } = render(<PcQualitySummary quality={qualityFrozen()} />);
    const text = container.textContent ?? '';
    expect(text).toContain('缺数时段');
    expect(text).toContain('2026-09-26 16:00–2026-09-26 18:00');
    expect(text).toContain('2026-09-28 21:44–2026-09-29 04:00');
  });

  it('缺数时段取自响应字段（details.missingSegments / missingHours），不是写死的', () => {
    const loc = describePcQualityLocatability(qualityFrozen());
    expect(loc.missingHourCount).toBe(8);
    expect(loc.missingHours).toContain('2026-09-26 16:00');
    expect(loc.missingSegments.join(' ')).toContain('2026-09-26 16:00–2026-09-26 18:00');
  });

  it('没有缺数时不显示缺数块', () => {
    const { container } = render(<PcQualitySummary quality={qualityCollectorStopped()} />);
    expect(container.textContent ?? '').not.toContain('缺数时段');
  });
});

describe('REQ-7 · AC-7.2 「本库滞后」与「采集端停机」可区分', () => {
  it('情形 A：一起停住 → 两种可能都列出，并给出判别字段取值', () => {
    const loc = describePcQualityLocatability(qualityFrozen());
    expect(loc.lagVerdict).toBe('ambiguous-frozen');
    const text = render(<PcQualitySummary quality={qualityFrozen()} />).container.textContent ?? '';
    expect(text).toContain('本库数据滞后');
    expect(text).toContain('采集端停机');
    // 判别字段必须真的取自响应
    expect(text).toContain('3120.6');
    expect(text).toContain('2026-09-28T13:44:00');
    expect(text).toContain('2026-09-28T13:43:25');
  });

  it('情形 B：仅心跳过期 → 判定为采集端心跳问题，明确「本库数据未见滞后」', () => {
    expect(describePcQualityLocatability(qualityCollectorStopped()).lagVerdict).toBe('collector-heartbeat');
    const text = render(<PcQualitySummary quality={qualityCollectorStopped()} />).container.textContent ?? '';
    expect(text).toContain('采集端心跳');
    expect(text).toContain('本库数据未见滞后');
    expect(text).not.toContain('本库数据滞后 或 采集端停机');
  });

  it('情形 C：本库内容比心跳新 → 心跳通道停，仍判「本库数据未见滞后」', () => {
    expect(describePcQualityLocatability(qualityHeartbeatChannel()).lagVerdict).toBe('heartbeat-channel');
    const text = render(<PcQualitySummary quality={qualityHeartbeatChannel()} />).container.textContent ?? '';
    expect(text).toContain('心跳');
    expect(text).toContain('本库数据未见滞后');
  });

  it('情形 D：本库落后于查询范围 → 判定为「本库数据滞后」', () => {
    expect(describePcQualityLocatability(qualityDatabaseLag()).lagVerdict).toBe('database-lag');
    const text = render(<PcQualitySummary quality={qualityDatabaseLag()} />).container.textContent ?? '';
    expect(text).toContain('本库数据滞后');
    expect(text).toContain('3121');
  });

  it('四种情形的界面结论两两不同（真的能区分，而不是同一句套话）', () => {
    const labels = [
      describePcQualityLocatability(qualityFrozen()).lagVerdictLabel,
      describePcQualityLocatability(qualityCollectorStopped()).lagVerdictLabel,
      describePcQualityLocatability(qualityHeartbeatChannel()).lagVerdictLabel,
      describePcQualityLocatability(qualityDatabaseLag()).lagVerdictLabel,
    ];
    expect(new Set(labels).size).toBe(4);
    // 「本库数据未见滞后」只出现在非滞后情形；「本库数据滞后」只出现在滞后/二义情形
    expect(labels[1]).toContain('未见滞后');
    expect(labels[2]).toContain('未见滞后');
    expect(labels[3]).toContain('本库数据滞后');
    expect(labels[3]).not.toContain('未见滞后');
  });
});

describe('REQ-7 · 原有内容不回归', () => {
  it('仍显示总体结论与问题列表', () => {
    const { container } = render(<PcQualitySummary quality={qualityFrozen()} />);
    const text = container.textContent ?? '';
    expect(text).toContain('PC 数据质量');
    expect(text).toContain('Windows 守护程序心跳已过期');
  });
});
