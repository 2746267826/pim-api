import { apiDelete, apiFetch, apiGet, apiPost, apiPut, apiUpload } from '@/api/client'
import type { components } from '@/api/generated'
import type { PagedResult } from '@/api/types'
import type {
  FileItem,
  FileProvider,
  FileShare,
  FileTextSnapshot,
  ProviderBindingStart,
  ProviderBindingStatus,
  SyncStatus,
  UploadSession,
} from './types'

export const filesApi = {
  /* 网盘账号绑定（同一 provider 可并存多个账号） */
  providers: () => apiGet<FileProvider[]>('/api/v1/files/providers'),
  bindOneDrive: (clientId: string) =>
    apiPost<ProviderBindingStart>('/api/v1/files/providers/onedrive', { clientId }),
  /**
   * 发起 Google 浏览器跳转授权。返回 authorizationUrl，
   * 调用方需 `window.location.assign(url)` 整页跳转 ——
   * 浏览器跳转授权无法由脚本自动完成，必须由人在 Google 页面上点「同意」。
   */
  authorizeGoogle: () =>
    apiPost<ProviderBindingStart>('/api/v1/files/providers/google/authorize', {}),
  bindingStatus: (id: string) =>
    apiGet<ProviderBindingStatus>(`/api/v1/files/providers/${id}/binding-status`),
  /** 切换附件/闪念图片的默认落点账号 */
  setDefault: (id: string) => apiPost<boolean>(`/api/v1/files/providers/${id}/default`, {}),
  unbind: (id: string) => apiDelete<boolean>(`/api/v1/files/providers/${id}`),
  startSync: (id: string) =>
    apiPost<{ started: boolean; message: string }>(`/api/v1/files/providers/${id}/sync`, {}),
  syncStatus: (id: string) => apiGet<SyncStatus>(`/api/v1/files/providers/${id}/sync-status`),

  /* 目录与条目 */
  listItems: (params: { path: string; providerId: string; page?: number; pageSize?: number; q?: string; sort?: string; order?: string; type?: string }) => {
    const q = new URLSearchParams({ path: params.path, providerId: params.providerId })
    if (params.page) q.set('page', String(params.page))
    q.set('pageSize', String(params.pageSize ?? 100))
    if (params.q) q.set('q', params.q)
    if (params.sort) q.set('sort', params.sort)
    if (params.order) q.set('order', params.order)
    if (params.type) q.set('type', params.type)
    return apiGet<{ result: PagedResult<FileItem> }>(`/api/v1/files/items?${q}`)
  },
  // 搜索默认跨账号（后端口径）：结果里的每条都带 providerId，可显式按账号过滤。
  search: (q: string, page = 1, providerId?: string) =>
    apiGet<{ items: FileItem[]; totalCount: number; totalPages: number }>(
      `/api/v1/files/search?q=${encodeURIComponent(q)}&mode=keyword&page=${page}&pageSize=100`
        + (providerId ? `&providerId=${encodeURIComponent(providerId)}` : ''),
    ),
  folderTreeChildren: (path: string, providerId: string) =>
    filesApi.listItems({ path, providerId, pageSize: 100, type: 'folder' }),

  /* 上传（双通道） */
  uploadSimple: (providerId: string, path: string, file: File) => {
    const form = new FormData()
    form.append('providerId', providerId)
    form.append('path', path)
    form.append('file', file)
    return apiUpload<FileItem>('/api/v1/files/items/upload', form)
  },
  createUploadSession: (path: string, fileName: string, providerId: string) =>
    apiPost<UploadSession>('/api/v1/files/items/upload-session', { path, fileName, providerId }),
  completeUploadSession: (path: string, fileName: string, providerId: string, uploadedItemId?: string) =>
    apiPost<FileItem>('/api/v1/files/items/upload-session/complete', { path, fileName, providerId, uploadedItemId }),

  /* 整理 */
  createFolder: (path: string, providerId: string) =>
    apiPost<FileItem>('/api/v1/files/folders', { path, providerId }),
  move: (id: string, destinationPath: string) =>
    apiPost<FileItem>(`/api/v1/files/items/${id}/move`, { destinationPath }),
  rename: (id: string, name: string) => apiPost<FileItem>(`/api/v1/files/items/${id}/rename`, { name }),
  remove: (id: string) => apiDelete<string>(`/api/v1/files/items/${id}`),

  /* 下载与预览 */
  downloadUrl: (id: string) => apiGet<{ url: string }>(`/api/v1/files/items/${id}/download-url`),
  previewUrl: (id: string) => apiGet<{ url: string }>(`/api/v1/files/items/${id}/preview-url`),
  // 契约该 operation 只声明 path 参数 id（pim-api FilesModule.cs:596-600 的
  // BuildOpenLinkAsync(Guid id, …) 也不接收 query）—— 停发契约外的 ?mode=view。
  // 响应类型里的 mode 是服务端返回字段，保留。
  openLink: (id: string) => apiGet<{ url: string; mode: string }>(`/api/v1/files/items/${id}/open-link`),
  readText: (id: string) =>
    apiGet<{ content: string; mimeType: string | null; size: number; truncated: boolean }>(
      `/api/v1/files/items/${id}/text`,
    ),
  saveText: (id: string, content: string) =>
    apiPut<boolean>(`/api/v1/files/items/${id}/text`, { content }),
  snapshots: (id: string) => apiGet<FileTextSnapshot[]>(`/api/v1/files/items/${id}/snapshots`),
  restoreSnapshot: (id: string, snapshotId: string) =>
    apiPost<boolean>(`/api/v1/files/items/${id}/snapshots/${snapshotId}/restore`, {}),

  /* 分享 */
  share: (id: string, body: components['schemas']['CreateShareRequest']) =>
    apiPost<FileShare>(`/api/v1/files/items/${id}/share`, body),
  shares: (id: string) => apiGet<FileShare[]>(`/api/v1/files/items/${id}/shares`),
  revokeShare: (id: string, permissionId: string) =>
    apiDelete<boolean>(`/api/v1/files/items/${id}/shares/${encodeURIComponent(permissionId)}`),
  allShares: () => apiGet<FileShare[]>('/api/v1/files/shares?limit=50'),
}

export { apiFetch }
