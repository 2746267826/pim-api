// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import {
  LEGACY_QUICK_NOTE_NOTICE,
  legacyQuickNotePlainText,
  legacyQuickNoteTitle,
} from './legacy-quick-note'

describe('legacy quick-note compatibility copy', () => {
  it('extracts plain text without presenting legacy HTML as migrated Markdown', () => {
    const html = '<h1>课堂重点</h1><p>公式 <strong>A+B</strong></p><script>alert(1)</script>'
    expect(legacyQuickNotePlainText(html)).toBe('课堂重点\n公式 A+B')
    expect(legacyQuickNoteTitle(html)).toBe('课堂重点')
    expect(LEGACY_QUICK_NOTE_NOTICE).toContain('不会自动合并')
    expect(LEGACY_QUICK_NOTE_NOTICE).toContain('删除旧记录')
  })

  it('keeps plain text untouched and truncates an overlong title', () => {
    expect(legacyQuickNotePlainText('第一行\n第二行')).toBe('第一行\n第二行')
    expect(legacyQuickNoteTitle('标题'.repeat(60))).toHaveLength(80)
    expect(legacyQuickNoteTitle('<p></p>')).toBe('旧闪念纯文本副本')
  })
})
