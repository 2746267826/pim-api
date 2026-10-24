import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from './AuthContext';

type LocationState = {
  from?: {
    pathname?: string;
    search?: string;
    hash?: string;
  };
};

export default function LoginPage() {
  const { login, register, isAuthenticated } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as LocationState | null)?.from;
  const redirectTarget = from?.pathname
    ? `${from.pathname}${from.search ?? ''}${from.hash ?? ''}`
    : '/today';
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [email, setEmail] = useState('');
  const [isRegister, setIsRegister] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  if (isAuthenticated) {
    return <Navigate to={redirectTarget} replace />;
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const err = isRegister
        ? await register(username, email, password)
        : await login(username, password);
      if (err) setError(err);
      else navigate(redirectTarget, { replace: true });
    } catch {
      setError('网络连接失败，请稍后再试');
    } finally { setLoading(false); }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-[var(--pim-bg)] px-4">
      <form onSubmit={handleSubmit} className="pim-panel w-full max-w-sm p-8 shadow-[var(--pim-shadow-soft)]">
        <div className="mb-6 text-center">
          <p className="text-xs font-semibold uppercase tracking-[0.28em] text-blue-600">
            {isRegister ? '创建账号' : '欢迎回来'}
          </p>
          <h1 className="mt-2 text-3xl font-semibold text-slate-950">PIM</h1>
        </div>
        {error && (
          <div className="mb-4 rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700" role="alert">{error}</div>
        )}
        <label className="mb-3 block">
          <span className="sr-only">用户名</span>
          <input
            type="text" placeholder="用户名" value={username}
            onChange={e => setUsername(e.target.value)}
            className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100" required
          />
        </label>
        {isRegister && (
          <label className="mb-3 block">
            <span className="sr-only">邮箱</span>
            <input
              type="email" placeholder="邮箱" value={email}
              onChange={e => setEmail(e.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100" required
            />
          </label>
        )}
        <label className="mb-4 block">
          <span className="sr-only">密码</span>
          <input
            type="password" placeholder="密码" value={password}
            onChange={e => setPassword(e.target.value)}
            className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-blue-300 focus:ring-2 focus:ring-blue-100" required
          />
        </label>
        <button
          type="submit" disabled={loading}
          className="pim-button-primary w-full py-2.5 font-medium disabled:cursor-not-allowed disabled:opacity-50"
        >
          {loading ? '处理中...' : isRegister ? '注册' : '登录'}
        </button>
        <button
          type="button"
          onClick={() => { setIsRegister(!isRegister); setError(null); }}
          className="mt-3 w-full text-center text-sm font-medium text-blue-600 hover:underline"
        >
          {isRegister ? '已有账号？登录' : '没有账号？注册'}
        </button>
      </form>
    </div>
  );
}
