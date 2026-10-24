import type { ReactNode } from 'react';

export interface WorkbenchPanelProps {
  title: string;
  description?: string;
  action?: ReactNode;
  children: ReactNode;
}

export default function WorkbenchPanel({ title, description, action, children }: WorkbenchPanelProps) {
  return (
    <section className="overflow-hidden rounded-md border border-slate-200 bg-white">
      <div className="flex items-start justify-between gap-3 border-b border-slate-100 p-4">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-slate-950">{title}</h2>
          {description && <p className="mt-1 text-xs text-slate-500">{description}</p>}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}
