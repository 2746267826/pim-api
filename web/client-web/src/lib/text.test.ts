import { describe, expect, it } from 'vitest'
import { htmlToPlainText, summarizeLines, summarizeText } from './text'

describe('htmlToPlainText', () => {
  it('去标签并把块级标签转换行', () => {
    expect(htmlToPlainText('<p>甲</p><div>乙</div>')).toBe('甲\n乙')
  })

  it('解码命名与数字实体', () => {
    expect(htmlToPlainText('a&amp;b&nbsp;c&#65;&#x42;')).toBe('a&b cAB')
  })

  it('还原字面量 \\n 转义（Outlook 源常见）', () => {
    expect(htmlToPlainText('班级: 甲\\n教师: 乙')).toBe('班级: 甲\n教师: 乙')
  })

  it('压缩空白与多余空行', () => {
    expect(htmlToPlainText('<a></a>\n\n\n班级: 甲')).toBe('班级: 甲')
  })

  it('空值与纯空白返回空串', () => {
    expect(htmlToPlainText(null)).toBe('')
    expect(htmlToPlainText('   ')).toBe('')
  })
})

describe('summarizeLines', () => {
  it('按行切分并剔除与 exclude 重复的段', () => {
    const lines = summarizeLines('地点: 大成313\\n教师: 曹\n周次: 3-8周', {
      exclude: ['大成313', null, undefined],
    })
    expect(lines).toEqual(['教师: 曹', '周次: 3-8周'])
  })

  it('剔除与标题重复的段', () => {
    expect(summarizeLines('电工学②\\n教师: 肖军', { exclude: ['电工学②'] })).toEqual(['教师: 肖军'])
  })

  it('剔除只有标签没有值的残段（如「备注:」）', () => {
    expect(summarizeLines('备注:\\n16:00 开始')).toEqual(['16:00 开始'])
  })

  it('maxLines 截断', () => {
    expect(summarizeLines('A\\nB\\nC\\nD', { maxLines: 2 })).toEqual(['A', 'B'])
  })

  it('空输入返回空数组', () => {
    expect(summarizeLines(null)).toEqual([])
  })
})

describe('summarizeText', () => {
  it('拼接为单行并以省略号截断（不残留分隔符）', () => {
    const out = summarizeText('教师: 肖军\\n周次: 3-7周', { maxLength: 8 })
    expect(out).toBe('教师: 肖军…')
    expect(out.length).toBeLessThanOrEqual(8)
  })

  it('未超长时不截断', () => {
    expect(summarizeText('教师: 肖军\\n周次: 3-7周', { maxLength: 99 })).toBe('教师: 肖军 · 周次: 3-7周')
  })
})
