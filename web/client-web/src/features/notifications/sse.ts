export interface SseFrame {
  event: string
  data: string
  id: string | null
}

export class SseParser {
  private buffer = ''

  push(chunk: string): SseFrame[] {
    this.buffer += chunk
    const frames: SseFrame[] = []
    let boundary = this.buffer.search(/\r?\n\r?\n/)
    while (boundary >= 0) {
      const raw = this.buffer.slice(0, boundary)
      const match = /\r?\n\r?\n/.exec(this.buffer.slice(boundary))
      const consumed = boundary + (match?.[0].length ?? 2)
      this.buffer = this.buffer.slice(consumed)
      const frame = parseFrame(raw)
      if (frame) frames.push(frame)
      boundary = this.buffer.search(/\r?\n\r?\n/)
    }
    return frames
  }
}

function parseFrame(raw: string): SseFrame | null {
  let event = 'message'
  let id: string | null = null
  const data: string[] = []
  for (const line of raw.split(/\r?\n/)) {
    if (line === '' || line.startsWith(':')) continue
    const separator = line.indexOf(':')
    const field = separator >= 0 ? line.slice(0, separator) : line
    const value = separator >= 0 ? line.slice(separator + 1).replace(/^ /, '') : ''
    if (field === 'event') event = value
    else if (field === 'id') id = value
    else if (field === 'data') data.push(value)
  }
  if (data.length === 0) return null
  return { event, data: data.join('\n'), id }
}
