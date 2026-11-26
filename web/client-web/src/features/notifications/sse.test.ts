import { describe, expect, it } from 'vitest'
import { SseParser } from './sse'

describe('SseParser', () => {
  it('parses notification frames split across transport chunks', () => {
    const parser = new SseParser()
    expect(parser.push('retry: 3000\n\nevent: notif')).toEqual([])
    expect(parser.push('ication\nid: 7\ndata: {"title":"任务')).toEqual([])
    const frames = parser.push('已完成"}\n\n')

    expect(frames).toEqual([
      {
        event: 'notification',
        id: '7',
        data: '{"title":"任务已完成"}',
      },
    ])
  })

  it('ignores keep-alive comments and frames without data', () => {
    const parser = new SseParser()
    expect(parser.push(': keep-alive\n\nevent: notification\n\n')).toEqual([])
    expect(parser.push('event: notification\ndata: first\ndata: second\n\n')).toEqual([
      { event: 'notification', id: null, data: 'first\nsecond' },
    ])
  })

  it('accepts CRLF framing and trims one leading space from field values', () => {
    const parser = new SseParser()
    const frames = parser.push('event: notification\r\nid: 42\r\ndata:  {}  \r\n\r\n')

    expect(frames).toEqual([{ event: 'notification', id: '42', data: ' {}  ' }])
  })

  it('keeps an incomplete trailing frame buffered', () => {
    const parser = new SseParser()
    expect(parser.push('event: notification\ndata: {"a":1}')).toEqual([])
    expect(parser.push('\n\n')).toHaveLength(1)
    expect(parser.push('event: notification\ndata: {"b":2}\n\n')).toHaveLength(1)
  })
})
