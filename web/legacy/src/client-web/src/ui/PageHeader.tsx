import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  subtitle?: string;
  beforeActions?: ReactNode;
  actions?: ReactNode;
}

export default function PageHeader({ title, subtitle, beforeActions, actions }: PageHeaderProps) {
  return (
    <header className="pim-panel px-4 py-3 flex flex-wrap items-center justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-lg font-semibold text-slate-950 truncate">{title}</h1>
        {subtitle && <p className="text-sm text-slate-500 mt-0.5 truncate">{subtitle}</p>}
      </div>
      <div className="flex items-center gap-2">
        {beforeActions}
        {actions}
      </div>
    </header>
  );
}
