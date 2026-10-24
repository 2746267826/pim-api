import assert from 'node:assert/strict';
import {
  buildQuickNoteUpdatePayload,
  extractQuickNoteAttachmentIds,
  getQuickNoteAttachmentIdFromDownloadUrl,
  rewriteQuickNoteAttachmentUrls,
} from '../../src/client-web/src/components/quick-notes/quickNoteAttachmentBlobUrls';

const firstId = '11111111-1111-1111-1111-111111111111';
const secondId = '22222222-2222-2222-2222-222222222222';

const markdown = [
  `![diagram](/api/v1/quick-notes/attachments/${firstId}/download)`,
  `[spreadsheet](/api/v1/quick-notes/attachments/${secondId}/download)`,
  `![duplicate](/api/v1/quick-notes/attachments/${firstId}/download)`,
  '[external](https://example.com/file.png)',
].join('\n');

assert.deepEqual(extractQuickNoteAttachmentIds(markdown), [firstId, secondId]);
assert.equal(
  getQuickNoteAttachmentIdFromDownloadUrl(`/api/v1/quick-notes/attachments/${firstId}/download`),
  firstId,
);
assert.equal(getQuickNoteAttachmentIdFromDownloadUrl(`https://example.com/api/v1/quick-notes/attachments/${firstId}/download`), null);

assert.equal(
  rewriteQuickNoteAttachmentUrls(
    markdown,
    new Map([
      [firstId, 'blob:http://localhost/first'],
      [secondId, 'blob:http://localhost/second'],
    ]),
  ),
  [
    '![diagram](blob:http://localhost/first)',
    '[spreadsheet](blob:http://localhost/second)',
    '![duplicate](blob:http://localhost/first)',
    '[external](https://example.com/file.png)',
  ].join('\n'),
);

assert.deepEqual(buildQuickNoteUpdatePayload(markdown), { contentMarkdown: markdown });
