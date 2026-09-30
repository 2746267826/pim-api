import EChartBox from '../charts/EChartBox';
import { buildAnalysisBlocksOption } from '../charts/pcHeatmapOptions';
import type { PcActivityAnalysisBlock, PcActivityAnalysisResponse } from '../../types';

interface Props {
  analysis: PcActivityAnalysisResponse | undefined;
  selectedStart: string | null;
  onSelectBlock: (block: PcActivityAnalysisBlock) => void;
}

function formatMinutes(seconds: number) {
  return Math.round(seconds / 60).toLocaleString('zh-CN');
}

function formatTime(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
}

interface ShareItem {
  key: string;
  label: string;
  color: string | null;
  durationSeconds: number;
}

/**
 * 块内占比条（REQ-2）。
 *
 * 分母取 `max(块时长, 本组时长合计)`：后端修复后各项合计本就 ≤ 块时长，
 * 此时占比即 `durationSeconds / 块时长`；若遇到脏数据（合计超过块时长），
 * 分母退化为合计值，保证渲染出的占比合计恒 ≤ 100%。
 */
function sharePercent(durationSeconds: number, blockSeconds: number, groupTotalSeconds: number): number {
  const denominator = Math.max(blockSeconds, groupTotalSeconds);
  if (!(denominator > 0) || !(durationSeconds > 0)) return 0;
  return Math.min(Math.round((durationSeconds / denominator) * 1000) / 10, 100);
}

function ShareList({
  group,
  title,
  blockSeconds,
  items,
}: {
  group: 'categories' | 'apps';
  title: string;
  blockSeconds: number;
  items: ShareItem[];
}) {
  if (items.length === 0) return null;
  const totalSeconds = items.reduce((acc, item) => acc + Math.max(item.durationSeconds, 0), 0);

  return (
    <div className="min-w-0 space-y-1.5" data-share-section={group}>
      <div className="text-[11px] font-medium text-slate-500">{title}</div>
      {items.map(item => {
        const percent = sharePercent(item.durationSeconds, blockSeconds, totalSeconds);
        return (
          <div key={item.key} className="space-y-0.5">
            <div className="flex min-w-0 items-center justify-between gap-2 text-xs text-slate-600">
              <span className="min-w-0 break-words">{item.label}</span>
              <span className="shrink-0">
                {formatMinutes(item.durationSeconds)} 分钟 · {percent}%
              </span>
            </div>
            <div className="h-1.5 w-full overflow-hidden rounded-full bg-slate-200">
              <div
                data-share-bar
                data-share-group={group}
                data-share-percent={percent}
                className="h-full rounded-full"
                style={{ width: `${percent}%`, backgroundColor: item.color || '#0ea8a0' }}
              />
            </div>
          </div>
        );
      })}
    </div>
  );
}

export default function ActivityAnalysisHeatmap({ analysis, selectedStart, onSelectBlock }: Props) {
  const blocks = analysis?.blocks ?? [];
  const selected = blocks.find(block => block.start === selectedStart)
    ?? blocks.find(block => block.activeDurationSeconds > 0)
    ?? blocks[0];

  const blockSeconds = selected
    ? Math.max((new Date(selected.end).getTime() - new Date(selected.start).getTime()) / 1000, 0)
    : 0;

  if (!analysis || blocks.length === 0) {
    return (
      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
        暂无活动分析数据。
      </div>
    );
  }

  const handleClick = (params: unknown) => {
    const p = params as { data?: { blockIndex?: number } | number[] } | undefined;
    const d = p?.data;
    const idx = Array.isArray(d) ? d[0] : d?.blockIndex;
    if (typeof idx === 'number' && blocks[idx]) {
      onSelectBlock(blocks[idx]);
    }
  };

  return (
    <div className="space-y-3">
      <EChartBox
        option={buildAnalysisBlocksOption(blocks, selectedStart)}
        height={116}
        ariaLabel="活动分析热力图"
        onEvents={{ click: handleClick }}
      />

      <div className="flex flex-wrap gap-3 text-xs text-slate-500">
        <span>活动分析</span>
        <span>颜色越深表示活动越密集</span>
        <span>琥珀色边框表示有待分类记录</span>
      </div>

      {selected && (
        <section className="rounded-lg border border-slate-200 bg-slate-50 p-3">
          <div className="text-sm font-semibold text-slate-950">
            {formatTime(selected.start)} - {formatTime(selected.end)}
          </div>
          <p className="mt-1 text-xs text-slate-600">
            活跃 {formatMinutes(selected.activeDurationSeconds)} 分钟 | {selected.contextSwitchCount.toLocaleString('zh-CN')} 次上下文切换 | {selected.pendingClassificationCount.toLocaleString('zh-CN')} 条待分类
          </p>

          <div className="mt-2 grid gap-3 md:grid-cols-2">
            <ShareList
              group="categories"
              title="分类占比"
              blockSeconds={blockSeconds}
              items={selected.categories.slice(0, 4).map(item => ({
                key: item.categoryName,
                label: item.categoryName,
                color: item.color,
                durationSeconds: item.durationSeconds,
              }))}
            />
            <ShareList
              group="apps"
              title="应用占比"
              blockSeconds={blockSeconds}
              items={selected.apps.slice(0, 4).map(item => ({
                key: item.appName,
                label: item.appName,
                color: null,
                durationSeconds: item.durationSeconds,
              }))}
            />
          </div>
          <p className="mt-2 text-[11px] text-slate-400">占比 = 该项时长 ÷ 块时长（{Math.round(blockSeconds / 60)} 分钟），合计不超过 100%。</p>
        </section>
      )}
    </div>
  );
}
