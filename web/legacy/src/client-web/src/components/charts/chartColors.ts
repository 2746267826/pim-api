/**
 * PIM 色板常量：镜像 index.css 的 --pim-* CSS 变量（canvas 渲染不继承 CSS 变量，需显式传入）。
 * 与 src/client-web/src/index.css :root 中 --pim-primary/--pim-activity/--pim-warning/--pim-danger/
 * --pim-text-muted/--pim-border-soft/--pim-surface-muted 及热力色阶一一对应。
 */
export const chartColors = {
  /** --pim-primary */
  primary: '#2563eb',
  /** --pim-activity */
  activity: '#14b8a6',
  /** --pim-warning */
  warning: '#f59e0b',
  /** --pim-danger */
  danger: '#ef4444',
  /** --pim-text-muted */
  textMuted: '#64748b',
  /** --pim-border-soft */
  borderSoft: '#e2e8f0',
  /** --pim-surface-muted */
  surfaceMuted: '#f8fafc',
  /** 活动热力 teal 5 档色阶（--pim-surface-muted → --pim-activity → 深 teal） */
  heatmapTeal: ['#f8fafc', '#ccfbf1', '#5eead4', '#2dd4bf', '#0f766e'],
  /** 分类 7 色映射（沿用 CategoryLegacyMapper） */
  category: {
    '编程/折腾': '#6B5EE4',
    '学习': '#14b8a6',
    '视频': '#F97316',
    '聊天': '#3B82F6',
    '文档': '#F59E0B',
    '游戏': '#F43F5E',
    '其他': '#64748b',
  } as Record<string, string>,
  /** GitHub 风格贡献热力 5 档（任务 4 日历热力使用） */
  githubGreen: ['#ebedf0', '#9be9a8', '#40c463', '#30a14e', '#216e39'],
};
