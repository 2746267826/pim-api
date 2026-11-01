import { useEffect, useMemo, useState } from 'react'
import { Button, InlineAlert, Skeleton, StatusBadge } from '@/components/ui'
import { formatAsrSeconds, parseAsrDiagnostics } from './asr-diagnostics'
import { MIMO_MULTIMODAL_PRESET_ID } from './asr-model-guidance'

const SEGMENTS_PER_PAGE = 100

function metadataPresetId(metadataJson: string | null | undefined) {
  if (!metadataJson?.trim()) return null
  try {
    const parsed: unknown = JSON.parse(metadataJson)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null
    const presetId = (parsed as Record<string, unknown>).asrPresetId
    return typeof presetId === 'string' ? presetId : null
  } catch {
    return null
  }
}

export function AsrResultInsights({ rawJson, presetId = null, metadataJson = null, loading = false, loadError = null }: {
  rawJson?: string | null
  presetId?: string | null
  metadataJson?: string | null
  loading?: boolean
  loadError?: string | null
}) {
  const diagnostics = useMemo(() => parseAsrDiagnostics(rawJson), [rawJson])
  const declaredPreset = presetId ?? metadataPresetId(metadataJson)
  const multimodal = declaredPreset === MIMO_MULTIMODAL_PRESET_ID || diagnostics?.multimodalWrapper === true
  const [page, setPage] = useState(1)
  const totalSegments = diagnostics?.segments.length ?? 0
  useEffect(() => setPage(1), [rawJson, totalSegments])
  const legacySummary = <InlineAlert tone="neutral" title="非多模态ASR记录">
    该记录未声明 MiMo 多模态预设，沿用旧 ASR 专用接口语义，不套用 canonical audio_tokens 成功门槛。
  </InlineAlert>
  if (loading) return <Skeleton className="h-20" />
  if (loadError) return <InlineAlert tone="warn" title="ASR结果诊断读取失败">{loadError}；请打开完整记录核对，未核验内容不视为成功。</InlineAlert>
  if (!rawJson?.trim()) return null
  if (declaredPreset !== MIMO_MULTIMODAL_PRESET_ID && diagnostics?.multimodalWrapper !== true &&
      declaredPreset != null) return legacySummary
  if (!diagnostics?.recognized) return <InlineAlert tone="warn" title="ASR结果无法核验">
    响应快照没有可识别的 audio_tokens、语义标记或转写分段；请打开完整记录人工核对，不能据此确认成功。
  </InlineAlert>
  if (!multimodal) return legacySummary

  const tokenMissing = diagnostics.audioTokens == null || diagnostics.audioTokens <= 0
  const critical = tokenMissing || diagnostics.empty || diagnostics.repetition || diagnostics.truncation
  const totalPages = Math.max(1, Math.ceil(diagnostics.segments.length / SEGMENTS_PER_PAGE))
  const currentPage = Math.min(page, totalPages)
  const visibleSegments = diagnostics.segments.slice((currentPage - 1) * SEGMENTS_PER_PAGE, currentPage * SEGMENTS_PER_PAGE)
  return <section aria-label="ASR结果核验" className="space-y-3 rounded-ctl border border-border p-3">
    <div className="flex flex-wrap items-center gap-2">
      <h3 className="text-sm font-medium text-text-1">ASR结果核验</h3>
      {!tokenMissing && <StatusBadge tone="ok" dot={false}>audio_tokens {diagnostics.audioTokens}</StatusBadge>}
      {diagnostics.empty && <StatusBadge tone="crit" dot={false}>空转写</StatusBadge>}
      {diagnostics.repetition && <StatusBadge tone="crit" dot={false}>重复异常</StatusBadge>}
      {diagnostics.truncation && <StatusBadge tone="crit" dot={false}>输出截断</StatusBadge>}
      {!critical && <StatusBadge tone="ok" dot={false}>基础语义检查通过</StatusBadge>}
    </div>
    {tokenMissing && <InlineAlert tone="crit" title="音频处理未确认">
      <p>仅接受 canonical <code>usage.prompt_tokens_details.audio_tokens &gt; 0</code>；当前字段{diagnostics.audioTokens === 0 ? '为 0' : '缺失'}，不得视为成功。</p>
      {diagnostics.ignoredDirectAudioTokens != null && <p className="mt-1">已忽略非标准 <code>usage.audio_tokens={diagnostics.ignoredDirectAudioTokens}</code>，它不能证明音频已被处理。</p>}
    </InlineAlert>}
    {diagnostics.empty && <InlineAlert tone="crit" title="返回空转写">请试听原音频并人工核对；页面不会自动重试或据此发布笔记。</InlineAlert>}
    {diagnostics.repetition && <InlineAlert tone="crit" title="检测到重复输出">转写可能未完整完成，请人工核对后再使用。</InlineAlert>}
    {diagnostics.truncation && <InlineAlert tone="crit" title="输出达到截断条件">结束原因为 {diagnostics.finishReason ?? '截断'}，请分段处理或人工核对。</InlineAlert>}
    {diagnostics.timestampFallback && <InlineAlert tone="warn" title="时间戳为回退状态">
      未检测到可验证的 <code>起始秒-结束秒 | 说话人: 文本</code> 时间轴。纯文本仍可查看，但不能当作已校验的分段或覆盖范围。
    </InlineAlert>}
    {diagnostics.audioTokens != null && diagnostics.audioTokens > 0 && !critical && <p className="text-xs text-text-3">
      模型估算时间轴{diagnostics.formatDetected ? '（格式已识别）' : '（格式未识别）'} · 区间并集覆盖 {formatAsrSeconds(diagnostics.coverageSeconds)}
      {diagnostics.hasGaps ? ' · 存在空隙' : ''}{diagnostics.hasOverlaps ? ' · 存在重叠' : ''}
    </p>}
    {diagnostics.segments.length > 0 && <div className="text-xs text-text-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="font-medium text-text-2">分段结果（{diagnostics.segments.length}）</p>
        <p>第 {currentPage} / {totalPages} 页</p>
      </div>
      <p className="mt-1 rounded-ctl bg-warn-soft px-2 py-1.5 text-warn">模型估算，非强制对齐；时间与说话人用于核对，不代表逐字准确或已对齐原音。</p>
      <ol className="mt-2 space-y-2">
        {visibleSegments.map((segment, index) => <li key={`${currentPage}-${segment.startSeconds}-${segment.endSeconds}-${index}`} className="rounded-ctl bg-surface px-3 py-2">
          <p className="font-mono text-[11px] text-text-3">{formatAsrSeconds(segment.startSeconds)}–{formatAsrSeconds(segment.endSeconds)} | {segment.speaker}</p>
          <p className="mt-1 whitespace-pre-wrap break-words text-sm text-text-1">{segment.text}</p>
        </li>)}
      </ol>
      {totalPages > 1 && <div className="mt-2 flex items-center justify-end gap-2">
        <Button type="button" size="sm" variant="secondary" aria-label="上一页分段" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>上一页</Button>
        <Button type="button" size="sm" variant="secondary" aria-label="下一页分段" disabled={currentPage >= totalPages} onClick={() => setPage(currentPage + 1)}>下一页</Button>
      </div>}
    </div>}
    {diagnostics.issues.length > 0 && <details className="text-xs text-warn">
      <summary className="cursor-pointer">解析问题（{diagnostics.issues.length}）</summary>
      <ul className="mt-2 list-disc space-y-1 pl-5">{diagnostics.issues.map((issue, index) => <li key={`${issue}-${index}`}>{issue}</li>)}</ul>
    </details>}
  </section>
}
