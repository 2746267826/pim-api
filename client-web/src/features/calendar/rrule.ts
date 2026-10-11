/*
 * 日程重复规则的读写口径（PW-05 / I-1）。
 *
 * 契约（CreateEventRequest / UpdateEventRequest）里的字段名是 `rRule`；
 * 历史响应数据里出现过小写 `rrule`（规范文档 05/calendar.md 也用过）。
 * 读：两种形状都要认；写：只写契约字段名 `rRule`。
 */

/** 只关心重复规则字段的最小形状（响应可能是 rRule，也可能是旧的 rrule） */
export interface RruleCarrier {
  rRule?: string | null
  rrule?: string | null
}

/** 规范化：空串 / 非字符串按“没有规则”处理 */
function nonEmpty(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null
}

/**
 * 读取事件的重复规则。
 * 优先契约字段 `rRule`，缺失时回退到旧写法 `rrule`；两者都没有 → null。
 */
export function readEventRrule(event: RruleCarrier | null | undefined): string | null {
  if (!event) return null
  return nonEmpty(event.rRule) ?? nonEmpty(event.rrule)
}

/** 事件是否应当被标成“重复”（有规则或是系列主事件） */
export function isRepeatedEvent(
  event: (RruleCarrier & { isSeriesMaster?: boolean | null }) | null | undefined,
): boolean {
  if (!event) return false
  return readEventRrule(event) !== null || Boolean(event.isSeriesMaster)
}
