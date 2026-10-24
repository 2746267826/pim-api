import { useEffect, useRef } from 'react';

export interface ShellShareDetail { text?: string; url?: string }

export function useShellShare(onShare: (detail: ShellShareDetail) => void) {
  const ref = useRef(onShare);
  ref.current = onShare;
  useEffect(() => {
    if (typeof window === 'undefined') return;
    const handler = (e: Event) => {
      const detail = (e as CustomEvent<ShellShareDetail>).detail;
      if (detail && (typeof detail.text === 'string' || typeof detail.url === 'string')) {
        ref.current(detail);
      }
    };
    window.addEventListener('pim-shell:share', handler as EventListener);
    return () => window.removeEventListener('pim-shell:share', handler as EventListener);
  }, []);
}
