import { format } from 'date-fns';
import type { TaskResponse } from '../types';

/** Formats a Date as a datetime-local input value (yyyy-MM-dd'T'HH:mm). */
export function toLocalDateTimeInputValue(date: Date): string {
  return format(date, "yyyy-MM-dd'T'HH:mm");
}

/** Parses an ISO-ish datetime string; returns null for empty or invalid input. */
export function parseCalendarDate(value?: string): Date | null {
  if (!value) return null;

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * Parses a .NET TimeSpan-ish duration string (e.g. "01:30:00" or "1.02:30:00.1234567")
 * into milliseconds; returns null for empty or malformed values.
 */
export function parseTimeSpanMs(value?: string): number | null {
  if (!value) return null;

  const match = /^(?:(\d+)\.)?(\d+):([0-5]\d):([0-5]\d)(?:\.(\d{1,7}))?$/.exec(value);
  if (!match) return null;

  const [, days = '0', hours, minutes, seconds, fraction = ''] = match;
  const baseMs = (
    Number(days) * 24 * 60 * 60
    + Number(hours) * 60 * 60
    + Number(minutes) * 60
    + Number(seconds)
  ) * 1000;
  const fractionMs = fraction
    ? Number(`0.${fraction}`) * 1000
    : 0;

  return baseMs + fractionMs;
}

/**
 * Computes the planned end for a task dropped at `plannedStart`.
 * Keeps the existing planned window duration when known, otherwise falls back
 * to the estimated duration, then to the task due date.
 */
export function getPlannedEndForDrop(task: TaskResponse, plannedStart: string): string | undefined {
  const plannedStartDate = parseCalendarDate(plannedStart);
  if (!plannedStartDate) return task.due;

  const existingPlannedEnd = parseCalendarDate(task.plannedEnd);
  const existingPlannedStart = parseCalendarDate(task.dtStart);
  if (existingPlannedEnd && existingPlannedStart) {
    const durationMs = existingPlannedEnd.getTime() - existingPlannedStart.getTime();
    if (durationMs > 0) return toLocalDateTimeInputValue(new Date(plannedStartDate.getTime() + durationMs));
  }

  const estimatedDurationMs = parseTimeSpanMs(task.estimatedDuration);
  if (estimatedDurationMs && estimatedDurationMs > 0) {
    return toLocalDateTimeInputValue(new Date(plannedStartDate.getTime() + estimatedDurationMs));
  }

  return task.due;
}
