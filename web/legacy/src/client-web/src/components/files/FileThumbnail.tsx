import { useEffect, useRef, useState } from 'react';
import { ImageOff, Loader2 } from 'lucide-react';
import { oneDriveThumbnailUrl } from '../../api/files';
import type { FileItem } from '../../types';

export interface FileThumbnailProps {
  item: FileItem;
  /** 尺寸档：P8 沿用 Graph 的 medium / large。 */
  size?: 'medium' | 'large';
  className?: string;
}

/**
 * 图片条目的真缩略图（REQ-23 / AC-23.1 / AC-23.3）。
 *
 * 关键约束：
 * - **懒加载**：只有滚动到可见范围才发起请求（`IntersectionObserver`），
 *   否则打开含 2.5 万张图的目录会一次性打爆浏览器的连接池（AC-23.3 反面）；
 * - 缩略图走 `/files/items/{id}/thumbnail`（302 到微软直链），**不经过服务器搬运**；
 * - 失败显示占位图标，绝不出现破图（AC-23.3）。
 *
 * 302 需要 Authorization，`<img src>` 无法带头，因此沿用既有的带鉴权取 blob 的方式；
 * blob 用完即 revoke，避免大目录下内存堆积。
 */
export default function FileThumbnail({ item, size = 'medium', className }: FileThumbnailProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [visible, setVisible] = useState(false);
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [state, setState] = useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');

  // 懒加载：进入视口才把 visible 置真，之后不再回退（避免滚动抖动导致反复请求）。
  // setState 只发生在 IntersectionObserver 的**回调**里（外部系统事件），
  // 不在 effect 体内同步调用——后者会触发级联渲染。
  useEffect(() => {
    const node = containerRef.current;
    if (!node || visible) return;

    if (typeof IntersectionObserver === 'undefined') {
      // 无 Observer（极老环境）时退化为「立即加载」
      const handle = window.setTimeout(() => {
        setState('loading');
        setVisible(true);
      }, 0);
      return () => window.clearTimeout(handle);
    }

    const observer = new IntersectionObserver(
      entries => {
        if (entries.some(entry => entry.isIntersecting)) {
          // 在**回调**里同时置 visible 与 loading：都是外部事件驱动的更新，
          // 既避免 effect 体内同步 setState，也不引入额外的时序延迟
          setState('loading');
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(node);
    return () => observer.disconnect();
  }, [visible]);

  useEffect(() => {
    if (!visible) return;
    let cancelled = false;
    let created: string | null = null;
    const controller = new AbortController();

    (async () => {
      try {
        const response = await fetch(oneDriveThumbnailUrl(item.id, size), {
          headers: authorizationHeader(),
          signal: controller.signal,
        });
        if (!response.ok) throw new Error(`缩略图请求失败（${response.status}）`);
        const blob = await response.blob();
        if (cancelled) return;
        created = URL.createObjectURL(blob);
        setObjectUrl(created);
        setState('loaded');
      } catch (error) {
        if (cancelled || (error instanceof DOMException && error.name === 'AbortError')) return;
        setState('error');
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
      if (created) URL.revokeObjectURL(created);
    };
  }, [visible, item.id, size]);

  return (
    <div
      ref={containerRef}
      className={`relative flex items-center justify-center overflow-hidden bg-[var(--pim-surface-muted)] ${className ?? ''}`}
      data-testid="file-thumbnail"
      data-thumb-state={state}
    >
      {state === 'loaded' && objectUrl ? (
        <img src={objectUrl} alt={item.name} className="h-full w-full object-cover" loading="lazy" />
      ) : state === 'error' ? (
        // AC-23.3：失败显示占位图标，不得破图
        <ImageOff size={18} className="text-[var(--pim-text-muted)]" data-testid="file-thumbnail-placeholder" />
      ) : state === 'loading' ? (
        <Loader2 size={14} className="animate-spin text-[var(--pim-text-muted)]" />
      ) : (
        <ImageOff size={18} className="text-[var(--pim-text-muted)] opacity-40" data-testid="file-thumbnail-idle" />
      )}
    </div>
  );
}

/** 从 localStorage 取 access token（与 api/client.ts 同一来源）。 */
function authorizationHeader(): Record<string, string> {
  try {
    const token = window.localStorage.getItem('accessToken');
    return token ? { Authorization: `Bearer ${token}` } : {};
  } catch {
    return {};
  }
}
