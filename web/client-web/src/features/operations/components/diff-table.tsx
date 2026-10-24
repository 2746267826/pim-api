import { cn } from '@/lib/utils'

function parseJsonSafe(value: string | null): Record<string, unknown> | null {
  if (!value) return null
  try {
    const parsed = JSON.parse(value)
    return typeof parsed === 'object' && parsed != null ? (parsed as Record<string, unknown>) : null
  } catch {
    return null
  }
}

/** 字段级 before/after 差异对比表（确认中心与审计时间线共用） */
export function DiffTable({ before, after }: { before: string | null; after: string | null }) {
  const b = parseJsonSafe(before)
  const a = parseJsonSafe(after)
  if (!b && !a) return null
  const keys = [...new Set([...Object.keys(b ?? {}), ...Object.keys(a ?? {})])]
  return (
    <div className="overflow-hidden rounded-ctl border border-border">
      <table className="w-full text-[13px]">
        <thead>
          <tr className="bg-surface text-left text-xs text-text-3">
            <th className="px-3 py-2 font-medium">字段</th>
            <th className="px-3 py-2 font-medium">变更前</th>
            <th className="px-3 py-2 font-medium">变更后</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-divider">
          {keys.map((key) => {
            const bv = b?.[key]
            const av = a?.[key]
            const changed = JSON.stringify(bv) !== JSON.stringify(av)
            return (
              <tr key={key} className={cn(changed && 'bg-warn-soft/50')}>
                <td className="px-3 py-1.5 align-top font-medium text-text-2">{key}</td>
                <td className="max-w-56 px-3 py-1.5 align-top break-all text-text-3">
                  {bv == null ? '—' : typeof bv === 'object' ? JSON.stringify(bv) : String(bv)}
                </td>
                <td className="max-w-56 px-3 py-1.5 align-top break-all text-text-1">
                  {av == null ? '—' : typeof av === 'object' ? JSON.stringify(av) : String(av)}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
