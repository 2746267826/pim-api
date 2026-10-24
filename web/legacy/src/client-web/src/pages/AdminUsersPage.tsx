import { useCallback, useEffect, useState } from 'react';
import PageHeader from '../ui/PageHeader';
import { listAdminUsers, updateUserRole, updateUserStatus, type AdminUser } from '../api/admin';
import { apiGet } from '../api/client';
import type { ApiResponse } from '../types';

interface MeResponse {
  id: string;
  username: string;
  displayName: string;
  role: string;
}

export default function AdminUsersPage() {
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [meId, setMeId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setError(null);
    try {
      setUsers(await listAdminUsers());
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    reload();
    apiGet<ApiResponse<MeResponse>>('/auth/me')
      .then(json => { if (json?.code === 0 && json.data) setMeId(json.data.id); })
      .catch(() => {});
  }, [reload]);

  const runAction = async (user: AdminUser, action: () => Promise<{ ok: boolean; message?: string }>) => {
    setBusyId(user.id);
    setError(null);
    const result = await action();
    setBusyId(null);
    if (!result.ok) {
      setError(result.message || '操作失败');
      return;
    }
    await reload();
  };

  const toggleRole = (user: AdminUser) => {
    const next = user.role === 'admin' ? 'user' : 'admin';
    const label = next === 'admin' ? '设为管理员' : '移除管理员';
    if (!window.confirm(`确定将「${user.displayName || user.username}」${label}吗？`)) return;
    return runAction(user, () => updateUserRole(user.id, next));
  };

  const toggleActive = (user: AdminUser) => {
    const next = !user.isActive;
    if (!window.confirm(`确定${next ? '启用' : '停用'}「${user.displayName || user.username}」吗？${next ? '' : '停用后该账号将无法登录。'}`)) return;
    return runAction(user, () => updateUserStatus(user.id, next));
  };

  return (
    <div className="mx-auto max-w-3xl space-y-4 pb-20">
      <PageHeader title="用户管理" subtitle="管理账号角色与启用状态（仅管理员可见）" />

      {error && (
        <div role="alert" className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {loading ? (
        <div className="pim-card rounded-lg border p-5 text-sm text-slate-500">加载中…</div>
      ) : (
        <div className="pim-card overflow-hidden rounded-lg border">
          {users.map(user => (
            <div
              key={user.id}
              className="flex flex-wrap items-center gap-3 border-b border-slate-100 px-4 py-3 last:border-b-0"
            >
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="truncate text-sm font-semibold text-slate-900">
                    {user.displayName || user.username}
                  </span>
                  {user.id === meId && (
                    <span className="rounded-full bg-blue-50 px-2 py-0.5 text-xs text-blue-700">我</span>
                  )}
                  <span className={`rounded-full px-2 py-0.5 text-xs ${user.role === 'admin' ? 'bg-amber-50 text-amber-700' : 'bg-slate-100 text-slate-600'}`}>
                    {user.role === 'admin' ? '管理员' : '普通用户'}
                  </span>
                  {!user.isActive && (
                    <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs text-red-600">已停用</span>
                  )}
                </div>
                <div className="mt-0.5 truncate text-xs text-slate-500">
                  {user.username} · {user.email} · 注册于 {new Date(user.createdAt).toLocaleDateString('zh-CN')}
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <button
                  type="button"
                  disabled={busyId === user.id}
                  onClick={() => toggleRole(user)}
                  className="min-h-[36px] rounded-lg border border-slate-200 px-3 text-xs text-slate-700 transition-colors hover:bg-slate-50 disabled:opacity-50"
                >
                  {user.role === 'admin' ? '移除管理员' : '设为管理员'}
                </button>
                <button
                  type="button"
                  disabled={busyId === user.id}
                  onClick={() => toggleActive(user)}
                  className={`min-h-[36px] rounded-lg border px-3 text-xs transition-colors disabled:opacity-50 ${user.isActive ? 'border-red-200 text-red-600 hover:bg-red-50' : 'border-green-200 text-green-700 hover:bg-green-50'}`}
                >
                  {user.isActive ? '停用' : '启用'}
                </button>
              </div>
            </div>
          ))}
          {users.length === 0 && (
            <div className="px-4 py-6 text-center text-sm text-slate-500">暂无用户</div>
          )}
        </div>
      )}

      <p className="text-xs text-slate-400">
        首个注册的用户自动成为管理员；系统会始终保留至少一名有效管理员。
      </p>
    </div>
  );
}
