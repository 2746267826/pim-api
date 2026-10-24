import { useCallback, useEffect, useState } from 'react';
import { Download, FileText, History, Loader2, Save } from 'lucide-react';
import {
  apiDownloadBlob,
  getDownloadUrl,
  getOneDrivePreviewUrl,
  getOneDriveSnapshots,
  getOneDriveText,
  restoreOneDriveSnapshot,
  saveOneDriveText,
} from '../../api/files';
import type { FileItem, FileTextSnapshot } from '../../types';

export interface OneDrivePreviewPaneProps {
  item: FileItem | null;
  onToast?: (message: string) => void;
}

const TEXT_EDITABLE_EXTENSIONS = ['.txt', '.md', '.markdown', '.json', '.csv', '.log', '.yml', '.yaml', '.xml'];

export function isTextEditable(item: FileItem): boolean {
  const mime = item.mimeType ?? '';
  if (mime.startsWith('text/')) return true;
  if (['application/json', 'application/xml', 'application/yaml'].includes(mime)) return true;
  const name = item.name.toLowerCase();
  return TEXT_EDITABLE_EXTENSIONS.some(ext => name.endsWith(ext));
}

export function isOfficeDocument(item: FileItem): boolean {
  const mime = item.mimeType ?? '';
  return mime.includes('word') || mime.includes('sheet') || mime.includes('presentation')
    || /\.(docx?|xlsx?|pptx?)$/i.test(item.name);
}

/** 带鉴权的内容 blob（302 直链由 fetch 内部跟随），组件卸载时回收。 */
export function useAuthedContentBlob(path: string | null, enabled: boolean) {
  const [state, setState] = useState<{ blobUrl: string | null; error: string | null }>({
    blobUrl: null,
    error: null,
  });

  useEffect(() => {
    if (!path || !enabled) return;
    let objectUrl: string | null = null;
    let cancelled = false;
    apiDownloadBlob(path)
      .then((blob: Blob) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setState({ blobUrl: objectUrl, error: null });
      })
      .catch((e: unknown) => {
        if (!cancelled) setState({ blobUrl: null, error: e instanceof Error ? e.message : '加载失败' });
      });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path, enabled]);

  // loading 与关闭态都在渲染期派生，effect 内不做同步 setState
  return {
    blobUrl: enabled ? state.blobUrl : null,
    loading: enabled && state.blobUrl === null && state.error === null,
    error: enabled ? state.error : null,
  };
}

/** 方案 A 右栏：预览面板（图片/PDF/Office/文本 + 元数据 + 操作）。 */
export default function OneDrivePreviewPane({ item, onToast }: OneDrivePreviewPaneProps) {
  if (!item) {
    return (
      <div className="hidden w-[340px] shrink-0 flex-col border-l border-[var(--pim-border)] bg-[var(--pim-surface)] lg:flex" data-testid="preview-empty">
        <div className="flex flex-1 items-center justify-center p-6 text-center text-sm text-[var(--pim-text-muted)]">
          选择一个文件查看预览
        </div>
      </div>
    );
  }
  return <PreviewBody key={item.id} item={item} onToast={onToast} />;
}

function PreviewBody({ item, onToast }: { item: FileItem; onToast?: (message: string) => void }) {
  const isImage = item.mimeType?.startsWith('image/') ?? false;
  const isPdf = item.mimeType?.includes('pdf') || item.name.toLowerCase().endsWith('.pdf');
  const editable = isTextEditable(item);
  const office = isOfficeDocument(item);

  return (
    <div className="flex w-full shrink-0 flex-col border-[var(--pim-border)] bg-[var(--pim-surface)] md:w-[340px] md:border-l" data-testid="preview-pane">
      <div className="border-b border-[var(--pim-border)] px-4 py-3 text-sm font-medium" title={item.name}>{item.name}</div>
      <PreviewBodyInner item={item} isImage={isImage} isPdf={isPdf} editable={editable} office={office} onToast={onToast} />
    </div>
  );
}

function PreviewBodyInner({
  item,
  isImage,
  isPdf,
  editable,
  office,
  onToast,
}: {
  item: FileItem;
  isImage: boolean;
  isPdf: boolean;
  editable: boolean;
  office: boolean;
  onToast?: (message: string) => void;
}) {
  const [mode, setMode] = useState<'preview' | 'edit'>('preview');
  // 图片走缩略图端点（§8）：原图可能几十 MB，缩略图由 Graph 生成
  const image = useAuthedContentBlob(item ? `/files/items/${item.id}/thumbnail?size=large` : null, isImage);
  const pdf = useAuthedContentBlob(item ? `/files/items/${item.id}/content` : null, isPdf && mode === 'preview');

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="min-h-[170px] border-b border-[var(--pim-border)] p-4">
        {isImage && (
          <ImagePreview blobUrl={image.blobUrl} loading={image.loading} error={image.error} />
        )}
        {isPdf && (
          <PdfPreview blobUrl={pdf.blobUrl} loading={pdf.loading} error={pdf.error} />
        )}
        {office && <OfficePreview itemId={item.id} />}
        {!isImage && !isPdf && !office && !editable && (
          <div className="flex h-[150px] items-center justify-center text-sm text-[var(--pim-text-muted)]">
            <FileText size={28} className="mr-2" /> 此类型暂不支持预览
          </div>
        )}
        {editable && mode === 'preview' && !isImage && !isPdf && !office && (
          <div className="flex h-[150px] items-center justify-center text-sm text-[var(--pim-text-muted)]">
            <FileText size={28} className="mr-2" /> 文本文件，可点击下方「编辑文本」
          </div>
        )}
      </div>

      <MetaBlock item={item} />

      <div className="flex flex-col gap-2 p-4">
        <div className="grid grid-cols-2 gap-2">
          <button
            type="button"
            className="pim-button-secondary text-sm"
            data-testid="preview-download"
            onClick={() => {
              // REQ-20 / AC-20.1（验收 F-3 的同源问题）：这里一度用 apiDownloadBlob('/content')
              // 跟随 302 把**整个文件**读进页面内存再转 blob，大文件会直接崩标签页。
              // 改为取 JSON 直链后交给浏览器打开，页面不搬字节。
              void (async () => {
                try {
                  const url = await getDownloadUrl(item.id);
                  if (!url) throw new Error('未取到下载直链');
                  window.open(url, '_blank', 'noopener,noreferrer');
                } catch (err: unknown) {
                  onToast?.(err instanceof Error ? err.message : '下载失败');
                }
              })();
            }}
          >
            <span className="inline-flex items-center gap-1.5"><Download size={14} /> 下载</span>
          </button>
          <button
            type="button"
            className="pim-button-secondary text-sm"
            onClick={() => {
              setMode(m => (m === 'edit' ? 'preview' : 'edit'));
            }}
            disabled={!editable}
          >
            <span className="inline-flex items-center gap-1.5"><FileText size={14} /> {mode === 'edit' ? '退出编辑' : '编辑文本'}</span>
          </button>
        </div>
        {mode === 'edit' && <TextEditor itemId={item.id} onToast={onToast} />}
      </div>
    </div>
  );
}

function ImagePreview({ blobUrl, loading, error }: { blobUrl: string | null; loading: boolean; error: string | null }) {
  if (loading) return <CenterLoading label="加载缩略图…" />;
  if (error) return <CenterMessage text={error} />;
  if (!blobUrl) return <CenterMessage text="暂无预览" />;
  return (
    <div className="flex items-center justify-center overflow-hidden rounded-xl bg-[var(--pim-surface-muted)]" style={{ minHeight: 170 }}>
      <img src={blobUrl} alt="文件预览" className="max-h-[220px] w-full object-contain" data-testid="image-preview" />
    </div>
  );
}

function PdfPreview({ blobUrl, loading, error }: { blobUrl: string | null; loading: boolean; error: string | null }) {
  if (loading) return <CenterLoading label="加载 PDF…" />;
  if (error) return <CenterMessage text={error} />;
  if (!blobUrl) return <CenterMessage text="暂无预览" />;
  return <iframe src={blobUrl} title="PDF 预览" className="h-[220px] w-full rounded-xl border border-[var(--pim-border)]" data-testid="pdf-preview" />;
}

function OfficePreview({ itemId }: { itemId: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getOneDrivePreviewUrl(itemId)
      .then(link => {
        if (!cancelled) setUrl(link);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : '预览不可用');
      });
    return () => {
      cancelled = true;
    };
  }, [itemId]);

  if (error) return <CenterMessage text="Office 预览不可用，可在 OneDrive 中打开" />;
  if (!url) return <CenterLoading label="获取预览…" />;
  return <iframe src={url} title="Office 预览" className="h-[220px] w-full rounded-xl border border-[var(--pim-border)]" data-testid="office-preview" />;
}

function TextEditor({ itemId, onToast }: { itemId: string; onToast?: (message: string) => void }) {
  const [content, setContent] = useState<string | null>(null);
  const [snapshots, setSnapshots] = useState<FileTextSnapshot[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(() => {
    getOneDriveText(itemId)
      .then(text => setContent(text.content))
      .catch((e: unknown) => setError(e instanceof Error ? e.message : '读取失败'));
    getOneDriveSnapshots(itemId)
      .then(setSnapshots)
      .catch(() => setSnapshots([]));
  }, [itemId]);

  useEffect(() => {
    reload();
  }, [reload]);

  const handleSave = async () => {
    if (content === null) return;
    setSaving(true);
    try {
      await saveOneDriveText(itemId, content);
      onToast?.('已保存到 OneDrive');
      setSnapshots(await getOneDriveSnapshots(itemId));
    } catch (e) {
      onToast?.(e instanceof Error ? e.message : '保存失败');
    } finally {
      setSaving(false);
    }
  };

  const handleRestore = async (snapshotId: string) => {
    try {
      await restoreOneDriveSnapshot(itemId, snapshotId);
      const text = await getOneDriveText(itemId);
      setContent(text.content);
      setSnapshots(await getOneDriveSnapshots(itemId));
      onToast?.('已恢复历史版本');
    } catch (e) {
      onToast?.(e instanceof Error ? e.message : '恢复失败');
    }
  };

  if (error) return <CenterMessage text={error} />;
  if (content === null) return <CenterLoading label="读取文本…" />;

  return (
    <div className="space-y-2" data-testid="text-editor">
      <textarea
        className="h-40 w-full rounded-lg border border-[var(--pim-border)] p-2 font-mono text-xs"
        value={content}
        onChange={e => setContent(e.target.value)}
        aria-label="文本内容"
      />
      <button type="button" className="pim-button-primary w-full text-sm" onClick={handleSave} disabled={saving}>
        <span className="inline-flex items-center gap-1.5">
          {saving ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />} 保存到 OneDrive
        </span>
      </button>
      {snapshots.length > 0 && (
        <div className="rounded-lg border border-[var(--pim-border)] p-2">
          <div className="mb-1 flex items-center gap-1 text-xs text-[var(--pim-text-muted)]">
            <History size={12} /> 历史版本（最近 {snapshots.length} 份）
          </div>
          <ul className="max-h-28 space-y-1 overflow-auto">
            {snapshots.map(snapshot => (
              <li key={snapshot.id} className="flex items-center justify-between gap-2 text-xs">
                <span className="min-w-0 truncate text-[var(--pim-text-muted)]">
                  {snapshot.content.slice(0, 16)} · {new Date(snapshot.createdAt).toLocaleString('zh-CN')} · {snapshot.reason === 'pre-restore' ? '恢复前' : '编辑前'}
                </span>
                <button type="button" className="shrink-0 text-[var(--pim-primary)] underline" onClick={() => handleRestore(snapshot.id)}>
                  恢复
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function MetaBlock({ item }: { item: FileItem }) {
  return (
    <div className="border-b border-[var(--pim-border)] px-4 py-2 text-xs text-[var(--pim-text-muted)]">
      <Row label="大小" value={item.size === null ? '—' : `${(item.size / 1024).toFixed(1)} KB`} />
      <Row label="修改时间" value={item.modifiedAt ? new Date(item.modifiedAt).toLocaleString('zh-CN') : '—'} />
      <Row label="路径" value={item.path} />
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-3 py-1">
      <span className="shrink-0">{label}</span>
      <span className="truncate text-right text-[var(--pim-text)]" title={value}>{value}</span>
    </div>
  );
}

function CenterLoading({ label }: { label: string }) {
  return (
    <div className="flex h-[150px] items-center justify-center gap-2 text-sm text-[var(--pim-text-muted)]">
      <Loader2 size={16} className="animate-spin" /> {label}
    </div>
  );
}

function CenterMessage({ text }: { text: string }) {
  return (
    <div className="flex h-[150px] items-center justify-center px-4 text-center text-sm text-[var(--pim-text-muted)]">
      {text}
    </div>
  );
}
