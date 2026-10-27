import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Card, EmptyState, Label, Select, Skeleton } from '@/components/ui'
import { aiApi, type AsrPreset, type AsrRequest, type CallResult, type Model, type ModelSettings } from './api'
import { createAiRequestId } from './request-id'
import { readMediaFile } from './media-file'

const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败'
const languageNames: Record<string, string> = { auto: '自动识别', zh: '中文', en: '英语' }
function operation(model: Model | undefined, preset: AsrPreset | undefined) {
  if (!model || !preset) return ''
  try { return new URL(preset.operationPath, model.baseUrl).href } catch { return '' }
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
  const [file, setFile] = useState<File | null>(null); const [selection, setSelection] = useState(''); const [consentedTo, setConsentedTo] = useState('')
  const [submitted, setSubmitted] = useState<{ id: string; model: Model; preset: AsrPreset } | null>(null)
  const [pending, setPending] = useState(false); const [result, setResult] = useState<CallResult | null>(null); const [message, setMessage] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null); const inFlight = useRef(false); const input = useRef<HTMLInputElement | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
  const current = models.find((m) => m.id === (selected || settings.transcriptionModelId))
  const currentPreset = presets.data?.find((p) => p.id === current?.asrPresetId)
  const target = submitted?.model ?? current, preset = submitted?.preset ?? currentPreset
  const endpoint = operation(target, preset)
  const approvalKey = target && preset ? `${target.id}/${target.revision}/${preset.id}/${preset.version}/${endpoint}/${selection}/${language}` : ''
  const ready = settings.enabled && current?.enabled && current.capabilities.includes('Transcription') && currentPreset
  const locked = pending || submitted !== null
  const format = file?.name.toLowerCase().split('.').at(-1) ?? ''
  const chooseFile = (next: File | null) => {
    setConsentedTo(''); setSelection(createAiRequestId()); setResult(null)
    const kind = next?.name.toLowerCase().split('.').at(-1)
    if (next && (!preset || !kind || !preset.formats.includes(kind) || next.size < 1 || next.size > preset.maxAudioBytes)) {
      setFile(null); setMessage('音频格式或大小不符合当前预设，请查看下方限制。'); return
    }
    setFile(next); setMessage(null)
  }
  async function send() {
    if (inFlight.current || !file || !target || !preset || !ready || submitted || consentedTo !== approvalKey) return
    inFlight.current = true; const controller = new AbortController(); abort.current = controller
    const id = createAiRequestId(); setSubmitted({ id, model: target, preset }); setPending(true); setResult(null); setMessage(null)
    try {
      const body: AsrRequest = { clientRequestId: id, expectedUserId: userId, modelId: selected || null, language,
        audio: { name: file.name, format, base64: await readMediaFile(file, controller.signal) },
        consent: { modelId: target.id, modelRevision: target.revision, endpoint, presetId: preset.id, presetVersion: preset.version, confirmed: true } }
      setResult(await aiApi.asrCall(body, controller.signal))
    } catch (error) { setMessage(controller.signal.aborted ? '已请求取消，请核对ASR记录，上游结果以实际记录为准。' : errorText(error) + '；请先核对记录，页面不会自动重发。') }
    finally { inFlight.current = false; setPending(false); changed() }
  }
  return <Card className="space-y-4 p-5"><div><h2 className="text-sm font-semibold text-text-1">ASR音频转写</h2><p className="mt-1 text-xs text-text-3">通过所选预设发送单条音频，保存原音频、实际请求与转写。本入口用于单片调用，整堂课的上传与分段任务待接入。</p></div>
    {presets.isLoading ? <Skeleton className="h-16" /> : presets.isError ? <EmptyState title="ASR预设加载失败" action={<Button onClick={() => void presets.refetch()}>重试</Button>} /> : null}
    <div><Label htmlFor="ai-asr-model">本次ASR模型</Label><Select id="ai-asr-model" ariaLabel="本次ASR模型" value={selected} disabled={locked}
      options={[{ value: '', label: '使用默认ASR模型' }, ...models.filter((m) => m.enabled && m.asrPresetId && m.capabilities.includes('Transcription')).map((m) => ({ value: m.id, label: m.name }))]}
      onValueChange={(value) => { setSelected(value); setConsentedTo(''); setFile(null); setLanguage('auto') }} /></div>
    {!ready && <p role="status" className="text-sm text-text-3">请启用SDK直连并选择带有接入预设的独立ASR模型；旧能力声明不能直接转写。</p>}
    {preset && <div className="text-xs text-text-3"><p>预设：{preset.name} · v{preset.version}</p><p>格式：{preset.formats.join(' / ')} · 单音频最多 {(preset.maxAudioBytes / 1024 / 1024).toFixed(0)} MiB</p></div>}
    <div><Label htmlFor="ai-asr-file">本次音频</Label><input ref={input} id="ai-asr-file" type="file" className="sr-only" accept={preset?.formats.map((value) => `.${value}`).join(',')} disabled={locked || !ready}
      onChange={(event) => { const next = event.target.files?.[0] ?? null; event.target.value = ''; chooseFile(next) }} />
      <div className="mt-1"><Button variant="secondary" disabled={locked || !ready} onClick={() => input.current?.click()}>选择音频</Button></div>
      {file && <div className="mt-3 space-y-2"><p className="break-all text-xs text-text-2">{file.name} · {(file.size / 1024).toFixed(1)} KiB</p><AudioPreview file={file} /></div>}
    </div>
    <div><Label htmlFor="ai-asr-language">识别语言</Label><Select id="ai-asr-language" ariaLabel="识别语言" value={language} disabled={locked || !preset}
      options={(preset?.languages ?? ['auto']).map((value) => ({ value, label: languageNames[value] ?? value }))} onValueChange={(value) => { setLanguage(value); setConsentedTo('') }} /></div>
    {target && preset && <div className="space-y-2 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2"><p>音频发送目标：{target.name} · {target.model}</p><p className="break-all font-mono">{endpoint}</p>
      <p>原音频另存发起账号的默认文件来源；确认后由PIM服务器发给上述ASR服务。每次新调用可能产生服务商费用。</p>
      <label className="flex items-start gap-2"><input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary" checked={approvalKey !== '' && consentedTo === approvalKey} disabled={locked || !ready || !file}
        onChange={(event) => setConsentedTo(event.target.checked ? approvalKey : '')} /><span>确认将本次音频发送到上述ASR模型</span></label></div>}
    <div className="flex flex-wrap gap-2"><Button variant="primary" loading={pending} disabled={locked || !ready || !file || !approvalKey || approvalKey !== consentedTo} onClick={() => void send()}>开始转写并记录</Button>
      {pending && <Button variant="secondary" onClick={() => abort.current?.abort()}>取消ASR等待</Button>}
      {submitted && !pending && <><Button variant="secondary" onClick={() => { void aiApi.asrLookup(submitted.id).then((value) => { setResult(value); setMessage(null); changed() }).catch((error: unknown) => setMessage(errorText(error))) }}>核对ASR状态</Button>
        <Button variant="ghost" onClick={() => { setSubmitted(null); setConsentedTo(''); setResult(null); setMessage(null) }}>新建一次ASR调用</Button></>}
    </div>
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {result && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(result.status)}<Button variant="ghost" size="sm" onClick={() => showDetail(result.logId)}>查看ASR完整记录</Button></div>
      {result.errorMessage && <p role="alert" className="text-sm text-text-2">{result.errorMessage}</p>}
      {result.responseText !== null && <pre aria-label="实际ASR转写" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{result.responseText || '本次返回空转写，请试听原音频核对。'}</pre>}
      {result.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新发送音频。</p>}
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
