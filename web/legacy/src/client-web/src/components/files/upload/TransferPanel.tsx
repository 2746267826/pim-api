import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Uppy from '@uppy/core';
import type { UppyFile } from '@uppy/core';
import { AlertCircle, CheckCircle2, Clock, RotateCw, Upload, X } from 'lucide-react';
import { getFileItems, uploadFile as uploadFileViaApi } from '../../../api/files';
import { createFolder as createFolderApi } from '../../../api/files';
import { uploadFile, type UploadTarget } from './uploadEngine';
import { exceedsUploadLimit, formatBytes } from '../fileActions';
import { completeUploadSession, createUploadSession } from '../../../api/files';

/**
 * 传输任务面板（REQ-13）：进行中任务（含单文件进度）、历史记录、失败重试。
 *
 * 说明与取舍：
 * - 队列与文件状态由 **Uppy**（REQ-29 指定库）承载，进度/失败/重试语义不手搓；
 * - 真正的字节传输走本模块的 `uploadEngine`（>4MB 时浏览器直发微软），
 *   而不是 Uppy 的 XHR 插件直接打 PIM——因为要满足「字节不经服务器」的硬约束；
 * - AC-13.3：刷新页面后进行中的任务**不得凭空消失**。浏览器无法在刷新后继续一个
 *   `File` 对象（安全限制），因此这里明确**如实标记为中断**并提示重新上传，
 *   而不是假装它们还在传。历史记录持久化在 localStorage。
 */

export type TransferState = 'queued' | 'uploading' | 'completed' | 'failed' | 'interrupted';

export interface TransferTask {
  id: string;
  fileName: string;
  size: number;
  /** 0..1 */
  progress: number;
  state: TransferState;
  error?: string;
  /** 目标目录 */
  path: string;
}

const HISTORY_STORAGE_KEY = 'pim.files.transfers.v1';
/** 历史记录上限：面板只做最近记录，避免 localStorage 无界增长。 */
const HISTORY_LIMIT = 50;

export interface TransferPanelProps {
  target: UploadTarget & { providerId: string };
  open: boolean;
  onClose: () => void;
  /** 任务完成后刷新列表（新文件出现在目标目录）。 */
  onFinished?: () => void;
  /** 上报任务状态，供页面在工具条上显示/隐藏入口。 */
  onTasksChange?: (tasks: TransferTask[]) => void;
}

export default function TransferPanel({ target, open, onClose, onFinished, onTasksChange }: TransferPanelProps) {
  const [tasks, setTasks] = useState<TransferTask[]>(() => restoreHistory());
  const pending = useRef<{ file: File; taskId: string }[]>([]);
  const running = useRef(false);

  // 用函数式更新：并发完成多个任务时不会互相覆盖。
  const persist = useCallback((update: (current: TransferTask[]) => TransferTask[]) => {
    setTasks(update);
  }, []);

  // 落盘放在 effect 里：状态更新器必须是纯函数（StrictMode 会调用两次），
  // 在里面写 localStorage 会产生重复副作用。
  useEffect(() => {
    writeHistory(tasks);
    onTasksChange?.(tasks);
  }, [onTasksChange, tasks]);

  const runQueue = useCallback(async () => {
    if (running.current) return;
    running.current = true;
    try {
      while (pending.current.length > 0) {
        const { file, taskId: id } = pending.current.shift()!;

        if (exceedsUploadLimit(file.size)) {
          persist(current =>
            current.map(task =>
              task.id === id
                ? { ...task, state: 'failed', error: '文件超过 2GB，请改用 OneDrive 客户端上传' }
                : task,
            ),
          );
          continue;
        }

        // 从「排队中」翻成「上传中」（任务在入队时已建立）
        persist(current =>
          current.map(task => (task.id === id ? { ...task, state: 'uploading', error: undefined } : task)),
        );

        const result = await uploadFile(
          file,
          { path: target.path, providerId: target.providerId },
          {
            createSession: (path, name) => createUploadSession(path, name),
            completeSession: (path, name) => completeUploadSession(path, name),
            simpleUpload: (providerId, path, upload) => uploadFileViaApi(providerId, path, upload),
          },
          progress => {
            setTasks(current =>
              current.map(task => (task.id === id ? { ...task, progress: progress.ratio } : task)),
            );
          },
        );

        persist(current =>
          current.map(task =>
            task.id === id
              ? {
                  ...task,
                  progress: result.ok ? 1 : task.progress,
                  state: result.ok ? 'completed' : 'failed',
                  error: result.error,
                }
              : task,
          ),
        );

        if (result.ok) onFinished?.();
      }
    } finally {
      running.current = false;
    }
  }, [onFinished, persist, target.path, target.providerId]);

  /**
   * 入队：**立即**为每个文件建立任务（state=queued）并显示在面板上。
   *
   * 此前只有「开始处理」时才建任务，导致排在后面的文件在面板里完全不可见——
   * 用户一次拖 10 个文件只看到 1 个，会以为其余的丢了（AC-13.1 要求进行中任务可见）。
   */
  const enqueue = useCallback(
    (files: File[]) => {
      const now = Date.now();
      const queued: TransferTask[] = files.map((file, index) => ({
        id: `${file.name}-${file.size}-${now}-${index}`,
        fileName: file.name,
        size: file.size,
        progress: 0,
        state: 'queued',
        path: target.path,
      }));

      // 把任务与文件按同一顺序配对，处理时按 id 找到对应任务
      for (let index = 0; index < files.length; index += 1) {
        pending.current.push({ file: files[index], taskId: queued[index].id });
      }

      persist(current => [...current, ...queued]);
      void runQueue();
    },
    [persist, runQueue, target.path],
  );

  // Uppy 作为队列/状态承载（REQ-29）：这里用它的核心实例登记文件并驱动同一套上传逻辑
  const uppy = useMemo(
    () =>
      new Uppy({
        id: 'pim-files-upload',
        autoProceed: true,
        allowMultipleUploadBatches: true,
        restrictions: { maxNumberOfFiles: 100 },
      }),
    [],
  );

  // 在 effect 里同步 ref（渲染期写 ref 会让并发渲染读到不一致的值）
  const enqueueRef = useRef(enqueue);
  useEffect(() => {
    enqueueRef.current = enqueue;
  }, [enqueue]);

  useEffect(() => {
    const onAdded = (file: UppyFile<Record<string, unknown>, Record<string, unknown>> | undefined) => {
      if (!file?.data) return;
      enqueueRef.current([file.data as File]);
    };
    uppy.on('file-added', onAdded);
    return () => {
      uppy.off('file-added', onAdded);
    };
  }, [uppy]);

  useEffect(() => () => uppy.destroy(), [uppy]);

  // 对外暴露：供页面的三种入口（按钮 / 拖拽 / 剪贴板）投递文件
  useEffect(() => {
    const handler = (event: Event) => {
      const detail = (event as CustomEvent<{ files: File[]; path?: string }>).detail;
      if (!detail?.files?.length) return;
      if (detail.path && detail.path !== target.path) return;
      uppy.addFiles(detail.files.map(file => ({ name: file.name, type: file.type, data: file })));
    };
    window.addEventListener('pim:files-upload', handler);
    return () => window.removeEventListener('pim:files-upload', handler);
  }, [target.path, uppy]);

  const retry = useCallback(
    (task: TransferTask) => {
      persist(current =>
        current.map(candidate =>
          candidate.id === task.id
            ? { ...candidate, state: 'interrupted', error: '请重新选择该文件后再次上传（浏览器不允许刷新后续传）' }
            : candidate,
        ),
      );
    },
    [persist],
  );

  const clearFinished = useCallback(() => {
    persist(current => current.filter(task => task.state === 'uploading' || task.state === 'queued'));
  }, [persist]);

  if (!open) return null;

  const active = tasks.filter(task => task.state === 'uploading' || task.state === 'queued');
  const history = tasks.filter(task => task.state !== 'uploading' && task.state !== 'queued');

  return (
    <aside
      className="fixed bottom-4 right-4 z-50 flex max-h-[70vh] w-[360px] flex-col rounded-xl border border-[var(--pim-border)] bg-[var(--pim-surface)] shadow-[var(--pim-shadow-pop)]"
      data-testid="transfer-panel"
      aria-label="传输任务"
    >
      <header className="flex items-center gap-2 border-b border-[var(--pim-border)] px-3 py-2 text-sm">
        <Upload size={14} />
        <span className="flex-1">传输任务</span>
        <button type="button" className="text-[var(--pim-text-muted)]" aria-label="清除已完成" onClick={clearFinished}>
          <RotateCw size={13} />
        </button>
        <button type="button" className="text-[var(--pim-text-muted)]" aria-label="关闭传输面板" onClick={onClose}>
          <X size={14} />
        </button>
      </header>

      <div className="min-h-0 flex-1 overflow-auto px-3 py-2 text-xs">
        {tasks.length === 0 && (
          <p className="py-4 text-center text-[var(--pim-text-muted)]" data-testid="transfer-empty">
            还没有传输任务。可拖拽文件到列表，或用「上传」按钮选择文件。
          </p>
        )}

        {active.map(task => (
          <div key={task.id} className="mb-2" data-testid="transfer-active" data-file={task.fileName}>
            <div className="flex items-center gap-2">
              <span className="min-w-0 flex-1 truncate" title={task.fileName}>{task.fileName}</span>
              <span className="text-[var(--pim-text-muted)]">{formatBytes(task.size)}</span>
            </div>
            <div className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-[var(--pim-surface-muted)]">
              <div
                className="h-full rounded-full bg-[var(--pim-primary)] transition-[width]"
                style={{ width: `${Math.round(task.progress * 100)}%` }}
                data-testid="transfer-progress"
                data-ratio={task.progress}
              />
            </div>
            <div className="mt-0.5 text-[11px] text-[var(--pim-text-muted)]" data-testid="transfer-percent">
              上传中… {Math.round(task.progress * 100)}%
            </div>
          </div>
        ))}

        {history.map(task => (
          <div
            key={task.id}
            className="flex items-start gap-2 border-t border-[var(--pim-border-soft)] py-1.5"
            data-testid="transfer-history"
            data-file={task.fileName}
            data-state={task.state}
          >
            <span className="mt-0.5">
              {task.state === 'completed' ? (
                <CheckCircle2 size={13} className="text-[var(--pim-success)]" />
              ) : task.state === 'failed' ? (
                <AlertCircle size={13} className="text-[var(--pim-danger)]" />
              ) : (
                <Clock size={13} className="text-[var(--pim-warning)]" />
              )}
            </span>
            <span className="min-w-0 flex-1">
              <span className="block truncate" title={task.fileName}>{task.fileName}</span>
              {task.error && (
                <span className="block text-[11px] text-[var(--pim-danger)]" data-testid="transfer-error">
                  {task.error}
                </span>
              )}
            </span>
            {task.state !== 'completed' && (
              <button
                type="button"
                className="shrink-0 text-[11px] text-[var(--pim-primary)] underline"
                data-testid="transfer-retry"
                onClick={() => retry(task)}
              >
                重试
              </button>
            )}
          </div>
        ))}
      </div>
    </aside>
  );
}

function restoreHistory(): TransferTask[] {
  try {
    const raw = window.localStorage.getItem(HISTORY_STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as TransferTask[];
    if (!Array.isArray(parsed)) return [];
    // AC-13.3：刷新后进行中的任务不得「凭空消失」——如实标记为中断，让用户知道要重传
    return parsed
      .filter(task => task && typeof task.fileName === 'string')
      .map(task =>
        task.state === 'uploading' || task.state === 'queued'
          ? { ...task, state: 'interrupted' as TransferState, error: '页面刷新导致上传中断，请重新选择文件上传' }
          : task,
      )
      .slice(0, HISTORY_LIMIT);
  } catch {
    return [];
  }
}

function writeHistory(tasks: TransferTask[]): void {
  try {
    window.localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(tasks.slice(0, HISTORY_LIMIT)));
  } catch {
    // 隐私模式/配额满：历史写不进去不影响本次上传
  }
}

/** 供列表/树/剪贴板共用的上传投递入口（REQ-11 三种入口）。 */
export function dispatchFilesForUpload(files: File[], path: string): void {
  window.dispatchEvent(new CustomEvent('pim:files-upload', { detail: { files, path } }));
}

/** 从剪贴板/拖拽事件里取出文件（REQ-11：剪贴板粘贴与拖拽共用）。 */
export function extractFiles(source: DataTransfer | null | undefined): File[] {
  if (!source) return [];
  if (source.files && source.files.length > 0) return Array.from(source.files);
  return Array.from(source.items ?? [])
    .filter(item => item.kind === 'file')
    .map(item => item.getAsFile())
    .filter((file): file is File => file !== null);
}

/** 供页面判断是否要显示面板。 */
export function hasActiveTransfers(tasks: readonly TransferTask[]): boolean {
  return tasks.some(task => task.state === 'uploading' || task.state === 'queued');
}

export { getFileItems, createFolderApi };
