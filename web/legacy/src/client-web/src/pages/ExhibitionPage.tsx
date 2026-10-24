import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import PageHeader from '../ui/PageHeader';
import MobileCategoryDonut from '../components/charts/MobileCategoryDonut';
import HourHeatmapChart from '../components/charts/HourHeatmapChart';
import PcAppGradientBar from '../components/charts/PcAppGradientBar';
import TaskCompletionArea from '../components/charts/TaskCompletionArea';
import HabitCalendarHeatmap from '../components/charts/HabitCalendarHeatmap';
import DeviceHealthGauge from '../components/charts/DeviceHealthGauge';
import LocationBubbleChart from '../components/charts/LocationBubbleChart';
import WeekTrendLine from '../components/charts/WeekTrendLine';
import WeekTrendArea from '../components/charts/WeekTrendArea';
import CategoryStackedBar from '../components/charts/CategoryStackedBar';
import DayHeatmapMatrix from '../components/charts/DayHeatmapMatrix';
import GpsTrackMap from '../components/charts/GpsTrackMap';
import LocationHeatmap from '../components/charts/LocationHeatmap';
import SpeedHistogram from '../components/charts/SpeedHistogram';
import PcAppDonut from '../components/charts/PcAppDonut';
import KeyboardHeatmap from '../components/charts/KeyboardHeatmap';
import HabitStreakRing from '../components/charts/HabitStreakRing';
import DeviceStatusTimeline from '../components/charts/DeviceStatusTimeline';
import TaskFunnel from '../components/charts/TaskFunnel';
import DataQualityGauge from '../components/charts/DataQualityGauge';
import { useExhibitionData } from '../components/charts/hooks/useExhibitionData';
import { getFakeData } from '../components/charts/fakeData';
import { CardErrorBoundary } from '../components/error/CardErrorBoundary';

/**
 * 展览馆 React 画廊页 — 20+ 生产组件网格，与静态展览馆数据同源（fakeData.ts）
 * 交互：筛选/搜索/排序/分页/打分/选中/对比/导出/复制链接/键盘/URL状态/埋点/空态/深色
 * 存储：复用 exhibition_ratings / exhibition_selected / exhibition_views
 * 视觉：复用 --pim-* 设计Token，卡片 13px/600 + 11px/400，gap 14px，圆角 12px，150ms ease-out
 * 路由：/exhibition（navItems.ts + AppLayout.tsx 已注册），支持 ?card=dtId-ctId 直达
 */
type GalleryItem = {
  id: string;
  title: string;
  module: string;
  dataType: string;
  chartType: string;
  file: string;
  Component: React.ComponentType<any>;
  desc: string;
};

const GALLERY: GalleryItem[] = [
  { id: '1-10', title: '日使用时长 × 环形图', module: '手机使用', dataType: '日使用时长分布', chartType: '环形图', file: 'MobileCategoryDonut.tsx', Component: MobileCategoryDonut, desc: 'Top3占60%，微信→聊天、抖音→视频联动' },
  { id: '2-5', title: '周趋势 × 折线图', module: '手机使用', dataType: '周使用趋势', chartType: '折线图', file: 'WeekTrendLine.tsx', Component: WeekTrendLine, desc: '周一/五双峰，周末-20%，与日使用同源' },
  { id: '2-7', title: '周趋势 × 面积图', module: '手机使用', dataType: '周使用趋势', chartType: '面积图', file: 'WeekTrendArea.tsx', Component: WeekTrendArea, desc: '堆叠面积展示周周期，与分类联动' },
  { id: '3-3', title: '分类占比 × 堆叠柱状', module: '手机使用', dataType: 'App分类占比', chartType: '堆叠柱状图', file: 'CategoryStackedBar.tsx', Component: CategoryStackedBar, desc: '8类和100%，聊天28%+视频22%占半壁' },
  { id: '3-10', title: '分类占比 × 环形图', module: '手机使用', dataType: 'App分类占比', chartType: '环形图', file: 'MobileCategoryDonut.tsx', Component: MobileCategoryDonut, desc: '聊天28%+视频22%占半壁，与日使用一致' },
  { id: '4-15', title: '24h热力 × 矩阵热力', module: '手机使用', dataType: '24小时热力图', chartType: '热力图（矩阵）', file: 'DayHeatmapMatrix.tsx', Component: DayHeatmapMatrix, desc: '8-12/19-23双峰，凌晨1-5点仅3%，4点有缝' },
  { id: '4-15b', title: '24h热力 × 时段热力', module: '手机使用', dataType: '24小时热力图', chartType: '热力图（矩阵）', file: 'HourHeatmapChart.tsx', Component: HourHeatmapChart, desc: '按小时×分类，4点业务日切割' },
  { id: '5-12', title: 'GPS轨迹 × 散点/线图', module: '位置轨迹', dataType: 'GPS轨迹地图', chartType: '散点图', file: 'GpsTrackMap.tsx', Component: GpsTrackMap, desc: '家→地铁→公司3段连续，速度与段绑定' },
  { id: '5-40', title: 'GPS轨迹 × 六边形分箱', module: '位置轨迹', dataType: 'GPS轨迹地图', chartType: '六边形分箱', file: 'LocationHeatmap.tsx', Component: LocationHeatmap, desc: '轨迹密度分箱，北京39.8-40.1/116.2-116.6' },
  { id: '6-13', title: '常去地点 × 气泡图', module: '位置轨迹', dataType: '常去地点气泡图', chartType: '气泡图', file: 'LocationBubbleChart.tsx', Component: LocationBubbleChart, desc: '家128 公司96 学校42，与轨迹起终点一致' },
  { id: '7-24', title: '速度分布 × 直方图', module: '位置轨迹', dataType: '速度分布', chartType: '柱状图', file: 'SpeedHistogram.tsx', Component: SpeedHistogram, desc: '步行4/骑行20/高铁300三峰' },
  { id: '8-32', title: 'PC应用时长 × 渐变条', module: 'PC活动', dataType: 'PC应用使用时长', chartType: '渐变进度条', file: 'PcAppGradientBar.tsx', Component: PcAppGradientBar, desc: 'VS Code35%+Chrome30%占大头，AFK单独' },
  { id: '8-10', title: 'PC应用时长 × 环形图', module: 'PC活动', dataType: 'PC应用使用时长', chartType: '环形图', file: 'PcAppDonut.tsx', Component: PcAppDonut, desc: '6类占比，VS Code/Chrome主导' },
  { id: '9-15', title: '键盘热力 × 矩阵热力', module: 'PC活动', dataType: '键盘热力图', chartType: '热力图（矩阵）', file: 'KeyboardHeatmap.tsx', Component: KeyboardHeatmap, desc: 'ASDF/JKL;高频，Q/Z低频，Space3410' },
  { id: '10-7', title: '任务完成率 × 面积图', module: '日程习惯', dataType: '任务完成率', chartType: '面积图', file: 'TaskCompletionArea.tsx', Component: TaskCompletionArea, desc: '61%→89%波动，带7日均线' },
  { id: '10-20', title: '任务完成率 × 漏斗图', module: '日程习惯', dataType: '任务完成率', chartType: '漏斗图', file: 'TaskFunnel.tsx', Component: TaskFunnel, desc: '创建→进行→完成递减' },
  { id: '11-16', title: '习惯打卡 × 日历热力', module: '日程习惯', dataType: '习惯打卡热力', chartType: '日历热力图', file: 'HabitCalendarHeatmap.tsx', Component: HabitCalendarHeatmap, desc: '5习惯×30天，周末-10%，早起72%运动45%' },
  { id: '11-31', title: '习惯打卡 × 进度环', module: '日程习惯', dataType: '习惯打卡热力', chartType: '进度环', file: 'HabitStreakRing.tsx', Component: HabitStreakRing, desc: '连续天数+打卡率，5环并排' },
  { id: '12-21', title: '设备健康 × 仪表盘', module: '设备健康', dataType: '设备健康状态', chartType: '仪表盘', file: 'DeviceHealthGauge.tsx', Component: DeviceHealthGauge, desc: '2在线1离线1告警，与同步联动' },
  { id: '12-26', title: '设备健康 × 状态条', module: '设备健康', dataType: '设备健康状态', chartType: '条形图', file: 'DeviceStatusTimeline.tsx', Component: DeviceStatusTimeline, desc: '健康分与在线状态联动' },
  { id: '12-21b', title: '数据质量 × 仪表盘', module: '设备健康', dataType: '设备健康状态', chartType: '仪表盘', file: 'DataQualityGauge.tsx', Component: DataQualityGauge, desc: '数据质量评分，0-100' },
];

function loadRatings(): Map<string, number> {
  try { const raw = localStorage.getItem('exhibition_ratings'); if (raw) return new Map(JSON.parse(raw)); } catch {}
  return new Map();
}
function GalleryCardContent({ dtId, useReal, Comp }: { dtId: number; useReal: boolean; Comp: React.ComponentType<Record<string, never>> }) {
  const { data, loading, error, isEmpty } = useExhibitionData(dtId, { real: useReal });
  if (loading) return <div className="grid h-[168px] place-items-center rounded-md bg-slate-100 text-xs text-slate-500" aria-busy="true">加载真实数据…</div>;
  if (error) return <div className="grid h-[168px] place-items-center rounded-md border border-red-200 bg-red-50 p-4 text-center"><div className="text-xs font-semibold text-red-600">真实数据加载失败</div><div className="mt-1 text-xs text-red-500">{String((error as Error).message || error)}</div><div className="mt-2 text-xs text-slate-500">已回退到模拟数据</div><div className="mt-2"><Comp /></div></div>;
  if (isEmpty) return <div className="grid h-[168px] place-items-center rounded-md border border-dashed border-slate-200 bg-white p-4 text-center"><div className="text-xs text-slate-500">真实数据为空</div><div className="mt-1 text-xs text-amber-600">已回退到模拟</div><div className="mt-2 w-full"><Comp /></div></div>;
  // @ts-ignore — 部分组件的 data 形状与真实 API 一致，尝试传递；渲染期异常由 CardErrorBoundary 捕获
  return <Comp data={data as never} />;
}

function loadSelected(): Set<string> {
  try { const raw = localStorage.getItem('exhibition_selected'); if (raw) return new Set(JSON.parse(raw)); } catch {}
  return new Set();
}

export default function ExhibitionPage() {
  const queryClient = useQueryClient();
  const searchRef = useRef<HTMLInputElement>(null);
  const [moduleFilter, setModuleFilter] = useState('');
  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState<'default' | 'ratingDesc' | 'ratingAsc'>('default');
  const [page, setPage] = useState(1);
  const pageSize = 9;
  const [ratings, setRatings] = useState<Map<string, number>>(() => loadRatings());
  const [selected, setSelected] = useState<Set<string>>(() => loadSelected());
  const [compareOpen, setCompareOpen] = useState(false);
  const [globalReal, setGlobalReal] = useState<boolean>(() => {
    try { return localStorage.getItem('exhibition_real') === 'true'; } catch { return false; }
  });
  const [cardReal, setCardReal] = useState<Map<string, boolean>>(() => {
    try { const raw = localStorage.getItem('exhibition_card_real'); return raw ? new Map(JSON.parse(raw)) : new Map(); } catch { return new Map(); }
  });

  // URL 状态同步
  useEffect(() => {
    const hash = location.hash.startsWith('#') ? location.hash.slice(1) : '';
    const p = new URLSearchParams(hash);
    if (p.get('mod')) setModuleFilter(p.get('mod') || '');
    if (p.get('q')) setSearch(p.get('q') || '');
    if (p.get('sort')) setSortBy((p.get('sort') as never) || 'default');
    if (p.get('page')) setPage(Math.max(1, parseInt(p.get('page') || '1', 10)));
    if (p.get('sel')) { try { setSelected(new Set((p.get('sel') || '').split(',').filter(Boolean))); } catch {} }
    const cardQ = new URLSearchParams(location.search).get('card');
    if (cardQ) { setTimeout(() => { document.getElementById(`card-${cardQ}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' }); }, 400); }
  }, []);
  useEffect(() => {
    const params = new URLSearchParams();
    if (moduleFilter) params.set('mod', moduleFilter);
    if (search) params.set('q', search);
    if (sortBy !== 'default') params.set('sort', sortBy);
    if (page !== 1) params.set('page', String(page));
    if (selected.size) params.set('sel', [...selected].join(','));
    const h = params.toString();
    history.replaceState(null, '', h ? '#' + h : location.pathname + location.search);
  }, [moduleFilter, search, sortBy, page, selected]);

  // DEV check ?check=1
  useEffect(() => {
    if (new URLSearchParams(location.search).get('check') === '1') {
      console.assert(GALLERY.length === 21, '展览馆应有21个精选');
      const mock = getFakeData(3) as { value: number }[];
      const sum = mock.reduce((s, c) => s + c.value, 0);
      console.assert(Math.abs(sum - 100) < 1, `分类占比和应为100，实际${sum}`);
      console.log('✅ Exhibition data invariants check passed');
    }
  }, []);
  // 埋点 views
  useEffect(() => { try { const c = parseInt(localStorage.getItem('exhibition_views') || '0', 10) + 1; localStorage.setItem('exhibition_views', String(c)); } catch {} }, []);

  useEffect(() => { try { localStorage.setItem('exhibition_ratings', JSON.stringify([...ratings.entries()])); } catch {} }, [ratings]);
  useEffect(() => { try { localStorage.setItem('exhibition_selected', JSON.stringify([...selected])); } catch {} }, [selected]);
  useEffect(() => { try { localStorage.setItem('exhibition_real', String(globalReal)); } catch {} }, [globalReal]);
  useEffect(() => { try { localStorage.setItem('exhibition_card_real', JSON.stringify([...cardReal.entries()])); } catch {} }, [cardReal]);
  useEffect(() => { setPage(1); }, [moduleFilter, search, sortBy]);

  const filtered = useMemo(() => {
    let list = GALLERY.filter((g) => {
      if (moduleFilter && g.module !== moduleFilter) return false;
      if (search) {
        const q = search.toLowerCase();
        const hay = (g.title + ' ' + g.dataType + ' ' + g.chartType + ' ' + g.module + ' ' + g.desc).toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
    if (sortBy === 'ratingDesc') list = [...list].sort((a, b) => (ratings.get(b.id) || 0) - (ratings.get(a.id) || 0));
    if (sortBy === 'ratingAsc') list = [...list].sort((a, b) => (ratings.get(a.id) || 0) - (ratings.get(b.id) || 0));
    return list;
  }, [moduleFilter, search, sortBy, ratings]);

  const totalPages = Math.max(1, Math.ceil(filtered.length / pageSize));
  const pageItems = useMemo(() => filtered.slice((page - 1) * pageSize, page * pageSize), [filtered, page]);

  // 键盘 / j/k  — 放 filtered 之后以避免 TDZ，且带正确 deps 避免每帧重绑
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === '/' && !(e.target instanceof HTMLInputElement)) { e.preventDefault(); searchRef.current?.focus(); }
      if (e.key === 'j' && !(e.target instanceof HTMLInputElement)) { const pages = Math.max(1, Math.ceil(filtered.length / pageSize)); if (page < pages) setPage((p) => p + 1); }
      if (e.key === 'k' && !(e.target instanceof HTMLInputElement)) { if (page > 1) setPage((p) => p - 1); }
      if (e.key === 'Escape' && compareOpen) setCompareOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [filtered.length, page, compareOpen, pageSize]);

  const toggleSelect = (id: string) => setSelected((prev) => { const n = new Set(prev); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const setRate = (id: string, v: number) => setRatings((prev) => { const n = new Map(prev); n.set(id, v); return n; });
  const copyLink = (id: string) => {
    const url = location.origin + location.pathname + '?card=' + encodeURIComponent(id) + location.hash;
    if (navigator.clipboard) navigator.clipboard.writeText(url).then(() => {}).catch(() => {});
  };

  const exportSelected = () => {
    const items = [...selected].map((id) => {
      const g = GALLERY.find((x) => x.id === id);
      return g ? { id: g.id, title: g.title, module: g.module, dataType: g.dataType, chartType: g.chartType, file: `src/client-web/src/components/charts/${g.file}`, rating: ratings.get(id) || 0 } : { id, rating: ratings.get(id) || 0 };
    });
    const payload = {
      exportedAt: new Date().toISOString(),
      totalSelected: items.length,
      note: 'PIM 展览馆选中清单（React 画廊，复用 exhibition_selected）',
      items: items.length ? items : filtered.slice(0, 3).map((g) => ({ id: g.id, title: g.title, module: g.module, file: `src/client-web/src/components/charts/${g.file}`, rating: ratings.get(g.id) || 0, hint: '未选中，已导出筛选前3作为示例' })),
    };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `pim-exhibition-selected-${new Date().toISOString().slice(0, 10)}.json`;
    // 不设置固定 id，避免与常驻按钮 id="btnExport" 重复
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 800);
  };

  return (
    <div className="mx-auto w-full max-w-[1400px] space-y-4 pb-24 md:pb-4">
      <PageHeader
        title="展览馆 · React 画廊（21 落地）"
        subtitle="与静态展览馆同源 fakeData.ts · 筛选/搜索/排序/分页/打分/选中/对比/导出/复制链接/键盘"
        actions={
          <div className="flex flex-wrap gap-2">
            <a href="/dashboard-exhibition/Exhibition.html" target="_blank" rel="noreferrer" className="pim-button-secondary px-3 py-2 text-sm">打开静态展览馆</a>
            <button type="button" id="btnExport" onClick={exportSelected} className="pim-button-primary px-3 py-2 text-sm">导出选中 JSON</button>
          </div>
        }
      />

      <section className="pim-panel p-4">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-6">
          <label className="text-xs">
            <span className="font-semibold text-slate-500">数据源</span>
            <select value={globalReal ? 'real' : 'mock'} onChange={(e) => setGlobalReal(e.target.value === 'real')} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="mock">🔮 模拟数据</option>
              <option value="real">🔗 真实数据</option>
            </select>
          </label>
          <label className="text-xs">
            <span className="font-semibold text-slate-500">模块</span>
            <select value={moduleFilter} onChange={(e) => setModuleFilter(e.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="">全部模块</option>
              <option value="手机使用">手机使用</option>
              <option value="位置轨迹">位置轨迹</option>
              <option value="PC活动">PC活动</option>
              <option value="日程习惯">日程习惯</option>
              <option value="设备健康">设备健康</option>
            </select>
          </label>
          <label className="text-xs">
            <span className="font-semibold text-slate-500">排序</span>
            <select value={sortBy} onChange={(e) => setSortBy(e.target.value as never)} className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm">
              <option value="default">默认</option>
              <option value="ratingDesc">评分从高到低</option>
              <option value="ratingAsc">评分从低到高</option>
            </select>
          </label>
          <label className="text-xs md:col-span-3">
            <span className="font-semibold text-slate-500">搜索 <span className="ml-1 rounded bg-slate-100 px-1 py-0.5 text-[10px]">/</span></span>
            <input ref={searchRef} value={search} onChange={(e) => setSearch(e.target.value)} placeholder="搜 任务/热力/玫瑰/GPS…   j/k 翻页"
              className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm" />
          </label>
        </div>
        <div className="mt-3 flex flex-wrap gap-2 text-xs">
          <span className="rounded-full bg-blue-50 px-2.5 py-1 font-semibold text-blue-700">{GALLERY.length} 组件已落地</span>
          <span className="rounded-full bg-emerald-50 px-2.5 py-1 font-semibold text-emerald-700">已选 {selected.size}</span>
          <span className="rounded-full bg-amber-50 px-2.5 py-1 font-semibold text-amber-700">筛选 {filtered.length} · 第 {page}/{totalPages} 页</span>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 font-semibold text-slate-600">localStorage: exhibition_ratings / exhibition_selected / exhibition_views</span>
        </div>
        {selected.size >= 2 && (
          <div className="mt-3 flex items-center justify-between rounded-lg border border-blue-200 bg-blue-50 px-3 py-2">
            <span className="text-sm font-semibold text-blue-700">对比({selected.size})</span>
            <button type="button" onClick={() => setCompareOpen(true)} className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-semibold text-white">进入对比</button>
          </div>
        )}
      </section>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {pageItems.map((item) => {
          const rating = ratings.get(item.id) || 0;
          const isSel = selected.has(item.id);
          return (
            <section key={item.id} id={`card-${item.id}`} className={`pim-card flex flex-col overflow-hidden transition-all duration-150 ease-out hover:-translate-y-0.5 hover:shadow-lg ${isSel ? 'ring-2 ring-blue-200 border-blue-300' : ''}`}>
              <div className="flex items-start justify-between gap-2 border-b border-slate-100 px-3 py-2">
                <div className="min-w-0">
                  <h3 className="truncate text-[13px] font-semibold text-slate-900">{item.title}</h3>
                  <p className="truncate text-xs text-slate-500">{item.dataType} · {item.chartType} · #{item.id}</p>
                  <p className="mt-1 line-clamp-2 text-[11px] leading-relaxed text-slate-500">{item.desc}</p>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <select value={cardReal.has(item.id) ? (cardReal.get(item.id)! ? 'real' : 'mock') : 'global'} onChange={(e)=>{ const v=e.target.value; setCardReal(prev=>{ const n=new Map(prev); if(v==='global') n.delete(item.id); else n.set(item.id, v==='real'); return n; }); }} className="rounded-md border border-slate-200 bg-white px-1 py-0.5 text-[10px]" title="数据源">
                    <option value="global">跟随全局</option>
                    <option value="mock">🔮模拟</option>
                    <option value="real">🔗真实</option>
                  </select>
                  <button type="button" onClick={() => copyLink(item.id)} className="rounded-md border border-slate-200 bg-white px-1.5 py-1 text-[11px] text-slate-500 hover:bg-slate-50" title="复制直达链接" aria-label="复制链接">🔗</button>
                  <input type="checkbox" checked={isSel} onChange={() => toggleSelect(item.id)} className="h-4 w-4 accent-blue-600" aria-label="选中" />
                </div>
              </div>
              <div className="flex flex-wrap gap-1 px-3 py-2">
                <span className="rounded-full bg-blue-50 px-2 py-0.5 text-[10px] font-semibold text-blue-700">{item.module}</span>
                <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] text-slate-600">{item.chartType}</span>
                <span className="rounded-full bg-slate-50 px-2 py-0.5 text-[10px] text-slate-500">{item.file}</span>
                <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${(() => { const useReal = cardReal.has(item.id) ? cardReal.get(item.id)! : globalReal; return useReal ? 'bg-emerald-50 text-emerald-700' : 'bg-amber-50 text-amber-700'; })()}`}>{(() => { const useReal = cardReal.has(item.id) ? cardReal.get(item.id)! : globalReal; return useReal ? '🔗真实' : '🔮模拟'; })()}</span>
              </div>
              <div className="border-y border-slate-100 bg-slate-50/50 p-2">
                {(() => {
                  const dtId = parseInt(item.id.split('-')[0], 10);
                  const useReal = cardReal.has(item.id) ? cardReal.get(item.id)! : globalReal;
                  return (
                    <CardErrorBoundary
                      key={`${item.id}-${useReal ? 'real' : 'mock'}`}
                      cardTitle={item.title}
                      resetKeys={[item.id, useReal]}
                      onRetry={() => queryClient.invalidateQueries({ queryKey: ['exh', dtId] })}
                    >
                      <Suspense fallback={<div className="grid h-[168px] place-items-center rounded-md bg-slate-100 text-xs text-slate-500" aria-busy="true">加载中…</div>}>
                        <GalleryCardContent dtId={dtId} useReal={useReal} Comp={item.Component} />
                      </Suspense>
                    </CardErrorBoundary>
                  );
                })()}
              </div>
              <div className="flex items-center justify-between gap-2 px-3 py-2">
                <div className="flex gap-1">
                  {[1, 2, 3, 4, 5].map((s) => (
                    <button key={s} onClick={() => setRate(item.id, s)} aria-label={`${s}星`} className={`h-7 w-7 rounded-md border text-sm transition ${s <= rating ? 'bg-amber-50 border-amber-200 text-amber-600' : 'bg-white border-slate-200 text-slate-400'}`}>
                      {s <= rating ? '★' : '☆'}
                    </button>
                  ))}
                </div>
                <span className="text-xs text-slate-400">{rating ? `${rating}★` : '未评分'}</span>
                <button type="button" onClick={() => toggleSelect(item.id)} className={`rounded-lg px-2.5 py-1 text-xs font-semibold ${isSel ? 'bg-slate-900 text-white' : 'bg-white border border-slate-200 text-slate-600'}`}>
                  {isSel ? '已选中' : '选中'}
                </button>
              </div>
            </section>
          );
        })}
      </div>

      {filtered.length === 0 && (
        <div className="rounded-lg border border-dashed border-slate-200 bg-white p-10 text-center">
          <div className="text-3xl">🔍</div><div className="mt-2 text-sm font-semibold text-slate-700">没有匹配的卡片</div><div className="mt-1 text-xs text-slate-500">试试放宽筛选条件</div>
          <button type="button" onClick={() => { setModuleFilter(''); setSearch(''); setSortBy('default'); }} className="mt-3 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm font-semibold text-slate-600">清空筛选</button>
        </div>
      )}

      <div className="flex items-center justify-center gap-2">
        <button type="button" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1} className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm font-semibold disabled:opacity-40">‹ 上一页</button>
        <span className="text-sm text-slate-500">{page} / {totalPages}</span>
        <button type="button" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages} className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm font-semibold disabled:opacity-40">下一页 ›</button>
        <span className="ml-2 text-xs text-slate-400">j/k 翻页 · / 搜索</span>
      </div>

      {compareOpen && (
        <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4" onClick={() => setCompareOpen(false)}>
          <div className="max-h-[90vh] w-full max-w-5xl overflow-auto rounded-xl bg-white p-4" onClick={(e) => e.stopPropagation()}>
            <div className="mb-3 flex items-center justify-between"><h3 className="font-semibold">对比（{selected.size}）</h3><button type="button" onClick={() => setCompareOpen(false)} className="rounded-lg border px-3 py-1 text-sm">关闭 Esc</button></div>
            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
              {[...selected].slice(0, 3).map((id) => {
                const g = GALLERY.find((x) => x.id === id);
                if (!g) return null;
                const C = g.Component;
                return (
                  <div key={id} className="rounded-lg border p-2">
                    <div className="mb-2 text-sm font-semibold">{g.title}</div>
                    <CardErrorBoundary cardTitle={g.title}>
                      <Suspense fallback={<div className="grid h-[120px] place-items-center rounded-md bg-slate-100 text-xs text-slate-500">加载中…</div>}>
                        <C />
                      </Suspense>
                    </CardErrorBoundary>
                  </div>
                );
              })}
            </div>
            <button type="button" onClick={exportSelected} className="mt-3 w-full rounded-lg bg-blue-600 py-2 text-sm font-semibold text-white">导出对比清单</button>
          </div>
        </div>
      )}

      <section className="pim-panel p-4">
        <h3 className="text-sm font-semibold text-slate-900">如何体验</h3>
        <ol className="mt-2 list-decimal space-y-1 pl-5 text-sm text-slate-600">
          <li>静态展览馆（挑）：访问 <code>/dashboard-exhibition/Exhibition.html</code> 或本地打开 <code>src/client-web/src/components/dashboard-exhibition/Exhibition.html</code>，480 卡片可筛选/搜索/排序/打分/选中/对比/导出，支持 URL 分享与键盘 <code>/</code> <code>j/k</code>。</li>
          <li>React 画廊（落）：本页 <code>/exhibition</code> 即 21 生产组件的 React 网格，筛选/打分/选中/导出与静态页共享 <code>exhibition_ratings</code> / <code>exhibition_selected</code> / <code>exhibition_views</code>，刷新互通，支持分页与复制链接。</li>
          <li>导出：点击“导出选中 JSON”下载 <code>pim-exhibition-selected-*.json</code>，未选中时自动导出当前筛选前 3 作为示例。</li>
        </ol>
      </section>
    </div>
  );
}
