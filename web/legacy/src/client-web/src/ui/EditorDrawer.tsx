import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';

interface Props {
  open: boolean;
  title: string;
  subtitle?: string;
  onClose: () => void;
  children: ReactNode;
  footer: ReactNode;
}

export default function EditorDrawer({
  open,
  title,
  subtitle,
  onClose,
  children,
  footer,
}: Props) {
  const drawerRef = useRef<HTMLElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const titleId = useId();

  useEffect(() => {
    if (!open) return;

    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;

    const drawer = drawerRef.current;
    drawer?.focus();

    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, [open]);

  if (!open) return null;

  function getFocusableElements() {
    const drawer = drawerRef.current;
    if (!drawer) return [];

    return Array.from(
      drawer.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
      )
    ).filter(element => !element.hasAttribute('aria-hidden'));
  }

  function handleKeyDown(e: KeyboardEvent<HTMLElement>) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      onClose();
      return;
    }

    if (e.key !== 'Tab') return;

    const focusableElements = getFocusableElements();
    if (focusableElements.length === 0) {
      e.preventDefault();
      drawerRef.current?.focus();
      return;
    }

    const firstElement = focusableElements[0];
    const lastElement = focusableElements[focusableElements.length - 1];
    const activeElement = document.activeElement;

    if (e.shiftKey && (activeElement === firstElement || activeElement === drawerRef.current)) {
      e.preventDefault();
      lastElement.focus();
    } else if (!e.shiftKey && (activeElement === lastElement || activeElement === drawerRef.current)) {
      e.preventDefault();
      firstElement.focus();
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <div
        className="absolute inset-0 bg-slate-950/20"
        onClick={onClose}
      />
      <aside
        ref={drawerRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="relative flex h-full w-full max-w-[420px] flex-col border-l border-slate-200 bg-white shadow-2xl"
      >
        <header className="border-b border-slate-200 px-5 py-4">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 id={titleId} className="text-base font-semibold text-slate-950">{title}</h2>
              {subtitle && <p className="mt-1 text-sm text-slate-500">{subtitle}</p>}
            </div>
            <button type="button" onClick={onClose} className="pim-button-secondary px-3 py-1.5 text-sm">
              关闭
            </button>
          </div>
        </header>
        <div className="flex-1 overflow-auto px-5 py-4">{children}</div>
        <footer className="flex items-center justify-between gap-3 border-t border-slate-200 px-5 py-4">
          {footer}
        </footer>
      </aside>
    </div>
  );
}
