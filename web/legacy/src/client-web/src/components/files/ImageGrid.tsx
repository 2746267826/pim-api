import { useCallback, useMemo, useState } from 'react';
import Lightbox from 'yet-another-react-lightbox';
import 'yet-another-react-lightbox/styles.css';
import { X } from 'lucide-react';
import type { FileItem } from '../../types';
import { oneDriveContentUrl } from '../../api/files';
import FileThumbnail from './FileThumbnail';

export interface ImageGridProps {
  items: FileItem[];
  onSelect: (item: FileItem) => void;
  selectedItem: FileItem | null;
}

/** 图片条目判定（与预览面板同口径）。 */
function isImage(item: FileItem): boolean {
  return item.mimeType?.startsWith('image/') ?? false;
}

/**
 * 图片网格 + 灯箱（REQ-23）。
 *
 * - 缩略图懒加载由 <see cref="FileThumbnail"/> 负责（进视口才请求）；
 * - 灯箱用 **yet-another-react-lightbox**（REQ-29 指定库），不手搓；
 * - 大图走 `/files/items/{id}/content`（302 到微软直链）——内容不经服务器搬运（AC-23.2）；
 *   该端点需要 Authorization，因此这里用一次带鉴权的请求换取直链后交给灯箱。
 * - 左右翻仅在**当前网格内的图片**之间（与用户所见一致）。
 */
export default function ImageGrid({ items, onSelect, selectedItem }: ImageGridProps) {
  const images = useMemo(() => items.filter(isImage), [items]);
  const [lightboxIndex, setLightboxIndex] = useState(-1);
  const [resolved, setResolved] = useState<Record<string, string>>({});

  const openLightbox = useCallback(
    (item: FileItem) => {
      const index = images.findIndex(candidate => candidate.id === item.id);
      // 先解析该图（及相邻图）的直链，避免灯箱打开后一片空白
      void resolveLinks(images, item.id, resolved, setResolved);
      onSelect(item);
      setLightboxIndex(index >= 0 ? index : 0);
    },
    [images, onSelect, resolved],
  );

  const slides = images.map(image => ({
    src: resolved[image.id] ?? oneDriveContentUrl(image.id),
    alt: image.name,
    title: image.name,
  }));

  return (
    <section className="flex min-h-0 flex-1 flex-col" data-testid="image-grid">
      <div className="grid min-h-0 flex-1 grid-cols-[repeat(auto-fill,minmax(140px,1fr))] content-start gap-3 overflow-auto p-4">
        {items.map(item => (
          <button
            key={item.id}
            type="button"
            data-testid="grid-tile"
            data-file={item.name}
            className={`overflow-hidden rounded-xl border text-left transition ${
              selectedItem?.id === item.id
                ? 'border-[var(--pim-primary)] bg-[var(--pim-primary-soft)]'
                : 'border-[var(--pim-border)] bg-[var(--pim-surface)] hover:-translate-y-px hover:shadow-sm'
            }`}
            onClick={() => (isImage(item) ? openLightbox(item) : onSelect(item))}
          >
            {/* AC-23.1：只有图片走真缩略图；非图片保持类型图标 */}
            <FileThumbnail item={item} size="medium" className="h-24 w-full" />
            <div className="truncate px-2.5 py-2 text-xs text-[var(--pim-text-muted)]" title={item.name}>
              {item.name}
            </div>
          </button>
        ))}
      </div>

      {/* AC-23.2：左右翻 + ESC/关闭返回 */}
      {lightboxIndex >= 0 && slides.length > 0 && (
        <Lightbox
          open
          index={lightboxIndex}
          close={() => setLightboxIndex(-1)}
          slides={slides}
          on={{ view: ({ index }) => setLightboxIndex(index) }}
          carousel={{ finite: true }}
          render={{
            // 关闭按钮用全站图标，避免第二套视觉
            buttonClose: () => (
              <button
                type="button"
                aria-label="关闭大图"
                data-testid="lightbox-close"
                className="yarl__button"
                onClick={() => setLightboxIndex(-1)}
              >
                <X size={20} />
              </button>
            ),
          }}
        />
      )}
    </section>
  );
}

/**
 * 解析图片的微软直链（带鉴权请求 302 端点，跟随跳转后取最终 URL）。
 * 只解析当前与相邻的几张，避免打开网格就解析全部。
 */
async function resolveLinks(
  images: FileItem[],
  currentId: string,
  cache: Record<string, string>,
  apply: (updater: (current: Record<string, string>) => Record<string, string>) => void,
): Promise<void> {
  const index = images.findIndex(image => image.id === currentId);
  if (index < 0) return;
  const targets = [images[index - 1], images[index], images[index + 1]].filter(
    (image): image is FileItem => Boolean(image) && !cache[image!.id],
  );

  const token = (() => {
    try {
      return window.localStorage.getItem('accessToken');
    } catch {
      return null;
    }
  })();

  for (const target of targets) {
    try {
      const response = await fetch(oneDriveContentUrl(target.id), {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (!response.ok) continue;
      const url = response.url;
      // 只接受微软域：直链必须由微软提供，不能是 PIM 页面（AC-22.2 同源要求）
      if (url && /^https:\/\/[^/]*\.(microsoft|live|sharepoint|1drv)\./i.test(url)) {
        apply(current => ({ ...current, [target.id]: url }));
      }
    } catch {
      // 单张失败不影响其余；灯箱会回落到 302 端点
    }
  }
}
