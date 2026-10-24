import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useShellShare } from '../hooks/useShellShare';
import { Calendar, CheckSquare, Paperclip, Pencil, Plus } from 'lucide-react';

import { getQuickNotes } from '../api/quickNotes';
import QuickNoteDialog from '../components/quick-notes/QuickNoteDialog';
import type { QuickNoteListItem, QuickNoteStatus } from '../types';
import EmptyState from '../ui/EmptyState';
import MobilePageHeader from '../ui/MobilePageHeader';
import PageHeader from '../ui/PageHeader';

const statusFilters: Array<{ key: QuickNoteStatus | 'all'; label: string }> = [
  { key: 'all', label: '全部' },
  { key: 'inbox', label: '收集箱' },
  { key: 'processed', label: '已处理' },
  { key: 'archived', label: '已归档' },
];

const statusLabels: Record<QuickNoteStatus, string> = {
  inbox: '收集箱',
  processed: '已处理',
  archived: '已归档',
};

const noteCategories = ['全部', '灵感', '学业', '开发', '运维', '生活'] as const;

function extractNoteCategory(text?: string | null): string {
  if (!text) return '闪念';
  const match = text.match(/^\[(.*?)\]/) || text.match(/^#(.*?)\s/) || text.match(/^【(.*?)】/);
  if (match && match[1]?.trim()) {
    return match[1].trim();
  }
  for (const cat of ['灵感', '学业', '开发', '运维', '生活']) {
    if (text.includes(cat)) return cat;
  }
  return '闪念';
}

function formatFileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDateTime(value: string | null | undefined) {
  if (!value) return '未知时间';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function noteTitle(note: Pick<QuickNoteListItem, 'contentPreview'>) {
  const preview = note.contentPreview?.trim();
  return preview || '空白记录';
}

function StatusBadge({ status }: { status: QuickNoteStatus }) {
  const tone =
    status === 'inbox'
      ? 'border-blue-200 bg-blue-50 text-blue-700'
      : status === 'processed'
        ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
        : 'border-slate-200 bg-slate-100 text-slate-600';

  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${tone}`}>
      {statusLabels[status]}
    </span>
  );
}


// ─── Page ─────────────────────────────────────────────────────────────────────

export default function QuickNotesPage() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [statusFilter, setStatusFilter] = useState<QuickNoteStatus | 'all'>('all');
  const [categoryFilter, setCategoryFilter] = useState<string>('全部');
  const [search, setSearch] = useState('');
  const [searchParams] = useSearchParams();
  const prefill = searchParams.get('prefill') ?? searchParams.get('text') ?? '';
  const isEmbed = searchParams.get('embed') === '1';
  const [error, setError] = useState<string | null>(null);
  const [deletedIds, setDeletedIds] = useState<Set<string>>(() => new Set());

  // Dialog state
  const [dialogOpen, setDialogOpen] = useState(false);
  const [dialogMode, setDialogMode] = useState<'create' | 'edit'>('create');
  const [dialogNoteId, setDialogNoteId] = useState<string | null>(null);
  const [dialogInitialContent, setDialogInitialContent] = useState('');

  // FAB state
  const [showFabMenu, setShowFabMenu] = useState(false);
  const fabRef = useRef<HTMLDivElement>(null);

  const hasPrefilled = useRef(false);

  useEffect(() => {
    if (prefill && !hasPrefilled.current) {
      hasPrefilled.current = true;
      openCreateDialog(prefill);
    }
  }, [prefill]);

  useShellShare(useCallback((detail) => {
    openCreateDialog(detail.text ?? detail.url);
  }, []));

  const listParams = useMemo(() => ({
    status: statusFilter === 'all' ? undefined : statusFilter,
    search: search.trim() || undefined,
    page: 1,
    pageSize: 50,
  }), [search, statusFilter]);

  const listQuery = useQuery({
    queryKey: ['quick-notes', 'list', listParams],
    queryFn: () => getQuickNotes(listParams),
  });

  const notes = useMemo(
    () => (listQuery.data?.items ?? [])
      .filter(note => !deletedIds.has(note.id))
      .filter(note => categoryFilter === '全部' || extractNoteCategory(note.contentPreview) === categoryFilter),
    [deletedIds, listQuery.data?.items, categoryFilter],
  );

  useEffect(() => {
    if (listQuery.data) {
      const idsInList = new Set(listQuery.data.items.map(note => note.id));
      setDeletedIds(current => {
        const pendingIds = Array.from(current).filter(id => idsInList.has(id));
        if (pendingIds.length === current.size) return current;
        return new Set(pendingIds);
      });
    }
  }, [listQuery.data]);

  function invalidateQuickNotes() {
    void queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
  }

  function openCreateDialog(initialContent?: string) {
    setDialogMode('create');
    setDialogNoteId(null);
    setDialogInitialContent(initialContent ?? '');
    setDialogOpen(true);
    setShowFabMenu(false);
    setError(null);
  }

  function openEditDialog(noteId: string) {
    setDialogMode('edit');
    setDialogNoteId(noteId);
    setDialogOpen(true);
    setShowFabMenu(false);
    setError(null);
  }

  function closeDialog() {
    setDialogOpen(false);
    setDialogNoteId(null);
  }

  // Close FAB menu on outside click or Escape（与全局入口行为一致，#300）。
  useEffect(() => {
    if (!showFabMenu) return;
    function handleClick(e: MouseEvent) {
      if (fabRef.current && !fabRef.current.contains(e.target as Node)) {
        setShowFabMenu(false);
      }
    }
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setShowFabMenu(false);
    }
    document.addEventListener('mousedown', handleClick);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleClick);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [showFabMenu]);

  return (
    <div className="mx-auto flex h-full max-w-[1440px] flex-col gap-4 overflow-auto pb-24 md:pb-4">
      {!isEmbed && (
        <MobilePageHeader title="快速记录" action={<span className="text-xs text-slate-500 md:hidden">收集</span>} />
      )}
      {!isEmbed && (
        <PageHeader
          title="快速记录"
          subtitle="收集、整理、处理和归档临时想法。"
          actions={
            <button
              type="button"
              onClick={() => void listQuery.refetch()}
              disabled={listQuery.isFetching}
              className="pim-button-secondary min-h-[44px] px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            >
              刷新
            </button>
          }
        />
      )}

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {/* Filters & Search */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-zinc-400 font-mono">状态:</span>
          {statusFilters.map(item => (
            <button
              key={item.key}
              type="button"
              onClick={() => setStatusFilter(item.key)}
              className={`min-h-[32px] rounded-full border px-3 py-1 text-xs font-semibold transition-colors ${
                statusFilter === item.key
                  ? 'border-zinc-900 bg-zinc-900 text-white'
                  : 'border-zinc-200 bg-white text-zinc-600 hover:border-zinc-300 hover:bg-zinc-50'
              }`}
            >
              {item.label}
            </button>
          ))}
        </div>
        <label className="block flex-1 min-w-[200px] max-w-xs">
          <span className="sr-only">搜索快速记录</span>
          <input
            type="search"
            value={search}
            onChange={event => setSearch(event.target.value)}
            placeholder="搜索内容..."
            className="w-full rounded-lg border border-zinc-200 bg-white px-3 py-2 text-sm text-zinc-900 outline-none transition-colors placeholder:text-zinc-400 focus:border-zinc-400 focus:ring-2 focus:ring-zinc-100"
          />
        </label>
      </div>

      {/* Category Pills Filter */}
      <div className="flex flex-wrap items-center gap-1.5 pt-1">
        <span className="text-xs text-zinc-400 font-mono mr-1">分类:</span>
        {noteCategories.map(cat => (
          <button
            key={cat}
            type="button"
            onClick={() => setCategoryFilter(cat)}
            className={`min-h-[28px] rounded-lg border px-2.5 py-0.5 text-xs font-medium transition-colors ${
              categoryFilter === cat
                ? 'border-amber-400 bg-amber-50 text-amber-900 font-semibold'
                : 'border-zinc-200 bg-white text-zinc-600 hover:border-zinc-300 hover:bg-zinc-50'
            }`}
          >
            {cat}
          </button>
        ))}
      </div>

      {/* Masonry Card List */}
      {listQuery.isLoading ? (
        <div className="py-10 text-center text-sm text-zinc-500">加载中...</div>
      ) : notes.length === 0 ? (
        <EmptyState title="没有快速记录" description="调整筛选或新建一条记录。" />
      ) : (
        <div className="columns-1 gap-4 sm:columns-2 lg:columns-3 xl:columns-4">
          {notes.map(note => {
            const cat = extractNoteCategory(note.contentPreview);
            return (
              <div
                key={note.id}
                className="mb-4 break-inside-avoid cursor-pointer rounded-xl border border-zinc-200 bg-white shadow-card transition-all hover:border-zinc-300 hover:shadow-subtle"
                onClick={() => openEditDialog(note.id)}
              >
                <div className="p-4 flex flex-col justify-between">
                  <div>
                    <div className="mb-2 flex items-center justify-between gap-2">
                      <span className="text-[11px] px-2 py-0.5 rounded-full font-medium bg-amber-50 text-amber-700 border border-amber-200 font-mono">
                        {cat}
                      </span>
                      <span className="text-[11px] text-zinc-400 font-mono">
                        {formatDateTime(note.createdAt)}
                      </span>
                    </div>

                    <p className="line-clamp-4 min-w-0 text-xs leading-relaxed text-zinc-800">
                      {noteTitle(note)}
                    </p>

                    {/* Attachments preview capsules */}
                    {note.attachments && note.attachments.length > 0 ? (
                      <div className="mt-3 pt-2 border-t border-zinc-100 flex flex-wrap gap-1.5">
                        {note.attachments.map(att => (
                          <span
                            key={att.id}
                            className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md bg-zinc-50 border border-zinc-200 text-[11px] text-zinc-600 font-mono"
                          >
                            <Paperclip className="h-3 w-3 text-zinc-400 shrink-0" />
                            <span className="truncate max-w-[120px]">{att.fileName}</span>
                            <span className="text-zinc-400 text-[10px]">({formatFileSize(att.sizeBytes)})</span>
                          </span>
                        ))}
                      </div>
                    ) : note.attachmentCount > 0 ? (
                      <div className="mt-3 pt-2 border-t border-zinc-100 flex items-center gap-1 text-[11px] text-zinc-400 font-mono">
                        <Paperclip className="h-3 w-3" />
                        <span>{note.attachmentCount} 个附件</span>
                      </div>
                    ) : null}
                  </div>

                  <div className="mt-3 pt-2 border-t border-zinc-50 text-[11px] text-zinc-400 flex justify-between items-center">
                    <span>点击编辑</span>
                    <StatusBadge status={note.status} />
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* FAB */}
      <div ref={fabRef} className="fixed bottom-6 right-6 z-40 flex flex-col items-end gap-2">
        {showFabMenu && (
          <div
            role="menu"
            aria-label="快速记录菜单"
            className="animate-dialog rounded-xl border border-zinc-200 bg-white p-1 shadow-dialog"
          >
            <button
              type="button"
              role="menuitem"
              onClick={() => openCreateDialog()}
              className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50"
            >
              <Pencil className="h-4 w-4" /> 写闪念
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => navigate('/tasks')}
              className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50"
            >
              <CheckSquare className="h-4 w-4" /> 建任务
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => navigate('/calendar')}
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
          aria-expanded={showFabMenu}
          onClick={() => setShowFabMenu(prev => !prev)}
          className="flex h-14 w-14 items-center justify-center rounded-full bg-zinc-900 text-white shadow-lg transition-transform hover:scale-105 hover:bg-zinc-800"
        >
          <Plus className={`h-6 w-6 transition-transform ${showFabMenu ? 'rotate-45' : ''}`} />
        </button>
      </div>

      {/* NoteDialog */}
      <QuickNoteDialog
        open={dialogOpen}
        mode={dialogMode}
        noteId={dialogNoteId}
        initialContent={dialogInitialContent}
        onClose={closeDialog}
        onSaved={invalidateQuickNotes}
      />
    </div>
  );
}
