import { apiDelete, apiDownloadBlob, apiGet, apiPost, apiPut, apiUpload } from './client';

export { apiDownloadBlob };
import type {
  ApiResponse,
  FileShare,
  OneDriveSyncStarted,
  OneDriveSyncStatus,
  OneDriveUploadSessionInfo,
  BindNextcloudProviderRequest,
  FileTextSnapshot,
  OneDriveBindingStart,
  OneDriveBindingStatus,
  OneDriveLink,
  OneDriveSyncResult,
  OneDriveTextContent,
  FileIndexJob,
  FileItem,
  FileListResponse,
  FileOpenLink,
  FileOpenLinkMode,
  FileProvider,
  FileProviderTest,
  FileItemTypeFilter,
  FileSearchMode,
  FileSearchResult,
  FileSortKey,
  FileSortOrder,
  FileSuggestion,
  FileTrashItem,
  FileVersion,
  MoveFileRequest,
  RenameFileRequest,
  VersionRestorePreview,
} from '../types';

/** 当前文件夹列表的查询参数（REQ-2/3/8/9）。 */
export interface FileListParams {
  path?: string;
  page?: number;
  pageSize?: number;
  /** 当前文件夹内的名称过滤（大小写不敏感，服务端执行）。 */
  q?: string;
  sort?: FileSortKey;
  order?: FileSortOrder;
  type?: FileItemTypeFilter;
}

export const fileApiPaths = {
  providers: () => '/files/providers',
  bindNextcloud: () => '/files/providers/nextcloud',
  bindOneDrive: () => '/files/providers/onedrive',
  oneDriveBindingStatus: (id: string) => `/files/providers/${id}/binding-status`,
  itemContent: (id: string) => `/files/items/${id}/content`,
  itemThumbnail: (id: string, size = 'medium') => `/files/items/${id}/thumbnail?size=${encodeURIComponent(size)}`,
  itemPreviewUrl: (id: string) => `/files/items/${id}/preview-url`,
  itemDownloadUrl: (id: string) => `/files/items/${id}/download-url`,
  itemText: (id: string) => `/files/items/${id}/text`,
  itemSnapshots: (id: string) => `/files/items/${id}/snapshots`,
  itemSnapshotRestore: (id: string, snapshotId: string) => `/files/items/${id}/snapshots/${snapshotId}/restore`,
  provider: (id: string) => `/files/providers/${id}`,
  providerTest: (id: string) => `/files/providers/${id}/test`,
  providerSync: (id: string) => `/files/providers/${id}/sync`,
  items: (params: FileListParams = {}) => {
    const search: Record<string, string> = { path: params.path ?? '/' };
    if (params.page !== undefined) search.page = String(params.page);
    if (params.pageSize !== undefined) search.pageSize = String(params.pageSize);
    if (params.q) search.q = params.q;
    if (params.sort) search.sort = params.sort;
    if (params.order) search.order = params.order;
    if (params.type) search.type = params.type;
    return `/files/items?${new URLSearchParams(search).toString()}`;
  },
  item: (id: string) => `/files/items/${id}`,
  upload: () => '/files/items/upload',
  itemDownload: (id: string) => `/files/items/${id}/download`,
  move: (id: string) => `/files/items/${id}/move`,
  rename: (id: string) => `/files/items/${id}/rename`,
  trash: () => '/files/trash',
  trashRestore: (providerId: string, trashId: string) => `/files/trash/${providerId}/restore?${new URLSearchParams({ trashId }).toString()}`,
  versions: (id: string) => `/files/items/${id}/versions`,
  versionDownload: (id: string, versionId: string) => `/files/items/${id}/versions/${versionId}/download`,
  versionRestorePreview: (id: string, versionId: string) => `/files/items/${id}/versions/${versionId}/restore-preview`,
  versionRestore: (id: string, versionId: string) => `/files/items/${id}/versions/${versionId}/restore`,
  index: (id: string) => `/files/items/${id}/index`,
  search: (q: string, mode: FileSearchMode, page?: number, pageSize?: number) => {
    const params: Record<string, string> = { q, mode };
    if (page !== undefined) params.page = String(page);
    if (pageSize !== undefined) params.pageSize = String(pageSize);
    return `/files/search?${new URLSearchParams(params).toString()}`;
  },
  suggestions: () => '/files/suggestions',
  dismissSuggestion: (id: string) => `/files/suggestions/${id}/dismiss`,
  acceptSuggestion: (id: string) => `/files/suggestions/${id}/accept`,
  openLink: (id: string, mode: FileOpenLinkMode) => `/files/items/${id}/open-link?${new URLSearchParams({ mode }).toString()}`,
  // ---- PR-2（REQ-11 ~ REQ-25）----
  createFolder: () => '/files/folders',
  share: (id: string) => `/files/items/${id}/share`,
  shares: (id: string) => `/files/items/${id}/shares`,
  shareRevoke: (id: string, permissionId: string) =>
    `/files/items/${id}/shares/${encodeURIComponent(permissionId)}`,
  allShares: (limit?: number) => `/files/shares${limit ? `?limit=${limit}` : ''}`,
  syncStatus: (id: string) => `/files/providers/${id}/sync-status`,
  /** REQ-14：创建上传会话（返回预授权 uploadUrl，浏览器直接向微软分片上传）。 */
  uploadSession: () => '/files/items/upload-session',
  /** REQ-14：上传完成后登记元数据（字节不经服务器，只登记结果）。 */
  uploadSessionComplete: () => '/files/items/upload-session/complete',
} as const;

export function getFileProviders() {
  return apiGet<ApiResponse<FileProvider[]>>(fileApiPaths.providers()).then(r => r.data);
}

export function bindNextcloudProvider(data: BindNextcloudProviderRequest) {
  return apiPost<ApiResponse<FileProvider>>(fileApiPaths.bindNextcloud(), data).then(r => r.data);
}

export function testFileProvider(id: string) {
  return apiPost<ApiResponse<FileProviderTest>>(fileApiPaths.providerTest(id), {}).then(r => r.data);
}

export function syncFileProvider(id: string) {
  return apiPost<ApiResponse<FileItem[]>>(fileApiPaths.providerSync(id), {}).then(r => r.data);
}

export function getFileItems(params: FileListParams = {}) {
  return apiGet<ApiResponse<FileListResponse>>(fileApiPaths.items(params)).then(r => r.data);
}

export function getFileItem(id: string) {
  return apiGet<ApiResponse<FileItem>>(fileApiPaths.item(id)).then(r => r.data);
}

export function uploadFile(providerId: string, path: string, file: File) {
  const formData = new FormData();
  formData.append('providerId', providerId);
  formData.append('path', path);
  formData.append('file', file);

  return apiUpload<ApiResponse<FileItem>>(fileApiPaths.upload(), formData).then(r => r.data);
}

export function downloadFileBlob(id: string): Promise<Blob> {
  return apiDownloadBlob(fileApiPaths.itemDownload(id));
}

export function moveFile(id: string, data: MoveFileRequest) {
  return apiPost<ApiResponse<FileItem>>(fileApiPaths.move(id), data).then(r => r.data);
}

export function renameFile(id: string, data: RenameFileRequest) {
  return apiPost<ApiResponse<FileItem>>(fileApiPaths.rename(id), data).then(r => r.data);
}

export function deleteFile(id: string) {
  return apiDelete<ApiResponse<string>>(fileApiPaths.item(id)).then(r => r.data);
}

export function getFileTrash() {
  return apiGet<ApiResponse<FileTrashItem[]>>(fileApiPaths.trash()).then(r => r.data);
}

export function restoreFileTrash(providerId: string, trashId: string) {
  return apiPost<ApiResponse<string>>(fileApiPaths.trashRestore(providerId, trashId), {}).then(r => r.data);
}

export function getFileVersions(id: string) {
  return apiGet<ApiResponse<FileVersion[]>>(fileApiPaths.versions(id)).then(r => r.data);
}

export function downloadFileVersionBlob(id: string, versionId: string): Promise<Blob> {
  return apiDownloadBlob(fileApiPaths.versionDownload(id, versionId));
}

export function restoreFileVersionPreview(id: string, versionId: string) {
  return apiPost<ApiResponse<VersionRestorePreview>>(fileApiPaths.versionRestorePreview(id, versionId), {}).then(r => r.data);
}

export function restoreFileVersion(id: string, versionId: string) {
  return apiPost<ApiResponse<string>>(fileApiPaths.versionRestore(id, versionId), {}).then(r => r.data);
}

export function indexFile(id: string) {
  return apiPost<ApiResponse<FileIndexJob>>(fileApiPaths.index(id), {}).then(r => r.data);
}

export function searchFiles(q: string, mode: FileSearchMode, page?: number, pageSize?: number) {
  return apiGet<ApiResponse<FileSearchResult>>(fileApiPaths.search(q, mode, page, pageSize)).then(r => r.data);
}

export function getFileSuggestions() {
  return apiGet<ApiResponse<FileSuggestion[]>>(fileApiPaths.suggestions()).then(r => r.data);
}

export function dismissFileSuggestion(id: string) {
  return apiPost<ApiResponse<FileSuggestion>>(fileApiPaths.dismissSuggestion(id), {}).then(r => r.data);
}

export function acceptFileSuggestion(id: string) {
  return apiPost<ApiResponse<FileSuggestion>>(fileApiPaths.acceptSuggestion(id), {}).then(r => r.data);
}

export function getFileOpenLink(id: string, mode: FileOpenLinkMode) {
  return apiGet<ApiResponse<FileOpenLink>>(fileApiPaths.openLink(id, mode)).then(r => r.data);
}

// ---- OneDrive（文件模块 v2，见 designs/onedrive-files-v2.md）----

export function startOneDriveBinding(clientId: string) {
  return apiPost<ApiResponse<OneDriveBindingStart>>(fileApiPaths.bindOneDrive(), { clientId }).then(r => r.data);
}

export function getOneDriveBindingStatus(providerId: string) {
  return apiGet<ApiResponse<OneDriveBindingStatus>>(fileApiPaths.oneDriveBindingStatus(providerId)).then(r => r.data);
}

export function disconnectFileProvider(id: string) {
  return apiDelete<ApiResponse<string>>(fileApiPaths.provider(id)).then(r => r.data);
}

export function getOneDriveSyncResult(id: string) {
  return apiPost<ApiResponse<OneDriveSyncResult>>(fileApiPaths.providerSync(id), {}).then(r => r.data);
}

// ---- PR-2（REQ-11 ~ REQ-25）----

/** REQ-25：手动同步现在立即返回「已开始」（后台执行）。 */
export function startOneDriveSync(id: string) {
  return apiPost<ApiResponse<OneDriveSyncStarted>>(fileApiPaths.providerSync(id), {}).then(r => r.data);
}

/** REQ-25：同步状态（顶部横幅）。 */
export function getOneDriveSyncStatus(id: string) {
  return apiGet<ApiResponse<OneDriveSyncStatus>>(fileApiPaths.syncStatus(id)).then(r => r.data);
}

/** REQ-15：新建文件夹。 */
export function createFolder(path: string) {
  return apiPost<ApiResponse<FileItem>>(fileApiPaths.createFolder(), { path }).then(r => r.data);
}

/** REQ-21：生成分享链接。 */
export function createShare(id: string, permissionType: 'view' | 'edit', expiresInDays?: number | null) {
  return apiPost<ApiResponse<FileShare>>(fileApiPaths.share(id), { permissionType, expiresInDays }).then(r => r.data);
}

/** REQ-21：列出某条目的分享（预览面板就地撤销）。 */
export function getItemShares(id: string) {
  return apiGet<ApiResponse<FileShare[]>>(fileApiPaths.shares(id)).then(r => r.data);
}

/** REQ-21：撤销分享。 */
export function revokeShare(id: string, permissionId: string) {
  return apiDelete<ApiResponse<boolean>>(fileApiPaths.shareRevoke(id, permissionId)).then(r => r.data);
}

/** REQ-21：我的分享。 */
export function getAllShares(limit?: number) {
  return apiGet<ApiResponse<FileShare[]>>(fileApiPaths.allShares(limit)).then(r => r.data);
}

/** REQ-14：创建上传会话。服务器只创建会话与校验归属，不搬字节。 */
export function createUploadSession(path: string, fileName: string) {
  return apiPost<ApiResponse<OneDriveUploadSessionInfo>>(fileApiPaths.uploadSession(), { path, fileName }).then(r => r.data);
}

/** REQ-14：上传完成后登记元数据（内容已在微软侧，服务器只登记结果）。 */
export function completeUploadSession(path: string, fileName: string, uploadedItemId?: string) {
  return apiPost<ApiResponse<FileItem>>(fileApiPaths.uploadSessionComplete(), {
    path,
    fileName,
    uploadedItemId,
  }).then(r => r.data);
}

/** 直链与缩略图端点是 302；页面用带鉴权的 fetch 跟随得到内容，或直接引用相对 URL。 */
export function oneDriveContentUrl(id: string) {
  return fileApiPaths.itemContent(id);
}

export function oneDriveThumbnailUrl(id: string, size = 'medium') {
  return fileApiPaths.itemThumbnail(id, size);
}

export function getOneDrivePreviewUrl(id: string) {
  return apiGet<ApiResponse<OneDriveLink>>(fileApiPaths.itemPreviewUrl(id)).then(r => r.data.url);
}

/**
 * REQ-20 / AC-20.1：下载直链（JSON 形态）。
 *
 * 不走 302 端点再 fetch 跟随——那样页面会真的把文件体拉一遍（验收 F-3）。
 * 这里只取一个 URL 字符串，内容由浏览器直接从微软域加载。
 */
export function getDownloadUrl(id: string) {
  return apiGet<ApiResponse<OneDriveLink>>(fileApiPaths.itemDownloadUrl(id)).then(r => r.data.url);
}

export function getOneDriveText(id: string) {
  return apiGet<ApiResponse<OneDriveTextContent>>(fileApiPaths.itemText(id)).then(r => r.data);
}

export function saveOneDriveText(id: string, content: string) {
  return apiPut<ApiResponse<string>>(fileApiPaths.itemText(id), { content }).then(r => r.data);
}

export function getOneDriveSnapshots(id: string) {
  return apiGet<ApiResponse<FileTextSnapshot[]>>(fileApiPaths.itemSnapshots(id)).then(r => r.data);
}

export function restoreOneDriveSnapshot(id: string, snapshotId: string) {
  return apiPost<ApiResponse<string>>(fileApiPaths.itemSnapshotRestore(id, snapshotId), {}).then(r => r.data);
}
