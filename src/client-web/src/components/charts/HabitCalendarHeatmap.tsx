import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { getHabits } from '../../api/calendar';
import { habitCadenceLabel, isArchivedHabit } from '../schedule/habitCadence';

/**
 * 习惯卡（今日页 / 展览馆共用）。
 *
 * ⚠️ 2026-09-19 修复：此组件此前用「哈希伪随机」生成 30 天打卡数据画热力图
 * （原注释自述「打卡明细用轻量随机模拟+真实标题」），没有习惯时也全绿、
 * 外层还标着「真实」——属于假数据展示，已整体移除。
 * 现在：无习惯 → 空态引导；有习惯 → 真实习惯列表。
 * 真实「打卡热力图」需服务端先提供打卡记录查询端点（当前 CalendarModule
 * 只有创建打卡 POST /habits/{id}/occurrences，没有查询端点）后再接入。
 */
export default function HabitCalendarHeatmap() {
  const { data: allHabits = [], isLoading } = useQuery({
    queryKey: ['exhibition-habits'],
    queryFn: getHabits,
  });

  // #351：归档习惯不再是"活跃习惯"，与日历图层/今日区块口径一致 ——
  // 此处若不过滤，归档后"今日"页的习惯卡仍会列出该习惯，与官方
  // calendar.habits 区块（已过滤）自相矛盾。
  const habits = allHabits.filter(habit => !isArchivedHabit(habit));

  return (
    <section className="pim-card min-w-0 p-4">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-slate-900">习惯</h3>
        <span className="rounded-full bg-slate-100 px-2 py-1 text-xs text-slate-600">{habits.length} 个习惯</span>
      </div>
      <p className="mt-1 text-xs text-slate-500">习惯与打卡追踪</p>
      <div className="mt-3">
        {isLoading ? (
          <div
            style={{ height: 160 }}
            className="animate-pulse rounded-md bg-slate-100"
            aria-busy="true"
            aria-label="加载中"
          />
        ) : habits.length === 0 ? (
          <div className="grid h-[160px] place-items-center rounded-md border border-dashed border-slate-200 bg-white text-center">
            <div>
              <div className="text-2xl">🌱</div>
              <div className="mt-1 text-xs text-slate-500">还没有创建习惯</div>
              <Link to="/habits" className="mt-1 inline-block text-xs font-medium text-blue-600 hover:underline">
                去习惯页创建 →
              </Link>
            </div>
          </div>
        ) : (
          <ul className="max-h-[160px] space-y-2 overflow-y-auto pr-1">
            {habits.map(habit => (
              <li
                key={habit.id}
                className="flex items-center justify-between gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2"
              >
                <span className="truncate text-sm text-slate-800">{habit.title}</span>
                <span className="shrink-0 text-[11px] text-slate-400">{habitCadenceLabel(habit.cadence)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
