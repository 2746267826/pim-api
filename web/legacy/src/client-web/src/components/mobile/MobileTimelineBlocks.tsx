import type {
  MobileSessionEvent,
  MobileTimelineBlock,
  MobileTimelineBlockSession,
} from '../../api/mobile';
import { formatDuration, formatShortTime, sourceLabel } from './mobileFormatting';
import EChartBox from '../charts/EChartBox';
import { buildTimelineStripOption } from '../charts/mobileChartOptions';

export interface MobileTimelineBlocksProps {
  blocks: MobileTimelineBlock[];
  sessionsByBlock: Record<string, MobileTimelineBlockSession[]>;
  eventsBySession: Record<string, MobileSessionEvent[]>;
  expandedBlockId?: string | null;
  expandedSessionId?: string | null;
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  isLoading?: boolean;
  isLoadingSessions?: boolean;
  isLoadingEvents?: boolean;
  onToggleBlock: (blockId: string) => void;
  onToggleSession: (sessionId: string) => void;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
}

const pageSizeOptions = [10, 20, 50, 100];

export default function MobileTimelineBlocks({
  blocks,
  sessionsByBlock,
  eventsBySession,
  expandedBlockId = null,
  expandedSessionId = null,
  page,
  pageSize,
  totalCount,
  totalPages,
  isLoading = false,
  isLoadingSessions = false,
  isLoadingEvents = false,
  onToggleBlock,
  onToggleSession,
  onPageChange,
  onPageSizeChange,
}: MobileTimelineBlocksProps) {
  const safeTotalPages = Math.max(1, totalPages);
  const canGoPrevious = page > 1;
  const canGoNext = page < safeTotalPages;
  const visiblePages = Array.from(
    { length: Math.min(5, safeTotalPages) },
    (_, index) => Math.max(1, Math.min(page - 2, safeTotalPages - 4)) + index,
  ).filter(value => value <= safeTotalPages);

  return (
    <section className="rounded-md border border-slate-200 bg-white p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-950">使用时间线</h2>
          <p className="mt-1 text-xs text-slate-500">按连续行为块展示，避免一次性塞入原始事件</p>
        </div>
        <div className="flex flex-wrap items-center gap-3 text-xs text-slate-500">
          <span>{totalCount} 个时间块</span>
          <label className="flex items-center gap-2">
            <span>每页</span>
            <select
              aria-label="每页数量"
              value={pageSize}
              onChange={event => onPageSizeChange(Number(event.target.value))}
              className="h-8 rounded border border-slate-200 bg-white px-2 text-xs text-slate-700"
            >
              {pageSizeOptions.map(option => (
                <option key={option} value={option}>{option}</option>
              ))}
            </select>
          </label>
        </div>
      </div>
      <div className="mt-4">
        <EChartBox
          option={buildTimelineStripOption(blocks)}
          height={110}
          ariaLabel="停留与移动时间线"
          onEvents={{
            click: params => {
              const p = (Array.isArray(params) ? params[0] : params) as { data?: { blockId?: string } } | undefined;
              if (p?.data?.blockId) onToggleBlock(p.data.blockId);
            },
          }}
        />
      </div>
      <div className="mt-4 space-y-3">
        {blocks.map(block => {
          const expanded = expandedBlockId === block.id;
          const sessions = sessionsByBlock[block.id] ?? [];
          return (
            <div key={block.id} className="rounded-md border border-slate-200">
              <button
                type="button"
                onClick={() => onToggleBlock(block.id)}
                className="grid w-full grid-cols-[minmax(120px,0.35fr)_1fr_auto] items-center gap-3 px-3 py-3 text-left"
              >
                <span className="text-sm font-medium text-slate-900">{formatShortTime(block.startUtc)} - {formatShortTime(block.endUtc)}</span>
                <span className="min-w-0">
                  <span className="block truncate text-sm text-slate-700">{block.lifeCategory}</span>
                  <span className="block truncate text-xs text-slate-500">{block.topApps.map(app => app.displayName).join(' / ')}</span>
                </span>
                <span className="text-sm font-semibold text-slate-900">{formatDuration(block.foregroundSeconds)}</span>
              </button>
              {expanded && (
                <div className="border-t border-slate-100 px-3 py-3">
                  {sessions.length === 0 && (
                    <p className="text-xs text-slate-500">
                      {isLoadingSessions ? '正在加载会话' : '暂无会话'}
                    </p>
                  )}
                  <div className="space-y-2">
                    {sessions.map(session => {
                      const sessionExpanded = expandedSessionId === session.id;
                      return (
                        <div key={session.id} className="rounded border border-slate-100 bg-slate-50">
                          <button
                            type="button"
                            onClick={() => onToggleSession(session.id)}
                            className="grid w-full grid-cols-[1fr_auto] gap-3 px-3 py-2 text-left text-sm"
                          >
                            <span className="min-w-0">
                              <span className="block truncate font-medium text-slate-800">{session.displayName}</span>
                              <span className="block truncate text-xs text-slate-500">{session.packageName} · {sourceLabel(session.source)}</span>
                            </span>
                            <span className="text-slate-600">{formatDuration(session.durationSeconds)}</span>
                          </button>
                          {sessionExpanded && (
                            <div className="border-t border-slate-200 px-3 py-2">
                              <p className="mb-2 text-xs font-medium text-slate-500">原始事件</p>
                              {isLoadingEvents && <p className="text-xs text-slate-500">正在加载原始事件</p>}
                              {(eventsBySession[session.id] ?? []).map(event => (
                                <div key={event.id} className="grid grid-cols-[120px_1fr] gap-2 py-1 text-xs text-slate-600">
                                  <span>{formatShortTime(event.eventTimeUtc)}</span>
                                  <span className="truncate">{event.eventType} · {event.className ?? event.packageName}</span>
                                </div>
                              ))}
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>
          );
        })}
      </div>
      {isLoading && <p className="mt-3 text-xs text-slate-500">正在加载时间块</p>}
      {!isLoading && blocks.length === 0 && <p className="mt-3 text-xs text-slate-500">暂无时间块数据</p>}
      <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
        <span className="text-xs text-slate-500">第 {Math.min(page, safeTotalPages)} / {safeTotalPages} 页</span>
        <div className="flex items-center gap-1">
          <button
            type="button"
            aria-label="上一页"
            onClick={() => onPageChange(page - 1)}
            disabled={!canGoPrevious || isLoading}
            className="h-8 w-8 rounded border border-slate-200 text-sm text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
          >
            ‹
          </button>
          {visiblePages.map(item => (
            <button
              key={item}
              type="button"
              onClick={() => onPageChange(item)}
              disabled={isLoading}
              className={`h-8 min-w-8 rounded border px-2 text-xs font-medium ${
                item === page
                  ? 'border-slate-950 bg-slate-950 text-white'
                  : 'border-slate-200 text-slate-600 hover:bg-slate-50'
              }`}
            >
              {item}
            </button>
          ))}
          <button
            type="button"
            aria-label="下一页"
            onClick={() => onPageChange(page + 1)}
            disabled={!canGoNext || isLoading}
            className="h-8 w-8 rounded border border-slate-200 text-sm text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
          >
            ›
          </button>
        </div>
      </div>
    </section>
  );
}
