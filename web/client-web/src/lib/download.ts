/** 客户端生成下载（03 §5）：CSV 带 BOM / JSON Blob */
export function downloadBlob(fileName: string, blob: Blob): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function downloadJson(fileName: string, content: string): void {
  downloadBlob(fileName, new Blob([content], { type: 'application/json' }))
}

export function downloadCsv(fileName: string, rows: Record<string, unknown>[], columns?: string[]): void {
  if (rows.length === 0) return
  const cols = columns ?? Object.keys(rows[0]!)
  const esc = (v: unknown) => {
    const s = v == null ? '' : String(v)
    return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
  }
  const lines = [cols.join(','), ...rows.map((r) => cols.map((c) => esc(r[c])).join(','))]
  // BOM：Excel 中文兼容
  downloadBlob(fileName, new Blob(['\uFEFF' + lines.join('\n')], { type: 'text/csv;charset=utf-8' }))
}
