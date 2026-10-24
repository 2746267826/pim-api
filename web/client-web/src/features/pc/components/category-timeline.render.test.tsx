/*
 * CategoryTimeline 状态分支测试：
 * 验证「加载中 / 请求失败 / 空数据 / 有数据」四态互斥，
 * 尤其是「请求失败不能显示成暂无数据」这条（避免静默空白）。
 */
import { describe, expect, it } from 'vitest'
import { renderToString } from 'react-dom/server'
import { CategoryTimeline } from './category-timeline'

const R = {
  start: '2026-09-27T03:00:00.0400000+00:00', // 墙钟 11:00
  end: '2026-09-27T03:30:00.0400000+00:00',
  durationMinutes: 30,
  appName: 'code',
  windowTitle: 'main.ts',
  categoryName: '编程/折腾',
  categoryColor: '#6B5EE4',
}

/* SSR 渲染一次取 HTML（组件无浏览器专属副作用，可安全服务端渲染首屏） */
function render(props: Parameters<typeof CategoryTimeline>[0]): string {
  return renderToString(<CategoryTimeline {...props} />)
}

/** 相邻文本节点间 SSR 会插入注释，且文案被 <span> 包裹；此处抽成纯文本再断言 */
function text(props: Parameters<typeof CategoryTimeline>[0]): string {
  return render(props)
    .replace(/<!-- -->/g, '')
    .replace(/<[^>]*>/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

describe('CategoryTimeline 状态分支', () => {
  it('加载中：显示骨架，不显示空态与错误', () => {
    const html = render({ timeline: undefined, loading: true })
    expect(html).toContain('时间线加载中')
    expect(html).not.toContain('当日暂无活动时间线')
    expect(html).not.toContain('时间线数据加载失败')
  })

  it('请求失败：显示可见错误，且不显示空态（关键：不得静默空白）', () => {
    const html = render({ timeline: undefined, loading: false, error: '内部服务器错误' })
    expect(html).toContain('时间线数据加载失败')
    expect(html).toContain('内部服务器错误')
    expect(html).not.toContain('当日暂无活动时间线')
    expect(html).not.toContain('时间线加载中')
  })

  it('空数据：显示空态，不画时间条也不显示错误', () => {
    const html = render({ timeline: [], loading: false })
    expect(html).toContain('当日暂无活动时间线')
    expect(html).not.toContain('时间线数据加载失败')
    // 不应渲染任何横条
    expect(html).not.toContain('aria-label="编程')
  })

  it('有数据：渲染统计条、刻度、小时行与横条', () => {
    const t = text({ timeline: [R], loading: false, subtitle: '业务日 2026-09-27（+08:00 墙钟）' })
    expect(t).toContain('分类时间线')
    expect(t).toContain('查看详情')
    expect(t).toContain('编程/折腾')
    expect(t).toContain('0.5h') // 30 分钟
    expect(t).toContain('专注率')
    expect(t).toContain('100%')
    expect(t).toContain('1 条')
    expect(t).toContain('11:00') // 小时行
    expect(t).toContain('0m')
    expect(t).toContain('60m')
    expect(t).toContain('显示全部 00:00–23:00（含空行）')
    // 横条的可访问名包含四项信息：分类 / 应用 / 起止 / 时长（aria-label 在属性里，用原始 HTML 断言）
    expect(render({ timeline: [R], loading: false })).toContain('aria-label="编程/折腾 code 11:00 至 11:30 30 分钟"')
  })

  it('字段缺失/null 不崩溃、不出现 undefined', () => {
    const html = render({
      timeline: [{ start: R.start, end: R.end, appName: null, windowTitle: null, categoryName: null }],
      loading: false,
    })
    expect(html).not.toContain('undefined')
    expect(html).not.toContain('NaN')
    expect(html).toContain('未知应用')
    expect(html).toContain('其他')
  })

  it('全部为无效记录时按空态处理', () => {
    const html = render({
      timeline: [
        { start: 'bad', end: 'worse' },
        { start: R.start, end: R.start }, // end == start
      ],
      loading: false,
    })
    expect(html).toContain('当日暂无活动时间线')
    expect(html).not.toContain('undefined')
  })
})
