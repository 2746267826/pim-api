export const DIAGNOSTIC_REDACTION_MARKER = '[已脱敏]'
export const DIAGNOSTIC_PATH_MARKER = '<path>'

const SENSITIVE_ATTRIBUTE_NAMES = new Set([
  'authorization',
  'access_token',
  'accesstoken',
  'refresh_token',
  'refreshtoken',
  'token',
  'api_key',
  'apikey',
  'client_secret',
  'clientsecret',
  'secret',
  'password',
  'graphEventId',
  'changeKey',
  'signature',
  'cookie',
  'set-cookie',
  'private_key',
  'privatekey',
  'credential',
  'credentials',
])

const REDACTIONS: Array<[RegExp, string]> = [
  [
    /("(?:authorization|access_token|refresh_token|token|api_key|apikey|client_secret|secret|password|graphEventId|changeKey|signature|cookie|set-cookie|private_key|privatekey|credential|credentials)"\s*:\s*)"[^"]*"/gi,
    `$1"${DIAGNOSTIC_REDACTION_MARKER}"`,
  ],
  [
    /([?&](?:access_token|refresh_token|token|api_key|apikey|client_secret|secret|password|graphEventId|changeKey|signature|sig|cookie|private_key|credential)=)[^&\s#]+/gi,
    `$1${DIAGNOSTIC_REDACTION_MARKER}`,
  ],
  [
    /\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}/gi,
    `$1 ${DIAGNOSTIC_REDACTION_MARKER}`,
  ],
  [
    /\b(authorization|x-pim-ops-key|x-api-key|x-goog-api-key|access[-_]?token|refresh[-_]?token|token|api[-_]?key|client[-_]?secret|graphEventId|changeKey|password|signature|secret|cookie|set-cookie|private[-_]?key|credential|credentials)\b(\s*[:=]\s*)[^\s,;}\]]+/gi,
    `$1$2${DIAGNOSTIC_REDACTION_MARKER}`,
  ],
  [
    /\b(?:sk|rk|gsk|hf|xai)[-_][A-Za-z0-9_-]{16,}\b|\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bxox[baprs]-[A-Za-z0-9-]{10,}\b|\bAIza[0-9A-Za-z_-]{30,}\b|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/gi,
    DIAGNOSTIC_REDACTION_MARKER,
  ],
  [
    /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----/gi,
    DIAGNOSTIC_REDACTION_MARKER,
  ],
  [/(https?:\/\/)[^/@\s:]+:[^/@\s]+@/gi, `$1${DIAGNOSTIC_REDACTION_MARKER}@`],
  [
    /\b[A-Z]:\\(?:Users\\[^\\\s]+\\AppData\\Local\\Temp|Windows\\Temp|Temp)\\[^\r\n\t"'<>|]+/gi,
    DIAGNOSTIC_PATH_MARKER,
  ],
  [/\b[A-Z]:\\Users\\[^\\\s]+\\[^\r\n\t"'<>|]+/gi, DIAGNOSTIC_PATH_MARKER],
  [/(?<!\w)\/(?:tmp|var\/tmp|private\/var\/folders|var\/folders)\/[^\s"'<>]+/g, DIAGNOSTIC_PATH_MARKER],
  [/(?<!\w)\/(?:data\/user\/0|data\/data|storage\/emulated\/0)\/[^\s"'<>]+/g, DIAGNOSTIC_PATH_MARKER],
  [/(?<!\w)\/home\/[^/\s]+\/[^\s"'<>]+/g, DIAGNOSTIC_PATH_MARKER],
  [/(?<!\w)\/(?:data\/pim|app|opt|var\/log|root)(?:\/[^\s"'<>]+)?/g, DIAGNOSTIC_PATH_MARKER],
  [/(?<!\w)\/Users\/[^/\s]+\/[^\s"'<>]+/g, DIAGNOSTIC_PATH_MARKER],
  [/\b[A-Z]:\\[^\r\n\t"'<>|]+/gi, DIAGNOSTIC_PATH_MARKER],
  [/\bfile:\/\/\/[^\s"'<>]+/gi, DIAGNOSTIC_PATH_MARKER],
]

export function truncateDiagnosticText(value: string, maxLength: number): string {
  return value.length <= maxLength ? value : value.slice(0, maxLength)
}

export function redactDiagnosticText(value: string | null | undefined, maxLength: number): string {
  if (!value?.trim()) return ''
  let text = value.replaceAll('\0', '')
  for (const [pattern, replacement] of REDACTIONS) {
    text = text.replace(pattern, replacement)
  }
  return truncateDiagnosticText(text.trim(), maxLength)
}

export function redactDiagnosticAttributes(
  attributes: Record<string, string> | null | undefined,
): Record<string, string> {
  if (!attributes) return {}
  const result: Record<string, string> = {}
  for (const [rawKey, rawValue] of Object.entries(attributes)) {
    if (Object.keys(result).length >= 16) break
    const key = truncateDiagnosticText(rawKey.trim(), 64)
    if (!key || isSensitiveAttributeName(key)) continue
    if (redactDiagnosticText(key, 64) !== key) continue
    result[key] = redactDiagnosticText(rawValue, 512)
  }
  return result
}

function isSensitiveAttributeName(name: string): boolean {
  if (SENSITIVE_ATTRIBUTE_NAMES.has(name)) return true
  const normalized = name.toLowerCase().replace(/[^a-z0-9]/g, '')
  return normalized.includes('token')
    || normalized.includes('secret')
    || normalized.includes('apikey')
    || normalized.includes('authorization')
    || normalized.includes('grapheventid')
    || normalized.includes('changekey')
    || normalized.includes('password')
    || normalized.includes('signature')
    || normalized.includes('cookie')
    || normalized.includes('privatekey')
    || normalized.includes('credential')
}
