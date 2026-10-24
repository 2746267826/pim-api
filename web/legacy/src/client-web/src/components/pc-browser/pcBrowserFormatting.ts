/**
 * 浏览器使用页格式化工具：Ms → 中文时长、host 头像配色、上海时区小时坐标。
 * 时长口径与 mobileFormatting.formatDuration 类似，但输入为毫秒且按「1小时23分」格式输出。
 */

const HOST_AVATAR_COLORS = [
  '#3b82f6',
  '#8b5cf6',
  '#06b6d4',
  '#10b981',
  '#f59e0b',
  '#ef4444',
  '#ec4899',
  '#6366f1',
  '#14b8a6',
  '#f97316',
];

/** host 稳定哈希 → 头像背景色（同域名颜色固定，不同域名尽量分散）。 */
export function hostAvatarColor(host: string): string {
  let hash = 5381;
  for (let i = 0; i < host.length; i++) {
    hash = ((hash << 5) + hash + host.charCodeAt(i)) | 0;
  }
  const index = Math.abs(hash) % HOST_AVATAR_COLORS.length;
  return HOST_AVATAR_COLORS[index];
}

/** 头像字母：去掉 www. 前缀后取首个字母/数字（中文域名取首个字符）。 */
export function hostInitial(host: string): string {
  const normalized = (host || '').replace(/^www\./i, '').trim();
  const match = /[a-z0-9]/i.exec(normalized);
  const initial = match ? match[0] : normalized.charAt(0);
  return initial ? initial.toUpperCase() : '?';
}

/** 毫秒 → 中文短时长：1天2小时 / 1小时23分 / 45秒。 */
export function formatDurationMs(ms: number | null | undefined): string {
  const totalSeconds = Math.max(0, Math.round((ms ?? 0) / 1000));
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) return hours > 0 ? `${days}天${hours}小时` : `${days}天`;
  if (hours > 0) return minutes > 0 ? `${hours}小时${minutes}分` : `${hours}小时`;
  if (minutes > 0) return `${minutes}分`;
  return `${seconds}秒`;
}

/** 数量 → 千分位字符串。 */
export function formatBrowserCount(value: number | null | undefined): string {
  return Math.round(value ?? 0).toLocaleString('zh-CN');
}

const SHANGHAI_OFFSET_MS = 8 * 60 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;

/** epoch 毫秒 → 上海时区内的一天内小时数（0-24 浮点，画时段分布图用）。 */
export function shanghaiHourOfDay(startMs: number): number {
  const shifted = (startMs + SHANGHAI_OFFSET_MS) % DAY_MS;
  return ((shifted + DAY_MS) % DAY_MS) / 3600000;
}

/** 天内小时浮点（0-24）→ "HH:mm"。 */
export function formatHourLabel(hourFloat: number): string {
  const clamped = Math.min(24, Math.max(0, hourFloat));
  const totalMinutes = Math.round(clamped * 60);
  const hour = Math.min(23, Math.floor(totalMinutes / 60));
  const minute = totalMinutes >= 24 * 60 ? 59 : totalMinutes % 60;
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
}
