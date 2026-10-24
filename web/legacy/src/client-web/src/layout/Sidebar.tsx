import { useEffect, useRef, useState } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getCalendars, createCalendar, updateCalendar, deleteCalendar, previewCalendarDelete, getTaskBooks } from '../api/calendar';
import { useAuth } from '../auth/AuthContext';
import { useCalendarVisibility } from '../context/CalendarVisibilityContext';
import SidebarStatusIndicator from '../components/status/SidebarStatusIndicator';
import ConfirmActionDialog, { type DeleteConfirmationInput } from '../ui/ConfirmActionDialog';
import { NAV_ITEMS, type NavItem } from './navItems';

export const primaryNavItems = NAV_ITEMS;

/** 当前路径命中的导航项里取路径最长的一个（父子级路径如 /pc-tracker 与 /pc-tracker/browser 只高亮子级）。 */
function activeNavPath(items: NavItem[], pathname: string): string {
  let best = '';
  for (const item of items) {
    const matched = pathname === item.path || pathname.startsWith(`${item.path}/`);
    if (matched && item.path.length > best.length) best = item.path;
  }
  return best;
}

function CalendarBookSection({
  title,
  books,
  queryKey,
  kind,
  manageable = true,
  onSelectBook,
}: {
  title: string;
  books: Array<{ id: string; name: string; color: string; taskCount?: number }>;
  queryKey: string[];
  kind: string;
  manageable?: boolean;
  onSelectBook?: (bookId: string) => void;
}) {
  const queryClient = useQueryClient();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [newName, setNewName] = useState('');
  const [showNew, setShowNew] = useState(false);
  const [deleteInput, setDeleteInput] = useState<DeleteConfirmationInput | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const activeDeletePreviewRequestRef = useRef<{ deleteId: string; requestId: number } | null>(null);
  const nextDeletePreviewRequestIdRef = useRef(0);
  const { hiddenCalendarIds, toggleCalendar } = useCalendarVisibility();

  const createMut = useMutation({
    mutationFn: (data: { name: string; color?: string; kind?: string }) => createCalendar(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey });
      setNewName('');
      setShowNew(false);
    }
  });

  const updateMut = useMutation({
    mutationFn: ({ id, data }: { id: string; data: { name?: string; color?: string } }) => updateCalendar(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey });
      setEditingId(null);
    }
  });

  const deleteMut = useMutation({
    mutationFn: deleteCalendar,
    onSuccess: () => {
      const affectedQueryKeys: string[][] = [
        queryKey,
        ['calendars'],
        ['calendar-recycle-bin'],
        ['events'],
        ['events-paged'],
        ['tasks'],
        ['today-sections'],
        ['today-section'],
      ];

      affectedQueryKeys.forEach(key => {
        queryClient.invalidateQueries({ queryKey: key });
      });

      activeDeletePreviewRequestRef.current = null;
      setDeleteInput(null);
      setDeleteId(null);
      setDeleteError(null);
    },
    onError: () => {
      activeDeletePreviewRequestRef.current = null;
      setDeleteInput(null);
      setDeleteId(null);
      setDeleteError('删除失败，请稍后重试。');
    }
  });

  const previewDeleteMut = useMutation({
    mutationFn: previewCalendarDelete,
  });

  function startRename(id: string, currentName: string) {
    setEditingId(id);
    setEditName(currentName);
  }

  function submitRename(id: string) {
    if (editName.trim()) updateMut.mutate({ id, data: { name: editName.trim() } });
  }

  function isActiveDeletePreviewRequest(id: string, requestId: number) {
    return activeDeletePreviewRequestRef.current?.deleteId === id
      && activeDeletePreviewRequestRef.current.requestId === requestId;
  }

  function requestDeletePreview(id: string) {
    const requestId = nextDeletePreviewRequestIdRef.current + 1;

    nextDeletePreviewRequestIdRef.current = requestId;
    activeDeletePreviewRequestRef.current = { deleteId: id, requestId };
    setDeleteId(id);
    setDeleteInput(null);
    setDeleteError(null);
    previewDeleteMut.mutate(id, {
      onSuccess: preview => {
        if (isActiveDeletePreviewRequest(id, requestId)) {
          setDeleteInput({
            targetType: preview.targetType,
            title: preview.title,
            affectedCount: Math.max(1, preview.affectedCount),
            samples: preview.samples,
          });
        }
      },
      onError: () => {
        if (isActiveDeletePreviewRequest(id, requestId)) {
          activeDeletePreviewRequestRef.current = null;
          setDeleteInput(null);
          setDeleteId(null);
          setDeleteError('删除预览失败，请稍后重试。');
        }
      },
    });
  }

  function cancelDelete() {
    activeDeletePreviewRequestRef.current = null;
    setDeleteInput(null);
    setDeleteId(null);
  }

  function confirmDelete() {
    if (deleteId) deleteMut.mutate(deleteId);
  }

  return (
    <div className="mt-4 border-t border-slate-200/80 pt-4">
      <div className="mb-2 flex items-center justify-between px-2">
        <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-400">{title}</p>
        {manageable && (
          <button
            onClick={() => setShowNew(!showNew)}
            className="h-6 w-6 rounded-full text-sm leading-none text-slate-400 transition-colors hover:bg-blue-50 hover:text-blue-600"
            aria-label={`新建${title}`}
          >
            +
          </button>
        )}
      </div>

      {manageable && showNew && (
        <div className="px-2 mb-2 flex gap-1">
          <input
            type="text" placeholder={`${title}名称`}
            value={newName}
            onChange={e => setNewName(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && newName.trim()) createMut.mutate({ name: newName.trim(), kind }); }}
            className="min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-2 py-1 text-xs text-slate-700 outline-none transition-colors focus:border-blue-400"
            autoFocus
          />
          <button
            onClick={() => newName.trim() && createMut.mutate({ name: newName.trim(), kind })}
            disabled={createMut.isPending}
            className="rounded-lg bg-blue-600 px-2 py-1 text-xs text-white transition-colors hover:bg-blue-700 disabled:opacity-50"
          >
            确定
          </button>
        </div>
      )}

      {deleteError && (
        <p className="px-2 pb-1 text-xs text-red-500">{deleteError}</p>
      )}

      {books?.map(book => {
        const hidden = hiddenCalendarIds.has(book.id);
        const deleteDisabled = previewDeleteMut.isPending || deleteMut.isPending;
        return (
          <div key={book.id} className={`group flex items-center gap-2 rounded-lg px-2 py-1.5 transition-colors hover:bg-slate-100 ${hidden ? 'opacity-45' : ''}`}>
            <button
              onClick={() => toggleCalendar(book.id)}
              className="h-5 w-5 rounded-full border border-slate-200 text-[10px] leading-none text-slate-400 transition-colors hover:border-blue-300 hover:text-blue-600 flex-shrink-0"
              title={hidden ? '显示' : '隐藏'}
            >
              {hidden ? '○' : '●'}
            </button>
            <span className="w-2.5 h-2.5 rounded-full flex-shrink-0" style={{ backgroundColor: book.color }} />
            {editingId === book.id && manageable ? (
              <input
                type="text" value={editName}
                onChange={e => setEditName(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') submitRename(book.id); if (e.key === 'Escape') setEditingId(null); }}
                onBlur={() => submitRename(book.id)}
                className="min-w-0 flex-1 rounded border border-slate-200 bg-white px-1 py-0.5 text-xs text-slate-700 outline-none focus:border-blue-400"
                autoFocus
              />
            ) : (
              <span
                className={`flex-1 truncate text-xs text-slate-600 ${onSelectBook ? 'cursor-pointer hover:text-blue-600 font-medium' : 'cursor-default'}`}
                onClick={onSelectBook ? () => onSelectBook(book.id) : undefined}
                onDoubleClick={manageable ? () => startRename(book.id, book.name) : undefined}
                title={onSelectBook ? `查看「${book.name}」` : (manageable ? '双击重命名' : undefined)}
              >
                {book.name}
              </span>
            )}
            {book.taskCount !== undefined && (
              <span className="ml-auto rounded-full bg-slate-100 px-1.5 text-[10px] text-slate-500">{book.taskCount}</span>
            )}
            {manageable && (
            <div className="hidden group-hover:flex items-center gap-0.5">
              <button
                onClick={() => startRename(book.id, book.name)}
                className="rounded px-1 text-xs leading-none text-slate-400 hover:bg-blue-50 hover:text-blue-600"
                title="重命名"
              >
                ✎
              </button>
              <button
                onClick={() => requestDeletePreview(book.id)}
                disabled={deleteDisabled}
                className="rounded px-1 text-xs leading-none text-slate-400 hover:bg-red-50 hover:text-red-500 disabled:cursor-not-allowed disabled:opacity-50"
                title="删除"
              >
                ✕
              </button>
            </div>
            )}
          </div>
        );
      })}

      {(!books || books.length === 0) && !showNew && (
        <p className="px-2 py-1 text-xs text-slate-400">暂无{title}{manageable ? '，点击 + 创建' : ''}</p>
      )}

      <ConfirmActionDialog
        open={deleteInput !== null}
        input={deleteInput}
        isPending={deleteMut.isPending}
        onCancel={cancelDelete}
        onConfirm={confirmDelete}
      />
    </div>
  );
}

export interface SidebarProps {
  mobileOpen?: boolean;
  onClose?: () => void;
}

export default function Sidebar({ mobileOpen = false, onClose }: SidebarProps = {}) {
  const navigate = useNavigate();
  const location = useLocation();
  const { logout, username } = useAuth();

  useEffect(() => {
    if (!mobileOpen || !onClose) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [mobileOpen, onClose]);

  const { data: calendars = [] } = useQuery({
    queryKey: ['calendars', 'calendar'],
    queryFn: () => getCalendars('calendar')
  });

  const { data: taskBooks = [] } = useQuery({
    queryKey: ['task-books'],
    queryFn: () => getTaskBooks()
  });

  const handleNavigate = (path: string) => {
    navigate(path);
    onClose?.();
  };

  return (
    <>
      {mobileOpen && (
        <div
          data-testid="sidebar-backdrop"
          className="fixed inset-0 z-40 bg-slate-900/40 backdrop-blur-xs md:hidden"
          onClick={onClose}
          aria-hidden="true"
        />
      )}

      <aside
        className={`fixed inset-y-0 left-0 z-50 flex h-full w-[260px] max-w-[80vw] flex-col border-r border-slate-200/80 bg-white/95 shadow-2xl transition-transform duration-200 ease-out md:static md:z-auto md:w-[220px] md:h-full md:max-h-full md:shrink-0 md:bg-white/90 md:shadow-none md:translate-x-0 ${
          mobileOpen ? 'translate-x-0' : '-translate-x-full pointer-events-none md:pointer-events-auto'
        } md:flex`}
      >
        <div className="flex items-center justify-between px-4 py-5">
          <div>
            <p className="text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">PIM</p>
            <p className="mt-1 text-lg font-semibold text-slate-950">个人中枢</p>
          </div>
          {onClose && (
            <button
              type="button"
              onClick={onClose}
              aria-label="关闭主菜单"
              className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-600 md:hidden"
            >
              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
        <SidebarStatusIndicator />

        <nav className="flex-1 min-h-0 space-y-1 overflow-y-auto px-3 pb-3">

          {primaryNavItems.map(item => {
            // 嵌套路径（如 /pc-tracker/browser）会同时命中多个前缀，仅高亮最长匹配项
            const active = item.path === activeNavPath(primaryNavItems, location.pathname);

            return (
              <button
                key={item.path}
                onClick={() => handleNavigate(item.path)}
                aria-current={active ? 'page' : undefined}
                className={`flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left text-sm font-medium transition-colors ${
                  active
                    ? 'bg-blue-50 text-blue-700 shadow-[inset_0_0_0_1px_rgba(79,70,229,0.14)]'
                    : 'text-slate-600 hover:bg-slate-100 hover:text-slate-950'
                }`}
              >
                <span aria-hidden="true" className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-xs font-semibold ${
                  active ? 'bg-blue-600 text-white' : 'bg-slate-100 text-slate-500'
                }`}>
                  {item.short}
                </span>
                <span>{item.label}</span>
              </button>
            );
          })}

          <CalendarBookSection
            title="日历本"
            books={calendars}
            queryKey={['calendars']}
            kind="calendar"
            onSelectBook={bookId => handleNavigate(`/calendar?calendarId=${bookId}`)}
          />

          <CalendarBookSection
            title="任务本"
            books={taskBooks.map(b => ({ ...b, color: (b as { color?: string }).color || '#6366f1' }))}
            queryKey={['task-books']}
            kind="task"
            manageable={false}
            onSelectBook={bookId => handleNavigate(`/tasks?taskBookId=${bookId}`)}
          />
        </nav>

        <div className="flex items-center justify-between border-t border-slate-200/80 p-3">
          <div className="min-w-0">
            <span className="truncate text-xs text-slate-500">{username}</span>
            <p className="mt-1 truncate text-[10px] text-slate-400" title={__APP_VERSION__}>
              {__APP_VERSION__}
            </p>
          </div>
          <button onClick={logout} className="rounded-lg px-2 py-1 text-xs text-slate-500 hover:bg-red-50 hover:text-red-500">退出</button>
        </div>
      </aside>
    </>
  );
}

