import { useEffect, useMemo, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Plus, Trash2 } from 'lucide-react'
import { ApiError, apiPost } from '@/api/client'
import type { components } from '@/api/generated'
import { isOutlookEvent, type CalendarBook, type EventResponse } from '../types'
import { isRepeatedEvent, readEventRrule } from '../rrule'
import { useDeleteEvent, useSaveEvent } from '../queries'
import { fromLocalInputValue, toLocalInputValue } from '@/lib/datetime'
import { InlineAlert, Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, Input, Label, Segmented, Select, Switch, Textarea } from '@/components/ui'

/** 与后端 CreateEventRequest 对齐的全部可写字段（05/calendar.md §日程事件） */
const schema = z.object({
  title: z.string().min(1, '请输入标题').max(255, '最多 255 字符'),
  calendarId: z.string().min(1, '请选择日历本'),
  isAllDay: z.boolean(),
  dtStart: z.string().min(1, '请选择开始时间'),
  dtEnd: z.string().min(1, '请选择结束时间'),
  location: z.string().max(500, '最多 500 字符'),
  description: z.string(),
  rrule: z.enum(['', 'FREQ=DAILY', 'FREQ=WEEKLY', 'FREQ=MONTHLY']),
  // 提醒
  isReminderOn: z.boolean(),
  reminderMinutesBeforeStart: z.string(),
  // Outlook 统一字段
  showAs: z.enum(['', 'busy', 'free', 'tentative', 'oof', 'workingElsewhere']),
  importance: z.enum(['', 'low', 'normal', 'high']),
  sensitivity: z.enum(['', 'normal', 'personal', 'private', 'confidential']),
  categories: z.string(),
  /**
   * 工单 04：日程期间是否静音（三态）。
   * 'follow' = 未指定 → 生效值取所属日历本的默认静音；'mute' 静音；'unmute' 不静音。
   * 用非空值表示「未指定」：分段控件的空串等于「没选中」，会把三态压成两态。
   */
  isMuted: z.enum(['follow', 'mute', 'unmute']),
  // 联机会议
  isOnlineMeeting: z.boolean(),
  onlineMeetingProvider: z.string(),
  onlineMeetingUrl: z.string(),
  // 外部链接
  externalLink: z.string(),
  // 组织者
  organizerName: z.string(),
  organizerEmail: z.string(),
})

export interface EventEditorProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  calendars: CalendarBook[]
  /** 新建：日历拖选预填的时段 */
  initial?: { start: Date; end: Date; allDay?: boolean } | null
  /** 编辑：事件对象 */
  event?: EventResponse | null
}

const RECURRENCE_OPTIONS = [
  { value: '', label: '不重复' },
  { value: 'FREQ=DAILY', label: '每天' },
  { value: 'FREQ=WEEKLY', label: '每周' },
  { value: 'FREQ=MONTHLY', label: '每月' },
] as const

const REMINDER_OPTIONS = [
  { value: '', label: '不提醒' },
  { value: '0', label: '准时' },
  { value: '5', label: '提前 5 分钟' },
  { value: '15', label: '提前 15 分钟' },
  { value: '30', label: '提前 30 分钟' },
  { value: '60', label: '提前 1 小时' },
  { value: '1440', label: '提前 1 天' },
]

/** 工单 04：三态静音选项（'' = 未指定，跟随日历本默认） */
const MUTE_OPTIONS = [
  { value: 'follow', label: '跟随默认', title: '未指定：生效值取所属日历本的默认静音设定' },
  { value: 'mute', label: '静音', title: '该日程期间静音' },
  { value: 'unmute', label: '不静音', title: '该日程期间不改变静音状态' },
]

const SHOW_AS_OPTIONS = [
  { value: '', label: '默认（忙碌）' },
  { value: 'busy', label: '忙碌' },
  { value: 'free', label: '空闲' },
  { value: 'tentative', label: '暂定' },
  { value: 'oof', label: '外出' },
  { value: 'workingElsewhere', label: '异地办公' },
]

const IMPORTANCE_OPTIONS = [
  { value: '', label: '普通' },
  { value: 'low', label: '低' },
  { value: 'normal', label: '中' },
  { value: 'high', label: '高' },
]

const SENSITIVITY_OPTIONS = [
  { value: '', label: '普通' },
  { value: 'normal', label: '公开' },
  { value: 'personal', label: '个人' },
  { value: 'private', label: '私密' },
  { value: 'confidential', label: '机密' },
]

interface AttendeeRow {
  name: string
  email: string
  type: 'required' | 'optional'
}

/** 日程编辑弹窗：基本信息平铺，提醒/闲忙/会议/参会人收进"更多设置" */
export function EventEditorDialog({ open, onOpenChange, calendars, initial, event }: EventEditorProps) {
  const isEdit = event != null
  const isOutlook = event != null && isOutlookEvent(event)
  const hasRecurrence = event != null && (isRepeatedEvent(event) || Boolean(event.seriesMasterId))
  const save = useSaveEvent()
  const remove = useDeleteEvent()

  const [scope, setScope] = useState<'this' | 'series'>('this')
  const [error, setError] = useState<string | null>(null)
  const [conflict, setConflict] = useState<{ serverTitle?: string; message: string } | null>(null)
  const [attendees, setAttendees] = useState<AttendeeRow[]>([])
  const [showMore, setShowMore] = useState(false)

  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: {
      title: '', calendarId: '', isAllDay: false, dtStart: '', dtEnd: '',
      location: '', description: '', rrule: '',
      isReminderOn: false, reminderMinutesBeforeStart: '',
      showAs: '', importance: '', sensitivity: '', categories: '',
      isMuted: 'follow',
      isOnlineMeeting: false, onlineMeetingProvider: '', onlineMeetingUrl: '',
      externalLink: '', organizerName: '', organizerEmail: '',
    },
  })

  /* 不可编辑日历（含只读 Outlook 绑定）在新建与编辑时都不可选 */
  const editableCalendars = useMemo(() => calendars.filter((c) => c.canEdit), [calendars])

  useEffect(() => {
    if (!open) return
    setError(null)
    setConflict(null)
    setScope('this')
    setShowMore(false)
    if (event) {
      form.reset({
        title: event.title,
        calendarId: event.calendarId,
        isAllDay: event.isAllDay,
        dtStart: toLocalInputValue(event.dtStart),
        dtEnd: toLocalInputValue(event.dtEnd),
        location: event.location ?? '',
        description: event.description ?? '',
        rrule: (readEventRrule(event) as z.infer<typeof schema>['rrule']) ?? '',
        isReminderOn: event.isReminderOn ?? false,
        reminderMinutesBeforeStart: event.reminderMinutesBeforeStart != null ? String(event.reminderMinutesBeforeStart) : '',
        showAs: (event.showAs as z.infer<typeof schema>['showAs']) ?? '',
        importance: (event.importance as z.infer<typeof schema>['importance']) ?? '',
        sensitivity: (event.sensitivity as z.infer<typeof schema>['sensitivity']) ?? '',
        categories: (event.categories ?? []).join(', '),
        isMuted: event.isMuted === true ? 'mute' : event.isMuted === false ? 'unmute' : 'follow',
        isOnlineMeeting: event.isOnlineMeeting ?? false,
        onlineMeetingProvider: event.onlineMeetingProvider ?? '',
        onlineMeetingUrl: event.onlineMeetingUrl ?? '',
        externalLink: event.externalLink ?? '',
        organizerName: event.organizer?.name ?? '',
        organizerEmail: event.organizer?.email ?? '',
      })
      setAttendees((event.attendees ?? []).map((a) => ({ name: a.name ?? '', email: a.email ?? '', type: a.type === 'optional' ? 'optional' : 'required' })))
      const hasMore = Boolean(event.isMuted != null || event.isReminderOn || event.showAs || event.importance || event.sensitivity || (event.categories?.length ?? 0) > 0 || event.isOnlineMeeting || event.externalLink || (event.attendees?.length ?? 0) > 0)
      setShowMore(hasMore)
    } else if (initial) {
      const def = calendars.find((c) => c.isDefault && c.kind === 'calendar') ?? calendars.find((c) => c.kind === 'calendar')
      form.reset({
        title: '', calendarId: def?.id ?? '',
        isAllDay: initial.allDay ?? false,
        dtStart: toLocalInputValue(initial.start.toISOString()),
        dtEnd: toLocalInputValue(initial.end.toISOString()),
        location: '', description: '', rrule: '',
        isReminderOn: false, reminderMinutesBeforeStart: '',
        showAs: '', importance: '', sensitivity: '', categories: '',
        isMuted: 'follow',
        isOnlineMeeting: false, onlineMeetingProvider: '', onlineMeetingUrl: '',
        externalLink: '', organizerName: '', organizerEmail: '',
      })
      setAttendees([])
    }
  }, [open, event, initial, calendars, form])

  async function submit(values: z.infer<typeof schema>) {
    setError(null)
    setConflict(null)
    const dtStart = fromLocalInputValue(values.dtStart)
    const dtEnd = fromLocalInputValue(values.dtEnd)
    if (!dtStart || !dtEnd) {
      setError('时间格式不正确')
      return
    }
    if (dtEnd <= dtStart) {
      setError('结束时间必须晚于开始时间')
      return
    }
    /* 参会人门禁：契约 EventAttendeeDto.email 必填且非 nullable。
     * 「有效行」沿用既有语义（姓名或邮箱任一非空；全空占位行仍然忽略）。
     * 任一有效行缺邮箱就整体阻止提交——既不放行违规报文体，也不静默过滤该行；
     * 提示用户补邮箱后，所有有效行按原顺序 / 原 name / 原 type 原样发出。 */
    const attendeeRows = attendees.filter((a) => a.name || a.email)
    const missingEmail = attendeeRows.find((a) => !a.email)
    if (missingEmail) {
      setError(`参会人「${missingEmail.name || '未填姓名'}」缺少邮箱，请补充邮箱后再保存。`)
      return
    }
    const categories = values.categories.split(/[,，]/).map((s) => s.trim()).filter(Boolean)
    /* 未填组织者时整个 organizer 键省略（后端 NormalizeOrganizer(null) → null，
     * 缺键与显式 null 的服务端可观察状态相同；属工程等价，非产品决定）。 */
    const organizer = values.organizerName || values.organizerEmail
      ? { name: values.organizerName || undefined, email: values.organizerEmail || undefined }
      : undefined
    const body: components['schemas']['CreateEventRequest'] = {
      calendarId: values.calendarId,
      title: values.title,
      description: values.description || null,
      location: values.location || null,
      dtStart,
      dtEnd,
      isAllDay: values.isAllDay,
      rRule: values.rrule || null,
      // 后端只接受 'html'（'text' 报 2009 DescriptionFormat 值无效）；纯文本描述可省略该字段
      descriptionFormat: values.description ? 'html' : null,
      isReminderOn: values.isReminderOn,
      reminderMinutesBeforeStart: values.reminderMinutesBeforeStart ? Number(values.reminderMinutesBeforeStart) : null,
      showAs: values.showAs || null,
      importance: values.importance || null,
      sensitivity: values.sensitivity || null,
      categories: categories.length > 0 ? categories : null,
      /* 三态：follow 发 null（未指定 → 跟随日历本默认）；后端把 null 与 true/false 一视同仁地整体替换 */
      isMuted: values.isMuted === 'follow' ? null : values.isMuted === 'mute',
      isOnlineMeeting: values.isOnlineMeeting,
      onlineMeetingProvider: values.onlineMeetingProvider || null,
      onlineMeetingUrl: values.onlineMeetingUrl || null,
      externalLink: values.externalLink || null,
      ...(organizer ? { organizer } : {}),
      attendees: attendeeRows.map((a) => ({ name: a.name || undefined, email: a.email, type: a.type })),
    }
    // 目标日历是否为 Outlook 绑定（新建时也需判定：直接 POST 会被后端拒 02009）
    const targetCalendar = calendars.find((c) => c.id === values.calendarId)
    const targetIsOutlook = targetCalendar?.outlookCalendarBindingId != null || targetCalendar?.source === 'outlook'
    try {
      if (!isEdit && targetIsOutlook) {
        const result = await apiPost<{ status: string; event: EventResponse | null; errorCode: string | null; errorMessage: string | null }>(
          '/api/v1/calendar/outlook/events/writeback',
          {
            operation: 'create',
            calendarBindingId: targetCalendar?.outlookCalendarBindingId,
            draft: { ...body, uid: crypto.randomUUID() },
            scope: 'instance',
            clientOperationId: crypto.randomUUID(),
          },
          { allowStatuses: [409, 412] },
        )
        if (result.status === 'conflict') {
          setConflict({ message: result.errorMessage ?? '创建冲突：服务器已有同源事件。' })
          return
        }
        if (result.status === 'reauth-required') {
          setError('Outlook 连接需要重新授权（设置 → Microsoft 账户）')
          return
        }
        if (result.status === 'error') {
          setError(result.errorMessage ?? 'Outlook 写回失败')
          return
        }
        onOpenChange(false)
        return
      }
      if (isEdit && event) {
        if (isOutlook) {
          const result = await apiPost<{ status: string; latestEvent: EventResponse | null; errorCode: string | null; errorMessage: string | null }>(
            '/api/v1/calendar/outlook/events/writeback',
            {
              operation: 'update',
              calendarBindingId: event.outlookCalendarBindingId,
              eventId: event.id,
              draft: { ...body, uid: event.uid },
              scope: hasRecurrence ? (scope === 'this' ? 'instance' : 'series') : 'instance',
              clientOperationId: crypto.randomUUID(),
              expectedEtag: event.outlookEtag ?? undefined,
              originalEventId: event.originalEventId ?? undefined,
              recurrenceId: event.recurrenceId ?? undefined,
            },
            { allowStatuses: [409, 412] },
          )
          if (result.status === 'conflict') {
            setConflict({
              serverTitle: result.latestEvent?.title,
              message: result.errorMessage ?? '服务器上已有更新版本（HTTP 409/412 冲突），请刷新后重试。',
            })
            return
          }
          if (result.status === 'reauth-required') {
            setError('Outlook 连接需要重新授权（设置 → Microsoft 账户）')
            return
          }
          if (result.status === 'error') {
            setError(result.errorMessage ?? 'Outlook 写回失败')
            return
          }
        } else {
          await save.mutateAsync({
            id: event.id,
            body,
            scope: hasRecurrence ? { scope, recurrenceId: event.recurrenceId ?? undefined, originalEventId: event.originalEventId ?? undefined } : undefined,
          })
        }
      } else {
        await save.mutateAsync({ body })
      }
      onOpenChange(false)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败，请稍后重试')
    }
  }

  async function onDelete() {
    if (!event) return
    setError(null)
    try {
      if (isOutlook) {
        const result = await apiPost<{ status: string; errorMessage: string | null }>(
          '/api/v1/calendar/outlook/events/writeback',
          {
            operation: 'delete',
            calendarBindingId: event.outlookCalendarBindingId,
            eventId: event.id,
            scope: hasRecurrence ? (scope === 'this' ? 'instance' : 'series') : 'instance',
            clientOperationId: crypto.randomUUID(),
            originalEventId: event.originalEventId ?? undefined,
            recurrenceId: event.recurrenceId ?? undefined,
          },
          { allowStatuses: [409, 412] },
        )
        if (result.status === 'conflict') {
          setConflict({ message: result.errorMessage ?? '删除冲突：服务器上已有更新版本。' })
          return
        }
        if (result.status === 'reauth-required') {
          setError('Outlook 连接需要重新授权（设置 → Microsoft 账户）')
          return
        }
      } else {
        await remove.mutateAsync({
          id: event.id,
          scope: hasRecurrence ? { scope, recurrenceId: event.recurrenceId ?? undefined, originalEventId: event.originalEventId ?? undefined } : undefined,
        })
      }
      onOpenChange(false)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '删除失败，请稍后重试')
    }
  }

  const busy = save.isPending || remove.isPending
  const values = form.watch()
  /* 工单 04：「未指定」的生效值取自所选日历本的默认静音，界面必须说清当前会按什么执行 */
  const selectedCalendar = calendars.find((c) => c.id === values.calendarId)
  const selectedCalendarName = selectedCalendar?.name ?? '当前日历本'
  const selectedCalendarDefaultMuted = selectedCalendar?.defaultMuted ?? false

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-[560px]">
        <DialogHeader
          title={isEdit ? '编辑日程' : '新建日程'}
          description={event?.isCancelled ? '该日程已取消' : undefined}
        />
        <DialogBody className="space-y-4">
          {isOutlook && (
            <InlineAlert tone="info">
              Outlook 镜像事件：保存/删除将通过写回流程同步到 Microsoft 日历。
            </InlineAlert>
          )}
          {conflict && (
            <InlineAlert tone="crit" title="写回冲突">
              {conflict.message}
              {conflict.serverTitle && <> 服务器当前标题：「{conflict.serverTitle}」。</>}
            </InlineAlert>
          )}
          {error && <InlineAlert tone="crit">{error}</InlineAlert>}

          {hasRecurrence && isEdit && (
            <div>
              <Label>重复范围</Label>
              <Segmented
                value={scope}
                onValueChange={setScope}
                options={[
                  { value: 'this', label: '仅本次' },
                  { value: 'series', label: '整个系列' },
                ]}
              />
            </div>
          )}

          <div>
            <Label htmlFor="event-title">标题 *</Label>
            <Input id="event-title" {...form.register('title')} />
            {form.formState.errors.title && (
              <p className="mt-1 text-xs text-crit">{form.formState.errors.title.message}</p>
            )}
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <Label htmlFor="event-calendar">日历本 *</Label>
              <Select
                id="event-calendar"
                value={values.calendarId || undefined}
                onValueChange={(v) => form.setValue('calendarId', v)}
                options={editableCalendars.map((c) => ({ value: c.id, label: c.name, disabled: !c.canEdit }))}
                placeholder="选择日历本"
              />
            </div>
            <div>
              <Label htmlFor="event-rrule">重复</Label>
              <Select
                id="event-rrule"
                value={values.rrule || ''}
                onValueChange={(v) => form.setValue('rrule', v as z.infer<typeof schema>['rrule'])}
                options={RECURRENCE_OPTIONS.map((o) => ({ value: o.value, label: o.label }))}
              />
            </div>
          </div>

          <div className="grid grid-cols-2 items-end gap-3">
            <div>
              <Label htmlFor="event-start">开始 *</Label>
              <Input id="event-start" type="datetime-local" {...form.register('dtStart')} />
            </div>
            <div>
              <Label htmlFor="event-end">结束 *</Label>
              <Input id="event-end" type="datetime-local" {...form.register('dtEnd')} />
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Switch id="event-allday" checked={values.isAllDay} onCheckedChange={(v) => form.setValue('isAllDay', v)} />
            <Label htmlFor="event-allday" className="mb-0">全天</Label>
          </div>

          <div>
            <Label htmlFor="event-location">地点</Label>
            <Input id="event-location" {...form.register('location')} />
          </div>
          <div>
            <Label htmlFor="event-desc">描述</Label>
            <Textarea id="event-desc" rows={3} {...form.register('description')} />
          </div>

          {/* ── 更多设置 ── */}
          <button
            type="button"
            onClick={() => setShowMore((s) => !s)}
            className="flex w-full items-center gap-2 text-[13px] font-medium text-text-3 transition-colors hover:text-text-1 outline-none"
          >
            <span className="h-px flex-1 bg-divider" />
            {showMore ? '收起更多设置' : '提醒、闲忙、会议、参会人…'}
            <span className="h-px flex-1 bg-divider" />
          </button>

          {showMore && (
            <div className="space-y-4 rounded-ctl border border-border bg-surface p-3.5">
              <div className="grid grid-cols-2 items-end gap-3">
                <div>
                  <Label htmlFor="event-reminder">提醒</Label>
                  <Select
                    id="event-reminder"
                    value={values.isReminderOn ? values.reminderMinutesBeforeStart || '0' : ''}
                    onValueChange={(v) => {
                      form.setValue('isReminderOn', v !== '')
                      form.setValue('reminderMinutesBeforeStart', v)
                    }}
                    options={REMINDER_OPTIONS}
                  />
                </div>
                <div>
                  <Label htmlFor="event-showas">忙闲（showAs）</Label>
                  <Select
                    id="event-showas"
                    value={values.showAs || ''}
                    onValueChange={(v) => form.setValue('showAs', v as z.infer<typeof schema>['showAs'])}
                    options={SHOW_AS_OPTIONS}
                  />
                </div>
              </div>

              <div>
                <Label>期间静音</Label>
                <Segmented
                  value={values.isMuted}
                  onValueChange={(v) => form.setValue('isMuted', v as z.infer<typeof schema>['isMuted'])}
                  options={MUTE_OPTIONS}
                />
                <p className="mt-1 text-[11px] text-text-4" data-testid="event-mute-hint">
                  {values.isMuted === 'follow'
                    ? `未指定：按日历本「${selectedCalendarName}」的默认（${selectedCalendarDefaultMuted ? '静音' : '不静音'}）执行`
                    : values.isMuted === 'mute'
                      ? '该日程期间将手机静音（有勿扰权限的手机生效）'
                      : '该日程期间不改变手机静音状态'}
                </p>
              </div>

              <div className="grid grid-cols-3 gap-3">
                <div>
                  <Label htmlFor="event-importance">重要级</Label>
                  <Select
                    id="event-importance"
                    value={values.importance || ''}
                    onValueChange={(v) => form.setValue('importance', v as z.infer<typeof schema>['importance'])}
                    options={IMPORTANCE_OPTIONS}
                  />
                </div>
                <div>
                  <Label htmlFor="event-sensitivity">隐私</Label>
                  <Select
                    id="event-sensitivity"
                    value={values.sensitivity || ''}
                    onValueChange={(v) => form.setValue('sensitivity', v as z.infer<typeof schema>['sensitivity'])}
                    options={SENSITIVITY_OPTIONS}
                  />
                </div>
                <div>
                  <Label htmlFor="event-categories">分类</Label>
                  <Input id="event-categories" placeholder="逗号分隔" {...form.register('categories')} />
                </div>
              </div>

              <div className="space-y-2">
                <div className="flex items-center gap-2">
                  <Switch id="event-meeting" checked={values.isOnlineMeeting} onCheckedChange={(v) => form.setValue('isOnlineMeeting', v)} />
                  <Label htmlFor="event-meeting" className="mb-0">联机会议</Label>
                </div>
                {values.isOnlineMeeting && (
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <Label htmlFor="event-meeting-provider">提供方</Label>
                      <Input id="event-meeting-provider" placeholder="teams" {...form.register('onlineMeetingProvider')} />
                    </div>
                    <div>
                      <Label htmlFor="event-meeting-url">会议链接</Label>
                      <Input id="event-meeting-url" placeholder="https://…" {...form.register('onlineMeetingUrl')} />
                    </div>
                  </div>
                )}
              </div>

              <div>
                <Label htmlFor="event-external">外部链接</Label>
                <Input id="event-external" placeholder="https://…" {...form.register('externalLink')} />
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <Label htmlFor="event-organizer-name">组织者姓名</Label>
                  <Input id="event-organizer-name" {...form.register('organizerName')} />
                </div>
                <div>
                  <Label htmlFor="event-organizer-email">组织者邮箱</Label>
                  <Input id="event-organizer-email" type="email" {...form.register('organizerEmail')} />
                </div>
              </div>

              {/* 参会人 */}
              <div>
                <div className="mb-1.5 flex items-center justify-between">
                  <Label className="mb-0">参会人（{attendees.length}）</Label>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => setAttendees((a) => [...a, { name: '', email: '', type: 'required' }])}
                  >
                    <Plus className="size-4" aria-hidden /> 添加
                  </Button>
                </div>
                {attendees.length === 0 && <p className="text-xs text-text-4">未添加参会人</p>}
                <div className="space-y-1.5">
                  {attendees.map((a, i) => (
                    <div key={i} className="flex items-center gap-1.5">
                      <Input
                        className="h-8 w-24"
                        placeholder="姓名"
                        value={a.name}
                        onChange={(e) => setAttendees((arr) => arr.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))}
                      />
                      <Input
                        className="h-8 flex-1"
                        placeholder="邮箱"
                        value={a.email}
                        onChange={(e) => setAttendees((arr) => arr.map((x, j) => (j === i ? { ...x, email: e.target.value } : x)))}
                      />
                      <Select
                        className="h-8 w-24"
                        value={a.type}
                        onValueChange={(v) => setAttendees((arr) => arr.map((x, j) => (j === i ? { ...x, type: v as AttendeeRow['type'] } : x)))}
                        options={[
                          { value: 'required', label: '必选' },
                          { value: 'optional', label: '可选' },
                        ]}
                        ariaLabel="参会人类型"
                      />
                      <button
                        type="button"
                        aria-label="移除参会人"
                        onClick={() => setAttendees((arr) => arr.filter((_, j) => j !== i))}
                        className="rounded-ctl p-1 text-text-4 transition-colors hover:text-crit outline-none"
                      >
                        <Trash2 className="size-3.5" aria-hidden />
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </DialogBody>
        <DialogFooter>
          {isEdit && (
            <Button
              variant="danger-soft"
              className="mr-auto"
              loading={remove.isPending}
              onClick={() => void onDelete()}
            >
              <Trash2 className="size-4" aria-hidden /> 删除
            </Button>
          )}
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={busy}>
            取消
          </Button>
          <Button variant="primary" loading={save.isPending} onClick={() => void form.handleSubmit(submit)()}>
            保存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
