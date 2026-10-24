export const PC_BUSINESS_DAY_START_HOUR = 4;

// 后端分类快照/聚合口径固定 Asia/Shanghai（无夏令时，UTC+8），业务日 04:00 起。
// 前端业务日计算必须与该口径一致，不能用浏览器时区（异地客户端会在 04:00 边界错一天）。
const SHANGHAI_OFFSET_MS = 8 * 60 * 60 * 1000;

export const PC_BUSINESS_HOURS = Array.from(
  { length: 24 },
  (_, index) => (index + PC_BUSINESS_DAY_START_HOUR) % 24,
);

/** 上海时区当前的业务日，返回 UTC 午夜 Date（表示该业务日）。格式化请用 formatPcDate。 */
export function getPcBusinessDate(date = new Date()) {
  const shanghai = new Date(date.getTime() + SHANGHAI_OFFSET_MS);
  const year = shanghai.getUTCFullYear();
  const month = shanghai.getUTCMonth();
  const day = shanghai.getUTCDate();
  const hour = shanghai.getUTCHours();
  const businessDay = hour < PC_BUSINESS_DAY_START_HOUR ? day - 1 : day;
  return new Date(Date.UTC(year, month, businessDay));
}

/** 业务日日期 → yyyy-MM-dd（UTC 字段，与后端 date 参数口径一致，不受浏览器时区影响）。 */
export function formatPcDate(date: Date) {
  const year = date.getUTCFullYear();
  const month = String(date.getUTCMonth() + 1).padStart(2, '0');
  const day = String(date.getUTCDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

export function pcHourLabel(hour: number) {
  return `${String(hour).padStart(2, '0')}:00`;
}

const CN_WEEKDAYS = ['日', '一', '二', '三', '四', '五', '六'];

/** 业务日 Date（UTC 午夜语义）→ 中文显示，如「2026年8月16日 星期日」（UTC 字段，不受浏览器时区影响）。 */
export function formatPcDateCn(date: Date) {
  const weekday = CN_WEEKDAYS[date.getUTCDay()];
  return `${date.getUTCFullYear()}年${date.getUTCMonth() + 1}月${date.getUTCDate()}日 星期${weekday}`;
}

/** UTC 日历加减天数（跨 DST 稳定），返回新 Date。 */
export function addPcDays(date: Date, days: number) {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate() + days));
}

/** UTC 日历加减月数（跨 DST 稳定，日期钳制到目标月末，与 date-fns subMonths 语义一致），返回新 Date。 */
export function addPcMonths(date: Date, months: number) {
  const year = date.getUTCFullYear();
  const month = date.getUTCMonth() + months;
  const targetYear = year + Math.floor(month / 12);
  const targetMonth = ((month % 12) + 12) % 12;
  const lastDay = new Date(Date.UTC(targetYear, targetMonth + 1, 0)).getUTCDate();
  return new Date(Date.UTC(targetYear, targetMonth, Math.min(date.getUTCDate(), lastDay)));
}

/**
 * 分类建议的业务日 → 展示文案（REQ-5）。
 *
 * 建议列表每条带后端下发的 `generatedForDate`（业务日，yyyy-MM-dd），历史行由
 * 「样本最新时刻 → 最后刷新时刻」回退推导。列表按日展示时必须用该字段，
 * 不能用请求的 `date` 参数（其语义是「扫描该业务日并刷新建议」，不代表返回范围）。
 * 缺字段或格式非法时返回 null，由调用方决定不展示，绝不伪造日期。
 */
export function formatSuggestionBusinessDate(value: string | null | undefined): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  if (!/^\d{4}-\d{2}-\d{2}$/.test(trimmed)) return null;
  const parsed = new Date(`${trimmed}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) return null;
  return trimmed;
}
