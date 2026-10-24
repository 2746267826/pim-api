export interface NavItem {
  label: string;
  path: string;
  short: string;
}

/**
 * 左侧主导航条目。
 *
 * 注意：展览馆 / 状态信息 / 设备管理 / 应用知识库 四个板块已移入「设置」页入口
 * （见 SETTINGS_SECTION_ITEMS，issue #279），页面路由与功能保持不变，
 * 因此它们不再出现在这里。
 */
export const NAV_ITEMS: NavItem[] = [
  { label: '今日', path: '/today', short: '今' },
  { label: '日历', path: '/calendar', short: '历' },
  { label: '工作台', path: '/workbench', short: '工' },
  { label: '确认', path: '/confirmations', short: '确' },
  { label: '数据中心', path: '/data-center', short: '数' },
  { label: '提醒', path: '/reminders', short: '提' },
  { label: '报告', path: '/reports', short: '报' },
  { label: '习惯', path: '/habits', short: '习' },
  { label: '快速记录', path: '/quick-notes', short: '记' },
  { label: '文件', path: '/files', short: '文' },
  { label: '任务', path: '/tasks', short: '任' },
  { label: '电脑记录', path: '/pc-tracker', short: '电' },
  { label: '浏览器使用', path: '/pc-tracker/browser', short: '浏' },
  { label: '手机记录', path: '/mobile-records', short: '机' },
  { label: '历史位置', path: '/location-history', short: '位' },
  { label: '设置', path: '/settings', short: '设' },
];

/** 设置页「板块入口」卡片（issue #279）：从主导航移入，页面与 URL 不变。 */
export interface SettingsSectionItem {
  title: string;
  description: string;
  /** 卡片左侧徽标文字（与设置页既有卡片一致） */
  label: string;
  path: string;
}

export const SETTINGS_SECTION_ITEMS: SettingsSectionItem[] = [
  {
    title: '展览馆',
    description: '打开展览馆，查看周趋势与习惯打卡等可视化内容',
    label: '展',
    path: '/exhibition',
  },
  {
    title: '状态信息',
    description: '查看系统总体状态、数据质量、连接设备与工作站',
    label: '态',
    path: '/status',
  },
  {
    title: '设备管理',
    description: '管理已登记的移动设备：改名、查看明细、合并与删除',
    label: '设',
    path: '/devices',
  },
  {
    title: '应用知识库',
    description: '维护应用签名、分类规则与上下文知识',
    label: '库',
    path: '/app-knowledge-base',
  },
];

/**
 * 页头标题查找表（AppLayout 依据当前路径显示标题）。
 *
 * 与 NAV_ITEMS 分开维护：条目从主导航移入设置页后不再出现在 NAV_ITEMS 中，
 * 但页面头部仍需显示正确标题，而不是回退成默认值。
 */
export const PAGE_TITLE_ITEMS: NavItem[] = [
  ...NAV_ITEMS,
  ...SETTINGS_SECTION_ITEMS.map(item => ({
    label: item.title,
    path: item.path,
    short: item.label,
  })),
];
