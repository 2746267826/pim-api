import { useMemo } from 'react'
import { STORAGE_KEYS, setJSON } from '@/lib/storage'
import { notifySuccess } from '@/lib/notify'
import { useCalendars, useEvents, useLayers } from '../queries'
import { dayEndIso, dayStartIso } from '@/lib/datetime'
import { MiniDayCalendar } from './mini-day-calendar'
import type { CalendarVariant, MiniEvent } from './mini-day-calendar'
import { Button, Card, CardTitle, PageHeader, StatusBadge } from '@/components/ui'

/*
 * 日历样式提案 · 十种大厂成熟风格。
 * 同一份数据、自绘高保真预览（重叠均分列宽，绝不遮盖）；选定后正式日历仍只改样式层。
 */

const LAYER_COLORS: Record<string, string> = {
  'task-segments': '#22C55E',
  habits: '#A855F7',
  availability: '#0EA5E9',
  'ai-placeholders': '#F97316',
}

const PROPOSALS: {
  variant: CalendarVariant
  title: string
  ref: string
  desc: string
  liked?: boolean
  current?: boolean
}[] = [
  {
    variant: 'fade',
    title: '晕 · 左浓右淡',
    ref: 'Google Calendar「未确认邀请」语言',
    desc: '你点赞的那款。左缘色浓度向右渐隐，中间留白。',
    liked: true,
  },
  {
    variant: 'gcal-bar',
    title: '谷歌竖条',
    ref: 'Google Calendar 日视图',
    desc: 'Google Calendar 日/周视图的标准事件：左侧宽竖条 + 白底 + 轻投影。辨识度极高，全世界最熟悉的日历语言。',
    current: true,
  },
  {
    variant: 'gcal-dot',
    title: '谷歌圆点行',
    ref: 'Google Calendar 月视图',
    desc: '月视图语言放进日视图：无块感，彩色圆点 + 标题一行。页面最"素"，多事件时最清爽。',
    current: true,
  },
  {
    variant: 'notion',
    title: 'Notion 色条',
    ref: 'Notion Calendar',
    desc: '3px 色条 + 8% 同色底、小圆角。Notion Calendar 的标志性克制。',
  },
  {
    variant: 'outlook',
    title: 'Outlook 浅块',
    ref: 'Microsoft 365 新版',
    desc: '18% 同色浅块 + 4px 左条、直角。微软新办公套件的正式感，色块比 Google 饱满、比玻璃轻。',
  },
  {
    variant: 'apple',
    title: '苹果浅渐变',
    ref: 'Apple Calendar / Fantastical 月视图',
    desc: '自上而下的浅色渐变（16% → 30%），无左条，圆角 6。macOS 原生日历的柔和质感。',
  },
  {
    variant: 'linear',
    title: 'Linear 圆点行',
    ref: 'Linear Schedule',
    desc: '完全无块：彩色圆点 + 标题 + 右侧淡时间一行。工程团队审美，密度最高。',
  },
  {
    variant: 'ticktick',
    title: '胶囊细边',
    ref: '滴答清单',
    desc: '白底胶囊 + 发丝边框，色彩只在时间上。圆润友好，国内工具的常见语言。',
  },
  {
    variant: 'feishu',
    title: '飞书浅块',
    ref: '飞书 / Lark 日历',
    desc: '12% 同色浅块 + 3px 左条、圆角 6。介于谷歌竖条与 Notion 之间，平衡感好。',
  },
  {
    variant: 'stripe',
    title: '顶条白块',
    ref: 'Stripe Dashboard / Cron 列表',
    desc: '白底 + hairline 边框 + 顶部 2px 色条，时间用日历本色加粗。仪表盘气质，和浅色效率主题最搭。',
  },
  {
    variant: 'glass',
    title: '玻璃 · 饱和渐变',
    ref: '当前正式方案（对照）',
    desc: '饱和渐变大色块，保留在此作对照。',
    current: true,
  },
]

export function CalendarStylesPage() {
  const range = { start: dayStartIso(new Date()), end: dayEndIso(new Date()) }
  const { data: eventsData } = useEvents(range.start, range.end, true)
  const { data: layersData } = useLayers(range.start, range.end, 'task-segments,habits,availability,ai-placeholders', false, true)
  const { data: calendars = [] } = useCalendars('calendar')

  const miniEvents = useMemo<MiniEvent[]>(() => {
    const list: MiniEvent[] = []
    for (const ev of eventsData?.items ?? []) {
      list.push({
        id: `e:${ev.id}`,
        title: ev.title,
        start: ev.dtStart,
        end: ev.dtEnd,
        color: calendars.find((c) => c.id === ev.calendarId)?.color ?? '#3B82F6',
      })
    }
    for (const item of layersData?.items ?? []) {
      list.push({
        id: `l:${item.id}`,
        title: item.title,
        start: item.startsAt,
        end: item.endsAt,
        color: LAYER_COLORS[item.layer] ?? item.color,
      })
    }
    return list
  }, [eventsData, layersData, calendars])

  function choose(variant: CalendarVariant, title: string) {
    setJSON(STORAGE_KEYS.calendarSkin, variant)
    notifySuccess(`已记录选择：${title}`, '正式日历将按该语言定制（只改样式层，交互不变）')
  }

  return (
    <div>
      <PageHeader
        title="日历样式提案 · 十种大厂风格"
        subtitle="已选定：日视图「谷歌竖条」+ 月视图「谷歌圆点行」（正式日历已应用，仅样式层，库能力与交互不变）"
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {PROPOSALS.map((p) => (
          <Card key={p.variant} className="overflow-hidden p-4">
            <div className="flex flex-wrap items-center gap-2">
              <CardTitle className="text-[15px]">{p.title}</CardTitle>
              <StatusBadge tone="info" dot={false}>{p.ref}</StatusBadge>
              {p.liked && <StatusBadge tone="warn" dot={false}>你点赞</StatusBadge>}
              {p.current && <StatusBadge tone="ok">当前</StatusBadge>}
            </div>
            <p className="mt-1.5 text-[13px] leading-5 text-text-2">{p.desc}</p>
            <div className="mt-3">
              <MiniDayCalendar variant={p.variant} events={miniEvents} />
            </div>
            {!p.current && (
              <Button variant="primary" size="sm" className="mt-3" onClick={() => choose(p.variant, p.title)}>
                选用此方案
              </Button>
            )}
          </Card>
        ))}
      </div>

      <Card className="mt-4 p-4">
        <CardTitle className="text-[13px]">说明</CardTitle>
        <p className="mt-1.5 text-[13px] text-text-3">
          无论选哪种，日历的拖选建日程、收件箱拖入排期、月视图、重叠事件自动分列等能力都来自
          FullCalendar（成熟库），我们只改事件与网格的视觉层。也可以混搭（如"谷歌竖条 + 晕的渐变"）
          或调整参数（条宽、浓度、圆角、字号），随时说。
        </p>
      </Card>
    </div>
  )
}
