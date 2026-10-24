import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import FullCalendar from '@fullcalendar/react';
import dayGridPlugin from '@fullcalendar/daygrid';
import interactionPlugin, { Draggable } from '@fullcalendar/interaction';
import timeGridPlugin from '@fullcalendar/timegrid';
import luxon3Plugin from '@fullcalendar/luxon3';
import type { DateSelectArg, DatesSetArg, EventClickArg, EventContentArg, EventMountArg } from '@fullcalendar/core';
import { format } from 'date-fns';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { AlertTriangle, Bell, Repeat2 } from 'lucide-react';
import { getCalendarLayers, getCalendars, getEvents, getTasks, planTask } from '../api/calendar';
import { useCalendarVisibility } from '../context/CalendarVisibilityContext';
import EventEditorDialog from '../dialogs/EventEditorDialog';
import TaskEditorDialog from '../dialogs/TaskEditorDialog';
import CalendarLayerToolbar from '../components/schedule/CalendarLayerToolbar';
import PageHeader from '../ui/PageHeader';
import SegmentedControl from '../ui/SegmentedControl';
import type { CalendarLayerId, EventResponse, TaskResponse } from '../types';
import { looksLikeHtml, sanitizeDescriptionHtml } from '../utils/safeHtml';
import { buildCalendarEvents, type CalendarEventInput, type CalendarEventProps } from '../utils/calendarEvents';
import { getPlannedEndForDrop, toLocalDateTimeInputValue } from '../utils/dropDuration';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

type CalendarMode = 'timeline' | 'month';
type CalendarLayerToggleId = CalendarLayerId;

type CalendarDropArg = {
  draggedEl: HTMLElement;
  date: Date;
};

const CALENDAR_MODE_OPTIONS: Array<{ value: CalendarMode; label: string }> = [
  { value: 'timeline', label: '时间轴' },
  { value: 'month', label: '月视图' },
];

const CALENDAR_LAYER_OPTIONS: Array<{ value: CalendarLayerToggleId; label: string }> = [
  { value: 'events', label: '日程' },
  { value: 'task-segments', label: '任务时间段' },
  { value: 'habits', label: '习惯' },
  { value: 'availability', label: '可用时间' },
  { value: 'ai-placeholders', label: '智能占位' },
];

export default function CalendarPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const mode = normalizeMode(searchParams.get('view'));
  const [activeDate, setActiveDate] = useState(() => new Date());
  const [visibleRange, setVisibleRange] = useState(() => rangeForDate(activeDate, mode));
  const [taskEditorOpen, setTaskEditorOpen] = useState(false);
  const [editingTask, setEditingTask] = useState<TaskResponse | undefined>();
  const [taskDefaultDtStart, setTaskDefaultDtStart] = useState<string | undefined>();
  const [visibleTitle, setVisibleTitle] = useState(() => formatCalendarTitle(new Date(), mode));
  const [eventEditorOpen, setEventEditorOpen] = useState(false);
  const [editingEvent, setEditingEvent] = useState<EventResponse | undefined>();
  const [eventDefaultStart, setEventDefaultStart] = useState<string | undefined>();
  const [eventDefaultEnd, setEventDefaultEnd] = useState<string | undefined>();
  const [planTaskError, setPlanTaskError] = useState<string | null>(null);
  const [enabledLayerIds, setEnabledLayerIds] = useState<CalendarLayerToggleId[]>(['events', 'task-segments']);
  const [outlookOnly, setOutlookOnly] = useState(false);
  const calendarRef = useRef<FullCalendar>(null);
  const pageRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();

  useEffect(() => {
    if (searchParams.get('view') === mode) return;

    const nextParams = new URLSearchParams(searchParams);
    nextParams.set('view', mode);
    setSearchParams(nextParams, { replace: true });
  }, [mode, searchParams, setSearchParams]);

  useEffect(() => {
    const root = pageRef.current?.closest('.pim-shell') as HTMLElement | null;
    if (!root) return;

    const draggable = new Draggable(root, {
      itemSelector: '.js-draggable-task',
      eventData: draggedEl => ({
        create: false,
        title: draggedEl.dataset.taskTitle || '未命名任务',
      }),
    });

    return () => draggable.destroy();
  }, []);

  const { data: events = [] } = useQuery({
    queryKey: ['events', visibleRange.start, visibleRange.end],
    queryFn: () => getEvents(visibleRange.start, visibleRange.end),
  });

  const { data: tasks = [] } = useQuery({
    queryKey: ['tasks'],
    queryFn: () => getTasks(),
  });

  const { data: calendars = [] } = useQuery({
    queryKey: ['calendars'],
    queryFn: () => getCalendars(),
  });

  const enabledLayerKey = enabledLayerIds.join(',');
  const enabledLayerSet = useMemo(() => new Set(enabledLayerIds), [enabledLayerIds]);

  const { data: calendarLayerData } = useQuery({
    queryKey: ['calendar-layers', visibleRange.start, visibleRange.end, enabledLayerKey, outlookOnly],
    queryFn: () => getCalendarLayers({
      start: visibleRange.start,
      end: visibleRange.end,
      layers: enabledLayerIds,
      outlookOnly,
    }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const { hiddenCalendarIds } = useCalendarVisibility();
  const planTaskMutation = useMutation({
    mutationFn: ({ task, plannedStart }: { task: TaskResponse; plannedStart: string }) =>
      planTask(task.id, {
        plannedStart,
        plannedEnd: getPlannedEndForDrop(task, plannedStart),
      }),
    onMutate: () => setPlanTaskError(null),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tasks'] });
      queryClient.invalidateQueries({ queryKey: ['tasks-paged'] });
      queryClient.invalidateQueries({ queryKey: ['events'] });
      queryClient.invalidateQueries({ queryKey: ['today-sections'] });
      queryClient.invalidateQueries({ queryKey: ['today-section'] });
    },
    onError: error => {
      setPlanTaskError(error instanceof Error ? error.message : '任务计划失败，请稍后再试。');
    },
  });
  const { mutate: mutatePlanTask } = planTaskMutation;

  // defense-in-depth: API 层已主归一化为 EventResponse[]，此处仅为历史兼容兜底
  // 若上游回退或契约再次漂移，仍能避免 .filter 崩溃；稳定后可移除
  const safeEvents = useMemo(() => {
    if (Array.isArray(events)) return events;
    const maybePaged = events as unknown as { items?: unknown };
    if (maybePaged && Array.isArray(maybePaged.items)) return maybePaged.items as EventResponse[];
    return [] as EventResponse[];
  }, [events]);

  const calendarEvents = useMemo(() => {
    const visibleEvents = hiddenCalendarIds.size > 0
      ? safeEvents.filter(event => !hiddenCalendarIds.has(event.calendarId))
      : safeEvents;
    const layerItems = calendarLayerData?.items ?? [];

    return buildCalendarEvents(
      enabledLayerSet.has('events') ? visibleEvents : [],
      tasks,
      layerItems,
      enabledLayerSet,
      calendars,
    );
  }, [calendarLayerData?.items, calendars, enabledLayerSet, safeEvents, tasks, hiddenCalendarIds]);

  function toggleCalendarLayer(layerId: CalendarLayerToggleId) {
    setEnabledLayerIds(current => (
      current.includes(layerId)
        ? current.filter(item => item !== layerId)
        : [...current, layerId]
    ));
  }

  function handleModeChange(nextMode: CalendarMode) {
    const currentDate = calendarRef.current?.getApi().getDate() ?? new Date();
    const nextParams = new URLSearchParams(searchParams);
    nextParams.set('view', nextMode);
    setActiveDate(currentDate);
    setVisibleRange(rangeForDate(currentDate, nextMode));
    setVisibleTitle(formatCalendarTitle(currentDate, nextMode));
    setSearchParams(nextParams);
  }

  function handleCalendarPrev() {
    calendarRef.current?.getApi().prev();
  }

  function handleCalendarToday() {
    calendarRef.current?.getApi().today();
  }

  function handleCalendarNext() {
    calendarRef.current?.getApi().next();
  }

  function handleDatesSet(arg: DatesSetArg) {
    const nextActiveDate = arg.view.currentStart || arg.start;
    const nextRange = {
      start: toDateStr(arg.start),
      end: toDateStr(arg.end),
    };

    setActiveDate(nextActiveDate);
    setVisibleTitle(arg.view.title);
    setVisibleRange(currentRange => (
      currentRange.start === nextRange.start && currentRange.end === nextRange.end
        ? currentRange
        : nextRange
    ));
  }

  const handleDateSelect = useCallback((selectInfo: DateSelectArg) => {
    setEditingEvent(undefined);
    setEventDefaultStart(toLocalDateTimeInputValue(selectInfo.start));
    setEventDefaultEnd(toLocalDateTimeInputValue(selectInfo.end));
    setEventEditorOpen(true);
  }, []);

  const handleEventClick = useCallback((clickInfo: EventClickArg) => {
    const props = clickInfo.event.extendedProps as CalendarEventInput['extendedProps'];

    if (props.type === 'layer') {
      return;
    }

    if (props.type === 'task') {
      setEditingTask(props.raw as TaskResponse);
      setTaskDefaultDtStart(undefined);
      setTaskEditorOpen(true);
      return;
    }

    setEditingEvent(props.raw as EventResponse);
    setEventDefaultStart(undefined);
    setEventDefaultEnd(undefined);
    setEventEditorOpen(true);
  }, []);

  const cardObserverMap = useRef(new WeakMap<HTMLElement, () => void>());

  const handleEventMount = useCallback((info: EventMountArg) => {
    cardObserverMap.current.get(info.el)?.();
    cardObserverMap.current.delete(info.el);

    const props = info.event.extendedProps as CalendarEventProps;
    if (props.type === 'layer') return;
    let mountObserver: MutationObserver | undefined;
    let resizeObserver: ResizeObserver | undefined;

    const attachResizeObserver = () => {
      if (resizeObserver) return true;
      const cardEl = info.el.querySelector<HTMLElement>('[data-calendar-event-card]');
      if (!cardEl) return false;

      const computeLevel = () => {
        const height = info.el.clientHeight;
        let level = 1;
        if (height >= 80) level = 5;
        else if (height >= 64) level = 4;
        else if (height >= 48) level = 3;
        else if (height >= 32) level = 2;
        cardEl.dataset.contentLevel = String(level);
      };

      computeLevel();
      resizeObserver = new ResizeObserver(computeLevel);
      resizeObserver.observe(info.el);
      mountObserver?.disconnect();
      return true;
    };

    if (!attachResizeObserver()) {
      mountObserver = new MutationObserver(attachResizeObserver);
      mountObserver.observe(info.el, { childList: true, subtree: true });
    }

    cardObserverMap.current.set(info.el, () => {
      mountObserver?.disconnect();
      resizeObserver?.disconnect();
    });
  }, []);

  const handleEventUnmount = useCallback((info: EventMountArg) => {
    const cleanup = cardObserverMap.current.get(info.el);
    if (cleanup) {
      cleanup();
      cardObserverMap.current.delete(info.el);
    }
  }, []);

  const handleExternalDrop = useCallback((dropInfo: CalendarDropArg) => {
    const taskId = dropInfo.draggedEl.dataset.taskId;
    const task = tasks.find(item => item.id === taskId);
    if (!task) return;

    const plannedStart = toLocalDateTimeInputValue(dropInfo.date);
    mutatePlanTask({ task, plannedStart });
  }, [mutatePlanTask, tasks]);

  return (
    <div ref={pageRef} className="flex h-full min-h-0 flex-col gap-4">
      <PageHeader
        title="日历"
        subtitle={mode === 'timeline' ? '按时间轴安排今天的任务和日程' : '按月查看任务和日程分布'}
        beforeActions={
          <div className="flex items-center gap-2">
            <button
              type="button"
              className="pim-button-secondary px-3 py-1.5 text-sm"
              onClick={handleCalendarPrev}
              aria-label="上一段时间范围"
            >
              上一段
            </button>
            <button
              type="button"
              className="pim-button-secondary px-3 py-1.5 text-sm"
              onClick={handleCalendarToday}
            >
              今天
            </button>
            <button
              type="button"
              className="pim-button-secondary px-3 py-1.5 text-sm"
              onClick={handleCalendarNext}
              aria-label="下一段时间范围"
            >
              下一段
            </button>
            <span className="min-w-24 text-sm font-semibold text-slate-700">{visibleTitle}</span>
          </div>
        }
        actions={
          <SegmentedControl
            value={mode}
            options={CALENDAR_MODE_OPTIONS}
            onChange={handleModeChange}
            ariaLabel="切换日历视图"
          />
        }
      />

      <CalendarLayerToolbar
        options={CALENDAR_LAYER_OPTIONS}
        activeLayerIds={enabledLayerIds}
        outlookOnly={outlookOnly}
        onToggleLayer={toggleCalendarLayer}
        onToggleOutlookOnly={setOutlookOnly}
      />

        <section className="calendar-board pim-panel min-h-0 flex-1 overflow-hidden p-3">
        {planTaskError && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {planTaskError}
          </div>
        )}
        <FullCalendar
          key={mode}
          ref={calendarRef}
          plugins={mode === 'timeline'
            ? [timeGridPlugin, interactionPlugin, luxon3Plugin]
            : [dayGridPlugin, interactionPlugin, luxon3Plugin]}
          initialView={mode === 'timeline' ? 'timeGridDay' : 'dayGridMonth'}
          initialDate={toDateStr(activeDate)}
          events={calendarEvents}
          locale="zh-cn"
          timeZone="local"
          height="100%"
          headerToolbar={false}
          eventContent={renderCalendarEvent}
          dayMaxEvents={mode === 'month' ? true : undefined}
          slotLabelFormat={{ hour: '2-digit', minute: '2-digit', hour12: false }}
          eventTimeFormat={{ hour: '2-digit', minute: '2-digit', hour12: false }}
          allDaySlot={mode === 'timeline' ? false : undefined}
          slotMinTime={mode === 'timeline' ? '00:00:00' : undefined}
          slotMaxTime={mode === 'timeline' ? '24:00:00' : undefined}
          selectable
          selectMirror
          editable={false}
          droppable
          datesSet={handleDatesSet}
          select={handleDateSelect}
          eventClick={handleEventClick}
          drop={handleExternalDrop}
          eventDidMount={handleEventMount}
          eventWillUnmount={handleEventUnmount}
        />
        </section>
      {/* grid grid-cols-1 gap-4 lg:grid-cols-[1.5fr_1fr] — reserved */}
      <div className="pb-20 md:pb-4" aria-hidden="true" />

      <TaskEditorDialog
        open={taskEditorOpen}
        onClose={() => setTaskEditorOpen(false)}
        task={editingTask}
        defaultDtStart={taskDefaultDtStart}
      />
      <EventEditorDialog
        open={eventEditorOpen}
        onClose={() => setEventEditorOpen(false)}
        event={editingEvent}
        defaultStart={eventDefaultStart}
        defaultEnd={eventDefaultEnd}
      />
    </div>
  );
}

function normalizeMode(value: string | null): CalendarMode {
  return value === 'month' ? 'month' : 'timeline';
}

function rangeForDate(date: Date, mode: CalendarMode) {
  if (mode === 'month') {
    return {
      start: toDateStr(new Date(date.getFullYear(), date.getMonth(), 1)),
      end: toDateStr(new Date(date.getFullYear(), date.getMonth() + 1, 1)),
    };
  }

  return {
    start: toDateStr(date),
    end: toDateStr(new Date(date.getTime() + 86400000)),
  };
}

function formatCalendarTitle(date: Date, mode: CalendarMode): string {
  return mode === 'month' ? format(date, 'yyyy年M月') : format(date, 'yyyy年M月d日');
}

function truncateText(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;
  return text.slice(0, maxLength - 1) + '…';
}

function htmlToPlainText(html: string): string {
  if (!looksLikeHtml(html)) return html;
  const sanitized = sanitizeDescriptionHtml(html);
  const parsed = new DOMParser().parseFromString(sanitized, 'text/html');
  return (parsed.body.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function renderCalendarEvent(arg: EventContentArg) {
  const props = arg.event.extendedProps as CalendarEventInput['extendedProps'];
  if (props.type === 'layer') {
    const raw = props.raw;
    const isTaskSegment = raw.layer === 'task-segments';
    const toneClass = isTaskSegment
      ? 'calendar-event--quiet'
      : raw.requiresConfirmation
        ? 'calendar-event--warning'
        : 'calendar-event--primary';

    return (
      <div className={`calendar-event-card ${toneClass} ${isTaskSegment ? 'pim-calendar-layer-task-segment' : 'pim-calendar-layer'}`}>
        <span className="calendar-event-dot" />
        <span className="calendar-event-title">{arg.event.title}</span>
        {arg.timeText && <span className="calendar-event-time">{arg.timeText}</span>}
      </div>
    );
  }

  const { accentColor, calendarLabel } = props;
  const style = { '--calendar-accent': accentColor } as CSSProperties;

  if (props.type === 'event') {
    const raw = props.raw;
    const description = raw.description
      ? truncateText(htmlToPlainText(raw.description), 80)
      : undefined;
    const isImportant = raw.importance === 'high';
    const isCancelled = (raw as { isCancelled?: boolean | null }).isCancelled ?? raw.status === 'CANCELLED';
    const isRecurring = !!raw.rrule || !!raw.isSeriesMaster || !!raw.seriesMasterId;
    const showAsLabel = raw.showAs === 'free' || raw.showAs === 'tentative'
      ? (raw.showAs === 'free' ? '空闲' : '暂定')
      : undefined;
    const cardClass = `calendar-event-card${isImportant ? ' calendar-event--important' : ''}${isCancelled ? ' calendar-event--cancelled' : ''}`;

    return (
      <div className={cardClass} data-calendar-event-card style={{ ...style, opacity: isCancelled ? 0.5 : undefined } as CSSProperties}>
        <span className="calendar-event-dot" />
        <span className="calendar-event-title">{arg.event.title}</span>
        {arg.timeText && <span className="calendar-event-time">{arg.timeText}</span>}
        {isCancelled && <span className="calendar-event-cancelled" aria-label="已取消">已取消</span>}
        {showAsLabel && (
          <span className={`calendar-event-showas calendar-event-showas--${raw.showAs}`}>{showAsLabel}</span>
        )}
        {raw.location && <span className="calendar-event-location">{raw.location}</span>}
        {calendarLabel && <span className="calendar-event-source">{calendarLabel}</span>}
        {description && <span className="calendar-event-description">{description}</span>}
        {isRecurring && (
          <span className="calendar-event-rrule">
            <Repeat2 size={10} aria-label="重复" />
          </span>
        )}
        {isImportant && (
          <span className="calendar-event-importance" aria-label="重要">
            <AlertTriangle size={10} aria-hidden="true" />
            重要
          </span>
        )}
        {raw.isReminderOn && (
          <span className="calendar-event-reminder" aria-label="提醒">
            <Bell size={10} aria-hidden="true" />
          </span>
        )}
      </div>
    );
  }

  const raw = props.raw;
  const description = raw.description
    ? truncateText(htmlToPlainText(raw.description), 80)
    : undefined;

  return (
    <div className="calendar-event-card" data-calendar-event-card style={style}>
      <span className="calendar-event-dot" />
      <span className="calendar-event-title">{arg.event.title}</span>
      {arg.timeText && <span className="calendar-event-time">{arg.timeText}</span>}
      {calendarLabel && <span className="calendar-event-source">{calendarLabel}</span>}
      {description && <span className="calendar-event-description">{description}</span>}
    </div>
  );
}

function toDateStr(date: Date): string {
  return format(date, 'yyyy-MM-dd');
}
