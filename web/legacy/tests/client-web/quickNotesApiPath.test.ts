import assert from 'node:assert/strict';
import { quickNoteApiPaths } from '../../src/client-web/src/api/quickNotes';

assert.equal(quickNoteApiPaths.list({ status: 'inbox', search: 'alpha', page: 2, pageSize: 30 }), '/quick-notes?status=inbox&search=alpha&page=2&pageSize=30');
assert.equal(quickNoteApiPaths.detail('11111111-1111-1111-1111-111111111111'), '/quick-notes/11111111-1111-1111-1111-111111111111');
assert.equal(quickNoteApiPaths.process('11111111-1111-1111-1111-111111111111'), '/quick-notes/11111111-1111-1111-1111-111111111111/process');
assert.equal(quickNoteApiPaths.archive('11111111-1111-1111-1111-111111111111'), '/quick-notes/11111111-1111-1111-1111-111111111111/archive');
assert.equal(quickNoteApiPaths.restore('11111111-1111-1111-1111-111111111111'), '/quick-notes/11111111-1111-1111-1111-111111111111/restore');
assert.equal(quickNoteApiPaths.attachments(), '/quick-notes/attachments');
assert.equal(quickNoteApiPaths.attachmentDownload('22222222-2222-2222-2222-222222222222'), '/quick-notes/attachments/22222222-2222-2222-2222-222222222222/download');
