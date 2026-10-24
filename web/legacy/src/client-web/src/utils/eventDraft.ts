import { datetimeLocalToUtcIso } from './dateTimeInput';
import type {
  EventAttendee,
  EventAttachmentReference,
  EventPerson,
  UnifiedEventDraft,
} from '../types';

export interface EventFormValue {
  calendarId: string;
  title: string;
  description?: string | null;
  descriptionFormat?: string | null;
  location?: string | null;
  dtStart: string;
  dtEnd: string;
  rrule?: string | null;
  isAllDay?: boolean;
  timeZoneId?: string | null;
  showAs?: string | null;
  importance?: string | null;
  sensitivity?: string | null;
  categories?: string[] | null;
  isReminderOn?: boolean | null;
  reminderMinutesBeforeStart?: number | null;
  organizer?: EventPerson | null;
  attendees?: EventAttendee[] | null;
  isOnlineMeeting?: boolean | null;
  onlineMeetingProvider?: string | null;
  onlineMeetingUrl?: string | null;
  externalLink?: string | null;
  attachmentReferences?: EventAttachmentReference[] | null;
  isSeriesMaster?: boolean | null;
  isException?: boolean | null;
  seriesMasterId?: string | null;
  recurrenceId?: string | null;
}

function normalizeString(value: string | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  const trimmed = value.trim();
  return trimmed === '' ? null : trimmed;
}

function dedupeCategories(values: string[] | null | undefined): string[] {
  if (!values) return [];
  const seen = new Set<string>();
  const result: string[] = [];
  for (const value of values) {
    const trimmed = value.trim();
    if (trimmed === '' || seen.has(trimmed)) continue;
    seen.add(trimmed);
    result.push(trimmed);
  }
  return result;
}

function normalizeOrganizer(organizer: EventPerson | null | undefined): EventPerson | null {
  if (!organizer) return null;
  const name = normalizeString(organizer.name);
  const email = normalizeString(organizer.email);
  if (!name && !email) return null;
  return { name, email };
}

function normalizeAttendees(attendees: EventAttendee[] | null | undefined): EventAttendee[] {
  if (!attendees) return [];
  const result: EventAttendee[] = [];
  for (const attendee of attendees) {
    if (!attendee) continue;
    const email = normalizeString(attendee.email);
    if (!email) continue;
    result.push({
      name: normalizeString(attendee.name),
      email,
      type: normalizeString(attendee.type) ?? 'required',
    });
  }
  return result;
}

export function buildUnifiedEventDraft(form: EventFormValue): UnifiedEventDraft {
  const timeZoneId = normalizeString(form.timeZoneId);
  return {
    calendarId: form.calendarId,
    title: form.title.trim(),
    description: normalizeString(form.description),
    descriptionFormat: normalizeString(form.descriptionFormat),
    location: normalizeString(form.location),
    dtStart: datetimeLocalToUtcIso(form.dtStart, timeZoneId ?? undefined),
    dtEnd: datetimeLocalToUtcIso(form.dtEnd, timeZoneId ?? undefined),
    rrule: normalizeString(form.rrule),
    isAllDay: Boolean(form.isAllDay),
    timeZoneId,
    showAs: normalizeString(form.showAs),
    importance: normalizeString(form.importance),
    sensitivity: normalizeString(form.sensitivity),
    categories: dedupeCategories(form.categories),
    isReminderOn: form.isReminderOn ?? null,
    reminderMinutesBeforeStart: form.isReminderOn ? (form.reminderMinutesBeforeStart ?? null) : null,
    organizer: normalizeOrganizer(form.organizer),
    attendees: normalizeAttendees(form.attendees),
    isOnlineMeeting: form.isOnlineMeeting ?? null,
    onlineMeetingProvider: normalizeString(form.onlineMeetingProvider),
    onlineMeetingUrl: normalizeString(form.onlineMeetingUrl),
    externalLink: normalizeString(form.externalLink),
    attachmentReferences: form.attachmentReferences?.filter((a): a is EventAttachmentReference => !!a) ?? [],
    isSeriesMaster: form.isSeriesMaster ?? null,
    isException: form.isException ?? null,
    seriesMasterId: normalizeString(form.seriesMasterId),
    recurrenceId: normalizeString(form.recurrenceId),
  };
}
