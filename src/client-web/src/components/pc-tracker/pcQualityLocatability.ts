import type { PcQualityComponent, PcQualityResponse } from '../../types';

/**
 * WO-FRONTEND-PC-CONTRACT-20260930 · REQ-7
 *
 * 把 `pc/quality` 新增的可定位信息（references/A §A-6）整理成界面可直接渲染的结论：
 * - 「缺数时段」：`tracker-events.details.missingHours` 与 issue `tracker-events-missing-hours`；
 * - 「本库滞后 vs 采集端停机」：`daemon-upload.details` 的
 *   `dataHorizonUtc` / `contentHorizonUtc` / `databaseLagMinutes` / `heartbeatStaleAt`
 *   / `staleCause` / `staleReason`。
 *
 * 判定口径与后端 `PcTrackerQualityService.CheckDaemon` 的 `staleCause` 注释一一对应
 * （心跳红灯不因「库可能旧」而降级，所以判定只看时间关系，不把心跳年龄当成结论）：
 *
 * | staleCause | 含义 | 界面结论 |
 * | --- | --- | --- |
 * | `collector-heartbeat-stale` | 本库另有更新的内容，只有心跳停 | 心跳通道停，本库数据未见滞后 |
 * | `content-and-heartbeat-frozen` / `database-or-collector-frozen` | 心跳与内容一起停住 | 两种可能，需要取证 |
 * | `query-range-beyond-database-horizon` | 查询范围超出本库内容 | 本库数据滞后 |
 * | `none` + 心跳类 `staleReason` | 心跳过期但内容与心跳同步 | 采集端心跳过期，本库数据未见滞后 |
 * | `no-content` / `heartbeat-missing` | 无可比较的时间关系 | 无法判定 |
 */

export type PcQualityLagVerdict =
  | 'database-lag'
  | 'collector-heartbeat'
  | 'heartbeat-channel'
  | 'ambiguous-frozen';

export interface PcQualityLocatability {
  /** 缺数小时（本地时间字面量，来自响应字段） */
  missingHours: string[];
  missingHourCount: number | null;
  /** 连续缺数时段（从后端 issue 文案里解析出的「起–止」） */
  missingSegments: string[];
  /** 是否检测到缺数 */
  hasMissingHours: boolean;
  /** 本库数据最后内容时刻（UTC ISO） */
  contentHorizonUtc: string | null;
  /** 采集端最后心跳/数据时刻（UTC ISO） */
  dataHorizonUtc: string | null;
  databaseLagMinutes: number | null;
  heartbeatStaleAt: boolean | null;
  staleCause: string | null;
  staleReason: string | null;
  lagVerdict: PcQualityLagVerdict | null;
  /** 与 verdict 对应的一句界面结论（四种情形两两不同） */
  lagVerdictLabel: string | null;
  /** 判定依据（取自响应字段的取值，供用户自行取证） */
  lagEvidenceLabel: string | null;
}

const VERDICT_LABELS: Record<PcQualityLagVerdict, string> = {
  'database-lag': '本库数据滞后 —— 查询范围内这段时间在本库里没有数据。',
  'collector-heartbeat': '采集端心跳过期，本库数据未见滞后（本库内容与心跳同步停在同一时刻）。',
  'heartbeat-channel': '心跳通道已停止上报，本库数据未见滞后（本库另有比心跳更新的内容）。',
  'ambiguous-frozen': '本库数据滞后 或 采集端停机 —— 两者都可能，请按下方的判别字段取证后再下结论。',
};

function componentByKey(quality: PcQualityResponse | undefined, key: string): PcQualityComponent | undefined {
  return quality?.components.find(component => component.key === key);
}

function readDetail(details: Record<string, string> | undefined, key: string): string | null {
  const value = details?.[key];
  if (value === undefined || value === null) return null;
  const trimmed = String(value).trim();
  return trimmed === '' ? null : trimmed;
}

function readBool(details: Record<string, string> | undefined, key: string): boolean | null {
  const raw = readDetail(details, key);
  if (raw === null) return null;
  if (raw.toLowerCase() === 'true') return true;
  if (raw.toLowerCase() === 'false') return false;
  return null;
}

function readNumber(details: Record<string, string> | undefined, key: string): number | null {
  const raw = readDetail(details, key);
  if (raw === null) return null;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : null;
}

/** 从 issue 文案里解析「YYYY-MM-DD HH:mm–YYYY-MM-DD HH:mm」形式的缺数时段。 */
export function parseMissingSegments(message: string | null | undefined): string[] {
  if (!message) return [];
  const matches = message.match(/\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}[–-]\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}/g);
  return matches ? [...new Set(matches.map(m => m.replace(/-(\d{4}-\d{2}-\d{2})/g, '–$1')))] : [];
}

export function describePcQualityLocatability(quality: PcQualityResponse | undefined): PcQualityLocatability {
  const tracker = componentByKey(quality, 'tracker-events');
  const daemon = componentByKey(quality, 'daemon-upload');

  const missingHoursRaw = readDetail(tracker?.details, 'missingHours');
  const missingHours = missingHoursRaw
    ? missingHoursRaw.split(/[、,;；]/).map(item => item.trim()).filter(Boolean)
    : [];
  const missingHourCount = readNumber(tracker?.details, 'missingHourCount');
  const missingIssue = quality?.issues.find(issue => issue.code === 'tracker-events-missing-hours');
  const missingSegments = parseMissingSegments(missingIssue?.message);

  const contentHorizonUtc = readDetail(daemon?.details, 'contentHorizonUtc');
  const dataHorizonUtc = readDetail(daemon?.details, 'dataHorizonUtc');
  const databaseLagMinutes = readNumber(daemon?.details, 'databaseLagMinutes');
  const heartbeatStaleAt = readBool(daemon?.details, 'heartbeatStaleAt');
  const staleCause = readDetail(daemon?.details, 'staleCause');
  const staleReason = readDetail(daemon?.details, 'staleReason');
  const rangeBeyond = quality?.issues.some(issue => issue.code === 'range-beyond-database-horizon') ?? false;

  let lagVerdict: PcQualityLagVerdict | null = null;
  if (staleCause === 'collector-heartbeat-stale') {
    lagVerdict = 'heartbeat-channel';
  } else if (staleCause === 'content-and-heartbeat-frozen' || staleCause === 'database-or-collector-frozen') {
    lagVerdict = 'ambiguous-frozen';
  } else if (staleCause === 'query-range-beyond-database-horizon' || rangeBeyond) {
    lagVerdict = 'database-lag';
  } else if (staleCause === 'none' && (staleReason === 'collector-heartbeat-stale' || staleReason === 'collector-heartbeat-old')) {
    lagVerdict = 'collector-heartbeat';
  }

  const evidence: string[] = [];
  if (contentHorizonUtc) evidence.push(`本库最后内容 ${contentHorizonUtc}`);
  if (dataHorizonUtc) evidence.push(`采集端最后心跳 ${dataHorizonUtc}`);
  if (databaseLagMinutes !== null) evidence.push(`本库滞后 ${databaseLagMinutes} 分钟`);
  if (heartbeatStaleAt !== null) evidence.push(`心跳已过期：${heartbeatStaleAt ? '是' : '否'}`);
  const rangeShortfallMinutes = readNumber(daemon?.details, 'rangeShortfallMinutes');
  if (rangeShortfallMinutes !== null) evidence.push(`落后查询范围 ${rangeShortfallMinutes} 分钟`);

  return {
    missingHours,
    missingHourCount,
    missingSegments,
    hasMissingHours: missingHours.length > 0 || missingSegments.length > 0,
    contentHorizonUtc,
    dataHorizonUtc,
    databaseLagMinutes,
    heartbeatStaleAt,
    staleCause,
    staleReason,
    lagVerdict,
    lagVerdictLabel: lagVerdict ? VERDICT_LABELS[lagVerdict] : null,
    lagEvidenceLabel: evidence.length > 0 ? evidence.join('；') : null,
  };
}
