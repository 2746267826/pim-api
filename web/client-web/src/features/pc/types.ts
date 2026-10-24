/* PC 活动追踪域（05/pc-tracker.md；读组大部分匿名，业务日 04:00 切分） */

export interface KeystatsSummary {
  date: string
  keyPresses: number
  totalClicks: number
  leftClicks: number
  rightClicks: number
  /** 中键点击数（规格 pc-tracker.md:29） */
  middleClicks: number
  /** 侧键后退点击数 */
  sideBackClicks: number
  /** 侧键前进点击数 */
  sideForwardClicks: number
  mouseDistance: number
  scrollDistance: number
  peakKps: number
  peakCps: number
  keyPressCounts: Record<string, number>
  /** 按键 Top 榜（规格 pc-tracker.md:38-40） */
  topKeys?: { keyName: string; count: number; share: number }[]
}

/** summary.heatmap 的逐小时桶（业务日 04:00 起算，24 桶） */
export interface HeatmapBucket {
  start: string
  end: string
  hour: number
  activeMinutes: number
  totalEvents: number
  /** 0–5 强度档（活跃分钟：≤5→1、≤15→2、≤30→3、≤45→4、>45→5） */
  intensityLevel: number
  /** 档位上限（=5），随响应下发 */
  intensityMax: number
}

/** heatmap/grid 的桶（day/month/year 维度；PC-4 修复后按业务日窗口切分） */
export interface HeatmapGridBucket {
  start: string
  end: string
  hour: number
  /** 业务日 yyyy-MM-dd（hour/day/month/year 四维度一律返回；本地 00:00–03:59 桶按 start 推会归错天，必须读此字段） */
  businessDay: string
  /** 该桶活跃分钟（day/month/year 维度现在有值） */
  activeMinutes: number
  totalEvents: number
  /** 键盘原始按键计数（色阶深浅用） */
  keyPressCount: number
  /** 0–5 强度档 */
  intensityLevel: number
  intensityMax: number
}

export interface HeatmapGridResponse {
  grid: HeatmapGridBucket[][]
  dimension: string
  maxKeyCount: number
}

export interface TimelineItem {
  start: string
  end: string
  durationMinutes: number
  appName: string
  windowTitle: string | null
  categoryName: string
  categoryColor: string
  classificationConfidence: number
}

export interface PcSummaryResponse {
  keystats: KeystatsSummary | null
  heatmap: HeatmapBucket[]
  timeline: TimelineItem[]
  sessions: { start: string; end: string; durationMinutes: number; mainApp: string; appSwitchCount: number }[]
  metrics: {
    totalRecordedDuration: string
    activeInputDuration: string
    idleDuration: string
    sessionCount: number
    activeAppCount: number
    totalKeyPresses: number
    totalClicks: number
    appSwitchCount: number
    switchFrequency: number
    mostFocusedApp: string
    keyClickRatio: number
  } | null
  categories: { categoryName: string; color: string; share: number; keyPresses: number; totalClicks: number }[]
}

export interface PcActivityAnalysis {
  date: string
  blockMinutes: number
  blocks: {
    start: string
    end: string
    /** 0–5 强度档（活跃时长占块时长比例） */
    intensityLevel: number
    intensityMax: number
    /** 活跃墙钟秒（重叠消解后，恒 ≤ 块时长） */
    activeDurationSeconds: number
    pendingClassificationCount: number
    contextSwitchCount: number
    categories: { categoryName: string; color: string; durationSeconds: number }[]
    /** 块内应用分布（后端实际返回，规格漏列） */
    apps: { appName: string; durationSeconds: number }[]
  }[]
}

export interface PcAppUsageItem {
  appName: string
  displayName: string | null
  totalMinutes: number
  percentage: number
}

export interface PcCategoryDistributionItem {
  categoryName: string
  color: string
  minutes: number
  percentage: number
}

export interface ProductivityDashboard {
  todayScore: number
  productiveHours: number
  distractingHours: number
  neutralHours: number
  targetHours: number
  goalMet: boolean
  weeklyTrend: { date: string; productiveMinutes: number; neutralMinutes: number; distractingMinutes: number; totalMinutes: number; productiveRatio: number }[]
}

export interface LabelingQueueItem {
  targetType: string
  target: string
  displayName: string
  minutes: number
  sampleTitles: string[]
  currentCategory: string | null
}

export interface CategoryDictionaryItem {
  id: string
  name: string
  color: string
  icon: string | null
}

export interface ContextSuggestion {
  id: string
  clusterKey: string
  sampleCount: number
  totalDurationSeconds: number
  currentCategory: string | null
  suggestedCategory: string | null
  appDisplayName: string | null
  status: string
  /** 该建议的业务日（后端 PC-6 修复后下发；date 参数只决定扫描哪天，返回的是全部待处理） */
  generatedForDate: string | null
}

/* ── 浏览器站点（browser-tt） ─────────────────────────────── */

export interface SiteSummary {
  from: string
  to: string
  totalFocusMs: number
  totalVisits: number
  totalRunMs: number
  totalMediaMs: number
  siteCount: number
  topHosts: { host: string; alias: string | null; focusMs: number; visitCount: number }[]
}

export interface SiteDailyRow {
  date: string
  host: string
  focusMs: number
  visitCount: number
  runMs: number
  mediaMs: number
}

export interface SiteTimelineRow {
  host: string
  startMs: number
  durationMs: number
}
