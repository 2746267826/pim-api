export const LEGACY_QUICK_NOTE_NOTICE =
  '旧闪念仍按原 HTML 记录保存；这里是用户主动创建的纯文本副本，不会自动合并、覆盖或删除旧记录。'

export function legacyQuickNotePlainText(content: string): string {
  if (!content.includes('<')) return content.trim()
  if (typeof DOMParser === 'undefined') {
    return content
      .replace(/<br\s*\/?>/gi, '\n')
      .replace(/<\/(p|div|li|h[1-6]|blockquote)>/gi, '\n')
      .replace(/<[^>]+>/g, '')
      .trim()
  }
  const document = new DOMParser().parseFromString(content, 'text/html')
  document.querySelectorAll('script,style').forEach((node) => node.remove())
  document.querySelectorAll('br').forEach((node) => node.replaceWith('\n'))
  document.querySelectorAll('address,article,aside,blockquote,div,dl,fieldset,figcaption,figure,footer,h1,h2,h3,h4,h5,h6,header,li,main,nav,ol,p,pre,section,table,tr,ul')
    .forEach((node) => node.insertAdjacentText('afterend', '\n'))
  return (document.body.textContent ?? '')
    .replace(/\u00a0/g, ' ')
    .replace(/[ \t]+\n/g, '\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim()
}

export function legacyQuickNoteTitle(content: string): string {
  const firstLine = legacyQuickNotePlainText(content)
    .split(/\r?\n/)
    .map((line) => line.trim())
    .find(Boolean)
  if (!firstLine) return '旧闪念纯文本副本'
  return firstLine.length > 80 ? `${firstLine.slice(0, 77)}...` : firstLine
}
