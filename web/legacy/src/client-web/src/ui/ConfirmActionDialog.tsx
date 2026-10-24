import { useEffect, useId, useRef, type KeyboardEvent } from 'react';
import type { CalendarOperationSample } from '../types';
import {
  buildDeleteConfirmationCopy,
  getOperationSampleTypeLabel,
  type DeleteConfirmationInput,
} from './confirmActionDialogModel';

export {
  type DeleteConfirmationCopy,
  type DeleteConfirmationInput,
} from './confirmActionDialogModel';
// eslint-disable-next-line react-refresh/only-export-components -- Compatibility export; implementation lives in the model module.
export { buildDeleteConfirmationCopy } from './confirmActionDialogModel';

interface ConfirmActionDialogProps {
  open: boolean;
  input: DeleteConfirmationInput | null;
  isPending?: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}

function formatSampleTime(sample: CalendarOperationSample) {
  if (sample.start && sample.end) return `${sample.start} - ${sample.end}`;
  return sample.start || sample.end || null;
}

export default function ConfirmActionDialog({
  open,
  input,
  isPending = false,
  onCancel,
  onConfirm,
}: ConfirmActionDialogProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const titleId = useId();

  useEffect(() => {
    if (!open || !input) return;

    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    const dialog = dialogRef.current;
    dialog?.focus();

    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, [open, input]);

  if (!open || !input) return null;

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
      onCancel();
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

  const copy = buildDeleteConfirmationCopy(input);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/30 px-4 py-6">
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="w-full max-w-lg rounded-lg border border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4">
          <p className="text-xs font-semibold uppercase tracking-wide text-red-600">严格确认</p>
          <h2 id={titleId} className="mt-1 text-base font-semibold text-slate-950">
            {copy.title}
          </h2>
          <p className="mt-2 text-sm leading-6 text-slate-600">{copy.description}</p>
        </header>

        <section className="px-5 py-4">
          <div className="flex items-center justify-between gap-3">
            <h3 className="text-sm font-medium text-slate-800">受影响样例</h3>
            <span className="rounded-md bg-slate-100 px-2 py-1 text-xs font-medium text-slate-600">
              共 {input.affectedCount} 项
            </span>
          </div>

          {copy.samples.length > 0 ? (
            <ul className="mt-3 max-h-56 space-y-2 overflow-auto">
              {copy.samples.map(sample => {
                const sampleTime = formatSampleTime(sample);

                return (
                  <li key={`${sample.type}:${sample.id}`} className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2">
                    <div className="flex items-start justify-between gap-3">
                      <div className="min-w-0">
                        <p className="truncate text-sm font-medium text-slate-900">{sample.title}</p>
                        <p className="mt-0.5 text-xs text-slate-500">
                          {getOperationSampleTypeLabel(sample.type)}
                          {sample.bookName ? ` · ${sample.bookName}` : ''}
                        </p>
                      </div>
                      {sampleTime && <span className="shrink-0 text-xs text-slate-500">{sampleTime}</span>}
                    </div>
                  </li>
                );
              })}
            </ul>
          ) : (
            <p className="mt-3 rounded-md border border-dashed border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-500">
              暂无可预览项目。
            </p>
          )}
        </section>

        <footer className="flex items-center justify-end gap-2 border-t border-slate-200 px-5 py-4">
          <button
            type="button"
            onClick={onCancel}
            className="rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
          >
            取消
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={isPending}
            className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isPending ? '处理中' : copy.confirmLabel}
          </button>
        </footer>
      </div>
    </div>
  );
}
