/** 后端统一响应封装（04 §2）：{ code, message, data, timestamp }，code=0 成功 */
export interface ApiResponse<T> {
  code: number
  message: string
  data: T
  timestamp: string
}

/** 服务端分页封装（注意字段是 totalCount 而非 total） */
export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
}

export interface UserInfo {
  id: string
  username: string
  displayName: string
  role: 'admin' | 'user'
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  expiresAt: string
  user: UserInfo
}

/** GET /api/version（匿名，非 ApiResponse 封装的裸对象） */
export interface ApiVersionInfo {
  version: string
  capabilities: string[]
  latestVersion: string | null
  checkedAt: string | null
  error: string | null
  windowsVersion?: string | null
  androidVersion?: string | null
  shellWindowsVersion?: string | null
  shellAndroidVersion?: string | null
}

/** GET /api/v1/status/summary（status 后端输出为数字枚举，消费时用 normalizeHealthStatus） */
export interface SystemStatusSummary {
  status: number | string
  label: string
  message: string
  checkedAt: string
}
