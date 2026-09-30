import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';

import EmptyState from '../../ui/EmptyState';
import MetricCard from '../../ui/MetricCard';
import StatusBadge from '../../ui/StatusBadge';
import type { PcActivityTodayData, TodaySection } from '../../types';
import { getPcCategoryDistribution, getPcFocusBlocks } from '../../api/pcTracker';
import PcActiveMinutesNote from '../pc-tracker/PcActiveMinutesNote';
import { formatPcDate, getPcBusinessDate } from '../../utils/pcBusinessDay';
import EChartBox from '../charts/EChartBox';
import {
  buildCategoryDonutOption,
  buildFocusSummary,
  buildTodayActivityAreaOption,
} from '../charts/pcTodayOptions';

function formatNumber(value: number | undefined) {
  return (value ?? 0).toLocaleString('zh-CN');
}

/** 内部信息块：紧凑容器（2026-09-19 空间优化：p-4 → p-3，标题与说明同行）。 */
function Block({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
      <div className="mb-2 flex items-baseline justify-between gap-2">
        <p className="text-sm font-medium text-slate-800">{title}</p>
        {hint && <p className="truncate text-[11px] text-slate-400">{hint}</p>}
      </div>
      {children}
    </div>
  );
}

export default function TodayPcOverview({ section }: { section: TodaySection<PcActivityTodayData> }) {
  const summary = section.data.summary;
  const metrics = summary.metrics;
  const keystats = summary.keystats;
  // 今日业务日期字符串（yyyy-MM-dd，与后端 date 参数一致）：避免 query key 固定 + 无 date 参数导致的跨日缓存陈旧。
  const dateStr = formatPcDate(getPcBusinessDate());

  const categoryQuery = useQuery({
    queryKey: ['pc-category-distribution', dateStr],
    queryFn: () => getPcCategoryDistribution({ date: dateStr }),
    enabled: section.status !== 'empty',
  });
  const focusQuery = useQuery({
    queryKey: ['pc-focus-blocks', dateStr],
    queryFn: () => getPcFocusBlocks({ date: dateStr }),
    enabled: section.status !== 'empty',
  });

  const categoryItems = categoryQuery.data?.items ?? [];
  const focusSummary = focusQuery.data ? buildFocusSummary(focusQuery.data.items) : null;

  return (
    <section className="pim-panel min-w-0 p-3">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div>
          <h2 className="font-semibold text-slate-900">PC 记录概览</h2>
          <p className="mt-0.5 text-xs text-slate-500">输入、应用活跃度与 24 小时热力分布</p>
        </div>
        <StatusBadge tone={section.status === 'empty' ? 'neutral' : 'activity'}>
          {section.status === 'empty' ? '暂无数据' : '今日'}
        </StatusBadge>
      </div>

      {section.status === 'empty' ? (
        <EmptyState title="暂无 PC 记录" description="守护程序同步后会显示今天的使用概览。" />
      ) : (
        <div className="space-y-3">
          {/* 指标 4 连排：宽屏一行放下（原 2×2 每张卡大而空） */}
          <div className="grid grid-cols-2 gap-2.5 md:grid-cols-4">
            <MetricCard
              dense
              label="记录时长"
              value={metrics?.totalRecordedDuration ?? '-'}
              helper={metrics?.mostFocusedApp ? `最专注：${metrics.mostFocusedApp}` : '等待同步'}
              tone="primary"
            />
            <MetricCard
              dense
              label="活跃输入"
              value={metrics?.activeInputDuration ?? '-'}
              helper={`会话 ${metrics?.sessionCount ?? 0} 次`}
              tone="activity"
            />
            <MetricCard
              dense
              label="按键"
              value={formatNumber(keystats?.keyPresses ?? metrics?.totalKeyPresses)}
              helper={`峰值 ${keystats?.peakKps ?? 0} KPS`}
              tone="neutral"
            />
            <MetricCard
              dense
              label="点击"
              value={formatNumber(keystats?.totalClicks ?? metrics?.totalClicks)}
              helper={`应用 ${metrics?.activeAppCount ?? 0} 个`}
              tone="warning"
            />
          </div>

          {/* 热力图 | 分类分布：并排两列，减少纵向堆叠 */}
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <Block title="24 小时热力图" hint="04:00 起算，越深越集中">
              <EChartBox
                option={buildTodayActivityAreaOption(summary.heatmap)}
                height={150}
                ariaLabel="今日 24 小时 PC 活跃面积图"
              />
              {/* REQ-8：与 WorkbenchPage 的「PC 活跃」同口径说明 */}
              <PcActiveMinutesNote className="mt-2" />
            </Block>
            <Block title="分类分布">
              {categoryItems.length > 0 ? (
                <EChartBox
                  option={buildCategoryDonutOption(categoryItems)}
                  height={150}
                  ariaLabel="今日 PC 分类分布环图"
                />
              ) : (
                <p className="py-12 text-center text-xs text-slate-400">
                  {categoryQuery.isLoading ? '加载中…' : '暂无数据'}
                </p>
              )}
            </Block>
          </div>

          {/* 专注段 | 主要应用：并排两列 */}
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            <Block title="专注段">
              {focusSummary && focusSummary.count > 0 ? (
                <div className="grid grid-cols-3 gap-2">
                  <MetricCard dense label="专注段" value={`${focusSummary.count} 段`} helper="今日专注块数" tone="primary" />
                  <MetricCard dense label="最长" value={`${focusSummary.longestMinutes} 分钟`} helper="单段最长" tone="activity" />
                  <MetricCard dense label="合计" value={`${focusSummary.totalMinutes} 分钟`} helper="专注总时长" tone="warning" />
                </div>
              ) : (
                <p className="py-12 text-center text-xs text-slate-400">
                  {focusQuery.isLoading ? '加载中…' : '暂无数据'}
                </p>
              )}
            </Block>
            <Block title="主要应用">
              {summary.appRanking?.length ? (
                <div className="space-y-1">
                  {summary.appRanking.slice(0, 4).map(app => (
                    <div
                      key={app.appName}
                      className="flex items-center justify-between rounded-lg bg-slate-100/80 px-2.5 py-1.5"
                    >
                      <span className="min-w-0 truncate text-xs text-slate-700">{app.displayName || app.appName}</span>
                      {/* #301：占总量占比，显示 1 位小数（合计 ≈100%） */}
                      <span className="text-xs font-medium text-slate-500">{(app.share * 100).toFixed(1)}%</span>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="py-12 text-center text-xs text-slate-400">暂无数据</p>
              )}
            </Block>
          </div>
        </div>
      )}
    </section>
  );
}
