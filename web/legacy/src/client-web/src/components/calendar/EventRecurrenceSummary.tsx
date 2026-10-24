interface EventRecurrenceSummaryProps {
  rrule?: string | null;
}

const FREQ_LABELS: Record<string, string> = {
  DAILY: '每天',
  WEEKLY: '每周',
  MONTHLY: '每月',
  YEARLY: '每年',
};

function summarizeRrule(rrule?: string | null): string | null {
  if (!rrule) return null;

  const freqMatch = rrule.match(/FREQ=(\w+)/);
  const freq = freqMatch?.[1]?.toUpperCase();
  const intervalMatch = rrule.match(/INTERVAL=(\d+)/);
  const interval = intervalMatch ? Number(intervalMatch[1]) : 1;
  const untilMatch = rrule.match(/UNTIL=(\d{4})(\d{2})(\d{2})(?:T\d{6})?(?:Z)?/);

  const freqLabel = freq ? FREQ_LABELS[freq] : undefined;
  let summary: string;
  if (freqLabel && interval > 1) {
    const unit = freq === 'DAILY' ? '天' : freq === 'WEEKLY' ? '周' : freq === 'MONTHLY' ? '个月' : '年';
    summary = `每隔 ${interval} ${unit}`;
  } else if (freqLabel) {
    summary = freqLabel;
  } else {
    summary = '重复日程';
  }

  if (untilMatch) {
    summary += `，持续至 ${untilMatch[1]}-${untilMatch[2]}-${untilMatch[3]}`;
  }

  return summary;
}

export default function EventRecurrenceSummary({ rrule }: EventRecurrenceSummaryProps) {
  const summary = summarizeRrule(rrule);
  return (
    <p className="event-recurrence-summary text-sm text-slate-600">
      {summary ?? '此日程不重复'}
    </p>
  );
}
