export default function MobilePageHeader({ title, action }: { title: string; action?: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between border-b bg-white px-4 py-3 md:hidden" style={{ paddingTop: 'max(0.75rem, env(safe-area-inset-top))' }}>
      <h1 className="text-base font-semibold">{title}</h1>
      {action}
    </div>
  );
}
