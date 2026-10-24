import { apiGet, apiPost } from './client'
import type { AuthResponse, UserInfo } from './types'

/*
 * WO-AUTH-REGISTER-20261014 REQ-11：公开注册已关闭，本模块不再提供 register。
 * 账号由管理员在「用户管理」里创建（POST /api/v1/admin/users），
 * 全新部署的引导见后端注册策略（空库时首个用户仍为管理员）。
 */
export const authApi = {
  /** 登录（用户名或邮箱 + 密码；匿名端点） */
  login: (username: string, password: string) =>
    apiPost<AuthResponse>('/api/v1/auth/login', { username, password }, { auth: false }),

  /** 当前用户（页面刷新后恢复会话） */
  me: () => apiGet<UserInfo>('/api/v1/auth/me'),
}
