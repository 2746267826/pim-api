import type { MobileLivenessDeviceBlock, MobileLivenessOverview, MobileLivenessSeverity } from '../../api/mobile';

export function formatCoverage(value: number | null): string {
  if (value === null || !Number.isFinite(value)) return '—';
  return `${(value * 100).toFixed(1)}%`;
}

export function silenceSeverityClass(severity: MobileLivenessSeverity): string {
  if (severity === 'warning') return 'bg-amber-100 text-amber-900';
  if (severity === 'critical') return 'bg-red-100 text-red-900';
  return '';
}

/**
 * REQ-18 兑现率的展示文案。
 *
 * 三个要点，都是**口径**而非样式：
 * - 无闹钟数据（`null`）→ 明确空态，**不得显示 0%**（AC-18.2：0% 会被读成「保活完全失效」）；
 * - 确实执行但都不按时 → 显示 0%（这才是真的 0%）；
 * - 一并显示分子/分母，让读者能复算（与覆盖率的双口径做法一致）。
 */
export function formatFulfillment(
  fulfillment: { rate: number | null; fulfilled: number; considered: number; excludedNoActualTime: number } | null
): string {
  if (!fulfillment || fulfillment.rate === null) return '无数据（区间内没有已执行的叫醒）';
  const percent = `${(fulfillment.rate * 100).toFixed(1)}%`;
  return `${percent}（${fulfillment.fulfilled}/${fulfillment.considered} 次按时）`;
}

/** 兑现率的口径说明（AC-18.3：页面需说明分母排除规则）。 */
export const FULFILLMENT_DEFINITION =
  '兑现率：区间内实际执行且延迟不超过 15 分钟的叫醒次数 ÷ 有实际执行时刻的叫醒次数。设备关机或用户暂停导致的未执行不计入分母。';

export function formatUtc(value: string | null): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false });
}

export function formatPayload(payloadJson: string): string {
  try {
    return JSON.stringify(JSON.parse(payloadJson), null, 2);
  } catch {
    return payloadJson;
  }
}

export interface MobileLivenessGroup {
  title: string;
  devices: MobileLivenessDeviceBlock[];
}

export function groupDeviceBlocks(overview: MobileLivenessOverview): MobileLivenessGroup[] {
  return [
    { title: '手机', devices: overview.phones },
    { title: '平板', devices: overview.tablets },
    { title: '未分类机型', devices: overview.unclassified },
  ];
}
