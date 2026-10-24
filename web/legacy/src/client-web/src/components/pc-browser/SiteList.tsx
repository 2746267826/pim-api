import type { PcBrowserHostSummary } from '../../api/pcBrowserSite';
import { formatBrowserCount, formatDurationMs, hostAvatarColor, hostInitial } from './pcBrowserFormatting';

interface Props {
  hosts: PcBrowserHostSummary[];
  isLoading: boolean;
}

/**
 * 站点列表（对齐 time-tracker-4-browser 记录页的行结构）：
 * 序号 + 字母头像 + 别名/host + 右对齐专注时长 + 相对最大值的蓝紫渐变时长条 + 访问次数。
 */
export default function SiteList({ hosts, isLoading }: Props) {
  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: 5 }, (_, i) => (
          <div key={i} className="flex items-center gap-3">
            <div className="h-9 w-9 animate-pulse rounded-full bg-slate-100" />
            <div className="flex-1 space-y-2">
              <div className="h-3 w-1/3 animate-pulse rounded bg-slate-100" />
              <div className="h-2 w-2/3 animate-pulse rounded bg-slate-100" />
            </div>
          </div>
        ))}
      </div>
    );
  }

  if (!hosts.length) {
    return (
      <div className="py-10 text-center text-sm text-slate-400">
        暂无浏览数据
      </div>
    );
  }

  const maxFocusMs = Math.max(...hosts.map(h => h.focusMs), 0);

  return (
    <ol className="space-y-3">
      {hosts.map((site, index) => {
        const ratio = maxFocusMs > 0 ? site.focusMs / maxFocusMs : 0;
        const barWidth = site.focusMs > 0 ? Math.max(ratio * 100, 2) : 0;
        return (
          <li key={site.host} className="flex items-start gap-3">
            <span className="w-5 shrink-0 pt-1.5 text-right text-xs tabular-nums text-slate-400">
              {index + 1}
            </span>
            <span
              aria-hidden="true"
              className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full text-sm font-semibold text-white"
              style={{ backgroundColor: hostAvatarColor(site.host) }}
            >
              {hostInitial(site.host)}
            </span>
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-3">
                <p className="min-w-0 truncate text-sm font-medium text-slate-900">
                  {site.alias || site.host}
                </p>
                <span className="shrink-0 text-xs font-semibold tabular-nums text-slate-700">
                  {formatDurationMs(site.focusMs)}
                </span>
              </div>
              {site.alias && (
                <p className="mt-0.5 truncate text-[11px] text-slate-400">{site.host}</p>
              )}
              <div className="mt-1.5 flex items-center gap-2">
                <div className="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-slate-100">
                  <div
                    className="h-full rounded-full bg-gradient-to-r from-blue-500 to-violet-500"
                    style={{ width: `${barWidth}%` }}
                  />
                </div>
                <span className="shrink-0 text-[11px] tabular-nums text-slate-400">
                  {formatBrowserCount(site.visitCount)} 次
                </span>
              </div>
            </div>
          </li>
        );
      })}
    </ol>
  );
}
