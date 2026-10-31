import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Card, EmptyState, Input, Label, Select, Skeleton } from '@/components/ui'
import { ApiError } from '@/api/client'
import {
  aiApi,
  type AsrPreset,
  type AsrRequest,
  type AsrUrlRequest,
  type AsrUrlResult,
  type CallResult,
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
  const [urlLookupMissing, setUrlLookupMissing] = useState(false)
  const [pending, setPending] = useState(false); const [result, setResult] = useState<CallResult | null>(null)
  const [urlResult, setUrlResult] = useState<AsrUrlResult | null>(null); const [message, setMessage] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null); const inFlight = useRef(false); const input = useRef<HTMLInputElement | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
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
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    const id = createAiRequestId(); setSubmitted({ id, model: target, preset }); setPending(true); setResult(null); setMessage(null)
    try {
      const body: AsrRequest = { clientRequestId: id, expectedUserId: userId, modelId: selected || null, language,
        audio: { name: file.name, format, base64: await readMediaFile(file, controller.signal) },
        consent: { modelId: target.id, modelRevision: target.revision, endpoint, presetId: preset.id, presetVersion: preset.version, confirmed: true } }
      setResult(await aiApi.asrCall(body, controller.signal))
    } catch (error) {
      setMessage(controller.signal.aborted ? '已请求取消，请核对ASR记录，上游结果以实际记录为准。' : errorText(error) + '；请先核对记录，页面不会自动重发。')
    } finally { inFlight.current = false; setPending(false); changed() }
  }

  async function sendUrl() {
    if (inFlight.current || !urlReady || !urlValid || !target || !preset || !ready || urlSubmitted || submitted || !urlConsent) return
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
      setUrlResult(await aiApi.asrUrlCall(body, controller.signal))
    } catch (error) {
      setMessage(controller.signal.aborted
        ? '已请求取消，请核对URL ASR记录，上游结果以实际记录为准。'
        : redactSourceUrl(errorText(error), sourceUrl) + '；请先核对记录，页面不会自动重发。')
    } finally { inFlight.current = false; setPending(false); changed() }
  }

  async function lookupUrl() {
    if (!urlSubmitted || pending) return
    setPending(true); setMessage(null)
    try {
      setUrlResult(await aiApi.asrUrlLookup(urlSubmitted.id)); setUrlLookupMissing(false)
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        setUrlLookupMissing(true)
        setMessage('服务端尚未找到该URL请求标识；保留原ID，未自动重发。如确认重试，请使用“按原标识重发URL”。')
      } else {
        setMessage(redactSourceUrl(errorText(error), urlSubmitted.body.audio.url) + '；请保留原标识并核对记录。')
      }
    } finally { setPending(false) }
  }

  async function resendUrl() {
    if (!urlSubmitted || !urlLookupMissing || pending) return
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    setPending(true); setMessage(null); setUrlLookupMissing(false)
    try {
      setUrlResult(await aiApi.asrUrlCall(urlSubmitted.body, controller.signal))
    } catch (error) {
      setMessage(controller.signal.aborted
        ? '已请求取消，请核对URL ASR记录，上游结果以实际记录为准。'
        : redactSourceUrl(errorText(error), urlSubmitted.body.audio.url) + '；保留原标识，页面不会自动重发。')
    } finally { inFlight.current = false; setPending(false); changed() }
  }

  function newCall() {
    setSubmitted(null); setUrlSubmitted(null); setConsentedTo('')
    setUrlSourceConfirmed(false); setUrlProviderFetchConfirmed(false); setUrlLookupMissing(false)
    setResult(null); setUrlResult(null); setMessage(null)
    setAudioUrl(''); setUrlName(''); setUrlFormat('')
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
    <div className="flex flex-wrap gap-2"><Button variant="primary" loading={pending} disabled={locked || !ready || !canSend} onClick={() => void (sourceKind === 'file' ? send() : sendUrl())}>{sourceKind === 'file' ? '开始转写并记录' : '确认发送URL并转写'}</Button>
      {pending && <Button variant="secondary" onClick={() => abort.current?.abort()}>取消ASR等待</Button>}
      {submitted && !pending && <><Button variant="secondary" onClick={() => { void aiApi.asrLookup(submitted.id).then((value) => { setResult(value); setMessage(null); changed() }).catch((error: unknown) => setMessage(errorText(error))) }}>核对ASR状态</Button>
        <Button variant="ghost" onClick={newCall}>新建一次ASR调用</Button></>}
      {urlSubmitted && !pending && <><Button variant="secondary" onClick={() => void lookupUrl()}>核对URL ASR状态</Button>
        {urlLookupMissing && <Button variant="secondary" onClick={() => void resendUrl()}>按原标识重发URL</Button>}
        <Button variant="ghost" onClick={newCall}>新建一次ASR调用</Button></>}
    </div>
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {result && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(result.status)}<Button variant="ghost" size="sm" onClick={() => showDetail(result.logId)}>查看ASR完整记录</Button></div>
      {result.errorMessage && <p role="alert" className="text-sm text-text-2">{result.errorMessage}</p>}
      {result.responseText !== null && <pre aria-label="实际ASR转写" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{result.responseText || '本次返回空转写，请试听原音频核对。'}</pre>}
      {result.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新发送音频。</p>}
      <AsrResultInsights rawJson={detail.data?.responseRawJson} presetId={preset?.id ?? target?.asrPresetId}
        loading={detail.isLoading} loadError={detail.isError ? errorText(detail.error) : null} />
    </div>}
    {urlResult && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(urlResult.status)}<Button variant="ghost" size="sm" onClick={() => showDetail(urlResult.logId)}>查看URL ASR完整记录</Button></div>
      {urlResult.status !== 'Succeeded' && <p role="status" className="rounded-ctl border border-warn-border bg-warn-soft p-3 text-xs leading-5 text-warn">状态 {urlResult.status} 未确认为成功；保留原请求标识，页面不会自动重发。</p>}
      {urlResult.errorMessage && <p role="alert" className="text-sm text-text-2">{urlResult.errorMessage}</p>}
      {urlResult.responseText !== null && <pre aria-label="实际URL ASR转写" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{urlResult.responseText || '本次返回空转写，请人工核对来源与记录。'}</pre>}
      <div className="grid gap-2 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2">
        <p>来源主机（已脱敏）：{urlResult.sourceOrigin ?? '未提供'}</p>
        <p>本地URL预检：{urlResult.preflightVerified ? '已核验' : '未核验或未完成'}{urlResult.preflightSha256 ? <> · <span className="break-all font-mono">SHA256 {urlResult.preflightSha256}</span></> : null}</p>
        <p className={urlResult.providerFetchVerified ? '' : 'text-warn'}>Provider再次读取：{urlResult.providerFetchVerified ? '已核验' : '未核验；不得假定与本地预检字节相同'}</p>
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
