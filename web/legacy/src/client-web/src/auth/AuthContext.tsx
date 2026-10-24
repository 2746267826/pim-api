import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';
import { loadTokens, clearTokens, setTokens, onTokensChanged, apiPost, apiGet } from '../api/client';
import type { ApiResponse, AuthResponse } from '../types';

interface CurrentUser {
  id: string;
  username: string;
  displayName: string;
  role: string;
}

interface AuthState {
  isAuthenticated: boolean;
  username: string | null;
  role: string | null;
  login: (username: string, password: string) => Promise<string | null>;
  register: (username: string, email: string, password: string, displayName?: string) => Promise<string | null>;
  logout: () => void;
}

const AuthContext = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isAuth, setIsAuth] = useState(() => loadTokens());
  const [username, setUsername] = useState<string | null>(null);
  const [role, setRole] = useState<string | null>(null);

  useEffect(() => {
    onTokensChanged(() => { setIsAuth(false); setUsername(null); setRole(null); });
    return () => onTokensChanged(() => {});
  }, []);

  // 页面刷新后凭本地令牌恢复用户信息（用户名与角色）
  useEffect(() => {
    if (!isAuth) return;
    let cancelled = false;
    apiGet<ApiResponse<CurrentUser>>('/auth/me')
      .then(json => {
        if (cancelled || json?.code !== 0 || !json.data) return;
        setUsername(json.data.displayName || json.data.username);
        setRole(json.data.role ?? null);
      })
      .catch(() => { /* 令牌失效时由 client 统一登出 */ });
    return () => { cancelled = true; };
  }, [isAuth]);

  const login = useCallback(async (uname: string, pwd: string): Promise<string | null> => {
    try {
      const json = await apiPost<ApiResponse<AuthResponse>>('/auth/login', { username: uname, password: pwd });
      if (json?.code !== 0 || !json.data) return json?.message || '登录失败';
      setTokens(json.data.accessToken, json.data.refreshToken);
      setUsername(json.data.user?.displayName || uname);
      setRole(json.data.user?.role ?? null);
      setIsAuth(true);
      return null;
    } catch (err) {
      return err instanceof Error ? err.message : '登录失败';
    }
  }, []);

  const register = useCallback(async (uname: string, email: string, pwd: string, displayName?: string) => {
    try {
      const json = await apiPost<ApiResponse<AuthResponse>>('/auth/register', { username: uname, email, password: pwd, displayName });
      if (json?.code !== 0 || !json.data) return json?.message || '注册失败';
      setTokens(json.data.accessToken, json.data.refreshToken);
      setUsername(json.data.user?.displayName || uname);
      setRole(json.data.user?.role ?? null);
      setIsAuth(true);
      return null;
    } catch (err) {
      return err instanceof Error ? err.message : '注册失败';
    }
  }, []);

  const logout = useCallback(() => {
    clearTokens();
    setIsAuth(false);
    setUsername(null);
    setRole(null);
  }, []);

  return (
    <AuthContext.Provider value={{ isAuthenticated: isAuth, username, role, login, register, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() { return useContext(AuthContext); }
