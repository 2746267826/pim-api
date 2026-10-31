/** Browser-side guard for public URL ASR sources. The server remains authoritative. */
export const MAX_PUBLIC_AUDIO_URL_CHARS = 8192
export const REDACTED_SOURCE_URL = '[已脱敏源URL]'

export type PublicAudioUrlValidation =
  | { ok: false; message: string }
  | { ok: true; origin: string }

function hasControlCharacter(value: string) {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    if (code <= 0x1f || code === 0x7f) return true
  }
  return false
}

/**
 * Validates only what a browser can know before the owner submits.
 * DNS pinning, public-address checks and the 100 MiB streamed budget stay server-side.
 */
export function validatePublicAudioUrl(value: string): PublicAudioUrlValidation {
  if (hasControlCharacter(value)) {
    return { ok: false, message: 'URL不能包含换行或控制字符' }
  }
  const raw = value.trim()
  if (!raw) return { ok: false, message: '请输入公开HTTPS音频地址' }
  if (raw.length > MAX_PUBLIC_AUDIO_URL_CHARS) {
    return { ok: false, message: `URL最多 ${MAX_PUBLIC_AUDIO_URL_CHARS} 字符` }
  }

  let url: URL
  try {
    url = new URL(raw)
  } catch {
    return { ok: false, message: 'URL格式无效' }
  }
  if (url.protocol !== 'https:') return { ok: false, message: '只允许HTTPS地址' }
  if (url.username || url.password) return { ok: false, message: 'URL不能包含账号密码' }
  if (url.hash) return { ok: false, message: 'URL不能包含片段' }
  if (!url.hostname) return { ok: false, message: 'URL缺少主机名' }
  return { ok: true, origin: url.origin }
}

/** Defense-in-depth redaction; server-side URL audit redaction remains authoritative. */
export function redactSourceUrl(value: string, sourceUrl: string): string {
  const raw = sourceUrl.trim()
  if (!raw) return value
  let output = value.split(raw).join(REDACTED_SOURCE_URL)
  try {
    const query = new URL(raw).search
    if (query) output = output.split(query).join(REDACTED_SOURCE_URL)
  } catch {
    // An invalid value cannot have a separately parseable query form.
  }
  return output
}
