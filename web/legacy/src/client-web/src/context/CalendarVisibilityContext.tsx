import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';

const STORAGE_KEY = 'pim_hidden_calendars';

function loadHiddenIds(): Set<string> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return new Set();
    const parsed = JSON.parse(raw);
    return new Set<string>(Array.isArray(parsed) ? parsed : []);
  } catch {
    return new Set();
  }
}

function saveHiddenIds(ids: Set<string>): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...ids]));
  } catch {
    // localStorage unavailable (private mode / quota): degrade to in-memory only
  }
}

interface VisibilityContext {
  hiddenCalendarIds: Set<string>;
  toggleCalendar: (id: string) => void;
}

const CalendarVisibilityContext = createContext<VisibilityContext>({
  hiddenCalendarIds: new Set(),
  toggleCalendar: () => {}
});

export function CalendarVisibilityProvider({ children }: { children: ReactNode }) {
  const [hiddenIds, setHiddenIds] = useState<Set<string>>(loadHiddenIds);

  useEffect(() => {
    saveHiddenIds(hiddenIds);
  }, [hiddenIds]);

  const toggleCalendar = useCallback((id: string) => {
    setHiddenIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  return (
    <CalendarVisibilityContext.Provider value={{ hiddenCalendarIds: hiddenIds, toggleCalendar }}>
      {children}
    </CalendarVisibilityContext.Provider>
  );
}

export function useCalendarVisibility() {
  return useContext(CalendarVisibilityContext);
}
