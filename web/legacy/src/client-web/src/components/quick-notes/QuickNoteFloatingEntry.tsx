import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Calendar, CheckSquare, Pencil, Plus } from 'lucide-react';

// 编辑卡片内含 Markdown 编辑器（体积大），懒加载以保持它不进入应用入口 bundle。
const LazyQuickNoteDialog = lazy(() => import('./QuickNoteDialog'));

/**
 * 已有自己右下角悬浮按钮（黑色 +）的页面：这些路径不再渲染全局入口，
 * 否则会出现两个按钮重叠（#280 的现象）。
 */
export const PAGE_FAB_PATHS: readonly string[] = ['/quick-notes'];

interface QuickNoteFloatingEntryProps {
  /** 当前路由路径；命中 PAGE_FAB_PATHS 时不渲染入口。 */
  pathname?: string;
}

/**
 * 全站快速记录入口（#280 统一为黑按钮；#300 统一交互）：
 * - 只有**一个**悬浮按钮，样式与快速记录页内的黑色按钮一致；
 * - 点击先弹出菜单（写闪念 / 建任务 / 排日程），与「快速记录」页完全一致；
 *   「写闪念」→ 新版编辑卡片（QuickNoteDialog，可拖动、含分类 / 附件 / 归档）；
 *   「建任务」→ 任务页；「排日程」→ 日历页；
 * - 旧的 QuickNoteGlobalPanel 编辑面板不再出现；
 * - 页面自己已有 FAB 时（/quick-notes）不渲染，避免两个按钮重叠。
 */
export function QuickNoteFloatingEntry({ pathname }: QuickNoteFloatingEntryProps) {
  const navigate = useNavigate();
  const [menuOpen, setMenuOpen] = useState(false);
  const [dialogOpen, setDialogOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const currentPath = pathname ?? (typeof window === 'undefined' ? '' : window.location.pathname);
  const hidden = PAGE_FAB_PATHS.includes(currentPath);

  // 与「快速记录」页一致：点击菜单外部关闭菜单；Escape 也能关闭。
  useEffect(() => {
    if (!menuOpen) return;
    function handleClick(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setMenuOpen(false);
      }
    }
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setMenuOpen(false);
    }
    document.addEventListener('mousedown', handleClick);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleClick);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [menuOpen]);

  // 本页已有自己的 FAB 时必须收起全局菜单/卡片（review 发现）：
  // 组件不会卸载，若只 return null，menuOpen 会残留为 true，
  // 离开该页时菜单会「自己弹开」。
  useEffect(() => {
    if (hidden) {
      setMenuOpen(false);
      setDialogOpen(false);
    }
  }, [hidden]);

  if (hidden) {
    return null;
  }

  function openDialog() {
    setMenuOpen(false);
    setDialogOpen(true);
  }

  function goTo(path: string) {
    setMenuOpen(false);
    navigate(path);
  }

  return (
    <>
      <div ref={containerRef} className="fixed bottom-6 right-6 z-40 flex flex-col items-end gap-2">
        {menuOpen && (
          <div
            role="menu"
            aria-label="快速记录菜单"
            className="animate-dialog rounded-xl border border-zinc-200 bg-white p-1 shadow-dialog"
          >
            <button
              type="button"
              role="menuitem"
              onClick={openDialog}
              className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50"
            >
              <Pencil className="h-4 w-4" /> 写闪念
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => goTo('/tasks')}
              className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50"
            >
              <CheckSquare className="h-4 w-4" /> 建任务
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => goTo('/calendar')}
              className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50"
            >
              <Calendar className="h-4 w-4" /> 排日程
            </button>
          </div>
        )}
        <button
          type="button"
          aria-label="打开快速记录"
          title="打开快速记录"
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen(previous => !previous)}
          className="flex h-14 w-14 items-center justify-center rounded-full bg-zinc-900 text-white shadow-lg transition-transform hover:scale-105 hover:bg-zinc-800 focus:outline-none focus:ring-4 focus:ring-zinc-300"
        >
          <Plus className={`h-6 w-6 text-white transition-transform ${menuOpen ? 'rotate-45' : ''}`} />
        </button>
      </div>
      {dialogOpen && (
        <Suspense fallback={null}>
          <LazyQuickNoteDialog
            open={dialogOpen}
            mode="create"
            noteId={null}
            onClose={() => setDialogOpen(false)}
            onSaved={() => undefined}
            source="web-floating"
          />
        </Suspense>
      )}
    </>
  );
}

export default QuickNoteFloatingEntry;
