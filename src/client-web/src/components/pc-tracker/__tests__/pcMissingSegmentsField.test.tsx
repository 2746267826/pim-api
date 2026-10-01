import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import PcQualitySummary from '../PcQualitySummary';
import { describePcQualityLocatability } from '../pcQualityLocatability';
import type { PcQualityComponent, PcQualityIssue, PcQualityResponse } from '../../../types';

/**
 * WO-FRONTEND-PC-20261001 · REQ-3 / AC-3.1 ~ AC-3.4
 *
 * 「连续缺数时段」过去唯一来源是 issue 的**本地化文案**正则（#377）：后端文案一改词、
 * 换分隔符或加单位，时段就静默消失。本组用例锁定新契约：
 * - AC-3.1 `details.missingSegments` 存在时直接按字段渲染，**不再解析文案**；
 * - AC-3.2 字段缺失或解析失败时，界面出现可读降级提示（说明时段不可用并给出小时清单），
 *          不允许静默无时段；
 * - AC-3.3 覆盖字段存在 / 字段缺失 / 文案不含时刻三种输入；
 * - AC-3.4 后端字段未交付前按「字段缺失」分支实现并显式标注，**不拿文案正则当长期方案**。
 *
 * 因此本地化文案里的「起–止」**不会**被渲染：文案含时段与不含时段，界面都是「时段不可用
 * ＋小时清单」，区别只在于不能静默 —— 这两条输入都要断言到。
 *
 * 响应形状取自隔离实例实测（127.0.0.1:5912 + 克隆库，业务日 2026-09-26，
 * 真实响应 details 只有 missingHourCount / missingHours，message 形如
 * 「检测到 1 段连续缺数（本地时间）：2026-09-26 16:00–2026-09-26 18:00；最近一次中断自 …」）。
 */

/** 后端实测：业务日 2026-09-26 的缺数（本地 16:00–18:00 = UTC 08:00–10:00）。 */
const MISSING_HOURS_LOCAL = '2026-09-26 16:00、2026-09-26 17:00';
const MISSING_HOURS_COUNT = '2';
/** 后端 issue 文案里那段「起–止」（旧实现唯一可用的来源）。 */
const MESSAGE_WITH_RANGE =
  '检测到 1 段连续缺数（本地时间）：2026-09-26 16:00–2026-09-26 18:00；最近一次中断自 2026-09-26 16:00 起。';
/** 文案改词后不再包含可识别时刻（#377 描述的场景）。 */
const MESSAGE_WITHOUT_RANGE = '检测到连续缺数，但本次未给出具体时段；最近一次中断时间未知。';

function trackerComponent(details: Record<string, string>): PcQualityComponent {
  return {
    key: 'tracker-events',
    name: 'PC 原生追踪事件',
    status: 'Warning',
    message: '组件存在采集质量警告。',
    details,
  };
}

function missingIssue(message: string): PcQualityIssue {
  return {
    code: 'tracker-events-missing-hours',
    severity: 'Warning',
    componentKey: 'tracker-events',
    message,
    nextStep: '检查采集端在该时段是否离线。',
  };
}

function quality(
  details: Record<string, string>,
  message: string | null = MESSAGE_WITH_RANGE,
): PcQualityResponse {
  return {
    overallStatus: 'Warning',
    label: '警告',
    message: '所选范围内的 PC 事实数据可靠性不足。',
    checkedAt: '2026-10-01T00:00:00.0000000+00:00',
    components: [trackerComponent(details), {
      key: 'daemon-upload',
      name: 'Windows 守护程序上传',
      status: 'Healthy',
      message: '组件状态正常。',
      details: { staleCause: 'none', staleReason: 'none' },
    }],
    issues: message === null ? [] : [missingIssue(message)],
    nextSteps: [],
  };
}

/** 卡片里的缺数块（找不到时为 null）。 */
function missingBlock(container: HTMLElement): string | null {
  const el = container.querySelector('[data-pc-quality-section="missing-hours"]');
  return el ? (el.textContent ?? '').replace(/\s+/g, ' ').trim() : null;
}

describe('WO-FRONTEND-PC-20261001 AC-3.1 · details.missingSegments 存在时按字段渲染', () => {
  const FIELD = JSON.stringify([
    { startUtc: '2026-09-26T08:00:00.0000000+00:00', endUtc: '2026-09-26T10:00:00.0000000+00:00' },
  ]);

  it('时段直接来自结构化字段（UTC → 本地时间）', () => {
    const loc = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT, missingSegments: FIELD }),
    );
    expect(loc.missingSegmentsSource).toBe('details');
    expect(loc.missingSegments).toEqual(['2026-09-26 16:00–2026-09-26 18:00']);
    expect(loc.missingSegmentsNotice).toBeNull();
  });

  it('字段存在时不再解析本地化文案：文案里的另一段时间不得出现在界面上', () => {
    const staleMessage =
      '检测到 1 段连续缺数（本地时间）：2020-01-01 00:00–2020-01-01 01:00；最近一次中断自 2020-01-01 00:00 起。';
    const { container } = render(
      <PcQualitySummary
        quality={quality(
          { missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT, missingSegments: FIELD },
          staleMessage,
        )}
      />,
    );
    const text = missingBlock(container) ?? '';
    expect(text).toContain('2026-09-26 16:00–2026-09-26 18:00');
    expect(text).not.toContain('2020-01-01 00:00–2020-01-01 01:00');
  });

  it('字段为字符串数组时同样可用（后端 details 是 Dictionary<string,string>，形状可能退化）', () => {
    const loc = describePcQualityLocatability(
      quality({
        missingHours: MISSING_HOURS_LOCAL,
        missingSegments: JSON.stringify(['2026-09-26 16:00–2026-09-26 18:00']),
      }),
    );
    expect(loc.missingSegmentsSource).toBe('details');
    expect(loc.missingSegments).toEqual(['2026-09-26 16:00–2026-09-26 18:00']);
  });

  it('字段里塞的是整句提示时，只抽出「起–止」，不把句子渲染到卡片上', () => {
    const loc = describePcQualityLocatability(
      quality({
        missingHours: MISSING_HOURS_LOCAL,
        missingSegments: '检测到 1 段连续缺数（本地时间）：2026-09-26 16:00–2026-09-26 18:00；最近一次中断自 2026-09-26 16:00 起。',
      }),
    );
    expect(loc.missingSegmentsSource).toBe('details');
    expect(loc.missingSegments).toEqual(['2026-09-26 16:00–2026-09-26 18:00']);
    expect(loc.missingSegmentsNotice).toBeNull();
  });

  it('一句里有多段时全部抽出（去重），顺序按出现顺序', () => {
    const loc = describePcQualityLocatability(
      quality({
        missingHours: MISSING_HOURS_LOCAL,
        missingSegments: JSON.stringify([
          '2026-09-26 16:00–2026-09-26 18:00 以及 2026-09-28 21:44–2026-09-29 04:00',
          '2026-09-26 16:00–2026-09-26 18:00',
        ]),
      }),
    );
    expect(loc.missingSegmentsSource).toBe('details');
    expect(loc.missingSegments).toEqual([
      '2026-09-26 16:00–2026-09-26 18:00',
      '2026-09-28 21:44–2026-09-29 04:00',
    ]);
  });
});

describe('WO-FRONTEND-PC-20261001 AC-3.2 / AC-3.4 · 字段缺失必须显式降级，不得静默', () => {
  it('字段缺失 + 文案含时段：不得把文案里的时段当数据渲染，但必须明说「不可用」并给出小时清单', () => {
    const loc = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT }),
    );
    expect(loc.missingSegmentsSource).toBe('unavailable');
    expect(loc.missingSegments).toEqual([]);
    expect(loc.missingSegmentsNotice).toContain('不可用');

    const { container } = render(
      <PcQualitySummary quality={quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT })} />,
    );
    const text = missingBlock(container) ?? '';
    expect(text).toContain('缺数时段');
    expect(text).toContain('不可用');
    // 文案里的那一段不得冒充结构化时段出现在列表里
    expect(text).not.toContain('连续缺数：2026-09-26 16:00–2026-09-26 18:00');
    // 小时清单必须同时在场（AC-3.2：给出小时清单）
    expect(text).toContain('2026-09-26 16:00');
    expect(text).toContain('2026-09-26 17:00');
  });

  it('字段缺失 + 文案不含时刻：明确说「时段不可用」，且小时清单还在', () => {
    const loc = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT }, MESSAGE_WITHOUT_RANGE),
    );
    expect(loc.missingSegmentsSource).toBe('unavailable');
    expect(loc.missingSegments).toEqual([]);
    expect(loc.missingSegmentsNotice).toContain('不可用');

    const { container } = render(
      <PcQualitySummary
        quality={quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT }, MESSAGE_WITHOUT_RANGE)}
      />,
    );
    const text = missingBlock(container) ?? '';
    expect(text).toContain('缺数时段');
    expect(text).toContain('不可用');
    expect(text).toContain('2026-09-26 16:00');
    expect(text).toContain('2026-09-26 17:00');
  });

  it('字段存在但解析失败：报「解析失败」，不静默丢弃，也不假装没有缺数', () => {
    const loc = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT, missingSegments: 'not-a-segment' }),
    );
    expect(loc.missingSegmentsSource).not.toBe('details');
    expect(loc.missingSegmentsNotice).toContain('解析失败');

    const { container } = render(
      <PcQualitySummary
        quality={quality({ missingHours: MISSING_HOURS_LOCAL, missingHourCount: MISSING_HOURS_COUNT, missingSegments: 'not-a-segment' })}
      />,
    );
    expect((missingBlock(container) ?? '')).toContain('解析失败');
  });

  it('字段存在但元素缺 startUtc/endUtc：按解析失败处理，不渲染半截时段', () => {
    // 文案里也不给时段，隔离出「字段本身半截」这一条路。
    const loc = describePcQualityLocatability(
      quality(
        {
          missingHours: MISSING_HOURS_LOCAL,
          missingSegments: JSON.stringify([{ startUtc: '2026-09-26T08:00:00Z' }, { endUtc: '2026-09-26T10:00:00Z' }]),
        },
        MESSAGE_WITHOUT_RANGE,
      ),
    );
    expect(loc.missingSegmentsSource).toBe('unavailable');
    expect(loc.missingSegments).toEqual([]);
    expect(loc.missingSegmentsNotice).toContain('解析失败');
  });
});

describe('WO-FRONTEND-PC-20261001 AC-3.3 · 字段存在 / 字段缺失 / 文案不含时刻', () => {
  it('字段存在 → 按字段渲染且无提示；其余两种输入 → 不可用且有提示（都不静默）', () => {
    const withField = describePcQualityLocatability(
      quality({
        missingHours: MISSING_HOURS_LOCAL,
        missingSegments: JSON.stringify([{ startUtc: '2026-09-26T08:00:00Z', endUtc: '2026-09-26T10:00:00Z' }]),
      }),
    );
    const withoutField = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL }),
    );
    const noRangeAnywhere = describePcQualityLocatability(
      quality({ missingHours: MISSING_HOURS_LOCAL }, MESSAGE_WITHOUT_RANGE),
    );

    expect(withField.missingSegmentsSource).toBe('details');
    expect(withField.missingSegments).toEqual(['2026-09-26 16:00–2026-09-26 18:00']);
    expect(withField.missingSegmentsNotice).toBeNull();

    // 后端字段未交付（真实现状）与文案也不含时刻，界面表现一致：不可用 + 提示，绝不静默
    for (const loc of [withoutField, noRangeAnywhere]) {
      expect(loc.missingSegmentsSource).toBe('unavailable');
      expect(loc.missingSegments).toEqual([]);
      expect(loc.missingSegmentsNotice).toBeTruthy();
      expect(loc.hasMissingHours).toBe(true);
    }
  });

  it('没有缺数时，三种提示都不出现（不误报）', () => {
    const { container } = render(
      <PcQualitySummary quality={quality({ missingHourCount: '0', missingHours: '' }, null)} />,
    );
    expect(container.querySelector('[data-pc-quality-section="missing-hours"]')).toBeNull();
    expect(container.textContent ?? '').not.toContain('解析失败');
  });
});
