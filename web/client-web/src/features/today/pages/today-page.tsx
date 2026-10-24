import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router'
import { ChevronDown, ChevronRight, Hammer } from 'lucide-react'
import { useBusinessDate, useTodayRegistry, useTodaySection } from '../queries'
import { TodayPulse } from '../components/today-pulse'
import type { TodaySectionRef, TodaySectionStatus } from '../api'
import { formatDuration, formatRange, formatTime } from '@/lib/datetime'
import { Card, CardTitle, EmptyState, InlineAlert, PageHeader, Skeleton, StatusBadge, type Tone } from '@/components/ui'
import { Stagger, StaggerItem } from '@/components/motion/primitives'
import { cn } from '@/lib/utils'

const STATUS_TONE: Record<TodaySectionStatus, Tone> = {
  available: 'ok',
  normal: 'ok',
  empty: 'neutral',
  warning: 'warn',
  critical: 'crit',
  unavailable: 'neutral',
}

const STATUS_LABEL: Record<TodaySectionStatus, string> = {
  available: '可用',
  normal: '正常',
  empty: '空',
  warning: '警告',
  critical: '故障',
  unavailable: '不可用',
}

/* ── 分区元数据：中文标题（卡片名不再直接展示 kind 英文串） ──── */

const SECTION_LABEL: Record<string, string> = {
  'calendar.schedule': '今日日程',
  'calendar.tasks': '今日任务',
  'calendar.habits': '习惯',
  'calendar.availability': '可用时间',
  'calendar.ai_placeholders': 'AI 排程建议',
  'pc.activity': '电脑活动',
  'pc.classification_suggestions': '分类建议',
  'pc.quality': 'PC 数据质量',
  'operations.confirmations': '待确认操作',
  'operations.health': '系统健康',
  'endpoints.status': '设备端点',
  'reminders.queue': '提醒队列',
  'reports.available': '报告',
  'sync.outlook': 'Outlook 同步',
}

/** 分区副标题：说明该卡片的取数范围（点击可跳转到对应详情页） */
const SECTION_HINT: Record<string, { hint: string; to?: string }> = {
  'calendar.schedule': { hint: '已排期的日程与任务', to: '/calendar' },
  'calendar.tasks': { hint: '待办 / 今日到期 / 未排期', to: '/tasks' },
  'calendar.habits': { hint: '今日习惯投射', to: '/habits' },
  'calendar.availability': { hint: '可安排的空闲窗口', to: '/calendar' },
  'calendar.ai_placeholders': { hint: '待采纳的智能排期', to: '/workbench' },
  'pc.activity': { hint: '键鼠与专注块', to: '/pc-tracker' },
  'pc.classification_suggestions': { hint: '待处理的应用分类', to: '/app-knowledge-base' },
  'pc.quality': { hint: '事实数据可靠性', to: '/pc-tracker' },
  'operations.confirmations': { hint: '高风险操作待办', to: '/confirmations' },
  'operations.health': { hint: 'API / 数据库 / 采集', to: '/status' },
  'endpoints.status': { hint: '设备心跳与上报', to: '/devices' },
  'reminders.queue': { hint: '到期与逾期提醒', to: '/reminders' },
  'reports.available': { hint: '已生成的报告', to: '/reports' },
  'sync.outlook': { hint: '日历同步状态', to: '/settings/microsoft?tab=outlook' },
}

/** 内容多、值得占整行的分区（今日页 lg 双列网格中跨双列） */
const WIDE_SECTIONS = new Set(['calendar.schedule', 'calendar.tasks'])

function sectionTitle(id: string, kind: string): string {
  return SECTION_LABEL[id] ?? SECTION_LABEL[kind] ?? (id.split('.').slice(1).join(' ') || id)
}

/* ── 分区壳（规格：标题/副标题/徽标 + 条目 + 空提示） ──────── */

interface SectionItemView {
  key: string
  title: string
  meta?: string
}

/**
 * 分区载荷 → 条目列表。
 * 各 kind 的 data 形状并不统一（实测）：{items:[]} / 数组 / {events,scheduledTasks} /
 * {unscheduledTasks} / {dueTodayTasks,overdueTasks} / {suggestions} / {reports} /
 * {quality,components} / {detail:{components}} / {endpointCount,items} / {count} 等，
 * 故按类型逐个提取，而不是只认 .items。
 */
function extractItems(id: string, data: unknown): SectionItemView[] {
  if (data == null || typeof data !== 'object') return []
  const o = data as Record<string, unknown>

  const toView = (item: unknown, index: number): SectionItemView => {
    const it = (item ?? {}) as Record<string, unknown>
    const title = String(
      it.title ?? it.name ?? it.label ?? it.message ?? it.deviceId ?? it.host ?? `条目 ${index + 1}`,
    )
    const start = (it.startsAt ?? it.start ?? it.scheduledAt ?? it.dtStart ?? it.lastHeartbeatAt) as string | undefined
    const end = (it.endsAt ?? it.end ?? it.plannedEnd) as string | undefined
    const meta =
      start && end
        ? formatRange(String(start), String(end))
        : start
          ? formatTime(String(start))
          : it.value != null
            ? String(it.value)
            : it.statusLabel != null
              ? String(it.statusLabel)
              : it.detail != null
                ? String(it.detail)
                : undefined
    return { key: String(it.id ?? it.objectId ?? it.deviceId ?? index), title, meta }
  }

  const collect = (arr: unknown): SectionItemView[] =>
    Array.isArray(arr) ? arr.slice(0, 8).map(toView) : []

  /* 计数型分区（reminders.queue / sync.outlook 等）：0 视为无内容，交由空态处理 */
  const countOnly = (n: number, label: string): SectionItemView[] =>
    n > 0 ? [{ key: `${id}-count`, title: label, meta: String(n) }] : []

  /** 内部占位名 → 可读名（app:__idle__ 等哨兵值不应直接暴露给用户） */
  const prettyTarget = (raw: string): string => {
    const v = raw.trim()
    if (!v || /^_+idle_+$/i.test(v)) return '空闲时段'
    return v
  }

  // 1) 直接为数组
  if (Array.isArray(data)) return collect(data)

  // 2) 纯计数分区：{kind, count}
  if (id === 'reminders.queue') return countOnly(Number(o.count ?? 0), '待处理提醒')
  if (id === 'sync.outlook') return countOnly(Number(o.count ?? 0), '同步批次')
  if (id === 'reports.available') return countOnly(Number(o.availableCount ?? 0), '可查看报告')

  // 2) calendar.schedule：日程 + 已排期任务合并，按开始时间排序
  if (id === 'calendar.schedule') {
    const merged = [...collect(o.events), ...collect(o.scheduledTasks)]
    return merged.slice(0, 8)
  }

  // 3) calendar.tasks：今日到期 / 逾期 / 未排期合并（未排期取前几条）
  if (id === 'calendar.tasks') {
    return [
      ...collect(o.overdueTasks),
      ...collect(o.dueTodayTasks),
      ...collect(o.unscheduledTasks).slice(0, 3),
    ].slice(0, 8)
  }

  // 4) pc.classification_suggestions：无 title 字段，用 clusterKey + 时长/次数合成可读条目
  if (id === 'pc.classification_suggestions') {
    const arr = Array.isArray(o.suggestions) ? o.suggestions : []
    return arr.slice(0, 8).map((s, i) => {
      const it = (s ?? {}) as Record<string, unknown>
      const cluster = String(it.clusterKey ?? '')
      const name = cluster.includes(':') ? cluster.split(':').slice(1).join(':') : cluster
      const secs = Number(it.totalDurationSeconds ?? 0)
      const samples = Number(it.sampleCount ?? 0)
      return {
        key: String(it.id ?? i),
        title: `${prettyTarget(name) || '未命名'}（${samples} 次样本）`,
        meta: secs > 0 ? formatDuration(secs) : undefined,
      }
    })
  }

  // 5) pc.activity：仅 summary.keystats，无列表；提取为若干统计条目
  if (id === 'pc.activity') {
    const ks = (o.summary as Record<string, unknown> | undefined)?.keystats as Record<string, unknown> | undefined
    if (!ks) return []
    const num = (v: unknown) => (typeof v === 'number' ? v.toLocaleString() : '0')
    return [
      { key: 'keys', title: '按键总数', meta: num(ks.keyPresses) },
      { key: 'clicks', title: '点击总数', meta: num(ks.totalClicks) },
      { key: 'peak', title: '峰值速度', meta: `${num(ks.peakKps)} 键/秒` },
    ]
  }

  // 6) 通用集合键（含各分区实测字段名）
  for (const key of [
    'items', 'suggestions', 'reports', 'components', 'devices', 'endpoints', 'tasks', 'events', 'records',
  ]) {
    const arr = o[key]
    if (Array.isArray(arr) && arr.length > 0) return collect(arr)
  }

  // 7) 嵌套 detail.components（operations.health 实测形状）
  const detail = o.detail as Record<string, unknown> | undefined
  if (detail && Array.isArray(detail.components)) return collect(detail.components)

  // 8) pc.quality：{quality:{...}, components:[...]}
  const quality = o.quality as Record<string, unknown> | undefined
  if (quality) {
    const items: SectionItemView[] = []
    if (quality.label) items.push({ key: 'q-label', title: String(quality.label), meta: quality.message ? String(quality.message) : undefined })
    if (Array.isArray(o.components)) items.push(...collect(o.components))
    if (items.length) return items.slice(0, 8)
  }

  return []
}

function SectionShell({
  section,
  items,
  loading,
  error,
  onItemClick,
}: {
  section: TodaySectionRef
  items: SectionItemView[]
  loading?: boolean
  error?: string | null
  onItemClick?: (item: SectionItemView) => void
}) {
  const meta = SECTION_HINT[section.id] ?? SECTION_HINT[section.kind]
  return (
    <Card className="flex h-full flex-col p-4">
      <div className="flex items-center gap-2">
        <CardTitle className="truncate">{sectionTitle(section.id, section.kind)}</CardTitle>
        <StatusBadge tone={STATUS_TONE[section.status]} className="ml-auto shrink-0">
          {STATUS_LABEL[section.status]}
        </StatusBadge>
      </div>
      {meta?.hint && <p className="mt-0.5 truncate text-xs text-text-4">{meta.hint}</p>}
      <div className="mt-2 min-h-0 flex-1">
        {loading ? (
          <div className="space-y-2 py-1">
            <Skeleton className="h-4 w-3/4" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        ) : error ? (
          <InlineAlert tone="crit">{error}</InlineAlert>
        ) : items.length === 0 ? (
          <EmptyState size="sm" title="暂无内容" />
        ) : (
          <ul className="divide-y divide-divider">
            {items.map((item) => (
              <li key={item.key}>
                <button
                  type="button"
                  onClick={() => onItemClick?.(item)}
                  disabled={!onItemClick}
                  className="flex w-full items-center gap-3 py-1.5 text-left text-[13px] outline-none disabled:cursor-default"
                >
                  <span className="min-w-0 flex-1 truncate text-text-1">{item.title}</span>
                  {item.meta && <span className="tnum shrink-0 text-xs text-text-3">{item.meta}</span>}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Card>
  )
}


/* ── 单分区宿主（按 kind 分发；pc./operations. 延迟轮询） ──── */

function TodaySectionHost({ section, date }: { section: TodaySectionRef; date: string }) {
  const navigate = useNavigate()
  const poll = section.kind.startsWith('pc.') || section.kind.startsWith('operations.')
  const { data, isLoading } = useTodaySection(section.id, date, poll)

  const items = useMemo(() => extractItems(section.id, data?.data), [section.id, data])
  const error = data?.error?.message ?? null

  /* 条目点击：跳到该分区对应的详情页（具体对象级编辑由各页自行处理）；路由转场 */
  const onItemClick = () => {
    const to = SECTION_HINT[section.id]?.to ?? SECTION_HINT[section.kind]?.to
    if (to) navigate(to, { viewTransition: true })
  }

  return (
    <SectionShell
      section={section}
      items={items}
      loading={isLoading}
      error={error}
      onItemClick={SECTION_HINT[section.id]?.to || SECTION_HINT[section.kind]?.to ? onItemClick : undefined}
    />
  )
}

/* ── 运维与状态手风琴（operations.* 分区归入折叠区） ──────── */

function OpsAccordion({ sections, date }: { sections: TodaySectionRef[]; date: string }) {
  const [open, setOpen] = useState(false)
  if (sections.length === 0) return null
  return (
    <section>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-2 rounded-ctl px-1 py-2 text-left outline-none"
      >
        {open ? <ChevronDown className="size-4 text-text-3" aria-hidden /> : <ChevronRight className="size-4 text-text-3" aria-hidden />}
        <h2 className="text-sm font-semibold text-text-1">运维与状态</h2>
        <span className="tnum text-xs text-text-4">({sections.length})</span>
      </button>
      {open && (
        <div className="grid grid-cols-1 gap-3 pb-2 sm:grid-cols-2 xl:grid-cols-4">
          {sections.map((s) => (
            <TodaySectionHost key={s.id} section={s} date={date} />
          ))}
        </div>
      )}
    </section>
  )
}

/* ── 页面 ─────────────────────────────────────────────────── */

export function TodayPage() {
  const date = useBusinessDate()
  const { data: registry, isLoading, isError, error } = useTodayRegistry(date)
  const navigate = useNavigate()

  const groups = useMemo(() => {
    const sections = registry?.sections ?? []
    /*
     * 分区归组（规格 02 §today）：行动格=calendar.*、数据条=pc.*、
     * 运维与状态手风琴=operations.* 及运维侧 provider（endpoints./reminders./reports./sync.）。
     * 后端当前注册 14 节，其中后 4 个前缀不属于 operations. 但语义同属运维区，
     * 若不归入会错误落到「未知区块」。
     */
    const OPS_PREFIXES = ['operations.', 'endpoints.', 'reminders.', 'reports.', 'sync.']
    const isOps = (kind: string) => OPS_PREFIXES.some((p) => kind.startsWith(p))
    return {
      action: sections.filter((s) => s.kind.startsWith('calendar.')),
      data: sections.filter((s) => s.kind.startsWith('pc.')),
      ops: sections.filter((s) => isOps(s.kind)),
      unknown: sections.filter((s) => !s.kind.startsWith('calendar.') && !s.kind.startsWith('pc.') && !isOps(s.kind)),
    }
  }, [registry])

  const weekday = new Date().toLocaleDateString('zh-CN', { weekday: 'long', month: 'long', day: 'numeric' })

  return (
    <div>
      <PageHeader title="今日" subtitle={weekday} />

      {isError && (
        <InlineAlert tone="crit" title="今日分区加载失败">
          {error instanceof Error ? error.message : '请稍后重试'}
        </InlineAlert>
      )}

      {isLoading ? (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
          {Array.from({ length: 4 }, (_, i) => (
            <Skeleton key={i} className="h-40" />
          ))}
        </div>
      ) : (
        <div className="space-y-6">
          {/* 今日脉搏：业务日 24h 三轨时间条（日程/任务段/习惯） */}
          <TodayPulse date={date} />

          {/* 行动格 */}
          {groups.action.length > 0 && (
            <section>
              <h2 className="mb-2 flex items-center gap-2 border-l-2 border-primary/50 pl-2 text-xs font-medium text-text-3">
                行动
              </h2>
              {/* 列表型卡（日程/任务）跨双列；dense 让半宽卡自动填缝不留孤行 */}
              <Stagger className="grid grid-flow-dense grid-cols-1 gap-3 lg:grid-cols-2">
                {groups.action.map((s, i) => (
                  <StaggerItem
                    key={s.id}
                    index={i}
                    className={cn('h-full', WIDE_SECTIONS.has(s.id) && 'lg:col-span-2')}
                  >
                    <TodaySectionHost section={s} date={date} />
                  </StaggerItem>
                ))}
              </Stagger>
            </section>
          )}

          {/* 数据条 */}
          {groups.data.length > 0 && (
            <section>
              <h2 className="mb-2 flex items-center gap-2 border-l-2 border-primary/50 pl-2 text-xs font-medium text-text-3">
                数据
              </h2>
              <Stagger className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
                {groups.data.map((s, i) => (
                  <StaggerItem key={s.id} index={i} className="h-full">
                    <TodaySectionHost section={s} date={date} />
                  </StaggerItem>
                ))}
              </Stagger>
            </section>
          )}

          {/* 运维与状态（可折叠） */}
          <OpsAccordion sections={groups.ops} date={date} />

          {/* 未知区块（规格：显示"未知区块"占位） */}
          {groups.unknown.length > 0 && (
            <section>
              <h2 className="mb-2 flex items-center gap-2 border-l-2 border-primary/50 pl-2 text-xs font-medium text-text-3">
                其它
              </h2>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
                {groups.unknown.map((s) => (
                  <Card key={s.id} className="p-4">
                    <CardTitle className="flex items-center gap-1.5">
                      <Hammer className="size-3.5 text-text-4" aria-hidden />
                      未知区块
                    </CardTitle>
                    <p className="mono mt-2 truncate text-xs text-text-4">{s.kind}</p>
                  </Card>
                ))}
              </div>
            </section>
          )}

          {/* 全空 */}
          {registry && registry.sections.length === 0 && (
            <Card>
              <EmptyState
                title="今天还没有分区数据"
                description="创建日程或任务后，这里会汇总你的一天。"
                action={
                  <button
                    type="button"
                    onClick={() => navigate('/calendar')}
                    className={cn('rounded-ctl bg-primary px-3.5 py-2 text-[13px] font-medium text-primary-fg hover:bg-primary-hover')}
                  >
                    去安排日程
                  </button>
                }
              />
            </Card>
          )}
        </div>
      )}
    </div>
  )
}
