import { useEffect, useId, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { importPcBrowserHistory } from '../../api/pcBrowserSite';
import type { PcBrowserImportMode, PcBrowserImportResult } from '../../api/pcBrowserSite';
import { formatBrowserCount } from './pcBrowserFormatting';

interface Props {
  open: boolean;
  onClose: () => void;
  /** 导入成功后回调（父组件用于刷新汇总/列表数据） */
  onSuccess: () => void;
}

const MODE_OPTIONS: Array<{ value: PcBrowserImportMode; label: string; description: string }> = [
  { value: 'overwrite', label: '覆盖', description: '清除已有浏览器数据后导入' },
  { value: 'add', label: '累加', description: '在已有浏览器数据上追加' },
];

/** 导入历史数据弹窗：选择 time-tracker-4-browser 导出的 JSON 或备份 markdown 文件，提交到后端解析入库。 */
export default function ImportDialog({ open, onClose, onSuccess }: Props) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);
  const [fileName, setFileName] = useState('');
  const [content, setContent] = useState('');
  const [mode, setMode] = useState<PcBrowserImportMode>('overwrite');
  const [result, setResult] = useState<PcBrowserImportResult | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const importMutation = useMutation({
    mutationFn: importPcBrowserHistory,
    onSuccess: data => {
      setResult(data);
      setErrorMessage(null);
      onSuccess();
    },
    onError: error => {
      setErrorMessage(error instanceof Error ? error.message : '导入失败，请稍后重试');
      setResult(null);
    },
  });

  useEffect(() => {
    if (!open) return;
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    dialogRef.current?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
      previouslyFocusedRef.current = null;
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    function handleKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [open, onClose]);

  if (!open) return null;

  function handleFileChange(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (!file) {
      setFileName('');
      setContent('');
      return;
    }
    file.text().then(text => {
      setFileName(file.name);
      setContent(text);
    }).catch(() => {
      setFileName('');
      setContent('');
      setErrorMessage('读取文件失败，请重试');
    });
  }

  function handleSubmit() {
    if (!content.trim() || importMutation.isPending) return;
    setErrorMessage(null);
    setResult(null);
    importMutation.mutate({ content, mode });
  }

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center px-4 py-8">
      <div className="fixed inset-0 bg-slate-950/40 backdrop-blur-sm" onClick={onClose} />
      <section
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className="relative flex max-h-full w-full max-w-[560px] flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl outline-none"
      >
        <header className="flex shrink-0 items-center justify-between border-b border-slate-100 px-5 py-4">
          <h3 id={titleId} className="text-sm font-semibold text-slate-900">
            导入历史数据
          </h3>
          <button
            type="button"
            onClick={onClose}
            className="flex h-8 w-8 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700"
            aria-label="关闭"
          >
            ✕
          </button>
        </header>

        <div className="overflow-y-auto px-5 py-4">
          <p className="text-xs leading-relaxed text-slate-500">
            在 time-tracker 插件的记录页导出 JSON，或在备份功能生成备份 markdown 文件后导入。
          </p>

          <label className="mt-4 block text-xs font-semibold text-slate-500">
            数据文件（.json 或 .md）
            <input
              type="file"
              accept=".json,.md,application/json,text/markdown,text/plain"
              onChange={handleFileChange}
              className="mt-1 block w-full cursor-pointer rounded-lg border border-slate-200 bg-white text-sm text-slate-700 file:mr-3 file:cursor-pointer file:rounded-md file:border-0 file:bg-blue-50 file:px-3 file:py-1.5 file:text-xs file:font-medium file:text-blue-700 hover:file:bg-blue-100"
            />
          </label>
          {fileName && (
            <p className="mt-1.5 truncate text-[11px] text-slate-400">已选择：{fileName}</p>
          )}

          <fieldset className="mt-4">
            <legend className="text-xs font-semibold text-slate-500">导入模式</legend>
            <div className="mt-1.5 space-y-2">
              {MODE_OPTIONS.map(option => (
                <label
                  key={option.value}
                  className={`flex cursor-pointer items-start gap-2 rounded-lg border px-3 py-2 transition-colors ${
                    mode === option.value
                      ? 'border-blue-300 bg-blue-50'
                      : 'border-slate-200 bg-white hover:border-blue-200'
                  }`}
                >
                  <input
                    type="radio"
                    name="browser-import-mode"
                    value={option.value}
                    checked={mode === option.value}
                    onChange={() => setMode(option.value)}
                    className="mt-0.5 h-3.5 w-3.5"
                  />
                  <span className="min-w-0">
                    <span className="block text-sm font-medium text-slate-800">{option.label}</span>
                    <span className="block text-[11px] text-slate-400">{option.description}</span>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>

          {result && (
            <div className="mt-4 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2.5 text-xs text-emerald-700">
              <p className="font-semibold">导入成功（{result.format}）</p>
              <p className="mt-1 tabular-nums">
                共 {formatBrowserCount(result.rows)} 条记录 · {formatBrowserCount(result.dates)} 个日期 ·{' '}
                {formatBrowserCount(result.hosts)} 个站点 · 跳过 {formatBrowserCount(result.skipped)} 条
              </p>
            </div>
          )}

          {errorMessage && (
            <p className="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-600">
              {errorMessage}
            </p>
          )}
        </div>

        <footer className="flex shrink-0 items-center justify-end gap-2 border-t border-slate-100 px-5 py-3">
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:bg-slate-50"
          >
            关闭
          </button>
          <button
            type="button"
            onClick={handleSubmit}
            disabled={!content.trim() || importMutation.isPending}
            className="pim-button-primary rounded-lg px-3 py-1.5 text-xs font-medium disabled:cursor-not-allowed disabled:opacity-50"
          >
            {importMutation.isPending ? '导入中…' : '开始导入'}
          </button>
        </footer>
      </section>
    </div>
  );
}
