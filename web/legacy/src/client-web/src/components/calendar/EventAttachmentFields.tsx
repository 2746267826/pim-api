import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Paperclip, X } from 'lucide-react';
import { getFileItems } from '../../api/files';
import type { EventFormValue } from '../../utils/eventDraft';

interface EventAttachmentFieldsProps {
  form: EventFormValue;
  onChange: (patch: Partial<EventFormValue>) => void;
  disabled?: boolean;
  providerReadOnly?: boolean;
}

const inputClass = 'w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-500';

export default function EventAttachmentFields({
  form,
  onChange,
  disabled = false,
  providerReadOnly = false,
}: EventAttachmentFieldsProps) {
  const [selectedFileId, setSelectedFileId] = useState('');

  const { data: fileItems, isLoading: filesLoading, isError: filesError } = useQuery({
    queryKey: ['files', 'items', '/'],
    queryFn: () => getFileItems({ path: '/' }),
    enabled: !disabled && !providerReadOnly,
  });

  function addSelectedFile() {
    if (!selectedFileId) return;
    const file = (fileItems?.result.items ?? []).find(item => item.id === selectedFileId);
    if (!file) return;
    const refs = [...(form.attachmentReferences ?? [])];
    if (refs.some(ref => ref.kind === 'pimFile' && ref.id === file.id)) return;
    refs.push({
      kind: 'pimFile',
      id: file.id,
      name: file.name,
      contentType: file.mimeType ?? null,
      size: file.size,
      canDownload: true,
    });
    onChange({ attachmentReferences: refs });
    setSelectedFileId('');
  }

  function removeReference(index: number) {
    const refs = [...(form.attachmentReferences ?? [])];
    refs.splice(index, 1);
    onChange({ attachmentReferences: refs });
  }

  function formatSize(size: number | null | undefined): string {
    if (size === null || size === undefined) return '';
    if (size < 1024) return `${size} B`;
    if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
    return `${(size / (1024 * 1024)).toFixed(1)} MB`;
  }

  const files = fileItems?.result.items ?? [];

  return (
    <div className="space-y-3">
      {(form.attachmentReferences ?? []).length === 0 && (
        <p className="text-xs text-slate-400">暂无附件</p>
      )}
      <ul className="space-y-2">
        {(form.attachmentReferences ?? []).map((ref, index) => (
          <li key={`${ref.kind}-${ref.id}-${index}`} className="event-attachment-row" data-attachment-row>
            <Paperclip size={14} className="shrink-0 text-slate-400" />
            <span className="min-w-0 flex-1 truncate">{ref.name}</span>
            {ref.size != null && <span className="shrink-0 text-xs text-slate-400">{formatSize(ref.size)}</span>}
            {ref.kind === 'outlook' && (
              <span className="shrink-0 text-xs text-slate-400">Outlook 附件（只读）</span>
            )}
            {ref.kind === 'pimFile' && (
              <button
                type="button"
                aria-label={`移除附件 ${ref.name}`}
                title="移除附件"
                onClick={() => removeReference(index)}
                disabled={disabled}
                className="event-attachment-remove"
              >
                <X size={14} />
              </button>
            )}
          </li>
        ))}
      </ul>

      {!providerReadOnly && (
        <div className="flex flex-wrap items-center gap-2">
          <select
            aria-label="选择附件文件"
            value={selectedFileId}
            onChange={e => setSelectedFileId(e.target.value)}
            disabled={disabled || filesLoading || filesError}
            className={`${inputClass} min-w-0 flex-1`}
          >
            <option value="">请选择文件</option>
            {files.map(file => (
              <option key={file.id} value={file.id}>{file.name}</option>
            ))}
          </select>
          <button
            type="button"
            onClick={addSelectedFile}
            disabled={disabled || !selectedFileId}
            className="pim-button-secondary px-2.5 py-1.5 text-xs disabled:opacity-50"
          >
            添加附件
          </button>
        </div>
      )}
      {filesLoading && <p className="text-xs text-slate-400">正在加载文件...</p>}
      {filesError && (
        <p className="text-xs text-red-600">文件列表加载失败，请稍后重试。</p>
      )}
      {!filesLoading && !filesError && !providerReadOnly && files.length === 0 && (
        <p className="text-xs text-slate-400">暂无可用文件</p>
      )}
    </div>
  );
}
