/*
 * 富文本 → 纯文本：日程 description 的 descriptionFormat 为 'html'（后端已净化但仍含标签与缩进噪声），
 * 日历卡摘要必须先转纯文本，否则会把 <a></a>、连续换行等噪声带进卡片。
 */

const NAMED_ENTITIES: Record<string, string> = {
  nbsp: ' ',
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  ldquo: '“',
  rdquo: '”',
  hellip: '…',
  mdash: '—',
  ndash: '–',
}

/** 解码常见命名实体与数字实体（未知实体原样保留） */
function decodeEntities(input: string): string {
  return input.replace(
    /&(#[xX][0-9a-fA-F]+|#\d+|[a-zA-Z][a-zA-Z0-9]*);/g,
    (match, entity: string) => {
      if (entity.startsWith('#')) {
        const isHex = entity[1] === 'x' || entity[1] === 'X'
        const code = isHex
          ? Number.parseInt(entity.slice(2), 16)
          : Number.parseInt(entity.slice(1), 10)
        if (!Number.isFinite(code) || code <= 0 || code > 0x10ffff) return match
        try {
          return String.fromCodePoint(code)
        } catch {
          return match
        }
      }
      return NAMED_ENTITIES[entity.toLowerCase()] ?? match
    },
  )
}

/** HTML → 纯文本：块级标签转换行、去其余标签、解码实体、压缩空白 */
export function htmlToPlainText(input: string | null | undefined): string {
  if (!input) return ''
  const withBreaks = input
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/(p|div|li|tr|h[1-6])>/gi, '\n')
  return decodeEntities(withBreaks.replace(/<[^>]*>/g, ''))
    // Outlook 源里换行常以字面量 "\n"（反斜杠 + n）出现，需还原为真实换行
    .replace(/\\r\\n|\\n|\\r/g, '\n')
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t\f\v\u00a0]+/g, ' ')
    .replace(/\n{2,}/g, '\n')
    .trim()
}

/**
 * 摘要分段：转纯文本 → 按行切分 → 去空段与前导符号 → 去与 exclude 重复的段（如地点、标题）。
 * 返回数组而非拼接串，便于调用方按可用高度选择「单行拼接」或「逐行展示」。
 */
export function summarizeLines(
  input: string | null | undefined,
  options: { exclude?: (string | null | undefined)[]; maxLines?: number } = {},
): string[] {
  const text = htmlToPlainText(input)
  if (!text) return []
  const exclude = (options.exclude ?? [])
    .map((v) => (v == null ? '' : String(v).trim()))
    .filter(Boolean)
  const lines = text
    .split('\n')
    .map((line) => line.trim().replace(/^[·•\-–—:：\s]+/, '').trim())
    .filter(Boolean)
    // 丢弃只有标签没有值的残段（如 "备注:"），它们不携带信息
    .filter((line) => !/^[^:：]{1,12}[:：]$/.test(line))
    .filter((line) => {
      // 段形如「地点: xxx」时，用冒号后的值参与去重
      const value = /[:：]/.test(line) ? line.split(/[:：]/).slice(1).join(':').trim() : line
      return !exclude.some((ex) => line === ex || value === ex)
    })
  return lines.slice(0, options.maxLines ?? 4)
}

/**
 * 摘要（单行）：分段后以 ' · ' 连接，超长截断并加省略号。
 * 用于日历卡在有限高度内展示最有信息量的一段。
 */
export function summarizeText(
  input: string | null | undefined,
  options: { exclude?: (string | null | undefined)[]; maxLength?: number } = {},
): string {
  const joined = summarizeLines(input, { exclude: options.exclude, maxLines: 6 }).join(' · ')
  const max = options.maxLength ?? 160
  if (joined.length <= max) return joined
  // 截断后去掉尾部悬空的分隔符，避免出现「… · 」这类残段
  const head = joined.slice(0, max - 1).replace(/[\s·•,，、;；:：-]+$/, '')
  return `${head}…`
}
