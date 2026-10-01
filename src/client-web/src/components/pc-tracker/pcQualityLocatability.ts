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

/**
 * 「连续缺数时段」的来源（WO-FRONTEND-PC-20261001 REQ-3）。
 *
 * - `details`：后端结构化字段 `details.missingSegments`（WO-PC-BACKEND-20261001 REQ-5），首选；
 * - `message-fallback`：字段缺失/解析失败时，退到本地化文案解析的**临时兜底**（必须同时给出
 *   可读提示，界面不得假装它是结构化字段给的）；
 * - `unavailable`：两条路都拿不到时刻 —— 时段不可用，界面必须明说并给出小时清单。
 */
export type PcMissingSegmentsSource = 'details' | 'message-fallback' | 'unavailable';

export interface PcQualityLocatability {
  /** 缺数小时（本地时间字面量，来自响应字段） */
  missingHours: string[];
  missingHourCount: number | null;
  /** 连续缺数时段（展示用「起–止」本地时间串） */
  missingSegments: string[];
  /** 上面的时段从哪来（AC-3.1 ~ AC-3.4） */
  missingSegmentsSource: PcMissingSegmentsSource;
  /** 时段不可用 / 只是兜底时的可读提示；字段可用时为 null */
  missingSegmentsNotice: string | null;
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

/** PC 模块墙钟固定 Asia/Shanghai（UTC+8，无夏令时），与后端业务日口径一致。 */
const SHANGHAI_OFFSET_MS = 8 * 60 * 60 * 1000;

/**
 * UTC ISO → 本地（Asia/Shanghai）「yyyy-MM-dd HH:mm」。
 *
 * 结构化字段给的是 `startUtc` / `endUtc`，而小时清单与旧文案都是本地时间；
 * 两者必须同口径，否则同一张卡片上会出现相差 8 小时的时段。
 */
export function formatLocalDateTime(value: string): string | null {
  const ms = Date.parse(value);
  if (Number.isNaN(ms)) return null;
  const shifted = new Date(ms + SHANGHAI_OFFSET_MS);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())} ${pad(shifted.getUTCHours())}:${pad(shifted.getUTCMinutes())}`;
}

/** 「起–止」形状（本地或 UTC 均可），用于识别后端可能直接给的字符串时段。 */
const SEGMENT_SHAPE = /\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}\s*[–—-]\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}/g;

function normalizeSegmentSeparator(value: string): string {
  return value.replace(/\s*[–—-]\s*(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2})/, '–$1');
}

/**
 * 从一个字符串里**抽出**所有「起–止」时段。
 *
 * 只认形状、不认整串：后端若把整句提示（如「检测到 1 段连续缺数（本地时间）：…」）
 * 塞进 `missingSegments`，也只取其中的时段，不把前后文案一起渲染到卡片上。
 */
function segmentsFromString(value: string): string[] {
  const matches = value.match(SEGMENT_SHAPE);
  return matches ? [...new Set(matches.map(normalizeSegmentSeparator))] : [];
}

/** 结构化元素 → 展示串列表；认不出来的元素产不出任何时段（不猜、不渲染半截）。 */
function segmentsFromItem(item: unknown): string[] {
  if (typeof item === 'string') return segmentsFromString(item);
  if (item && typeof item === 'object') {
    const record = item as Record<string, unknown>;
    const start = record.startUtc ?? record.start;
    const end = record.endUtc ?? record.end;
    if (typeof start !== 'string' || typeof end !== 'string') return [];
    const from = formatLocalDateTime(start);
    const to = formatLocalDateTime(end);
    if (!from || !to) return [];
    return [`${from}–${to}`];
  }
  return [];
}

export interface ParsedMissingSegments {
  /** 字段在响应里是否有非空取值 */
  present: boolean;
  /** 是否所有元素都解析成功（present 且 ok 才按结构化字段呈现） */
  ok: boolean;
  /** 展示用「起–止」串 */
  segments: string[];
}

/**
 * 解析 `details.missingSegments`（AC-3.1）。
 *
 * 后端 `PcQualityComponentDto.Details` 是 `IReadOnlyDictionary<string, string>`，所以该字段
 * 大概率是 JSON 字符串；这里同时接受几种形状，避免后端换序列化方式就整块失效：
 * 1. JSON 字符串（对象数组，元素含 `startUtc` / `endUtc`）—— 工单约定的形状；
 * 2. JSON 字符串（字符串数组，元素本身是「起–止」或含「起–止」的句子）；
 * 3. 裸字符串（整句提示或 `;`/`；`/换行 分隔的时段串）—— 只抽出其中的「起–止」。
 *
 * 空数组、认不出的内容都算**解析失败**（`ok: false`），由调用方给出降级提示，
 * 不允许默默当成「没有缺数时段」。
 */
export function parseMissingSegmentsField(raw: unknown): ParsedMissingSegments {
  if (raw === undefined || raw === null) return { present: false, ok: false, segments: [] };
  if (Array.isArray(raw)) return fromList(raw, true);
  if (typeof raw !== 'string') return fromList([raw], true);

  const trimmed = raw.trim();
  if (trimmed === '') return { present: false, ok: false, segments: [] };

  let items: unknown[];
  try {
    const parsed = JSON.parse(trimmed);
    items = Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    // 不是 JSON：整串按「起–止」抽取（分隔符与前后文案都不参与渲染）。
    const segments = segmentsFromString(trimmed);
    return { present: true, ok: segments.length > 0, segments };
  }
  return fromList(items, true);
}

function fromList(items: unknown[], present: boolean): ParsedMissingSegments {
  const parsed = items.map(segmentsFromItem);
  const segments = [...new Set(parsed.flat())];
  return { present, ok: segments.length > 0 && parsed.every(s => s.length > 0), segments };
}

function missingSegmentsNotice(
  parsed: ParsedMissingSegments,
  usedFallback: boolean,
): string | null {
  if (parsed.present && parsed.ok) return null;
  if (parsed.present) {
    // 字段在，但内容认不出来 —— 必须说出来，不能退化成「没有时段」。
    return usedFallback
      ? 'details.missingSegments 解析失败，以下时段由后端文案临时兜底解析；后端字段修好后会自动改为按字段渲染。'
      : 'details.missingSegments 解析失败，且后端文案中也没有可识别的时刻 —— 连续缺数时段不可用，请以下方小时清单为准。';
  }
  return usedFallback
    ? '接口未提供结构化缺数时段（details.missingSegments 缺失）：以下时段由后端文案临时兜底解析，后端补齐字段后会自动改为按字段渲染。'
    : '连续缺数时段不可用：接口未提供结构化时段字段（details.missingSegments 缺失），后端文案中也没有可识别的时刻；请以下方小时清单为准。';
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

  // AC-3.1：优先按结构化字段渲染；只有字段缺失/解析失败时才退到文案兜底（D-2），
  // 且无论哪条路都要留下可读提示（AC-3.2），不允许静默无时段。
  const parsedField = parseMissingSegmentsField(
    tracker?.details ? (tracker.details as Record<string, unknown>)['missingSegments'] : undefined,
  );
  const fallbackSegments = parsedField.present && parsedField.ok
    ? []
    : parseMissingSegments(missingIssue?.message);
  const useField = parsedField.present && parsedField.ok;
  const missingSegments = useField ? parsedField.segments : fallbackSegments;
  const missingSegmentsSource: PcMissingSegmentsSource = useField
    ? 'details'
    : fallbackSegments.length > 0
      ? 'message-fallback'
      : 'unavailable';
  const segmentsNotice = missingSegmentsNotice(parsedField, fallbackSegments.length > 0);

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
    missingSegmentsSource,
    missingSegmentsNotice: segmentsNotice,
    // `missingHourCount` 也算「有缺数」：字段解析不出来时小时清单可能为空串，但后端已经
    // 明确报了缺数小时数 —— 那时缺数块（含降级提示）必须照常出现，不能整块消失。
    hasMissingHours:
      missingHours.length > 0 || missingSegments.length > 0 || (missingHourCount ?? 0) > 0,
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
