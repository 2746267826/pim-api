import { useNavigate } from 'react-router-dom';

export interface ErrorPageProps {
  error: Error | null;
  onRetry?: () => void;
  onGoHome?: () => void;
  title?: string;
  emoji?: string;
}

export default function ErrorPage({ error, onRetry, onGoHome, title = '页面加载出错', emoji = '💥' }: ErrorPageProps) {
  const navigate = useNavigate();
  const handleGoHome = () => {
    if (onGoHome) onGoHome();
    else navigate('/today', { replace: true });
  };

  return (
    <div className="mx-auto flex min-h-[60vh] w-full max-w-[560px] flex-col items-center justify-center px-4 py-12">
      <div className="pim-panel w-full px-6 py-10 text-center">
        <div className="text-[48px] leading-none" aria-hidden="true">{emoji}</div>
        <h1 className="mt-4 text-lg font-semibold text-slate-900">{title}</h1>
        <p className="mt-2 text-sm text-slate-500">组件渲染时发生了错误</p>

        {error && (
          <details className="mt-4 text-left">
            <summary className="cursor-pointer select-none text-sm font-medium text-slate-600 hover:text-slate-900">
              查看详情
            </summary>
            <div className="mt-2 max-h-48 overflow-auto rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs leading-relaxed text-slate-600">
              <div className="font-semibold text-red-600">{error.name}: {error.message}</div>
              {import.meta.env.DEV && error.stack && (
                <pre className="mt-2 whitespace-pre-wrap break-all text-[11px] text-slate-500">{error.stack}</pre>
              )}
              {!import.meta.env.DEV && (
                <p className="mt-2 text-[11px] text-slate-400">错误详情已记录到控制台</p>
              )}
            </div>
          </details>
        )}

        <div className="mt-6 flex flex-wrap justify-center gap-3">
          {onRetry && (
            <button type="button" onClick={onRetry} className="pim-button-primary px-5 py-2 text-sm">
              重试
            </button>
          )}
          <button type="button" onClick={handleGoHome} className="pim-button-secondary px-5 py-2 text-sm">
            返回首页
          </button>
        </div>
      </div>
    </div>
  );
}
