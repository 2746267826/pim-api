import { X } from 'lucide-react';
import { useMemo, useRef, useState, useId, useEffect, type FormEvent, type KeyboardEvent } from 'react';
import { useMutation, useQueryClient, useQuery } from '@tanstack/react-query';
import { createEvent, updateEvent, deleteEvent, getCalendars, getOutlookSettings, writeOutlookEvent } from '../api/calendar';
import ConfirmActionDialog, { type DeleteConfirmationInput } from '../ui/ConfirmActionDialog';
import BeforeAfterDiff from '../components/schedule/BeforeAfterDiff';
import { Field } from './common';
import { useCalendarVisibility } from '../context/CalendarVisibilityContext';
import { resolveCalendarId, hasWritableCalendar, noWritableCalendarMessage } from '../utils/calendarSelection';
import { isoToDatetimeLocal, isEndAfterStart, minimumEndValue } from '../utils/dateTimeInput';
import { looksLikeHtml, sanitizeDescriptionHtml } from '../utils/safeHtml';
import { buildUnifiedEventDraft, type EventFormValue } from '../utils/eventDraft';
import { formatFieldValue, summarizeEventFields, toDiffRecord, type EventFieldDiffInput } from '../utils/eventFieldDiff';
import EventSection from '../components/calendar/EventSection';
import RichDescriptionEditor from '../components/calendar/RichDescriptionEditor';
import EventAdvancedFields from '../components/calendar/EventAdvancedFields';
import EventCollaborationFields from '../components/calendar/EventCollaborationFields';
import EventMeetingFields from '../components/calendar/EventMeetingFields';
import EventAttachmentFields from '../components/calendar/EventAttachmentFields';
import EventRecurrenceSummary from '../components/calendar/EventRecurrenceSummary';
import RecurrenceRuleEditor from '../components/calendar/RecurrenceRuleEditor';
import OutlookAdditionalInfo from '../components/calendar/OutlookAdditionalInfo';
import type { EventResponse, OutlookWriteRequest, OutlookEventDraft, UnifiedEventDraft } from '../types';

interface Props {
  open: boolean;
  onClose: () => void;
  event?: EventResponse;
  defaultStart?: string;
  defaultEnd?: string;
}

export default function EventEditorDialog(props: Props) {
  const formKey = [
    props.open ? 'open' : 'closed',
    props.event?.id || 'new',
    props.defaultStart || 'none',
    props.defaultEnd || 'none',
  ].join(':');

  return <EventEditorForm key={formKey} {...props} />;
}

type WritebackPhase =
  | { type: 'idle' }
  | { type: 'preview' }
  | { type: 'submitting' }
  | { type: 'conflict'; latestEvent: EventResponse | null; latestEtag?: string | null; errorMessage?: string | null }
  | { type: 'error'; message: string };

function escapePlainTextToParagraphHtml(text: string): string {
  const escaped = text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/\r\n/g, '\n')
    .replace(/\n/g, '<br>');
  return escaped === '' ? '' : `<p>${escaped}</p>`;
}

const formInputClass = 'w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-500';

function EventEditorForm({ open, onClose, event, defaultStart, defaultEnd }: Props) {
  const [form, setForm] = useState<EventFormValue>(() => ({
    calendarId: event?.calendarId || '',
    title: event?.title || '',
    description: event?.description || null,
    descriptionFormat: event?.descriptionFormat || null,
    location: event?.location || '',
    dtStart: event ? isoToDatetimeLocal(event.dtStart, event.timeZoneId) : (defaultStart || ''),
    dtEnd: event ? isoToDatetimeLocal(event.dtEnd, event.timeZoneId) : (defaultEnd || ''),
    rrule: event?.rrule || null,
    isAllDay: Boolean(event?.isAllDay),
    timeZoneId: event?.timeZoneId || null,
    showAs: event?.showAs || null,
    importance: event?.importance || null,
    sensitivity: event?.sensitivity || null,
    categories: event?.categories ?? [],
    isReminderOn: event?.isReminderOn ?? false,
    reminderMinutesBeforeStart: event?.reminderMinutesBeforeStart ?? null,
    organizer: event?.organizer ?? null,
    attendees: event?.attendees ?? [],
    isOnlineMeeting: event?.isOnlineMeeting ?? false,
    onlineMeetingProvider: event?.onlineMeetingProvider || null,
    onlineMeetingUrl: event?.onlineMeetingUrl || null,
    externalLink: event?.externalLink || null,
    attachmentReferences: event?.attachmentReferences ?? [],
    isSeriesMaster: event?.isSeriesMaster ?? false,
    isException: event?.isException ?? false,
    seriesMasterId: event?.seriesMasterId || null,
    recurrenceId: event?.recurrenceId || null,
  }));
  const [deleteInput, setDeleteInput] = useState<DeleteConfirmationInput | null>(null);
  const queryClient = useQueryClient();
  const editorTitleId = useId();
  const { hiddenCalendarIds } = useCalendarVisibility();
  const dialogRef = useRef<HTMLElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialogRef.current?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
    };
  }, [open]);

  function patchForm(patch: Partial<EventFormValue>) {
    setForm(prev => ({ ...prev, ...patch }));
  }

  const descriptionDisplayHtml = useMemo(() => {
    const raw = form.description || '';
    if (form.descriptionFormat === 'html' || looksLikeHtml(raw)) {
      return sanitizeDescriptionHtml(raw);
    }
    return escapePlainTextToParagraphHtml(raw);
  }, [form.description, form.descriptionFormat]);

  function handleDescriptionHtmlChange(html: string) {
    patchForm({ description: html, descriptionFormat: 'html' });
  }

  const { data: calendars, isLoading, isError: calendarsError } = useQuery({
    queryKey: ['calendars', 'calendar'],
    queryFn: () => getCalendars('calendar'),
    enabled: open
  });

  const [writebackPhase, setWritebackPhase] = useState<WritebackPhase>({ type: 'idle' });
  const [pendingRequest, setPendingRequest] = useState<OutlookWriteRequest | null>(null);
  const [outlookScope, setOutlookScope] = useState<'instance' | 'series'>(() =>
    event?.outlookEventType === 'seriesMaster' ? 'series' : 'instance',
  );
  const [diffBefore, setDiffBefore] = useState<EventFieldDiffInput>({});
  const [diffAfter, setDiffAfter] = useState<EventFieldDiffInput>({});
  const [writebackValidationError, setWritebackValidationError] = useState('');

  const selectedCalendarId = resolveCalendarId(
    calendars || [],
    form.calendarId || (event ? event.calendarId : undefined),
    hiddenCalendarIds,
  );

  const isOutlookExisting = event?.source === 'outlook' && !!event?.outlookCalendarBindingId;
  const selectedCalendar = calendars?.find(c => c.id === selectedCalendarId);
  const isNewOutlook = !event && !!selectedCalendar?.outlookCalendarBindingId;
  const isOutlook = isOutlookExisting || isNewOutlook;

  const eventCalendar = event ? calendars?.find(c => c.id === event.calendarId) : undefined;
  const isReadOnly = event ? (eventCalendar?.canEdit === false) : (selectedCalendar?.canEdit === false);
  const isFormDisabled = isReadOnly;

  const showScopeRadio = !!event && (event.outlookEventType === 'occurrence' || event.outlookEventType === 'exception' || event.outlookEventType === 'seriesMaster');
  const isNativeSeries = !!event && (!!event.isSeriesMaster || !!event.seriesMasterId) && !isOutlookExisting;
  const [nativeScope, setNativeScope] = useState<'this' | 'series'>(() =>
    event?.isSeriesMaster && !event?.isException ? 'series' : 'this'
  );

  function resolveEffectiveId(scope: string): string {
    if (!event) return '';
    // For series scope, always target master
    if (scope === 'series' && event.seriesMasterId) return event.seriesMasterId;
    if (scope === 'series' && event.isSeriesMaster) return event.id;
    // For this scope on occurrence/exception, target master with recurrenceId
    if (scope === 'this' && event.seriesMasterId) return event.seriesMasterId;
    // Synthetic occurrence: id is derived, originalEventId is master
    if (event.originalEventId && event.id !== event.originalEventId && !event.isException) return event.originalEventId;
    return event.id;
  }
  function resolveRecurrenceId(): string | undefined {
    if (!event) return undefined;
    return event.recurrenceId || undefined;
  }
  function resolveOriginalEventIdForScope(_scope: string): string | undefined {
    if (!event) return undefined;
    const isOccurrence = !!event.seriesMasterId || !!event.isException || (!!event.originalEventId && event.id !== event.originalEventId);
    if (!isOccurrence) return undefined;
    // For both this/series, send master id as originalEventId when editing an occurrence/exception/synthetic
    if (event.originalEventId && event.id !== event.originalEventId) return event.originalEventId;
    if (event.seriesMasterId) return event.seriesMasterId;
    if (event.isException && event.seriesMasterId) return event.seriesMasterId;
    return undefined;
  }

  const { data: outlookSettings } = useQuery({
    queryKey: ['outlook-settings', 'writeback'],
    queryFn: () => getOutlookSettings(),
    enabled: open && isOutlook,
  });

  function getCalendarBindingId(): string {
    return selectedCalendar?.outlookCalendarBindingId || event?.outlookCalendarBindingId || '';
  }

  function buildDraft(): OutlookEventDraft {
    return {
      ...buildUnifiedEventDraft({
        ...form,
        calendarId: selectedCalendarId || form.calendarId,
      }),
      uid: event?.uid || undefined,
    };
  }

  function buildWritebackRequest(operation: 'create' | 'update' | 'delete'): OutlookWriteRequest {
    const effScope = operation === 'create' ? 'instance' : outlookScope;
    const req: OutlookWriteRequest = {
      operation,
      calendarBindingId: getCalendarBindingId(),
      scope: effScope,
      clientOperationId: crypto.randomUUID(),
    };
    if (event) {
      // For Outlook, keep original occurrence id (persisted) and let backend handle scope
      // Native synthetic handling is separate (see resolveEffectiveId)
      req.eventId = event.id;
      if (event.originalEventId && event.id !== event.originalEventId) req.originalEventId = event.originalEventId;
      else if (event.seriesMasterId) req.originalEventId = event.seriesMasterId;
      if (effScope === 'instance' && event.recurrenceId) req.recurrenceId = event.recurrenceId;
    }
    if (operation !== 'delete') {
      req.draft = buildDraft();
    }
    if (operation !== 'create') {
      req.expectedEtag = event?.outlookEtag || undefined;
    }
    return req;
  }

  function openWritebackPreview(operation: 'create' | 'update' | 'delete') {
    const req = buildWritebackRequest(operation);
    const beforeRecord = operation === 'create'
      ? {}
      : event
        ? toDiffRecord(event as unknown as Record<string, unknown>)
        : {};
    const afterRecord = operation === 'delete'
      ? {}
      : req.draft
        ? toDiffRecord(req.draft as unknown as Record<string, unknown>)
        : {};
    setDiffBefore(beforeRecord);
    setDiffAfter(afterRecord);
    setPendingRequest(req);
    setWritebackPhase({ type: 'preview' });
  }

  function invalidateEventQueries() {
    queryClient.invalidateQueries({ queryKey: ['events'] });
    queryClient.invalidateQueries({ queryKey: ['events-paged'] });
    queryClient.invalidateQueries({ queryKey: ['calendars'] });
    queryClient.invalidateQueries({ queryKey: ['calendar-layers'] });
    queryClient.invalidateQueries({ queryKey: ['workbench-calendar-layers'] });
  }

  function invalidateWritebackQueries(operation: string) {
    invalidateEventQueries();
    queryClient.invalidateQueries({ queryKey: ['outlook-sync-batches'] });
    if (operation === 'delete') {
      queryClient.invalidateQueries({ queryKey: ['calendar-recycle-bin'] });
    }
  }

  async function confirmWriteback() {
    if (!pendingRequest) return;
    setWritebackPhase({ type: 'submitting' });
    try {
      const requestScope = pendingRequest.operation === 'create' ? 'instance' : outlookScope;
      const result = await writeOutlookEvent({ ...pendingRequest, scope: requestScope });
      const status = result.status;
      if (status === 'created' || status === 'updated' || status === 'deleted') {
        invalidateWritebackQueries(pendingRequest.operation);
        setWritebackPhase({ type: 'idle' });
        onClose();
        return;
      }
      if (status === 'conflict') {
        setDiffBefore(toDiffRecord((result.latestEvent ?? {}) as Record<string, unknown>));
        setWritebackPhase({
          type: 'conflict',
          latestEvent: result.latestEvent ?? null,
          latestEtag: result.latestEtag,
          errorMessage: result.errorMessage,
        });
        return;
      }
      setWritebackPhase({ type: 'error', message: result.errorMessage || `操作失败：${status}` });
    } catch (e) {
      setWritebackPhase({ type: 'error', message: e instanceof Error ? e.message : '未知错误' });
    }
  }

  function retryWithLatest() {
    if (writebackPhase.type !== 'conflict' || !writebackPhase.latestEtag) return;
    if (!pendingRequest) return;
    setPendingRequest({ ...pendingRequest, expectedEtag: writebackPhase.latestEtag });
    setDiffBefore(toDiffRecord((writebackPhase.latestEvent ?? {}) as Record<string, unknown>));
    setWritebackPhase({ type: 'preview' });
  }

  function cancelWriteback() {
    setWritebackPhase({ type: 'idle' });
  }

  const createMut = useMutation({
    mutationFn: (data: Partial<UnifiedEventDraft>) => createEvent(data),
    onSuccess: () => {
      invalidateEventQueries();
      onClose();
    }
  });

  function invalidateEventDeleteQueries() {
    invalidateEventQueries();
    queryClient.invalidateQueries({ queryKey: ['calendar-recycle-bin'] });
  }

  const updateMut = useMutation({
    mutationFn: (data: Partial<UnifiedEventDraft>) => {
      const scope = isNativeSeries ? nativeScope : undefined;
      const recId = scope === 'this' ? resolveRecurrenceId() : undefined;
      const effectiveId = scope ? resolveEffectiveId(scope) : event!.id;
      const originalEventId = scope ? resolveOriginalEventIdForScope(scope) : undefined;
      // Ensure recurrenceId passed via draft as well for backend merge
      const payload = recId && !data.recurrenceId ? { ...data, recurrenceId: recId } : data;
      return updateEvent(effectiveId, payload, scope ? { scope, recurrenceId: recId, originalEventId } : undefined);
    },
    onSuccess: () => {
      invalidateEventQueries();
      onClose();
    }
  });

  const deleteMut = useMutation({
    mutationFn: () => {
      if (isNativeSeries) {
        const effectiveId = resolveEffectiveId(nativeScope);
        const recId = nativeScope === 'this' ? resolveRecurrenceId() : undefined;
        const originalEventId = resolveOriginalEventIdForScope(nativeScope);
        return deleteEvent(effectiveId, { scope: nativeScope, recurrenceId: recId, originalEventId });
      }
      return deleteEvent(event!.id);
    },
    onSuccess: () => {
      invalidateEventDeleteQueries();
      setDeleteInput(null);
      onClose();
    },
    onError: () => setDeleteInput(null),
  });

  const mutationError = createMut.error || updateMut.error || deleteMut.error;
  const mutationErrorMessage = mutationError instanceof Error ? mutationError.message : null;

  const isWritebackActive = writebackPhase.type !== 'idle';
  const isSubmitting = writebackPhase.type === 'submitting';
  const isProcessing = createMut.isPending || updateMut.isPending || deleteMut.isPending || isSubmitting;

  function handleDelete() {
    if (!event) return;
    if (isOutlookExisting) {
      if (writebackPhase.type !== 'idle') return;
      if (!event.outlookEtag) {
        setWritebackValidationError('缺少版本标识，无法执行写回操作。');
        return;
      }
      openWritebackPreview('delete');
    } else {
      deleteMut.reset();
      setDeleteInput({
        targetType: 'event',
        title: event.title,
        affectedCount: 1,
        samples: [{
          id: event.id,
          type: 'event',
          title: event.title,
          start: event.dtStart,
          end: event.dtEnd,
        }],
      });
    }
  }

  function confirmDelete() {
    if (!event) return;
    deleteMut.mutate();
  }

  function cancelDelete() {
    if (deleteMut.isPending) return;
    setDeleteInput(null);
  }

  function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (isReadOnly) return;
    if (isProcessing) return;

    if (calendarsError) {
      setWritebackValidationError('日历加载失败，请稍后重试。');
      return;
    }
    if (!form.title.trim()) {
      setWritebackValidationError('请输入标题');
      return;
    }
    if (!form.dtStart || !form.dtEnd) {
      setWritebackValidationError('请选择开始和结束时间');
      return;
    }
    if (!event && !hasWritableCalendar(calendars || [], hiddenCalendarIds)) {
      setWritebackValidationError(noWritableCalendarMessage());
      return;
    }
    if (!isEndAfterStart(form.dtStart, form.dtEnd)) {
      setWritebackValidationError('结束时间必须晚于开始时间');
      return;
    }

    setWritebackValidationError('');

    if (isOutlook) {
      if (writebackPhase.type !== 'idle') return;
      if (event && !event.outlookEtag) {
        setWritebackValidationError('缺少版本标识，无法执行写回操作。');
        return;
      }
      openWritebackPreview(event ? 'update' : 'create');
    } else {
      const data = buildUnifiedEventDraft({
        ...form,
        calendarId: selectedCalendarId || form.calendarId,
      });
      // For native series, ensure recurrenceId included when editing single occurrence
      if (isNativeSeries && nativeScope === 'this' && !data.recurrenceId) {
        const recId = resolveRecurrenceId();
        if (recId) (data as unknown as Record<string, unknown>).recurrenceId = recId;
      }
      if (event) updateMut.mutate(data);
      else createMut.mutate(data);
    }
  }

  const titleText = isReadOnly ? `${event ? '编辑日程' : '新建日程'}（只读）` : (event ? '编辑日程' : '新建日程');

  if (!open) return null;

  return (
    <>
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/40 backdrop-blur-xs animate-backdrop" onClick={onClose}>
      <aside role="dialog" aria-modal="true" aria-labelledby={editorTitleId} tabIndex={-1} ref={dialogRef} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }} className="w-full max-w-lg max-h-[85vh] flex flex-col rounded-xl border border-zinc-200 bg-white shadow-dialog animate-dialog" onClick={e => e.stopPropagation()}>
        <header className="flex items-center justify-between border-b border-zinc-200 px-5 py-4 shrink-0">
          <h2 id={editorTitleId} className="text-base font-semibold text-zinc-900">{titleText}</h2>
          <button onClick={onClose} className="text-zinc-400 hover:text-zinc-600 p-1 rounded-lg hover:bg-zinc-100">
            <X className="w-4 h-4" />
          </button>
        </header>
        <div className="overflow-y-auto max-h-[75vh] px-5 py-4 space-y-4">
          <form id="event-editor-form" onSubmit={handleSubmit} className="space-y-4" noValidate>
        {writebackValidationError && (
          <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {writebackValidationError}
          </div>
        )}
        {mutationErrorMessage && (
          <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">
            {mutationErrorMessage}
          </div>
        )}
        {isReadOnly && (
          <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-700">
            此日历为只读，无法编辑或删除。
          </div>
        )}
        {event?.source === 'outlook-ics' && (
          <div className="rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm leading-6 text-blue-700">
            这是从 Outlook ICS 导入的事件，会议上下文已保留，PIM 暂不处理会议接受/拒绝/参会状态。
          </div>
        )}
        <EventSection title="基本信息" defaultOpen>
          <Field label="日历本">
            <select value={selectedCalendarId} onChange={e => patchForm({ calendarId: e.target.value })}
              disabled={isLoading || (!!event && (isFormDisabled || isOutlookExisting))}
              className={formInputClass}>
              {isLoading ? (
                <option value="" disabled>正在加载日历...</option>
              ) : calendarsError ? (
                <option value="" disabled>日历加载失败</option>
              ) : calendars?.map(cal => (
                <option key={cal.id} value={cal.id}>{cal.name}{cal.outlookCalendarBindingId ? ' (Outlook)' : ''}</option>
              ))}
            </select>
            {calendarsError && (
              <p className="mt-1 text-xs text-red-600">日历加载失败，请稍后重试。</p>
            )}
            {!isLoading && !calendarsError && !isReadOnly && !hasWritableCalendar(calendars || [], hiddenCalendarIds) && (
              <p className="mt-1 text-xs text-red-600">{noWritableCalendarMessage()}</p>
            )}
          </Field>
          <Field label="标题">
            <input type="text" value={form.title} onChange={e => patchForm({ title: e.target.value })}
              disabled={isFormDisabled}
              className={formInputClass} required />
          </Field>
          <label className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
            <input
              type="checkbox"
              checked={Boolean(form.isAllDay)}
              onChange={e => patchForm({ isAllDay: e.target.checked })}
              disabled={isFormDisabled}
              className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-200"
            />
            全天事件
          </label>
          <Field label="开始时间">
            <input type="datetime-local" value={form.dtStart} onChange={e => patchForm({ dtStart: e.target.value })}
              disabled={isFormDisabled}
              className={formInputClass} required />
          </Field>
          <Field label="结束时间">
            <input type="datetime-local" value={form.dtEnd} onChange={e => patchForm({ dtEnd: e.target.value })}
              min={minimumEndValue(form.dtStart)}
              disabled={isFormDisabled}
              className={formInputClass} required />
          </Field>
          <Field label="地点">
            <input type="text" value={form.location ?? ''} onChange={e => patchForm({ location: e.target.value })}
              disabled={isFormDisabled}
              className={formInputClass} />
          </Field>
          <div className="mb-3">
            <span className="text-sm font-medium text-gray-600 block mb-1">描述</span>
            <RichDescriptionEditor
              value={descriptionDisplayHtml}
              onChange={handleDescriptionHtmlChange}
              disabled={isFormDisabled}
            />
          </div>
        </EventSection>
        <EventSection title="高级">
          <EventAdvancedFields form={form} onChange={patchForm} disabled={isFormDisabled} />
        </EventSection>
        <EventSection title="协作">
          <EventCollaborationFields form={form} onChange={patchForm} disabled={isFormDisabled} providerReadOnly={isOutlook} />
        </EventSection>
        <EventSection title="会议">
          <EventMeetingFields form={form} onChange={patchForm} disabled={isFormDisabled} providerReadOnly={isOutlook} />
        </EventSection>
        <EventSection title="附件">
          <EventAttachmentFields form={form} onChange={patchForm} disabled={isFormDisabled} providerReadOnly={isOutlook} />
        </EventSection>
        {isNativeSeries && !isFormDisabled && (
          <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3">
            <p className="text-sm font-medium text-slate-700 mb-2">编辑范围</p>
            <div className="flex gap-4">
              <label className="flex items-center gap-2 text-sm text-slate-600">
                <input type="radio" name="native-scope" value="this" checked={nativeScope === 'this'} onChange={() => setNativeScope('this')} className="text-blue-600 focus:ring-blue-200" />
                此实例
              </label>
              <label className="flex items-center gap-2 text-sm text-slate-600">
                <input type="radio" name="native-scope" value="series" checked={nativeScope === 'series'} onChange={() => setNativeScope('series')} className="text-blue-600 focus:ring-blue-200" />
                整个系列
              </label>
            </div>
          </div>
        )}
        <EventSection title="重复">
          {isFormDisabled ? (
            <EventRecurrenceSummary rrule={form.rrule} />
          ) : event?.isException ? (
            <div className="space-y-2">
              <p className="text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded px-3 py-2">此为重复日程的例外实例，修改后将仅影响此实例。如需修改整个系列，请编辑主事件。</p>
              <EventRecurrenceSummary rrule={form.rrule} />
            </div>
          ) : (
            <RecurrenceRuleEditor
              value={form.rrule}
              onChange={rrule => patchForm({ rrule, isSeriesMaster: !!rrule })}
              disabled={isFormDisabled}
            />
          )}
        </EventSection>
        {(isOutlook || event?.source === 'outlook-ics') && <OutlookAdditionalInfo info={event?.outlookAdditionalInfo} />}
      </form>
        </div>
        <footer className="flex items-center justify-between border-t border-zinc-200 px-5 py-4 shrink-0">
          <div>
            {event && !isReadOnly && (
              <button type="button" onClick={handleDelete}
                disabled={isProcessing}
                className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600 disabled:opacity-50">
                删除
              </button>
            )}
          </div>
          <div className="flex gap-2">
            <button type="button" onClick={onClose}
              className="pim-button-secondary px-4 py-2 text-sm">取消</button>
            {!isReadOnly && (
              <button type="submit" form="event-editor-form"
                disabled={isProcessing || isLoading || (!event && !hasWritableCalendar(calendars || [], hiddenCalendarIds))}
                className="pim-button-primary px-4 py-2 text-sm disabled:opacity-50">
                {event ? '保存' : '创建'}
              </button>
            )}
          </div>
        </footer>
      </aside>
    </div>
    {!isOutlookExisting && (
      <ConfirmActionDialog
        open={deleteInput !== null}
        input={deleteInput}
        isPending={deleteMut.isPending}
        onCancel={cancelDelete}
        onConfirm={confirmDelete}
      />
    )}
    {isWritebackActive && (
      <OutlookWritebackConfirmDialog
        open={isWritebackActive}
        phase={writebackPhase}
        operation={pendingRequest?.operation || 'update'}
        before={diffBefore}
        after={diffAfter}
        scope={outlookScope}
        showScope={showScopeRadio}
        accountName={outlookSettings?.activeAuthorization?.accountDisplayName || null}
        calendarName={selectedCalendar?.name ?? eventCalendar?.name ?? null}
        onScopeChange={setOutlookScope}
        onConfirm={confirmWriteback}
        onCancel={cancelWriteback}
        onRetryWithLatest={retryWithLatest}
      />
    )}
    </>
  );
}

function OutlookWritebackConfirmDialog({
  open,
  phase,
  operation,
  before,
  after,
  scope,
  showScope,
  accountName,
  calendarName,
  onScopeChange,
  onConfirm,
  onCancel,
  onRetryWithLatest,
}: {
  open: boolean;
  phase: WritebackPhase;
  operation: string;
  before: EventFieldDiffInput;
  after: EventFieldDiffInput;
  scope: 'instance' | 'series';
  showScope: boolean;
  accountName: string | null;
  calendarName: string | null;
  onScopeChange: (value: 'instance' | 'series') => void;
  onConfirm: () => void;
  onCancel: () => void;
  onRetryWithLatest: () => void;
}) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const titleId = useId();

  const latestSummary = useMemo(() => {
    if (phase.type !== 'conflict' || !phase.latestEvent) return [];
    return summarizeEventFields(
      toDiffRecord(phase.latestEvent as unknown as Record<string, unknown>),
    );
  }, [phase]);

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    const dialog = dialogRef.current;
    dialog?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, [open]);

  if (!open) return null;

  function getFocusableElements() {
    const dialog = dialogRef.current;
    if (!dialog) return [];
    return Array.from(
      dialog.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ),
    ).filter(element => !element.hasAttribute('aria-hidden'));
  }

  function handleKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      if (phase.type === 'preview' || phase.type === 'conflict') {
        onCancel();
      }
      return;
    }
    if (e.key !== 'Tab') return;
    const focusableElements = getFocusableElements();
    if (focusableElements.length === 0) {
      e.preventDefault();
      dialogRef.current?.focus();
      return;
    }
    const firstElement = focusableElements[0];
    const lastElement = focusableElements[focusableElements.length - 1];
    const activeElement = document.activeElement;
    if (e.shiftKey && (activeElement === firstElement || activeElement === dialogRef.current)) {
      e.preventDefault();
      lastElement.focus();
    } else if (!e.shiftKey && (activeElement === lastElement || activeElement === dialogRef.current)) {
      e.preventDefault();
      firstElement.focus();
    }
  }

  const isPending = phase.type === 'submitting';
  const isConflict = phase.type === 'conflict';
  const isError = phase.type === 'error';

  const opLabel = operation === 'delete' ? '删除' : operation === 'create' ? '创建' : '更新';

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-slate-950/30 px-4 py-6">
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="w-full max-w-lg max-h-[85vh] flex flex-col rounded-lg border border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4 shrink-0">
          <p className="text-xs font-semibold uppercase tracking-wide text-blue-600">Outlook 写回 确认</p>
          <h2 id={titleId} className="mt-1 text-base font-semibold text-slate-950">
            {opLabel} Outlook 日程
          </h2>
          <p className="mt-2 text-sm leading-6 text-slate-600">
            {operation === 'delete' ? '此操作将同步删除 Outlook 中的日程，请确认。' : '以下变更将同步写回 Outlook，请确认后再提交。'}
          </p>
          {isConflict && (
            <div className="mt-3 rounded-lg border border-orange-200 bg-orange-50 px-3 py-2 text-sm text-orange-700">
              <p className="font-medium">变更冲突 (conflict)</p>
              <p className="mt-1">Outlook 中的日程已被其他人修改，以下是 Outlook 中的最新内容，请参考后重新比较并确认。</p>
              {(phase as { type: 'conflict'; errorMessage?: string | null }).errorMessage && (
                <p className="mt-1 text-xs">{(phase as { type: 'conflict'; errorMessage?: string | null }).errorMessage}</p>
              )}
            </div>
          )}
          {isError && (
            <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              {(phase as { type: 'error'; message: string }).message}
            </div>
          )}
        </header>

        <section className="px-5 py-4 overflow-auto">
          {showScope && (
            <div className="mb-4 rounded-lg border border-slate-200 bg-slate-50 px-3 py-3">
              <p className="text-sm font-medium text-slate-700 mb-2">变更范围</p>
              <div className="flex gap-4">
                <label className="flex items-center gap-2 text-sm text-slate-600">
                  <input
                    type="radio"
                    name="outlook-scope"
                    value="instance"
                    checked={scope === 'instance'}
                    onChange={() => onScopeChange('instance')}
                    disabled={isPending}
                    className="text-blue-600 focus:ring-blue-200"
                  />
                  仅此 实例
                </label>
                <label className="flex items-center gap-2 text-sm text-slate-600">
                  <input
                    type="radio"
                    name="outlook-scope"
                    value="series"
                    checked={scope === 'series'}
                    onChange={() => onScopeChange('series')}
                    disabled={isPending}
                    className="text-blue-600 focus:ring-blue-200"
                  />
                  整个 系列
                </label>
              </div>
            </div>
          )}

          {isConflict && (
            <div className="mb-4 rounded-lg border border-slate-200 bg-slate-50 px-3 py-3">
              <p className="text-sm font-medium text-slate-700 mb-2">最新 Outlook 内容</p>
              {latestSummary.length === 0 ? (
                <p className="text-xs text-slate-500">无法获取最新内容，请关闭后重试。</p>
              ) : (
                <ul className="space-y-1">
                  {latestSummary.map(item => (
                    <li key={item.key} className="flex min-w-0 gap-2 text-sm leading-6 text-slate-600">
                      <span className="w-24 shrink-0 truncate text-xs font-semibold text-slate-500">{item.label}</span>
                      <span className="min-w-0 flex-1 break-words">{formatFieldValue(item.after, item.key)}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}

          <BeforeAfterDiff
            before={before}
            after={after}
            meta={{
              operation,
              accountName,
              calendarName,
              scope: showScope ? scope : undefined,
            }}
          />
        </section>

        <footer className="flex items-center justify-end gap-2 border-t border-slate-200 px-5 py-4 shrink-0">
          {isConflict ? (
            <>
              {phase.type === 'conflict' && phase.latestEtag ? (
                <button
                  type="button"
                  onClick={onRetryWithLatest}
                  className="rounded-md border border-blue-300 bg-blue-50 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100"
                >
                  基于 Outlook 最新版本重新比较
                </button>
              ) : (
                <p className="text-xs text-red-600 mr-auto">无法获取最新版本标识，请关闭后重试。</p>
              )}
              <button
                type="button"
                onClick={onCancel}
                className="shrink-0 whitespace-nowrap rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
              >
                取消
              </button>
            </>
          ) : isError ? (
            <>
              <button
                type="button"
                onClick={onCancel}
                className="rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
              >
                关闭
              </button>
            </>
          ) : (
            <>
              <button
                type="button"
                onClick={onCancel}
                disabled={isPending}
                className="rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50"
              >
                取消
              </button>
              <button
                type="button"
                onClick={onConfirm}
                disabled={isPending}
                className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {isPending ? '提交中' : '确认'}
              </button>
            </>
          )}
        </footer>
      </div>
    </div>
  );
}
