import { useEffect, useRef, useState } from 'react';
import { Download, ExternalLink, FolderInput, Link2, MoreHorizontal, Pencil, Trash2 } from 'lucide-react';
import type { FileItem } from '../../types';
import type { RowAction } from './fileActions';
import { rowActionsFor } from './fileActions';

export interface RowMenuProps {
  item: FileItem;
  onAction: (action: RowAction, item: FileItem) => void;
}

const LABELS: Record<RowAction, string> = {
  open: '打开',
  download: '下载',
  share: '复制链接 / 分享',
  rename: '重命名',
  move: '移动到…',
  delete: '删除',
  'open-in-onedrive': '在 OneDrive 打开',
};

const ICONS: Record<RowAction, typeof Download> = {
  open: FolderInput,
  download: Download,
  share: Link2,
  rename: Pencil,
  move: FolderInput,
  delete: Trash2,
  'open-in-onedrive': ExternalLink,
};

/**
 * 行内「⋯」菜单（REQ-19）。
 *
 * AC-19.2 的两条反面要求：
 * - **点击外部可关闭**：用 document 级 pointerdown 监听，并排除菜单自身；
 * - **不遮挡关键信息**：菜单右对齐、限制高度，按钮阻止冒泡以免与行点击（选中/进入）冲突。
 */
export default function RowMenu({ item, onAction }: RowMenuProps) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  const actions = rowActionsFor(item);

  return (
    <div className="relative" ref={containerRef} data-testid="row-menu">
      <button
        type="button"
        aria-label={`${item.name} 的更多操作`}
        aria-haspopup="menu"
        aria-expanded={open}
        className="rounded-md px-1.5 py-1 text-[var(--pim-text-muted)] hover:bg-[var(--pim-surface-muted)]"
        data-testid="row-menu-trigger"
        onClick={event => {
          // 阻止冒泡：行点击会选中/进入，菜单开合不应触发它（AC-19.2）
          event.stopPropagation();
          setOpen(current => !current);
        }}
      >
        <MoreHorizontal size={15} />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-30 mt-1 max-h-72 w-48 overflow-auto rounded-lg border border-[var(--pim-border)] bg-[var(--pim-surface)] py-1 text-xs shadow-[var(--pim-shadow-pop)]"
          data-testid="row-menu-items"
          onClick={event => event.stopPropagation()}
        >
          {actions.map(action => {
            const Icon = ICONS[action];
            const danger = action === 'delete';
            return (
              <button
                key={action}
                type="button"
                role="menuitem"
                className={`flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-[var(--pim-surface-muted)] ${
                  danger ? 'text-[var(--pim-danger)]' : ''
                }`}
                data-testid={`row-menu-${action}`}
                onClick={() => {
                  setOpen(false);
                  onAction(action, item);
                }}
              >
                <Icon size={13} />
                {LABELS[action]}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
