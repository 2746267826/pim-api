import assert from 'node:assert/strict';
import type {
  FileAiResult,
  FileChunkSearchHit,
  FileIndexJob,
  FileItem,
  FileListResponse,
  FileOpenLink,
  FileProvider,
  FileProviderTest,
  FileSearchResult,
  FileSuggestion,
  FileTrashItem,
  FileVersion,
  MoveFileRequest,
  RenameFileRequest,
  VersionRestorePreview,
} from '../../src/client-web/src/types';
import { apiDownloadBlob, apiUpload } from '../../src/client-web/src/api/client';
import {
  deleteFile,
  downloadFileBlob,
  downloadFileVersionBlob,
  getFileItems,
  getFileOpenLink,
  getFileTrash,
  restoreFileTrash,
  searchFiles,
  uploadFile,
} from '../../src/client-web/src/api/files';

const providerId = '11111111-1111-1111-1111-111111111111';
const itemId = '22222222-2222-2222-2222-222222222222';
const versionId = '33333333-3333-3333-3333-333333333333';

function acceptsApiUploadSignature(fn: typeof apiUpload) {
  return fn('/files/items/upload', new FormData());
}

function acceptsApiDownloadBlobSignature(fn: typeof apiDownloadBlob): Promise<Blob> {
  return fn(`/files/items/${itemId}/download`);
}

function acceptsUploadFileSignature(fn: typeof uploadFile): Promise<FileItem> {
  return fn(providerId, '/Reports', new File(['alpha'], 'alpha.txt', { type: 'text/plain' }));
}

function acceptsDownloadFileBlobSignature(fn: typeof downloadFileBlob): Promise<Blob> {
  return fn(itemId);
}

function acceptsDownloadFileVersionBlobSignature(fn: typeof downloadFileVersionBlob): Promise<Blob> {
  return fn(itemId, versionId);
}

function acceptsGetFileItemsReturn(result: ReturnType<typeof getFileItems>): Promise<FileListResponse> {
  return result;
}

function acceptsDeleteFileReturn(result: ReturnType<typeof deleteFile>): Promise<string> {
  return result;
}

function acceptsGetFileTrashReturn(result: ReturnType<typeof getFileTrash>): Promise<FileTrashItem[]> {
  return result;
}

function acceptsRestoreFileTrashReturn(result: ReturnType<typeof restoreFileTrash>): Promise<string> {
  return result;
}

function acceptsSearchFilesReturn(result: ReturnType<typeof searchFiles>): Promise<FileSearchResult> {
  return result;
}

function acceptsOpenLinkReturn(result: ReturnType<typeof getFileOpenLink>): Promise<FileOpenLink> {
  return result;
}

void acceptsApiUploadSignature;
void acceptsApiDownloadBlobSignature;
void acceptsUploadFileSignature;
void acceptsDownloadFileBlobSignature;
void acceptsDownloadFileVersionBlobSignature;
void acceptsGetFileItemsReturn;
void acceptsDeleteFileReturn;
void acceptsGetFileTrashReturn;
void acceptsRestoreFileTrashReturn;
void acceptsSearchFilesReturn;
void acceptsOpenLinkReturn;

const provider: FileProvider = {
  id: providerId,
  provider: 'nextcloud',
  baseUrl: 'https://cloud.example.test',
  internalBaseUrl: null,
  username: 'ada',
  status: 'connected',
  lastSyncAt: null,
  lastError: null,
  createdAt: '2026-05-27T00:00:00Z',
  updatedAt: '2026-05-27T00:00:00Z',
};

const providerTest: FileProviderTest = {
  success: true,
  status: 'connected',
  errorMessage: null,
};

const ai: FileAiResult = {
  id: '55555555-5555-5555-5555-555555555555',
  fileItemId: itemId,
  versionId,
  summary: 'Quarterly budget report.',
  tags: ['budget', 'finance'],
  language: 'en',
  sensitivity: 'internal',
  generatedAt: '2026-05-27T00:00:00Z',
  model: 'gpt-test',
  aiRequestLogId: null,
  evidenceChunkIds: ['66666666-6666-6666-6666-666666666666'],
};

const item: FileItem = {
  id: itemId,
  providerId,
  externalFileId: 'fileid-123',
  parentExternalFileId: null,
  path: '/Reports/budget.docx',
  name: 'budget.docx',
  itemType: 'file',
  mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  size: 4096,
  etag: 'abc',
  contentHash: 'sha256:abc',
  currentVersionId: versionId,
  permissions: 'RGDNVW',
  isDeleted: false,
  deletedAt: null,
  lastSeenAt: '2026-05-27T00:00:00Z',
  createdAt: '2026-05-27T00:00:00Z',
  modifiedAt: '2026-05-27T00:00:00Z',
  syncedAt: '2026-05-27T00:00:00Z',
  indexStatus: 'completed',
  ai,
};

const listResponse: FileListResponse = {
  result: {
    items: [item],
    page: 1,
    pageSize: 50,
    totalCount: 1,
    totalPages: 1,
  },
};

const version: FileVersion = {
  id: versionId,
  fileItemId: itemId,
  externalVersionId: 'v1',
  etag: 'abc',
  size: 4096,
  modifiedAt: '2026-05-27T00:00:00Z',
  source: 'nextcloud',
  isCurrent: true,
  syncedAt: '2026-05-27T00:00:00Z',
};

const suggestion: FileSuggestion = {
  id: '77777777-7777-7777-7777-777777777777',
  fileItemId: itemId,
  suggestionType: 'rename',
  title: 'Rename budget report',
  reason: 'The current name is ambiguous.',
  confidence: 0.91,
  payloadJson: '{}',
  status: 'pending',
  aiRequestLogId: null,
  createdAt: '2026-05-27T00:00:00Z',
  updatedAt: '2026-05-27T00:00:00Z',
};

const chunk: FileChunkSearchHit = {
  chunkId: '88888888-8888-8888-8888-888888888888',
  fileItemId: itemId,
  versionId,
  text: 'budget report',
  score: 0.87,
};

const searchResult: FileSearchResult = {
  items: [item],
  chunks: [chunk],
  // REQ-8 / P7：全盘搜索结果必须能表达「共 N 项 · 第 X/Y 页」
  totalCount: 1,
  totalPages: 1,
};

const trashItem: FileTrashItem = {
  trashId: 'trash/budget.docx',
  originalLocation: '/Reports/budget.docx',
  name: 'budget.docx',
  itemType: 'file',
  size: 4096,
  deletedAt: '2026-05-27T00:00:00Z',
};

const restorePreview: VersionRestorePreview = {
  fileItemId: itemId,
  versionId,
  currentVersionLabel: '2026-05-27T00:00:00Z',
  restoreVersionLabel: '2026-05-26T00:00:00Z',
  requiresConfirmation: true,
  summary: 'Restore old version',
};

const indexJob: FileIndexJob = {
  id: '99999999-9999-9999-9999-999999999999',
  fileItemId: itemId,
  versionId,
  status: 'queued',
  stage: 'extract',
  attemptCount: 0,
  lastError: null,
};

const moveRequest: MoveFileRequest = { destinationPath: '/Archive/budget.docx' };
const renameRequest: RenameFileRequest = { name: 'budget-final.docx' };
const openLink: FileOpenLink = { url: 'https://cloud.example.test/f/123', mode: 'nextcloud' };

assert.equal(provider.status, providerTest.status);
assert.equal(listResponse.result.items[0].ai?.tags[0], 'budget');
assert.equal(version.isCurrent, true);
assert.equal(suggestion.confidence, 0.91);
assert.equal(searchResult.chunks[0].score, 0.87);
assert.equal(trashItem.trashId, 'trash/budget.docx');
assert.equal(restorePreview.requiresConfirmation, true);
assert.equal(indexJob.stage, 'extract');
assert.equal(moveRequest.destinationPath, '/Archive/budget.docx');
assert.equal(renameRequest.name, 'budget-final.docx');
assert.equal(openLink.mode, 'nextcloud');
