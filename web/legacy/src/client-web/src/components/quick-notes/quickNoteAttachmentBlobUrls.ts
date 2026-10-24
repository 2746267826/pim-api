import type { UpdateQuickNoteRequest } from '../../types';

const attachmentDownloadPattern = /\/api\/v1\/quick-notes\/attachments\/([0-9a-fA-F-]{36})\/download/g;
const exactAttachmentDownloadPattern = /^\/api\/v1\/quick-notes\/attachments\/([0-9a-fA-F-]{36})\/download$/;

export function extractQuickNoteAttachmentIds(markdown: string): string[] {
  const ids = new Set<string>();

  for (const match of markdown.matchAll(attachmentDownloadPattern)) {
    ids.add(match[1]);
  }

  return Array.from(ids);
}

export function rewriteQuickNoteAttachmentUrls(markdown: string, objectUrlsByAttachmentId: Map<string, string>) {
  attachmentDownloadPattern.lastIndex = 0;

  return markdown.replace(attachmentDownloadPattern, (url, id: string) => (
    objectUrlsByAttachmentId.get(id) ?? url
  ));
}

export function getQuickNoteAttachmentIdFromDownloadUrl(url: string): string | null {
  const match = exactAttachmentDownloadPattern.exec(url);
  return match?.[1] ?? null;
}

export function buildQuickNoteUpdatePayload(contentMarkdown: string): UpdateQuickNoteRequest {
  return { contentMarkdown };
}
