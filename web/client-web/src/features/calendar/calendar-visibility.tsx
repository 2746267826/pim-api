import { createContext, useCallback, useContext, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import type { LayerName } from './types'

export interface LayerToggles {
  events: boolean
  'task-segments': boolean
  habits: boolean
  availability: boolean
  'ai-placeholders': boolean
  outlookOnly: boolean
}

interface CalendarVisibilityValue {
  /** 日历本显隐（键=日历 id；缺省 true） */
  calendarVisibility: Record<string, boolean>
  toggleCalendar: (id: string) => void
  layerToggles: LayerToggles
  toggleLayer: (key: keyof LayerToggles) => void
}

const DEFAULT_TOGGLES: LayerToggles = {
  events: true,
  'task-segments': true,
  habits: true,
  availability: true,
  'ai-placeholders': true,
  outlookOnly: false,
}

const CalendarVisibilityContext = createContext<CalendarVisibilityValue | null>(null)

/** 日历图层显隐（规格 03 §9：Context 持久化到 localStorage pim.calendarLayers） */
export function CalendarVisibilityProvider({ children }: { children: ReactNode }) {
  const [calendarVisibility, setCalendarVisibility] = useState<Record<string, boolean>>(() =>
    getJSON<Record<string, boolean>>(STORAGE_KEYS.calendarLayerVisibility, {}),
  )
  const [layerToggles, setLayerToggles] = useState<LayerToggles>(() => {
    const saved = getJSON<Partial<LayerToggles>>(STORAGE_KEYS.calendarLayerVisibility, {})
    return { ...DEFAULT_TOGGLES, ...saved }
  })

  const toggleCalendar = useCallback((id: string) => {
    setCalendarVisibility((prev) => {
      const next = { ...prev, [id]: !(prev[id] ?? true) }
      setJSON(STORAGE_KEYS.calendarLayerVisibility, { ...next, ...layerToggles })
      return next
    })
  }, [layerToggles])

  const toggleLayer = useCallback((key: keyof LayerToggles) => {
    setLayerToggles((prev) => {
      const next = { ...prev, [key]: !prev[key] }
      setJSON(STORAGE_KEYS.calendarLayerVisibility, { ...calendarVisibility, ...next })
      return next
    })
  }, [calendarVisibility])

  const value = useMemo(
    () => ({ calendarVisibility, toggleCalendar, layerToggles, toggleLayer }),
    [calendarVisibility, toggleCalendar, layerToggles, toggleLayer],
  )

  return (
    <CalendarVisibilityContext.Provider value={value}>
      {children}
    </CalendarVisibilityContext.Provider>
  )
}

export function useCalendarVisibility(): CalendarVisibilityValue {
  const ctx = useContext(CalendarVisibilityContext)
  if (!ctx) throw new Error('useCalendarVisibility 必须在 CalendarVisibilityProvider 内使用')
  return ctx
}

export type { LayerName }
