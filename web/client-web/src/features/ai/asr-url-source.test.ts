// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import {
  MAX_PUBLIC_AUDIO_URL_CHARS,
  REDACTED_SOURCE_URL,
  redactSourceUrl,
  validatePublicAudioUrl,
} from './asr-url-source'

describe('validatePublicAudioUrl', () => {
  it('accepts bounded HTTPS and exposes only the origin for confirmation', () => {
    const result = validatePublicAudioUrl('  https://audio.example.test/path/file.wav?token=synthetic  ')
    expect(result).toEqual({ ok: true, origin: 'https://audio.example.test' })
  })

  it('rejects non-HTTPS, credentials, fragments, controls, empty and oversized values', () => {
    expect(validatePublicAudioUrl('http://audio.example.test/file.wav').ok).toBe(false)
    expect(validatePublicAudioUrl('https://user:pass@audio.example.test/file.wav').ok).toBe(false)
    expect(validatePublicAudioUrl('https://audio.example.test/file.wav#fragment').ok).toBe(false)
    expect(validatePublicAudioUrl('https://audio.example.test/file.wav?x=\n').ok).toBe(false)
    expect(validatePublicAudioUrl('').ok).toBe(false)
    expect(validatePublicAudioUrl(`https://audio.example.test/${'a'.repeat(MAX_PUBLIC_AUDIO_URL_CHARS)}`).ok).toBe(false)
  })

  it('does not write browser storage', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem')
    validatePublicAudioUrl('https://audio.example.test/file.wav')
    expect(setItem).not.toHaveBeenCalled()
    setItem.mockRestore()
  })
})

describe('redactSourceUrl', () => {
  it('removes the exact source and query form from error text', () => {
    const source = 'https://audio.example.test/file.wav?token=synthetic&sig=private'
    const message = `读取失败：${source}；查询字符串 ${new URL(source).search}`
    const redacted = redactSourceUrl(message, source)
    expect(redacted).not.toContain(source)
    expect(redacted).not.toContain('token=synthetic')
    expect(redacted).toContain(REDACTED_SOURCE_URL)
  })
})
