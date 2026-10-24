import { useEffect, useMemo, useState } from 'react';

import { downloadQuickNoteAttachmentBlob } from '../../api/quickNotes';
import type { QuickNoteAttachment } from '../../types';
import QuickNoteEditor from './QuickNoteEditor';
import {
  extractQuickNoteAttachmentIds,
  rewriteQuickNoteAttachmentUrls,
} from './quickNoteAttachmentBlobUrls';

export interface QuickNoteMarkdownPreviewProps {
  markdown: string;
  attachments?: QuickNoteAttachment[];
  minHeight?: number;
}

export default function QuickNoteMarkdownPreview({
  markdown,
  attachments = [],
  minHeight = 120,
}: QuickNoteMarkdownPreviewProps) {
  const [objectUrlsByAttachmentId, setObjectUrlsByAttachmentId] = useState<Map<string, string>>(() => new Map());

  const referencedAttachmentIds = useMemo(() => extractQuickNoteAttachmentIds(markdown), [markdown]);
  const markdownWithObjectUrls = useMemo(
    () => rewriteQuickNoteAttachmentUrls(markdown || ' ', objectUrlsByAttachmentId),
    [markdown, objectUrlsByAttachmentId],
  );

  useEffect(() => {
    let cancelled = false;
    const createdUrls: string[] = [];

    async function loadAttachmentObjectUrls() {
      const entries = await Promise.all(
        referencedAttachmentIds.map(async id => {
          const blob = await downloadQuickNoteAttachmentBlob(id);
          const objectUrl = URL.createObjectURL(blob);
          createdUrls.push(objectUrl);
          return [id, objectUrl] as const;
        }),
      );

      if (cancelled) {
        createdUrls.forEach(url => URL.revokeObjectURL(url));
        return;
      }

      setObjectUrlsByAttachmentId(new Map(entries));
    }

    setObjectUrlsByAttachmentId(current => {
      current.forEach(url => URL.revokeObjectURL(url));
      return new Map();
    });

    if (referencedAttachmentIds.length > 0) {
      void loadAttachmentObjectUrls();
    }

    return () => {
      cancelled = true;
      createdUrls.forEach(url => URL.revokeObjectURL(url));
    };
  }, [referencedAttachmentIds]);

  async function downloadAttachment(attachment: QuickNoteAttachment) {
    const blob = await downloadQuickNoteAttachmentBlob(attachment.id);
    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');

    link.href = objectUrl;
    link.download = attachment.fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(objectUrl);
  }

  return (
    <div className="space-y-3">
      <QuickNoteEditor value={markdownWithObjectUrls} minHeight={minHeight} readOnly />
      {attachments.length > 0 && (
        <div className="border-t border-slate-100 pt-3">
          <h3 className="text-xs font-semibold text-slate-500">引用附件</h3>
          <ul className="mt-2 space-y-1">
            {attachments.map(attachment => (
              <li key={attachment.id}>
                <button
                  type="button"
                  className="break-all text-left text-sm font-medium text-blue-600 hover:underline"
                  onClick={() => void downloadAttachment(attachment)}
                >
                  {attachment.fileName}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
