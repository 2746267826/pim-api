import { useMemo, useState, useEffect } from 'react';

interface Props {
  value?: string | null;
  onChange: (rrule: string | null) => void;
  disabled?: boolean;
}

type Frequency = 'none' | 'DAILY' | 'WEEKLY' | 'MONTHLY' | 'YEARLY';
type EndMode = 'never' | 'count' | 'until';

const WEEKDAYS = [
  { value: 'MO', label: '一' },
  { value: 'TU', label: '二' },
  { value: 'WE', label: '三' },
  { value: 'TH', label: '四' },
  { value: 'FR', label: '五' },
  { value: 'SA', label: '六' },
  { value: 'SU', label: '日' },
];

function parseRrule(rrule?: string | null): {
  freq: Frequency;
  interval: number;
  byDay: string[];
  endMode: EndMode;
  count?: number;
  until?: string;
} {
  if (!rrule) return { freq: 'none', interval: 1, byDay: [], endMode: 'never' };
  const freqMatch = rrule.match(/FREQ=(\w+)/);
  const freq = (freqMatch?.[1]?.toUpperCase() as Frequency) || 'none';
  const intervalMatch = rrule.match(/INTERVAL=(\d+)/);
  const interval = intervalMatch ? Number(intervalMatch[1]) : 1;
  const byDayMatch = rrule.match(/BYDAY=([^;]+)/);
  const byDay = byDayMatch ? byDayMatch[1].split(',') : [];
  const countMatch = rrule.match(/COUNT=(\d+)/);
  const untilMatch = rrule.match(/UNTIL=([^;]+)/);
  if (countMatch) {
    return { freq: freq === 'none' ? 'DAILY' : freq, interval, byDay, endMode: 'count', count: Number(countMatch[1]) };
  }
  if (untilMatch) {
    // UNTIL is like 20261231T235959Z or 20261231
    const raw = untilMatch[1];
    // try to format as yyyy-MM-dd
    let until = raw;
    if (/^\d{8}T/.test(raw)) {
      const y = raw.slice(0, 4), m = raw.slice(4, 6), d = raw.slice(6, 8);
      until = `${y}-${m}-${d}`;
    } else if (/^\d{8}$/.test(raw)) {
      until = `${raw.slice(0, 4)}-${raw.slice(4, 6)}-${raw.slice(6, 8)}`;
    }
    return { freq: freq === 'none' ? 'DAILY' : freq, interval, byDay, endMode: 'until', until };
  }
  return { freq: freq === 'none' ? 'none' : freq, interval, byDay, endMode: 'never' };
}

function buildRrule(state: { freq: Frequency; interval: number; byDay: string[]; endMode: EndMode; count?: number; until?: string }): string | null {
  if (state.freq === 'none') return null;
  let rrule = `FREQ=${state.freq}`;
  if (state.interval > 1) rrule += `;INTERVAL=${state.interval}`;
  if (state.freq === 'WEEKLY' && state.byDay.length > 0) {
    rrule += `;BYDAY=${state.byDay.join(',')}`;
  }
  if (state.endMode === 'count' && state.count && state.count > 0) {
    rrule += `;COUNT=${state.count}`;
  } else if (state.endMode === 'until' && state.until) {
    // Convert yyyy-MM-dd to 20261231T235959Z (23:59:59Z so same-day 09:00 occurrence is included)
    // Use Date.UTC from parts to avoid local-timezone shift when parsing yyyy-MM-dd
    const parts = state.until.split('-').map(Number);
    if (parts.length === 3 && parts.every(n => !isNaN(n))) {
      const [y, m, d] = parts;
      const utc = new Date(Date.UTC(y, m - 1, d, 23, 59, 59));
      if (!isNaN(utc.getTime())) {
        const ys = utc.getUTCFullYear().toString().padStart(4, '0');
        const ms = (utc.getUTCMonth() + 1).toString().padStart(2, '0');
        const day = utc.getUTCDate().toString().padStart(2, '0');
        rrule += `;UNTIL=${ys}${ms}${day}T235959Z`;
      }
    }
  }
  return rrule;
}

export default function RecurrenceRuleEditor({ value, onChange, disabled }: Props) {
  const parsed = useMemo(() => parseRrule(value), [value]);
  const [freq, setFreq] = useState<Frequency>(parsed.freq);
  const [interval, setInterval] = useState<number>(parsed.interval);
  const [byDay, setByDay] = useState<string[]>(parsed.byDay);
  const [endMode, setEndMode] = useState<EndMode>(parsed.endMode);
  const [count, setCount] = useState<number>(parsed.count ?? 10);
  const [until, setUntil] = useState<string>(parsed.until ?? '');

  useEffect(() => {
    setFreq(parsed.freq);
    setInterval(parsed.interval);
    setByDay(parsed.byDay);
    setEndMode(parsed.endMode);
    setCount(parsed.count ?? 10);
    setUntil(parsed.until ?? '');
  }, [parsed.freq, parsed.interval, parsed.byDay.join(','), parsed.endMode, parsed.count, parsed.until]);

  function emit(next: { freq: Frequency; interval: number; byDay: string[]; endMode: EndMode; count?: number; until?: string }) {
    const rrule = buildRrule(next);
    onChange(rrule);
  }

  function handleFreqChange(nextFreq: Frequency) {
    setFreq(nextFreq);
    emit({ freq: nextFreq, interval, byDay, endMode, count, until });
  }

  function handleIntervalChange(v: number) {
    const n = Math.max(1, Math.min(30, Math.floor(v) || 1));
    setInterval(n);
    emit({ freq, interval: n, byDay, endMode, count, until });
  }

  function toggleDay(day: string) {
    const next = byDay.includes(day) ? byDay.filter(d => d !== day) : [...byDay, day];
    setByDay(next);
    emit({ freq, interval, byDay: next, endMode, count, until });
  }

  function handleEndModeChange(mode: EndMode) {
    setEndMode(mode);
    emit({ freq, interval, byDay, endMode: mode, count, until });
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <label className="flex flex-col gap-1 text-sm">
          <span className="font-medium text-slate-700">重复频率</span>
          <select
            value={freq}
            onChange={e => handleFreqChange(e.target.value as Frequency)}
            disabled={disabled}
            className="w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100"
          >
            <option value="none">不重复</option>
            <option value="DAILY">每天</option>
            <option value="WEEKLY">每周</option>
            <option value="MONTHLY">每月</option>
            <option value="YEARLY">每年</option>
          </select>
        </label>
        {freq !== 'none' && (
          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">间隔</span>
            <div className="flex items-center gap-2">
              <input
                type="number"
                min={1}
                max={30}
                value={interval}
                onChange={e => handleIntervalChange(Number(e.target.value))}
                disabled={disabled}
                className="w-20 border rounded px-3 py-2 text-sm disabled:bg-slate-100"
              />
              <span className="text-slate-600 text-xs">
                {freq === 'DAILY' ? '天' : freq === 'WEEKLY' ? '周' : freq === 'MONTHLY' ? '个月' : '年'}
              </span>
            </div>
          </label>
        )}
      </div>

      {freq === 'WEEKLY' && (
        <div className="space-y-1">
          <span className="text-sm font-medium text-slate-700">重复日</span>
          <div className="flex flex-wrap gap-2">
            {WEEKDAYS.map(d => (
              <label key={d.value} className={`flex items-center gap-1 rounded border px-2 py-1 text-xs cursor-pointer ${byDay.includes(d.value) ? 'bg-blue-50 border-blue-300 text-blue-700' : 'bg-white border-slate-200 text-slate-600'}`}>
                <input
                  type="checkbox"
                  checked={byDay.includes(d.value)}
                  onChange={() => toggleDay(d.value)}
                  disabled={disabled}
                  className="h-3 w-3"
                />
                周{d.label}
              </label>
            ))}
          </div>
        </div>
      )}

      {freq !== 'none' && (
        <div className="space-y-2">
          <span className="text-sm font-medium text-slate-700">结束条件</span>
          <div className="flex flex-col gap-2">
            <label className="flex items-center gap-2 text-sm">
              <input type="radio" name="endMode" value="never" checked={endMode === 'never'} onChange={() => handleEndModeChange('never')} disabled={disabled} />
              永不结束
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="radio" name="endMode" value="count" checked={endMode === 'count'} onChange={() => handleEndModeChange('count')} disabled={disabled} />
              重复
              <input
                type="number"
                min={1}
                max={99}
                value={count}
                onChange={e => { const v = Number(e.target.value); setCount(v); emit({ freq, interval, byDay, endMode: 'count', count: v, until }); }}
                disabled={disabled || endMode !== 'count'}
                className="w-16 border rounded px-2 py-1 text-sm disabled:bg-slate-100"
              />
              次后结束
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input type="radio" name="endMode" value="until" checked={endMode === 'until'} onChange={() => handleEndModeChange('until')} disabled={disabled} />
              截止到
              <input
                type="date"
                value={until}
                onChange={e => { setUntil(e.target.value); emit({ freq, interval, byDay, endMode: 'until', count, until: e.target.value }); }}
                disabled={disabled || endMode !== 'until'}
                className="border rounded px-2 py-1 text-sm disabled:bg-slate-100"
              />
            </label>
          </div>
        </div>
      )}

      <div className="rounded bg-slate-50 border border-slate-200 px-3 py-2 text-xs font-mono break-all text-slate-600">
        {value || '不重复（无 RRule）'}
      </div>
    </div>
  );
}
