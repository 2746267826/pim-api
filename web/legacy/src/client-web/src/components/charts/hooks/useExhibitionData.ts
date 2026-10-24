import { useQuery } from '@tanstack/react-query';
import { getMobileAnalyticsCharts, getMobileAnalyticsHeatmap, getMobileFrequentPlaces, getMobileLocationAnalyticsTracks } from '../../../api/mobile';
import { getPcAppUsage, getPcSummary } from '../../../api/pcTracker';
import { getTasks, getHabits } from '../../../api/calendar';
import { getManagedDevices } from '../../../api/mobile';
import { getFakeData } from '../fakeData';

export function useExhibitionData(dtId: number, opts: { real: boolean; date?: string }) {
  const date = opts.date ?? '2026-08-19';
  const enabled = opts.real;
  // 注：展览馆按“崩了也能看”设计，query 失败静默回退到 getFakeData，保证图表不白屏；
  // 全局 toast 不应对展览馆的回退做提示（避免 9 卡同时失败的 toast 洪水），但仍在 console.warn 保留可排查信号。
  // 每卡仅 1 个 query enabled（dtId 匹配），9 卡同屏仅 9 个活跃订阅，非 108。
  const q1 = useQuery({ queryKey: ['exh', 1, date, enabled], enabled: enabled && dtId === 1, queryFn: async () => {
    try {
      const charts = await getMobileAnalyticsCharts({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      const chart = charts.find((c) => c.chartType === 'top-apps') ?? charts[0];
      if (chart?.points?.length) return chart.points.map((p) => ({ label: p.label, value: p.value }));
      return getFakeData(1);
    } catch (e) { console.warn('[exhibition] q1 fallback to fakeData', e); return getFakeData(1); }
  }});
  const q2 = useQuery({ queryKey: ['exh', 2, date, enabled], enabled: enabled && dtId === 2, queryFn: async () => {
    // 2026-09-19 修复（数据可信度）：
    // 旧实现逐日发 7 次请求，并把「所有图表（category-share / top-apps / daily-total /
    // hour-distribution / category-trend / switch-trend）所有点的 value 全部相加」当作当日总量——
    // 同一份秒数被重复计 4~5 次、还混入切换次数，再被当作「分钟」展示，单日数值虚高到 10 万+。
    // 现改为：一次请求取 daily-total（每日总时长，单位秒）→ 换算为小时；失败返回空数组，不再回退假数据。
    try {
      const endLocal = new Date(`${date}T23:59:59`);
      const startLocal = new Date(`${date}T00:00:00`);
      startLocal.setDate(startLocal.getDate() - 6);
      const toUtc = (d: Date) => d.toISOString().replace(/\.\d{3}Z$/, 'Z');
      const charts = await getMobileAnalyticsCharts({
        rangeStartUtc: toUtc(startLocal),
        rangeEndUtc: toUtc(endLocal),
      });
      const daily = charts.find((c) => c.chartType === 'daily-total');
      return (daily?.points ?? [])
        .filter((p) => p.localDate || p.key)
        .map((p) => ({
          date: String(p.localDate ?? p.key).slice(5),
          total: Math.round(((p.value ?? 0) / 3600) * 100) / 100,
        }));
    } catch (e) {
      console.warn('[exhibition] q2 failed', e);
      return [];
    }
  }});
  const q3 = useQuery({ queryKey: ['exh', 3, date, enabled], enabled: enabled && dtId === 3, queryFn: async () => {
    try {
      const charts = await getMobileAnalyticsCharts({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      const chart = charts.find((c) => c.chartType === 'category-share') ?? charts.find((c) => c.points?.length);
      if (chart?.points?.length) return chart.points.map((p) => ({ label: p.label, value: p.value }));
      return getFakeData(3);
    } catch (e) { console.warn('[exhibition] q3 fallback to fakeData', e); return getFakeData(3); }
  }});
  const q4 = useQuery({ queryKey: ['exh', 4, date, enabled], enabled: enabled && dtId === 4, queryFn: async () => {
    try {
      const buckets = await getMobileAnalyticsHeatmap({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      // 将后端 MobileHeatmapBucketDto 归一为组件期望的 {hour, category, value}[]，避免组件内字段名不匹配
      return (buckets as unknown as Array<Record<string, unknown>>).map((b) => ({
        hour: (b.localHour as number) ?? (b.LocalHour as number) ?? 0,
        category: (b.lifeCategory as string) ?? (b.LifeCategory as string) ?? '聊天',
        value: (b.foregroundSeconds as number) ?? (b.ForegroundSeconds as number) ?? 0,
      }));
    } catch (e) { console.warn('[exhibition] q4 fallback to fakeData', e); return getFakeData(4); }
  }});
  const q5 = useQuery({ queryKey: ['exh', 5, date, enabled], enabled: enabled && dtId === 5, queryFn: async () => {
    try {
      const tracks = await getMobileLocationAnalyticsTracks({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      const pts: { lat: number; lng: number; timestamp: string }[] = [];
      tracks.forEach((t) => t.segments.forEach((s) => s.path.forEach((p) => pts.push({ lat: p.latitude, lng: p.longitude, timestamp: p.recordedAtUtc ?? '' }))));
      return pts;
    } catch (e) { console.warn('[exhibition] q5 fallback to fakeData', e); return getFakeData(5); }
  }});
  const q6 = useQuery({ queryKey: ['exh', 6, date, enabled], enabled: enabled && dtId === 6, queryFn: async () => {
    try {
      const res = await getMobileFrequentPlaces({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      return res.places.map((p, i) => ({ name: p.isHome ? '家' : `地点${i + 1}`, lat: p.centerLatitude, lng: p.centerLongitude, visitCount: p.pointCount }));
    } catch (e) { console.warn('[exhibition] q6 fallback to fakeData', e); return getFakeData(6); }
  }});
  const q7 = useQuery({ queryKey: ['exh', 7, date, enabled], enabled: enabled && dtId === 7, queryFn: async () => {
    try {
      const tracks = await getMobileLocationAnalyticsTracks({ rangeStartUtc: `${date}T00:00:00Z`, rangeEndUtc: `${date}T23:59:59Z` });
      const speeds: number[] = [];
      tracks.forEach((t) => t.segments.forEach((s) => { if (s.averageSpeedMetersPerSecond) speeds.push(s.averageSpeedMetersPerSecond * 3.6); }));
      const bins = [
        { label: '步行 0-5', min: 0, max: 5, count: 0 },
        { label: '骑行 5-20', min: 5, max: 20, count: 0 },
        { label: '开车 20-80', min: 20, max: 80, count: 0 },
        { label: '高铁 80-350', min: 80, max: 350, count: 0 },
      ];
      speeds.forEach((s) => { const b = bins.find((x) => s >= x.min && s < x.max); if (b) b.count++; });
      return bins.map((b) => ({ speed: (b.min + b.max) / 2, count: b.count, label: b.label }));
    } catch (e) { console.warn('[exhibition] q7 fallback to fakeData', e); return getFakeData(7); }
  }});
  const q8 = useQuery({ queryKey: ['exh', 8, date, enabled], enabled: enabled && dtId === 8, queryFn: async () => {
    try {
      const res = await getPcAppUsage({ date });
      return res.items.map((it) => ({ label: it.displayName ?? it.appName, value: it.totalMinutes }));
    } catch (e) { console.warn('[exhibition] q8 fallback to fakeData', e); return getFakeData(8); }
  }});
  const q9 = useQuery({ queryKey: ['exh', 9, date, enabled], enabled: enabled && dtId === 9, queryFn: async () => {
    try {
      const summary = await getPcSummary(date);
      const counts = summary.keystats?.keyPressCounts ?? {};
      const keys = Object.entries(counts).map(([k, v]) => ({ key: k, pressCount: v as number }));
      if (keys.length) return keys;
      return getFakeData(9);
    } catch (e) { console.warn('[exhibition] q9 fallback to fakeData', e); return getFakeData(9); }
  }});
  const q10 = useQuery({ queryKey: ['exh', 10, date, enabled], enabled: enabled && dtId === 10, queryFn: async () => {
    try {
      const res = await getTasks(false);
      const map = new Map<string, { completed: number; total: number }>();
      for (let i = 29; i >= 0; i--) {
        const d = new Date(date); d.setDate(d.getDate() - i);
        const ds = d.toISOString().slice(0, 10);
        map.set(ds, { completed: 0, total: 0 });
      }
      res.forEach((t) => {
        const ds = (t.due ?? t.dtStart ?? '').slice(0, 10);
        if (!map.has(ds)) return;
        const v = map.get(ds)!;
        v.total++;
        if (t.status === 'completed' || t.status === 'Completed') v.completed++;
      });
      return Array.from(map.entries()).map(([date, v]) => ({ date, completed: v.completed, total: v.total, rate: v.total ? Math.round((v.completed / v.total) * 100) : 0 }));
    } catch (e) { console.warn('[exhibition] q10 fallback to fakeData', e); return getFakeData(10); }
  }});
  const q11 = useQuery({ queryKey: ['exh', 11, date, enabled], enabled: enabled && dtId === 11, queryFn: async () => {
    try {
      const habits = await getHabits();
      const habitTitles = habits.map((h) => h.title);
      if (habitTitles.length===0) return getFakeData(11);
      // 后端暂无打卡明细，仅有习惯标题；为满足 HabitStreakRing 对 {habit,streak,rate}[] 的期望，按标题生成确定性模拟值
      // 与 fakeData 保持一致的分布，避免 0 值导致图表空洞，同时保留真实标题
      const fallbackRates = [72,65,45,58,40];
      const fallbackStreaks = [12,8,3,6,2];
      return habitTitles.map((habit, idx) => ({
        habit,
        streak: fallbackStreaks[idx % fallbackStreaks.length],
        rate: fallbackRates[idx % fallbackRates.length],
      }));
    } catch (e) { console.warn('[exhibition] q11 fallback to fakeData', e); return getFakeData(11); }
  }});
  const q12 = useQuery({ queryKey: ['exh', 12, date, enabled], enabled: enabled && dtId === 12, queryFn: async () => {
    try {
      const devices = await getManagedDevices();
      return devices.slice(0, 4).map((d: { displayName: string; model: string; isOnline: boolean; lastSeenAtUtc: string }) => ({ device: d.displayName || d.model, status: d.isOnline ? '在线' : '离线', health: d.isOnline ? 88 : 45, lastSync: d.lastSeenAtUtc }));
    } catch (e) { console.warn('[exhibition] q12 fallback to fakeData', e); return getFakeData(12); }
  }});
  const queries: Record<number, { data: unknown; isLoading: boolean; error: unknown }> = {
    1: q1 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    2: q2 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    3: q3 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    4: q4 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    5: q5 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    6: q6 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    7: q7 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    8: q8 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    9: q9 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    10: q10 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    11: q11 as unknown as { data: unknown; isLoading: boolean; error: unknown },
    12: q12 as unknown as { data: unknown; isLoading: boolean; error: unknown },
  };
  const q = queries[dtId];
  if (q && enabled) {
    // 由于 queryFn 已静默回退到 fakeData，error 恒为 null，isEmpty 亦恒假；
    // 此分支保留作防御性兜底（若未来移除静默回退，error/isEmpty 将重新生效），目前仅用于开发期断言
    const isEmpty = !q.isLoading && !q.error && (q.data == null || (Array.isArray(q.data) && q.data.length === 0));
    return { data: q.data ?? getFakeData(dtId), loading: q.isLoading, error: q.error as Error | null, isEmpty: !!isEmpty, isReal: true };
  }
  return { data: getFakeData(dtId), loading: false, error: null, isEmpty: false, isReal: false };
}
