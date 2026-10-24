import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { File, Upload, X } from 'lucide-react';

import {
  archiveQuickNote,
  createQuickNote,
  deleteQuickNote,
  getQuickNote,
  processQuickNote,
  restoreQuickNote,
  updateQuickNote,
  uploadQuickNoteAttachment,
} from '../../api/quickNotes';
import QuickNoteEditor from './QuickNoteEditor';
import {
  clampPanelPosition,
  clearQuickNoteDraft,
  loadPanelPosition,
  loadQuickNoteDraft,
  savePanelPosition,
  saveQuickNoteDraft,
  type PanelPoint,
  type PanelSize,
} from './quickNoteFloatingState';
import type { QuickNoteAttachment } from '../../types';

/** 编辑卡片尺寸（与旧面板一致的默认体量，用于位置夹取与初始定位）。 */
const DIALOG_SIZE: PanelSize = { width: 672, height: 520 };

function getViewportSize(): PanelSize {
  if (typeof window === 'undefined') {
    return { width: 1024, height: 768 };
  }

  return { width: window.innerWidth, height: window.innerHeight };
}

function formatFileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function extractNoteCategory(text?: string | null): string {
  if (!text) return '闪念';
  const match = text.match(/^\[(.*?)\]/) || text.match(/^#(.*?)\s/) || text.match(/^【(.*?)】/);
  if (match && match[1]) return match[1];
  return '闪念';
}

// ─── NoteDialog ───────────────────────────────────────────────────────────────

interface NoteDialogProps {
  open: boolean;
  mode: 'create' | 'edit';
  noteId: string | null;
  onClose: () => void;
  onSaved: () => void;
  initialContent?: string;
  /**
   * 落库来源标识（#300）。默认 `web-page`（快速记录页）；全局悬浮入口传入
   * `web-floating`，以保留被删除的 QuickNoteGlobalPanel 原有的来源区分，
   * 避免「悬浮入口创建」与「页面创建」在数据里无法区分。
   */
  source?: string;
}

export default function QuickNoteDialog({ open, mode, noteId, onClose, onSaved, initialContent, source = 'web-page' }: NoteDialogProps) {
  const queryClient = useQueryClient();
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  /**
   * 「下一次持久化 effect 应当跳过」的标记（#300）。
   * 恢复与持久化两个 effect 在同一次提交内依次执行：恢复 effect 先 setContent，
   * 但本次提交里持久化 effect 仍然会拿着**上一轮的旧 content**（通常是空串）执行，
   * 写出 `saveQuickNoteDraft('')` 把已有草稿短暂删除（跨标签页 storage 监听可观察到）。
   * 因此在恢复时置位、由持久化 effect 消费并立即清零，精确跳过那一次陈旧写入。
   */
  const skipNextDraftPersistRef = useRef(false);

  // ─── 拖动（#280）：编辑卡片可移动，位置记忆沿用旧面板的存储 key 与夹取规则 ───
  const [position, setPosition] = useState<PanelPoint>(() => loadPanelPosition(getViewportSize(), DIALOG_SIZE));
  const positionRef = useRef(position);
  const dragRef = useRef<{ pointerId: number; offsetX: number; offsetY: number } | null>(null);

  useEffect(() => {
    positionRef.current = position;
  }, [position]);

  // 打开时按当前视口夹取一次，避免窗口变小后卡片跑到屏幕外。
  useEffect(() => {
    if (!open) return;
    const nextPosition = clampPanelPosition(positionRef.current, getViewportSize(), DIALOG_SIZE);
    positionRef.current = nextPosition;
    setPosition(nextPosition);
  }, [open]);

  useEffect(() => {
    function handleResize() {
      setPosition(current => {
        const nextPosition = clampPanelPosition(current, getViewportSize(), DIALOG_SIZE);
        positionRef.current = nextPosition;
        savePanelPosition(nextPosition);
        return nextPosition;
      });
    }

    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, []);

  function handlePointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    // 卡片内的按钮/输入不做拖动起点判断：由拖动手柄自身 stopPropagation 排除。
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = {
      pointerId: event.pointerId,
      offsetX: event.clientX - positionRef.current.x,
      offsetY: event.clientY - positionRef.current.y,
    };
  }

  function handlePointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;

    const nextPosition = clampPanelPosition(
      {
        x: event.clientX - drag.offsetX,
        y: event.clientY - drag.offsetY,
      },
      getViewportSize(),
      DIALOG_SIZE,
    );
    positionRef.current = nextPosition;
    setPosition(nextPosition);
  }

  function handlePointerUp(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;

    dragRef.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    savePanelPosition(positionRef.current);
  }

  function handleLostPointerCapture(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) return;

    dragRef.current = null;
    savePanelPosition(positionRef.current);
  }

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialogRef.current?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
    };
  }, [open]);

  const [content, setContent] = useState('');
  const [selectedCategory, setSelectedCategory] = useState('闪念');
  const [attachmentIds, setAttachmentIds] = useState<string[]>([]);
  const [localAttachments, setLocalAttachments] = useState<QuickNoteAttachment[]>([]);
  const [isArchived, setIsArchived] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);

  function handleCategoryChange(newCat: string) {
    setSelectedCategory(newCat);
    setContent(prev => {
      const hasPrefix = /^\[.*?\]\s*/.test(prev);
      if (hasPrefix) {
        return prev.replace(/^\[.*?\]\s*/, `[${newCat}] `);
      }
      return `[${newCat}] ${prev}`;
    });
  }

  // Fetch detail for edit mode
  const detailQuery = useQuery({
    queryKey: ['quick-notes', 'detail', noteId],
    queryFn: () => getQuickNote(noteId as string),
    enabled: mode === 'edit' && Boolean(noteId),
  });

  const selected = detailQuery.data;

  // Populate state when detail loads
  useEffect(() => {
    if (mode === 'edit' && selected) {
      setContent(selected.contentMarkdown);
      setSelectedCategory(extractNoteCategory(selected.contentMarkdown));
      setAttachmentIds(selected.attachments.map(a => a.id));
      setLocalAttachments(selected.attachments);
      setIsArchived(selected.status === 'archived');
    }
  }, [mode, selected]);

  // Reset state when opening（#300）：显式 initialContent 优先，否则恢复上次未保存的草稿，
  // 与旧 QuickNoteGlobalPanel 的行为一致（入口统一后不得丢失草稿能力）。
  useEffect(() => {
    if (!open || mode !== 'create') {
      return;
    }

    const restored = initialContent && initialContent.length > 0 ? initialContent : loadQuickNoteDraft();
    // 标记「下一次持久化跳过」：本次提交里持久化 effect 拿到的仍是旧 content。
    skipNextDraftPersistRef.current = true;
    // 直接把恢复值落库，覆盖「setContent 未触发重渲染」的情况。
    saveQuickNoteDraft(restored);
    setContent(restored);
    setSelectedCategory(extractNoteCategory(restored));
    setAttachmentIds([]);
    setLocalAttachments([]);
    setIsArchived(false);
    setEditError(null);
  }, [open, mode, initialContent]);

  // 持续保存草稿（仅在新建模式；编辑模式的内容属于已存在的记录，不应污染草稿）。
  useEffect(() => {
    if (!open || mode !== 'create') {
      skipNextDraftPersistRef.current = false;
      return;
    }
    // 消费并清零：跳过紧随恢复之后的那一次陈旧写入。
    if (skipNextDraftPersistRef.current) {
      skipNextDraftPersistRef.current = false;
      return;
    }
    saveQuickNoteDraft(content);
  }, [open, mode, content]);

  const createMutation = useMutation({
    mutationFn: (markdown: string) =>
      createQuickNote({
        contentMarkdown: markdown,
        source,
        attachmentIds: attachmentIds.length > 0 ? attachmentIds : undefined,
      }),
    onSuccess: () => {
      // 已提交的内容不再作为草稿恢复（#300）。
      skipNextDraftPersistRef.current = false;
      clearQuickNoteDraft();
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      onSaved();
      onClose();
    },
    onError: () => {
      setEditError('创建失败，请稍后重试。');
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({
      id,
      markdown,
      attIds,
    }: {
      id: string;
      markdown: string;
      attIds: string[];
    }) => updateQuickNote(id, { contentMarkdown: markdown, attachmentIds: attIds }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
      onSaved();
      onClose();
    },
    onError: () => {
      setEditError('保存失败，请稍后重试。');
    },
  });

  const attachmentMutation = useMutation({
    mutationFn: ({
      id,
      markdown,
      attIds,
    }: {
      id: string;
      markdown: string;
      attIds: string[];
    }) => updateQuickNote(id, { contentMarkdown: markdown, attachmentIds: attIds }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
      setEditError(null);
    },
    onError: () => {
      setEditError('附件操作失败，请稍后重试。');
    },
  });

  const archiveMutation = useMutation({
    mutationFn: archiveQuickNote,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
      setIsArchived(true);
    },
    onError: () => {
      setEditError('归档失败，请稍后重试。');
    },
  });

  const restoreMutation = useMutation({
    mutationFn: (id: string) => restoreQuickNote(id, 'inbox'),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
      setIsArchived(false);
    },
    onError: () => {
      setEditError('恢复失败，请稍后重试。');
    },
  });

  const deleteMutation = useMutation({
    mutationFn: deleteQuickNote,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      onSaved();
      onClose();
    },
    onError: () => {
      setEditError('删除失败，请稍后重试。');
    },
  });

  const processMutation = useMutation({
    mutationFn: processQuickNote,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quick-notes'] });
      queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
    },
    onError: () => {
      setEditError('处理失败');
    },
  });

  const busy =
    createMutation.isPending ||
    updateMutation.isPending ||
    archiveMutation.isPending ||
    restoreMutation.isPending ||
    deleteMutation.isPending ||
    processMutation.isPending ||
    attachmentMutation.isPending;

  function handleSave() {
    const trimmed = content.trim();
    if (!trimmed) return;

    if (mode === 'create') {
      createMutation.mutate(content);
    } else if (noteId) {
      updateMutation.mutate({ id: noteId, markdown: content, attIds: attachmentIds });
    }
  }

  function handleArchiveToggle() {
    if (!noteId) return;
    if (isArchived) {
      restoreMutation.mutate(noteId);
    } else {
      archiveMutation.mutate(noteId);
    }
  }

  function handleDelete() {
    if (!noteId) return;
    const confirmed = window.confirm('确定删除这条快速记录？此操作无法撤销。');
    if (confirmed) {
      deleteMutation.mutate(noteId);
    }
  }

  async function handleFileUpload(file: File) {
    if (!noteId && mode === 'edit') return;
    if (mode === 'create') {
      // Upload then associate on save
      try {
        const result = await uploadQuickNoteAttachment(file);
        setAttachmentIds(prev => [...prev, result.id]);
        setLocalAttachments(prev => [
          ...prev,
          {
            id: result.id,
            fileName: result.fileName,
            contentType: result.contentType,
            sizeBytes: result.sizeBytes,
            downloadUrl: result.downloadUrl,
            previewUrl: result.previewUrl,
            createdAt: new Date().toISOString(),
          },
        ]);
      } catch {
        setEditError('附件上传失败');
      }
      return;
    }

    // Edit mode: upload then update note immediately
    let result: Awaited<ReturnType<typeof uploadQuickNoteAttachment>>;
    try {
      result = await uploadQuickNoteAttachment(file);
    } catch {
      setEditError('附件上传失败');
      return;
    }
    const newIds = [...attachmentIds, result.id];
    try {
      await attachmentMutation.mutateAsync({ id: noteId!, markdown: content, attIds: newIds });
    } catch {
      return;
    }
    setAttachmentIds(newIds);
    setLocalAttachments(prev => [
      ...prev,
      {
        id: result.id,
        fileName: result.fileName,
        contentType: result.contentType,
        sizeBytes: result.sizeBytes,
        downloadUrl: result.downloadUrl,
        previewUrl: result.previewUrl,
        createdAt: new Date().toISOString(),
      },
    ]);
    queryClient.invalidateQueries({ queryKey: ['quick-notes', 'detail', noteId] });
  }

  function handleFileInputChange(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (file) {
      void handleFileUpload(file);
    }
    event.target.value = '';
  }

  function handleDrop(event: React.DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setDragOver(false);
    const file = event.dataTransfer.files?.[0];
    if (file) {
      void handleFileUpload(file);
    }
  }

  function handleDragOver(event: React.DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setDragOver(true);
  }

  function handleDragLeave(event: React.DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setDragOver(false);
  }

  function removeAttachment(attId: string) {
    const newIds = attachmentIds.filter(id => id !== attId);
    setAttachmentIds(newIds);
    setLocalAttachments(prev => prev.filter(a => a.id !== attId));

    if (mode === 'edit' && noteId) {
      attachmentMutation.mutate({ id: noteId, markdown: content, attIds: newIds });
    }
  }



  if (!open) return null;

  const isLoading = mode === 'edit' && detailQuery.isLoading;

  return (
    <div
      className="fixed inset-0 z-50 bg-zinc-950/40 backdrop-blur-xs animate-backdrop"
      onClick={onClose}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="quick-note-dialog-title"
        tabIndex={-1}
        ref={dialogRef}
        onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
        style={{
          left: position.x,
          top: position.y,
          width: 'min(672px, calc(100vw - 24px))',
          maxHeight: 'calc(100vh - 24px)',
        }}
        className="absolute flex flex-col rounded-xl border border-zinc-200 bg-white shadow-dialog animate-dialog"
        onClick={e => e.stopPropagation()}
      >
        <header
          className="flex shrink-0 cursor-move touch-none select-none items-center justify-between border-b border-zinc-200 px-5 py-4"
          onPointerDown={handlePointerDown}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
          onPointerCancel={handlePointerUp}
          onLostPointerCapture={handleLostPointerCapture}
        >
          <h2 id="quick-note-dialog-title" className="text-base font-semibold text-zinc-900">
            {mode === 'edit' ? '编辑记录' : '写闪念'}
          </h2>
          <button
            type="button"
            onClick={onClose}
            onPointerDown={event => event.stopPropagation()}
            aria-label="关闭编辑卡片"
            className="rounded-lg p-1 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-600"
          >
            <X className="h-4 w-4" />
          </button>
        </header>

        {isLoading ? (
          <div className="flex items-center justify-center py-20 text-sm text-zinc-500">
            加载中...
          </div>
        ) : (
          <div className="max-h-[75vh] space-y-4 overflow-y-auto px-5 py-4">
            {editError && (
              <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                {editError}
              </div>
            )}

            <div className="flex items-center justify-between gap-2 pb-1">
              <div className="flex items-center gap-2">
                <span className="text-xs font-medium text-zinc-500">记录分类：</span>
                <select
                  value={selectedCategory}
                  onChange={e => handleCategoryChange(e.target.value)}
                  className="rounded-lg border border-zinc-200 bg-white px-2 py-1 text-xs text-zinc-800 outline-none focus:border-zinc-400"
                >
                  <option value="灵感">💡 灵感</option>
                  <option value="学业">🎓 学业</option>
                  <option value="开发">💻 开发</option>
                  <option value="运维">🛠️ 运维</option>
                  <option value="生活">🌱 生活</option>
                  <option value="闪念">📝 闪念</option>
                </select>
              </div>
              <span className="text-[11px] text-zinc-400 font-mono">支持 Markdown 语法</span>
            </div>

            <QuickNoteEditor value={content} onChange={setContent} minHeight={200} />

            <div className="flex items-center gap-4">
              <label className="flex cursor-pointer items-center gap-2 text-sm text-zinc-700">
                <input
                  type="checkbox"
                  checked={isArchived}
                  onChange={handleArchiveToggle}
                  disabled={busy || mode === 'create'}
                  className="rounded border-zinc-300 text-zinc-900 focus:ring-zinc-500"
                />
                已归档
              </label>
              {mode === 'edit' && selected && selected.status !== 'processed' && selected.status !== 'archived' && (
                <button
                  type="button"
                  onClick={() => {
                    if (noteId) {
                      processMutation.mutate(noteId);
                    }
                  }}
                  disabled={busy}
                  className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-semibold text-emerald-700 hover:bg-emerald-100 disabled:opacity-60"
                >
                  {processMutation.isPending ? '处理中...' : '标记处理'}
                </button>
              )}
            </div>

            {/* Attachments */}
            <div>
              <h3 className="mb-2 text-sm font-semibold text-zinc-700">附件</h3>

              {localAttachments.length > 0 && (
                <div className="mb-2 space-y-1">
                  {localAttachments.map(att => (
                    <div key={att.id} className="flex items-center gap-2 rounded-md border border-zinc-200 px-3 py-2 text-sm">
                      <File className="h-4 w-4 shrink-0 text-zinc-400" />
                      <span className="min-w-0 flex-1 truncate text-zinc-700">{att.fileName}</span>
                      <span className="shrink-0 text-xs text-zinc-400">{formatFileSize(att.sizeBytes)}</span>
                      <button
                        type="button"
                        onClick={() => removeAttachment(att.id)}
                        disabled={busy}
                        className="shrink-0 rounded p-0.5 text-zinc-400 hover:text-red-600 disabled:opacity-50"
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  ))}
                </div>
              )}

              <label className="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-dashed border-zinc-300 px-3 py-2 text-sm text-zinc-500 hover:border-blue-300 hover:text-blue-600">
                <Upload className="h-4 w-4" />
                上传附件
                <input type="file" hidden onChange={handleFileInputChange} />
              </label>

              <div
                className={`mt-2 rounded-lg border-2 border-dashed p-4 text-center text-sm transition-colors ${
                  dragOver
                    ? 'border-blue-400 bg-blue-50 text-blue-600'
                    : 'border-zinc-200 text-zinc-400'
                }`}
                onDragOver={handleDragOver}
                onDragLeave={handleDragLeave}
                onDrop={handleDrop}
              >
                拖拽文件到此处上传
              </div>
            </div>
          </div>
        )}

        <footer className="flex shrink-0 items-center justify-between border-t border-zinc-200 px-5 py-4">
          {mode === 'edit' && (
            <button
              type="button"
              onClick={handleDelete}
              disabled={busy}
              className="text-sm text-red-600 hover:text-red-800 disabled:opacity-50"
            >
              删除
            </button>
          )}
          {mode === 'create' && <div />}
          <div className="flex gap-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-zinc-200 px-4 py-2 text-sm text-zinc-600 hover:bg-zinc-50"
            >
              取消
            </button>
            <button
              type="button"
              onClick={handleSave}
              disabled={!content.trim() || busy}
              className="rounded-lg bg-zinc-900 px-4 py-2 text-sm text-white hover:bg-zinc-800 disabled:opacity-50"
            >
              {busy ? '保存中...' : '保存'}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
