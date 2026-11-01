import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Card, EmptyState, Input, Label, Select, Skeleton } from '@/components/ui'
import { ApiError } from '@/api/client'
import { STORAGE_KEYS, getJSON, setJSON } from '@/lib/storage'
import {
  aiApi,
  type AsrJobResult,
  type AsrJobStatus,
  type AsrPreset,
  type AsrRequest,
  type AsrUrlRequest,
  type AsrUrlResult,
  type Model,
  type ModelSettings,
} from './api'
import { createAiRequestId } from './request-id'
import { readMediaFile } from './media-file'
import { asrAudioLimitLabel, isMimoPro } from './asr-model-guidance'
import { AsrResultInsights } from './asr-result-insights'
import { MAX_PUBLIC_AUDIO_URL_CHARS, redactSourceUrl, validatePublicAudioUrl } from './asr-url-source'

const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败'
const languageNames: Record<string, string> = { auto: '自动识别', zh: '中文', en: '英语' }
type SourceKind = 'file' | 'url'
const terminalJobStatuses: AsrJobStatus[] = ['Succeeded', 'Incomplete', 'Failed', 'Cancelled', 'Blocked', 'TimedOut']
const GATEWAY_STATUSES = new Set([502, 503, 504, 524])
const POLL_RETRY_DELAYS = [1500, 3000, 6000, 12000, 20000]

function isJobTerminal(status: AsrJobStatus) {
  return terminalJobStatuses.includes(status)
}

function jobGuidance(result: AsrJobResult) {
  const d = result.diagnostics
  if (result.status === 'Queued') return '任务已持久化排队。关闭页面或停止轮询不会取消服务端任务。'
  if (result.status === 'Succeeded') return '后台转写已完成，结果已从持久记录读取。'
  if (result.status === 'Running') return '后台正在调用ASR服务商。浏览器连接已不再承担等待，离开页面不会切断模型调用。'
  if (result.status === 'Cancelling') return '已发出显式取消。服务商可能已开始处理，完成前结果仍不能视为已知。'
  if (d.outputLimitExceeded) return '估算输出超过当前 token 上限，本次在发送服务商前已阻止。请提高上限、改用 Batch 或缩短音频。'
  if (d.abortSource === 'user_cancel_late') return '取消到达时服务商已完成；结果已保留，请核对后使用。'
  if (d.abortSource.startsWith('user_cancel')) return d.outcomeKnown
    ? '用户已明确取消；任务在发送服务商前停止。'
    : '用户已请求取消，但服务商结果未知，可能已处理或计费；请核对调用记录，不要自动重发。'
  if (d.abortSource === 'provider_timeout') return 'ASR服务商超时，结果未知。可核对模型超时、改用 Batch 或缩短音频；不要自动重发。'
  if (d.abortSource === 'worker_timeout') return '后台执行超时，服务商结果未知。请核对调用记录后再决定是否新建请求。'
  if (d.abortSource === 'worker_lost') return '后台执行租约中断，服务商结果未知。任务不会自动重发。'
  if (d.gatewayEvidenceSource === 'client_http_response') return `浏览器真实收到 HTTP ${d.gatewayStatus ?? '网关错误'}${d.gatewayName ? `（${d.gatewayName}）` : ''}；这是传输层证据，不覆盖已保存的服务商结果。`
  if (d.gatewayEvidenceSource === 'client_network_failure') return '浏览器未收到 HTTP 响应；只能确认网络传输失败，不能推断网关或服务商状态。'
  if (d.abortSource === 'caller_disconnect') return '旧同步调用的浏览器连接已断开且没有网关响应证据；服务商结果未知，请核对记录。'
  if (d.abortSource === 'upstream_http') return `ASR服务商返回 HTTP ${d.upstreamStatus ?? '未知'}；本次未自动重试。`
  if (result.status === 'Blocked') return '任务未发送服务商，请按下一步说明修正后明确新建请求。'
  if (result.status === 'Incomplete') return '转写未完整确认，请核对结束原因与实际回复。'
  if (result.status === 'TimedOut') return '任务超时且结果未知，请核对服务商记录。'
  if (result.status === 'Cancelled') return d.outcomeKnown ? '任务已取消。' : '任务已停止，但上游结果未知。'
  return result.errorMessage ?? '任务未成功，请核对调用记录。'
}

function transportGuidance(diagnostics: NonNullable<AsrUrlResult['diagnostics']>) {
  if (diagnostics.gatewayEvidenceSource === 'client_http_response') {
    return `浏览器确实收到网关 HTTP ${diagnostics.gatewayStatus ?? '错误'}${diagnostics.gatewayName ? `（${diagnostics.gatewayName}）` : ''}；这是传输层证据，不改变已保存的服务商结果。`
  }
  if (diagnostics.gatewayEvidenceSource === 'client_network_failure') {
    return '浏览器未收到 HTTP 响应；只能确认网络传输失败，不能推断网关或服务商状态。'
  }
  if (diagnostics.abortSource === 'caller_disconnect') {
    return '服务端等待连接已中断，但当前没有浏览器实际收到网关响应的证据；服务商结果未知，请核对记录。'
  }
  if (diagnostics.abortSource === 'provider_timeout') {
    return 'ASR服务商请求超时，结果未知。请核对模型超时和调用记录，不要自动重发。'
  }
  if (diagnostics.abortSource.startsWith('user_cancel')) {
    return diagnostics.outcomeKnown
      ? '用户已明确取消；本次在发送服务商前停止。'
      : '用户已请求取消，但服务商结果未知；请核对调用记录，不要自动重发。'
  }
  if (diagnostics.abortSource === 'upstream_http') {
    return `ASR服务商返回 HTTP ${diagnostics.upstreamStatus ?? '未知'}；本次未自动重试。`
  }
  return diagnostics.nextAction
}

function operation(model: Model | undefined, preset: AsrPreset | undefined) {
  if (!model || !preset) return ''
  try { return new URL(preset.operationPath, model.baseUrl).href } catch { return '' }
}

function audioNameFromUrl(value: string) {
  try {
    const url = new URL(value.trim())
    const last = url.pathname.split('/').filter(Boolean).at(-1)
    if (!last) return 'remote-audio'
    const decoded = decodeURIComponent(last)
    return decoded.length > 0 && decoded.length <= 255 ? decoded : 'remote-audio'
  } catch {
    return value.trim() ? 'remote-audio' : ''
  }
}

function formatFromUrl(value: string, formats: readonly string[]) {
  try {
    const url = new URL(value.trim())
    const last = url.pathname.split('/').filter(Boolean).at(-1) ?? ''
    const extension = last.toLowerCase().split('.').at(-1) ?? ''
    return formats.includes(extension) ? extension : ''
  } catch {
    return ''
  }
}

function originOf(value: string) {
  try { return new URL(value).origin } catch { return '来源主机未知' }
}

function AudioPreview({ file }: { file: File }) {
  const attach = useCallback((node: HTMLAudioElement | null) => {
    if (!node) return
    const url = URL.createObjectURL(file); node.src = url
    return () => { node.removeAttribute('src'); URL.revokeObjectURL(url) }
  }, [file])
  return <audio ref={attach} controls preload="metadata" aria-label="待转写音频" className="w-full" />
}

export function AsrCall({ userId, models, settings, changed, showDetail, renderStatus }: {
  userId: string; models: Model[]; settings: ModelSettings; changed: () => void; showDetail: (id: string) => void; renderStatus: (state: string) => ReactNode
}) {
  const presets = useQuery({ queryKey: ['ai-asr-presets'], queryFn: ({ signal }) => aiApi.asrPresets(signal) })
  const [selected, setSelected] = useState(''); const [language, setLanguage] = useState('auto')
  const [sourceKind, setSourceKind] = useState<SourceKind>('file')
  const [file, setFile] = useState<File | null>(null); const [selection, setSelection] = useState(''); const [consentedTo, setConsentedTo] = useState('')
  const [audioUrl, setAudioUrl] = useState(''); const [urlName, setUrlName] = useState(''); const [urlFormat, setUrlFormat] = useState('')
  const [urlSourceConfirmed, setUrlSourceConfirmed] = useState(false); const [urlProviderFetchConfirmed, setUrlProviderFetchConfirmed] = useState(false)
  const [submitted, setSubmitted] = useState<{ id: string; model: Model; preset: AsrPreset } | null>(null)
  const [urlSubmitted, setUrlSubmitted] = useState<{ id: string; body: AsrUrlRequest; model: Model; preset: AsrPreset } | null>(null)
  const [resumeReceipt, setResumeReceipt] = useState<{ owner: string; clientRequestId: string } | null>(() => {
    const receipt = getJSON<{ owner?: string; clientRequestId?: string } | null>(
      STORAGE_KEYS.asrJobReceipt, null)
    return receipt?.owner && receipt.clientRequestId
      ? { owner: receipt.owner, clientRequestId: receipt.clientRequestId } : null
  })
  const [urlLookupMissing, setUrlLookupMissing] = useState(false)
  const [pending, setPending] = useState(false); const [result, setResult] = useState<AsrJobResult | null>(null)
  const [urlResult, setUrlResult] = useState<AsrUrlResult | null>(null); const [message, setMessage] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null); const pollAbort = useRef<AbortController | null>(null)
  const inFlight = useRef(false); const input = useRef<HTMLInputElement | null>(null)
  const pollFailures = useRef(0)
  const activeOwner = useRef(userId); const ownerEpoch = useRef(0)
  const resumeId = resumeReceipt?.owner === userId ? resumeReceipt.clientRequestId : null
  useEffect(() => () => { abort.current?.abort(); pollAbort.current?.abort() }, [])
  useEffect(() => {
    if (activeOwner.current === userId) return
    activeOwner.current = userId
    ownerEpoch.current += 1
    abort.current?.abort(); pollAbort.current?.abort()
    inFlight.current = false; pollFailures.current = 0
    const receipt = getJSON<{ owner?: string; clientRequestId?: string } | null>(
      STORAGE_KEYS.asrJobReceipt, null)
    setResumeReceipt(receipt?.owner === userId && receipt.clientRequestId
      ? { owner: receipt.owner, clientRequestId: receipt.clientRequestId } : null)
    setSelected(''); setSourceKind('file'); setFile(null); setSelection(''); setConsentedTo('')
    setAudioUrl(''); setUrlName(''); setUrlFormat('')
    setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false)
    setSubmitted(null); setUrlSubmitted(null); setUrlLookupMissing(false)
    setPending(false); setResult(null); setUrlResult(null); setMessage(null)
  }, [userId])
  const current = models.find((m) => m.id === (selected || settings.transcriptionModelId))
  const currentPreset = presets.data?.find((p) => p.id === current?.asrPresetId)
  const target = submitted?.model ?? urlSubmitted?.model ?? current
  const preset = submitted?.preset ?? urlSubmitted?.preset ?? currentPreset
  const activeLogId = result?.logId ?? urlResult?.logId
  const detail = useQuery({ queryKey: ['ai', userId, 'asr-result-detail', activeLogId], queryFn: ({ signal }) => aiApi.detail(activeLogId!, signal), enabled: Boolean(activeLogId) })
  const endpoint = operation(target, preset)
  const approvalKey = target && preset
    ? `${target.id}/${target.revision}/${preset.id}/${preset.version}/${endpoint}/${sourceKind === 'url' ? audioUrl : selection}/${language}`
    : ''
  const ready = settings.enabled && current?.enabled && current.capabilities.includes('Transcription') && currentPreset
  const locked = pending || submitted !== null || urlSubmitted !== null
  const format = file?.name.toLowerCase().split('.').at(-1) ?? ''
  const urlSupported = preset?.id === 'mimo-multimodal'
  const urlValidation = sourceKind === 'url' ? validatePublicAudioUrl(audioUrl) : null
  const urlValid = urlValidation?.ok === true
  const urlReady = Boolean(sourceKind === 'url' && urlSupported && urlValid && urlName.trim() && urlFormat && preset?.formats.includes(urlFormat))
  const fileConsent = Boolean(file && approvalKey && consentedTo === approvalKey)
  const urlConsent = Boolean(urlReady && approvalKey && consentedTo === approvalKey && urlSourceConfirmed && urlProviderFetchConfirmed)
  const canSend = sourceKind === 'file' ? Boolean(ready && fileConsent) : Boolean(ready && urlConsent)
  const reportTransport = useCallback(async (id: string,
    transportKind: 'AsyncSubmit' | 'AsyncPoll' | 'SyncUrlCall',
    error: unknown): Promise<string | null> => {
    if (error instanceof DOMException && error.name === 'AbortError') return null
    const isHttpResponse = error instanceof ApiError && GATEWAY_STATUSES.has(error.status)
    const isNetworkFailure = error instanceof TypeError
    if (!isHttpResponse && !isNetworkFailure) return null
    try {
      const observation = await aiApi.asrTransportObserve({
        clientRequestId: id,
        expectedUserId: userId,
        transportKind,
        source: isHttpResponse ? 'ClientHttpResponse' : 'ClientNetworkFailure',
        gatewayStatus: isHttpResponse ? (error as ApiError).status : null,
        gatewayName: isHttpResponse ? (error as ApiError).gatewayName : null,
      })
      if (observation.gatewayStatus != null) {
        return `已记录浏览器真实收到 HTTP ${observation.gatewayStatus}${observation.gatewayName ? `（${observation.gatewayName}）` : ''}；服务端 ASR 结果未被改写。`
      }
      return '已记录浏览器网络连接失败；无法据此确认是浏览器、网关还是服务端断开。'
    } catch {
      return isHttpResponse
        ? `浏览器真实收到 HTTP ${(error as ApiError).status}，但证据回传失败；中断位置仍不能由套接字断开推断。`
        : '浏览器网络连接失败，证据回传未确认；中断位置未知。'
    }
  }, [userId])
  useEffect(() => {
    if (!submitted || (result && isJobTerminal(result.status))) return
    let disposed = false
    let timer = 0
    const schedule = () => {
      const delay = POLL_RETRY_DELAYS[Math.min(pollFailures.current,
        POLL_RETRY_DELAYS.length - 1)]
      timer = window.setTimeout(() => { void poll() }, delay)
    }
    const poll = async () => {
      if (disposed) return
      const controller = new AbortController()
      pollAbort.current = controller
      try {
        const next = await aiApi.asrJobLookup(submitted.id, controller.signal)
        if (disposed) return
        pollFailures.current = 0
        setResult(next)
        if (isJobTerminal(next.status)) { changed(); return }
      } catch (error) {
        if (disposed || (error instanceof DOMException && error.name === 'AbortError')) return
        pollFailures.current += 1
        const evidence = await reportTransport(submitted.id, 'AsyncPoll', error)
        if (disposed) return
        setMessage(evidence ?? '轮询连接暂时失败；已按有限退避继续查询，后台任务不会因此取消。')
      }
      if (!disposed) schedule()
    }
    schedule()
    return () => {
      disposed = true
      window.clearTimeout(timer)
      pollAbort.current?.abort()
    }
  }, [submitted, result, changed, reportTransport])

  useEffect(() => {
    if (!resumeId || submitted || !presets.data) return
    const model = models.find((item) => item.id === settings.transcriptionModelId) ?? models[0]
    const restoredPreset = presets.data.find((item) => item.id === model?.asrPresetId)
    if (!model || !restoredPreset) return
    const timer = window.setTimeout(() => {
      setSubmitted({ id: resumeId, model, preset: restoredPreset })
      setResult(null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [resumeId, submitted, presets.data, models, settings.transcriptionModelId])

  const chooseFile = (next: File | null) => {
    setConsentedTo(''); setSelection(createAiRequestId()); setResult(null); setUrlResult(null); setUrlLookupMissing(false)
    const kind = next?.name.toLowerCase().split('.').at(-1)
    if (next && (!preset || !kind || !preset.formats.includes(kind) || next.size < 1 || next.size > preset.maxAudioBytes)) {
      setFile(null); setMessage('音频格式或大小不符合当前预设，请查看下方限制。'); return
    }
    setFile(next); setMessage(null)
  }

  const chooseUrl = (next: string) => {
    setAudioUrl(next); setConsentedTo(''); setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false)
    setUrlResult(null); setUrlLookupMissing(false); setMessage(null)
    setUrlName(audioNameFromUrl(next)); setUrlFormat(formatFromUrl(next, preset?.formats ?? []))
  }

  async function send() {
    if (inFlight.current || !file || !target || !preset || !ready || submitted || urlSubmitted || !fileConsent || sourceKind !== 'file') return
    const epoch = ownerEpoch.current
    const stillCurrent = () => ownerEpoch.current === epoch && activeOwner.current === userId
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    const id = createAiRequestId(); setSubmitted({ id, model: target, preset })
    setResumeReceipt({ owner: userId, clientRequestId: id })
    setJSON(STORAGE_KEYS.asrJobReceipt, { owner: userId, clientRequestId: id })
    pollFailures.current = 0; setPending(true); setResult(null); setMessage(null)
    try {
      const body: AsrRequest = { clientRequestId: id, expectedUserId: userId, modelId: selected || null, language,
        audio: { name: file.name, format, base64: await readMediaFile(file, controller.signal) },
        consent: { modelId: target.id, modelRevision: target.revision, endpoint, presetId: preset.id, presetVersion: preset.version, confirmed: true } }
      const next = await aiApi.asrJobSubmit(body, controller.signal)
      if (stillCurrent()) setResult(next)
    } catch (error) {
      if (!stillCurrent()) return
      const evidence = await reportTransport(id, 'AsyncSubmit', error)
      if (stillCurrent()) setMessage(controller.signal.aborted
        ? '浏览器提交连接已停止；这不等于用户取消服务端任务。请按原标识查询，不要重新提交。'
        : evidence ?? errorText(error) + '；已保留原标识，页面不会自动重发。')
    } finally {
      if (stillCurrent()) { inFlight.current = false; setPending(false) }
    }
  }

  async function sendUrl() {
    if (inFlight.current || !urlReady || !urlValid || !target || !preset || !ready || urlSubmitted || submitted || !urlConsent) return
    const epoch = ownerEpoch.current
    const stillCurrent = () => ownerEpoch.current === epoch && activeOwner.current === userId
    const sourceUrl = audioUrl.trim()
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    const id = createAiRequestId()
    const body: AsrUrlRequest = {
      clientRequestId: id,
      expectedUserId: userId,
      modelId: selected || null,
      language,
      audio: {
        name: urlName.trim(),
        format: urlFormat,
        url: sourceUrl,
        sourceConfirmed: true,
        providerFetchConfirmed: true,
      },
      consent: { modelId: target.id, modelRevision: target.revision, endpoint, presetId: preset.id, presetVersion: preset.version, confirmed: true },
    }
    setUrlSubmitted({ id, body, model: target, preset }); setPending(true); setUrlResult(null); setUrlLookupMissing(false); setMessage(null)
    try {
      const next = await aiApi.asrUrlCall(body, controller.signal)
      if (stillCurrent()) setUrlResult(next)
    } catch (error) {
      if (!stillCurrent()) return
      const evidence = await reportTransport(id, 'SyncUrlCall', error)
      if (stillCurrent()) setMessage(controller.signal.aborted
        ? '已请求取消，请核对URL ASR记录，上游结果以实际记录为准。'
        : evidence ?? redactSourceUrl(errorText(error), sourceUrl) + '；请先核对记录，页面不会自动重发。')
    } finally {
      if (stillCurrent()) { inFlight.current = false; setPending(false); changed() }
    }
  }

  async function lookupUrl() {
    if (!urlSubmitted || pending) return
    const epoch = ownerEpoch.current
    setPending(true); setMessage(null)
    try {
      const next = await aiApi.asrUrlLookup(urlSubmitted.id)
      if (ownerEpoch.current === epoch) {
        setUrlResult(next); setUrlLookupMissing(false)
      }
    } catch (error) {
      if (ownerEpoch.current !== epoch) return
      if (error instanceof ApiError && error.status === 404) {
        setUrlLookupMissing(true)
        setMessage('服务端尚未找到该URL请求标识；保留原ID，未自动重发。如确认重试，请使用“按原标识重发URL”。')
      } else {
        setMessage(redactSourceUrl(errorText(error), urlSubmitted.body.audio.url) + '；请保留原标识并核对记录。')
      }
    } finally { if (ownerEpoch.current === epoch) setPending(false) }
  }

  async function resendUrl() {
    if (!urlSubmitted || !urlLookupMissing || pending) return
    const epoch = ownerEpoch.current
    const stillCurrent = () => ownerEpoch.current === epoch && activeOwner.current === userId
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    setPending(true); setMessage(null); setUrlLookupMissing(false)
    try {
      const next = await aiApi.asrUrlCall(urlSubmitted.body, controller.signal)
      if (stillCurrent()) setUrlResult(next)
    } catch (error) {
      if (!stillCurrent()) return
      setMessage(controller.signal.aborted
        ? '已请求取消，请核对URL ASR记录，上游结果以实际记录为准。'
        : redactSourceUrl(errorText(error), urlSubmitted.body.audio.url) + '；保留原标识，页面不会自动重发。')
    } finally {
      if (stillCurrent()) { inFlight.current = false; setPending(false); changed() }
    }
  }

  function newCall() {
    setSubmitted(null); setUrlSubmitted(null); setResumeReceipt(null); setConsentedTo('')
    setJSON(STORAGE_KEYS.asrJobReceipt, null)
    setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false); setUrlLookupMissing(false)
    setResult(null); setUrlResult(null); setMessage(null)
    setAudioUrl(''); setUrlName(''); setUrlFormat('')
  }

  async function cancelJob() {
    if (!submitted || !result || isJobTerminal(result.status) || pending) return
    const epoch = ownerEpoch.current
    setPending(true)
    try {
      const next = await aiApi.asrJobCancel(submitted.id)
      if (ownerEpoch.current === epoch) { setResult(next); setMessage(null) }
    } catch (error) {
      if (ownerEpoch.current === epoch)
        setMessage(errorText(error) + '；后台任务可能仍在运行，请查询原标识。')
    } finally { if (ownerEpoch.current === epoch) setPending(false) }
  }

  async function lookupJob() {
    if (!submitted) return
    const epoch = ownerEpoch.current
    try {
      const value = await aiApi.asrJobLookup(submitted.id)
      if (ownerEpoch.current === epoch) { setResult(value); setMessage(null); changed() }
    } catch (error) {
      if (ownerEpoch.current !== epoch) return
      setMessage(errorText(error))
      const evidence = await reportTransport(submitted.id, 'AsyncPoll', error)
      if (evidence && ownerEpoch.current === epoch) setMessage(evidence)
    }
  }

  async function resumeJob() {
    if (!resumeId || pending) return
    const epoch = ownerEpoch.current
    setPending(true); setMessage(null)
    try {
      const value = await aiApi.asrJobLookup(resumeId)
      if (ownerEpoch.current !== epoch) return
      if (target && preset) setSubmitted({ id: resumeId, model: target, preset })
      setResult(value); setResumeReceipt(null); pollFailures.current = 0; changed()
    } catch (error) {
      if (ownerEpoch.current !== epoch) return
      setMessage(error instanceof ApiError && error.status === 404
        ? '原标识尚无已接受任务；为防止重复扣费未自动重发。'
        : '恢复查询暂不可用；原标识仍保留，可稍后再次恢复。')
    } finally { if (ownerEpoch.current === epoch) setPending(false) }
  }

  return <Card className="min-w-0 space-y-4 p-4 sm:p-5"><div><h2 className="text-sm font-semibold text-text-1">ASR音频转写</h2><p className="mt-1 text-xs text-text-3">通过所选预设发送单条音频，保存实际请求与转写。本入口用于单片调用；课堂长音频仍走分段任务。公开URL模式不会把完整地址写入本地存储或日志。</p></div>
    {presets.isLoading ? <Skeleton className="h-16" /> : presets.isError ? <EmptyState title="ASR预设加载失败" action={<Button onClick={() => void presets.refetch()}>重试</Button>} /> : null}
    <div><Label htmlFor="ai-asr-model">本次ASR模型</Label><Select id="ai-asr-model" ariaLabel="本次ASR模型" value={selected} disabled={locked}
      options={[{ value: '', label: '使用默认ASR模型' }, ...models.filter((m) => m.enabled && m.asrPresetId && m.capabilities.includes('Transcription')).map((m) => ({ value: m.id, label: m.name }))]}
      onValueChange={(value) => {
        setSelected(value); setConsentedTo(''); setFile(null); setLanguage('auto')
        setSourceKind('file'); setAudioUrl(''); setUrlName(''); setUrlFormat('')
        setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false); setUrlSubmitted(null)
        setUrlResult(null); setUrlLookupMissing(false)
      }} /></div>
    {!ready && <p role="status" className="text-sm text-text-3">请启用SDK直连并选择带有接入预设的独立ASR模型；旧能力声明不能直接转写。</p>}
    {preset && <div className="text-xs text-text-3"><p>预设：{preset.name} · v{preset.version}</p><p>格式：{preset.formats.join(' / ')} · {asrAudioLimitLabel(preset.maxAudioBytes)}（解码后大小）</p></div>}
    {target && isMimoPro(target.model) && <p role="alert" className="rounded-ctl border border-warn-border bg-warn-soft px-3 py-2 text-xs leading-5 text-warn">Pro 模型不推荐用于长音频。课堂录音、8–10 分钟以上材料请切换到 Flash；本提示不代表服务商承诺固定时长上限。</p>}
    <div><Label htmlFor="ai-asr-source-kind">音频来源</Label><Select id="ai-asr-source-kind" ariaLabel="音频来源" value={sourceKind} disabled={locked || !ready}
      options={[{ value: 'file', label: '本机文件' }, ...(urlSupported ? [{ value: 'url', label: '公开 HTTPS URL' }] : [])]}
      onValueChange={(value) => {
        const next = value as SourceKind
        setSourceKind(next); setConsentedTo(''); setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false)
        setResult(null); setUrlResult(null); setUrlLookupMissing(false); setMessage(null)
        if (next === 'file') { setAudioUrl(''); setUrlName(''); setUrlFormat(''); setFile(null) }
      }} /></div>
    {sourceKind === 'file' ? <div><Label htmlFor="ai-asr-file">本次音频</Label><input ref={input} id="ai-asr-file" type="file" className="sr-only" accept={preset?.formats.map((value) => `.${value}`).join(',')} disabled={locked || !ready}
      onChange={(event) => { const next = event.target.files?.[0] ?? null; event.target.value = ''; chooseFile(next) }} />
      <div className="mt-1"><Button variant="secondary" disabled={locked || !ready} onClick={() => input.current?.click()}>选择音频</Button></div>
      {file && <div className="mt-3 space-y-2"><p className="break-all text-xs text-text-2">{file.name} · {(file.size / 1024).toFixed(1)} KiB</p><AudioPreview file={file} /></div>}
    </div> : <div className="space-y-2">
      {urlSubmitted ? <div className="space-y-1 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2">
        <p>已冻结公开URL调用：{urlSubmitted.body.audio.name} · {urlSubmitted.body.audio.format}</p>
        <p>来源主机（已脱敏）：{originOf(urlSubmitted.body.audio.url)}</p>
        <p className="text-text-3">完整地址只保留在当前页面内存，未写入本地存储、日志或结果展示。</p>
      </div> : <>
        <Label htmlFor="ai-asr-url">公开HTTPS音频地址</Label>
        <Input id="ai-asr-url" type="url" value={audioUrl} disabled={locked || !ready} maxLength={MAX_PUBLIC_AUDIO_URL_CHARS}
          autoComplete="off" spellCheck={false} placeholder="https://example.com/audio.wav"
          onChange={(event) => chooseUrl(event.target.value)} />
        {audioUrl.trim() && urlValidation && !urlValidation.ok && <p role="alert" className="rounded-ctl border border-crit-border bg-crit-soft px-3 py-2 text-xs text-crit">{urlValidation.message}</p>}
        {urlValidation?.ok && <p className="break-words text-xs text-text-3">{urlName || 'remote-audio'} · {urlFormat || 'URL扩展名不是当前预设格式'} · 来源主机 {urlValidation.origin}。完整地址不会进入本地存储或日志。</p>}
        <p className="text-xs leading-5 text-text-3">URL模式使用服务端100 MiB（解码后）预检预算；这不是浏览器内存上限，也不证明ASR服务商之后读取到相同字节。本机文件Base64仍按当前预设限制。</p>
      </>}
    </div>}
    <div><Label htmlFor="ai-asr-language">识别语言</Label><Select id="ai-asr-language" ariaLabel="识别语言" value={language} disabled={locked || !preset}
      options={(preset?.languages ?? ['auto']).map((value) => ({ value, label: languageNames[value] ?? value }))}
      onValueChange={(value) => { setLanguage(value); setConsentedTo(''); setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false) }} /></div>
    {target && preset && <div className="space-y-2 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2"><p>音频发送目标：{target.name} · {target.model}</p><p className="break-all font-mono">{endpoint}</p>
      <p>原音频另存发起账号的默认文件来源；确认后由PIM服务器发给上述ASR服务。每次新调用可能产生服务商费用。</p>
      {sourceKind === 'url' ? <>
        <p className="rounded-ctl border border-warn-border bg-warn-soft p-2 leading-5 text-warn">PIM服务器会先读取公开源；ASR服务商随后会再次读取原URL，且无法保证两次字节一致。URL预检SHA不是服务商读取证明。</p>
        <label className="flex items-start gap-2"><input type="checkbox" aria-label="确认PIM服务器读取公开HTTPS音频" className="mt-0.5 size-4 shrink-0 accent-primary" checked={urlSourceConfirmed} disabled={locked || !ready || !urlValid}
          onChange={(event) => setUrlSourceConfirmed(event.target.checked)} /><span>确认PIM服务器将读取上述公开HTTPS音频源并保存调用记录</span></label>
        <label className="flex items-start gap-2"><input type="checkbox" aria-label="确认ASR服务将再次读取该URL且内容可能不同" className="mt-0.5 size-4 shrink-0 accent-primary" checked={urlProviderFetchConfirmed} disabled={locked || !ready || !urlValid}
          onChange={(event) => setUrlProviderFetchConfirmed(event.target.checked)} /><span>确认ASR服务商会再次读取原URL；本地预检不能证明其读取到相同字节</span></label>
        <label className="flex items-start gap-2"><input type="checkbox" aria-label="确认本次URL转写目标与可能费用" className="mt-0.5 size-4 shrink-0 accent-primary" checked={approvalKey !== '' && consentedTo === approvalKey} disabled={locked || !ready || !urlReady}
          onChange={(event) => setConsentedTo(event.target.checked ? approvalKey : '')} /><span>确认本次URL转写会调用上述ASR目标，理解可能产生服务商费用</span></label>
      </> : <label className="flex items-start gap-2"><input type="checkbox" aria-label="确认将本次音频发送到上述ASR模型" className="mt-0.5 size-4 shrink-0 accent-primary" checked={approvalKey !== '' && consentedTo === approvalKey} disabled={locked || !ready || !file}
        onChange={(event) => setConsentedTo(event.target.checked ? approvalKey : '')} /><span>确认将本次音频发送到上述ASR模型</span></label>}
    </div>}
    <div className="flex flex-wrap gap-2"><Button variant="primary" loading={pending && result === null} disabled={locked || !ready || !canSend} onClick={() => void (sourceKind === 'file' ? send() : sendUrl())}>{sourceKind === 'file' ? '提交后台转写' : '确认发送URL并转写'}</Button>
      {pending && submitted && result === null && <Button variant="secondary" onClick={() => abort.current?.abort()}>停止等待提交</Button>}
      {submitted && result && !isJobTerminal(result.status) && <Button variant="secondary" loading={pending} disabled={pending} onClick={() => void cancelJob()}>取消任务</Button>}
      {submitted && !pending && <><Button variant="secondary" onClick={() => void lookupJob()}>核对ASR任务</Button>
        <Button variant="ghost" onClick={newCall}>新建一次ASR调用</Button></>}
      {resumeId && !submitted && <Button variant="secondary" loading={pending} disabled={pending} onClick={() => void resumeJob()}>恢复原ASR任务</Button>}
      {urlSubmitted && !pending && <><Button variant="secondary" onClick={() => void lookupUrl()}>核对URL ASR状态</Button>
        {urlLookupMissing && <Button variant="secondary" onClick={() => void resendUrl()}>按原标识重发URL</Button>}
        <Button variant="ghost" onClick={newCall}>新建一次ASR调用</Button></>}
    </div>
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {result && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(result.status)}<Button variant="ghost" size="sm" onClick={() => showDetail(result.logId)}>查看ASR完整记录</Button></div>
      <p role="status" className="rounded-ctl border border-border bg-surface p-3 text-xs leading-5 text-text-2">{jobGuidance(result)}</p>
      {result.errorMessage && <p role="alert" className="text-sm text-text-2">{result.errorMessage}</p>}
      <div className="grid gap-1 text-xs text-text-3 sm:grid-cols-2">
        <span>中断来源：{result.diagnostics.abortSource}</span>
        <span>结果已知：{result.diagnostics.outcomeKnown ? '是' : '否'}</span>
        <span>网关状态：{result.diagnostics.gatewayStatus ?? '未观察到网关HTTP响应'}</span>
        <span>网关名称：{result.diagnostics.gatewayName ?? '未提供'}</span>
        <span>传输证据：{result.diagnostics.gatewayEvidenceSource ?? '未记录'}</span>
        <span>观察环节：{result.diagnostics.transportObservationKind ?? '未记录'}</span>
        <span>服务商状态：{result.diagnostics.upstreamStatus ?? '未提供'}</span>
        <span>输出上限：{result.diagnostics.maxOutputTokens} token</span>
        <span>估算音频：{result.diagnostics.estimatedAudioSeconds == null ? '未估算' : `${Math.round(result.diagnostics.estimatedAudioSeconds)} 秒`}</span>
      </div>
      {result.diagnostics.gatewayEvidenceSource === 'client_network_failure' &&
        <p className="text-xs leading-5 text-warn">浏览器轮询未收到 HTTP 响应；该现象不能证明是网关、浏览器还是服务端断开。</p>}
      {result.diagnostics.gatewayEvidenceSource === 'client_http_response' &&
        <p className="text-xs leading-5 text-warn">浏览器确实收到网关 HTTP {result.diagnostics.gatewayStatus ?? '错误'}{result.diagnostics.gatewayName ? `（${result.diagnostics.gatewayName}）` : ''}；该证据不改变服务商调用状态。</p>}
      {result.diagnostics.nextAction && <p className="text-xs leading-5 text-warn">下一步：{result.diagnostics.nextAction}</p>}
      {result.responseText !== null && <pre aria-label="实际ASR转写" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{result.responseText || '本次返回空转写，请试听原音频核对。'}</pre>}
      {result.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新发送音频。</p>}
      <AsrResultInsights rawJson={detail.data?.responseRawJson} presetId={preset?.id ?? target?.asrPresetId}
        loading={detail.isLoading} loadError={detail.isError ? errorText(detail.error) : null} />
    </div>}
    {urlResult && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(urlResult.status)}<Button variant="ghost" size="sm" onClick={() => showDetail(urlResult.logId)}>查看URL ASR完整记录</Button></div>
      {urlResult.status !== 'Succeeded' && <p role="status" className="rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-warn">{urlResult.diagnostics ? transportGuidance(urlResult.diagnostics) : `状态 ${urlResult.status} 未确认为成功`}；保留原请求标识，页面不会自动重发。</p>}
      {urlResult.errorMessage && <p role="alert" className="text-sm text-text-2">{urlResult.errorMessage}</p>}
      {urlResult.responseText !== null && <pre aria-label="实际URL ASR转写" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{urlResult.responseText || '本次返回空转写，请人工核对来源与记录。'}</pre>}
      <div className="grid gap-2 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2">
        <p>来源主机（已脱敏）：{urlResult.sourceOrigin ?? '未提供'}</p>
        <p>本地URL预检：{urlResult.preflightVerified ? '已核验' : '未核验或未完成'}{urlResult.preflightSha256 ? <> · <span className="break-all font-mono">SHA256 {urlResult.preflightSha256}</span></> : null}</p>
        <p className={urlResult.providerFetchVerified ? '' : 'text-warn'}>Provider再次读取：{urlResult.providerFetchVerified ? '已核验' : '未核验；不得假定与本地预检字节相同'}</p>
        {urlResult.diagnostics && <>
          <p>中断来源：{urlResult.diagnostics.abortSource} · 结果已知：{urlResult.diagnostics.outcomeKnown ? '是' : '否'}</p>
          <p>网关证据：{urlResult.diagnostics.gatewayStatus == null ? '未观察到HTTP响应' : `HTTP ${urlResult.diagnostics.gatewayStatus}${urlResult.diagnostics.gatewayName ? `（${urlResult.diagnostics.gatewayName}）` : ''}`}</p>
        </>}
      </div>
      {urlResult.warning && <p role="status" className="rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-warn">{urlResult.warning}</p>}
      {urlResult.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新读取URL或调用服务商。</p>}
      <AsrResultInsights rawJson={detail.data?.responseRawJson} presetId={urlSubmitted?.preset.id ?? preset?.id}
        loading={detail.isLoading} loadError={detail.isError ? errorText(detail.error) : null} />
    </div>}
  </Card>
}

export function CallAudio({ logId, owner, viewer, running = false }: { logId: string; owner: string | null | undefined; viewer: string; running?: boolean }) {
  const allowed = owner === viewer
  const source = useQuery({ queryKey: ['ai', viewer, 'audio', logId, running], queryFn: ({ signal }) => aiApi.audio(logId, signal), enabled: allowed, refetchInterval: running ? 3000 : false })
  const [url, setUrl] = useState(''); const [error, setError] = useState(''); const [retry, setRetry] = useState(0)
  useEffect(() => {
    if (!allowed || !source.data?.stored) return
    const cancel = new AbortController(); let link = ''
    void aiApi.audioContent(logId, cancel.signal).then((blob) => { if (!cancel.signal.aborted) { link = URL.createObjectURL(blob); setUrl(link) } })
      .catch((failure: unknown) => { if (!cancel.signal.aborted) setError(errorText(failure)) })
    return () => { cancel.abort(); if (link) URL.revokeObjectURL(link) }
  }, [allowed, logId, source.data?.stored, retry])
  if (!allowed) return <p className="text-xs text-text-3">原音频仅调用发起账号可查看。</p>
  if (source.isLoading) return <Skeleton className="h-20" />
  if (source.isError) return <EmptyState title="音频清单读取失败" action={<Button onClick={() => void source.refetch()}>重试</Button>} />
  const audio = source.data
  if (!audio) return <p className="text-xs text-text-3">没有音频材料记录。</p>
  return <div className="space-y-2 rounded-ctl border border-border p-3"><h3 className="text-sm font-medium text-text-1">本次原音频</h3><p className="break-all text-xs text-text-2">{audio.name} · {audio.sizeBytes} bytes</p>
    {!audio.stored ? <p className="text-xs text-text-3">音频尚未保存，尚无完整输入的发送记录。</p> : error ? <div className="text-xs text-text-3"><p>原音频无法读取或播放，可能已移除、改动或来源不可用。{error}</p>
      <Button size="sm" variant="ghost" onClick={() => { setUrl(''); setError(''); setRetry((value) => value + 1) }}>重试原音频</Button></div>
      : url ? <audio src={url} controls preload="metadata" aria-label="本次原音频" className="w-full" onError={() => setError('音频解码失败')} /> : <Skeleton className="h-12" />}
    <details className="text-xs text-text-3"><summary className="cursor-pointer">音频指纹</summary><p className="break-all font-mono">SHA256 {audio.sha256}</p></details>
  </div>
}
