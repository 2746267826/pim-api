/*
 * 报告正文轻量 Markdown 渲染（api#495）：
 * 后端 ReportService.BuildContent 只产出 ## 标题、- 列表和 > 引用三种结构，
 * 这里按行解析成现有视觉语言的分组，不引入完整 Markdown 解析器。
 */

export function ReportMarkdown({ content }: { content: string }) {
  const blocks: React.ReactNode[] = []
  let list: string[] = []

  const flushList = () => {
    if (list.length === 0) return
    blocks.push(
      <ul key={`ul-${blocks.length}`} className="ml-4 list-disc space-y-1">
        {list.map((item, i) => (
          <li key={i}>{item}</li>
        ))}
      </ul>,
    )
    list = []
  }

  for (const rawLine of content.split('\n')) {
    const line = rawLine.trimEnd()
    if (line.startsWith('- ')) {
      list.push(line.slice(2))
      continue
    }
    flushList()
    if (line === '') continue
    if (line.startsWith('## ')) {
      blocks.push(
        <h3 key={`h-${blocks.length}`} className="pt-1 text-[13px] font-semibold text-text-1">
          {line.slice(3)}
        </h3>,
      )
      continue
    }
    if (line.startsWith('# ')) {
      blocks.push(
        <h2 key={`h-${blocks.length}`} className="text-sm font-semibold text-text-1">
          {line.slice(2)}
        </h2>,
      )
      continue
    }
    if (line.startsWith('> ')) {
      blocks.push(
        <blockquote key={`q-${blocks.length}`} className="rounded-ctl border-l-2 border-border-strong bg-surface px-3 py-2 text-[12px] text-text-3">
          {line.slice(2)}
        </blockquote>,
      )
      continue
    }
    blocks.push(<p key={`p-${blocks.length}`}>{line}</p>)
  }
  flushList()

  return <div className="space-y-2">{blocks}</div>
}
