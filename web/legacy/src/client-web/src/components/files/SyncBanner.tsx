import { AlertCircle, CheckCircle2, Loader2, RefreshCw } from 'lucide-react';
import type { OneDriveSyncStatus } from '../../types';

export interface SyncBannerProps {
  /** 已连接 provider 的同步状态；未绑定或未加载时为 null。 */
  status: OneDriveSyncStatus | null;
  /** 手动同步请求进行中（已入队但还没拿到状态）。 */
  starting?: boolean;
  /** 点击「立即同步」。 */
  onSync: () => void;
  /** 同步中断（例如 503）时的可读原因；由调用方提供。 */
  error?: string | null;
}

/**
 * 同步状态横幅（REQ-25 / AC-25.1 / AC-25.2）。
 *
 * 设计要点：
 * - 「立即同步」必须**立即反馈已开始**，不阻塞页面（阻塞式同步已在后端改为入队）；
 * - 同步进行中与失败都要有可读文案：成功显示进度量、失败显示原因（AC-25.2）；
 * - 失败恢复后横幅自动回到正常态（由 status 驱动，无粘滞状态）。
 *
 * #353：`syncedItemCount` 的语义是「**最近一次同步运行**中应用的项目数」，
 * 每次运行覆盖写入；日常「无增量变更」的同步会把它写成 0。而「已同步 X 项」
 * 给人的是累计/总量直觉，索引其实完整也会被误读成同步失败。
 * 因此空闲态改为**组合口径**：上次同步时间 + 本次变更数 + 已索引总量，
 * 参考格式「上次同步 12:40 · 本次变更 0 项 · 共 121,399 项」。
 */
export default function SyncBanner({ status, starting = false, onSync, error }: SyncBannerProps) {
  const syncState = status?.syncStatus ?? 'idle';
  const syncing = starting || syncState === 'syncing';
  const hasError = Boolean(error) || syncState === 'error';

  const tone = hasError
    ? 'error'
    : syncing
      ? 'busy'
      : 'idle';

  const text = (() => {
    if (error) return error;
    if (syncState === 'error') return status?.lastError?.trim() || '同步出错，请稍后重试';
    if (starting) return '已开始同步，可继续浏览…';
    if (syncState === 'syncing') {
      return status && status.syncedItemCount > 0
        ? `正在同步…本次已变更 ${status.syncedItemCount} 项`
        : '正在同步…';
    }
    if (status?.lastSyncAt) {
      const at = new Date(status.lastSyncAt);
      if (!Number.isNaN(at.getTime())) {
        const time = at.toLocaleString('zh-CN', { hour: '2-digit', minute: '2-digit' });
        // 组合口径：时间 + 本次变更数 + 已索引总量，避免「已同步 0 项」被误读为失败。
        const parts = [
          `上次同步 ${time}`,
          `本次变更 ${status.syncedItemCount} 项`,
        ];
        if (typeof status.totalIndexedCount === 'number') {
          parts.push(`共 ${status.totalIndexedCount.toLocaleString('zh-CN')} 项`);
        }
        return parts.join(' · ');
      }
    }
    return '尚未同步';
  })();

  const className = tone === 'error'
    ? 'border-[var(--pim-danger)] bg-[var(--pim-danger-soft)] text-[var(--pim-danger)]'
    : tone === 'busy'
      ? 'border-[var(--pim-info)] bg-[var(--pim-info-soft)] text-[var(--pim-info)]'
      : 'border-[var(--pim-border)] bg-[var(--pim-surface-muted)] text-[var(--pim-text-muted)]';

  return (
    <div
      className={`flex flex-wrap items-center gap-2 rounded-lg border px-3 py-1.5 text-xs ${className}`}
      role={tone === 'error' ? 'alert' : 'status'}
      data-testid="sync-banner"
      data-sync-state={tone === 'error' ? 'error' : syncing ? 'syncing' : 'idle'}
    >
      {tone === 'error' ? (
        <AlertCircle size={13} />
      ) : syncing ? (
        <Loader2 size={13} className="animate-spin" />
      ) : (
        <CheckCircle2 size={13} />
      )}
      <span className="min-w-0 flex-1" data-testid="sync-banner-text">{text}</span>
      <button
        type="button"
        className="pim-button-secondary inline-flex items-center gap-1 px-2 py-1 text-xs disabled:opacity-50"
        onClick={onSync}
        disabled={syncing}
        data-testid="sync-now-button"
      >
        <RefreshCw size={12} className={syncing ? 'animate-spin' : undefined} />
        {syncing ? '同步中…' : '立即同步'}
      </button>
    </div>
  );
}
