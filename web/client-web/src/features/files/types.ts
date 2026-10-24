/* 文件域（05/files.md，多网盘三栏浏览器） */

/** 已支持的网盘类型。与后端 file_providers.provider 同域。 */
export type ProviderKey = 'onedrive' | 'google'

export const PROVIDER_LABELS: Record<ProviderKey, string> = {
  onedrive: 'OneDrive',
  google: 'Google Drive',
}

export function providerLabel(key: string): string {
  return PROVIDER_LABELS[key as ProviderKey] ?? key
}

export interface FileProvider {
  id: string
  provider: ProviderKey
  status: 'pending' | 'connected' | 'expired' | 'denied'
  clientId: string | null
  driveId: string | null
  accountId: string | null
  accountName: string | null
  syncStatus: 'idle' | 'syncing' | 'error'
  /** 最近一次同步运行中应用（新增/更新）的条目数，每次运行覆盖写入；0 不代表失败 */
  syncedItemCount: number
  /** 当前已索引条目总量（排除软删除）；变更前服务端无此字段 → 视为「总量未知」而非 0 */
  totalIndexedCount?: number | null
  lastSyncAt: string | null
  lastError: string | null
  tokenExpiresAt: string | null
  /** 是否是附件与闪念图片的默认落点账号（同一用户至多一个） */
  isDefault: boolean
}

export interface FileItem {
  id: string
  providerId: string
  externalFileId: string
  parentExternalFileId: string | null
  path: string
  name: string
  itemType: 'folder' | 'file'
  mimeType: string | null
  size: number | null
  etag: string | null
  contentHash: string | null
  createdAt: string
  modifiedAt: string
  syncedAt: string
  indexStatus: string
}

export interface FileShare {
  itemId: string
  itemName: string
  path: string
  permissionType: string
  permissionId: string | null
  webUrl: string
  expiresAt: string | null
  createdAt: string
}

export interface SyncStatus {
  syncStatus: 'idle' | 'syncing' | 'error'
  lastError: string | null
  lastSyncAt: string | null
  /** 最近一次同步运行中应用的条目数；0 不代表失败 */
  syncedItemCount: number
  /** 当前已索引条目总量；变更前服务端无此字段 → 视为「总量未知」 */
  totalIndexedCount?: number | null
}

/**
 * 绑定发起结果。两种形态共存：
 * - `device-code`（OneDrive）：给 userCode + verificationUri，用户在浏览器输入码；
 * - `browser-redirect`（Google）：给 authorizationUrl，前端整页跳转，由人工点「同意」。
 */
export interface ProviderBindingStart {
  providerId: string
  providerKey: ProviderKey
  mode: 'device-code' | 'browser-redirect'
  userCode: string | null
  verificationUri: string | null
  expiresIn: number | null
  authorizationUrl: string | null
}

export interface ProviderBindingStatus {
  /**
   * **规范账号行 id**。同一账号重新绑定时服务端会复用既有行并删掉本次的待授权行，
   * 因此轮询回来的 id 可能与发起时不同 —— 后续的 sync / unbind / setDefault 都要用它。
   */
  providerId: string
  status: 'pending' | 'connected' | 'expired' | 'denied'
  driveId: string | null
  accountId: string | null
  accountName: string | null
  userCode: string | null
  verificationUri: string | null
  deviceCodeExpiresAt: string | null
  /** 服务端要求的轮询间隔（RFC 8628 的 slow_down）；无要求时为 null */
  pollIntervalSeconds?: number | null
}

export interface UploadSession {
  uploadUrl: string
  expirationDateTime: string | null
  path: string
  fileName: string
}

export interface FileTextSnapshot {
  id: string
  path: string
  name: string
  content: string
  byteSize: number
  reason: 'pre-edit' | 'pre-restore'
  createdAt: string
}
