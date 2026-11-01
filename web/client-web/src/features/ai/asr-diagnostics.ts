export interface AsrTranscriptSegment {
  startSeconds: number
  endSeconds: number
  speaker: string
  text: string
}

export interface AsrDiagnostics {
  recognized: boolean
  auditWrapper: boolean
  parsedAvailable: boolean
  multimodalWrapper: boolean
  audioTokens: number | null
  ignoredDirectAudioTokens: number | null
  empty: boolean
  repetition: boolean
  truncation: boolean
  timestampFallback: boolean
  formatDetected: boolean
  hasGaps: boolean
  hasOverlaps: boolean
  coverageSeconds: number | null
  segments: AsrTranscriptSegment[]
  issues: string[]
  finishReason: string | null
  contentSource: string | null
  transcriptText: string
}

type JsonObject = Record<string, unknown>

function object(value: unknown): JsonObject | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as JsonObject : null
}

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function text(value: unknown): string | null {
  return typeof value === 'string' ? value : null
}

function booleanFlag(flags: JsonObject | null, name: string) {
  return flags?.[name] === true
}

function parseSegment(value: unknown): AsrTranscriptSegment | null {
  const segment = object(value)
  if (!segment) return null
  const startSeconds = finiteNumber(segment.startSeconds)
  const endSeconds = finiteNumber(segment.endSeconds)
  const speaker = text(segment.speaker)
  const body = text(segment.text)
  if (startSeconds == null || endSeconds == null || startSeconds < 0 || endSeconds < startSeconds || !speaker || body == null) return null
  return { startSeconds, endSeconds, speaker, text: body }
}

function parseTranscriptLine(line: string, index: number): AsrTranscriptSegment | null {
  const match = /^(?<start>\d+(?:\.\d+)?)\s*[-–—]\s*(?<end>\d+(?:\.\d+)?)\s*\|\s*(?<speaker>[^:：]+?)\s*[:：]\s*(?<text>.+)$/.exec(line.trim())
  if (!match?.groups) return null
  const startSeconds = Number(match.groups.start)
  const endSeconds = Number(match.groups.end)
  const speaker = match.groups.speaker.trim()
  const body = match.groups.text.trim()
  if (!Number.isFinite(startSeconds) || !Number.isFinite(endSeconds) || startSeconds < 0 || endSeconds < startSeconds || !speaker || !body) return null
  return { startSeconds, endSeconds, speaker, text: body || `第 ${index + 1} 行` }
}

function transcriptSegments(transcript: JsonObject | null) {
  if (!Array.isArray(transcript?.segments)) return []
  return transcript.segments
    .map(parseSegment)
    .filter((segment): segment is AsrTranscriptSegment => segment !== null)
    .sort((left, right) => left.startSeconds - right.startSeconds || left.endSeconds - right.endSeconds)
}

function fallbackSegments(transcriptText: string) {
  return transcriptText
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .map(parseTranscriptLine)
    .filter((segment): segment is AsrTranscriptSegment => segment !== null)
    .sort((left, right) => left.startSeconds - right.startSeconds || left.endSeconds - right.endSeconds)
}

function coverageStats(segments: AsrTranscriptSegment[]) {
  if (!segments.length) return { coverageSeconds: null, hasGaps: false, hasOverlaps: false }
  let start = segments[0]!.startSeconds
  let end = segments[0]!.endSeconds
  let total = 0
  let hasGaps = false
  let hasOverlaps = false
  for (const segment of segments.slice(1)) {
    if (segment.startSeconds > end) {
      if (segment.startSeconds > end + 0.05) hasGaps = true
      total += end - start
      start = segment.startSeconds
      end = segment.endSeconds
    } else {
      if (segment.startSeconds < end) hasOverlaps = true
      end = Math.max(end, segment.endSeconds)
    }
  }
  return {
    coverageSeconds: Math.round((total + end - start) * 1000) / 1000,
    hasGaps,
    hasOverlaps,
  }
}

function readCanonicalAudioTokens(upstream: JsonObject) {
  const usage = object(upstream.usage)
  const details = object(usage?.prompt_tokens_details)
  return finiteNumber(details?.audio_tokens)
}

function readParsedAudioTokens(payload: JsonObject, upstream: JsonObject, parsedAvailable: boolean) {
  const canonical = readCanonicalAudioTokens(upstream)
  return canonical ?? (parsedAvailable ? finiteNumber(payload.audioTokens) : null)
}

function readDirectAudioTokens(upstream: JsonObject) {
  return finiteNumber(object(upstream.usage)?.audio_tokens)
}

function transcriptTextFromMessage(upstream: JsonObject) {
  const choices = upstream.choices
  if (!Array.isArray(choices)) return ''
  const message = object(object(choices[0])?.message)
  const content = text(message?.content)
  if (content?.trim()) return content
  const reasoning = text(message?.reasoning_content)
  return reasoning?.trim() ? reasoning : ''
}

function finishReasonFromMessage(upstream: JsonObject) {
  const choices = upstream.choices
  return Array.isArray(choices) ? text(object(choices[0])?.finish_reason) : null
}

/**
 * Reads the sanitized MiMo wrapper without trusting non-canonical audio-token aliases.
 * Unknown or malformed snapshots stay unrecognized instead of being presented as verified.
 */
export function parseAsrDiagnostics(rawJson: string | null | undefined): AsrDiagnostics | null {
  if (!rawJson?.trim()) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(rawJson)
  } catch {
    return null
  }
  const root = object(parsed)
  if (!root) return null

  // AiAsrCallService stores one bounded outer audit record: parsed is the sanitized
  // preset parser output, while raw is only the wire body when they differ.
  const auditWrapper = text(root.recordFormat) === 'asr-call-response-v1'
  const parsedPayload = auditWrapper ? object(root.parsed) : null
  const rawPayload = auditWrapper && !parsedPayload ? object(root.raw) : null
  const payload = parsedPayload ?? rawPayload ?? root
  const parsedAvailable = parsedPayload !== null
  const upstream = object(payload.upstream) ?? payload
  const transcript = object(payload.transcript)
  const flags = object(payload.flags)
  const isMimoWrapper = typeof payload.recordFormat === 'string' && payload.recordFormat.startsWith('mimo-multimodal')
  const looksLikeCompletion = object(upstream.usage) !== null || Array.isArray(upstream.choices)
  if (!auditWrapper && !isMimoWrapper && !transcript && !flags && !looksLikeCompletion) return null

  const wrappedText = text(transcript?.text)
  const transcriptText = wrappedText?.trim() ? wrappedText : transcriptTextFromMessage(upstream)
  const parsedSegments = transcriptSegments(transcript)
  const segments = parsedSegments.length ? parsedSegments : fallbackSegments(transcriptText)
  const issues = Array.isArray(transcript?.issues)
    ? transcript.issues.filter((issue): issue is string => typeof issue === 'string')
    : []
  const finishReason = text(payload.finishReason) ?? text(payload.finishReasonRaw) ?? finishReasonFromMessage(upstream)
  const stats = coverageStats(segments)
  const reportedCoverage = finiteNumber(transcript?.coverageSeconds)
  const noTranscript = !transcriptText.trim()
  const normalizedFinishReason = finishReason?.toLowerCase() ?? ''

  return {
    recognized: true,
    auditWrapper,
    parsedAvailable,
    multimodalWrapper: isMimoWrapper,
    audioTokens: readParsedAudioTokens(payload, upstream, parsedAvailable),
    ignoredDirectAudioTokens: readDirectAudioTokens(upstream),
    empty: booleanFlag(flags, 'empty') || normalizedFinishReason === 'incomplete:empty' || noTranscript,
    repetition: booleanFlag(flags, 'repetition') || normalizedFinishReason === 'incomplete:repetition' ||
      normalizedFinishReason === 'repetition',
    truncation: booleanFlag(flags, 'truncation') || normalizedFinishReason === 'length' ||
      normalizedFinishReason === 'max_tokens' || normalizedFinishReason.startsWith('incomplete:truncation') ||
      normalizedFinishReason === 'truncation',
    timestampFallback: booleanFlag(flags, 'timestampFallback') || (Boolean(transcriptText.trim()) && segments.length === 0),
    formatDetected: booleanFlag(flags, 'formatDetected') || parsedSegments.length > 0,
    hasGaps: booleanFlag(flags, 'hasGaps') || stats.hasGaps,
    hasOverlaps: booleanFlag(flags, 'hasOverlaps') || stats.hasOverlaps,
    coverageSeconds: reportedCoverage ?? stats.coverageSeconds,
    segments,
    issues,
    finishReason,
    contentSource: text(payload.contentSource),
    transcriptText,
  }
}

export function formatAsrSeconds(value: number | null) {
  if (value == null || !Number.isFinite(value)) return '未知'
  const roundedTenths = Math.round(value * 10)
  const minutes = Math.floor(roundedTenths / 600)
  const remainder = roundedTenths % 600
  const seconds = Math.floor(remainder / 10)
  const tenths = remainder % 10
  return `${minutes}:${String(seconds).padStart(2, '0')}${tenths ? `.${tenths}` : ''}`
}
