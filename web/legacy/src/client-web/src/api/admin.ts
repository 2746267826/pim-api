import { apiGet, apiPost } from './client';
import type { ApiResponse } from '../types';

export interface AdminUser {
  id: string;
  username: string;
  email: string;
  displayName: string | null;
  role: string;
  isActive: boolean;
  createdAt: string;
}

export interface AdminActionResult {
  ok: boolean;
  message?: string;
}

export async function listAdminUsers(): Promise<AdminUser[]> {
  const json = await apiGet<ApiResponse<AdminUser[]>>('/admin/users');
  return json?.data ?? [];
}

export async function updateUserRole(id: string, role: 'admin' | 'user'): Promise<AdminActionResult> {
  try {
    const json = await apiPost<ApiResponse<AdminUser>>(`/admin/users/${id}/role`, { role });
    return json?.code === 0 ? { ok: true } : { ok: false, message: json?.message || '操作失败' };
  } catch (err) {
    return { ok: false, message: err instanceof Error ? err.message : '操作失败' };
  }
}

export async function updateUserStatus(id: string, isActive: boolean): Promise<AdminActionResult> {
  try {
    const json = await apiPost<ApiResponse<AdminUser>>(`/admin/users/${id}/status`, { isActive });
    return json?.code === 0 ? { ok: true } : { ok: false, message: json?.message || '操作失败' };
  } catch (err) {
    return { ok: false, message: err instanceof Error ? err.message : '操作失败' };
  }
}
