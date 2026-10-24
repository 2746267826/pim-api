import type { TodaySectionKind } from '../types';

/**
 * 今日页分区布局（2026-09-19 信息架构重排 v2）。
 *
 * 背景：今日页曾是「10+ 个模块无差别平铺」——4 张独立卡（待确认 / 微软同步 /
 * 提醒队列 / 报告）+ 6 个注册表区块 + 2 张图表嵌入卡，全部堆在首屏之后，
 * 没有主次、没有分区，用户需要滚动扫视才能找到「今天要干什么」。
 *
 * 重排为三个语义区（自上而下）：
 *   1. action —— 今天要处理的：日程 / 待办任务 / 分类建议（首屏，最高优先级）
 *   2. data   —— 数据回顾：PC 记录概览 + 周趋势 / 习惯图表
 *   3. status —— 运维与状态：系统健康 / 数据质量 / 待确认 / 同步等（折叠收纳）
 *
 * #285 防拉伸约束在新布局中同样成立：
 *   - 每个区内部是独立的 grid + items-start，区与区之间互不影响；
 *   - 长列表板块（任务 / 分类建议）在自身组件内独立滚动。
 */
export type TodayZone = 'action' | 'data' | 'status';

const ZONE_OF: Record<TodaySectionKind, TodayZone> = {
  'calendar.schedule': 'action',
  'calendar.tasks': 'action',
  'pc.classification_suggestions': 'action',
  'pc.activity': 'data',
  'operations.health': 'status',
  'pc.quality': 'status',
  // 2026-09-19 全量接入的 8 个模块：统一收纳在「运维与状态」折叠区。
  // （A 类：服务端就绪、此前前端未接；B 类：服务端 provider 待修复，修复后自动出数。）
  'operations.confirmations': 'status',
  'sync.outlook': 'status',
  'reminders.queue': 'status',
  'reports.available': 'status',
  'endpoints.status': 'status',
  'calendar.availability': 'status',
  'calendar.habits': 'status',
  'calendar.ai_placeholders': 'status',
};

/** 未知 / 未注册的区块统一归入 status（折叠收纳），不占用首屏。 */
export function todayZoneOf(kind: TodaySectionKind | string): TodayZone {
  return ZONE_OF[kind as TodaySectionKind] ?? 'status';
}
